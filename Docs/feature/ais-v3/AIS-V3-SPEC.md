# AIS v3: the inventory API the LL viewer drives

AIS v3 is the HTTP inventory API (the `InventoryAPIv3` and `LibraryAPIv3` capabilities) that current Linden
Lab viewers use for inventory changes. This document has two parts:
- §0, for operators: what turning it on does, how to configure it, and where the code is;
- §1, for maintainers: the viewer contract the implementation follows, read from the LL viewer source.

Turning-on checks and known limits are in `A5-LIVE-CHECKLIST.md`. Code and test comments cite this file by
section number (§1a to §1g), so the numbers do not change.

## 0. For operators

### 0.1 What it does

When enabled on a region, the simulator gives each agent two capabilities:
- `InventoryAPIv3`, for the agent's own inventory;
- `LibraryAPIv3`, for the shared library, read-only (every mutation answers 405).

The handlers translate AIS requests into calls on the region's `IInventoryService`, so no new grid service is
needed.

**Read §1g before enabling it.** Once a region advertises `InventoryAPIv3`, the LL viewer sends all of these
through AIS, with no fallback for the changes:
- fetches;
- deleting items and folders;
- emptying Trash;
- outfit changes (slam);
- creating folders.

If AIS answers an error, the operation simply does not happen.

### 0.2 Configuration

In the simulator's ini (`OpenSimDefaults.ini` ships the default):

```ini
[AIS]
    Enabled = false      ; true advertises InventoryAPIv3 and LibraryAPIv3 on every region of this simulator
```

A single region opts out of a simulator-wide `true` in its own section:

```ini
[My Region]
    AIS_Enabled = false
```

The rules:
- The region's own key wins if present. Otherwise the global `[AIS] Enabled` applies. Otherwise AIS is off.
- A region section that does not mention `AIS_Enabled` does not opt out.
- `AIS_Enabled = true` in a region section turns AIS on for that one region even when the global setting is
  `false`.
- The per-region key is `AIS_Enabled`, not `Enabled`, because a region section holds settings for many modules.

Each region logs one INFO line when it loads, saying whether AIS is on and which setting decided:
`region <name>: AIS v3 ON (global)` or `... (region section)`. An enabled region also logs the capabilities it
advertises.

### 0.3 Behaviour worth knowing

- **Scoping.** Each agent's `InventoryAPIv3` handler is bound to that agent. A request naming another
  resident's folder or item answers 404 and changes nothing. A library `COPY` can only write into the
  requesting agent's own folders.
- **Malformed bodies.** A mutating request whose body is not valid LLSD answers 400 `malformed LLSD body` and
  writes nothing. A body over 1 MiB answers 413 unread. A slam with no body at all answers 400 `missing body`
  rather than being read as "remove every link". The empty array `[]` is a valid, empty slam.
- **Concurrent slams.** Slams of the same folder are serialised. A slam that waits more than 15 s for the folder
  answers 503 with `Retry-After: 2` rather than proceeding unserialised.
- **Depth.** A requested depth is clamped to 50, the viewer's own ceiling (§1c-bis).
- **Not implemented** (501): creating inventory items through `CreateInventory` with a non-empty `items` array.
  Stock viewers never send this (A5 checklist); they create items over the legacy UDP path.
- **Dropped silently:** folder thumbnails and the favourite flag. This tree's `InventoryFolderBase` has no
  column for either.
- **Hypergrid inventory:** `HGInventoryService` and `HGSuitcaseInventoryService` refuse folder deletion
  whatever AIS asks.

### 0.4 Where the code is

| Area | Path |
|---|---|
| Region module, configuration, capability registration | `Source/OpenSim.Region.ClientStack.LindenCaps/AIS/AISv3Module.cs` |
| Request handling | `AisHandler.cs`, `AisRouter.cs` (URL parsing), `AisEnvelope.cs` (responses), `AisMutation.cs`, `AisSlam.cs`, `AisPurge.cs`, `AisCopy.cs`, `AisFolderLocks.cs`, `AisWornAssets.cs` in the same folder |
| Inventory seam | `IAisInventoryBackend.cs`, `AisInventory.cs` |
| Module discovery | `Source/OpenSim.Region.ClientStack.LindenCaps/PluginRegistration.cs` |
| Shipped config | `Source/OpenSim.Server.RegionServer/AppData/OpenSimDefaults.ini`, `[AIS]` |
| Tests and golden fixtures | `Tests/OpenSim.Region.ClientStack.LindenCaps.AIS.Tests/` |

The handler logic sits behind `IAisInventoryBackend`, so the same handler could later be hosted on Robust
rather than in the region.

---

## 1. The viewer contract

**Source:** the Linden Lab viewer, version 26.1.1. Each row cites the file and line it was read from. Lines
marked **UNVERIFIED** could not be pinned to a line in the files read, and say which file would settle them.
The main files are `indra/newview/llaisapi.h`, `indra/newview/llaisapi.cpp`, `indra/newview/llinventorymodel.cpp`
and `indra/newview/llviewerinventory.cpp`.

Cap names: `InventoryAPIv3` (`llaisapi.cpp:48`) and `LibraryAPIv3` (`:49`). The viewer asks the seed cap for
both (`AISAPI::getCapNames`, `:72-76`). HTTP timeout per request 180 s (`:50`). Maximum requested folder depth
50 (`MAX_FOLDER_DEPTH_REQUEST`, `:58`).

## 1a. Operations

`{inv}` is the InventoryAPIv3 cap URL and `{lib}` the LibraryAPIv3 cap URL. `tid` is a fresh random UUID per
call (`LLUUID tid; tid.generate();`).

Bodies are **LLSD XML**:
- Every request body is serialised with `LLSDSerialize::toXML` (`indra/llmessage/llcorehttputil.cpp:144` POST,
  `:169` PUT, `:193` PATCH).
- Every response is parsed with `LLSDSerialize::fromXML` (`:123`, `responseToLLSD`).
- `HttpCoroutineAdapter::checkDefaultHeaders` (`:1211-1229`) sets both `Content-Type` and `Accept` to
  `application/llsd+xml` on every AIS request, unless the caller already set them (the literal is at `:478`
  and `:497`).
- The response parse does **not** check the content type; it only gates a warning (`:495-500`). A response is
  read as LLSD XML whatever it is labelled.

| # | Operation (`llaisapi.h`) | Verb | URL relative to the cap | Query | Body | Headers | Source |
|---|---|---|---|---|---|---|---|
| 1 | `CreateInventory(parentId, newInventory)` | POST | `{inv}/category/{parentId}` | `tid={uuid}` | `newInventory` map: `categories` (array of category maps) verified at `llinventorymodel.cpp:1035-1042`; `items` / `links` arrays built in `llviewerinventory.cpp:1156,1370` | none | `llaisapi.cpp:99-143`, url `:115` |
| 2 | `SlamFolder(folderId, newInventory)` | PUT | `{inv}/category/{folderId}/links` | `tid={uuid}` | an LLSD array of link maps (§1d) | none | `llaisapi.cpp:145-180`, url `:161` |
| 3 | `RemoveCategory(categoryId)` | DELETE | `{inv}/category/{categoryId}` | — | none | none | `:182-217`, url `:197` |
| 4 | `RemoveItem(itemId)` | DELETE | `{inv}/item/{itemId}` | — | none | none | `:219-252`, url `:234` |
| 5 | `CopyLibraryCategory(sourceId, destId, copySubfolders)` | COPY | `{lib}/category/{sourceId}` | `tid={uuid}` and, when `!copySubfolders`, the literal suffix `,depth=0` **appended to the tid value with a comma** (`url += ",depth=0"`, `:278`), i.e. `?tid=<uuid>,depth=0` | none | destination = `destId.asString()` (`:282`) passed as the `copyAndSuspend` destination (`:294`), sent as the HTTP **`Destination`** header (`llcorehttputil.cpp:1135`) | `:255-301`, url `:275` |
| 6 | `PurgeDescendents(categoryId)` | DELETE | `{inv}/category/{categoryId}/children` | — | none | none | `:303-339`, url `:318` |
| 7 | `UpdateCategory(categoryId, updates)` | PATCH | `{inv}/category/{categoryId}` | — | map of category fields (§1d-bis) | none | `:341-374`, url `:355` |
| 8 | `UpdateItem(itemId, updates)` | PATCH | `{inv}/item/{itemId}` | — | the item's full `asLLSD()` with `asset_id`/`shadow_id` replaced by `hash_id` (callers `:454,1422,1434`; see the `UpdateItem` field table in §1d-ter) | none | `:376-409`, url `:391` |
| 9 | `FetchItem(itemId, type)` | GET | `{inv|lib}/item/{itemId}` (`lib` when `type == LIBRARY`) | — | none | none | `:412-445`, url `:426` |
| 10 | `FetchCategoryChildren(catId, type, recursive, depth)` | GET | `{inv|lib}/category/{catId}/children` | `depth=N` where N = 50 if `recursive`, else `min(depth, 50)` (`:463-474`) | none (the viewer keeps `{"depth": N}` locally for error handling, `:490`) | none | `:447-498` |
| 11 | `FetchCategoryChildren(identifier, recursive, depth)` | GET | `{inv}/category/{identifier}/children`, where `identifier` is any string, e.g. an alias | `depth=N` as above (`:527`) | none | none | `:500-549`, url `:514` |
| 12 | `FetchCategoryCategories(catId, type, recursive, depth)` | GET | `{inv|lib}/category/{catId}/categories` | `depth=N` (`:578`) | none | none | `:551-599`, url `:565` |
| 13 | `FetchCategorySubset(catId, specificChildren, type, recursive, depth)` | GET | `{inv|lib}/category/{catId}/children` | `depth=N&children={id1},{id2},...` (`:642-648`); the viewer warns above 2000 URL characters (`:651`) but still sends | none | none | `:601-678`, url `:628` |
| 14 | `FetchCOF()` | GET | `{inv}/category/current/links` | — (local `depth` 0, `:709`, not on the URL) | none | none | `:680-714`, url `:692` |
| 15 | `FetchCategoryLinks(catId)` | GET | `{inv}/category/{catId}/links` | — (local depth 0, `:745`) | none | none | `:716-751`, url `:728` |
| 16 | `FetchOrphans()` | GET | `{inv}/orphans` | — | none | none | `:753-784`, url `:765` |

Every operation first resolves the cap (`getInvCap()` / `getLibCap()`, `:79-97`); with no cap the callback fires
with a null id and nothing is sent. Requests are coroutines throttled to 2048 in flight (`:54`, `:786-834`).

A `simulate` query parameter does **not** appear anywhere in `llaisapi.cpp`, so this viewer never sends it.
**UNVERIFIED** whether any other viewer code path adds it.

## 1b. Aliases

- `current` is the Current Outfit folder, used as `{inv}/category/current/links` by `FetchCOF` (`:692`). It is
  the only alias literal in `llaisapi.cpp`.
- The string-identifier overload of `FetchCategoryChildren` (`:500-549`) accepts any identifier, so
  `current/children` is a legal request shape. Which callers use it is **UNVERIFIED**.

## 1c. Response envelope

Every response, success or error, goes through `AISUpdate` (`onUpdateReceived`, `:836-849` →
`AISUpdate::doUpdate`). `parseUpdate` is `parseMeta` followed by `parseContent` (`:1094-1099`). The viewer
treats **fetch** commands differently from **mutations**, and the difference matters for filtering below. The
fetch commands are `FETCHITEM`, `FETCHCATEGORYCHILDREN`, `FETCHCATEGORYCATEGORIES`, `FETCHCATEGORYSUBSET`,
`FETCHCOF`, `FETCHCATEGORYLINKS` and `FETCHORPHANS` (`:1028-1034`).

### Meta keys (`parseMeta`, `:1101-1177`), all top-level in the response map

| Key | LLSD type | What the viewer does |
|---|---|---|
| `_categories_removed` | array of uuid | each known category: parent's descendent delta −1, id queued for deletion (`:1105-1120`) |
| `_category_items_removed` | array of uuid | each known item: parent delta −1, queued for deletion (`:1123-1140`) |
| `_removed_items` | array of uuid | same handling as `_category_items_removed` (`:1124`) |
| `_broken_links_removed` | array of uuid | same handling (`:1142-1157`) |
| `_created_items` | array of uuid | the item/link ids the viewer will accept from `_embedded` on a mutation (`:1159`); also drives per-id callbacks for `CREATEINVENTORY` (`:995-1004`) |
| `_created_categories` | array of uuid | the category ids accepted from `_embedded` on a mutation (`:1162`); per-id callbacks for `CREATEINVENTORY` (`:984-993`) |
| `_updated_category_versions` | map uuid → integer | the authoritative folder versions after the operation (`:1165-1176`); see §1e |

This viewer does **not** read `_updated_items`, `_updated_categories` or `_removed_categories`; there is no
reference to them in `llaisapi.cpp`. The removal key is `_categories_removed`. Emitting the other names is
harmless; relying on them is wrong.

### Content keys (`parseContent`, `:1179-1214`), top-level

| Condition | Handling |
|---|---|
| `linked_id` **and** `parent_id` present | the response itself is a link: `parseLink` (`:1185-1188`) |
| else `item_id` **and** `parent_id` | the response is an item: `parseItem` (`:1189-1192`) |
| `FETCHCATEGORYSUBSET` | the top-level category is ignored (incomplete); `_embedded` parsed at `depth-1` (`:1194-1202`) |
| else `category_id` **and** `parent_id` | the response is a category: `parseCategory` (`:1203-1206`) |
| else | `_embedded` parsed if present (`:1207-1213`) |

Callback ids (`InvokeAISCommandCoro`, `:953-1011`):
- Fetch-category commands and `COPYLIBRARYCATEGORY` return `category_id`.
- `FETCHITEM` returns `item_id`, overridden by `linked_id` if present (the source comment reads "Error message
  might contain an item_id", `:972-980`).
- `CREATEINVENTORY` fires once per `_created_categories` / `_created_items` entry.

### `_embedded` (`parseEmbedded`, `:1484-1508`): a map with up to five keys

| Key | LLSD type | Where it appears | Handling |
|---|---|---|---|
| `categories` | map: category uuid string → category map | inside a category | `parseEmbeddedCategories` (`:1586-1604`): each parsed at `depth` |
| `items` | map: item uuid string → item map | inside a category | `parseEmbeddedItems` (`:1554-1572`) |
| `links` | map: link item uuid string → link map | inside a category | `parseEmbeddedLinks` (`:1523-1540`): each `parseLink` at `depth` |
| `item` | single item map | inside a link | `parseEmbeddedItem` (`:1542-1552`) |
| `category` | single category map | inside a link | `parseEmbeddedCategory` (`:1574-1584`) |

**Links are a separate collection, not items.** A folder's `_embedded` carries `items` and `links` as sibling
maps, and a link's own `_embedded` may carry the linked `item` or `category`. On a fetch the viewer accepts
everything. On a mutation it ignores any embedded item, link or category whose id is not listed in
`_created_items` / `_created_categories` (`:1531-1534`, `:1547`, `:1562-1565`, `:1579`, `:1594-1597`).

**Descendent count** (`parseDescendentCount`, `:1466-1482`) is known only when `_embedded` has **all three** of
`categories`, `links` and `items` (the sum of their sizes). The one exception is a fetch of a
`FT_CURRENT_OUTFIT` / `FT_OUTFIT` folder, where `links` alone is enough. A folder returned without the full set
gets no descendent count and so no version (§1e), and the viewer keeps re-fetching it.

## 1d. Item, link and category maps as the viewer reads them

| Object | Key | Type | Required | Source |
|---|---|---|---|---|
| item | `item_id` | uuid | yes (selects `parseItem`) | `:1189`, `:1217` |
| item | `parent_id` | uuid | yes | `:1189`; a null parent puts the item in Lost And Found (`:1236`, `:1695-1706`) |
| link | `linked_id` | uuid | yes (selects `parseLink`) | `:1185` |
| link | `item_id`, `parent_id` | uuid | yes | `:1262`, `:1274` |
| link | (permissions, sale info) | — | ignored: the viewer overwrites them with defaults (`:1278-1283`, `:1303-1307`) | |
| category | `category_id` | uuid | yes | `:1203`, `:1328` |
| category | `parent_id` | uuid | yes | `:1203` |
| category | `version` | integer | optional; −1 = unknown | `:1332-1335`, `:1441-1445` |
| category | `agent_id` | uuid | optional; owner of a newly created category (`:1358-1366`) | |
| category | `_embedded` | map | optional | `:1379`, `:1460` |

### The field set the viewer reads

`llaisapi.cpp` hands each object map to `LLViewerInventoryItem::unpackMessage(const LLSD&)` /
`LLViewerInventoryCategory::unpackMessage(const LLSD&)` (`:1223`, `:1268`, `:1368`). The LLSD readers in
`indra/llinventory/llinventory.cpp` are `LLInventoryItem::fromLLSD` (`:984-1183`) and
`LLInventoryCategory::fromLLSD` (`:1289-1352`). That the viewer subclasses delegate to these is **UNVERIFIED**
(it would be settled in `llviewerinventory.cpp`). They are the only LLSD readers for these types, and the label
constants below are theirs.

**Item** (`fromLLSD`, label constants at `:45-63`). Any key not listed is ignored:

| Key | Type | Line | Notes |
|---|---|---|---|
| `item_id` | uuid | `:1004` | |
| `parent_id` | uuid | `:1010` | |
| `thumbnail` | map with `asset_id` | `:1016-1035` | or `thumbnail_id` (uuid) at `:1037` |
| `favorite` | map with `toggled` (bool) | `:1043-1051` | |
| `permissions` | map | `:1054` | inner keys read by `LLPermissions::importLLSD` (**UNVERIFIED**, `llpermissions.cpp`) |
| `sale_info` | map | `:1060` | inner keys read by `LLSaleInfo::fromLLSD` (**UNVERIFIED**) |
| `shadow_id` | uuid | `:1087` | XOR-obfuscated asset id; an alternative to `asset_id` |
| `asset_id` | uuid | `:1094` | |
| `linked_id` | uuid | `:1100` | read **into the asset id**; its presence also selects `parseLink` (§1c) |
| `type` | string **or** integer | `:1106-1120` | asset type; `LLAssetType::lookup` for a string |
| `inv_type` | string **or** integer | `:1122-1135` | inventory type |
| `flags` | integer or binary | `:1137-1148` | |
| `name` | string | `:1150` | non-standard ASCII and `\|` replaced with spaces |
| `desc` | string | `:1156` | |
| `created_at` | integer | `:1162` | |

**Category** (`fromLLSD`, `:1289-1352`):

| Key | Type | Line | Notes |
|---|---|---|---|
| `category_id` | uuid | `:1293` | the constant is `INV_FOLDER_ID_LABEL_WS` = `"category_id"` (`:67`) |
| `parent_id` | uuid | `:1297` | |
| `thumbnail` / `thumbnail_id` | map with `asset_id` / uuid | `:1303-1318` | |
| `favorite` | map with `toggled` | `:1321-1331` | |
| `type` | integer | `:1333-1338` | folder type |
| `type_default` | integer | `:1339-1344` | `INV_ASSET_TYPE_LABEL_WS` (`:66`); read after `type`, so it wins |
| `name` | string | `:1346` | |

It reads neither `version` nor a descendent count; `llaisapi.cpp` reads those itself (§1e). `cat_id`
(`INV_FOLDER_ID_LABEL`, `:46`) is **not** read by `fromLLSD`: the category id key is `category_id`.

**What the server emits.** It sends integers for `type`, `inv_type` and `sale_type`. `fromLLSD` accepts either
form, and integers are what this tree already sends over FetchInventoryDescendents2
(`Source/OpenSim.Capabilities/LLSDInventoryItem.cs`). The `permissions` and `sale_info` inner key sets come from
the same file. Golden fixtures under `Tests/OpenSim.Region.ClientStack.LindenCaps.AIS.Tests/AIS/Fixtures` pin
the result.

### The categories-create map

`LLInventoryCategory::asAISCreateCatLLSD` (`indra/llinventory/llinventory.cpp:1256-1276`) is what
`llinventorymodel.cpp:1040` puts in `new_inventory["categories"]`. It emits, in order:

| Key | Line | Value |
|---|---|---|
| `category_id` | `:1259` | `mUUID`, **null on a create**: the viewer constructs the category with `LLUUID::null` (`llinventorymodel.cpp:1038`), so the server assigns the id |
| `parent_id` | `:1260` | `mParentUUID`, the same folder the POST is addressed to |
| `type_default` | `:1261-1262` | `mPreferredType` cast to a signed 8-bit integer: an **integer** folder type |
| `name` | `:1263` | |
| `thumbnail` | `:1265-1268` | `{ asset_id: mThumbnailUUID }`, **only when non-null** |
| `favorite` | `:1270-1273` | `{ toggled: mFavorite }`, **only when true** |

Nothing else. The server honours the body's `parent_id` when it names a folder. `thumbnail` and `favorite` are
accepted and dropped, because `InventoryFolderBase` has no column for either.

### The link map the viewer builds for a slam

`LLAppearanceMgr` builds a SlamFolder body as an **LLSD array** of link maps. Each map carries exactly `name`,
`desc`, `linked_id` and `type` (`AT_LINK`, or `AT_LINK_FOLDER` for the base-outfit link)
(`indra/newview/llappearancemgr.cpp:2209-2245`). That is the shape `PUT /category/{id}/links` accepts.

## 1d-bis. The delta contract: what the viewer applies from a mutation response

Taken from `AISUpdate::parseMeta` / `parseContent` / `parseItem` / `parseCategory` / `doUpdate` in
`llaisapi.cpp`, and from `LLInventoryModel::onObjectDeletedFromServer` in `llinventorymodel.cpp`.

### The complete set of delta keys

Read by `parseMeta` (`:1101-1177`) and nothing else:

| Key | LLSD | Line | What the viewer does |
|---|---|---|---|
| `_categories_removed` | array of uuid | `:1104-1119` | for each id **it already has**: parent descendent delta −1, id queued for deletion |
| `_category_items_removed` | array of uuid | `:1122-1139` | same, for items; merged into the same id set as the next row |
| `_removed_items` | array of uuid | `:1124` | parsed into the *same* list as `_category_items_removed`; the two are interchangeable |
| `_broken_links_removed` | array of uuid | `:1141-1156` | same handling again |
| `_created_items` | array of uuid | `:1159` | the ids the viewer will accept from `_embedded` on a mutation; drives per-id callbacks for CreateInventory |
| `_created_categories` | array of uuid | `:1162` | same for categories |
| `_updated_category_versions` | map uuid → integer | `:1164-1176` | the folder versions the viewer will adopt, **and the gate on all descendent accounting** |

### Updated objects are content, not a delta key

There is no "updated" delta key. An updated item or category arrives as **top-level content**. `parseContent`
(`:1179-1212`) routes a body with `item_id` + `parent_id` to `parseItem`, and one with `category_id` +
`parent_id` to `parseCategory`. On a **mutation** response (`!mFetch`):

- `parseItem` (`:1215-1258`), when the viewer already has the item: it copies the item's current values first
  (`copyViewerItem`, `:1222`, "Default to current values where not provided"). It then applies the map and
  files the item under `mItemsUpdated`, **plus a zero delta for the parent** (`:1241-1245`).
- `parseItem`, when the viewer does **not** have the item: the body is treated as a creation, filed under
  `mItemsCreated` with a parent delta of **+1** (`:1247-1252`).
- `parseCategory` (`:1327-1465`) does the same, filing under `mCategoriesUpdated` with zero deltas for **both**
  the parent and the category itself (`:1419-1428`).

This has two consequences for the server:
- A PATCH response may be sparse: only the changed fields plus `item_id` / `category_id` and `parent_id`. The
  viewer merges it onto its own copy.
- The updated object must be **top level**. On a mutation the viewer ignores any `_embedded` object whose id is
  not in `_created_items` / `_created_categories` (§1c), so an updated object hidden in `_embedded` is silently
  dropped.

### `_updated_category_versions` gates everything

`doUpdate` (`:1606-1648`) walks the accumulated descendent deltas and **skips any category not listed in**
`_updated_category_versions` ("Skipping version increment for non-updated category", `:1625-1629`). A folder
whose contents changed but which the response does not list keeps a stale descendent count and version
forever. Newly created categories are skipped too, on purpose (`:1618-1622`).

At the end of the update (`:1755-1791`), each listed category's local version is **set to the server's value**
(`:1776`; the source comment reads "the AIS version should be considered the true version"). A listed version of
−1 (`VERSION_UNKNOWN`) instead triggers a re-fetch with a 360 s expiry (`:1779-1789`).

> **Hazard.** That loop calls `cat->getVersion()` with **no null check** on `gInventory.getCategory(id)`
> (`:1760-1762`). Listing a folder the viewer has never fetched is a null dereference in the viewer. Only list
> folders the operation touched.

### Per operation: what to send

| Operation | Content | Delta keys | `_updated_category_versions` must list |
|---|---|---|---|
| `PATCH /item/{id}` | the item, top level (`item_id`, `parent_id`, changed fields) | none | the item's parent folder; the zero-delta entry `parseItem` creates is discarded without it |
| `PATCH /category/{id}` | the category, top level (`category_id`, `parent_id`, changed fields) | none | the category **and** its parent; `parseCategory` creates zero-delta entries for both |
| `DELETE /item/{id}` | none | `_removed_items` (or `_category_items_removed`) with the item id | the item's parent |
| `DELETE /category/{id}` | none | `_categories_removed` with the folder id **only** | the folder's parent |

**Descendents of a deleted folder are implied, not listed.** For a category,
`LLInventoryModel::onObjectDeletedFromServer` (`llinventorymodel.cpp:2015-2041`) calls
`onDescendentsPurgedFromServer` first ("For category, need to delete/update all children first"). So naming
the folder is enough: its children are purged locally. Listing them as well is harmless but pointless. Listing
them **instead** of the folder would leave the folder behind.

### Two edge rules

- **A delta naming an object the viewer does not have is dropped**, with a warning. Every removal arm is inside
  `if (cat)` / `if (item)` (`:1109`, `:1130`, `:1148`), so there is no descendent delta and no deletion.
  Sending a removal for something the viewer never knew is therefore safe and does nothing.
- **An absent delta key and an empty one are identical.** `parseUUIDArray` (`:1077-1088`) does nothing when the
  key is absent, and nothing when the array is empty. `_updated_category_versions` is guarded by `update.has`
  (`:1165`). Emitting empty arrays is neither required nor harmful.

### Update bodies

`UpdateItem`'s body is the item's **full** `asLLSD()` with `asset_id` and `shadow_id` removed, and replaced by
`hash_id` (the transaction id) when one is set. `LLViewerInventoryItem::updateServer`
(`llviewerinventory.cpp:435-454`) and `update_inventory_item` (`:1399-1422`) build it identically. The server
therefore receives the whole item map of §1d minus the asset id, and must ignore what it does not accept rather
than fail.

`UpdateCategory`'s body is the category's full `asLLSD()` for a rename (`LLViewerInventoryCategory::updateServer`,
`:651-665`) and for a type change (`changeType`, `:866-884`). For a **protected** folder type, the viewer refuses
to send anything except a single-key `{thumbnail}` or `{favorite}` map (`update_inventory_category`,
`:1436-1457`). Those are the only fields a protected system folder will ever be asked to change.

### 1d-ter. `UpdateItem`'s field set, and what the server does with each

`LLInventoryItem::asLLSD` (`llinventory.cpp:936-981`) is the whole body, minus the fields `updateServer` erases.

| Key | Source | Server |
|---|---|---|
| `asset_id` | `:952-955` (unrestricted perms, or a null asset) | **applied** |
| `shadow_id` | `:956-963` (restricted perms; the asset XORed with `MAGIC_ID`) | **never arrives**: both update-body builders erase it (`llviewerinventory.cpp:445-452`, `:1414-1421`) |
| `hash_id` | not from `asLLSD`; put in place of the two above when the transaction id is set | **applied**, by handing the transaction to the region's asset-transaction module, the only thing that knows which asset the upload produced |
| `permissions` | `:939` `ll_fill_sd_from_permissions` (`llpermissions.cpp:1082-1094`) | `next_owner_mask`, `everyone_mask`, `group_mask` **applied**, each masked by the item's own base; `base_mask` / `owner_mask` and the id fields ignored |
| `name`, `desc` | `:979-980` | **applied** |
| `flags` | `:976` | **applied** |
| `sale_info` | `:977` (`llsaleinfo.cpp:97-107`: `sale_type`, `sale_price`) | **applied** |
| `parent_id` | `:938` | **ignored**: a move changes two folders' versions and is not this route's job |
| `type`, `inv_type` | `:966-975` | **ignored**: invariants; `XInventoryService.UpdateItem` refuses to change them anyway |
| `created_at` | `:981` | **ignored** |
| `item_id` | `:937` | **ignored**: it is the URL |
| `thumbnail`, `favorite` | `:942-951` | **ignored**: no column in this tree |

If the asset-transaction module refuses the save, the PATCH answers 403 and lists no
`_updated_category_versions`. The viewer's folder version then does not advance, and its next fetch sees the
true state.

**Why applying the change matters.** The data layer bumps the parent folder's version on every item store. A
PATCH that changes nothing writes nothing, so no version moves and the viewer never re-reads the item. An asset
change that is silently dropped therefore stays invisible until the next login.

## 1e-bis. GET /orphans scope

`/orphans` reports **folder orphans only**: folders whose `ParentID` names a folder absent from the agent's
inventory. `IInventoryService` has no query for orphaned items. Finding them would mean listing the contents of
every folder, so items are never reported. **An empty response means "no orphan folders", not "no orphans of
any kind".**

## 1c-bis. The depth contract

### (a) Every URL the viewer builds with a depth

There are only two call sites, both in `llinventorymodelbackgroundfetch.cpp`. Both pass a literal `0` for the
depth argument and vary the *recursive* flag:

| Call | Line | Arguments |
|---|---|---|
| `AISAPI::FetchCategorySubset(cat_id, children, item_type, true, cb, 0)` | `:937` | recursive **true**, depth 0 |
| `AISAPI::FetchCategoryChildren(cat_id, item_type, type == FT_RECURSIVE, cb, 0)` | `:994` | recursive from the queue entry, depth 0 |

The depth that reaches the URL is computed in `llaisapi.cpp`: `MAX_FOLDER_DEPTH_REQUEST` when recursive, else
`llmin(depth, MAX_FOLDER_DEPTH_REQUEST)` (`:463-474` for children, `:517-527` for the string-identifier form,
`:630-637` for the subset), with `MAX_FOLDER_DEPTH_REQUEST = 50` (`:58`). **So the viewer only ever sends
`depth=50` (recursive) or `depth=0` (not recursive).**

### (b) What the viewer does with the response

1. **It re-queues descendants regardless.** `onAISContentCalback`
   (`llinventorymodelbackgroundfetch.cpp:579-625`) walks the direct child categories of every folder in the
   response and pushes each back on `mFetchFolderQueue` as `FT_RECURSIVE`. The source comment (`:610`) gives the
   reason: "push descendant back to verify they are fetched fully (ex: didn't encounter depth limit)". The
   viewer never assumes the server honoured the depth.
2. **"Fetched" means "has a version".** The queue drain skips a child category when
   `VERSION_UNKNOWN != child_cat->getVersion()` (`:894-898`, again at `:948-953`). Only unversioned children go
   into a subset request.

A folder only gets a version when the response let the viewer count its descendents. `parseCategory` sets the
version only when both of these hold (`llaisapi.cpp:1380-1407`):
- `mCatDescendentsKnown.find(category_id) != end`, which is filled only for a category whose `_embedded` carries
  all three collections, or `links` alone for a Current Outfit / Outfit folder (`:1466-1482`);
- `depth >= 0`.

The source comment there reads: "set version only if we are sure this update has full data and embeded items
since viewer uses version to decide if folder and content still need fetching".

### (c) The rule the server follows

The depth decrements as the parse descends. `parseContent` parses the top-level category at `mFetchDepth`
(`:1205`), which is the `depth` the viewer kept locally, defaulting to 50 (`:1036-1040`). `parseCategory` then
parses its `_embedded` at `depth - 1` (`:1461-1464`). Together with the `depth >= 0` gate:

> **The rule.** `depth=N` lets the server expand up to **N generations below the requested folder**. The
> requested folder is parsed at N, its children at N−1, and so on. A category that arrives deeper than N is
> parsed at a negative depth and **never gets a version**, so expanding further than N is wasted work. Expanding
> **fewer** generations than N is always safe: the unversioned folders are queued and fetched on the next round,
> at the cost of a round trip.

Two corollaries:

- Every category the server **expands** carries all three `_embedded` collections *and* a `version`. Otherwise
  the viewer cannot count its descendents, will not version it, and will re-request it forever.
- Every category the server **does not** expand is a stub with no `_embedded`. Sending `version` on a stub is
  harmless: the version gate sits inside the descendents-known branch, so an unexpanded category stays
  `VERSION_UNKNOWN` whatever version comes with it. That is the "come back for this one" signal.

At `depth=0` this produces exactly what the viewer intends:
- `mFetchDepth = 0`, so the requested folder is versioned;
- its `_embedded` is parsed at −1, so no child is versioned;
- every child goes back on the queue.

### (d) Limits

| Limit | Value | Source |
|---|---|---|
| Maximum depth requested | 50 | `MAX_FOLDER_DEPTH_REQUEST`, `llaisapi.cpp:58`; every depth is clamped to it |
| Children per subset request | `BatchSizeAIS3`, default **20**, clamped to [1, 40] | `llinventorymodelbackgroundfetch.cpp:883-885` |
| More children than the batch | parent re-queued as `FT_CONTENT_RECURSIVE` to collect the rest | `:909-913`, `:955-960` |
| URL length | warns above **2000** characters and **still sends** | `llaisapi.cpp:651-654` |
| Marketplace listings | never fetched by this path | `:900-904` |

### Conformance

`AisInventory.Walk(backend, agent, root, depth)` expands the requested folder plus `depth` further generations,
breadth first. `AisHandler.FetchChildren` emits every walked folder with all three collections, and every
unwalked child as a stub. That is the rule above with no off-by-one: the deepest expanded generation is parsed
by the viewer at exactly 0, the last depth that still versions. The handler also clamps a requested depth to 50,
the viewer's own ceiling. A client asking for more therefore cannot make the region walk further than the viewer
would ever use.

## 1d-ter. Protected folders, from the viewer's own table

`LLFolderType::lookupIsProtectedType` looks the type up in `LLFolderDictionary` and returns that entry's
PROTECTED flag. It **returns `true` for any type the table does not contain**
(`indra/llinventory/llfoldertype.cpp:154-162`). The table is at `:85-127`. The honest way to express it is
therefore an allow-list of the unprotected types with a protected default, and that is what the handler
implements.

**Unprotected** (PROTECTED = `false` in the table):

| Viewer type | Line | This tree |
|---|---|---|
| `FT_NONE` | `:126` | `FolderType.None` (−1), an ordinary user folder |
| `FT_ENSEMBLE_START`..`FT_ENSEMBLE_END` | `:106-109` | no member; handled as the numeric range 26–45. The viewer's own comment says "Not used" |
| `FT_OUTFIT` | `:112` | `FolderType.Outfit`, a saved outfit |
| `FT_MARKETPLACE_LISTINGS` | `:122` | `FolderType.MarketplaceListings` |
| `FT_MARKETPLACE_STOCK` | `:123` | `FolderType.MarkplaceStock` (the spelling is this tree's) |
| `FT_MARKETPLACE_VERSION` | `:124` | **no equivalent**; falls through to the protected default, see below |

**Protected:** everything else in the table, **and every type the table omits**. The listed ones are
`FT_TEXTURE`, `FT_SOUND`, `FT_CALLINGCARD`, `FT_LANDMARK`, `FT_CLOTHING`, `FT_OBJECT`, `FT_NOTECARD`,
`FT_ROOT_INVENTORY`, `FT_LSL_TEXT`, `FT_BODYPART`, `FT_TRASH`, `FT_SNAPSHOT_CATEGORY`, `FT_LOST_AND_FOUND`,
`FT_ANIMATION`, `FT_GESTURE`, `FT_FAVORITE`, `FT_CURRENT_OUTFIT`, `FT_MY_OUTFITS`, `FT_MESH`, `FT_INBOX`,
`FT_OUTBOX`, `FT_BASIC_ROOT`, `FT_SETTINGS` and `FT_MATERIAL` (`:87-102`, `:111`, `:113-121`, `:125`).

**Where the two trees differ:**

- `FT_MARKETPLACE_VERSION` (55) is unprotected in the viewer, but this tree has no `FolderType` member for it,
  so it takes the protected default. Nothing in OpenSim creates it, so the practical effect is nil.
- `FolderType.Suitcase` (100) exists only in this tree. The viewer's table has no entry for it, so
  `lookupIsProtectedType` would return `true`, and so does the server rule. That is the right answer for the
  Hypergrid suitcase folder anyway.
- The ensemble range exists in the viewer only as a numeric span with no member here, so the server matches it
  numerically.

The inventory root is refused both by type (`FT_ROOT_INVENTORY`) and structurally (a folder with no parent).

## 1e. Version semantics

- Folder versions arrive in two places: `version` on a category map (fetch and mutation responses), and
  `_updated_category_versions` (mutation responses).
- **Fetch.** `parseCategory` sets the local version from `version` only when the descendent count is known from
  `_embedded` (§1c) and `depth >= 0` (`:1389-1407`).
  - It refuses a category whose `version` is lower than the one it already holds ("Got stale folder",
    `:1338-1348`).
  - It logs a stale known folder when the server's version is higher (`:1409-1416`, "Version was" `:1396`).
  - A newly created category gets its version only with a known descendent count (`:1434-1447`).
- **Mutation.** Descendent deltas (±1 per created or removed child, `:1112`, `:1131`, `:1149`, `:1250`, `:1310`,
  `:1450`) apply only to categories listed in `_updated_category_versions` (`doUpdate`, `:1606-1650`). Afterwards
  each listed category's local version is **set to the server's value** (`:1757-1795`, set at `:1776`). A listed
  version of −1 instead triggers a re-fetch with a 360 s expiry (`:1771-1790`).
- **Consequence for the server.** Every mutation must list in `_updated_category_versions`, with the
  post-operation version, every folder whose contents it changed:
  - the parent of a created item, link or category;
  - the old and new parent of a move;
  - the parent of a removed object;
  - the slammed folder.

  A folder that changed but is not listed leaves the viewer's descendent count and version stale.
- **Folders each operation bumps,** derived from the viewer's accounting above:

  | Operation | Folders listed |
  |---|---|
  | CreateInventory | the parent |
  | SlamFolder | the slammed folder |
  | RemoveItem, RemoveCategory | the removed object's parent |
  | PurgeDescendents | the purged folder |
  | UpdateItem, UpdateCategory | the parent when the update moves the object; otherwise the object's own folder with delta 0 (`:1245`, `:1298`, `:1427`) |
  | CopyLibraryCategory | the destination |

  On the server, the data layer's own increment sets the version: it fires on item store, delete and move, and
  on folder store. The handler therefore re-reads the folder after a write rather than computing the version.

## 1f. HTTP status handling (`InvokeAISCommandCoro`, `:851-1011`)

| Condition | Viewer behaviour |
|---|---|
| response body not an LLSD map | status forced to 500 "Malformed response contents" (`:882-885`); warn; the non-map result still goes to `onUpdateReceived`, which finds nothing to do |
| 410 Gone, `REMOVECATEGORY` | warn; `fetchDescendentsOf(parent)`; the local folder is **not** deleted (`:886-903`) |
| 410 Gone, `REMOVEITEM` | warn; `fetchDescendentsOf(parent)`; local item deleted via `onObjectDeletedFromServer` (`:904-918`) |
| 403 Forbidden, `FETCHCATEGORYCHILDREN` with `depth == 0` | notification `InventoryLimitReachedAISAlert` (first time) / `InventoryLimitReachedAIS`; warn "content is over limit" (`:920-935`) |
| 403 Forbidden, `FETCHCATEGORYCHILDREN` with `depth > 0` | debug only: "recoverable by requesting with lower depth" (`:936-940`); the caller is expected to retry with a smaller depth (retry logic **UNVERIFIED**) |
| any other failure (4xx/5xx, timeout) | warn with status and pretty-printed body (`:942-943`); no retry in `llaisapi.cpp` (transport-level retries in `llcorehttputil` **UNVERIFIED**) |
| always, success or failure | `onUpdateReceived(result, type, body)` (`:946`) |

The last row matters most: the body **is parsed as an update even on error**. An error body must therefore be a
map, and must not carry `item_id` / `category_id` + `parent_id` pairs it does not mean. The completion callback
then fires at least once (`:953-1011`), with a null id unless the body carries the ids of §1c.

What an error body should look like: an LLSD map. Nothing in the files read uses `error_code`,
`error_description` or `message`. They are conventional and safe because the viewer ignores them; the server
returns them for logs.

## 1g. The `isAvailable()` gate and its consequence

`AISAPI::isAvailable()` (`:62-68`) is exactly `gAgent.getRegion()->isCapabilityAvailable("InventoryAPIv3")`. It
is true as soon as the current region's seed-cap response contains a URL for `InventoryAPIv3`. Nothing else is
checked: no version, no probe. The viewer requests that cap name on every seed (`:72-76`).

Once it is true, these paths go through AIS:

| Path | When AIS is available | When it is not | Source |
|---|---|---|---|
| delete an item | `AISAPI::RemoveItem` | warns "Tried to use inventory without AIS API" and does **nothing** | `llviewerinventory.cpp:1497-1509` |
| delete a category | `AISAPI::RemoveCategory` (no `isAvailable` check at all) | request fails at the cap lookup, callback null | `:1545-1568` |
| purge a folder's descendents | `AISAPI::PurgeDescendents` | warns, does nothing | `:1630-1645` |
| slam a folder's links (outfit changes) | `AISAPI::SlamFolder` (no check) | fails at cap lookup | `:1776-1784` |
| create a category | `AISAPI::CreateInventory` | falls back to the legacy path below the `if` (`:1034`) | `llinventorymodel.cpp:1034-1042` |
| fetch a category's descendents | `AISAPI::FetchCategoryChildren` (seen at `llviewerinventory.cpp:694,727`) | legacy cap | **UNVERIFIED** detail |
| background inventory fetch, item fetch, COF fetch, links, orphans, library copy | the remaining `AISAPI::Fetch*` / `CopyLibraryCategory` callers | | **UNVERIFIED** |

The consequence that matters: advertising `InventoryAPIv3` in the seed cap switches every path above at once. A
partial implementation returns errors for the paths it lacks, and for delete, purge and slam the LL viewer has
no other way to do them. A partial AIS is worse than none.

## 2. Code map: how the cap reaches the viewer

- **Seed caps.** `BunchOfCaps.SeedCapRequest` (`Source/OpenSim.Region.ClientStack.LindenCaps/BunchOfCaps/BunchOfCaps.cs`)
  adds every cap name the viewer requests and emits a URL only for names with a registered handler
  (`Source/OpenSim.Capabilities/CapsHandlers.cs`). For a new cap to reach the viewer, it must be registered on
  the agent's `Caps` under the exact name, from `OnRegisterCaps`, and the viewer must request it. The viewer
  requests `InventoryAPIv3` and `LibraryAPIv3` itself (§1g), so registration is the only region-side step.
- **Variable-path handlers.** AIS answers on paths below its cap URL (`<capurl>/item/{id}` and so on). It must
  therefore be registered as a variable-path handler (`AISv3Module.VarPath`). Registered the default way, every
  AIS request answers 404 while the seed response still looks correct.
- **Current Outfit folder.** Resolved with `IInventoryService.GetFolderForType(userID, FolderType.CurrentOutfit)`.
- **Links.** `IInventoryService` has no link-aware fetch: links are items with `AssetType.Link`. The AIS backend
  resolves link targets itself (one `GetFolderContent` plus one `GetMultipleItems` per folder, as
  `FetchInvDescHandler` does) and splits links out of `Items` into `_embedded.links`.
- **Folder versions** are read back from the inventory service on every call. Nothing caches them region-side.
