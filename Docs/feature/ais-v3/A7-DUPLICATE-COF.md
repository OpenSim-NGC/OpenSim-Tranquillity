# A7 — two Current Outfit folders, and which one wins

> **CORRECTED 2026-09-04 by A9. The central claim of this document was wrong.**
>
> There are **no duplicate Current Outfit folders on this grid**. The version-1 folders are the Current Outfit
> folders of the **HG suitcase skeleton** — parented to `My Suitcase` (type 100) and created by
> `HGSuitcaseInventoryService.CreateSystemFolders` (`:172-186`). Sixteen system folder types show the same
> pattern across the same seven accounts, which are exactly the seven accounts that have a suitcase. Counting
> agents with more than one type-46 folder *outside* any suitcase returns **zero**.
>
> Evidence and the full before-state table: `A9-SUITCASE-NOT-DUPLICATE.md`.
>
> **Also corrected:** the failure this document was written to explain was **not** a resolution failure. The
> region log shows every slam went to `71c3c184…`, the correct root COF. Step 10's real cause is re-diagnosed in
> `A10-STEP10-REDIAGNOSIS.md`.
>
> **What still stands:** the resolution in `AisInventory.GetSystemFolder` (`6cd13a3645`) is sound and worth
> keeping — `folders[0]` over an unordered query genuinely is non-deterministic, and a deterministic rule with a
> WARN is better than a coin flip. It simply was not fixing the bug we thought it was. See **A-Q16** for a latent
> risk the correct picture exposes.
>
> The original text follows, kept because the reasoning error in it is worth reading. **Sections (a)–(c) below
> are superseded.**

---

**Date:** 2026-09-04. **Region:** Ebony, `AIS_Enabled = true`. **Avatar:** Truly Bazar
(`a7d2ff2e-dc32-44d8-aa61-3d22070a4964`). **Checklist step:** 10, take off a garment.

**Symptom:** the skirt came off and was back after a relog. The viewer slammed
`71c3c184-410b-4dae-b20a-855741cf1faf` twice (12:36, 12:37); at login 12:38 it fetched `/category/current/links`
and immediately rebuilt links in `71c3c184…`. The avatar has two type-46 folders:

| folder | name | type | version | ~~claim~~ **actual (A9)** |
|---|---|---|---|---|
| `71c3c184-410b-4dae-b20a-855741cf1faf` | Current Outfit | 46 | 457 (now 500) | the real COF, under `My Inventory` |
| `52c327c4-cb7d-4365-a7f0-62a6f7545265` | Current Outfit | 46 | 1 | ~~the one we returned~~ **the suitcase's COF, under `My Suitcase`** |

## ~~The short version~~ — SUPERSEDED

> ~~Our `"current"` alias resolves to an arbitrary one of the agent's type-46 folders, and it picked the wrong
> one. The take-off did happen — it was written to a folder no viewer reads.~~
>
> **It did not pick the wrong one.** The log shows the slam went to `71c3c184…` every time.

## (a) What the resolution actually promises: nothing — STILL TRUE, but not the cause

This section's *description of the code* is accurate and unchanged. What was wrong was the inference drawn from it.

`AisInventory.GetCurrentOutfit` (`AisInventory.cs:106-107`) → `IAisInventoryBackend.GetFolderForType`
(`IAisInventoryBackend.cs:20`) → `InventoryServiceBackend` (`AISv3Module.cs:172`) → `IInventoryService`. On Legion
Grid inventory is remote (`RemoteXInventoryServiceConnector.cs:175-178` → `XInventoryServicesConnector.cs:186-195`,
`METHOD=GETFOLDERFORTYPE`) → Robust `XInventoryInConnector.cs:260` → `XInventoryService.GetFolderForType`.

Robust runs the plain service for the region-facing port (`Robust.ini:107-108`), so this is the code that answers:

```csharp
private InventoryFolderBase GetSystemFolderForType(InventoryFolderBase rootFolder, FolderType type)  // XInventoryService.cs:272-294
{
    if (type == FolderType.Root)
        return rootFolder;

    XInventoryFolder[] folders = m_Database.GetFolders(
            ["agentID", "parentFolderID", "type"],
            [rootFolder.Owner.ToString(), rootFolder.ID.ToString(), ((int)type).ToString()]);

    if (folders.Length == 0)
        return null;

    return ConvertToOpenSim(folders[0]);          // first row wins. No ordering, no tie-break, no warning.
}
```

`folders[0]`, and the query behind it has **no `ORDER BY` and no `LIMIT`**: `MySQLXInventoryData.GetFolders`
(`:56-59`) → `MySqlFolderHandler` (no `Get` override, `:250-256`) → `MySQLGenericTableHandler.Get(string[],
string[])` (`:154-157`), which delegates with `options = String.Empty` (`:159-185`). The schema permits duplicates:
`inventoryfolders` (`InventoryStore.migrations:30-40`) has `PRIMARY KEY (folderID)` and non-unique keys only.

**All of that remains true.** It is a real latent fragility and the reason `6cd13a3645` is worth keeping. It was
simply not what broke step 10.

### The reasoning error, stated plainly

> **A consequence worth stating:** this query filters on `parentFolderID = rootFolder.ID`. Two rows can only both
> match if **both COFs are direct children of the same root folder**. `52c327c4…` therefore certainly is.

The first sentence is correct. **The last sentence does not follow, and it is where this went wrong.**

The filter means `GetSystemFolderForType` can only ever return a folder parented to the root. I combined that with
an unverified premise — "AIS returned `52c327c4…`" — and concluded `52c327c4…` must be under root. The premise was
never checked; it came from the brief's own reading of the symptom and I adopted it as fact.

Run the other way, the same filter refutes the premise: `52c327c4…` is under `My Suitcase`, so
`GetSystemFolderForType` **could not have returned it**, so AIS was never resolving to it. One read-only query
would have caught this before a line of code was written. The lesson is not "check parentage" but: **when a
diagnosis rests on an assumed observation, verify the observation first, especially when it is the one fact that
makes the rest of the argument work.**

## (b) Every site that creates a type-46 folder — table still correct, conclusion inverted

| # | Site | When it fires | Guarded? | Parents to |
|---|---|---|---|---|
| 1 | `XInventoryService.CreateUserInventory` `:132-133` | account creation `UserAccountService.cs:817`; `RemoteAdminPlugin.cs:2630`; every Direct Delivery `DirectDeliveryPostHandler.cs:134, :217`; `XInventoryInConnector.cs:205` | yes — `GetSystemFolders` `:194-208` scans all root children | the real root |
| 2 | `HGSuitcaseInventoryService.CreateSystemFolders` `:185-186` | once per user, from `GetRootFolder` `:136-170` when they have no suitcase yet | yes, same shape | **the suitcase** |
| 3 | `HGInventoryService` `:103-115` | HG visitor with no suitcase | — | creates only a suitcase |
| 4 | IAR load | — | — | creates no type-46 |

~~Sites 2, 3 and 4 are excluded by the parentage consequence… That leaves site 1 as the only in-tree path that can
put a second type-46 under the root.~~

**Inverted by A9. Site 2 is the answer, and it is not a bug.** Site 2 created the version-1 folders, on purpose,
as part of the suitcase skeleton. Site 1 created nothing extra — the `EnsureSystemFolder` narrowing committed in
`68bfa60735` is defensible hardening of a real read-then-write race, but **no observed duplicate is attributable to
it**, and it should not be described as fixing one.

## (c) Does this predate AIS? — the question was moot

The section argued that every `GetFolderForType` caller inherits the same coin flip and that AIS merely made a
latent data fault visible. The first half stands as a statement about the code. The second half does not: **there
was no data fault.**

## The rule chosen, and why — STILL THE RULE, with a caveat

Highest `Version`, ties on lowest folder id. The justification from the tree is unchanged and still holds:

- version is bumped on every child add/remove (`MySqlFolderHandler.Store :283-291`, `MoveFolder :258-281` →
  `IncrementFolderVersion`, `MySQLXInventoryData.cs:303-317`) and never decreases (`UpdateFolder :423-427`,
  `:439-440`);
- creation order is not available — `XInventoryFolder` has six fields (`IXInventoryData.cs:32-45`) and the table
  has no timestamp;
- descendant count is unusable — a legitimately emptied COF has none.

**Caveat added by A9, tracked as A-Q16.** Resolution scans the skeleton, which for a local user includes the
suitcase subtree. Suitcase COFs sit at version 1 today, so the root COF always wins — but nothing enforces that.
If a suitcase COF ever overtook the root COF, a local user's outfit would resolve into their suitcase.

## ~~What would confirm the creation site~~ — RUN, AND IT REFUTED THIS DOCUMENT

The query this section proposed was run in A9. Its own stated failure branch was the one that came true:

> If the second row's parent is the suitcase, the parentage argument in (a) is wrong and the diagnosis must be
> reopened.

It is, it was, and it has been.

## ~~What the dedupe should do~~ — DO NOT RUN A DEDUPE

There is nothing to deduplicate. Running the proposed dedupe would have deleted a live part of the HG suitcase
skeleton for seven accounts, and it would **not** have grown back: `CreateSystemFolders` is only called when the
suitcase itself is missing (`GetRootFolder :152-165`).

## Ledger items

- **A-R8** — corrected. A unique index on `(agentID, type)` **must not be added**: it would reject the legitimate
  suitcase skeleton and break suitcase creation grid-wide. See the ledger for the candidate shape, which is not
  settled.
- **A-Q13** — unchanged. `InventoryFolderBase.Version` is `ushort` (`InventoryFolderBase.cs:67`) against an
  `int(11)` column.
- **A-Q16** — new, the suitcase-overtakes-root risk described above.
