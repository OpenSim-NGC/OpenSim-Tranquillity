# Moving a grid to the rebuilt Groups V2

How to put the groups rebuild (phases 0–4) on a grid, and how to move a grid off XmlRpcGroups (the Flotsam PHP
groups server) onto Groups V2 without losing its groups. Written for a Robust grid; a standalone needs only steps 2,
3 and 6 with the local connector.

## 1. Find out what the grid runs

Run `reports/groups-scale.sql` (read-only) against the grid's database:

- **Part A** answers if the grid runs Groups V2 (`os_groups_*` tables): sizes, the biggest groups, integrity.
- **Part B** answers if it runs XmlRpcGroups (`osgroup`, `osgroupmembership`, …): the same for the Flotsam tables.

The regions' `[Groups] Module` says the same: `GroupsModule` is XmlRpcGroups, `Groups Module V2` is V2.

## 2. Back up

Back up the database with the groups tables (V2's `os_groups_*`, and the Flotsam tables if any). The import below only
reads the Flotsam tables, but the V2 migrations (step 3) change `os_groups_*`.

## 3. Upgrade Robust and the regions

Robust and every region server run the new build. On start, Robust's groups store migrates `os_groups_Store`:

| Backend | Versions added | What |
|---|---|---|
| MySQL | 4, 5 | role-membership index; ban table |
| PostgreSQL | 5, 6 | role-membership, principal and notice indexes; ban table |
| SQLite | 1, 2 | every table; ban table |

## 4. Set the groups key

The same `[Groups] SecretKey` in Robust's config and in every region's. Robust then:

- answers its groups service only to callers with the key (until it is set, it answers anyone and says so at start);
- tells every region server of each group change, so their caches forget it at once (`POST /groups-changed`,
  refused without the key). Robust finds the region servers through `[Groups] GridService`, else
  `[GridService] LocalServiceModule`.

Region servers must be able to reach each other's main HTTP port from Robust for the change notices; without them
the caches still expire after 30 s.

## 5. Coming from XmlRpcGroups: import the groups

On Robust:

1. Enable the groups service: in `[ServiceList]`,
   `GroupsServiceConnector = "${Const|PrivatePort}/OpenSim.Addons.Groups.dll:GroupsServiceRobustConnector"`.
2. Point `[Groups] FlotsamImportConnectionString` at the MySQL database with the Flotsam tables (a read-only user is
   enough).
3. On Robust's console: `groups import flotsam dry-run`. It says how many groups, roles, memberships, invitations,
   notices and active groups it would copy, and which it would not (a group V2 already has is left as it is; a
   group whose name a V2 group has is not copied).
4. `groups import flotsam`. Safe to run again: it copies only what is new.
5. Check with `reports/groups-scale.sql` Part A that the counts match Part B, less what the dry run said it would
   leave out.

What the import repairs on the way: a group without an Everyone role gets one, a member missing from Everyone is put
in it, rows of groups that do not exist are left out, and every member gets a new access token. Notices keep their
attachment reference (the item still has to be in the sender's inventory when a member opens it).

## 6. Switch the regions to V2

In each region's `[Groups]`:

```ini
Enabled = true
Module = "Groups Module V2"
ServicesConnectorModule = "Groups Remote Service Connector"   ; "Groups HG Service Connector" on a Hypergrid grid
GroupsServerURI = "${Const|PrivURL}:${Const|PrivatePort}"
SecretKey = "<the key from step 4>"
MessagingEnabled = true
MessagingModule = "Groups Messaging Module V2"
```

Restart the region servers. The viewer's ban list (`GroupAPIv1`) is on by default (`[ClientStack.LindenCaps]
Cap_GroupAPIv1`).

## 7. Check it

On a test region, with Ferrobot's probe (`ferro-probe --scenarios groups,group-admin`; `--partner-home` a region
of another region server checks a change reaches a server it was not made on). Every step should pass: creating a
group, roles, titles, chat, notices, invitations, a plain member unable to eject or promote, officers editing the
charter and titles, ejection, bans.

## 8. Going back

The import never writes to the Flotsam tables: to go back, put the regions' `[Groups]` back as they were and restart
them. Changes made in V2 meanwhile stay in `os_groups_*` and are not carried back.

## What stays as it was

- XmlRpcGroups (`GroupsModule`) is still in the tree for grids that use it, but gets none of the rebuild's fixes; it
  logs at start that it is deprecated.
- Hypergrid groups keep the V1 protocol: a visitor's groups live on their home grid.
