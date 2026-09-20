# Build Plan — Server-Side Baking (L-2)

**Status:** IN PROGRESS — S0a–S1 done and deployed; S2 next. Still gated on Ledger D-1/D-3 + ADR-003 for the later sessions. **Date:** 2026-09-03 (S1-close)
**Estimating convention:** wall-clock Claude Code minutes per session, anchored to measured web-viewer sessions (S4 21 min, S10 20 min, S6 28 min, S11 39 min, S9 55 min). A session past ~2× its estimate is stuck: stop it, report, re-scope. One feature per session.
**Branch:** `feature/ssb-appearance` off `develop` HEAD (`cb141dd61d` + maptile fix). Commit locally at each session's DoD; push is John's call.
**Repos:** T = `/d/tranquillity-develop` (worktree for this branch — see §0), W = `D:\web-viewer`.

## 0. Setup (John, ~3 min, no CC)

```bash
cd /d/tranquillity-develop
git fetch --all
git worktree add /d/tranq-ssb -b feature/ssb-appearance develop
cd /d/tranq-ssb
mkdir -p Docs/feature/ssb-appearance
cp /d/_TO_REVIEW/ssb-appearance/*.md Docs/feature/ssb-appearance/
# then copy the five docs from this delivery into the same folder
git add Docs/feature/ssb-appearance
git commit -m "docs(ssb): recon + addendum, design brief, ADR set, build plan, ledger"
```

**Needs your attention:** the `cp` from `_TO_REVIEW` assumes the CC recon's filenames are `.md` at the top level of that folder — check `ls /d/_TO_REVIEW/ssb-appearance/` first.

## 1. Session table

| S | Repo | Feature | DoD (harness green, not "looks right") | Est. min | Verify loop (yours) |
|---|---|---|---|---|---|
| **S0a** ✅ | T | **Verification grep pass + library project skeleton.** Resolve every `[UNVERIFIED]` in the RECON addendum §2/§6 with file:line at HEAD; create `Source/OpenSimNGC.Appearance.Baking` (net10.0, SkiaSharp, tree's J2K encoder, embedded `avatar_lad.xml` per ADR-007) with an empty public API + test project. | Report table with file:line for all 6 items; solution builds; `dotnet test` runs 0 tests green. **Done:** `29105ccc44`, `7dbc092d2e`. | 25 → **?** | none |
| **S0b** ✅ | W→T | **Extract compositor.** Move `gateway/src/Gateway/Baking/` into the library; port its existing unit tests; golden-fixture harness: given a wearables+params fixture and Firestorm's bake assets for Truly's stock-Library outfit, pixel-diff per channel with a threshold (report SSIM/abs-diff per channel). Gateway switches to `ProjectReference` (NuGet later, ADR-003) and its `Baking/` dir is deleted. | Library tests green; gateway builds and its 9 S11 tests + S12 tests still green against the library; diff numbers printed for 6 legacy channels. **Done:** `303d2b39c1`, `8a245aa286`, `cbf3284e06`, `e3b969c9ab` (T); `be67e2d` (W). | 40 → **34** | **You:** produce the golden fixtures — log Truly in via Firestorm on the stock outfit, note the 6 bake UUIDs from Appearance debug (or from `AvatarAppearance` via the harness), pull the assets. This is the step that can't be automated and blocks S0b's last third. |
| **S1** ✅ | T | **Bake orchestrator + console trigger.** `AppearanceBakeModule` (Addons or OptionalModules per S0a finding): COF → wearables → textures → 11-channel composite via library → store assets → update ScenePresence TE → `SendAppearance` to all. Trigger: console `appearance bake <first> <last>`. No cap, no flag, no persistence keys yet. | Console command on Ebony bakes Truly; Firestorm observer (you) sees the sim's bake replace hers; harness diff of the stored assets vs goldens ≤ threshold. **Done:** `bbc065bc5f`, `99118ea1ab`; deployed to the live region server 2026-09-03 19:14 and verified in-world 19:46 (first server-composited avatar on the grid). **DoD closed in S2 Part 0** (commit `test(ssb): close S1 DoD — golden diff at the shipped bake size`): the caveat was that the harness-diff clause had been met only at 512 while the bakes stored on the live sim were made at 1024. The gate now bakes at the shipped `[Appearance] BakeSize` (1024, ADR-008) — `truly-stock/manifest.json` `bakeSize` raised 512 → 1024, `aleric-max` was already 1024 — and both sets pass every threshold against the 2048/512 references. Worst numbers at 1024: mean |dRGB| 1.25 (truly upper) against 4.0, pctRGB>8 0.75% against 5%, mean |dA| 1.00 against 2.0, mean |dM| 1.00 against 4.0, pctM>8 1.59% (aleric upper) against 5%. Every RGB mean is *better* at 1024 than it was at 512. | 35 → **15** | Firestorm side-by-side, 1 loop. |
| **S2** (part) | T | **Persistence + supersede + reaper** (ADR-004). Avatar-service keys, per-channel input hash, skip-compute on match, synchronous supersede-delete, Robust reaper with `BakeTTLDays`, off by default. | Unit tests: hash stability, supersede deletes old UUID, reaper deletes only past-TTL-and-not-logged-in; console bake twice → second run logs "reused 11/11". **Done for the index, the skip and supersede:** keys read/written through the existing `GetAvatar`/`SetItems`/`RemoveItems` (no service change, no schema change); reuse is per channel and decided before any texture is fetched; a stored hash whose asset has vanished is not trusted; `BakeSize` invalidates; supersede deletes only after the new asset is stored and never something a face points at. The one-line summary now ends `reused N/M`. **Still open in S2:** the Robust TTL reaper with `BakeTTLDays`. New finding: Ledger Q-14 (any appearance save wipes the index). Cost instrumentation and Q-10's answer landed here too. | 30 | none |
| **S3** ✅ | T | **Wire: flag, bit 0, `AppearanceData`, cap.** `[Appearance] ServerSideBaking` per region; `RegionProtocols |= 1` when set; `SendAppearance` emits `AppearanceData{1, CofVersion}` for sim-baked avatars only; `UpdateAvatarAppearance` cap with the §4.3 handshake + anti-livelock; login-time bake trigger on `MakeRootAgent`. Flag stays **false** in every shipped ini. | Unit tests for the handshake (equal / less / greater / livelock cap); with the flag on for a **test region only**, LL viewer logs in and is textured to itself; Firestorm on the same region POSTs and is textured. **Done:** `1e78b9a706` (Part 0, Q-14), `7554bf9b51` (Part 1); deployed to the live region 2026-09-04 at `1.1.216-alpha+7554bf9b51`, flag left false. Handshake tests cover all four branches plus a throwing re-read, window expiry, per-agent counters and the clear on success; the flag resolves per region in five configurations; the ADR-001 gate asserts a flag-off region's `AvatarAppearance` body is byte-identical to the pre-S3 form at three sizes. `cof_version` proven identical to AIS's folder version. **The DoD's two live clauses are not met and cannot be met by this session** — they need the flag on and a viewer in-world, which is John's loop. | 40 → **?** | **You:** stock LL viewer + Firestorm on the test region, 1 loop each. First moment the LL viewer isn't a cloud. **Also settles Q-6**, which gates S5's shape. |
| **S4** ✅ | T | **Appearance service on Robust** (ADR-002). `agent_appearance_service` in the login response; `GET texture/<agent>/<channel>/<uuid>` resolving via avatar-service keys and streaming the asset; standalone registration. | curl the URL for Truly's `head` returns the J2K bytes; LL viewer sees **other** avatars textured on the test region. **Done:** `59b12538b9` (handler), `51194ac754` (login response); deployed to both roots 2026-09-05 at `1.1.219-alpha+51194ac754`, with the connector and the URL left unconfigured. Channel token established from `llvoavatar.cpp:5912` (a name — `head`, `upper`, `lower`, `eyes`, `skirt`, `hair`, `leftarm`, `leftleg`, `aux1..3`), not assumed. 404 on every miss including a UUID that disagrees with the index. **The DoD's two clauses are John's loop and cannot be met here** — both need the service configured and a viewer in-world. | 30 → **?** | LL viewer observing Firestorm-and-sim-baked Legion, 1 loop. **Prerequisite for any flag flip**: S3's flip without S4 produced a correct cloud. |
| **S5** ✅ | T | **Change triggers + BoM aux channels.** Rebake on `AvatarNowWearing` (Firestorm on a bit-0 region) and on cap POST with a newer COF version; the 5 BoM aux channels produced when universal wearables are present. | Firestorm on the test region changes a shirt → new bake within one POST; fixture with a universal wearable yields 11 stored channels. **Done:** `d7ac58d187` (trigger), `9417a09402` (aux channels). Trigger is the save-completion event, not the arrival of the change (Q-16); both signal routes converge on it after the cap was changed to queue a save rather than bake on arrival; 2 s debounce sized against the 5 s save delay (the signal spread is unmeasured — Q-6). Aux channels exercised end to end with a synthetic Universal — composited, stored, faces 40-44, served by the Robust route. **The DoD's live clause is John's loop**; the aux clause is met by fixture, not by real content, and the fidelity gap is recorded rather than closed. | 30 → **?** | Firestorm outfit change, 1 loop. Watch for `reason=CofChanged` and one bake per change. |
| S6 | W | **Gateway SSB-aware mode** (ADR-009). Detect bit 0 per region; `server` appearance mode; accept `AppearanceData` for self; no bake path reachable on bit-0 regions (structural, like the S11 invariant). | Unit test proving the bake step is unreachable when bit 0 is set; live: Truly logs in via the web viewer on the test region and is textured with **zero** gateway bakes logged; on Transylvania (flag off) the S12 path still runs. | 25 | Web-viewer login on both regions, 1 loop. |
| S7 | T+W | **Soak + fidelity sign-off.** Harness against all three test avatars' outfits; 30-minute soak with LL viewer + Firestorm + web viewer on the test region; region restart with flag on → no rebake (persistence); flag off → Firestorm reverts, LL viewer clouds, nothing deleted. | All harness diffs ≤ threshold; no `AppearanceData` regressions on the flag-off region; report lists every unsupported layer seen. | 30 | Your call on flipping Ebony/Transylvania/Elm. |

**Total:** ~4.75 h CC across 9 sessions; 5 short verify loops of yours. Comparable to two web-viewer working days at the measured pace.

## 1a. Unplanned slices added mid-programme

Five sessions below were **not in the original table** — they were cut out of S0b/S1 when the work turned out to be
a separate concern, and one (T-1) was pure repo hygiene that S1 tripped over. They are listed here so the estimate
column above stays honest about what the programme actually cost.

| S | Feature | Commit(s) | Est. → actual (min) | Why it was added |
|---|---|---|---|---|
| **S0c** ✅ | `Client_OnAvatarNowWearing` merges instead of wiping unlisted wearable slots; 4 xunit tests | `a5e88d72f1` (now `11a2456833` on the deployed branch) | 20 → **9** | Ledger Q-3 / R-4 found the wipe bug present in S0a; it is a hard gate before any production flag flip, so it could not wait for S3. |
| **S0d** ✅ | 5th J2C component is the **morph mask**, not a bump pass; compositor + encoder + decoder | `30c82d472b` | 30 → **14** | Q-8 opened by S0b's golden diff: viewer bakes carry a component the server did not produce. Parity gap, had to close before S3. |
| **S0e** ✅ | Plain vs template layer semantics (`isUserSettable`); morph gather corrected to match the colour pass; doc renamed `MORPH-MASK-PASS.md` | `916dc35d00`, `22b3695389` (T); `0a6acff` (W) | 25 → **8** | S0d shipped on a wrong premise about per-instance layers; caught reading `lltexlayer.cpp` for the packing-order citation. |
| **A0** ✅ | AIS v3 spec + `AISv3Module` skeleton (separate worktree `D:\tranq-ais`) | 2 commits on `feature/ais-v3` | 30 → **21** | Track L's other half; sequenced after SSB per D-1 but started early because it is independent. |
| **T-1** ✅ | NUnit lifecycle hooks orphaned by the xunit migration — `CoreModules.Tests` 35 → 5 failing; `LindenCaps.Tests` restored to the solution | `c1fc7fff3e`, `d43f8cb362` | 30 → **18** | S0c reported 35 pre-existing `CoreModules.Tests` failures and flagged them as needing a separate owner; S1's test work needed a trustworthy baseline. |

## 1b. Estimate vs actual

| S | Est. | Actual | Note |
|---|---|---|---|
| S0a | 25 | ? | Not recorded at the time. |
| S0b | 40 | 34 | The one session that ran near estimate; the golden harness carried most of it. |
| S0c | 20 | 9 | Unplanned. |
| S0d | 30 | 14 | Unplanned. |
| S0e | 25 | 8 | Unplanned. |
| A0 | 30 | 21 | Unplanned (AIS worktree). |
| T-1 | 30 | 18 | Unplanned (repo hygiene). |
| S1 | 35 | 15 | Plus deploy and in-world verify. |

Across the seven sessions with a recorded actual, **estimates are running roughly 2:1 over actuals** (210 est. → 119 actual).
Two readings, and they are not exclusive: the estimating convention was anchored to web-viewer sessions that involved
more unknown-shape exploration than this programme has needed, and four of the seven were narrow slices carved out
of a session already scoped and understood. The ratio is **not** a reason to re-estimate S2–S7 downward: those
sessions carry the wire protocol, the cap handshake and the live flag flip, which is where the web-viewer sessions
overran too. Treat the "past ~2× its estimate means stuck" rule as unchanged.


## 2. Order and gates

```
S0a ──► S0b ──► S1 ──► S2 ──► S3 ──► S4 ──► S5 ──► S7
                 ▲                       │
   goldens (you) ┘                       └──► S6 (any time after S3)
```

Gates:
- **Before S0a:** D-1, D-3, ADR-003 ruled (Ledger).
- **Before S1:** golden fixtures exist (your step in S0b). If they lag, S1 can proceed and S0b's diff numbers land in S1's DoD instead.
- **Before S3:** S0b diff ≤ threshold on the stock outfit. This is the rule that keeps a worse-than-Firestorm bake from ever reaching a bit-0 region.
- **Before flipping any production region (after S7):** the wipe-loop check from S0a is resolved on Tranquillity.

## 3. Each session prompt carries

Per the standing prompt structure: the S0a grep results and file:line anchors; the library's public API; what *not* to read (no LibreMetaverse Baker, no Halcyon wire code, no `appearance-utility-bin`); test avatars Truly/Aleric only, never Legion; reporting contract (done / VERIFY-resolved with file:line / decisions needed). Prompts are written here per session, in a code block, when you say go.

## 4. Deploy notes

**Branch state as of 2026-09-03 (S1-close).** The deployed branch `fix/maptile-legacy-renderer` is at `11a2456833`
and now carries, besides the maptile fix itself (`db7c746248`), the **S0c wearable-wipe fix** (`11a2456833` — the
rebased form of `a5e88d72f1`) and the **T-1 fixture repair** (`c1fc7fff3e`, `d43f8cb362`). Both reached the live
region server with the S1 deploy. Both feature worktrees — `D:\tranq-ssb` (`feature/ssb-appearance`) and
`D:\tranq-ais` (`feature/ais-v3`) — are rebased onto `11a2456833`, so neither carries a stale copy of the
wipe fix or of the test fixtures. Nothing is pushed.

- Test region = one region only, flag on in its own ini section. Recommended: Transylvania (currently loads 0 objects anyway — separate issue — so nothing to disturb).
- Both servers down before deploy (your practice). Publish path is `bin\Release\net10.0\win-x64\publish\` per project (BUILDING.md is wrong on this; noted in [[repo-audit]]).
- Robust must be redeployed at S4 (appearance service) and S2 if the reaper is enabled; region-only for the rest.
