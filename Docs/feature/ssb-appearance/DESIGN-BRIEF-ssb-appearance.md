# Design Brief — Server-Side Baking for NGC-Tranquillity

**Programme:** Track L / L-2. **Status:** DRAFT for decision. **Date:** 2026-09-03
**Companion docs:** RECON (CC, 2026-09-02) + RECON addendum, ADR set, Build Plan, Ledger — all under `Docs/feature/ssb-appearance/`.

## 1. Problem

The stock LL viewer only bakes server-side (V1–V5). Tranquillity has no `UpdateAvatarAppearance` cap, no compositor, no appearance service, and sends no `AppearanceData` block. An LL-viewer user on a Tranquillity grid is a permanent cloud, and so is any appearance-passive client such as the web-viewer gateway. Firestorm masks this by baking client-side.

The web viewer has, in the meantime, built the hard part (a faithful compositor) in the wrong place: a per-client gateway whose bake the sim persists for everyone. That is both duplicated effort and a fidelity hazard. The correct home for baking on a grid that runs NGC code is the sim.

## 2. Goals

G1. Stock LL viewer (`--loginuri` pointed at Legion Grid) sees itself and others fully textured on an SSB-enabled region.
G2. Firestorm on the same region continues to work — with SSB (its SL codepath) when the region flag is on, with client bake when it is off.
G3. The web-viewer gateway becomes a pure consumer on SSB regions: no baking code runs for a session on a region advertising bit 0.
G4. One compositor, one test harness, shared by the gateway and the sim (`OpenSimNGC.Appearance.Baking`).
G5. Bakes persist across logins (Halcyon rule) and expire so they do not accumulate in an operator's asset store (D-6).
G6. Ordinary OpenSim grid owners can run the web viewer without any of this; SSB is an NGC-Tranquillity feature behind a per-region flag. Add-only: no legacy handler is removed.

## 3. Non-goals (this programme)

- Outfit changes from the LL viewer (that is AIS v3, L-1). SSB ships "log in as yourself" first.
- Bakes-on-Mesh **rendering** in the web viewer (rendering-side, [[web-viewer]] S13). The sim *does* produce the 5 BoM aux bakes (§6.4).
- Physics wearables, hover height, animesh, PBR overrides.
- The `appearance-utility-bin` (LL's GL-based baker). Kept only as a future `IBakeBackend` for exact-parity operators; not built.
- Bake sizes above 512 (D-7). The library is parameterised; the sim default is 512.

## 4. Architecture

```
                        +-------------------------------+
   LL viewer /          |  Region (Tranquillity sim)    |
   Firestorm(bit0) ---->|  AppearanceBakeModule         |         +---------------------------+
      POST UpdateAvatar |   - cap UpdateAvatarAppearance|         |  OpenSimNGC.Appearance.   |
        Appearance      |   - login-time bake trigger   |-------->|  Baking  (shared library) |
                        |   - COF/wearable resolver     |  in-proc|   - LLWearable parser     |
                        |   - IBakeBackend (in-proc)    |         |   - avatar_lad layer sets |
                        |   - BakeStore (persist/expire)|         |   - compositor (Skia)     |
                        |   - AvatarAppearance sender   |         |   - J2K encode            |
                        +-------+---------------+-------+         |   - fidelity report       |
                                |               |                  +------------+--------------+
                 assets (bakes) |               | avatar-service keys           ^
                                v               v                                |
                        +-------+-------+ +-----+------+                         |
                        | AssetService  | | Avatar Svc |                         |
                        +-------+-------+ +------------+                         |
                                |                                                |
                        +-------v----------------------+                         |
   LL viewer GET        | Robust: AppearanceService    |                         |
   texture/<agent>/     |  texture/<agent>/<ch>/<uuid> |                         |
   <channel>/<uuid> --->|  (proxy to AssetService)     |                         |
                        +------------------------------+                         |
                                                                                 |
   Web-viewer gateway (separate repository) ---- on non-SSB grids only ----------+
      on SSB regions: appearance-passive, consumes AvatarAppearance + asset route
```

### 4.1 Components

| # | Component | Repo / location | New or changed |
|---|---|---|---|
| C1 | `OpenSimNGC.Appearance.Baking` — shared compositor library | Tranquillity tree (placement: ADR-003) | **new project**, code lifted from the web-viewer gateway's `Gateway/Baking/` |
| C2 | `AppearanceBakeModule` — region module: orchestration, cap, triggers, sender | `Addons/` or `Source/OpenSim.Region.OptionalModules` (ADR-003) | new |
| C3 | `BakeStore` — persist bakes as assets, record channel→UUID + input hash + COF version in the avatar service, expiry reaper | region module + Robust reaper | new |
| C4 | `AppearanceServiceConnector` — Robust HTTP handler for `texture/<agent>/<channel>/<uuid>` and login-response `agent_appearance_service` | Robust | new (ADR-002) |
| C5 | `LLClientView.SendAppearance` — emit `AppearanceData{AppearanceVersion=1, CofVersion}` when the avatar is server-baked; unchanged otherwise | `OpenSim.Region.ClientStack.Linden.UDP` | changed, add-only |
| C6 | `RegionHandshake` — set bit 0 of `RegionProtocols` when `[Appearance] ServerSideBaking = true` | ClientStack | changed, flag-gated |
| C7 | Gateway SSB-aware mode | the web-viewer gateway, a separate repository | changed there, not here |

### 4.2 Bake pipeline (C2 → C1 → C3)

1. **Trigger** (one of): login/`MakeRootAgent` on an SSB region; `UpdateAvatarAppearance` POST; console `appearance bake <first> <last>`; wearables changed via legacy UDP path (`AvatarNowWearing`, Firestorm on a bit-0 region still sends it — VERIFY).
2. **Resolve inputs**: read the agent's COF folder from the inventory service → link items → wearable items → wearable assets (types 5/13) → parse `LLWearable` text → visual params + per-face texture UUIDs; collect `VisualParams` from `AvatarAppearance` as the authoritative param vector (the two must agree — mismatch is a fidelity report entry, not an error).
3. **Hash**: per bake channel, hash (wearable asset IDs, texture IDs, the subset of params that feed that channel's layer set, bake size). Compare with the avatar-service record. Unchanged → skip compute, reuse stored bake UUIDs, still send `AvatarAppearance`.
4. **Fetch** every referenced texture asset once; decode J2K.
5. **Composite** each of the 11 channels via C1 (head, upper, lower, eyes, skirt, hair, leftarm, leftleg, aux1–3; skirt/hair/aux only if the layer set has content).
6. **Encode** J2K, store as assets with the bake flag (ADR-004), record UUIDs + hash + COF version in the avatar service, update `AvatarAppearance.Texture` baked faces in the ScenePresence.
7. **Send** `AvatarAppearance` to self (with `AppearanceData`) and to everyone in view (with `AppearanceData` too — harmless for Firestorm, required for LL viewers observing).
8. **Report**: a structured fidelity report per bake (unsupported layers, missing textures, param/COF disagreement) goes to the log at INFO and to the cap response `error` field only when the bake was refused.

### 4.3 COF version handshake without AIS

The viewer's `cof_version` is the COF folder's inventory `Version`. The sim reads the same number from the inventory service and records it. Both are the same field with one writer — the data layer's folder-version bump — which S3 proved rather than assumed (`AisMutation.ReportVersion` and `ServerSideBakingModule.CofVersionOf` read the same `InventoryFolderBase.Version`).

Cap response:

- `cof_version == server's` → **accept**: `{success:true}`.
- `cof_version < server's` → `{success:false, expected:<server>}`; viewer re-requests.
- `cof_version > server's` → the viewer changed the COF through a path the sim hasn't seen yet; re-read the folder once, then respond as above. Never livelock: after N mismatches within T seconds, accept anyway at the server's version and log it (Ledger R-2).

**What `success:true` means (revised in S5).** It means *accepted — the bake will follow within the save cycle*. It does **not** mean "baked", which is what S3 shipped and what the first three bullets used to say.

S3 had the cap bake synchronously and answer afterwards. That is bake-on-arrival, which Q-16 rules out: the POST arrives before the region has resolved the new items to asset ids, and the appearance save that resolves them is up to `DelayBeforeAppearanceSave` (5 s) away. A bake at POST time composites wearables still carrying `UUID.Zero` and stores the result as if it were the new look.

**On the ordering of the two signals.** The 5 s save delay is a configured value and is the only interval here that is established. The spread between `AgentIsNowWearing` and the cap POST for one change is **not measured** — an earlier "310 ms" figure was written into these notes without a source and has been withdrawn (Ledger Q-6). It does not affect the design: S5 makes *both* routes queue an appearance save and bake off its completion, so the ordering between them stops mattering. It affects only the debounce, which is sized against the save delay instead.

So the cap now answers the handshake and queues an appearance save. The bake happens when that save completes, off `OnAvatarAppearanceChange` (§4.6). The legacy `AgentIsNowWearing` route already queued a save, so **both signals converge on one trigger with one ordering** — which matters because Q-6 established that both arrive, not one or the other.

Consequences worth stating:

- `BakeReason.Cap` is **no longer produced**. A cap-driven rebake surfaces as `CofChanged`, because that is what it is. The enum value remains valid.
- The `AvatarAppearance` the viewer is waiting for arrives after the save cycle rather than in step with the cap response. The viewer waits for it either way and does not re-request, so this is a latency change, not a protocol one.
- A change whose channels all hash the same still sends the appearance. Nothing is recomputed, but the message must go out: the viewer will not ask again.

Pre-AIS the LL viewer cannot change the COF, so in practice only the login case fires. Firestorm on a bit-0 region *can* change the COF via UDP and will POST; the path above handles it as long as the inventory service bumps `Version` on UDP link changes (S0 verification).

### 4.4 Persistence and expiry (G5)

- Bakes are assets. They carry a marker (ADR-004) so a reaper can find them.
- The avatar service holds, per agent: `Bake:<channel>` = asset UUID, `BakeHash:<channel>`, `BakeCOFVersion`, `BakeSize`, `BakeUpdated` (UTC). No schema change — the Avatars table is key/value.
- A bake is **superseded** when a new one for the same channel is stored; the old asset is deleted immediately (no reaper needed for the common case).
- A bake is **expired** by the reaper when `BakeUpdated` is older than `[Appearance] BakeTTLDays` (default 30) *and* the agent has not logged in since; expiry deletes the assets and clears the keys, so the next login rebakes. This is the "don't clog someone's database" rule (D-6) — the reaper is Robust-side, opt-in, off by default for standalone.
- Stored bakes are also what the web-viewer gateway and any observer fetch via the ordinary asset route, so no second copy is ever needed.

### 4.5 Rollout hazards (from BP-v2)

- `[Appearance] ServerSideBaking` is per region. Default `false`. Turn on for one test region with both viewers present before any other region.
- Flipping bit 0 makes Firestorm switch to `UpdateAvatarAppearance` immediately. If the compositor produces a worse bake than Firestorm would, every Firestorm user on that region degrades — which is why the fidelity harness (S0) precedes the flag (S3), not follows it.
- Turning the flag **off** again is safe: Firestorm reverts to client bake on next login; the LL viewer reverts to cloud; stored bakes are ignored, not deleted.

### 4.6 The AIS/COF seam (for S5)

AIS v3 is live on Ebony and the Current Outfit folder is the authoritative record of what an agent wears: an
outfit change is `SlamFolder` on the COF and a take-off is `DELETE /item` / `RemoveItem` (AIS ledger A10, which
corrected A7 on exactly this point). The bake does not read the COF. `ServerSideBakingModule` passes
`sp.Appearance.Wearables` to the orchestrator (`ServerSideBakingModule.cs:125`) — a deliberate S1 Part 1a choice,
made when nothing could change the COF behind the region's back. AIS changed that, and S5 owns the consequence.

#### Which store wins

Neither, as stated — the question is malformed, and getting it wrong is how S5 discovers this late.

- **The COF is authoritative about *membership*: which items are worn.** It is what the viewer reads back, what
  `cof_version` counts (§4.3), and what survives a relog.
- **`sp.Appearance.Wearables` is authoritative about *what a bake can be made from*.** The COF holds
  `AssetType.Link` rows (`AisEnvelope.cs:47`), so it names items, not assets; a bake needs asset ids and the
  wearable bodies behind them. The ScenePresence is the only place in the region where membership has already
  been resolved to assets.

So the rule for S5 is an ordering rule, not a precedence rule: **the COF decides *whether* to bake; the
ScenePresence decides *what* to bake; and the bake must not run until the ScenePresence has caught up with the
COF.** A bake that reads one store while the other has moved is not a merge conflict to resolve — it is simply
early.

#### What happens today if they disagree — the trace

**AIS never touches the ScenePresence.** The AIS surface is inventory-backend only by design (Ledger P-2;
`IAisInventoryBackend.cs:9`, `AisInventory.cs:22`): nothing under
`Source/OpenSim.Region.ClientStack.LindenCaps/AIS/` references `ScenePresence`, `AvatarAppearance` or
`AvatarFactory`. A `SlamFolder` that rewrites the COF leaves `sp.Appearance.Wearables` exactly as it was.

**The region learns from the viewer, over UDP, as a separate message.** The only assignment to
`Appearance.Wearables` anywhere in the tree is `AvatarFactoryModule.cs:1289`, inside `Client_OnAvatarNowWearing`
(`:1256`), which is raised by the `AgentIsNowWearing` packet (`LLClientView.cs:8431`, handler `:9229-9244`, event
`:86`). AIS writing the COF and the viewer sending `AgentIsNowWearing` are two independent messages with no
ordering guarantee between them.

**And when it arrives, the asset ids are not there yet.** `MergeNowWearing` (`:1315-1354`) fills a listed item's
`AssetID` from the *existing* contents of that slot (`GetAsset`, `:1345-1349`); an item that was not already in
the slot gets **`UUID.Zero`**. The real asset ids are resolved only in `SetAppearanceAssets` (`:901-947`, its live body; a long commented-out block follows), called
from `SaveAppearance` (`:888`), which runs on a thread pool behind the save queue — `DelayBeforeAppearanceSave`,
default **5 seconds** (`:51`, `:71`), on a 500 ms tick (`:155`).

**So the path is reliable but late, and there is a window.** Between `AgentIsNowWearing` and the queued save, the
newly worn item sits in `sp.Appearance.Wearables` with `AssetID == UUID.Zero`. A bake in that window reads it
through `BakeOrchestrator.ResolveWearables` (`BakeOrchestrator.cs:86-96`) as **worn but assetless** — a genuine
worn instance that contributes its layers' morph masks and no textures at all (the S1c/Q-12 rule, correct in its
own right and exactly wrong here). The avatar is baked wearing the *shape* of the new shirt and none of its
pixels. The window is at least the 5-second save delay, longer when the queue is busy, and unbounded at the front
because nothing bounds the gap between the AIS write and the UDP packet.

Two things already in place soften this and neither is sufficient. The input hash includes the wearable's asset
id (`BakeHash.cs:46`, `:58`), so a stale bake's hash differs from the correct one and a *later* bake will not
reuse it — but the stale bake has already been stored, its face already applied, and the previous good bake
already deleted by supersede. And `SetAppearanceAssets` drops an item whose inventory row is missing
(`AvatarFactoryModule.cs:936-939`), which is a different failure from this one.

#### The options for S5's trigger

**(a) Read the COF directly.** Authoritative, and independent of whether the viewer sends anything. Costs:
`GetFolderForType` plus a folder-content fetch per trigger — a Robust round trip on a grid — then link → item →
asset resolution for every worn item (the descendents cap already does this dance at
`FetchInvDescHandler.cs:436-455`), plus a dependency on the folder `Version` bump (Ledger Q-1: present at the
data layer, `MySQLXInventoryData.cs:162`, `:244`, `:277-278`, `:288`). It also puts a *second* wearable resolver
in the tree alongside `SetAppearanceAssets`, which is the same class of mistake as two lanes deploying from two
branches. Worse, it does not actually fix the disagreement: a bake made from COF-resolved assets writes faces
onto an appearance whose `Wearables` still say `UUID.Zero`, so the next save and the next bake disagree with the
one just stored.

**(b) Keep reading the ScenePresence, and trigger only after the region has applied the change.** Cheap, and it
reuses the one resolver. It depends on the apply path being reliable — and the trace above says it is reliable,
just late, with a precise completion point: `AvatarFactoryModule.cs:890`, immediately after `SetAppearanceAssets`
and `AvatarService.SetAppearance`. An event for exactly this already exists and is unused:
`EventManager.OnAvatarAppearanceChange` / `TriggerAvatarAppearanceChanged` (`EventManager.cs:404-405`,
`:1948-1967`), whose only call site in the tree is **commented out**, on the very next line
(`AvatarFactoryModule.cs:891`), with no subscribers anywhere.

#### What Q-14 means for this, and why it decides the choice

Q-14: an appearance save wipes the bake index, because `AvatarService.SetAvatar` deletes every row for the
principal before rewriting the appearance-derived keys (`AvatarService.cs:93`). **An outfit change is exactly
when an appearance save happens** — `Client_OnAvatarNowWearing` queues one at `AvatarFactoryModule.cs:1292`.

So a bake triggered on the *arrival* of a COF change is wrong twice over: it composites from `UUID.Zero` asset
ids, and about five seconds later the queued save deletes the index it just wrote — so the work is lost as well
as incorrect, and the next login re-bakes from nothing. A bake triggered *after* that save has run reads resolved
asset ids **and** writes its index into a record that has just been rewritten and will not be rewritten again by
this change.

**The stale-wearables problem and the wiped-index problem have the same fix, and it is an ordering fix.** That is
the strongest argument in this section.

#### Recommendation

**(b), hooked to the completion of `SaveAppearance`** — uncomment `TriggerAvatarAppearanceChanged` at
`AvatarFactoryModule.cs:891` (or raise an equivalent event on that line) and make S5's COF-change trigger a
subscriber to it.

In terms of what breaks if the recommendation is wrong:

- **If (b) is wrong** — some outfit change reaches the COF and never produces an `AgentIsNowWearing`, so the save
  never fires — the failure is **a bake that does not happen**. The avatar keeps its previous, *valid* bake and
  looks stale until the next login or one `appearance serverbake`. It is visible, residents report it as "my
  shirt didn't change", it is diagnosable from the absence of an `[SSB]` line, and it is recoverable with one
  console command. Nothing is corrupted.
- **If (a) is wrong** — the COF and the ScenePresence disagree and the bake follows the COF — the failure is **a
  bake that is wrong and stored**: faces written from one truth while `Wearables` holds another, an index whose
  hash describes inputs the ScenePresence never had, and a supersede that has *already deleted the previous good
  asset*. That is the S1d/Q-13 class of defect — a bad bake painted over a good one — and it needs an
  asset-level repair, not a re-trigger.

The asymmetry is the whole argument: **(b) fails late, (a) fails wrong.** A baker that is occasionally late is a
nuisance; a baker that is confidently wrong destroys the previous good bake through supersede. Cost is the
secondary argument and points the same way — (b) is free, (a) is a Robust round trip plus N link resolutions on
every trigger, on a path S2 measured live at 2823 ms cold.

**What (b) requires before S5 can rely on it.** These are S5's work, not caveats:

1. The trigger must fire *after* `AvatarService.SetAppearance`, never before — that is the Q-14 ordering, and it
   is the entire point.
2. `Client_OnAvatarNowWearing` returns early when nothing changed (`:1278-1283`), so no save is queued and no
   event fires. Harmless for the bake (unchanged inputs would be `Reused` anyway), but "no event" must not be
   read as "no change" by anything else that subscribes.
3. `SaveAppearance` drops a queued save when the presence has gone (`:871-880`); `FlushAppearanceSaveOnClose`
   (`:129`) covers the normal close. A bake must not be attempted for a presence that is closing.
4. `BakeCOFVersion` is stored but not compared today (S2, ADR-004 "as built"). §4.3's handshake is what will
   compare it, and it should be read *at the same point* the bake reads the wearables, not earlier.
5. **Ledger Q-6 stands and is the one thing that could overturn this.** If Firestorm on a bit-0 region stops
   sending `AgentIsNowWearing` and only POSTs the cap, (b)'s trigger disappears on precisely the regions SSB is
   enabled for — and then the cap POST becomes the trigger and this section's ordering rule applies to it
   unchanged. That is a measurement, not an argument, and S5 should take it first.

## 5. Interaction with the web viewer (G3, G4)

| Situation | Gateway behaviour |
|---|---|
| Region advertises bit 0 | Do not bake. Accept the sim's `AvatarAppearance` for self (must accept `AppearanceData`-bearing messages), fetch bakes via the existing asset route, render. `appearance.status = "server"`. |
| Region does not advertise bit 0 (ordinary OpenSim, or flag off) | Current S11/S12 behaviour: gateway-side bake with the complete-or-nothing invariant and the fidelity gate. |
| Library update | Both consumers reference the same `OpenSimNGC.Appearance.Baking` version; golden-fixture tests live with the library, run in both CI paths. |

The gateway's compositor directory is **deleted** once the library reference lands (S0b); no two copies.

## 6. Open design questions for John

Listed in the Ledger as D-1 … D-5 and Q-1 … Q-4. The three that block S0:
- D-1: SSB before AIS (order change from BP-v2's AIS→SSB).
- D-3: sim-side fidelity policy — bake best-effort with a logged report (recommended) vs refuse like the gateway.
- ADR-003: library placement (in-tree project vs NGC NuGet package — affects Mike and the gateway's reference style).

## 7. S6 recon — gateway SSB-aware mode (ADR-009)

Recorded 2026-09-05 against `feature/ais-v3` `b13f15add3` and web-viewer `master` `0a6acffea9`. No gateway code was changed; this section is the answer to the five recon questions S6 asked before building.

### 7.1 Does LibreMetaverse 3.1.4 expose `RegionProtocols`? — Ledger Q-5, **answered: yes**

Reflected out of `LibreMetaverse.dll` (net10.0, `~/.nuget/packages/libremetaverse/3.1.4/lib/net10.0/`), assembly identity `LibreMetaverse, Version=3.0.0.0`. Namespace is `LibreMetaverse.Packets`, **not** `OpenMetaverse.Packets` — the gateway's package and the OpenSim tree's vendored OMV are different assemblies with different namespaces.

- `LibreMetaverse.Packets.RegionHandshakePacket+RegionInfo4Block` carries `UInt64 RegionProtocols` alongside `UInt64 RegionFlagsExtended`. Raw access is available.
- Better, the library already decodes it: `LibreMetaverse.Simulator.Protocols` is an instance field of type `LibreMetaverse.RegionProtocols`, a flags enum:

| member | value |
|---|---|
| `None` | `0x0` |
| **`AgentAppearanceService`** | **`0x1`** |
| `SelfAppearanceSupport` | `0x4` |

So bit 0 is named, decoded, and held per `Simulator`. **No raw-packet path is needed**, and Q-5's fallback question does not arise. The gateway reads `simulator.Protocols.HasFlag(RegionProtocols.AgentAppearanceService)`.

That the sim's bit 0 is the right thing to key on is the viewer's own rule: `llviewerregion.cpp:3034` reads `RegionInfo4 / RegionProtocols` out of the handshake, `:3044` stores it, and `:3083` computes `mCentralBakeVersion = region_protocols & 1`. Consumers read it back through `getCentralBakeVersion()` (`llvoavatar.cpp:3945`).

### 7.2 Does it parse the `AppearanceData` block? — **yes, and it surfaces it**

- `LibreMetaverse.Packets.AvatarAppearancePacket+AppearanceDataBlock` = `{ Byte AppearanceVersion, Int32 CofVersion, UInt32 Flags }` — the same three fields the sim writes (S3).
- It reaches the consumer without touching packets: `LibreMetaverse.AvatarAppearanceEventArgs` exposes `AppearanceVersion`, `COFVersion`, `AppearanceFlags`, plus `FaceTextures`, `DefaultTexture`, `VisualParams` and `AvatarID`.

So the gateway gets `cof_version` and `appearance_version` for self and for others from the ordinary appearance event. Note `AppearanceFlags` is an enum whose only member is `None`, so the `Flags` word carries nothing the library names — consistent with the sim writing 0.

### 7.3 Does it surface `agent_appearance_service`? — **yes**

`LibreMetaverse.LoginResponseData.AgentAppearanceServiceURL` (get/set) and `LibreMetaverse.NetworkManager.AgentAppearanceServiceURL` (get). The gateway does not need to read the raw login LLSD. This is the value S4 taught Robust to advertise and `llstartup.cpp` adopts only when non-empty.

### 7.5 The gateway's self-appearance flow — every route to the bake/send step

Three entry points, all funnelling into one method:

| # | site | trigger | gate |
|---|---|---|---|
| 1 | `AgentSession.Appearance.cs:117` | login / appearance ready | `_opts.BakeOnLogin` (`:112`) |
| 2 | `AgentSession.Appearance.cs:121` `RequestRebake` | client `appearance.rebake`, from `SessionHub.cs:300` | none |
| 3 | `AgentSession.Appearance.cs:128` `OnRebakeRequested` | sim's `RebakeAvatarRequested` (subscribed `:70`, released `:79`) | `_opts.BakeOnLogin` |

All three call `RunBakeAsync` (`AgentSession.Appearance.cs:131`), which single-flights on `_bakeRunning` and calls `AppearanceBaker.RunAsync(this, …)` (`:145`). `AgentSession` implements `IBakeSteps` (`:26`), so the pipeline is: `GatherWearablesAsync` (`:180`) → `DownloadWearablesAsync` (`:205`) → `CheckSupportAsync` (`:259`, the fidelity gate) → `DownloadTexturesAsync` (`:269`) → `CreateAndUploadBakesAsync` (`:332`) → `SendAppearanceAsync` (`:402`).

`SendAppearanceAsync` is the only place an `AgentSetAppearance` leaves the gateway (`AppearanceBaker.cs:34`, and the S11 invariant at `:47-51`). It builds the packet in `BuildAppearancePacket` (`:481`) and publishes to the client in `PublishSelfAppearance` (`:430`).

**Consequence for S6.** The `server`-mode construction has to cut the type, not the call: all three entry points already converge, so the branch point is a single one — but `AgentSession` *is* the `IBakeSteps` implementation, so "the code that can send appearance does not exist on that branch" means the server-mode session must not be an `IBakeSteps` at all, rather than an `AgentSession` that declines to bake.

## 4.9 The config contract (S12)

**An operator turns both lanes on for the whole simulator with two lines, and never names a region.**

```ini
[AIS]
    Enabled = true

[Appearance]
    ServerSideBaking = true
```

That is the switch, verbatim. A `[<Region Name>]` section is an **optional override and never the way to opt
in**: a single region opts *out* of a simulator-wide true with `AIS_Enabled = false` or
`ServerSideBaking = false` in its own section.

**Precedence, both lanes, identical:** the region section's value if that section carries the key, else the
global, else `false`. `AISv3Module.ResolveEnabled` and `ServerSideBakingRegion.ResolveEnabled` have always done
this - S12 did not change the resolution, it changed what the configuration files *tell* an operator to do and
added the evidence. Note the asymmetry the operator sees and cannot avoid: the global AIS key is `Enabled`
(inside `[AIS]`) while the per-region key is `AIS_Enabled`, because a region section holds settings for many
modules and the key has to say which one it belongs to. `ServerSideBaking` is the same word in both places.

**A region section that exists but says nothing about these keys does not opt out.** Regions commonly have a
section for other settings; the override applies only when the key itself is present. That is a test, not a
convention.

**Every region logs one line at INFO when it loads, naming which config decided:**

```
[AIS]: region "Ebony": AIS v3 "ON" ("global")
[SSB]: region "Ebony": server-side baking "ON" ("global")
```

**The quotes are real.** The logger is structured and renders every argument quoted, so a verify grep written against the unquoted form matches nothing - use `grep -E 'AIS v3 "ON"|server-side baking "ON"'`. `("region section")` in place of `("global")` when the region's own section carried the key. This is what a flip
verify reads - after the two global lines go in and a region's own lines come out, every region must say
`("global")`, and a region still saying `("region section")` is one whose section was missed.

## 4.8 What triggers a bake (S5, S9)

Every trigger converges on one place: an appearance **save** completing, which raises `OnAvatarAppearanceChange`
(Q-16, S5). Nothing bakes on the arrival of a change, because the items are not resolved to asset ids yet.

| # | Change | What the viewer sends | What queues the save | Session |
|---|---|---|---|---|
| 1 | Login / teleport arrival | - | the baked-texture cache check (`ScenePresence.cs:2291-2294`) | S3/S5 |
| 2 | Wear or take off a wearable | `AgentIsNowWearing` | `Client_OnAvatarNowWearing` (`AvatarFactoryModule.cs:1298`) | pre-existing |
| 3 | Wear or take off, AIS route | `UpdateAvatarAppearance` POST | the cap handler, after the §4.3 handshake: **it reads the COF and derives the worn set first**, then queues the save | S5, **S10** |
| 4 | Attach or detach | - | `AttachmentsModule`, six `QueueAppearanceSave` call sites | pre-existing |
| 5 | **Edit a worn wearable** (colour, texture, params) | AIS `UpdateItem` carrying `hash_id` | **the AIS `UpdateItem` handler, when the item's asset changed and the item is worn** | **S9** |

**Why #5 needs its own trigger.** An edit moves nothing else the region watches. The worn set is unchanged
because the viewer keeps the item id (`llagentwearables.cpp:319`), so no `AgentIsNowWearing` follows. The COF
*does* move - the edit panel replaces the link - but a COF version bump is not a trigger by itself. And #3 is
present but unreliable here: `requestServerAppearanceUpdate` defers while any upload is pending
(`llappearancemgr.cpp:3849`), and an edit always has one, so the POST arrives late with a `cof_version` the COF
has already moved past and is refused as stale. Observed 2026-09-05: four edits, no bake, one POST at 20:57:40
refused with "client cof_version 578, server 579".

**Why #3 has to read the folder (S10).** Until S10 the cap only queued a save, and on a bit-0 region that
made #3 a trigger with no input: **nothing in the tree turned a COF link into an `AvatarAppearance.Wearables`
entry.** #2 cannot, because the LL viewer's only `AgentIsNowWearing` sender is
`LLAgentWearables::sendDummyAgentWearablesUpdate` — four hard-coded nonsense item ids, and no callers left
(`llagentwearables.cpp:819-851`). #5 cannot, because it only rewrites the asset of an item id already worn
(`AisWornAssets.cs:32-52`). So a wear that added a link produced a save of the wearables the sim already had and
a bake that reused every channel. Observed on Ebony 2026-09-06 10:09:52: two shirts linked in the COF, `reused
6/6`, and the `Avatars` record holding `Wearable 4:0` alone.

The cap now calls `ServerSideBakingModule.ApplyCofToWearables` (`:382`) before `QueueAppearanceSave` (`:365`):
`GetFolderContent` on the COF, each `AssetType.Link` resolved to its target, and `CofWearables.Derive` applied.
**Read, do not bake** — the Q-16 ordering is unchanged, because the derived items still carry unresolved asset
ids until `SetAppearanceAssets` runs inside the save.

**Order within a type.** The viewer keeps it in the link item's description as `"@" + (type * 100 + index)`
(`build_order_string`, `llappearancemgr.cpp:3637-3642`, written by `getWearableOrderingDescUpdates` `:3676-3702`
and pushed to the server by `updateClothingOrderingInfo` `:3733`), and layers by it, later index on top
(`LLTexLayerTemplate::render`, `lltexlayer.cpp:1659-1689`, over the cache built `0..n-1` at `:1615-1638`).
`CofWearables.Derive` sorts on that key and sinks unnumbered links below the numbered ones, as
`WearablesOrderComparator` does (`:3644-3674`). Nothing downstream needed changing: `AvatarWearable` already
holds five per type in order, `BakeOrchestrator.ResolveWearables` already walks `j` over `slot.Count`, and
`TexLayerCompositor` already composites every instance of a type in list order
(`TexLayerCompositor.cs:423-431`).

**S8 is what makes an unresolvable link safe.** A link whose target this region cannot read is dropped in the
reader rather than attributed to a guessed type; its type is then one this read says nothing about, and `Derive`
keeps what the agent already wears — the same answer `SetAppearanceAssets` gives (`AvatarFactoryModule.cs:975-989`).

**The save contract (A19).** #5 fires off the AIS `UpdateItem` PATCH, and that PATCH must only report success
when the save actually happened. Until A19 it always reported success: the asset-transaction chain was `void` from
`IAgentAssetTransactions` down, so a validator refusal reached nobody and the cap answered `200` with the item's
old asset id. It now carries `AisAssetTransaction` - `Applied`, `NotResolvable`, `Refused` - and a **`Refused`
answers 403 and emits no `_updated_category_versions`**, so the folder version the viewer holds does not advance
and its next fetch still sees the true state. `NotResolvable` (no transaction module, no client, the library
backend, an unknown transaction id whose xfer is still in flight) is **not** a failure and still answers 200.

The bake side follows from that: a refused save changes no asset, so the S9 hook does not fire and no bake is
queued for an outfit that did not change. That is pinned by a test rather than left to follow from the code.

**Cost.** #5 queues a save; it does not bake. The save re-resolves every worn item and the bake's per-channel
input hash then decides what is recomputed, so an edit that changed nothing visible costs one hash check per
channel. An item that is not worn queues nothing.

## 4.7 The body-part guard (S8)

A bake is refused when the incoming wearable set has lost one of the four body-part slots — shape, skin, hair,
eyes — since the last set this sim successfully baked from.

**Why a refusal and not a warning.** Storing a bake supersedes the asset it replaces, and supersede means delete
(ADR-004). A bake composed from a set with no skin is a valid-looking bake of nothing, and once it is stored the
good bakes are gone; baking again cannot recover them. On 2026-09-05 exactly that happened: four unresolvable
item ids emptied slots 1-4, and the `reason=CofChanged` bake that followed reported "no Skin worn / no Eyes worn
/ no Hair worn", stored 4 channels and superseded 4.

**Why these four slots and why only present -> absent.** A resident cannot take off a body part — no viewer
offers it, and every avatar has all four from creation — so that transition is never something the resident did.
It is always a failure upstream of the bake: a stale viewer cache, an inventory service that answered late, an
item from another grid. Everything a resident *can* do passes through untouched, because it either keeps the
body parts populated or replaces them: changing clothes, stripping to underwear, swapping a shape.

**The baseline.** Recorded only after a bake succeeds, never after a refusal. Recording a refused set would make
the next attempt see no loss and do the damage anyway — the guard would delay it, not prevent it. The first bake
of a session always proceeds, there being nothing to compare against, and `Forget` clears the baseline on close.

**Standing.** This is a backstop, not the cure. With S8's `SetAppearanceAssets` and child-presence fixes in
place the empty set should not reach the baker at all; the guard exists because the cost of being wrong here is
unrecoverable and the cost of a false refusal is one skipped bake.
