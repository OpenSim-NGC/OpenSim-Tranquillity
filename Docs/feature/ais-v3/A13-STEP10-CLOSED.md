# A13 — step 10 closed, A-Q17 answered, T-2 deployed

**Date:** 2026-09-04. Docs only.

---

## 1. Step 10 passes, on a clean run, with the cause fixed

**Build `1.1.202-alpha+bfb50070d8`** — the merged AIS + appearance-flush tree. No slam between the take-off and
the relog, and ~20 seconds in-world before logging out.

| time | line |
|---|---|
| 17:15:18,402 | `DELETE /item/a4cb683b-…` → RemoveItem |
| 17:15:18,432 | `RemoveItem -> 200 _removed_items=[a4cb683b-…] _updated_category_versions={71c3c184-…:503}` |
| ~17:15:23 | the 5 s timer fires and writes — **inferred**: no drop WARN, and the change persisted |
| 17:15:38 | logout; attachpoints 40, 7, 8 saved; **no flush line** |
| relog | **the dress stayed off** |

**The absent flush line is the cost guarantee, not a failure.** `FlushAppearanceSaveOnClose` writes only when the
queue actually holds an entry — *a close with nothing queued writes nothing*, which has a test of its own. The
timer had already drained the queue at ~17:15:23, fifteen seconds before logout, so there was nothing to flush.
The flush exists for the other case: a logout **inside** the five-second window. That is what happened at
14:09:40 on the failing run, and that is what lost the dress.

`A5-RUN-2026-09-04.md` is updated: step 10 **pass**. *(Tally revised again by A15, once step 7 was found to be
unreachable through the viewer: **12 pass, 1 not reachable (7), 2 not run (14, 15)**.)*

## 2. A-Q17 answered — and neither hypothesis was right

A10 left two hypotheses. Both are now dead.

| | Hypothesis | Verdict |
|---|---|---|
| **H1** | a mutation response delta the viewer rejected | **Dead.** A11's logging shows the deltas were correct and complete: `_removed_items` named the link, `_updated_category_versions` named the COF with its new version. Nothing was rejected because nothing was wrong. |
| **H2** | the avatar appearance record was never updated | **Wrong as framed.** The record was not simply never written — the wearables were correct (7 entries, no dress). H2 assumed a *missing server behaviour*; the truth is a *lost write*. |

**The actual cause (A12):** the detach happened — the viewer reconciles attachments itself in
`updateAppearanceFromCOF` (`llappearancemgr.cpp:2656`) and sent it, the region handled it, and attachpoint 18 was
absent from the logout save batch. What was lost is the **deferred appearance save**:
`AvatarFactoryModule.QueueAppearanceSave` defers by `m_savetime` (5 s) and `SaveAppearance` then does
`sp == null -> continue`, dropping the write silently when the agent has already left. Nothing flushed it —
`DeRezAttachments` never touches appearance.

It **predates AIS**, hits the **legacy path equally**, and is **symmetric**: wear queues the same deferred save
and loses it the same way, so an attachment worn just before logout comes back missing.

### The working lesson

**Three successive diagnoses were aimed at the wrong store, and each was corrected by one read-only query or one
log line.**

| # | Diagnosis | Store blamed | What corrected it | Cost |
|---|---|---|---|---|
| 1 | A7 | COF resolution — the agent has two type-46 folders and we pick the wrong one | one `SELECT` showing the version-1 folder is parented to `My Suitcase`, so the query could never have returned it (A9) | a fix, a deploy, and a dedupe plan that would have deleted live suitcase skeletons |
| 2 | A10 | the response delta, then the appearance record | the A11 response logging, which showed the deltas were correct (A13) | a session |
| 3 | A12 | attachment reconciliation — "nothing detached the object" | the logout save batch, which listed attachpoints 11/40/7/8 but **not** 18, proving the object *had* been detached | caught in-session, before any code |

Each wrong turn rested on **one unverified observation** carried in as fact: "AIS returned the version-1 folder",
"the response must have been wrong", "nothing detached the object". None was checked; each was cheap to check.

**The rule worth keeping: verify the single observation the argument rests on, before building on it.** The tell
is an argument whose whole structure depends on one premise nobody has measured — especially when that premise
arrived in the framing of the problem rather than from the evidence.

A related habit that paid for itself: when a diagnosis cannot be separated from its alternatives, **add the
missing evidence rather than another theory**. A11 was a session spent only on logging, and it settled a question
two prior sessions could not.

## 3. T-2 — the fix and its deploy

**`dc4e417bb3`** on `fix/appearance-save-flush`:

- flushes a pending appearance save on `EventManager.OnRemovePresence`, raised at `Scene.cs:3866` while the
  presence is still resolvable — it is not removed until `:3898` nor disposed until `:3905`;
- **skipped for child agents**, so a teleport cannot publish a stale outfit over a newer one the destination has
  since saved; on a teleport the source's root is converted by `MakeChildAgent`, not `RemoveClient`, and the
  appearance travels in the agent data;
- **WARN on both drop paths**, so this cannot fail silently again;
- **6 tests, 4 of which fail without it**; the other 2 assert the cost guarantee.

It also added the `DisableTestParallelization` declaration `OpenSim.Region.CoreModules.Tests` was missing — the
same one, for the same reason, already in `OpenSim.Region.Framework.Tests`. That project was order-dependent and
flaky: a full run failed **9 tests, but not the same 9**, with failures appearing inside `SceneHelpers.SetupScene`
rather than in any assertion. It is now deterministic at **5 failed / 84 passed**. Those 5 are pre-existing and
are **T-3's job**.

**Deployed** as merge **`bfb50070d8`** on `integration/ais-appearance` — merge-base `11a2456833`, clean, zero
conflicts, no file overlap between the branches. Backup at
`D:\legiongrid\_backup\regionserver-20260904-1703\`. Both features verified present in the **deployed** binaries:
`InventoryAPIv3` and the mutation-delta line in `LindenCaps.dll`, the flush and both WARN lines in
`CoreModules.dll`.

## 4. Open items — enough context to pick up cold

~~**1. The live region runs a commit reachable from only one branch.**~~ **DONE 2026-09-04 (A14).**
`fix/appearance-save-flush` merged into `feature/ais-v3`; the merged tree differs from the deployed
`bfb50070d8` only in `Docs/`, so the live binaries are reproducible from the branch. `integration/ais-appearance`
deleted and `D:\tranq-integration` removed. The deployed merge is not an ancestor of the new HEAD, so it is
tagged `deployed/region-2026-09-04` to keep the binaries' `+bfb50070d8` stamp resolvable.

~~**2. Robust redeploy, still blocked.**~~ **DONE 2026-09-04 (R1).** Merged as `a2c8fb63f3` and deployed;
Robust runs one commit where it ran four, and restarted cleanly at 19:03:58 with trusted-hypergrid loaded.
`ONLYIFTRASH` and `EnsureSystemFolder` are live. **Checklist step 7 was never actually blocked by this** — it is
not reachable through any viewer (A15).

**3. A12's remaining holes.** The viewer skips its removal arm entirely when `isFullyLoaded()` is false
(`llappearancemgr.cpp:2654`) and **never retries**; and an offline agent has no viewer to reconcile at all, which
Phase 2's Robust hosting makes the normal case. A12 option **B3 — login-time reconciliation** — is the standing
recommendation, with the guard that it must reconcile only on disagreement and **never strip on an empty or
unreadable COF**.

**4. A-Q16 open.** For a local user, AIS's system-folder resolution scans every type-46 folder including the
suitcase's. Suitcase COFs sit at version 1 so the root always wins today, but nothing enforces it.

**5. Checklist steps 14 and 15 unrun** — HG folder deletion, and folder thumbnail/favourite. Both are documented
limitations rather than suspected defects.

**6. T-3: the 5 residual `CoreModules` failures**, now stable and attributable — 2 asset-store assertions in
`AvatarFactoryModuleTests`, 3 IAR loader tests. They were hidden behind the flakiness until T-2 made the project
deterministic.

**7. Backup integrity, unaudited.** Any `.sql` in `D:\legiongrid\_backup\` written through a PowerShell text
pipeline is **corrupt and unrestorable** — `Set-Content` re-encodes the byte stream and replaces every byte that
is not valid text, which mangles binary column data. `legiongrid-predupe-20260904-1332.sql` was re-taken by shell
redirection and is good (2,685,971,589 bytes, no BOM). **An audit of the rest of that folder has not been run.**
