# Recon Report — Server-Side Baking (SSB) / Server-Side Appearance

**Status:** DRAFT for review — recon + design brief. No code, no branch, no build. Supersedes the appearance findings of `RECON-03-avatar-appearance.md` (Pass 3, pinned `645b0f3`) for this subject.
**Scope:** what the code does today, the two lineages that could deliver SSB, a recommendation, the compositor design, a safe rollout, dependencies, and a build-plan skeleton sized to a first demoable milestone.

**Tree:** `JohnLegionH/OpenSim-Tranquillity` at `/d/tranquillity-develop`
**Commit:** `a68d59f232340b62b3e12ee4c9f62f4f2374e60d` — "fix(map): restore legacy MapImageModule terrain rendering after Skia rework", 2026-09-02, checkout branch `feature/voice-visibility-matrix`. 167 commits after Pass 3's `645b0f3bb3` (2026-07-31).
**Target framework:** `net10.0`
**References read (read-only):** Halcyon at `/d/halcyon-reference-fresh` ("Copyright (c) InWorldz Halcyon Developers"); Firestorm viewer source at `/d/phoenix-firestorm/indra/newview` (the LL-upstream code with Firestorm's `[Legacy Bake]` additions marked, which is how the stock-LL behaviour was isolated — no separate LL checkout exists on this machine, `/d/SLViewer-Source` is empty); stock OpenSim 0.9.3 at `/d/opensim - Use this december 2025`; libopenmetaverse source at `/d/libomv-src`; LibreMetaverse 3.1.4 (the web viewer gateway's library) from the NuGet cache; the live grid's configuration under `/d/legiongrid`.
**Method:** direct inspection with `grep`/`sed`; every file:line below is to the commit above unless another tree is named.

---

## R0. Delta against Pass 3 (`645b0f3`)

None of the 167 commits between `645b0f3` and HEAD touch the appearance path. `AvatarFactoryModule.cs`, `LLClientView.cs` (handshake and `SendAppearance`), `SimulatorFeaturesModule.cs`, `UploadBakedTextureModule.cs`, `XBakesModule.cs` and `AvatarAppearance.cs` carry no SSB-related change. **Pass 3's findings stand at HEAD.** Two of them are *refined* below rather than corrected: the missing `AppearanceData` block (R3) explains why a stock LL viewer shows *other* avatars as clouds too, not only itself; and the web-viewer side turns out to already speak the SL SSB client contract (R9), which changes the sizing of the "serve both viewers" requirement.

## R1. No bake cap is registered; the SSB advertisement bit is clear

**Caps.** The complete set of cap names registered anywhere under `Source/OpenSim.Region.ClientStack.LindenCaps` and `Source/OpenSim.Region.CoreModules/Framework` was enumerated (`RegisterHandler(`, `RegisterSimpleHandler(`). It contains `UploadBakedTexture` and `GetTexture`; it does **not** contain `UpdateAvatarAppearance`, nor any appearance-service cap. The `UploadBakedTexture` cap is the *client-bake* upload path: `Source/OpenSim.Region.ClientStack.LindenCaps/UploadBakedTextureModule.cs:97-112` registers it (locally when `Cap_UploadBakedTexture = "localhost"`, `OpenSimDefaults.ini:867`) and `:189-209` stores whatever the viewer uploads as a `Temporary = true`, `Local = true` texture asset (`:207-208`).

**Where a bake cap would register.** The pattern to copy is `UploadBakedTextureModule.RegisterCaps` (`:97-112`): an `ISharedRegionModule` hooking `Scene.EventManager.OnRegisterCaps` and calling `caps.RegisterSimpleHandler("UpdateAvatarAppearance", …)`. `SimulatorFeaturesModule.cs:188` shows the same hook for `SimulatorFeatures`.

**RegionProtocols.** The RegionHandshake writer sets the `RegionInfo4` block explicitly, `Source/OpenSim.Region.ClientStack.LindenUDP/LLClientView.cs:986-995`:

```
//RegionInfo4 block
//RegionFlagsExtended
zc.AddByte(1);
zc.AddUInt64(regionFlags);
//RegionProtocols
    // bit 0 signals server side texture baking
    // bit 63 signals more than 6 baked textures support"
zc.AddUInt64(1UL << 63);
```

So **bit 0 (SSB) is 0 and bit 63 (eleven bake slots / Bakes-on-Mesh) is 1**, hard-coded, for every region. There is no configuration switch; `grep RegionProtocols` over `Source` finds only this site. `SimulatorFeatures` separately advertises `BakesOnMeshEnabled = true` (`SimulatorFeaturesModule.cs:136`).

**Who reads it.** Viewer side: `llviewerregion.cpp:3264-3277` reads `RegionInfo4.RegionProtocols` from the handshake and `:3316` derives `mCentralBakeVersion = region_protocols & 1`. That single value gates the whole SSB request path: `llappearancemgr.cpp:4268-4272` returns "Region does not support baking" when it is 0, before the `UpdateAvatarAppearance` cap is even looked up (`:4274-4278`). LibreMetaverse 3.1.4 exposes the same bit as `RegionProtocols.AgentAppearanceService` (R9). Nothing on the sim side reads the bit back.

## R2. What the appearance path does today: client-bake pass-through plus an optional bake cache

The sim never composes a texture. It receives the viewer's own bakes and relays them.

1. **Inbound.** `LLClientView.cs:8430` maps `AgentSetAppearance` to `HandlerAgentSetAppearance` (`:9196-9221`), which decodes the TextureEntry, visual params, avatar size and the `WearableData` cache items and raises `OnSetAppearance`. `AvatarFactoryModule.Client_OnSetAppearance` (`Source/OpenSim.Region.CoreModules/Avatar/AvatarFactory/AvatarFactoryModule.cs:1179-1186`) → `SetAppearance` (`:168-215`): `SetVisualParams` (`:185`), `SetTextureEntries` (`:195`), `UpdateBakedTextureCache` (`:199`), then `QueueAppearanceSave` / `QueueAppearanceSend` (`:214-215`). The TextureEntry the viewer sends already *contains the baked texture UUIDs* it uploaded through `UploadBakedTexture`; the sim stores the ids, never the layers.
2. **Cache validation.** `AgentCachedTexture` (`LLClientView.cs:8368`, `:12389`) → `Client_OnCachedTextureRequest` (`AvatarFactoryModule.cs:1243-1263`) answers from `sp.Appearance.WearableCacheItems`; `ValidateBakedTextureCache` (`:485-670`) checks the region asset cache and, when `IBakedTextureModule` is present, the external bake store (`:589-640`, assets re-flagged `Temporary`/`Local` at `:621-622`). A miss ends in `RequestRebake` (`:674-718`) → `SendRebakeAvatarTextures` (`:715`), i.e. *the viewer is asked to bake again*. `ScenePresence.cs:2291-2295` runs this validation on region entry.
3. **Outbound.** `ScenePresence.SendAppearanceToAgent` (`ScenePresence.cs:4338-4347`) → `LLClientView.SendAppearance` (`:4499-4543`), the `AvatarAppearance` packet: TextureEntry, visual params, **no `AppearanceData` block** (`:4532-4533`, literally `// no AppearanceData` then a zero count) and an `AppearanceHover` block.
4. **Wearables and COF.** `SetAppearanceAssets` (`:839-1066`) resolves wearable item ids to asset ids through `IInventoryService` at save time; `TryAndRepairBrokenWearable` (`:1067-1116`) rebuilds Current Outfit Folder links. `Client_OnRequestWearables` (`:1158`) and `Client_OnAvatarNowWearing` (`:1194`) are the legacy UDP wearable paths. The sim reads COF and wearables today without AIS (relevant to §E).
5. **Bake storage.** `IBakedTextureModule` (`Source/OpenSim.Region.Framework/Interfaces/IBakedTextureModule.cs`: `Get(UUID)`, `Store(UUID, WearableCacheItem[])`, `UpdateMeshAvatar`) is implemented by `XBakesModule` (`Source/OpenSim.Region.CoreModules/Avatar/BakedTextures/XBakesModule.cs`), a REST client to Robust's `XBakes` file store (`Source/OpenSim.Server.Handlers/BakedTextures/XBakes.cs:56-107`, `BaseDirectory`). It is inert unless `[XBakes] URL` is set (`XBakesModule.cs:55-67`). On the live grid Robust *does* host the service (`/d/legiongrid/gridserver/config/Robust.ini:281-282`, `[BakedTextureService] LocalServiceModule = "OpenSim.Server.Handlers.dll:XBakes"`) but no region config sets `[XBakes] URL`, so the regions never talk to it; `PersistBakedTextures = false` (`OpenSimDefaults.ini:947`) keeps uploaded bakes temporary.

**Net effect for a stock LL viewer.** It never sends `AgentSetAppearance` or uploads bakes (those code paths exist in Firestorm only inside `// <FS:Ansariel> [Legacy Bake]` blocks, e.g. `llagentwearables.cpp:510`, `:529`, `llagent.cpp:6369-6703`), never asks `UpdateAvatarAppearance` (R1), so the sim holds the default TextureEntry and everyone sees a cloud. Only Firestorm's legacy client-bake path (enabled on OpenSim grids) masks this — and it is Firestorm-only.

## R3. Refinement: the missing `AppearanceData` block makes *other* avatars fail too

The viewer resolves an avatar's appearance version from the `AvatarAppearance` packet (`llvoavatar.cpp:10729-10732` reads `AppearanceData.AppearanceVersion` and `CofVersion`; `:10838-10846` reads visual param 11000). LL-upstream's resolution, preserved as commented-out lines at `llvoavatar.cpp:10864-10874`, is: param if present, else field if > 0, **else 1** ("still not set, go with 1"). Firestorm's replacement (`:10875-10886`) resolves the same missing data to **0** (legacy). `:11015` then calls `setIsUsingServerBakes(appearance_version > 0)`.

Because Tranquillity sends no `AppearanceData` (R2 step 3), a **stock LL viewer treats every avatar it sees as server-baked**, builds bake URLs from the appearance-service URL (`llvoavatar.cpp:6815-6836`: `<service>texture/<avatarId>/<bakeName>/<textureId>`), finds the URL empty ("`AgentAppearanceServiceURL not set - Baked texture requests will fail`", `:6825`) and never fetches. The fix for that is part of SSB anyway (R7), but note it: **once the sim emits `AppearanceVersion = 1` it must also serve bake URLs, and until then it must emit `AppearanceVersion = 0` explicitly** rather than omit the block. Emitting `0` is a zero-risk, viewer-visible improvement independent of SSB (Firestorm ignores it; LL viewers stop trying the bake service for legacy avatars). Recorded as ledger F-3 / D-4.

## R4. The SL server-bake contract, as the viewer implements it

This is the contract Tranquillity must meet; it is small and precisely observable in the viewer source.

| Step | Viewer behaviour | Source |
|---|---|---|
| Advertise | `RegionProtocols` bit 0 → `mCentralBakeVersion` | `llviewerregion.cpp:3316` |
| Locate the bake server | login response field `agent_appearance_service` (URL, trailing slash expected) | `llstartup.cpp:5161-5166` |
| Request a bake | `POST <UpdateAvatarAppearance cap>` with LLSD `{ "cof_version": N }` (a debug setting can send the whole COF instead, `:4343-4351`) after every outfit change; skipped while editing appearance | `llappearancemgr.cpp:4243-4351` |
| Reply | LLSD map: `success` (bool) required; on failure `error` (string) and optionally `expected` (int) — a COF-version mismatch makes the viewer re-request its own `AvatarAppearance` and retry with back-off up to `BAKE_RETRY_MAX_COUNT` | `:4359-4400` |
| Result delivery | the sim broadcasts `AvatarAppearance` with `AppearanceData.AppearanceVersion = 1` and `CofVersion = N`; visual param 11000 must agree | `llvoavatar.cpp:10729-10732`, `:10851-10863` |
| Fetch bakes | per baked slot: `GET <agent_appearance_service>texture/<avatarId>/<bakeName>/<textureId>` (`FTT_SERVER_BAKE`, not written to the viewer's texture cache, expects J2C) | `llvoavatar.cpp:6831`, `lltexturefetch.cpp:1818`, `:2811` |
| Sanity | if the avatar is server-baked but the region says CBV 0, the viewer probes the bake URL and may force an update | `llvoavatarself.cpp:3636-3657` |

`bakeName` is the texture entry's default image name from the viewer's avatar dictionary (`head`, `upper`, `lower`, `eyes`, `skirt`, `hair`, `leftarm`, `leftleg`, `aux1`…`aux3`); the sim can treat it as opaque and key on `<textureId>`.

The LLSD *success* payload's texture and visual-param contents are not consumed by the viewer in this code path (the viewer waits for the UDP `AvatarAppearance` instead — `:4380-4386` "the message will return through the UDP"); returning `{ success: true, cof_version: N }` is sufficient, with `textures`/`visual_params` optional for diagnostics.

## R5. The appearance model already has the slots SSB needs

`Source/OpenSim.Framework/AvatarAppearance.cs`: `VISUALPARAM_COUNT = 218` (`:54`), `TEXTURE_COUNT = 45` (`:57`), `BAKE_INDICES = { 8, 9, 10, 11, 19, 20, 40, 41, 42, 43, 44 }` (`:63`) — the six classic bakes plus left-arm, left-leg and aux1–3 for Bakes-on-Mesh — and `WearableCacheItems` (`:126`). `WearableCacheItem` (`Source/OpenSim.Framework/WearableCacheItem.cs:34-39`) carries `TextureIndex`, `CacheId`, `TextureID`, `TextureAsset`. `Serial` (`:77`) is the field Halcyon and the viewer both use as the COF version. Nothing here needs to change for SSB; the compositor writes into the same slots the viewer would have.

## R6. Building blocks already in the tree, and one that is missing

| Need | Present? | Where |
|---|---|---|
| J2K decode | yes | `CoreJ2K.Skia` (9 projects); `GetTextureHandler.cs:303` |
| J2K encode | **yes** | `Source/OpenSim.Framework/SkiaImageUtils.cs:27-52` `TryEncodeToJ2KLossless(SKBitmap)` (CoreJ2K encoder; lossless preset, a lossy preset is a one-line variant) |
| Raster ops (resize, blend, tint, masks) | yes | SkiaSharp 4.151.1 (11 projects) |
| Wearable asset parser (`LLWearable` text: params + textures) | yes | `UtopiaSkye.OpenMetaverse` 1.1.6 (`Directory.Build.props:13-17`); the DLL exports `AssetWearable` and `VisualParams` |
| Visual-param / alpha-mask definitions (`avatar_lad.xml`) and the TGA mask layers | yes, shipped | `/d/legiongrid/regionserver/openmetaverse_data/` (`avatar_lad.xml`, `head_alpha.tga`, …), loaded by libomv's `VisualParams` from `Settings.RESOURCE_DIR` |
| **The compositor itself** | **no** | `UtopiaSkye.OpenMetaverse` does not export `Imaging.Baker`/`BakeLayer` (checked against the DLL; the fork dropped System.Drawing-era imaging, see `Docs/OPENMETAVERSE_SYSTEM_DRAWING_SPIKE.md`). The reference implementation exists in libomv (`/d/libomv-src/OpenMetaverse/Imaging/BakeLayer.cs`, 672 lines, BSD-3) and in LibreMetaverse 3.1.4 (`LibreMetaverse.Imaging.Baker`) |
| Inventory/COF read from the sim | yes | `AvatarFactoryModule.cs:839-1116` via `IInventoryService` |
| Bake asset serving to viewers | partly | `GetTextureHandler.cs:142-163` serves any `AssetType.Texture` from the asset service; the SL bake URL shape (R4) is a different route that does not exist |
| Login-response field | no | `Source/OpenSim.Services.LLLoginService/LLLoginResponse.cs:486-497` has no `agent_appearance_service` |
| AIS v3 | **no** | `Source/OpenSim.Services.AISv3` contains only a `WeatherForecast` scaffold |

## R7. Halcyon lineage: persistent grid-side bake cache, no compositor

Halcyon is client-bake with a grid-wide cache; it never composes either.

- **Upload persists.** `OpenSim/Region/CoreModules/Capabilities/AssetCapsModule.cs:206-209` registers `UploadBakedTexture`; `:387-420` stores the upload as a `Local = true` asset with `Temporary` deliberately *not* set ("Persist baked textures as we will use them in the baked texture cache", `:412-416`).
- **Cache is keyed by the viewer's cache id and lives in the user database.** `ScenePresence.SetAppearance` (`OpenSim/Region/Framework/Scenes/ScenePresence.cs:3546-3600`) builds `cacheId → textureId` from the `WearableData` blocks (`:3552-3563`, note the V1/V2 index conversion), sets `Serial` from the COF version (`:3566`) and hands both to `IAvatarFactory.UpdateDatabase` (`OpenSim/Region/Framework/Interfaces/IAvatarFactory.cs`, two methods). `AvatarFactoryModule.UpdateDatabase` (`OpenSim/Region/CoreModules/Avatar/AvatarFactory/AvatarFactoryModule.cs:656-743`) coalesces updates for 3 s, refuses appearances with a zeroed required wearable, then calls `AvatarService.UpdateUserAppearance` and `SetCachedBakedTextures` (`:737-738`). The user server exposes `get_cached_baked_textures` / `set_cached_baked_textures` over XML-RPC (`OpenSim/Grid/UserServer.Modules/UserServerAvatarAppearanceModule.cs:76-77`, `:267-310`) backed by a `cachedbakedtextures (cache, texture)` table (`OpenSim/Data/MySQL/MySQLUserData.cs:1565-1610`).
- **Cache hits answer `AgentCachedTexture` from the grid**, not the region: `AvatarFactoryModule.cs:387-410` (`_cacheBakedTexturesEnabled`, `:298`, `:369-370`), zero-filling indexes the grid does not know so the viewer rebakes only those.
- **COF building for V1 viewers.** `BuildCOF` (`:58-170`) and `AvatarIsWearing` (`:535-650`) synthesise Current Outfit links for viewers that cannot manage their own COF — solved a 2011 problem, irrelevant to modern viewers.
- **No compositor.** A grep for `Oven`, `BakeLayer`, `Composite`, `ManagedImage` outside `ThirdParty/` hits only unrelated files; the only baker in the tree is libomv's client-side `ThirdParty/libopenmetaverse/OpenMetaverse/Imaging/BakeLayer.cs` and a desktop `Programs/Baker` tool.

**Assessment.** Halcyon's design is a better *client-bake* experience than stock OpenSim's XBakes (cross-region and cross-login cache hits, so Firestorm users rebake less; bakes survive restarts because they are stored as non-temporary assets), but it does nothing for a viewer that will not bake. Porting it into Tranquillity would mean: a Robust `cachedbakedtextures` service (XBakes already stores by agent, not by cache id), a persistent-asset flag on `UploadBakedTexture`, and answering `AgentCachedTexture` from the grid. All of that is orthogonal to SSB and helps only Firestorm-legacy users. It does not move either gate in the brief.

## R8. Stock-OpenSim lineage: no SSB either; XBakes is a cache of viewer bakes

`grep UpdateAvatarAppearance` over stock 0.9.3 returns nothing; there is no appearance service and no compositor upstream. What upstream *did* build is exactly what Tranquillity carries: `UploadBakedTexture` (temporary/local storage), `XBakes` (Robust file store keyed by agent, `XBakes.cs:56-107`, region client `XBakesModule.cs`), `ValidateBakedTextureCache`/`RequestRebake`, bit 63 and `BakesOnMeshEnabled` for eleven-slot BOM, and the hard-coded `RegionProtocols` value with the comment that bit 0 "signals server side texture baking" (`LLClientView.cs:993`). Upstream's intent, as far as the tree shows it, is to *support viewers that bake* and to *refuse to advertise* SSB. "Porting or completing stock SSB" therefore has no source to port; it means writing the appearance service from the viewer contract (R4).

## R9. The web viewer's gateway library already speaks the SL SSB client contract

LibreMetaverse 3.1.4 (the `web-viewer` gateway's dependency) exports `AppearanceManager.UpdateAvatarAppearanceAsync(CancellationToken, int cofVersion)`, `AssetManager.RequestServerBakedImageAsync(UUID avatarId, UUID textureId, string bakeName, …)`, `NetworkManager.AgentAppearanceServiceURL` (populated from the login reply) and `RegionProtocols.AgentAppearanceService` (bit 0), plus `ImageType.ServerBaked`. It also still ships the client-side `LibreMetaverse.Imaging.Baker` (512×512 / 128×128 eyes) with the `content/linden/character` resources.

Consequence: if Tranquillity implements the *SL* contract (cap + login field + bake URL route + `AppearanceVersion = 1`), the gateway needs no protocol work to dress avatars — it reads the bit, asks for a bake for the agent it is logged in as, receives `AvatarAppearance` for everyone, and fetches bakes by the same URL a viewer uses. A *non-SL* design (bakes only reachable through `GetTexture`, no appearance-service URL) would still work for the gateway if bakes are stored as ordinary texture assets, but would not work for the LL viewer at all (R3/R4). This is the strongest argument for keeping the wire contract SL-exact.

## R10. Who bakes NPCs today

`Source/OpenSim.Region.OptionalModules/World/NPC/NPCModule.cs:135` still carries upstream's "We can't just use IAvatarFactoryModule.SetAppearance() yet". NPCs are created from a stored `AvatarAppearance` whose TextureEntry points at baked textures that *some viewer* uploaded earlier; with `PersistBakedTextures = false` those assets are temporary, so NPC bodies survive only as long as the region's asset cache does. SSB with persistent bakes fixes NPC appearance as a side effect (a server bake is reproducible from wearables at any time). Relevant to the bot/NPC track (`feature/bot-npc-framework`).

---

## A. The architectural fork

### A.1 Option (a) — stock-OpenSim-style SSB

There is nothing to port (R8); "stock-style" means: a region-side compositor + an `UpdateAvatarAppearance` cap + storing bakes through the existing asset service, with `XBakes` optionally kept as the persistent store. Work items: compositor (§B), the cap module, the bake-URL route, the login field, `AppearanceData` in `SendAppearance`, per-region protocol bit. Fidelity is whatever the compositor achieves. Serves the LL viewer and the gateway equally because it is the SL contract.

### A.2 Option (b) — Halcyon/InWorldz-lineage server-side appearance

Also has nothing that bakes (R7). Its transferable ideas are storage-side: persist bakes as real assets, key a grid cache by cache id, coalesce appearance saves, answer `AgentCachedTexture` from the grid. Porting means new Robust surface and database tables that serve *client-baking* viewers only. It does not open either gate and would still need everything in (a) to do so.

### A.3 Where each is better or worse

| Criterion | (a) SL-contract SSB on Tranquillity | (b) Halcyon-style cache |
|---|---|---|
| LL viewer shows a body | yes, once the compositor works | no |
| Web viewer shows a body | yes, no gateway protocol work (R9) | no (still needs a bake to exist) |
| Firestorm legacy users | unchanged until the region advertises bit 0; then Firestorm switches to SSB like LL (`llagent.cpp:6369-6382`) | fewer rebakes, bakes persist |
| Fidelity | bounded by the compositor (§B) — the viewer's own bake is the reference and will look slightly different | pixel-identical (it is the viewer's bake) |
| Effort | compositor + cap + route + login field + storage + rollout gating | grid service + table + cap change + cache answers |
| Risk | rollout (§D); compositor correctness | low; none of the risks in the brief |
| Persistence | bakes are reproducible; can be re-baked on demand | bakes persist but cannot be regenerated |

### A.4 Recommendation: **(a), as an SL-contract appearance service inside the region, with (b)'s persistence rule adopted as storage policy.**

Reasoning, tied to the brief's three criteria:

- **Fidelity.** Only a compositor puts a body on a viewer that will not bake; (b) has none, so fidelity for the two gated viewers is zero under (b). Under (a) the achievable fidelity is "close to a viewer bake" (§B.4 lists the known gaps), and the gaps are in the compositor, which can be iterated without touching the protocol.
- **Effort.** (b) is cheaper but buys nothing against either gate; (a)'s protocol surface is small and fully specified by the viewer source (R4, seven concrete points). The compositor is the only sizeable unknown, and a BSD-3 reference implementation exists to port (R6).
- **Serving both viewers.** The SL contract is the *only* one both consumers implement today: the LL viewer (R4) and LibreMetaverse (R9). Any Tranquillity-specific variant would need custom code in the gateway and would never work for the LL viewer.

From (b) keep one rule: **bakes are stored as persistent, regenerable texture assets** (not `Temporary`), so `GetTexture`, `XBakes`, NPCs and the gateway all see them, and a restart does not cloud everyone.

**What would change this recommendation.** (1) If the compositor's fidelity proves unacceptable on real content (mesh bodies with BOM rely on the skin/tattoo/alpha bakes being right) *and* no better compositor can be sourced, the fallback is to keep the region legacy (bit 0 clear, `AppearanceVersion = 0`) and accept that the LL viewer stays unsupported — the web viewer could then bake in its own gateway with LibreMetaverse's `Baker` instead. (2) If a maintained third-party SSB service surfaces that Tranquillity could proxy the cap to (`caps.RegisterHandler("UpdateAvatarAppearance", url)` is the existing pattern for remote caps, `UploadBakedTextureModule.cs:108-109`), the in-region compositor becomes optional. Neither exists on this machine or in these trees today.

---

## B. The compositor

### B.1 Inputs

Per avatar, from the sim's own data: the COF (links → wearable items → assets, exactly what `SetAppearanceAssets` walks, `AvatarFactoryModule.cs:839-1066`), each wearable's `LLWearable` asset (parameters + per-slot texture ids; parsed by `AssetWearable` from the linked libomv), the visual-param definitions and alpha-mask TGAs from `openmetaverse_data/avatar_lad.xml`, and the wearable textures via the asset service (J2K decode through CoreJ2K).

### B.2 Layer model (what the reference does, `libomv-src/OpenMetaverse/Imaging/BakeLayer.cs`)

Per bake type: canvas 512×512 (128×128 eyes) initialised to the base colour (`:118-125`); skin/body-paint and tattoo layers pulled out for special ordering on the head bake (`:130-150`, `:183-187`); built-in base layers `head_color.tga` / `upperbody_color.tga` / `lowerbody_color.tga`, head alpha and skin-grain multiply (`:153-167`); then each clothing texture in slot order, resized to the bake (nearest-neighbour, `:192-197`, with a `FIXME` to tile instead), tinted with the wearable colour (`ApplyTint`, `:222`, `:579`), masked by the wearable's alpha params (`VisualAlphaParam`, multiply vs non-multiply blends, `:239-270`), drawn with source alpha only for skirt/hair layer 0 (`:292-293`); finally the hair layer of the head bake multiplied by `head_hair.tga` (`:203-210`). `AppearanceManager.DecodeWearableParams` (`AppearanceManager.cs:1376-1478`) is the piece that turns a wearable's parameters into `AlphaMasks` and colour info — it must come across with the baker.

### B.3 Library choice

SkiaSharp is sufficient for every operation the bake needs — `SKCanvas.DrawBitmap` with `SKBlendMode.SrcOver`/`Multiply`/`DstIn` for layers, masks and skin grain, `SKColorFilter.CreateBlendMode` for tint, `SKBitmap.Resize` (bilinear or better, an improvement on the reference's nearest-neighbour) — and it is already loaded in the region process. The recommended shape is: **port `BakeLayer.cs` + `ManagedImage.cs` + `TGALoader.cs` (BSD-3, attribution header as done for PrimMesher in the web viewer) as the algorithm, replacing `ManagedImage` per-pixel loops with `SKBitmap` operations where they are the same operation**, decode/encode through `CoreJ2K` (`SkiaImageUtils.TryEncodeToJ2KLossless` exists; add a lossy preset at quality comparable to viewer uploads). Do *not* take a dependency on the LibreMetaverse NuGet inside the region: it would load a second copy of every type in the `LibreMetaverse` namespace next to `UtopiaSkye.OpenMetaverse` and the two `avatar_lad.xml` loaders would fight over `openmetaverse_data`.

### B.4 Known fidelity gaps to plan for (the reference is a bot baker, not the viewer's `LLTexLayer`)

1. **Resolution.** Reference bakes at 512 (viewer default for "medium/high" is also 512; 1024 is a viewer option). Start at 512.
2. **Resize.** Nearest-neighbour; use bilinear. The `FIXME: tile` case (texture smaller than the bake) is real for old content.
3. **Morph-driven masks.** The viewer evaluates alpha masks against the avatar's shape parameters; the reference applies `VisualAlphaParam` weights from the *wearable's* parameters only. Expect slight seam/length differences on gloves, sleeves, skirt length.
4. **Eleven-slot bakes.** The reference `BakeType` covers the six classic bakes; left-arm/left-leg/aux1–3 (BOM universal wearables) need adding — the layer rules for those are in the viewer's `avatar_lad.xml` (`bake` attributes) and are the same mechanism.
5. **Bakes-on-Mesh.** BOM does not change compositing — a BOM mesh body samples the *same* baked textures at the slots bit 63 already advertises. What BOM does change is *visibility of errors*: a mesh body shows the whole skin/tattoo/alpha bake, so gaps 1–3 are more visible than on the system body. The alpha-wearable layers (`LowerAlpha`…`HairAlpha`, skipped as colour layers at `:177-181` but applied as masks) matter most here.
6. **Materials/PBR.** Out of scope; bakes are diffuse only, as in SL.

### B.5 Storage and serving

- Bake output: J2K, stored through the asset service as `AssetType.Texture`, **not temporary**, creator = the avatar, name `Baked <slot>`, with a deterministic *cache key* recorded per avatar: `(avatarId, slot) → (textureId, cofVersion, inputHash)` where `inputHash` covers the wearable asset ids, colours and parameters that feed that slot. Re-baking a slot whose `inputHash` is unchanged is a no-op (the equivalent of the viewer's `AgentCachedTexture`).
- Serving: (1) the SL route `GET <agent_appearance_service>texture/<avatarId>/<bakeName>/<textureId>` — a region-hosted HTTP handler that resolves `<textureId>` through the asset service and answers `image/x-j2c` (Range requests welcome; the viewer's `FTT_SERVER_BAKE` uses the same fetcher as `GetTexture`); (2) `GetTexture` continues to serve the same asset id, which is what LibreMetaverse's normal texture pipeline and any Firestorm user in legacy mode will hit. `agent_appearance_service` should point at a grid-level URL that reverse-proxies to "the region the avatar is in", or, simpler for a single-host grid like this one, at a Robust handler that serves from the asset service directly — the asset is the same either way. Decision D-2.
- Old bakes: keep the last N per avatar/slot (viewers cache by texture id; a changed bake must have a new id) and let a sweeper delete assets that are no longer referenced by any `(avatarId, slot)` record.

### B.6 Where it runs

An `ISharedRegionModule` (`AppearanceBakeModule`) owning: the cap handler, a per-avatar bake queue (one bake job at a time per avatar, latest `cof_version` wins), the compositor, the cache-key table (SQLite/MySQL through the existing data layer; a region-local table is enough for milestone 1), and the bake URL route. A bake of six 512² slots from already-cached textures is tens of milliseconds of raster work plus J2K encode; the dominant cost is fetching wearable textures on first use. Concurrency limit per region (say 2 bakes in flight) protects the sim thread.

---

## C. Wire changes, in one list

| Area | Change | Site |
|---|---|---|
| Handshake | `RegionProtocols` bit 0 from a per-region flag, not a constant | `LLClientView.cs:995` |
| `AvatarAppearance` | emit `AppearanceData { AppearanceVersion, CofVersion }` (1 block): version 1 + COF version for server-baked avatars, version 0 otherwise | `LLClientView.cs:4532-4533`, callers `ScenePresence.cs:4338-4347` |
| Cap | `UpdateAvatarAppearance` (POST LLSD `cof_version` → `{ success, cof_version }` or `{ success:false, error, expected }`) | new module, pattern `UploadBakedTextureModule.cs:97-112` |
| Login | `agent_appearance_service` in the login response | `LLLoginResponse.cs:486-497` (+ `[LoginService]` config) |
| Bake route | `texture/<avatarId>/<bakeName>/<textureId>` | new handler (region and/or Robust) |
| Visual params | param 11000 ("appearance version") set to 1 in the broadcast params for server-baked avatars (the viewer cross-checks it, `llvoavatar.cpp:10851-10863`) | `AvatarAppearance.SetVisualParams` consumers |
| Storage | bake assets persistent; cache-key table | new |
| Legacy path | `AgentSetAppearance` / `UploadBakedTexture` keep working (Firestorm legacy, bots); when bit 0 is set for a region, an incoming `AgentSetAppearance` from a viewer is accepted but the server bake wins for the broadcast | `AvatarFactoryModule.cs:168-215` |

---

## D. Rollout hazards and the safe rollout

**The hazard, precisely.** The moment a region's handshake carries bit 0, every viewer connecting to it (LL *and* Firestorm — `llagent.cpp:6369-6382` moves a legacy avatar to server bakes on entering a CBV>0 region) stops baking locally and asks the cap. If the cap or compositor fails, *every* avatar in that region is a cloud, including Firestorm users who were fine. There is no viewer-side fallback: `checkForUnsupportedServerBakeAppearance` only fires the other way (server-baked avatar entering a legacy region, `llvoavatarself.cpp:3636-3657`).

**Gating design.**

1. **Three switches, all default off.** `[Appearance] ServerSideBaking = false` (grid default in `OpenSimDefaults.ini`); per-region override in the region's own ini (`ServerSideBaking = true` under `[Appearance]` scoped by region name, using the existing `Region_<Name>` override convention); and a **runtime console command** `appearance ssb <region> on|off` that flips the advertised bit for *new* handshakes without a restart (existing sessions keep their mode until they re-enter). The compositor module always loads; only the *advertisement* is gated.
2. **Bit 0 is advertised only when the module reports ready:** compositor self-test passed at startup (bake the default avatar from the library wearables and decode the result), the bake route answers a probe, and the login field is configured. If any check fails the region logs why and stays legacy even with the flag on.
3. **Dual-mode regions are legitimate.** With bit 0 clear the region still *accepts* `UpdateAvatarAppearance` requests (a viewer would not send them, but LibreMetaverse can be told to), which is how the web viewer and NPC re-baking can be exercised on a legacy region before any human-facing region flips. This is the key to testing on the live grid without touching Firestorm users.
4. **Fallback for a failed bake.** The cap answers `{ success:false, error:"…" }`; the viewer logs and retries with back-off (R4). The sim keeps broadcasting the *previous* good bake for that avatar (never the default cloud TE) and re-queues. If no bake has ever succeeded for an avatar, the broadcast falls back to the wearables' own textures where a slot has one (skin, eyes) — imperfect, visibly better than a cloud.
5. **Per-region enable order on the live grid:** (a) a new, empty test region (`SSB-Test`) with the flag on; verify with the web viewer, then an LL viewer, then Firestorm; (b) Elm or Transylvania off-peak with the console switch, watched, revert with the same command; (c) Ebony last; (d) grid default only after every region has run it.
6. **What to watch.** Per region: bake queue depth, bake failures per avatar, bake latency p95, bake-route 404s (a 404 means an `AvatarAppearance` referenced an id the store lost), and `AgentSetAppearance` arrivals on an SSB region (a viewer that has not switched).

**How to test without breaking the live grid.** Milestone 1 (§F) runs entirely on a legacy-advertised region: the gateway asks the cap directly, the resulting `AvatarAppearance` (version 1) is broadcast — *and here is the one live-grid caveat*: Firestorm users in the same region would receive that version-1 appearance for the test avatar and fetch its bakes from the appearance service, which must therefore already be reachable. Do milestone 1 on the `SSB-Test` region only, or during a window with no other users on the region.

---

## E. Dependencies and ordering

- **Track-L order (login benefits → AgentProfile → AIS → SSB), as given in the brief.** No Track-L document was found in the trees on this machine (`Docs/feature/sl-parity-audit/` is not present in `/d/tranquillity-develop`, `/d/tranquillity-hypergrid` or the other checkouts; the closest house documents are `Docs/feature/trusted-hypergrid/` in `tranquillity-hypergrid` and `Docs/voice/`). The order is taken as accepted and recorded as ledger A-1.
- **AIS v3 is not a hard dependency.** The sim reads the COF and wearables through `IInventoryService` today (R2 step 4) and can read the COF folder's version the same way; AIS v3 is the *viewer's* HTTP inventory path. Without AIS the viewer still maintains its COF through legacy UDP inventory ops and the `FetchInventory2`/`FetchInventoryDescendents2` caps (both registered, R1), and the COF folder version still increments on the inventory service. What AIS buys SSB is *tighter agreement on `cof_version`* (the viewer comments in `llvoavatar.cpp:10936-10937` say the canonical COF version is "maintained by the AIS code"); without it expect more `expected`-mismatch retries after fast outfit changes. SSB can proceed independently; the retry loop is the viewer's own mitigation.
- **Login benefits / AgentProfile** have no code coupling to SSB other than the login response being touched for `agent_appearance_service` (one field; coordinate the edit).
- **Not built yet and needed:** the compositor (R6), the cap, the bake route, the login field, per-region flag, `AppearanceData` emission, bake persistence policy, a cache-key store. **Present and reusable:** J2K encode/decode, SkiaSharp, wearable parser, `avatar_lad.xml` + masks on disk, `XBakes` (optional persistent store), `GetTexture`, inventory reads.
- **Bots/NPC track** benefits (R10) and should consume the same bake service rather than its own path.

---

## F. Build-plan skeleton (sizing only; not a BP)

| # | Milestone | Content | Done when |
|---|---|---|---|
| 0 | Hygiene (independent, ship first) | emit `AppearanceData { 0, cof }` explicitly; per-region `RegionProtocols` from config (still 0); `agent_appearance_service` field plumbing behind a flag | LL viewer no longer logs "AgentAppearanceServiceURL not set" for legacy avatars; nothing else changes for Firestorm |
| 1 | **First demoable: one avatar dressed in both viewers** | port compositor (six classic bakes, 512²) into `AppearanceBakeModule`; `UpdateAvatarAppearance` cap; bakes stored persistent; bake route on the region; version-1 broadcast for baked avatars; console switch; `SSB-Test` region flagged | Truly logs in on `SSB-Test` with a system-body outfit: a stock LL viewer shows her dressed (no cloud, no orange), and the web viewer's gateway (LibreMetaverse) fetches the same six bakes and renders them on its avatar. Evidence: cap request/response log, six bake assets, bake-route 200s from both clients, viewer log free of bake fetch failures |
| 2 | Fidelity + BOM slots | eleven-slot bakes (left arm/leg, aux1–3), bilinear resize, tiling, alpha-wearable correctness on a BOM mesh body; side-by-side comparison against Firestorm's own bake of the same outfit | a BOM mesh body looks the same to within an agreed visual tolerance in LL and Firestorm-legacy |
| 3 | Robustness | failed-bake fallback, previous-good retention, `expected` COF handling, queue limits, sweeper, metrics, `appearance ssb` console surface | soak on `SSB-Test` with outfit churn; no cloud regressions |
| 4 | Persistence + grid | Robust-side bake route (or reverse proxy), `XBakes`/asset-service policy, NPC re-bake on rez, HG considerations (foreign avatars carry their own bakes; foreign regions may be legacy) | an avatar TPs between an SSB and a legacy region and back without clouding in either |
| 5 | Rollout | per-region enable per §D.5; Firestorm users observed; grid default flip | all regions SSB; `AgentSetAppearance` arrivals ≈ 0 |

Dependencies inside the skeleton: 0 → 1 → 2/3 (parallel) → 4 → 5. Milestone 1 is the one to size first; its unknown is the compositor port, everything else is protocol plumbing with the viewer source as the spec.

---

## G. Risk register

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| RK-1 | Advertising bit 0 before the compositor is reliable clouds every avatar in the region, Firestorm included | high if unmanaged | high | §D gating: default off, readiness checks, console switch, test region first |
| RK-2 | Compositor fidelity below user expectation on BOM mesh bodies | medium | medium | milestone 2 comparison suite; keep per-region legacy as the fallback |
| RK-3 | `cof_version` drift without AIS causes retry storms after rapid outfit changes | medium | low–medium | honour `expected`; coalesce bake jobs per avatar; measure on `SSB-Test` |
| RK-4 | Bake assets bloat the asset store (a new id per rebake) | medium | medium | cache-key no-op rebakes; retention of last N; sweeper |
| RK-5 | Two libomv copies in one process (if LibreMetaverse were referenced for its Baker) | certain if done | medium | port the BSD code instead (§B.3) |
| RK-6 | Bake route reachability for foreign (HG) visitors and for the web gateway's host | medium | medium | grid-level `agent_appearance_service`; same asset served by `GetTexture` |
| RK-7 | Wearable assets missing/undecodable for old outfits | medium | low | per-slot fallback to base layers; log and continue |
| RK-8 | Login-response edit collides with the login-benefits work in Track-L | low | low | one field, coordinate |
