# Build Plan — Track L combined: AIS v3 (L-1) + SSB (L-2) together

**Status:** DRAFT, prompted by Mike's 2026-09-03 feedback. **Supersedes** nothing yet: `BUILD-PLAN-ssb-appearance.md` stays the SSB lane's detail; this document adds the AIS lane and the interleave.
**Estimating convention:** wall-clock Claude Code minutes per session, anchored to measured sessions (20–55 min). A session past ~2× its estimate is stuck: stop, report, re-scope. One feature per session.
**Branches:** `feature/ssb-appearance` (worktree `/d/tranq-ssb`) and `feature/ais-v3` (worktree `/d/tranq-ais`), both off `develop` HEAD. They touch disjoint files except the caps registration switch and the inventory folder-version path (see §3). Merge order: AIS first (it owns folder versions), then SSB.

## 1. Why together

- The LL viewer needs **both** to be a usable viewer: SSB makes the avatar visible, AIS makes it changeable. Either alone ships a half-viewer.
- They share one invariant — the COF folder `Version` — and SSB's change-trigger session (S5) can only be tested properly with the LL viewer once AIS's SlamFolder exists.
- Verify loops are the scarce resource (yours), not CC minutes. A joint soak on one test region with LL viewer + Firestorm + web viewer replaces two soaks.

## 2. AIS lane

Spec source: RECON-02 §3 (routes and `else`-branch table from `llaisapi.cpp` / `llviewerinventory.cpp` at `62033f2`). The prompt for A0 carries the extracted route/verb/envelope table so CC never opens the viewer tree.

| A | Feature | DoD | Est. min |
|---|---|---|---|
| A0 | **Verification + harness skeleton.** Grep pass at HEAD: `BunchOfCaps` switch and `validCaps` path, `XInventoryService` folder-version bump sites, `CreateInventoryCategory` cap, COF folder type resolution. Create `OpenSim.Region.ClientStack.Linden.Caps/AIS/` (or `Addons/`, per finding) module skeleton with `[AIS] Enabled = false` gate, plus the acceptance harness: HTTP client + LLSD envelope fixtures (`_embedded{categories,items,links}`, `_links`, `_updated_items`, `_created_items`, `_removed_items`, `_updated_categories`, `_category_items_removed`) as golden files. | Report with file:line; harness runs 0 tests green; **decision A-D1** answered (region-side proxy vs Robust-hosted, §4). | 30 |
| A1 | **Fetch surface.** `GET /item/<id>`, `GET /category/<id>`, `/children?depth=n`, `/children?depth=*&children=…` (subset), `/categories`, `/links`, `/category/current/links` (COF alias), `/orphans`. Links are a separate collection, never items. | Harness green on all fetch routes against a fake `IInventoryService` and against a real region for Truly. | 45 |
| A2 | **Item/category mutations.** `PATCH /item`, `PATCH /category` (name, desc, thumbnail, sale info), `DELETE /item`, `DELETE /category` (folder + descendents), `tid` echo, per-operation version-bump rule (parent bump on item ops, self+parent on moves), delta envelopes. | Harness asserts exact delta sets and version numbers per op. | 40 |
| A3 | **SlamFolder + create.** `PUT /category/<id>/links?tid=` atomic replace-all-links under the folder lock (all-or-nothing, proven by a fault-injection test), `POST /category/<id>/children` creating items/categories/links. | Fault-injection test: failure mid-slam leaves the COF unchanged; LL viewer on the test region changes outfit and it persists across relogin. | 40 |
| A4 | **Purge, library copy, simulate.** `DELETE /category/<id>/children` (Empty Trash / Lost and Found), `COPY /category/<src>?tid=` (CopyLibraryCategory), `simulate` dry-run on mutations, HTTP status codes the viewer branches on. | Harness green; Empty Trash works in the LL viewer. | 35 |
| A5 | **Advertise.** `InventoryAPIv3` in the caps seed **only** when `[AIS] Enabled` — through `validCaps`, not just the flag switch (RECON-01 rule). Firestorm-on-cap behaviour check (RECON-02 UNVERIFIED). | Cap absent by default; present on the test region; Firestorm on that region runs inventory through AIS without regressions on a scripted checklist. | 25 |

AIS lane total: **~3.6 h CC, 6 sessions.** Verify loops of yours: A3 (LL viewer outfit change), A5 (Firestorm checklist).

## 3. Interleave

```
week-view (CC sessions, left→right; ≈ your verify loop)

AIS :  A0 ──► A1 ──► A2 ──► A3≈ ──► A4 ──► A5≈ ─────────────┐
                                                             ├──► J1 joint soak ≈
SSB :  S0a ──► S0b≈ ──► S1≈ ──► S2 ──► S3≈ ──► S4≈ ──► S5≈ ─┘
                                                     ▲
                        A3 must land before S5 ──────┘         S6 (web viewer) any time after S3
```

Rules:
- **Alternate, don't overlap.** Two worktrees, but one CC session at a time, so a bug is attributable to one session. Order that respects the dependencies: `S0a, A0, S0b, A1, S1, A2, S2, A3, S3, A4, S4, A5, S5, S6, J1`.
- Both lanes gated on their region flags; both default off in every shipped ini.
- **J1 — joint soak (T+W, ~35 min):** replaces SSB S7 and an AIS soak. Test region with both flags on: LL viewer logs in textured, changes outfit → SlamFolder → `UpdateAvatarAppearance` → rebake → `AvatarAppearance` with new `CofVersion`; Firestorm on the same region does the same; web viewer consumes. Flags off → all three revert cleanly. Harness diffs ≤ threshold on all three test avatars' outfits.

## 4. Decisions this plan adds

| ID | Decision | Recommendation |
|---|---|---|
| A-D1 | AIS hosting: region-side caps module translating to `IInventoryService` (Phase 1) vs Robust-hosted service with a per-agent tokenized URL (Mike's "inventory out of the simulator") | **Phase 1 region-side, behind an interface so Phase 2 can lift the same handler onto a Robust connector.** Region-side gets auth free from the caps seed and needs no new wire trust; the translation layer is identical either way. Phase 2 is a single session later, not a redesign. |
| A-D2 | Merge order | AIS branch merges first (owns folder-version semantics); SSB rebases on it before S5. |
| A-D3 | Shared test region | Transylvania (D-5), both flags on. |

## 5. Timeline answer

| | CC wall-clock | Sessions | Your verify loops |
|---|---|---|---|
| SSB alone | ~4.75 h | 9 | 5 |
| AIS alone | ~3.6 h | 6 | 2 |
| **Both, interleaved** | **~8.5 h** (S7 folded into J1) | **15** | **~7** |

At the web-viewer cadence (12 sessions over two working days, verify loops between), that is **three working days**, four if the golden fixtures (Q-7) or the wipe-loop check (Q-3) turn up work. Running the two lanes strictly one-at-a-time costs nothing in CC time versus running them concurrently — the CC minutes are the same — and keeps bugs attributable, which is what made the web-viewer sessions cheap to verify.
