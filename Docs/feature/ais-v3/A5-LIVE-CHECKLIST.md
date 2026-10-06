# Turning on AIS v3: checks and known limits

This is for an operator enabling `[AIS]` on a simulator for the first time. Configuration and behaviour are
described in `AIS-V3-SPEC.md` §0, and the viewer contract in §1 of that file.

**Why to go carefully.** Once a region advertises `InventoryAPIv3`, the LL viewer sends deletes, purges, outfit
changes and folder creation through AIS, with no fallback (`AIS-V3-SPEC.md` §1g). If something is wrong, an
operation does not happen; it does not merely run slowly. Try it on one region first, and run the read-only
checks before anything that changes inventory.

## Before you start

1. Choose a test region and a test avatar whose inventory and outfit you can afford to disturb.
2. Back up that avatar's inventory (`save iar`, or a database dump of its `inventoryfolders` and
   `inventoryitems` rows). The mutating checks below make real changes.
3. Enable AIS for the test region only. Either set `AIS_Enabled = true` in that region's own section and leave
   `[AIS] Enabled = false`, or set `[AIS] Enabled = true` and `AIS_Enabled = false` in every other region's
   section. Restart the simulator.
4. Check the log. Each region logs `region <name>: AIS v3 ON` or `off`, followed by `(global)` or
   `(region section)`, and each enabled region also logs the capabilities it advertises. Only the test region
   should say `ON`.

## Read-only checks

Stop and turn AIS off if any of these fails.

| # | Do | Expect | If not |
|---|---|---|---|
| 1 | Clear the viewer's inventory cache, log in, open Inventory and let it settle. | The tree fills in and the item count stops growing. | A folder fetched over and over is the signature of a response missing one of the three `_embedded` collections (§1c-bis). |
| 2 | Open a folder several levels deep that was not opened before. | Its contents appear, after at most one brief fetch. | |
| 3 | Open Appearance, Wearing. | Every worn item is listed with its real name. | Blank entries mean the outfit links resolved but their targets did not. |

## Changes to single items and folders

| # | Do | Expect |
|---|---|---|
| 4 | Rename an item, then relog. | The new name persists. A name that reverts within seconds usually means the parent folder was missing from `_updated_category_versions` (§1d-bis). |
| 5 | Rename a folder you created, then relog. | The new name persists. |
| 6 | Delete an item, then relog. | It is in Trash and stays there. |
| 7 | Put a folder and a loose item in Trash, Empty Trash, then relog. | Trash is empty, including the subfolder's contents. |
| 8 | Create a folder and rename it, then relog. | It persists with its name. |
| 9 | Copy a library folder into inventory and wear one of its items. | The folder arrives with its contents nested as in the library, and the item can be worn. |

## Outfits

These checks can leave the test avatar partly dressed if something is wrong.

| # | Do | Expect |
|---|---|---|
| 10 | Wear a saved outfit. | The avatar changes to it, and Wearing lists exactly the new items. |
| 11 | Take off one garment, wait about ten seconds, then relog. | It stays off and the rest of the outfit is untouched. The wait matters: appearance saves are deferred by `[Appearance] DelayBeforeAppearanceSave` (5 s by default), so a faster relog tests the save on logout rather than the take-off. |
| 12 | Edit a wearable you own, change its colour, save, then close and reopen Appearance. | The saved colour. Reopening makes the viewer re-read the item instead of drawing on its own cache. |

## Known limits

- **Creating inventory items** through AIS (`CreateInventory` with a non-empty `items` array) answers 501.
  Stock viewers do not use that path: its viewer code is compiled out (`USE_AIS_FOR_NC` in
  `llviewerinventory.cpp`), and they create items over the legacy UDP message instead. New notecards, scripts
  and clothing therefore work normally.
- **Hypergrid inventory.** `HGInventoryService` and `HGSuitcaseInventoryService` refuse folder deletion, so a
  folder delete by a Hypergrid visitor fails whatever AIS does.
- **Folder thumbnails and favourites** are accepted and not stored. `InventoryFolderBase` has no column for
  either, so they do not survive a relog.
- **Orphans.** `GET /orphans` reports orphaned folders only, never orphaned items (§1e-bis).
- **Library.** `LibraryAPIv3` is read-only: every mutation on it answers 405.

## Turning it off

Set `AIS_Enabled = false` in the region's section, or `[AIS] Enabled = false`, and restart the simulator. The
capabilities disappear from the seed response, and viewers return to the legacy inventory paths at their next
login. Turning AIS off undoes nothing in inventory: changes made while it was on are real. Use the backup from
step 2 to undo them.
