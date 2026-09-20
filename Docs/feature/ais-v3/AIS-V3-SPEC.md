# AIS v3 — the surface the LL viewer drives

**Authority:** the Linden Lab viewer source, version 26.1.1. Every row below cites
the file and line it was read from. Files read, read-only: `indra/newview/llaisapi.h` (167 lines),
`indra/newview/llaisapi.cpp` (1798 lines), the AIS call site in `indra/newview/llinventorymodel.cpp`
(`:1025-1058`), and the AIS call sites `remove_inventory_item`, `remove_inventory_category`,
`purge_descendents_of`, `slam_inventory_folder` in `indra/newview/llviewerinventory.cpp`. Anything that could
not be pinned to a line in those files is marked **UNVERIFIED** and says which file would settle it.

This document is the contract every A-session implements. Later sessions do not open the viewer tree.

Cap names: `InventoryAPIv3` (`llaisapi.cpp:48`) and `LibraryAPIv3` (`:49`). The viewer asks the seed cap for
both (`AISAPI::getCapNames`, `:72-76`). HTTP timeout per request 180 s (`:50`). Maximum requested folder depth
50 (`MAX_FOLDER_DEPTH_REQUEST`, `:58`).

## 1a. Operations

`{inv}` = the InventoryAPIv3 cap URL, `{lib}` = the LibraryAPIv3 cap URL. `tid` is a fresh random UUID per call
(`LLUUID tid; tid.generate();`). Bodies are **LLSD XML** (A-Q2, resolved A1): every request body is serialised with `LLSDSerialize::toXML`
(`indra/llmessage/llcorehttputil.cpp:144` POST, `:169` PUT, `:193` PATCH) and every response parsed with
`LLSDSerialize::fromXML` (`:123`, `responseToLLSD`). `HttpCoroutineAdapter::checkDefaultHeaders` (`:1211-1229`)
sets both `Content-Type` and `Accept` to `HTTP_CONTENT_LLSD_XML` on every AIS request unless the caller
already set them; the literal is `application/llsd+xml` per the comments at `:478` and `:497`. The response
parse itself does **not** check the content type — it only gates a warning (`:495-500`) — so a response is
read as LLSD XML whatever it is labelled. (The file is at `indra/llmessage/`, not `indra/llcorehttp/`.)

| # | Operation (`llaisapi.h`) | Verb | URL relative to the cap | Query | Body | Headers | Source |
|---|---|---|---|---|---|---|---|
| 1 | `CreateInventory(parentId, newInventory)` | POST | `{inv}/category/{parentId}` | `tid={uuid}` | `newInventory` map: `categories` (array of category maps) verified at `llinventorymodel.cpp:1035-1042`; `items` / `links` arrays **UNVERIFIED** (built in `llviewerinventory.cpp:1156,1370`, outside the permitted functions) | none | `llaisapi.cpp:99-143`, url `:115` |
| 2 | `SlamFolder(folderId, newInventory)` | PUT | `{inv}/category/{folderId}/links` | `tid={uuid}` | `contents` as passed by the caller (`slam_inventory_folder`, `llviewerinventory.cpp:1776-1784`); shape **UNVERIFIED** (built by `LLAppearanceMgr`, not permitted): expected `{ "links": [ link maps ] }` | none | `llaisapi.cpp:145-180`, url `:161` |
| 3 | `RemoveCategory(categoryId)` | DELETE | `{inv}/category/{categoryId}` | — | none | none | `:182-217`, url `:197` |
| 4 | `RemoveItem(itemId)` | DELETE | `{inv}/item/{itemId}` | — | none | none | `:219-252`, url `:234` |
| 5 | `CopyLibraryCategory(sourceId, destId, copySubfolders)` | COPY | `{lib}/category/{sourceId}` | `tid={uuid}` and, when `!copySubfolders`, the literal suffix `,depth=0` **appended to the tid value with a comma** (`url += ",depth=0"`, `:278`), i.e. `?tid=<uuid>,depth=0` | none | destination = `destId.asString()` (`:282`) passed as the `copyAndSuspend` destination argument (`:294`), which appends it as the HTTP **`Destination`** header: `headers->append(HTTP_OUT_HEADER_DESTINATION, dest)` (`llcorehttputil.cpp:1135`) — A-Q2, resolved A1 | `:255-301`, url `:275` |
| 6 | `PurgeDescendents(categoryId)` | DELETE | `{inv}/category/{categoryId}/children` | — | none | none | `:303-339`, url `:318` |
| 7 | `UpdateCategory(categoryId, updates)` | PATCH | `{inv}/category/{categoryId}` | — | `updates` map of category fields (callers at `llviewerinventory.cpp:663,881,1455`, outside the permitted functions: field set **UNVERIFIED**) | none | `:341-374`, url `:355` |
| 8 | `UpdateItem(itemId, updates)` | PATCH | `{inv}/item/{itemId}` | — | the item's full `asLLSD()` with `asset_id`/`shadow_id` replaced by `hash_id` (callers `:454,1422,1434`; field set **verified**, see §1d-ter) | none | `:376-409`, url `:391` |
| 9 | `FetchItem(itemId, type)` | GET | `{inv|lib}/item/{itemId}` (`lib` when `type == LIBRARY`) | — | none | none | `:412-445`, url `:426` |
| 10 | `FetchCategoryChildren(catId, type, recursive, depth)` | GET | `{inv|lib}/category/{catId}/children` | `depth=N` where N = 50 if `recursive`, else `min(depth, 50)` (`:463-474`) | none (the viewer keeps `{"depth": N}` locally as `request_body` for error handling, `:490`) | none | `:447-498` |
| 11 | `FetchCategoryChildren(identifier, recursive, depth)` | GET | `{inv}/category/{identifier}/children` — `identifier` is any string, e.g. an alias | `depth=N` as above (`:527`) | none | none | `:500-549`, url `:514` |
| 12 | `FetchCategoryCategories(catId, type, recursive, depth)` | GET | `{inv|lib}/category/{catId}/categories` | `depth=N` (`:578`) | none | none | `:551-599`, url `:565` |
| 13 | `FetchCategorySubset(catId, specificChildren, type, recursive, depth)` | GET | `{inv|lib}/category/{catId}/children` | `depth=N&children={id1},{id2},...` (`:642-648`); the viewer warns above 2000 URL characters (`:651`) but still sends | none | none | `:601-678`, url `:628` |
| 14 | `FetchCOF()` | GET | `{inv}/category/current/links` | — (local `depth` 0, `:709`, not on the URL) | none | none | `:680-714`, url `:692` |
| 15 | `FetchCategoryLinks(catId)` | GET | `{inv}/category/{catId}/links` | — (local depth 0, `:745`) | none | none | `:716-751`, url `:728` |
| 16 | `FetchOrphans()` | GET | `{inv}/orphans` | — | none | none | `:753-784`, url `:765` |

Every operation first resolves the cap (`getInvCap()` / `getLibCap()`, `:79-97`); with no cap the callback fires
with a null id and nothing is sent. Requests are coroutines throttled to 2048 in flight (`:54`, `:786-834`).

The `simulate` query parameter mentioned in the session brief does **not** appear anywhere in `llaisapi.cpp`;
the viewer never sends it. **UNVERIFIED** whether any other viewer code path adds it.

## 1b. Aliases

- `current` — the Current Outfit folder, used as `{inv}/category/current/links` by `FetchCOF` (`:692`). This is
  the only alias literal in `llaisapi.cpp`. The string-identifier overload of `FetchCategoryChildren` (`:500-549`)
  accepts any identifier, so `current/children` is a legal request shape; which callers use it is **UNVERIFIED**
  (callers are outside the permitted files).
- No other alias appears in the permitted files.

## 1c. Response envelope

Every response, success or error, goes through `AISUpdate` (`onUpdateReceived`, `:836-849` → `AISUpdate::doUpdate`).
`parseUpdate` = `parseMeta` then `parseContent` (`:1094-1099`). The viewer distinguishes **fetch** commands
(`FETCHITEM, FETCHCATEGORYCHILDREN, FETCHCATEGORYCATEGORIES, FETCHCATEGORYSUBSET, FETCHCOF, FETCHCATEGORYLINKS,
FETCHORPHANS`, `:1028-1034`) from **mutations**; the difference matters for filtering below.

### Meta keys (`parseMeta`, `:1101-1177`) — all top-level in the response map

| Key | LLSD type | What the viewer does |
|---|---|---|
| `_categories_removed` | array of uuid | each known category: parent's descendent delta −1, id queued for deletion (`:1105-1120`) |
| `_category_items_removed` | array of uuid | each known item: parent delta −1, queued for deletion (`:1123-1140`) |
| `_removed_items` | array of uuid | same handling as `_category_items_removed` (`:1124`) |
| `_broken_links_removed` | array of uuid | same handling (`:1142-1157`) |
| `_created_items` | array of uuid | the set of item/link ids the viewer will accept from `_embedded` on a mutation (`:1159`); also drives per-id callbacks for `CREATEINVENTORY` (`:995-1004`) |
| `_created_categories` | array of uuid | the set of category ids accepted from `_embedded` on a mutation (`:1162`); per-id callbacks for `CREATEINVENTORY` (`:984-993`) |
| `_updated_category_versions` | map uuid → integer | the authoritative folder versions after the operation (`:1165-1176`); see §1e |

Keys the session brief named that this viewer does **not** read: `_updated_items`, `_updated_categories`,
`_removed_categories` — there is no reference to them in `llaisapi.cpp` (the removal key is
`_categories_removed`). Emitting them is harmless; relying on them is wrong.

### Content keys (`parseContent`, `:1179-1214`) — top-level

| Condition | Handling |
|---|---|
| `linked_id` **and** `parent_id` present | the response itself is a link: `parseLink` (`:1185-1188`) |
| else `item_id` **and** `parent_id` | the response is an item: `parseItem` (`:1189-1192`) |
| `FETCHCATEGORYSUBSET` | the top-level category is ignored (incomplete); `_embedded` parsed at `depth-1` (`:1194-1202`) |
| else `category_id` **and** `parent_id` | the response is a category: `parseCategory` (`:1203-1206`) |
| else | `_embedded` parsed if present (`:1207-1213`) |

Callback ids (`InvokeAISCommandCoro`, `:953-1011`): fetch-category commands and `COPYLIBRARYCATEGORY` return
`category_id`; `FETCHITEM` returns `item_id`, overridden by `linked_id` if present ("Error message might contain an
item_id", `:972-980`); `CREATEINVENTORY` fires once per `_created_categories` / `_created_items` entry.

### `_embedded` (`parseEmbedded`, `:1484-1508`) — a map with up to five keys

| Key | LLSD type | Where it appears | Handling |
|---|---|---|---|
| `categories` | map: category uuid string → category map | inside a category | `parseEmbeddedCategories` (`:1586-1604`): each parsed at `depth` |
| `items` | map: item uuid string → item map | inside a category | `parseEmbeddedItems` (`:1554-1572`) |
| `links` | map: link item uuid string → link map | inside a category | `parseEmbeddedLinks` (`:1523-1540`): each `parseLink` at `depth` |
| `item` | single item map | inside a link | `parseEmbeddedItem` (`:1542-1552`) |
| `category` | single category map | inside a link | `parseEmbeddedCategory` (`:1574-1584`) |

**Links are a separate collection, not items.** A folder's `_embedded` carries `items` and `links` as sibling maps;
a link's own `_embedded` may carry the linked `item` or `category`. On a mutation (non-fetch) response the viewer
ignores any embedded item/link/category whose id is not listed in `_created_items` / `_created_categories`
(`:1531-1534`, `:1547`, `:1562-1565`, `:1579`, `:1594-1597`); on a fetch it accepts everything.

Descendent count (`parseDescendentCount`, `:1466-1482`): known only when `_embedded` has **all three** of
`categories`, `links`, `items` (sum of their sizes), or, on a fetch of a `FT_CURRENT_OUTFIT` / `FT_OUTFIT` folder,
when it has `links` alone (links-only folders). A folder returned without all three collections gets no
descendent count and therefore no version (see §1e) — the viewer will keep re-fetching it.

## 1d. Item, link and category maps as the viewer reads them

Verified in the permitted files:

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

### A-Q1 resolved (A1): the field set `fromLLSD` reads

`llaisapi.cpp` hands each object map to `LLViewerInventoryItem::unpackMessage(const LLSD&)` /
`LLViewerInventoryCategory::unpackMessage(const LLSD&)` (`:1223`, `:1268`, `:1368`). There is **no**
`unpackMessage(const LLSD&)` in `indra/llinventory/llinventory.cpp`; the LLSD readers there are
`LLInventoryItem::fromLLSD` (`:984-1183`) and `LLInventoryCategory::fromLLSD` (`:1289-1352`). That the viewer
subclasses' `unpackMessage(const LLSD&)` delegate to these is **UNVERIFIED** — `llviewerinventory.cpp` is not a
permitted read — but they are the only LLSD readers for these types in the permitted file, and the label
constants below are theirs.

**Item** (`fromLLSD`, label constants at `:45-63`). Any key not listed is ignored by the loop:

| Key | Type | Line | Notes |
|---|---|---|---|
| `item_id` | uuid | `:1004` | |
| `parent_id` | uuid | `:1010` | |
| `thumbnail` | map with `asset_id` | `:1016-1035` | or `thumbnail_id` (uuid) at `:1037` |
| `favorite` | map with `toggled` (bool) | `:1043-1051` | |
| `permissions` | map | `:1054` | inner keys read by `LLPermissions::importLLSD`, **UNVERIFIED** (`llpermissions.cpp` not permitted) |
| `sale_info` | map | `:1060` | inner keys read by `LLSaleInfo::fromLLSD`, **UNVERIFIED** |
| `shadow_id` | uuid | `:1087` | XOR-obfuscated asset id; an alternative to `asset_id` |
| `asset_id` | uuid | `:1094` | |
| `linked_id` | uuid | `:1100` | read **into the asset id**; its presence is also what selects `parseLink` (§1c) |
| `type` | string **or** integer | `:1106-1120` | asset type; `LLAssetType::lookup` for a string |
| `inv_type` | string **or** integer | `:1122-1135` | inventory type |
| `flags` | integer or binary | `:1137-1148` | |
| `name` | string | `:1150` | non-standard ASCII and `|` replaced with spaces |
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

It reads neither `version` nor a descendent count — `llaisapi.cpp` reads those itself (§1e). Note `cat_id`
(`INV_FOLDER_ID_LABEL`, `:46`) is **not** read by `fromLLSD`; the category id key is `category_id`.

**What the server emits (A1 decision).** Integers for `type`, `inv_type` and `sale_type`, since `fromLLSD`
accepts either and integers are what this tree already sends over FetchInventoryDescendents2
(`Source/OpenSim.Capabilities/LLSDInventoryItem.cs:33-68`) and the LL viewer already accepts. The `permissions`
and `sale_info` inner key sets are taken from that same file for the same reason, their readers being
unverifiable this session. Golden fixtures under
`Tests/OpenSim.Region.ClientStack.LindenCaps.AIS.Tests/AIS/Fixtures` pin the result.

### A-Q3 closed (A5): the categories-create map

`LLInventoryCategory::asAISCreateCatLLSD` (`indra/llinventory/llinventory.cpp:1256-1276`) is what
`llinventorymodel.cpp:1040` puts in `new_inventory["categories"]`. It is a **base-class** method, which is why A4
could not find it in `llviewerinventory.cpp` and left this UNVERIFIED. It emits, in order:

| Key | Line | Value |
|---|---|---|
| `category_id` | `:1259` | `mUUID` — **null on a create**: the viewer constructs the category with `LLUUID::null` (`llinventorymodel.cpp:1038`), so the server assigns the id |
| `parent_id` | `:1260` | `mParentUUID` — the same folder the POST is addressed to |
| `type_default` | `:1261-1262` | `(S8)mPreferredType`, an **integer** folder type |
| `name` | `:1263` | |
| `thumbnail` | `:1265-1268` | `{ asset_id: mThumbnailUUID }`, **only when non-null** |
| `favorite` | `:1270-1273` | `{ toggled: mFavorite }`, **only when true** |

Nothing else. This is the same key set `fromLLSD` reads (§1d) minus the read-only `type` alias, so A4's
inference was right in substance; the one refinement A5 made is that the server now honours the body's
`parent_id` when it names a folder, instead of always using the one in the URL. `thumbnail` and `favorite` are
accepted and dropped: this tree's `InventoryFolderBase` has no column for either.

### A-Q3, partially resolved (A1): the link map the viewer builds

`LLAppearanceMgr` builds a SlamFolder body as an **LLSD array** of link maps, each carrying exactly `name`,
`desc`, `linked_id` and `type` (`AT_LINK`, or `AT_LINK_FOLDER` for the base-outfit link)
(`indra/newview/llappearancemgr.cpp:2209-2245`). That is the shape A2 must accept on
`PUT /category/{id}/links`. The `UpdateItem` / `UpdateCategory` / `CreateInventory` bodies are still
**UNVERIFIED**: their callers are elsewhere in `llviewerinventory.cpp`.
## 1d-bis. The delta contract (A2): what the viewer applies from a mutation response

Extracted from `AISUpdate::parseMeta` / `parseContent` / `parseItem` / `parseCategory` / `doUpdate` in
`llaisapi.cpp` and `LLInventoryModel::onObjectDeletedFromServer` in `llinventorymodel.cpp`. This is the table
the mutation routes implement.

### The complete set of delta keys

Read by `parseMeta` (`:1101-1177`) and **nothing else**. A0's list is confirmed against the source: the removal
key is `_categories_removed`, and `_updated_items`, `_updated_categories` and `_removed_categories` appear
nowhere in the file.

| Key | LLSD | Line | What the viewer does |
|---|---|---|---|
| `_categories_removed` | array of uuid | `:1104-1119` | for each id **it already has**: parent descendent delta −1, id queued for deletion |
| `_category_items_removed` | array of uuid | `:1122-1139` | same, for items; merged into the same id set as the next row |
| `_removed_items` | array of uuid | `:1124` | parsed into the *same* list as `_category_items_removed` — the two are interchangeable |
| `_broken_links_removed` | array of uuid | `:1141-1156` | same handling again |
| `_created_items` | array of uuid | `:1159` | the ids the viewer will accept from `_embedded` on a mutation; drives per-id callbacks for CreateInventory |
| `_created_categories` | array of uuid | `:1162` | same for categories |
| `_updated_category_versions` | map uuid → integer | `:1164-1176` | the folder versions the viewer will adopt, **and the gate on all descendent accounting** |

### Updated objects are content, not a delta key

There is no "updated" delta key. An updated item or category arrives as **top-level content**: `parseContent`
(`:1179-1212`) routes a body with `item_id` + `parent_id` to `parseItem`, and one with `category_id` +
`parent_id` to `parseCategory`. On a **mutation** response (`!mFetch`):

- `parseItem` (`:1215-1258`): if the viewer already has the item it copies its current values first
  (`copyViewerItem`, `:1222` — *"Default to current values where not provided"*), applies the map, and files it
  under `mItemsUpdated`, **plus a zero delta for the parent** (`:1241-1245`). If it does **not** have the item,
  the same body is treated as a creation: `mItemsCreated` and parent delta **+1** (`:1247-1252`).
- `parseCategory` (`:1327-1465`): the same, filing under `mCategoriesUpdated` with zero deltas for **both** the
  parent and the category itself (`:1419-1428`).

Two consequences for the server. A PATCH response may be sparse — only the changed fields plus `item_id` /
`category_id` and `parent_id` — because the viewer merges onto its own copy. And it must be **top level**: on a
mutation the viewer ignores any `_embedded` object whose id is not in `_created_items` / `_created_categories`
(§1c), so an updated object hidden in `_embedded` is silently dropped.

### `_updated_category_versions` gates everything

`doUpdate` (`:1606-1648`) walks the accumulated descendent deltas and **skips any category not listed in**
`_updated_category_versions` — *"Skipping version increment for non-updated category"* (`:1625-1629`). A folder
whose contents changed but which the response does not list keeps a stale descendent count and version forever.
Newly created categories are skipped too, deliberately (`:1618-1622`).

At the end of the update (`:1755-1791`) each listed category has its local version **set to the server's value**
(`:1776`, *"the AIS version should be considered the true version"*); a listed version of −1
(`VERSION_UNKNOWN`) instead triggers a re-fetch with a 360 s expiry (`:1779-1789`).

> **Hazard (Ledger A-R6).** That loop does `cat->getVersion()` with **no null check** on
> `gInventory.getCategory(id)` (`:1760-1762`). Listing a folder the viewer has never fetched is a null
> dereference in the viewer. Only list folders the operation touched.

### Per operation: what to send

| Operation | Content | Delta keys | `_updated_category_versions` must list |
|---|---|---|---|
| `PATCH /item/{id}` | the item, top level (`item_id`, `parent_id`, changed fields) | none | the item's parent folder — the zero-delta entry `parseItem` creates is discarded without it |
| `PATCH /category/{id}` | the category, top level (`category_id`, `parent_id`, changed fields) | none | the category **and** its parent — `parseCategory` creates zero-delta entries for both |
| `DELETE /item/{id}` | none | `_removed_items` (or `_category_items_removed`) with the item id | the item's parent |
| `DELETE /category/{id}` | none | `_categories_removed` with the folder id **only** | the folder's parent |

**Descendents of a deleted folder are implied, not enumerated.**
`LLInventoryModel::onObjectDeletedFromServer` (`llinventorymodel.cpp:2015-2041`) calls
`onDescendentsPurgedFromServer` first for a category — *"For category, need to delete/update all children
first"* — so naming the folder is enough and its children are purged locally. Enumerating them as well would be
harmless but pointless; enumerating them **instead** of the folder would leave the folder behind.

### Two edge rules

- **A delta naming an object the viewer does not have is dropped**, with a warning: every removal arm is inside
  `if (cat)` / `if (item)` (`:1109`, `:1130`, `:1148`), so there is no descendent delta and no deletion. Sending
  a removal for something the viewer never knew is therefore safe, and silently does nothing.
- **An absent delta key and an empty one are identical.** `parseUUIDArray` (`:1077-1088`) does nothing when the
  key is absent and nothing when the array is empty; `_updated_category_versions` is guarded by `update.has`
  (`:1165`). Emitting empty arrays is neither required nor harmful.

### A-Q3, resolved for updates (A2)

`UpdateItem`'s body is the item's **full** `asLLSD()` with `asset_id` and `shadow_id` removed and replaced by
`hash_id` (the transaction id) when it is set — `LLViewerInventoryItem::updateServer`
(`llviewerinventory.cpp:435-454`) and `update_inventory_item` (`:1399-1422`), identically. So the server receives
the whole item map of §1d, minus the asset id, and must ignore what it does not accept rather than fail.

`UpdateCategory`'s body is the category's full `asLLSD()` for a rename (`LLViewerInventoryCategory::updateServer`,
`:651-665`) and for a type change (`changeType`, `:866-884`). For a **protected** folder type the viewer refuses
to send anything but a single-key `{thumbnail}` or `{favorite}` map (`update_inventory_category`, `:1436-1457`),
so those two are the only fields a protected system folder will ever be asked to change.

`CreateInventory`'s `items` / `links` arrays remain **UNVERIFIED** — their callers are outside the permitted
functions — and are A4's problem, not A2's.

### 1d-ter. `UpdateItem`'s field set, and what the server does with each (A18)

`LLInventoryItem::asLLSD` (`llinventory.cpp:936-981`) is the whole body, minus the erase in `updateServer`. The
server's rule for each key, with the reason:

| Key | Source | Server |
|---|---|---|
| `asset_id` | `:952-955` (unrestricted perms, or a null asset) | **applied** |
| `shadow_id` | `:956-963` (restricted perms; the asset XORed with `MAGIC_ID`) | **never arrives** — both update-body builders erase it (`llviewerinventory.cpp:445-452`, `:1414-1421`) |
| `hash_id` | not from `asLLSD`; put in place of the two above when the transaction id is set | **applied**, by handing the transaction to the region's asset-transaction module — only it knows which asset the xfer produced (`Scene.Inventory.cs:579-582`) |
| `permissions` | `:939` `ll_fill_sd_from_permissions` (`llpermissions.cpp:1082-1094`) | `next_owner_mask`, `everyone_mask`, `group_mask` **applied**, each masked by the item's own base; `base_mask` / `owner_mask` and the id fields ignored (`Scene.Inventory.cs:497-548`) |
| `name`, `desc` | `:979-980` | **applied** |
| `flags` | `:976` | **applied** |
| `sale_info` | `:977` (`llsaleinfo.cpp:97-107`: `sale_type`, `sale_price`) | **applied** |
| `parent_id` | `:938` | **ignored** — a move changes two folders' versions and is not this route's job |
| `type`, `inv_type` | `:966-975` | **ignored** — invariants; `XInventoryService.UpdateItem` refuses to change them anyway (`:558-585`) |
| `created_at` | `:981` | **ignored** — not mutable through this route |
| `item_id` | `:937` | **ignored** — it is the URL |
| `thumbnail`, `favorite` | `:942-951` | **ignored** — no column in this tree |

**Why it matters that anything is applied at all.** The data layer bumps the parent folder's version on every
item store (`MySQLXInventoryData.cs:238-246`). A PATCH that changes nothing writes nothing, so no version moves,
so the viewer never re-reads the item — which is exactly how A18's asset loss stayed invisible until a relog.

## 1e-bis. GET /orphans scope (A1, recorded A2)

`/orphans` reports **folder orphans only**: folders whose `ParentID` names a folder absent from the agent's
inventory skeleton. `IInventoryService` has no item-orphan query and finding orphaned items would mean listing
the contents of every folder (tree state T5), so items are never reported. **An empty response means "no orphan
folders", not "no orphans of any kind".**

## 1c-bis. The depth contract (A2b), settled from the fetch path

A1 implemented `depth=N` as "N counts generations expanded below the requested folder" and marked it UNVERIFIED,
expecting a live SL capture to settle it. It is settled from the source instead, and the implementation matches.

### (a) Every URL the viewer builds with a depth

Only two call sites in `llinventorymodelbackgroundfetch.cpp`, and both pass a literal `0` for the depth argument
while varying the *recursive* flag:

| Call | Line | Arguments |
|---|---|---|
| `AISAPI::FetchCategorySubset(cat_id, children, item_type, true, cb, 0)` | `:937` | recursive **true**, depth 0 |
| `AISAPI::FetchCategoryChildren(cat_id, item_type, type == FT_RECURSIVE, cb, 0)` | `:994` | recursive from the queue entry, depth 0 |

The depth that reaches the URL is computed in `llaisapi.cpp`: `depth = MAX_FOLDER_DEPTH_REQUEST` when recursive,
else `llmin(depth, MAX_FOLDER_DEPTH_REQUEST)` (`:463-474` for children, `:517-527` for the string-identifier form,
`:630-637` for the subset), with `MAX_FOLDER_DEPTH_REQUEST = 50` (`:58`). **So the only depths the viewer ever
sends are `depth=50` (recursive) and `depth=0` (not).** No other value is reachable from these call sites.

### (b) What the viewer does with the response

Two mechanisms, and the second is the one that matters:

1. **It re-queues descendants regardless.** `onAISContentCalback` (`llinventorymodelbackgroundfetch.cpp:579-625`)
   walks the direct descendant categories of every folder in the response and pushes each back on
   `mFetchFolderQueue` as `FT_RECURSIVE` — the comment says why: *"push descendant back to verify they are fetched
   fully (ex: didn't encounter depth limit)"* (`:610`). The viewer never assumes the server honoured the depth.
2. **"Fetched" means "has a version".** The queue drain skips a child category when
   `VERSION_UNKNOWN != child_cat->getVersion()` (`:894-898`, again at `:948-953`); only unversioned children are
   put in a subset request.

And a folder only gets a version when the response let the viewer count its descendents: `parseCategory` sets it
inside `if (mCatDescendentsKnown.find(category_id) != end)` **and** `depth >= 0` — *"set version only if we are
sure this update has full data and embeded items since viewer uses version to decide if folder and content still
need fetching"* (`llaisapi.cpp:1380-1407`). `mCatDescendentsKnown` is filled only for a category whose
`_embedded` carries all three collections, or `links` alone for a Current Outfit / Outfit folder (`:1466-1482`).

### (c) The rule the server must follow

The depth in the parse decrements as it descends: `parseContent` parses the top-level category at `mFetchDepth`
(`:1205`), which is the `depth` the viewer kept locally, defaulting to 50 (`:1036-1040`); `parseCategory` then
parses its `_embedded` at `depth - 1` (`:1461-1464`). Combined with the `depth >= 0` gate above:

> **The rule.** `depth=N` licenses the server to expand up to **N generations below the requested folder**. The
> requested folder is parsed at N, its children at N−1, and so on; a category that arrives deeper than N is parsed
> at a negative depth and **never gets a version**, so expanding further than N is wasted work that the viewer
> will re-fetch anyway. Expanding **fewer** generations than N is always safe: the unversioned folders are simply
> queued and fetched on the next round (b1), at the cost of a round trip.

Two corollaries the implementation must respect, and does:

- Every category the server **expands** must carry all three `_embedded` collections *and* a `version`, or the
  viewer cannot count its descendents, will not version it, and will re-request it forever (risk A-R3).
- Every category the server **does not** expand must be a stub with no `_embedded`. Sending `version` on a stub is
  harmless — the version gate is inside the descendents-known branch, so an unexpanded category is left
  `VERSION_UNKNOWN` whatever version accompanies it — which is exactly the "come back for this one" signal.

At `depth=0` this produces precisely the viewer's intent: `mFetchDepth = 0`, the requested folder is versioned,
its `_embedded` is parsed at −1 so no child is versioned, and every child comes back on the queue.

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
breadth first, and `AisHandler.FetchChildren` emits every walked folder with all three collections and every
unwalked child as a stub. That is the rule above, with no off-by-one: our deepest expanded generation is parsed
by the viewer at exactly `depth − depth = 0`, the last value that still versions. The **UNVERIFIED** marker is
removed. The handler additionally clamps a requested depth to 50, matching the viewer's own ceiling, so a client
asking for more cannot make the region walk further than the viewer would ever use.

## 1d-ter. Protected folders (A3), from the viewer's own table

`LLFolderType::lookupIsProtectedType` looks the type up in `LLFolderDictionary` and returns that entry's
PROTECTED flag, **returning `true` for any type the table does not contain**
(`indra/llinventory/llfoldertype.cpp:154-162`). The table is at `:85-127`. So the honest way to express it is an
allow-list of the unprotected types with a protected default — which is what the handler implements.

**Unprotected** (PROTECTED = `false` in the table):

| Viewer type | Line | This tree |
|---|---|---|
| `FT_NONE` | `:126` | `FolderType.None` (−1) — an ordinary user folder |
| `FT_ENSEMBLE_START`..`FT_ENSEMBLE_END` | `:106-109` | no member; handled as the numeric range 26–45. The viewer's own comment says *"Not used"* |
| `FT_OUTFIT` | `:112` | `FolderType.Outfit` — a saved outfit |
| `FT_MARKETPLACE_LISTINGS` | `:122` | `FolderType.MarketplaceListings` |
| `FT_MARKETPLACE_STOCK` | `:123` | `FolderType.MarkplaceStock` (the spelling is this tree's) |
| `FT_MARKETPLACE_VERSION` | `:124` | **no equivalent** — falls through to the protected default; see below |

**Protected**: everything else in the table — `FT_TEXTURE`, `FT_SOUND`, `FT_CALLINGCARD`, `FT_LANDMARK`,
`FT_CLOTHING`, `FT_OBJECT`, `FT_NOTECARD`, `FT_ROOT_INVENTORY`, `FT_LSL_TEXT`, `FT_BODYPART`, `FT_TRASH`,
`FT_SNAPSHOT_CATEGORY`, `FT_LOST_AND_FOUND`, `FT_ANIMATION`, `FT_GESTURE`, `FT_FAVORITE`, `FT_CURRENT_OUTFIT`,
`FT_MY_OUTFITS`, `FT_MESH`, `FT_INBOX`, `FT_OUTBOX`, `FT_BASIC_ROOT`, `FT_SETTINGS`, `FT_MATERIAL`
(`:87-102`, `:111`, `:113-121`, `:125`) — **and every type the table omits**.

**Mismatches between the two trees.**

- `FT_MARKETPLACE_VERSION` (55) is unprotected in the viewer but has no `FolderType` member here, so it takes the
  protected default. Nothing in OpenSim creates it; the practical effect is nil.
- `FolderType.Suitcase` (100) is this tree's, not the viewer's. The viewer's table has no entry, so
  `lookupIsProtectedType` would return `true` for it — and so does the server rule. That is the right answer for
  the HG suitcase folder independently.
- The ensemble range exists in the viewer only as a numeric span with no member here; the server matches it
  numerically.

**What changed from A2b's guess.** A2b protected "the root, or any system type except `FolderType.Outfit`". The
real table is **strictly more permissive**: it additionally leaves the marketplace types and the ensemble range
deletable. No folder that A2b allowed became protected, so the change cannot break anything that worked; it only
stops refusing four classes of folder the viewer never considered protected. The root remains refused, by type
(`FT_ROOT_INVENTORY`) and structurally (a folder with no parent).

## 1e. Version semantics

- Folder versions arrive in two places: `version` on a category map (fetch and mutation responses) and
  `_updated_category_versions` (mutation responses).
- **Fetch:** `parseCategory` sets the local version from `version` only when the descendent count is known from
  `_embedded` (§1c) and `depth >= 0` (`:1389-1407`); it refuses ("Got stale folder", `:1338-1348`) a category whose
  `version` is lower than the version it already holds, and logs a stale-known-folder when the server's is higher
  (`:1409-1416`, "Version was" `:1396`). A newly created category gets its version only with a known descendent count (`:1434-1447`).
- **Mutation:** descendent deltas (±1 per created/removed child, `:1112`, `:1131`, `:1149`, `:1250`, `:1310`,
  `:1450`) are applied only to categories listed in `_updated_category_versions` (`doUpdate`, `:1606-1650`:
  "Skipping version increment for non-updated category"). Afterwards each listed category's local version is
  **set to the server's value** ("the AIS version should be considered the true version", `:1757-1795`, set at `:1776`); a listed
  version of −1 instead triggers a re-fetch with a 360 s expiry (`:1771-1790`).
- **Consequence for the server:** every mutation must list, in `_updated_category_versions`, every folder whose
  contents it changed (the parent of a created item/link/category; the old and new parent of a move; the parent
  of a removed object; the slammed folder), with the post-operation version. A folder changed but not listed
  leaves the viewer's descendent count and version stale.
- Which folders each operation is expected to bump (from the accounting rules above): CreateInventory → the parent;
  SlamFolder → the slammed folder (and each removed link's parent, which is the same folder); RemoveItem /
  RemoveCategory → the removed object's parent; PurgeDescendents → the purged folder; UpdateItem / UpdateCategory
  → the parent when the update moves the object, otherwise the object's own folder is listed with delta 0
  (`:1245`, `:1298`, `:1427`); CopyLibraryCategory → the destination. Those are derived from the viewer's
  accounting, not from an explicit table; the server rule in OpenSim is the data layer's own increment, which
  fires on item store, delete and move and on folder store, and which the cap's category create also applies
  to the parent. The handler must therefore re-read the folder after a write rather than compute the version.

## 1f. HTTP status handling (`InvokeAISCommandCoro`, `:851-1011`)

| Condition | Viewer behaviour |
|---|---|
| response body not an LLSD map | status forced to 500 "Malformed response contents" (`:882-885`); warn; the (non-map) result is still handed to `onUpdateReceived`, which finds nothing to do |
| 410 Gone, `REMOVECATEGORY` | warn; `fetchDescendentsOf(parent)`; the local folder is **not** deleted (`:886-903`) |
| 410 Gone, `REMOVEITEM` | warn; `fetchDescendentsOf(parent)`; local item deleted via `onObjectDeletedFromServer` (`:904-918`) |
| 403 Forbidden, `FETCHCATEGORYCHILDREN` with `depth == 0` | notification `InventoryLimitReachedAISAlert` (first time) / `InventoryLimitReachedAIS`; warn "content is over limit" (`:920-935`) |
| 403 Forbidden, `FETCHCATEGORYCHILDREN` with `depth > 0` | debug only: "recoverable by requesting with lower depth" (`:936-940`) — the caller is expected to retry with a smaller depth (retry logic outside the permitted files, **UNVERIFIED**) |
| any other failure (4xx/5xx, timeout) | warn with status and pretty-printed body (`:942-943`); no retry in `llaisapi.cpp` (transport-level retries in `llcorehttputil`, **UNVERIFIED**) |
| always, success or failure | `onUpdateReceived(result, type, body)` (`:946`): the body **is parsed as an update** even on error, so an error body must be a map and must not carry `item_id`/`category_id` + `parent_id` pairs it does not mean; then the completion callback fires at least once (`:953-1011`), with a null id unless the body carries the ids of §1c |

What an error body should look like: an LLSD map. Nothing in the permitted files reads `error_code`,
`error_description` or `message`; they are conventional and safe because they are ignored. The library returns
them for logs.

## 1g. The `isAvailable()` gate and its consequence

`AISAPI::isAvailable()` (`:62-68`) is exactly `gAgent.getRegion()->isCapabilityAvailable("InventoryAPIv3")`: true
as soon as the current region's seed-cap response contains a URL for `InventoryAPIv3`. Nothing else is checked
(no version, no probe). The viewer requests that cap name on every seed (`:72-76`).

Once true, the following go through AIS with **no fallback** (verified in the permitted functions):

| Path | Behaviour when AIS available | Behaviour when not | Source |
|---|---|---|---|
| delete an item | `AISAPI::RemoveItem` | warns "Tried to use inventory without AIS API" and does **nothing** | `llviewerinventory.cpp:1497-1509` |
| delete a category | `AISAPI::RemoveCategory` (no `isAvailable` check at all) | request fails at the cap lookup, callback null | `:1545-1568` |
| purge a folder's descendents | `AISAPI::PurgeDescendents` | warns, does nothing | `:1630-1645` |
| slam a folder's links (outfit changes) | `AISAPI::SlamFolder` (no check) | fails at cap lookup | `:1776-1784` |
| create a category | `AISAPI::CreateInventory` | falls back to the legacy path below the `if` (`:1034`) | `llinventorymodel.cpp:1034-1042` |
| fetch a category's descendents | `AISAPI::FetchCategoryChildren` (seen at `llviewerinventory.cpp:694,727`, function not in the permitted list) | legacy cap | **UNVERIFIED** detail |
| background inventory fetch, item fetch, COF fetch, links, orphans, library copy | the remaining `AISAPI::Fetch*` / `CopyLibraryCategory` callers are in files not permitted this session | | **UNVERIFIED** |

The consequence that matters: advertising `InventoryAPIv3` in the seed cap flips every path above at once. A
partial implementation returns errors for the paths it lacks, and for delete/purge/slam the LL viewer has no other
way to do them. Hence Ledger risk A-R1: partial AIS is worse than none.

---

# Tree state (HEAD `db7c746248`, branch `feature/ais-v3`)

| # | Finding | file:line |
|---|---|---|
| T1 | **Seed-cap request path.** `BunchOfCaps.SeedCapRequest` reads the requested cap names, adds every one to `validCaps` (the `switch` at `:340-374` only sets flags; `default: break;` then `validCaps.Add(cstr)` at `:375` — no whitelist), then `m_HostCapsObj.GetCapsDetailsLLSDxml(validCaps, sb)` at `:380`. `Caps.GetCapsDetailsLLSDxml` (`Source/OpenSim.Capabilities/Caps.cs:202-222`) delegates to `CapsHandlers.GetCapsDetailsLLSDxml` (`Source/OpenSim.Capabilities/CapsHandlers.cs`, method body: for each requested name, emit a URL only if a simple handler or a request handler is registered under that name), then poll handlers and external handlers, likewise only if requested. **For a new cap to reach the viewer it must be (1) registered on the agent's `Caps` under the exact name (`caps.RegisterSimpleHandler(name, ISimpleStreamHandler)` `Caps.cs:196` or `RegisterHandler` `:190`), at `OnRegisterCaps` time (`Scene/EventManager.cs:819`, fired per agent `:2119`), and (2) requested by the viewer in the seed body.** The viewer requests `InventoryAPIv3` and `LibraryAPIv3` (§1g), so (2) is satisfied; registration is the only region-side act, and the module does it per agent from `OnRegisterCaps`, as `FetchInventory2Module.RegionLoaded` → `RegisterCaps` does (`Source/OpenSim.Region.ClientStack.LindenCaps/FetchInventory2Module.cs`, `RegionLoaded` subscribes, `RegisterFetchCap` registers a `SimpleOSDMapHandler` at `"/" + UUID.Random()`). | `Source/OpenSim.Region.ClientStack.LindenCaps/BunchOfCaps/BunchOfCaps.cs:323-384`; `Source/OpenSim.Capabilities/Caps.cs:190-199, 202-222`; `Source/OpenSim.Capabilities/CapsHandlers.cs` (`GetCapsDetailsLLSDxml`) |
| T2 | **Current Outfit folder resolution:** `IInventoryService.GetFolderForType(userID, FolderType.CurrentOutfit)` (`Source/OpenSim.Services.Interfaces/IInventoryService.cs:68`); implemented by `XInventoryService.GetFolderForType` (`Source/OpenSim.Services.InventoryService/XInventoryService.cs:254`), which queries the user's folder of that type under the root; the folder is created at inventory creation (`:132-133`). Region-side call site that already does this: `AvatarFactoryModule.cs:1097` and `:1112`. The service is reached from a scene via `Scene.InventoryService` (a `LocalInventoryServicesConnector`, `RemoteXInventoryServicesConnector` or `HGInventoryBroker`, `Source/OpenSim.Region.CoreModules/ServiceConnectorsOut/Inventory/*.cs:41-43`). `FolderType.CurrentOutfit` = 46 (`SLUtil.cs:141,204` map it to `currentoutfitfolder`). | as listed |
| T3 | **CreateInventoryCategory cap:** registered at `BunchOfCaps.cs:264-265`, handler `:1138`; creates `new InventoryFolderBase(folderID, folderName, m_AgentID, (short)folderType, parentID, 1)` at `:1200` and calls `m_Scene.InventoryService.AddFolder(folder)` at `:1201`. Parent version bump confirmed by S0a V6: `XInventoryService.AddFolder` (`:369-407`) → `m_Database.StoreFolder` → `MySqlFolderHandler.Store` (`Source/OpenSim.Data.MySQL/MySQLXInventoryData.cs:283-288`) → `IncrementFolderVersion(folder.parentFolderID)`. | `BunchOfCaps.cs:264-265, 1138, 1200-1201`; `MySQLXInventoryData.cs:283-288` |
| T4 | **Where folder Version is read back:** `XInventoryService.GetFolder(principalID, folderID)` (`XInventoryService.cs:630-640`) → `ConvertToOpenSim(XInventoryFolder)` (`:677`: `Version = (ushort)folder.version`); `GetFolderContent` (`:296`) also fills `InventoryCollection.Version` (`:333`) and `InventoryCollection` carries `Version` and `Descendents` (`Source/OpenSim.Framework/InventoryCollection.cs:41-42`). The value is the DB column, so it is fresh on every call; nothing caches it region-side. | as listed |
| T5 | **Link-aware fetch:** `IInventoryService` has none. Its surface (`IInventoryService.cs:46-200`): `GetFolderContent`, `GetMultipleFoldersContent`, `GetFolderItems`, `GetItem`, `GetMultipleItems`, `GetFolder`, plus the mutators. `InventoryCollection` is `Folders` + `Items` (links are items with `AssetType.Link`). The existing descendents cap resolves link targets itself: `FetchInvDescHandler.ProcessLinks` (`Source/OpenSim.Capabilities.Handlers/FetchInventory/FetchInvDescHandler.cs:424-460`) collects `AssetType.Link` items and calls `GetMultipleItems` for the targets. **The AIS module must do the same** (one `GetFolderContent` + one `GetMultipleItems` per folder), and must split links out of `Items` into the `_embedded.links` collection itself. | as listed |
| T6 | **Existing inventory caps:** `FetchInventory2` / `FetchLib2` (`Source/OpenSim.Region.ClientStack.LindenCaps/FetchInventory2Module.cs`, `ISharedRegionModule`, config `[ClientStack.LindenCaps] Cap_FetchInventory2 = localhost`, registers a `SimpleOSDMapHandler("POST", "/" + UUID.Random(), ...)` per agent from `OnRegisterCaps`), `FetchInventoryDescendents2` / `WebFetchInventoryDescendents` (`WebFetchInvDescModule.cs`, a poll-service handler), `FetchLibDescModule.cs`; the request logic lives in `Source/OpenSim.Capabilities.Handlers/FetchInventory/{FetchInventory2Handler,FetchLib2Handler,FetchInvDescHandler}.cs` (`:41`, `:41`, `:42`) which are **plain classes with no shared base**: each is constructed with `(IInventoryService, agentID)` and exposes a request method. Modules are discovered through `PluginRegistration.RegisterPlugins` (`Source/OpenSim.Region.ClientStack.LindenCaps/PluginRegistration.cs:34-54`), not by reflection: a new module must be added there. There is no base class to reuse; the AIS module follows the same shape (module registers, handler class holds the logic) but puts the logic behind `IAisInventoryBackend` so Phase 2 can host it on Robust. | as listed |

Home for the module: `Source/OpenSim.Region.ClientStack.LindenCaps/AIS/` (the project is named `LindenCaps`, not
`Linden.Caps`; the brief's path does not exist). It sits beside `FetchInventory2Module.cs` because that is where
every region-side inventory cap lives (T6), the project already references `OpenSim.Services.Interfaces` and the
HTTP server, and `PluginRegistration.cs` is the discovery point. Tests go to a new project,
`Tests/OpenSim.Region.ClientStack.LindenCaps.AIS.Tests/` (NUnit 4, in the solution): the existing
`Tests/OpenSim.Region.ClientStack.LindenCaps.Tests` is not in `Tranquillity.sln` and does not compile at HEAD
(`EventQueue/Tests/EventQueueTests.cs`, syntax errors; its csproj also pins package versions below
`OpenSim.Tests.Common`), so it was left untouched.
