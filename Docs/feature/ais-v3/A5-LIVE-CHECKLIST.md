# A5 — the live checklist

**What this is.** The ordered in-world run for the first region with `AIS_Enabled = true`. Every step names what
to do, what the viewer does when it works, and the symptom when it does not. **Nothing in the AIS implementation
has ever met a real viewer**; 114 unit and HTTP tests pass against fakes, which proves the shapes, not the system.

**Why the order matters.** Risk A-R1: once `InventoryAPIv3` is in the seed cap, the LL viewer routes deletes,
purges, slams and creates through it with **no fallback** (spec §1g). A failure is not a degraded experience, it is
an operation that silently does not happen. So the read-only steps come first: if step 1 is wrong, stop and turn
the flag off before touching anything mutating.

---

## Before you start

| # | Do | Confirms |
|---|---|---|
| 0a | Set `AIS_Enabled = true` in the **region's own section** only — `[Ebony]`, not `[AIS]`. Restart the region. | The per-region flag, not a grid-wide flip |
| 0b | Console: `grep "\[AIS\]" OpenSim.Server.RegionServer<date>.log` | One line per enabled region: `region <name> advertises InventoryAPIv3, LibraryAPIv3`. **A second region in that output means the flag leaked — stop.** |
| 0c | On another region, confirm **no** `[AIS]` advertise line | The other regions are untouched |
| 0d | Take a full inventory backup for the test avatar (console `save iar` or a DB dump of `inventoryitems` / `inventoryfolders` for that PrincipalID) | Every mutating step below is destructive; this is the undo |

**Test avatars:** Truly Bazar and Aleric Fenwood, as in the SSB work. Never Legion.

---

## Phase 1 — read only (steps 1–3)

Nothing here writes. If any step fails, turn the flag off; do not continue.

### 1. Full inventory load after a cache clear

**Do:** log out; delete the viewer's inventory cache (`<viewer settings>/<user>/*.inv.llsd.gz`); log in to the test
region; open Inventory and let it settle.

**Works:** the inventory tree fills in, folder by folder, and the item count stops growing. Every folder you open
already has its contents.

**Fails:** folders stay empty or show "Fetching…" forever; the item count climbs and never settles; the same folder
is requested over and over. That last one is the specific signature of a folder we returned without all three
`_embedded` collections — the viewer never gets a descendent count, never accepts the version, and re-fetches
forever (risk A-R3).

**Server side:** `grep -c "InventoryAPIv3" <log>` to see the cap being hit; the request path in the HTTP log shows
`/category/<id>/children?depth=…`. Expect `depth=50` for the recursive sweep and `depth=0` for single folders, and
nothing else — those are the only two the viewer sends (spec §1c-bis).

### 2. Open a deep folder

**Do:** open a folder at least three levels down that was **not** expanded during step 1.

**Works:** contents appear immediately or after one brief fetch.

**Fails:** it stays empty, or reopening it re-fetches every time.

### 3. Current outfit reads back

**Do:** open Appearance → Wearing.

**Works:** every worn item is listed, with its real name rather than "(loading)".

**Fails:** blanks or missing entries — the COF links resolved but their targets did not
(`GET /category/current/links` must carry the link *targets* in `_embedded.items`).

---

## Phase 2 — single-object mutations (steps 4–8)

### 4. Rename an item

**Do:** rename any item.

**Works:** the new name sticks, survives closing and reopening the folder, and survives a relog.

**Fails:** the name reverts after a moment (the viewer applied it optimistically and our response did not confirm
it), or it reverts on relog (we did not write it). The likely cause of the first is a missing
`_updated_category_versions` entry for the parent — without it the viewer discards the update entirely
(spec §1d-bis).

**Server side:** `grep "PATCH /item" <log>`.

### 5. Rename a folder

**Do:** rename a folder you created yourself (not a system folder).

**Works:** as above.

**Fails:** as above. A category PATCH must list **both** the folder and its parent in
`_updated_category_versions`; only one is a bug.

### 6. Delete an item

**Do:** right-click an item → Delete.

**Works:** it moves to Trash and stays there through a relog.

**Fails:** it reappears after a moment or after relog.

### 7. Delete a folder **outside** Trash — NOT REACHABLE THROUGH THE VIEWER

> **Settled 2026-09-04: this step cannot be performed.** It is not unrun, and it is not blocked — **a resident
> has no way to ask for it.**

**Do:** nothing. There is no gesture that produces it.

Firestorm and the LL viewer offer exactly three folder-removal routes, and none of them is a delete of a folder
outside Trash:

| Route | What it actually is |
|---|---|
| Delete / right-click → Delete | a **MOVE** to Trash — `PATCH /category` changing `parent_id`, not `DELETE /category` |
| Purge a single item in Trash | acts on an item, not a folder |
| Empty Trash | `DELETE /category/{trash}/children` — **step 8**, and it passed |

**There is no shift-delete for folders.** The protected-folder rule reinforces this: the viewer routes folder
removal through the outfit/inventory machinery rather than raw deletion.

**Verified in-world 2026-09-04.** Two folders were deleted, one nested and one at the inventory root. **Both
moved to Trash**, and both were still in Trash after a restart. The AIS log for the whole day shows
`CreateInventory` and `UpdateCategory` — the move — and **no `RemoveCategory` at any point**.

**What this means for A2b.** The `ONLYIFTRASH` work is **still correct and still wanted**: the spec defines
`DELETE /category/{id}` (`llaisapi.h`), so the route must exist and must behave honestly when something calls it —
a script, a future viewer, another AIS client, or our own tooling. But **it was never gating a resident-visible
operation.** The folder removal residents actually perform is Empty Trash, which is step 8, and that has passed
since before the Robust redeploy.

**Server side:** `grep "DELETE /category" <log>` — expect nothing from ordinary use.

### 8. Empty Trash

**Do:** right-click Trash → Empty Trash. Have at least one folder **and** one loose item in there first.

**Works:** Trash empties completely, including the contents of the subfolder, and stays empty through a relog.

**Fails:** Trash still shows its contents afterwards — the purge response must **enumerate** the direct children
(spec §1d-bis); unlike a folder delete, nothing on the viewer side sweeps them for us. Partially emptied means the
service refused part of it; the response names the survivors.

---

## Phase 3 — outfits, the destructive ones (steps 9–10)

**These are the steps that can strip an avatar.** Do them on Truly first, not on an avatar whose outfit matters.

### 9. Wear an outfit (slam)

**Do:** Appearance → Outfits → wear a saved outfit.

**Works:** the avatar changes to that outfit; Wearing lists exactly the new items and none of the old.

**Fails, in order of seriousness:**
- **The avatar ends up wearing nothing / partially dressed.** This is the failure the slam ordering is built to
  make impossible (links are created before the old ones are removed), so if you see it, stop and report it — the
  ordering is wrong, not just a transient.
- **Duplicated attachments or doubled clothing layers.** The new links were created and the old ones were not
  removed. Recoverable by wearing the outfit again. This is the known window (Ledger A-Q10): there is no
  transaction under a slam.
- The outfit does not change at all: the slam was refused; check the log.

**Server side:** `grep "PUT /category" <log>`.

### 10. Take off a garment

> **Wait about ten seconds in-world before relogging.** Otherwise this step races the appearance-save timer
> instead of testing AIS.
>
> Taking something off updates the avatar's appearance record through a **deferred** write:
> `AvatarFactoryModule.QueueAppearanceSave` schedules it `m_savetime` seconds out — five by default — and
> `SaveAppearance` reads the `ScenePresence` only when the timer fires. Log out inside that window and, before
> `dc4e417bb3`, the write was dropped silently and the garment came back on the next login. That is precisely
> what happened on 2026-09-04: the detach was recorded at 14:09:35,408, the save was due at ~14:09:40.4, and the
> avatar left at ~14:09:40.0.
>
> `dc4e417bb3` flushes the queue on close, so the fast path is now covered too — but a run that logs out
> immediately is still testing the flush rather than the take-off. **Give it ten seconds and the test means what
> it says.** Also note: take-off is `DELETE /item` (RemoveItem), **not** a slam; the viewer removes the COF link
> and reconciles the attachment itself.

**Do:** right-click a worn garment → Take Off. Wait ~10 seconds. Then relog.

**Works:** it comes off, the rest of the outfit is untouched, and it stays off through a relog.

**Fails:** the garment comes back; or **other** garments come off with it — the slam replaced the folder's links
with the wrong set.

---

## Phase 4 — create and copy (steps 11–12)

### 11. Create a folder

**Do:** Inventory → + → New Folder, then rename it.

**Works:** the folder appears, keeps its name, and survives a relog.

**Fails:** the folder does not appear at all, or appears and vanishes on relog.

**Server side:** `grep "POST /category" <log>`.

### 12. Copy a library outfit

**Do:** open Library → an outfit folder → right-click → Copy to Inventory (or drag it into your inventory).

**Works:** the folder and its contents appear in your inventory, nested as they were in the library, and the items
are usable — wear one to confirm the permissions came across.

**Fails:** nothing arrives; or the folder arrives empty; or the items arrive but cannot be worn (permissions were
degraded — the library copy must carry the source's own masks, not `NextPermissions`).

**Server side:** `grep "COPY /category" <log>`. The destination folder id travels in the `Destination` header.

---

## Phase 5 — the things that are known not to work (steps 13–15)

Confirm these behave as documented rather than in some worse way.

### 13. Creating an inventory **item** — RESOLVED, it just works

**Do:** Inventory → + → New Notecard (or New Script, New Clothing).

**Expected:** the notecard **is** created, normally, and AIS is never involved. Settled in A11 from the source:
the AIS arm of `create_inventory_item` is inside `#ifdef USE_AIS_FOR_NC`
(`llviewerinventory.cpp:1120`-`:1166`), the macro is not defined, so control falls unconditionally to the legacy
`CreateInventoryItem` UDP send at `:1169`. Confirmed in the 2026-09-04 run: the item was created over UDP and the
only AIS request was a `FetchItem` syncing the result.

**This step was once "the single biggest argument against flipping this flag more widely". It is not any more** —
the code is LL's, so stock viewers behave identically, and our 501 route is simply never reached for item
creation. Watch only that the item appears and survives a relog.

### 14. Hypergrid folder deletion — expected refusal

**Do:** only if this region serves hypergrid visitors. As an HG visitor, try to delete a folder.

**Expected:** refused. `HGInventoryService` and `HGSuitcaseInventoryService` answer NOGO for folder deletion
whatever the flag says, so the verification step turns that into a 500.

### 15. Folder thumbnail and favourite — silently dropped

**Do:** set a folder thumbnail, or mark a folder as a favourite.

**Expected:** the operation appears to succeed and the setting does not persist across a relog. This tree's
`InventoryFolderBase` has no column for either, so both are accepted and dropped.

## Phase 6 — saving an asset (steps 16-17)

Added in A18. **The checklist had no step that saved an asset**, which is why steps 1-15 all passed while
`PATCH /item` was silently discarding every asset id a wearable save sent. Renaming an item exercises the same
route and does persist, so nothing here caught it; the defect surfaced during Q-11 preparation instead. Any
future route that stores something needs a step that reads it back **after the viewer has been made to forget**,
not just after the operation.

### 16. Edit a wearable, change a colour, save — the colour persists

**Do:** Appearance → Edit an item you can modify (a skirt, a shirt). Change its colour. **Save**. Close the
Appearance floater, then re-open it and look at the item again.

**Expected:** the colour you saved. Re-opening is the point: it makes the viewer re-read the item rather than
draw from its own cache, which is where the change lives until the server has actually stored it.

**Also check, if you have the log and the database:**

- the region log shows `ASSET XFER ... uploaded <asset>` and then a `PATCH .../item/<item>` answering 200;
- the item's `assetID` column equals **that** uploaded asset, not the one it had before;
- the `_updated_category_versions` in the PATCH response is **higher** than the folder's previous version. If the
  same version comes back twice for two different saves, nothing was written — that is the A18 signature.

### 17. Edit a WORN wearable, change a colour, save — the sim rebakes that channel

**Do:** wear the item first. Appearance → Edit it → change its colour → **Save**. Stay in world and watch
yourself; do not relog.

**Expected:** within about **7 s** the colour changes in-world, on you and to everyone else, with no relog. The
region log shows one `[SSB]` `reason=CofChanged` bake in which **that wearable's channel is `Baked` and the
others are `Reused`** — the whole point is that only what changed is recomputed.

Added in S9. Step 16 checks the asset is *stored*; this checks the region *acts* on it. They are different
failures and step 16 passed while this one did not: on 2026-09-05 four edits stored correctly and produced no
bake at all, because an edit moves neither the worn set nor any signal the region was watching.

**If nothing bakes,** the thing to check is whether the AIS `UpdateItem` for that item reported an asset change —
the region logs `item ... is worn by ... and its asset changed ...; queueing an appearance save` at DEBUG. No
such line means the item was not in the presence's wearables, which is a different bug from this one.

### 18. Add a SECOND wearable of a type — the sim layers both, newer on top

**Do:** wear a shirt. Then right-click a *different* shirt in inventory and choose **Add** (not Wear — Wear
replaces, Add layers). Stay in world.

**Expected:** you are wearing both, with the added one **on top**. The region log shows **one** `[SSB]`
`reason=CofChanged` bake with `Upper=Baked` — not `Reused` — and the other channels reused.

Added in S10. Before it, this failed silently and looked like a bake that had nothing to do: on 2026-09-06 at
10:09:52 both shirts were linked in the COF, the bake reported **`reused 6/6`**, and the `Avatars` record held
`Wearable 4:0` alone. The second shirt never reached the sim's wearables, because nothing on a bit-0 region
turned a COF link into one.

**Also check, if you have the log and the database:**

- the `Avatars` record for the agent now has **both** `Wearable 4:0` and `Wearable 4:1`;
- the two COF link items' `description` columns are `@400` and `@401` — that is the viewer's ordering
  information (`"@" + type * 100 + index`), and it is what the sim layers by, higher index on top;
- the region log carries `[SSB]: <name>'s worn set from the COF: … Shirt x2 …` at DEBUG.

**If the bake reuses everything,** the derivation did not see the second link. Check that DEBUG line first: no
line at all means the COF read found nothing to change, and a warning about links *"this region cannot resolve
as a wearable"* means the link's target did not come back from inventory — a different fault, and the S8 rule
deliberately keeps the old wearables in that case rather than emptying the slot.

Then swap which shirt is on top (take both off, add them in the other order) and confirm the bake follows.

### 19. Fetch ANOTHER resident's folder through your own AIS cap — 404

**Do:** log in as **Truly Bazar**. Get **Aleric**'s Current Outfit folder UUID out of the database (or out of
Aleric's own region log line, `FetchCOF resolved "current" to <folder>`), then ask Truly's own `InventoryAPIv3`
cap for it directly — a `GET <capurl>/category/<Aleric's COF>/children`. The cap URL is in Truly's seed
response; `curl` or the viewer's own debug console will do.

**Expected: 404**, with an error body and **no** `_embedded`. Nothing of Aleric's may appear, and Truly's own
inventory must be unaffected.

**Then the same UUID against the mutating routes**, one at a time, and each must also be refused with Aleric's
row unchanged afterwards: `PATCH /category/<Aleric's COF>` with `{"name":"x"}`, `DELETE /category/<Aleric's
COF>/children`, and `PUT /category/<Aleric's COF>/links` with an empty array. Check Aleric's folder name,
version and link rows in the database before and after.

**Why this step exists.** AIS-SEC-1 (ledger row A20): the region backend was a pure pass-through to
`IInventoryService`, which resolves by UUID and disregards the principal it is handed — so until
`1.1.<N>-alpha+f8263e22b8` every one of those requests **succeeded**, and the last of them would have stripped
another resident's outfit. The suite never saw it because every HTTP fixture ran on `FakeAisBackend`, which
enforces the scoping the real backend lacked.

**This is the "did it land" check for the AIS-SEC-1 deploy** (procedure step 11) and it fails differently from
the bug it replaces: before the fix the request answers 200 and does the work, after it the request answers 404
and does nothing. A hash cannot tell those apart.

### 20. A malformed slam body is refused, and the outfit survives it

**Do:** as **Truly**, get her own `InventoryAPIv3` cap URL from the seed response and `curl` a deliberately
broken body at her **own** Current Outfit links route:

```
curl -X PUT -H "Content-Type: application/llsd+xml" \
  --data-binary '<llsd><array><map>' \
  "<capurl>/category/<Truly's COF>/links"
```

**Expected: 400**, with `"message"` reading `malformed LLSD body`. Then reopen **Appearance**: the outfit is
**unchanged** — same garments, same order. Nothing was written.

**Then prove the legitimate empty slam still works**, because the fix must not have bought safety by breaking
it: `PUT` a body of exactly `[]` to the same URL. That answers **200** and empties the COF links, which is
what taking off the last garment does. **Re-wear from Outfits afterwards** to put Truly back.

**Also worth one run each**, same URL, all expected to answer 400 and leave the outfit alone: no body at all
(`--data-binary ''`), and `{}` (`--data-binary '<llsd><map /></llsd>'`).

**Why this step exists.** AIS-SEC-2 (ledger row A21). `AisHandler.ReadBodyOsd` ended in
`catch { return new OSDMap(); }` and returned that same empty map for an absent body, and
`AisSlam.ParseBody` read an empty map as an empty slam — so a truncated `PUT`, and a dropped connection is
enough, was read as *"replace every link with none"* and emptied the wearer's Current Outfit. Before
`1.1.359-alpha+6c37b5e9d5` the first command above returned **200** and Truly came back naked.

**The 400 and the `[]` case must both be observed.** Either alone proves nothing: refusing everything would
also pass the first half, and it would break the way an outfit is taken off.

### 21. Truly and Aleric after the duplicate-folder cleanup

**Do:** log in as **Truly**, then as **Aleric**. For each: wear one item and remove one item. Open
**Appearance** and confirm it still works. Show the caps list and `curl` their own
`<capurl>/category/<their COF>/links` — the outfit comes back.

**Then the database**, per agent:

```sql
SELECT f.folderID, f.version,
       CASE WHEN f.parentFolderID = r.folderID THEN 'ROOT' ELSE 'suitcase' END AS location
FROM inventoryfolders f JOIN inventoryfolders r ON r.agentID=f.agentID AND r.type=8
WHERE f.type=46 AND f.agentID='<agent>';
```

**Expected: exactly one type-46 row parented to ROOT. The suitcase COF is expected and must still be
present** — for Truly that is `52c327c4-cb7d-4365-a7f0-62a6f7545265`, for Aleric `88028d53-4a08-473c-ac52-fb301727edb8`.
Two rows total per agent, one of each location. **A missing suitcase row is a failure, not a success.**

**Also check `Textures`**, which is what AIS-COF-1 actually cleaned:

```sql
SELECT COUNT(*) FROM inventoryfolders f JOIN inventoryfolders r ON r.agentID=f.agentID AND r.type=8
WHERE f.type=0 AND f.parentFolderID=r.folderID AND f.agentID='<agent>';
```

**Expected 1.** Aleric had **nine**; Legion Hienrichs had two, and his surviving one must still hold its
**102 items**.

**And the new WARN must be silent.** After both logins, `grep '\[XINVENTORY\]: agent' <region log>` returns
nothing. A line there means a root-level duplicate has come back, which after this session should be
impossible on a single Robust.

### 22. Two slams at once on Truly's COF: one outfit wins, never both

**Do:** as **Truly**, run `two-slam-race.sh` from the AIS-SEC-3 handoff
(`D:\legiongrid\_ops\handoffs\HANDOFF-AIS-SEC-3-20260912.md`). Fill in only the **cap path** and the **two
link sets**; the script targets `category/current/links`, so the COF id is resolved server-side and is not a
placeholder. It fires both `PUT`s concurrently and prints both status codes.

**Expected:**

- the two status codes are **both 200**, or **one 200 and one 503** (with `Retry-After: 2`). A 503 is a pass,
  not a failure - it means the second slam waited 15 s for the folder and declined to proceed unserialised.
- `GET <cap>/category/current/links` afterwards returns **exactly one of the two sets** - never the union,
  and never a mixture.
- the region log shows two `SlamFolder ->` lines whose statuses match the two above. If one is 503, it is
  preceded by a `WARN [AIS]: SlamFolder on folder <cof> for agent <truly> waited 15s for the folder lock`.

**Then confirm the ordinary path still works:** a viewer **Replace Outfit** between two saved outfits, twice.
Both must apply, with `SlamFolder -> 200` each time and SSB following. A lock that serialises correctly and a
lock that deadlocks look identical until you try the normal case.

**Why this step exists.** AIS-SEC-3 (ledger row A23). A slam snapshots a folder's links, creates the wanted
set, then deletes the snapshot; nothing ordered two of them. Two slams that both snapshotted the old links,
both created their own set and both deleted only what they saw left the folder holding the **union of two
outfits** - reproduced deterministically in `AisConcurrencyHttpTests`. Before
`1.1.367-alpha+24fedc52b9` the script above would leave Truly wearing both sets at once.

### 23. A legacy WindLight setting applies, and a malformed one leaves the environment alone

**Not an AIS step** - it belongs to ENV-1 and lives here because this is the grid's only live checklist.
Needs an **estate manager** and, for the first half, a viewer old enough to use the legacy WindLight route
(a modern viewer uses the checked ExtEnvironment handler at `EnvironmentModule.cs:637` instead and will not
exercise this path at all).

**Do, part 1 - the ordinary case still works.** As an estate manager on Ebony, apply a legacy WindLight
environment setting (Region/Estate > Environment on an older viewer). **Expected:** it applies, and the region
log shows `New Environment settings has been saved from agentID <you> in region Ebony`.

**Do, part 2 - a malformed body is refused and changes nothing.** Note the region's current environment first.
Then, as the same estate manager, `curl` a deliberately broken body at the legacy setter:

```
curl -X POST -H "Content-Type: application/llsd+xml" \
  --data-binary '<llsd><array><map>' \
  "<capurl>/EnvironmentSettings"
```

**Expected:** the response is the handler's ordinary refusal shape - `success: false` with a `fail_reason` of
*"Environment settings for region Ebony were not in the expected format, settings not saved."* - and the region
log carries a **WARN**:

```
[Environment ...]: rejected a legacy WindLight setting for region Ebony from agentID <you>:
the body is Unknown, an LLSD array was expected
```

Then re-check the environment: **unchanged**. Before ENV-1 that request instead produced a
`NullReferenceException` in the log and a generic *"Environment Set for region ... has failed"* - it also left
the environment alone, but only because the exception happened to land before the write.

**Why this step is worth running even though the outcome looks the same.** The fix removed a *throw* that was
accidentally protecting a write. The point of part 2 is that the refusal is now deliberate and legible - a WARN
naming the body's type - rather than a stack trace; and the point of part 1 is that the new type check did not
break the legitimate path. **Part 1 is the half that would catch a mistake here**, because refusing everything
would also satisfy part 2.

See `Docs/feature/ais-v3/AUDIT-1-MALFORMED-LLSD.md` §5 for how this was found, and ledger row A26.

---

## The Robust question — RESOLVED, and it was never about step 7

> **Robust was reconciled and redeployed on 2026-09-04** (`1.1.208-alpha+a2c8fb63f3`), so `ONLYIFTRASH` is live.
> And step 7 turned out not to be reachable through any viewer, so this was never blocking a resident-visible
> operation — see step 7 above. The section below is kept because the wire-compatibility reasoning still governs
> the route.


A2b added an optional `ONLYIFTRASH` field to the inventory wire so that AIS could ask for a folder delete that is
not restricted to Trash. It was made backward-compatible in both directions on purpose: the simulator sends the
field only when it is `false`, and the Robust handler defaults it to `true` when it is absent
(`XInventoryServicesConnector.DeleteFolders`, `XInventoryInConnector.HandleDeleteFolders`).

Legion Grid resolves inventory **remotely**: `config-include/Grid.ini:12` sets
`InventoryServices = "RemoteXInventoryServicesConnector"`, and `GridCommon.ini:39` points it at
`http://127.0.0.1:8003`. Every folder delete therefore crosses to Robust.

~~So until the grid server is redeployed with the A2b change, step 7 will fail.~~ That was true of the wire, and
it is now moot twice over: Robust carries the change as of 2026-09-04, and no viewer can request the operation
anyway.

Emptying Trash (step 8) and deleting a folder **inside** Trash were never affected: those satisfy the old gate,
and step 8 passed before the Robust deploy.

**The honest summary:** A2b made the route correct; the Robust deploy made it live; neither changed anything a
resident can see. The Robust deploy's real value was ending the four-commit split (see
`../repo-audit/R1-ROBUST-RECONCILIATION.md`), not unblocking step 7.

## Firestorm is the only client available here

**Superseded by Ledger P-3 (A8, 2026-09-04).** This section was written expecting the LL viewer to be the
primary run and Firestorm a second pass. That is not possible on this grid: the stock LL viewer will not start
against it, because its Vivox voice component refuses to initialise outside SL. **Every run is a Firestorm run,
and there is no control.**

Firestorm remains a test client and never an authority (Ledger P-1). What changes is the reading of a green
result: it means Firestorm is satisfied, not that the protocol is right. Anything observed only in Firestorm
must be checked against the LL viewer source before it is relied on — step 13's legacy fallback is the live
example (`A5-RUN-2026-09-04.md`). These are the steps where Firestorm's own machinery differs most, so they
carry the least transferable evidence:

- **1** (full load) — Firestorm's fetch pacing differs;
- **9 and 10** (wear / take off) — Firestorm has its own outfit machinery and may still send
  `AvatarNowWearing` over UDP alongside the slam (open question A-Q6 in the SSB ledger);
- **8** (Empty Trash);
- **12** (library copy).

If Firestorm and the LL viewer **source** disagree on any of these, the source is right and the difference is
recorded, not fixed against Firestorm. The disagreement has to be found by reading the source, because the
viewer itself cannot be run here.

---

## Stopping

Turn `AIS_Enabled` back to `false` in the region section and restart the region. The caps disappear from the seed
response and every viewer returns to the legacy paths on its next login. Nothing in inventory needs undoing for the
flag itself — but anything steps 4–12 changed is real, which is what step 0d's backup is for.

**Report:** for each step, pass / fail / not-run, and for any failure the log lines around it. That report is what
decides whether the flag goes anywhere near a second region.
