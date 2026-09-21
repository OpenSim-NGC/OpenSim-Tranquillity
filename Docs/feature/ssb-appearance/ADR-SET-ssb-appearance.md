# ADR Set — Server-Side Baking

Status legend: **Proposed** = needs John's ruling · **Accepted** = ruled · **Carried** = ruled earlier (recon 2026-09-02), restated for the record.

---

## ADR-001 — Viewer contract is the stock LL viewer; add-only

**Status:** Carried (D-5 of the parity audit; viewer-compatibility policy 2026-08-31)
**Decision:** The wire behaviour in RECON addendum §3 (V1–V7) is the spec. No UDP appearance handler (`AgentSetAppearance`, `UploadBakedTexture`, `AgentCachedTexture`, `AvatarNowWearing`) is removed or altered in behaviour. All new behaviour is gated by `[Appearance] ServerSideBaking` per region.
**Consequences:** Firestorm client-bake keeps working on flag-off regions forever. Two code paths coexist in `SendAppearance` (with/without `AppearanceData`), selected per avatar by whether the sim baked it.

---

## ADR-002 — Bake compute in the region; appearance service and reaper on Robust

**Status:** Carried (D-2 "Robust-route")
**Decision:** Composition runs in-process in the region module that owns the ScenePresence (it has the params, the TE, and the `AvatarAppearance` sender). The **read** path viewers use for other avatars' bakes (`agent_appearance_service` → `texture/<agent>/<channel>/<uuid>`) is a Robust HTTP handler that resolves the channel to the stored asset UUID via the avatar service and streams the asset. The expiry reaper is a Robust-side timer.
**Alternatives rejected:** (a) Bake on Robust — needs a second copy of appearance state and a new region→Robust bake RPC; more moving parts for no fidelity gain. (b) Serve bakes from the region's HTTP server — regions come and go; the URL in the login response must be stable across teleports.
**Consequences:** Standalone mode: the same handler registers on the standalone's HTTP server (Robust connectors are hosted in-process there already). `IBakeBackend` seam retained so compute *could* move out-of-process later (LL utility, GPU box) without touching the module.

---

## ADR-003 — Shared compositor library, extracted from the web-viewer gateway

**Status:** Proposed
**Decision (recommended):** Create `OpenSimNGC.Appearance.Baking` as a project in the Tranquillity tree under `Source/` (not `Addons/`, because Robust never loads it but the library is core infrastructure, not an optional module), targeting `net10.0`, dependencies SkiaSharp (already in tree since #130) + the tree's existing J2K encoder. Ship it to NuGet as `OpenSimNGC.Appearance.Baking` alongside the other NGC packages so the web-viewer gateway consumes it by package reference, not path.
**Alternative A:** Keep the compositor in the gateway and copy it into Tranquillity. Rejected: two copies, two harnesses, drift within a week.
**Alternative B:** Publish from a third repo. Rejected: one more repo for one library; the gateway already depends on NGC packages.
**Why proposed, not accepted:** publishing a new NGC package is Mike's call as ecosystem maintainer; John should raise it with him before S0b. Until then the gateway can use a local `ProjectReference` to `/d/tranquillity-develop/Source/OpenSimNGC.Appearance.Baking`.
**Consequences:** The library carries the golden-fixture test project; both consumers get the tests. The gateway's `gateway/src/Gateway/Baking/` is deleted, not deprecated.

---

## ADR-004 — Bake persistence: assets with a bake marker, index in the avatar service, supersede-immediately + TTL reaper

**Status:** Accepted; index, hash skip and supersede **implemented in S2** (the TTL reaper is still Proposed)
**Decision:** Bakes are stored through `IAssetService` as texture assets. Marker (recommended): `AssetBase.Flags |= AssetFlags.Collectable` is *not* used (it means "temp" in stock code paths and gets purged wrongly); instead the asset **name** is `bake:<agent>:<channel>` and `Description` carries the input hash, and the authoritative index is the avatar-service key set (`Bake:<channel>`, `BakeHash:<channel>`, `BakeCOFVersion`, `BakeSize`, `BakeUpdated`). Supersede = delete the previous asset for that channel synchronously after the new one is confirmed stored. TTL reaper walks avatar-service records whose `BakeUpdated` is older than `BakeTTLDays` and whose presence record shows no login since, deletes assets, clears keys.
**Alternatives rejected:** (a) New `bakes` table — schema change in three DB backends for something the key/value Avatars table already expresses. (b) Never expire — violates D-6. (c) `AssetFlags` marker — see above; also not indexed.
**Consequences:** No migration. Grid owners on plain OpenSim never see any of this. A grid with the reaper off (default standalone) grows only until supersede, i.e. ≤11 bakes per avatar.

**As built (S2).** The index is read and written through the three calls `IAvatarService` already has — `GetAvatar`
(every key), `SetItems` (one batched write per bake) and `RemoveItems` — which exist on both the local service and the
Robust connector, so no service change and no schema change were needed; the longest key, `BakeHash:LeftArm`, is 16
characters against the table's `Name varchar(32)`. The skip is per channel and runs *before* any texture is fetched: the
hash is computed from the wearables alone, so a reused channel costs no asset fetch, no J2K decode, no composite, no
encode and no store. A channel is reused only when the stored hash matches **and** the stored asset still resolves — a
hash whose asset has vanished is never trusted. `BakeSize` is compared as well as being folded into the hash. Supersede
deletes the previous asset only after the new one is confirmed stored and the face has moved to it, and never an asset
any baked face still points at. One thing the design did not anticipate: any appearance save wipes the whole index,
because `AvatarService.SetAvatar` deletes every row for the principal first — Ledger Q-14.

---

## ADR-005 — Fidelity policy on the sim: best-effort with a structured report; refusal only for corrupt input

**Status:** Proposed (D-3)
**Decision (recommended):** Unlike the gateway (which refuses anything it cannot reproduce faithfully because its bake would replace a Firestorm bake), the sim on a bit-0 region **is** the only baker for LL viewers, so refusing means a permanent cloud. Policy: bake what is supported; skip unsupported layers; emit a fidelity report (INFO log + `[Appearance] FidelityReportPath` optional JSONL); the cap response is `success:true`. Refuse (`success:false`, `error`) only when inputs are unparseable or a texture fetch fails after retry — and in that case do **not** overwrite an existing good bake.
**Alternative:** Mirror the gateway's strict gate. Rejected for LL viewers; but see consequences.
**Consequences:** Firestorm users on a bit-0 region get the sim's best-effort bake instead of their own. The harness numbers (S0) must show the compositor at or above Firestorm's output on the stock-Library reference *before* any region flips the flag. If John rules strict, the flag stays off on Legion Grid until unsupported types are zero.

---

## ADR-006 — COF version source is the inventory folder `Version`; no AIS dependency

**Status:** Proposed
**Decision (recommended):** The sim reads the COF folder's `Version` from the inventory service and treats it as `cof_version`. AIS v3, when built, updates the same field, so nothing changes later. Anti-livelock rule per Design Brief §4.3.
**Alternative:** Block SSB on AIS (BP-v2 order). Rejected because the LL viewer's "log in as yourself" tier needs only the login-time bake, and the web viewer needs SSB now.
**Consequences:** S0 must verify the UDP link-create/delete path bumps `Version`. If it does not, that fix is a prerequisite slice, not a reason to wait for AIS.

---

## ADR-007 — `avatar_lad.xml` ships with the library

**Status:** Proposed (Q-2)
**Decision (recommended):** Vendor `avatar_lad.xml` into the library as an embedded resource, with a `THIRD-PARTY-NOTICES` entry naming its origin (Linden Lab viewer, LGPL 2.1 with the viewer's linking exception). The gateway currently reads it out of the LibreMetaverse package at runtime — that coupling ends with the extraction. **Extended (S0b/S0d):** the same rule covers the 56 parameter-mask / base-image TGAs the `layer_set` definitions name (`Data/character/*.tga`); the compositor cannot draw a clothing layer without them. Provenance rule for every vendored file: origin = the LL viewer tree, version recorded (`VIEWER_VERSION.txt` 26.1.1), SHA-256 listed in `THIRD-PARTY-NOTICES.md`, byte-identical to that tree (verified in S0d for all 56 masks and `avatar_lad.xml`). A file that is not in the viewer tree is not vendored (`head_wrinkles_highlights_alpha.tga`, referenced only by a bump-pass layer that is never rendered).
**Alternative:** Reference it from LibreMetaverse in both consumers. Rejected: the sim does not otherwise depend on LibreMetaverse, and a bake compositor must not change behaviour because a client library updated.
**Consequences:** One provenance line in Ledger Q-2 closes; the [[avatar-character-system]] Q-3 provenance question (system body mesh) is unaffected and stays open there.

---

## ADR-008 — Bake size 1024, parameterised

**Status:** Accepted, revised 2026-09-03 by measurement (supersedes the 512 default carried from D-7)
**Decision:** Sim default **1024** px per channel; `[Appearance] BakeSize` accepts 512, 1024 or 2048. Hash includes size so a config change invalidates stored bakes on next login rather than serving mixed sizes.

**Why 1024 and not the original 512.** Both reference sets were run at all three sizes against the LL
compositor references; the bake-size sweep in the golden harness reproduces the measurement. 1024 is the knee: every channel improves from 512 to 1024, and on
the richer of the two outfits the improvement is large — Aleric's lower channel goes from mean abs RGB **2.29**
at 512 to **0.89** at 1024, the single worst number in the matrix and the only one that would have failed a
tighter threshold. Going on to 2048 buys nothing: five of the nine channel rows get *worse*, none improves
materially, and the encoded bytes rise ~1.8×. 512 costs about 0.69× of 1024's bytes, which does not pay for a
visible loss on a busy outfit.

The references themselves are 2048 for head, upper, lower and hair, and 512 for eyes, on both avatars — so
1024 is also below the reference resolution everywhere except eyes, and the diff numbers already account for
that by resampling both images to the compared size.

**Consequences:** No config change is needed anywhere: S1 already shipped `BakeSize = 1024` in
`OpenSimDefaults.ini` and the live region server has been running 1024 since the 2026-09-03 deploy. This ADR
now records what is actually running. 512 remains available for operators who want the smaller assets and
accept the loss; 2048 remains available but is not recommended.

---

## ADR-009 — Gateway is a consumer on SSB regions

**Status:** Accepted, **implemented in S6(b)** (web-viewer `7a31412b54`)
**Decision:** On `RegionHandshake` with bit 0 set, the gateway session switches to `server` appearance mode: never bakes, accepts `AppearanceData`-bearing `AvatarAppearance` for self, fetches bakes via the existing asset route. On bit-0-clear regions the S11/S12 path stays. Detected per region, re-evaluated on every teleport.
**Consequences:** Web-viewer G6 ("standalone, no grid reliance") holds — the gateway degrades gracefully to its own baker off-NGC. On Legion Grid the corruption hazard that caused e881646 disappears entirely for SSB regions, because the gateway sends nothing.

**As built (S6(b)).** Five things the implementation settled that the decision above left open:

1. **The mode is a type, not a flag.** `ServerAppearance` implements `IAppearanceMode` but **not** `IBakeSteps`, and `AppearanceBaker.RunAsync` takes an `IBakeSteps`. There is therefore no send step to reach in server mode — the guarantee is the absence of the code, not a branch inside it. The S6(a) refactor that moved the whole S11/S12 pipeline into `ClientBakeAppearance` exists for this reason.
2. **Detection is `Simulator.Protocols` at every `SimConnected`**, login and teleport landing alike, which is the viewer's own test — `llviewerregion.cpp:3083` computes `mCentralBakeVersion = region_protocols & 1` from the handshake. Every choice is logged with the region name and the protocols word.
3. **Handover aborts a bake in flight before it can send.** `AppearanceBaker` now gates the send on the cancellation token explicitly, because a CPU-bound step need not observe it. A send already made stands — it is on the wire, and the simulator overwrites it in its own time, which is exactly what the S6 pre-verify observed.
4. **`AppearanceVersion == 0` is refused in server mode.** LibreMetaverse leaves both versions at 0 when the packet carried no `AppearanceData` block, so 0 means the simulator declared no server bake. The viewer's own resolver is more forgiving (`llvoavatar.cpp:9663-9690` substitutes 1 when neither the field nor the 11000 param is set); the stricter rule is the gateway's, because in server mode it has no bake of its own to fall back on.
5. **Two routes to a bake, asset first.** The existing asset route is tried first; on an empty answer the gateway retries the grid's appearance service at the URL a viewer would build — `<service>texture/<agent>/<channel>/<uuid>`, the channel as its **name** (`llvoavatar.cpp:5912` from `mDefaultImageName`), the service from the login response's `agent_appearance_service` (`llstartup.cpp:4047-4051`). Which route served is logged.

**Deferred, deliberately:** the gateway does not POST `UpdateAvatarAppearance`. That cap is how a viewer declares a COF change *it made*, and the gateway has no outfit-change UI, so it has nothing to declare. When one is added the POST belongs with it, and §4.3's handshake already answers it.
