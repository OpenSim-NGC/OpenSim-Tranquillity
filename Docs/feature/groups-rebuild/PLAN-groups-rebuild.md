# Groups rebuild

**Status: phase 0 in progress.** The plan for rebuilding the groups code on the existing Groups V2 data
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
