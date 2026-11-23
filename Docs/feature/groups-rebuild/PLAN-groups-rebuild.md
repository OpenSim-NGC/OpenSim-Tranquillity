# Groups rebuild

**Status: phases 0–3 built (PRs #441, #442, #445, #446); phase 4 partly (bans, #448); phase 5's import and rollout built.** The plan for rebuilding the groups code on the existing Groups V2 data
(`os_groups_*`), chosen over fixing the two current forks in place or rebuilding with a new schema.

## Why

The tree has two complete groups systems:

- **Groups V2**, in `Addons/OpenSim.Addons.Groups`: a Robust service over the `os_groups_*` tables.
- **XmlRpcGroups**, in `OptionalModules/Avatar/XmlRpcGroups`: a client of an external Flotsam PHP server.

The two are parallel forks of the same region code. Only V2 has the recent permission fixes (#405, #407, #426).

V2 also has these problems:

- **Query cost.** Listing a group's members costs about two queries per member. An arrival costs about four per
  group the avatar is in.
- **Robust calls.** Every call to Robust from a simulator takes one process-wide lock.
- **Caching.** The region cache never hears about changes made in another region.
- **Notices.** Notices go out one IM per member, inside the request.
- **Missing viewer features:** group bans (`GroupAPIv1`), invites answered by capability, chat moderation and
  history.

## What stays

- **Data.** The `os_groups_*` tables and what they hold. Schema changes are additive only.
- **Robust's `/groups` API**, so regions on the previous build keep working during the rollout.
- **The Hypergrid groups protocol.**
- **The viewer protocol**: packets, capabilities, event-queue messages and IM dialogs.
- **`IGroupsModule` and `IGroupsMessagingModule`**, for permissions, land, search, voice moderation, Phlox and
  OSSL.
- **Script behaviour**: `llSameGroup`, `llDetectedGroup`, `OBJECT_GROUP`, `osInviteToGroup`,
  `osEjectFromGroup`.
- **The `[Groups]` settings** operators already use.

## Phases

Each phase ships on its own.

0. **Test bed.**
   - A SQLite store, so V2 runs on SQLite standalones and the test rigs.
   - V2 messaging working as documented.
   - Ferrobot probe scenarios that record today's behaviour.
1. **Data layer.**
   - Batched queries: a member list, an avatar's memberships and a powers check each in a few statements,
     whatever the size.
   - The `(GroupID, PrincipalID)` index on role memberships, for MySQL, PostgreSQL and SQLite.
2. **Service.**
   - The group rules (powers, owners, assigning roles, invites, ejects) in one tested place, on the new queries.
   - Robust's `/groups` API and the Hypergrid connector stay compatible.
3. **Region module.**
   - One module in place of the V2 groups and messaging pair.
   - Caching that hears about changes.
   - Notices and invites through a queue, so a send never blocks the region or waits on an offline grid.
4. **Missing features.**
   - Ban lists (`GroupAPIv1`), `AcceptGroupInvite` / `DeclineGroupInvite`, and `ChatSessionRequest` (accepting
     an invitation, text mute, history).
   - Land contribution.
   - Notice attachments snapshotted when sent.
   - A group that owns land cannot be deleted.
   - Hypergrid members can be ejected.
5. **Retire XmlRpcGroups**, with a one-time copy from the Flotsam tables for grids that use it. Then the rollout.

The probe scenarios run before and after each phase.

## Phase 0, as built

- **`SQLiteGroupsData`**, with the `os_groups_Store` migration for SQLite.
  - The tables and columns are MySQL's.
  - The group search takes its text as a parameter.
  - It adds the `(GroupID, PrincipalID)` role-membership index from the start.
  - `config-include/storage/SQLiteStandalone.ini` gives groups their own `groups.db`, so a SQLite standalone
    only has to turn groups on:

    ```ini
    [Groups]
        Enabled = true
        Module = "Groups Module V2"
        ServicesConnectorModule = "Groups Local Service Connector"
        MessagingEnabled = true
        MessagingModule = "Groups Messaging Module V2"
    ```
- **V2 messaging is on wherever it is chosen.**
  - It used to read `MessageOnlineUsersOnly`, a setting for the Flotsam module. `RegionServerDefaults.ini` sets it
    to false, so V2 group chat and notices were off.
  - It now also reads the documented `DebugMessagingEnabled`.

## Phase 1, as built

- **Member list:** five statements whatever the group's size (group, roles, members, role memberships, grid
  users per 500); it was two per member.
- **An avatar's memberships**, read at every region arrival: four statements whatever the number of groups (memberships,
  groups, their roles, the avatar's roles); it was four per group.
- **Powers checks** were already three statements and are unchanged.
- **Store lookups added:** `IGroupsData.RetrieveGroupsByID`, `RetrieveRolesOfGroups`, `RetrievePrincipalRoles`, and
  `IGridUserData.GetMany`. Each has a default implementation (one call per key) for stores outside the tree.
- **Indexes:** `(GroupID, PrincipalID)` on role memberships for MySQL (version 4) and PostgreSQL (version 5).
  PostgreSQL also gets the principal and notice indexes MySQL already had.

Measured with `GroupsBatchedQueriesTests.TimingsAtScale` (`OPENSIM_TEST_GROUPS_SCALE=5000`): a 5001-member group, and an
avatar in 26 groups. Local servers (MariaDB 11, PostgreSQL 17, SQLite file); a grid whose database is across a network
pays a round trip per statement on top, so the old numbers grow with the latency and the new ones barely move.

| Backend | Member list, before | Member list, after | Memberships, before | Memberships, after |
|---|---|---|---|---|
| SQLite | 1.9 – 2.1 s | 33 – 52 ms | 4 – 7 ms | 0.4 – 7 ms |
| MySQL (MariaDB 11) | 1.4 – 1.9 s | 38 – 57 ms | 14 – 17 ms | 1.6 – 2.3 ms |
| PostgreSQL 17 | 1.24 – 1.26 s | 17 – 26 ms | 4 – 6 ms | 0.6 – 1.1 ms |

## Phase 2, first step: the rules in one place

`Service/GroupRules.cs` decides every permission for one avatar in one group, from three reads: the group, its roles
and the roles the avatar holds. The service asks it instead of reading the store for each check.

Rules changed, each to match the viewer's group panels (`llgroupmgr.h`, `LLPanelGroupGeneral`, `LLPanelGroupRoles`):

| Change | Before | Now |
|---|---|---|
| Group charter, insignia, listing, mature, publishing | Change Actions | Change Identity |
| Open enrollment and sign-up fee | Change Actions | Member Options (`ChangeOptions`) |
| A role's name, description and title | Change Actions | Role Properties |
| A role's abilities | Change Actions | Change Actions |
| A member's notice and profile settings | anyone | the member |
| An avatar's active group | anyone, any group | the avatar, among its groups |
| Removing an invitation | anyone | the invitee, or someone with Invite |

The default Officers role has Change Identity, Member Options and Role Properties but not Change Actions, so officers
could not edit the charter, the enrollment or a role's title before. A request that mixes parts changes the parts the
requester may change and keeps the rest, as the viewer sends a whole group or role at once.

Robust and Hypergrid (phase 2, second step):

- Robust's groups service asks every caller for `[Groups] SecretKey` once it is set there; the regions send it. With
  no key and no `AuthType`, Robust warns at startup that it believes any caller.
- A Hypergrid proxy group stays with the grid it came from: the same group id from another location is refused.
- A Hypergrid notice for a proxy group is taken once the group's grid confirms sending it (`VERIFYNOTICE`, which
  every grid answers).

The probe's `group-admin` scenario covers an officer's changes (Ferrobot). Still to do in phase 2: the probe extended
to the Hypergrid groups path on the two-grid rig.

## Phase 3, first steps

- **Calls side by side.** The region's connectors to Robust's groups service and to other grids' Hypergrid groups
  services made every request inside one lock, so a region's groups calls ran one at a time behind the slowest. The
  locks are gone (six half-second calls: 3.0 s before, about 0.5 s now), and the region's cache keeps its set of
  fetches under way under its own lock.
- **Notices a few at a time.** A notice to members outside the process goes to the message transfer module through
  `GroupNoticeSender`, four messages at a time, so the region's other instant messages wait behind at most those
  few instead of one job per member. The members reached are unchanged.

- **Changes seen by everyone on the region.** On a grid the region caches answers for 30 s, member lists per
  requester. A role given or taken, a member leaving, notices turned off or a title chosen used to drop only the
  requester's and the member's entries, so other avatars on the region kept the old answer (and notices and chat
  still went to a member who had left, or had turned notices off). Every change now clears the region's cache.
- **Group chat** already goes once per region to members online elsewhere (the receiving region fans it out), so it
  needs no sender.

- **Other regions told of a change.** With `[Groups] SecretKey` set, Robust's groups service tells every region server
  online (from the grid service; each server once, however many regions it runs) of the groups each change touched,
  gathered over a quarter of a second: `POST /groups-changed` with `GroupIDs=…` and the key. The region servers take
  it only with the key, and their groups caches forget those groups at once. Nothing is sent again: a server that
  misses a notice keeps its answers until its cache's 30 s are up. Without a key, both sides behave as before.

Still to do in phase 3: forgetting only the groups a notice names (today a region forgets its whole groups cache, as
a change made through it does); one module in place of the V2 groups and messaging pair.

## Phase 4, first step: group bans

- **Storage:** `os_groups_bans` on MySQL (version 5), PostgreSQL (version 6) and SQLite (version 2).
- **Rules:** "Manage ban list" (`GroupBanAccess`) sees and changes them; owners and oneself are never banned; at most
  500; a banned avatar cannot join, be invited or be added, and a pending invitation is withdrawn. The viewer ejects
  a banned member itself.
- **Viewer:** the `GroupAPIv1` capability (`Cap_GroupAPIv1`, on by default): GET the list, POST a ban or unban.
- **Grid:** Robust's `GETBANS` and `BAN`; another grid's groups keep their bans there.
- **Checked live** on a two-server grid with Ferrobot's `group-admin` (ban, no invitation while banned, unban).

Still to do in phase 4: accepting and declining invitations through the capabilities, chat moderation and history,
land contribution, notice attachments snapshotted when sent, groups that own land, ejecting Hypergrid members.

## Phase 3, last steps

- **Forgetting only what changed.** Each cache entry is noted under the groups and avatars it depends on; a change,
  made through the region or told by Robust (whose notices now name the avatar too), forgets only those entries.
  An avatar's list of groups is noted under every group in it, so a role's new powers reach its members' lists.
- **One module instead of the V2 groups and messaging pair: not done, on purpose.** Grids configure the two by name
  (`Module`, `MessagingModule`), the XmlRpcGroups module shares the messaging interfaces, and the rebuild's fixes
  already live in the pieces both use (the rules, the notice sender, the cache). Merging them would change every
  grid's config for no change players see.

## Phase 5: moving grids to V2

- **The import** (`FlotsamGroupsImport`, console `groups import flotsam [dry-run]`): a one-time copy from the Flotsam
  tables into V2's store, any backend; nothing in V2 is overwritten, a second run copies only what is new, and it
  repairs missing Everyone roles and memberships on the way. Checked on SQLite, MySQL and PostgreSQL targets, and on
  the local grid's Robust.
- **The rollout:** `ROLLOUT-groups-v2.md` (finding the backend, backup, upgrade, the key, the import, switching the
  regions, checking with the probe, going back).
- **Retiring XmlRpcGroups:** it stays in the tree for grids that use it, deprecated: the config says so and the module
  logs it at start.
