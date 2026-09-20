# RECON Addendum — Server-Side Baking (SSB) for NGC-Tranquillity

**Programme:** Track L, item L-2 (BUILD-PLAN-sl-parity-v2)
**Supplements:** `RECON-ssb-appearance.md` (Claude Code recon, delivered 2026-09-02 to `D:\_TO_REVIEW\ssb-appearance\`)
**Tree pin:** the parity audit's findings are pinned to `645b0f3`; live grid runs `cb141dd61d` + `db7c746248` (maptile fix). Nothing appearance-related has changed between those commits as far as chat history shows — **VERIFY at S0** (Build Plan).
**Date:** 2026-09-03

## 1. Why an addendum

The CC recon was written before the web-viewer Sessions 11 and 12 and the appearance wire spike. Those three pieces of work changed the SSB picture materially:

1. A **working, data-driven bake compositor now exists in C# on .NET 10** — `gateway/src/Gateway/Baking/` in `D:\web-viewer`. It interprets `avatar_lad.xml` layer sets with the viewer's `LLTexLayerSet` semantics, has a fidelity gate, and has been compared against Firestorm bakes on real avatars. The recon's "port BakeLayer.cs onto SkiaSharp+CoreJ2K" recommendation is therefore **already ~done, in the wrong repo**. SSB on the sim is no longer a compositor project; it is a *plumbing* project plus a *library extraction*.
2. **LibreMetaverse 3.1.4's `Baker` is disqualified** as a backend for anything that persists (decompile-confirmed: tiles sub-1024 layers into a 2×2 mosaic; earlier: skips layers). The recon's `IBakeBackend` seam stays, but "managed baker as default" now means *our* compositor, not LibreMetaverse's.
3. The wire spike established what the **sim already delivers** with zero grid changes: other avatars' baked-texture UUIDs (5 legacy slots) in `AvatarAppearance`, fetchable as ordinary assets; `VisualParams` present; `AppearanceData` block still omitted (count 0). The only reason a passive client stays a cloud is that nothing bakes for it.

## 2. State of the tree — appearance surface

Verdicts carry a source tag. `[P3]` = RECON-03 of the parity audit at `645b0f3`; `[WS]` = wire spike 2026-09-02, live grid; `[S11/S12]` = web-viewer sessions; `[UNVERIFIED]` = needs the S0 grep pass before any code is written.

| Element | State | Source |
|---|---|---|
| `RegionProtocols` in `RegionHandshake` | `1UL << 63` only; bit 0 (server bake) clear | [P3] |
| `UpdateAvatarAppearance` cap | absent | [P3] |
| `AgentSetAppearance` UDP handler | present (Firestorm/client-bake path) | [P3] |
| `UploadBakedTexture` cap | present (Firestorm path) | [P3] |
| `AvatarAppearance` → `AppearanceData` block | count 0 (`LLClientView.cs:4521` at 645b0f3) | [P3] [WS] |
| `AvatarAppearance` → `AppearanceHover` | 1 block, hover Z | [P3] |
| `AvatarAppearance` → baked TE UUIDs for others | present, 5 legacy slots; BoM aux slots not observed | [WS] |
| `VisualParams` on the wire | present | [WS] |
| `agent_appearance_service` in login response | absent | [P3] |
| Baked-texture service (`texture/<agent>/<channel>/<uuid>`) | absent | [P3] |
| `[BakedTextureService]` / XBakes-style store | present in stock OpenSim; **Tranquillity status UNVERIFIED** | [UNVERIFIED] |
| Avatar service persistence of textures | persists wearables + params; sim asks each login to rebake, so bakes are *not* durably persisted | [WS] |
| `AgentCachedTexture` handler | present (stock) | [UNVERIFIED] |
| `Client_OnAvatarNowWearing` wipe-loop fix | fixed on Legion Dec-2025 tree; **port status on Tranquillity UNVERIFIED** | [UNVERIFIED] |
| COF folder `Version` increments on link add/remove | stock OpenSim behaviour; **verify the inventory service actually bumps it on the UDP link path** | [UNVERIFIED] |
| Inventory API v3 (AIS) | absent (`OpenSim.Services.AISv3` is an empty template) | [P2] |

## 3. Viewer contract (stock LL viewer @ `62033f2`)

Reproduced from RECON-03 §3.1 because every design choice below hangs off it.

| # | Rule | Evidence |
|---|---|---|
| V1 | Viewer chooses server bake iff `RegionHandshake.RegionProtocols & 1` | `llviewerregion.cpp:3097` |
| V2 | Client-side bake path (`UploadBakedTexture` → `AgentSetAppearance`) has **no callers** — code retained, dead | `sendAppearanceMessage` only at definition |
| V3 | After every COF change the viewer POSTs `UpdateAvatarAppearance` with `{cof_version}` and expects `{success, expected, error}` | `llappearancemgr.cpp:2572, 3865–3882`, `requestServerAppearanceUpdateCoro` |
| V4 | The viewer **drops its own** `AvatarAppearance` as "Stale appearance" unless `AppearanceData.CofVersion` > last received; a message with **no** `AppearanceData` block is rejected for self | `llvoavatar.cpp:9779–9800` |
| V5 | `AppearanceVersion` forced to 1 when server bakes | `llvoavatar.cpp:9727–9737` |
| V6 | Other avatars' bakes are fetched from `<agent_appearance_service>texture/<avatar>/<channel>/<uuid>`; empty service URL → `""` + warning, avatar never textures | `LLVOAvatar::getImageURL` |
| V7 | Outfit *changes* (wear/take-off/replace outfit, empty trash) are AIS-only in the LL viewer | RECON-02 |

Consequence of V7: **SSB without AIS gives the LL viewer "log in as yourself, can't change clothes."** That is exactly the tier John accepted for the web viewer on 2026-09-02, so SSB is shippable ahead of AIS. See Ledger D-1.

## 4. Web-viewer facts that constrain the grid design

| Fact | Implication for SSB |
|---|---|
| Gateway must be **appearance-passive** by default (S3 hotfix e881646) — sending appearance from a partial wearables fetch corrupted stored looks | The sim's SSB must never depend on the *client* sending anything appearance-shaped; login-time bake is server-initiated |
| Gateway compositor is data-driven from `avatar_lad.xml`, read at runtime from the LibreMetaverse NuGet, not copied into the tree | Grid-side library must ship or reference `avatar_lad.xml` explicitly — Ledger Q-2 |
| Fidelity gate (S12): compositor refuses unsupported wearable types, multi-wearables, and the 5 BoM extra slots → `"unsupported"`, no bake persisted | Gate is correct in the gateway (its bake persists for all viewers). On the sim, for an LL viewer, refusing means a permanent cloud — different policy needed. Ledger D-3 |
| Ruth2 v4 / Roth2 v2 body meshes are AGPL/CC — rendering-side only | Irrelevant to the sim; bakes are body-agnostic |
| Gateway's bake is persisted by the sim and seen by every viewer | Once SSB is live on a region, the gateway must **stop baking there** and consume the sim's bakes (S6 in the Build Plan) |
| Stock-Library outfit on Truly is the clean reference; Firestorm bakes of it are the golden images | Golden fixtures for the shared library's test harness come from this — his step, still pending |

## 5. Halcyon reference — what to take, what not

From `/d/halcyon-reference-fresh/` (read-only). The recon recommended "Halcyon's persistent-bake rule". Restating it precisely so it is not over-applied:

- **Take:** bakes are first-class persisted assets tied to the avatar record; a login does not force a rebake if the stored bakes match the stored wearables; a change to wearables/params invalidates them.
- **Take:** hash-of-inputs as the invalidation key (wearable asset IDs + visual params + texture IDs per bake channel), so the compositor is skipped when nothing changed.
- **Do not take:** Halcyon's client-driven bake upload path — Halcyon still had the viewer composite. The *compute* moves to the sim here; only the persistence rule is Halcyon-lineage.
- **Do not take:** any Halcyon wire message; the LL viewer contract in §3 is the spec.

## 6. Items the CC recon should be re-checked on at S0

- Whether Tranquillity carries `OpenSim.Services.BakedTextureService`/`XBakes` at all, and if so whether it is region-side or Robust-side (affects ADR-002).
- Whether `Client_OnAvatarNowWearing` starts from an empty `AvatarAppearance` (the Legion wipe-loop bug) — must be fixed *before* any server-initiated bake touches stored wearables.
- Exact J2K encode path available in the tree (OpenJPEG via OpenMetaverse vs CoreJ2K/CSJ2K) — decides whether the shared library carries its own encoder dependency.
- Whether `SendAppearance` at HEAD still writes `AppearanceData` count 0 (line moved since 645b0f3?).
