# A10 — re-diagnosing step 10, after A9 removed the original explanation


> **ANSWERED 2026-09-04 by A12/A13. Neither hypothesis in §5 was right.**
>
> - **H1 (a response delta the viewer rejected) is dead.** A11's response logging showed the deltas were correct
>   and complete: `_removed_items` named the link and `_updated_category_versions` named the COF with its new
>   version. Nothing was rejected because nothing was wrong.
> - **H2 (the appearance record never updated) was wrong as framed.** The record was not simply never written —
>   the wearables were correct, 7 entries and no dress. H2 assumed a *missing server behaviour*; the truth is a
>   *lost write*.
>
> **The cause:** the detach happened and the **deferred appearance save was dropped** because the agent left
> before the five-second timer fired — `AvatarFactoryModule.SaveAppearance`, `sp == null -> continue`. It predates
> AIS, hits the legacy path equally, and is symmetric: wear loses the same way. Diagnosed in
> `A12-ATTACHMENT-RECONCILIATION.md`, fixed in `dc4e417bb3`, closed in `A13-STEP10-CLOSED.md`.
>
> **What this document got right and should be kept for:** take-off is `DELETE /item` and not a slam; COF
> resolution was never wrong; and the passing re-run it examined was not a clean repetition. Those three findings
> stand. Its §5 hypotheses do not.


**Date:** 2026-09-04. **Region:** Ebony. **Avatar:** Truly Bazar
(`a7d2ff2e-dc32-44d8-aa61-3d22070a4964`). **Sources:** `OpenSim.Server.RegionServer20260904.log` and the live
database, read-only.

**Conclusion up front: no single cause is established, and the passing re-run cannot be attributed to the A7
fix. This session stops without a fix, as the brief requires.** What it does establish is that three of the four
lines of inquiry were aimed at the wrong operation, and it rules several things out with evidence.

> **Timestamps.** Log times are local; `FROM_UNIXTIME` in the database is UTC, five hours ahead — the dump that
> finished at local 13:33 is stamped `18:33:26`. Both are quoted below in their own clock and labelled.

---

## 1. The operation under test is not a slam

**Take-off is `DELETE /item/{linkid}` → `RemoveItem`. It is not `SlamFolder`.**

Every `SlamFolder` in the log is immediately preceded by `GET /category/5d7b7115-…/links`, and
`5d7b7115-edcb-4638-b5f9-196a1dd7aed3` is the asset id of the `AT_LINK_FOLDER` link named **"Truly Base"** — the
saved outfit folder. Fetching an outfit's links and then slamming COF is **wear-outfit** (step 9):

| time (local) | request |
|---|---|
| 12:33:28,670 | `GET /category/5d7b7115-…/links` → FetchCategoryLinks |
| 12:33:28,720 | `PUT /category/71c3c184-…/links` → **SlamFolder** |
| 12:36:38,536 | `GET /category/5d7b7115-…/links` |
| 12:36:38,588 | `PUT /category/71c3c184-…/links` → **SlamFolder** |
| 12:37:33,205 | `GET /category/5d7b7115-…/links` |
| 12:37:33,239 | `PUT /category/71c3c184-…/links` → **SlamFolder** |

The take-offs are the `RemoveItem` calls, each followed by a fetch of the garment's own item — and the garment is
the same one every time, `21ae19b0-75a8-41ba-b6d9-1f0472e39437`:

| time (local) | request |
|---|---|
| 12:36:18,320 | `DELETE /item/dd6ac393-…` → **RemoveItem** |
| 12:37:26,116 | `DELETE /item/92873235-…` → **RemoveItem**, then `GET /item/21ae19b0-…` |
| 12:37:52,105 | `DELETE /item/f1137049-…` → **RemoveItem**, then `GET /item/21ae19b0-…` |

**Consequence for the brief:** lines of inquiry (a), (b) and (c) all concern the slam's create-then-remove
ordering and its response deltas. That machinery was not on the failing path. It was examined anyway (§3) and is
sound.

## 2. COF resolution was never wrong — confirmed on both sides of the fix

Every mutation in the failing window addressed `71c3c184-…`, the correct root COF, never `52c327c4-…`. After the
fix the new WARN prints the resolution explicitly:

```
13:11:38,047 WARN [AIS]: agent "a7d2ff2e-…" has 2 folders of type CurrentOutfit
  ("52c327c4-cb7d-4365-a7f0-62a6f7545265 v1, 71c3c184-410b-4dae-b20a-855741cf1faf v466");
  using "71c3c184-410b-4dae-b20a-855741cf1faf" version 466
```

Same folder before and after. **The A7 fix changed which folder was chosen in exactly zero cases**, because the
suitcase COF was never a candidate the old code could return (A9). Whatever made step 10 pass on re-run, it was
not this.

## 3. What was examined and cleared

| Checked | Finding |
|---|---|
| Slam ordering (`AisSlam.Run :95-147`) | Correct. Creates all, then deletes the prior links by id; on a creation failure it rolls back and reports; the compensating `Rollback :150-156` only ever deletes ids it created in this call, so it **cannot** fire spuriously against a successful removal. |
| `AisEnvelope.IsLink :46-47` | Handles **both** `AT_LINK` (24) and `AT_LINK_FOLDER` (25), so the outfit-folder link is slammed like any other. An earlier suspicion that type 25 was being skipped and accumulating is wrong. |
| `RemoveItem` (`AisHandler.cs:366-382`) | Correct by inspection. Captures `item.Folder` **before** the delete, reports `_removed_items` with the item id and `_updated_category_versions` with the parent's freshly read version — which is exactly what spec §1d-bis requires for `DELETE /item/{id}`. |
| Does an item delete bump the folder version? | **Yes.** `MySqlItemHandler.Delete(string[], string[])` collects the parents and calls `IncrementFolderVersion` for each. (The single-field overload deliberately does not, because it delegates.) So the version the response reports really has moved. |
| `FetchCOF` shape (`FetchLinks :251-271`) | Conformant. Emits `_embedded.links` plus the resolved targets in `_embedded.items`; §1c says the viewer takes a COF's descendent count from `links` alone. |
| Current COF contents | 14 links, **no duplicates**, no surviving skirt link. Nothing was left behind by any removal. |

Line of inquiry (a) asked for surviving links that should have been removed. **There are none** — but that is
weak evidence, because the current state postdates the re-run and a full slam has rewritten the folder since.

## 4. The failure signature, and why the re-run does not clear it

The failure is visible in the log as **`FetchCOF` immediately followed by `CreateInventory` into COF** — the
viewer reading the folder and instantly putting something back:

| login | FetchCOF | next CreateInventory into COF | gap |
|---|---|---|---|
| **12:38:07 (failing)** | 12:38:10,905 | **12:38:10,944** | **39 ms — automatic repair** |
| 13:11:34 (post-deploy) | 13:11:38,002 | none before the next user action | — |
| 13:14:41 (the "pass") | 13:14:44,639 | 13:15:19,543 | 35 s — a deliberate wear |
| 13:28:13 | 13:28:16,911 | none | — |

39 ms is not a user. The viewer read COF and repaired it against its own idea of what should be there.

**And the re-run was not a repetition of the failing sequence.** Between the take-off and the relog that "passed",
a full wear-outfit slam intervened:

| time (local) | event |
|---|---|
| 13:12:11,289 | `DELETE /item/7235d8bd-…` → RemoveItem (the take-off) |
| **13:13:53,919** | **`PUT /category/71c3c184-…/links` → SlamFolder** — rewrites every link in COF |
| 13:14:23,031 | `DELETE /item/18c41e76-…` → RemoveItem |
| 13:14:41,487 | relog |

The database confirms the slam rewrote the folder wholesale: thirteen of the fourteen links carry creation
timestamps of `18:13:53`–`18:13:54` UTC (local 13:13:53–54), and only the dress added later differs at `18:15:19`.

So any stale state left by the 13:12:11 take-off was **erased by a full slam** before the relog that was recorded
as a pass. The pass is therefore consistent with the fix working, with the slam having papered over the problem,
and with the problem being intermittent. **It does not discriminate between them, and an unexplained pass is not
a pass.**

## 5. What remains, and what would settle it

Two hypotheses survive. Neither can be confirmed from what exists.

**H1 — a mutation response delta the viewer rejected.** If the `RemoveItem` response did not take effect in the
viewer's model, the viewer kept the link and restored it at the next login. Against it: the code is correct by
inspection, and checklist step 6 (delete an item, verified with *no duplicate left in the source folder*) exercises
the same route and passed. **Response bodies are not logged**, so the actual delta sent at 12:37:52 cannot be
recovered.

**H2 — the avatar appearance record was never updated.** OpenSim keeps worn wearables in the avatar appearance
record, separate from the COF links. Under the legacy path a take-off travels as `AgentIsNowWearing` over UDP and
`AvatarFactoryModule` updates that record. Under AIS the viewer removed the COF link over HTTP, and **nothing in
the AIS handler touches appearance** — Ledger P-2 forbids it from taking a `Scene` or `ScenePresence` at all. If
the record still listed the garment, then at the next login the viewer would find itself wearing something with no
COF link and create one, which is exactly the 39 ms repair. This also fits the checklist's own note that Firestorm
has its own outfit machinery and may or may not send `AvatarNowWearing` alongside (SSB ledger A-Q6). Against it:
the region log contains **zero** `AVATAR FACTORY` lines for the whole day, so the module's activity is invisible
here and the hypothesis cannot be tested from this log either.

**What would settle it, in order of cost:**

1. **Log AIS response bodies at DEBUG for mutations** (or at least the delta keys and the reported version). The
   single missing piece in both hypotheses is what we actually sent back. This is a small change and it is what
   A6 taught: a request that arrives and misbehaves must not look the same as one that never arrived.
2. **Reproduce cleanly**: take off one garment, then relog **without** any intervening wear or slam, and watch for
   `FetchCOF` → `CreateInventory` within a few tens of milliseconds. That is a definitive, one-minute test, and it
   is the run the checklist should have recorded.
3. **Capture the appearance record** (the avatar's serialised wearables) immediately before and after a take-off.
   If the garment is still listed after, H2 is confirmed and H1 is dead.

## 6. Status

- Step 10 is **not** diagnosed. The A7 explanation is withdrawn (A9) and nothing has replaced it.
- The A7 resolution fix (`6cd13a3645`) stays, on its own merits, but **must not be described as fixing step 10**.
- The A5 run record's step 10 row — "FAILED, then passed after the A7 fix" — is **misleading as to cause** and is
  corrected by this document.
- No code was changed in this session.
