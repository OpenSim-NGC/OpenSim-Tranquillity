# A9 — the "duplicate Current Outfit folders" are the HG suitcase skeleton

**Date:** 2026-09-04. **Purpose of the session:** deduplicate the seven accounts believed to hold two Current
Outfit folders each, per the proposal in `A7-DUPLICATE-COF.md`.

**Outcome: stopped at the plan step with the database untouched. There is nothing to deduplicate.** The session
issued only `SELECT`s and one `mysqldump`. No `UPDATE`, `DELETE` or `INSERT` was executed.

**Backup taken before any inspection:** `D:\legiongrid\_backup\legiongrid-predupe-20260904-1332.sql`,
2,685,971,589 bytes, no BOM, terminating in `-- Dump completed on 2026-09-04 18:34:21`.

> **Backup hygiene, worth carrying forward.** The first attempt piped `mysqldump` through PowerShell's
> `Set-Content -Encoding utf8`. It produced a 4.08 GB file with a UTF-8 BOM, and it was **corrupt**: PowerShell
> decodes and re-encodes the byte stream, replacing every byte that is not valid text with U+FFFD, which mangles
> binary column data. The 1.4 GB of inflation over the correct 2.50 GB was that damage. It was deleted and the
> dump re-taken with byte-exact shell redirection. **Any `.sql` in `_backup\` produced through a PowerShell text
> pipeline should be assumed unrestorable.**

---

## Before-state survey

Every agent with more than one type-46 folder, with each folder's id, version, parent and child counts. Keeper
(by the A7 rule: highest version) in **bold**.

| agentID | folderID | version | parentFolderID | subFolders | items |
|---|---|---|---|---|---|
| 0f62cf39-71b8-49e1-94ea-ebdf54be01e2 | **2b74a4cd-e384-4778-86c4-80f057b713d1** | **115** | 0f62cf39-71b8-49e1-94ea-ebdf54be01e2 | 0 | 12 |
| 0f62cf39-71b8-49e1-94ea-ebdf54be01e2 | 2eb36833-915b-4076-9226-671ec914bf96 | 1 | 8ee583b3-9259-40e4-989a-431492d85768 | 0 | 0 |
| 47dd39a8-1261-45d2-9fc9-986def3a97b6 | **d2d5a12b-b2a0-412b-8c40-9a8e222dda12** | **9** | 97a4d413-5be4-4551-b977-470bd1f45e1b | 0 | 6 |
| 47dd39a8-1261-45d2-9fc9-986def3a97b6 | 08f89f73-f572-4382-bf09-1196f810ef7a | 1 | b36b7761-4da1-47d9-b138-1e15e20cb936 | 0 | 0 |
| 4dc144cb-4335-4d5f-ac2d-b2c87d0f67e9 | **509aa3ff-15be-4cc6-be8b-5c9aa8398a42** | **36** | 4dc144cb-4335-4d5f-ac2d-b2c87d0f67e9 | 0 | 15 |
| 4dc144cb-4335-4d5f-ac2d-b2c87d0f67e9 | 88028d53-4a08-473c-ac52-fb301727edb8 | 1 | 36b2d277-a69e-47ec-b835-140e20f42e09 | 0 | 0 |
| 4fbdfd2a-e0c6-4003-b2f8-8714fcc7b968 | **4161565b-b08b-490e-80d6-a5a61227bc0f** | **716** | dd748992-4298-4dc6-88b3-d5d72e00226c | 0 | 11 |
| 4fbdfd2a-e0c6-4003-b2f8-8714fcc7b968 | 2acd261c-af0b-4393-a183-5f2cfec6271d | 1 | aa02d15b-78d1-4c2e-9630-226db8a0f36e | 0 | 0 |
| 5266d93e-d723-4317-a653-227bd676dddd | **7853c313-6d58-4816-a1dc-66f7e9fb6d1b** | **9** | 88dcd9ee-7f7b-45b8-a323-8c055054a00a | 0 | 6 |
| 5266d93e-d723-4317-a653-227bd676dddd | 856550ad-84d5-4316-9898-67b020ab347a | 1 | 4f89c68d-8c50-4ddc-8ee3-0bf193dca988 | 0 | 0 |
| a7d2ff2e-dc32-44d8-aa61-3d22070a4964 | **71c3c184-410b-4dae-b20a-855741cf1faf** | **500** | bb7d5f74-a4cf-47cf-9f1d-96f60c1cd954 | 0 | 14 |
| a7d2ff2e-dc32-44d8-aa61-3d22070a4964 | 52c327c4-cb7d-4365-a7f0-62a6f7545265 | 1 | ec7a4f10-2307-4c23-857e-af0550216ea1 | 0 | 0 |
| c0b98d62-9705-4ca5-8f8a-902ad6ee9083 | **fb4e5690-4305-4863-b236-cb594aed5655** | **41** | c0b98d62-9705-4ca5-8f8a-902ad6ee9083 | 0 | 10 |
| c0b98d62-9705-4ca5-8f8a-902ad6ee9083 | 52f5dcdb-7ea7-4bbf-a015-78499f7ba46f | 1 | 40a7c741-4dc5-46fc-8df2-b9811d4ff7d5 | 0 | 0 |

Seven accounts, as expected, and every version-1 folder is empty. **But the two folders in each pair have
different parents** — which `A7-DUPLICATE-COF.md` had asserted was impossible. That is what stopped the run.

## Resolving the parents

| agent | keeper's parent | loser's parent |
|---|---|---|
| all seven | `My Inventory`, **type 8**, `parentFolderID = 00000000-…` | `My Suitcase`, **type 100** |

For Truly Bazar specifically:

| folderID | version | parent | parentName | parentType |
|---|---|---|---|---|
| 52c327c4-cb7d-4365-a7f0-62a6f7545265 | 1 | ec7a4f10-2307-4c23-857e-af0550216ea1 | **My Suitcase** | **100** |
| 71c3c184-410b-4dae-b20a-855741cf1faf | 500 | bb7d5f74-a4cf-47cf-9f1d-96f60c1cd954 | **My Inventory** | **8** |

The version-1 folders are the Current Outfit folders of the **HG suitcase skeleton**, created deliberately by
`HGSuitcaseInventoryService.CreateSystemFolders` (`:172-186`) when the suitcase is made (`GetRootFolder :152-165`).

## The three confirmations

**1. The affected accounts are exactly the accounts with a suitcase.**

| query | result |
|---|---|
| accounts with a type-100 folder | **7** |
| overlap between "accounts with >1 type-46" and "accounts with a suitcase" | **7 of 7** |

**2. It is not Current-Outfit-specific — sixteen system types show the same pattern, across the same seven
accounts.** That is the whole suitcase skeleton as `CreateSystemFolders` builds it.

| type | accounts affected | | type | accounts affected |
|---|---|---|---|---|
| 1 | 7 | | 15 | 7 |
| 2 | **14** | | 16 | 7 |
| 3 | 7 | | 20 | 7 |
| 5 | 7 | | 21 | 7 |
| 6 | 7 | | 23 | 7 |
| 7 | 7 | | **46** | **7** |
| 10 | 7 | | 56 | 7 |
| 13 | 7 | | | |
| 14 | 7 | | | |

Type 2 (Calling Cards) shows 14 because the skeleton nests `Friends` and `All` beneath it — three per tree, as
`CreateUserInventory :122-127` and `CreateSystemFolders` both do.

**3. Genuine duplicates: zero.** Counting agents with more than one type-46 folder whose parent is *not* a
suitcase returns **0**.

## What executing the dedupe would have done

Deleted a live part of the HG suitcase skeleton for seven accounts, permanently: `CreateSystemFolders` is called
only when the *suitcase itself* is missing (`GetRootFolder :152-165`), so a deleted suitcase COF does not grow
back. Applied consistently the same rule would have taken the other fifteen types too — and the plan's own
"report any other duplicated type" step ran *after* the deletions, so it would have reported the damage rather
than prevented it.

## Consequences for the record

- `A7-DUPLICATE-COF.md` is corrected in place, with the superseded reasoning kept visible.
- **A-R8** is corrected: no unique index on `(agentID, type)`. It would reject the suitcase skeleton and break
  suitcase creation grid-wide.
- **A-Q16** is opened: for a local user the A7 rule scans every type-46 folder including the suitcase's.
- The resolution fix `6cd13a3645` stands on its own merits — `folders[0]` over an unordered query really is
  non-deterministic — but it did not fix step 10, because COF resolution was never wrong. Step 10 is
  re-diagnosed in `A10-STEP10-REDIAGNOSIS.md`.
