# Server-side baking

With server-side baking (SSB), the region simulator composites an avatar's baked textures itself instead of
waiting for the viewer to upload them. Current Linden Lab viewers only bake server-side, so without SSB an LL
viewer user is a permanent cloud. Firestorm bakes client-side unless the region says it bakes, so it works
either way.

The design decisions are in `ADR-SET-ssb-appearance.md`, and the viewer contract (rules V1 to V7) is in
`RECON-ssb-appearance-addendum.md` §3. Code comments cite this file's section numbers as "Design Brief §n", so
the numbers do not change.

## 1. What it does

On a region with SSB on:

- the `RegionHandshake` sets bit 0 of `RegionProtocols`, which tells viewers the region bakes (V1);
- each agent gets an `UpdateAvatarAppearance` capability, which the viewer POSTs to after an outfit change (V3);
- the region bakes an agent when it becomes a root agent, and again after each completed appearance save (§4.8);
- `AvatarAppearance` messages for avatars this region baked carry an `AppearanceData` block
  (`AppearanceVersion = 1`, the COF version). Without that block the LL viewer drops its own appearance as
  stale (V4, V5).

With SSB off, nothing on the wire changes. No UDP appearance handler is removed or changed in either case
(ADR-001).

## 2. Requirements for a grid

- **Simulator:** `[Appearance] ServerSideBaking = true` (§4.9).
- **Robust:** the appearance service, which serves other avatars' bakes to viewers at
  `texture/<agent>/<channel>/<uuid>` (V6, ADR-002).
- **Login:** `[LoginService] AgentAppearanceServiceURL`, pointing at that service and ending in `/`. Set it only
  once the service is running: on an SSB region, a viewer with no service URL never textures other avatars.
- **AIS v3** (`[AIS] Enabled`) is not required for SSB. Without AIS, though, the LL viewer can log in and be seen
  but cannot change its outfit (V7).

## 3. Not covered

Physics wearables, hover height, animesh and PBR overrides are not part of the bake. There is no TTL reaper
for old bakes yet (§4.4).

## 4. How it works

### 4.1 Components

| Component | Where | Role |
|---|---|---|
| `OpenSimNGC.Appearance.Baking` | `Source/OpenSimNGC.Appearance.Baking/` | Compositor library: wearable parsing, `avatar_lad.xml` layer sets, Skia compositing, JPEG 2000 encoding (ADR-003, ADR-007) |
| `ServerSideBakingModule` | `Source/OpenSim.Region.OptionalModules/Avatar/ServerSideBaking/` | Region module: the per-region flag, the cap, the triggers, the bake index, the console command |
| `AppearanceServerConnector` | `Source/OpenSim.Server.Handlers/Appearance/AppearanceServerConnector.cs` | Robust handler that serves stored bakes to viewers |
| `LLClientView` | `Source/OpenSim.Region.ClientStack.LindenUDP/LLClientView.cs` | Sets `RegionProtocols` bit 0 and writes the `AppearanceData` block |

### 4.2 The bake pipeline

1. **Inputs.** The agent's worn wearables, as resolved to assets in the ScenePresence (§4.6), plus the visual
   params.
2. **Hash.** Each bake channel gets a hash of its inputs and the bake size. If the stored hash matches and the
   stored asset still exists, the channel is reused. A reused channel costs no texture fetch, decode,
   composite or encode.
3. **Composite and encode** each channel that changed: head, upper, lower, eyes, skirt, hair, left arm, left
   leg, and the three auxiliary Bakes-on-Mesh channels.
4. **Store** each new bake as a texture asset named `bake:<agent>:<channel>`. Record its id and hash in the
   avatar service (§4.4), then point the avatar's baked face at it. Only then is the previous asset for that
   channel deleted (supersede).
5. **Send** `AvatarAppearance`, with `AppearanceData`, to the agent and to everyone who can see it. This happens
   even when every channel was reused: the viewer will not ask again.

### 4.3 The COF version handshake

The viewer's `cof_version` is the inventory `Version` of its Current Outfit folder. The region reads the same
field. For each `UpdateAvatarAppearance` POST:

| Viewer's `cof_version` compared with the server's | Answer |
|---|---|
| equal | `success: true` |
| lower | `success: false`, `expected: <server's version>` |
| higher | re-read the folder once, then answer as above |

After 5 mismatches for one agent within 30 s, the next POST is accepted at the server's version and logged, so
viewer and server cannot loop forever.

`success: true` means *accepted*, not *baked*. The cap reads the Current Outfit folder into the agent's
wearables (§4.8) and queues an appearance save. The bake follows when that save completes. The viewer waits for
the `AvatarAppearance` either way.

### 4.4 Where bakes are kept

- Bakes are ordinary texture assets.
- The avatar service holds the index per agent: `Bake:<channel>` (asset id), `BakeHash:<channel>`,
  `BakeCOFVersion`, `BakeSize` and `BakeUpdated` (UTC). These are rows in the existing key/value avatar table,
  so no schema change is needed.
- Supersede keeps at most one stored bake per channel per agent.
- `BakeUpdated` is recorded for a future expiry reaper. No reaper exists yet, so the bakes of an avatar that
  never returns stay in the asset store.

### 4.5 Rolling it out

- Turn it on for one test region first, with both an LL viewer and Firestorm present.
- Turning the flag on makes Firestorm stop baking client-side on that region and use the region's bakes.
- Turning it off again is safe. Viewers go back to client-side baking at their next login, and stored bakes are
  left in place.

### 4.6 Which store decides what is baked

There are two records of what an agent wears:
- **The Current Outfit folder** decides *membership*: which items are worn. It is what the viewer reads back and
  what `cof_version` counts.
- **The ScenePresence's wearables** (`sp.Appearance.Wearables`) decide *what a bake is made from*. The folder
  holds links, which name items rather than assets. The ScenePresence is the only place in the region where
  membership has been resolved to asset ids.

Asset ids are resolved during the appearance save (`AvatarFactoryModule.SetAppearanceAssets`), not when a
change arrives. So the rule is about order: **a bake runs only after the appearance save has completed.** The
save raises `OnAvatarAppearanceChange`, and the module bakes off that event.

Baking earlier would be wrong in two ways:
- It would composite wearables whose asset ids are not resolved yet.
- The save that follows rewrites the avatar's record (`AvatarService.SetAvatar` deletes every row for the agent
  first), which would wipe the bake index just written.

### 4.7 The body-part guard

A bake is refused if the wearables have lost one of the four body-part slots (shape, skin, hair, eyes) since the
last successful bake of that agent.

- **Why refuse rather than warn.** Storing a bake supersedes, and so deletes, the asset it replaces. A bake made
  from a set with no skin would replace good bakes with a bake of nothing, and baking again could not bring
  them back.
- **Why only a lost body part.** A resident cannot take off a body part, so that transition always means a
  failure upstream: a stale viewer cache, an inventory service that answered late, or an item from another
  grid.
- **The baseline** is recorded only after a successful bake, and is cleared when the presence goes. The first
  bake after a presence arrives always proceeds, because there is nothing to compare against.

### 4.8 What triggers a bake

Every trigger ends in the same place: a completed appearance save (§4.6).

| Change | What queues the save |
|---|---|
| Login or arrival as a root agent | the region bakes on `OnMakeRootAgent`, and the login's own appearance save follows |
| Wear or take off, UDP route (`AgentIsNowWearing`) | `AvatarFactoryModule.Client_OnAvatarNowWearing` |
| Wear or take off, AIS route (`UpdateAvatarAppearance` POST) | the cap, after the §4.3 handshake: it reads the Current Outfit folder, derives the worn set (`CofWearables.Derive`), then queues the save |
| Attach or detach | `AttachmentsModule` |
| Edit a worn wearable (AIS `UpdateItem` changing the item's asset) | the AIS handler, through `AisWornAssets`, only when the item is worn |
| Console | `appearance serverbake <first> <last>` bakes at once |

**Order within a wearable type.** The viewer stores layer order in each link's description as
`@<type * 100 + index>`. `CofWearables.Derive` sorts on it and puts unnumbered links below numbered ones, so a
later index is layered on top, as in the viewer. A link whose target the region cannot read is dropped from the
derived set, and the agent keeps what it already wears for that type.

**A refused save does not bake.** If the asset-transaction module refuses an AIS `UpdateItem`, the PATCH answers
403, no asset changes, and no bake is queued.

### 4.9 Configuration

In the simulator, `[Appearance]` (defaults in `OpenSimDefaults.ini`):

| Key | Default | Meaning |
|---|---|---|
| `ServerSideBaking` | `false` | Turns SSB on for every region of this simulator. |
| `BakeSize` | `1024` | Edge size of each bake in pixels: 512, 1024 or 2048. Any other value logs a warning and uses 1024. Changing it invalidates every stored bake, because the size is part of the hash. |
| `BakeQuality` | `0.85` | JPEG 2000 quality, clamped to 0.1–1.0. |

A single region opts out of a simulator-wide `true` in its own section:

```ini
[My Region]
    ServerSideBaking = false
```

The region's key wins if present, otherwise the global setting applies, otherwise SSB is off. A region section
that does not mention the key does not opt out. Each region logs one INFO line when it loads:
`region <name>: server-side baking ON (global)`, or `(region section)` when its own section decided. The logger
quotes each argument in the line it writes, so a grep for the line should allow for the quotes.

To enable both SSB and AIS for a whole simulator:

```ini
[AIS]
    Enabled = true

[Appearance]
    ServerSideBaking = true
```

In Robust (`Robust.ini.example`; for standalone, `StandaloneCommon.ini.example` carries the
`AgentAppearanceServiceURL` line):

```ini
[ServiceList]
    AppearanceServiceConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:AppearanceServerConnector"

[AppearanceService]
    LocalServiceModule = "OpenSim.Services.AvatarService.dll:AppearanceService"
    AvatarService = "OpenSim.Services.AvatarService.dll:AvatarService"
    AssetService = "OpenSim.Services.AssetService.dll:AssetService"

[LoginService]
    AgentAppearanceServiceURL = "${Const|BaseURL}:${Const|PublicPort}/"
```

The connector goes on the **public** port, because the viewer fetches from it directly. The URL must end in
`/`, because the viewer appends `texture/...` to it with no separator. The `<channel>` in a request is a name:
`head`, `upper`, `lower`, `eyes`, `skirt`, `hair`, `leftarm`, `leftleg`, `aux1`, `aux2` or `aux3`.

The console command `appearance serverbake <first> <last>` exists on every region, whatever the flag says.

## 5. Where the code is

| Area | Path |
|---|---|
| Region module, handshake, triggers, bake index | `Source/OpenSim.Region.OptionalModules/Avatar/ServerSideBaking/` |
| Compositor library and vendored viewer data | `Source/OpenSimNGC.Appearance.Baking/` (provenance in its `THIRD-PARTY-NOTICES.md`) |
| Robust bake service | `Source/OpenSim.Server.Handlers/Appearance/AppearanceServerConnector.cs` |
| Wire changes | `Source/OpenSim.Region.ClientStack.LindenUDP/LLClientView.cs` |
| Appearance save and wearable resolution | `Source/OpenSim.Region.CoreModules/Avatar/AvatarFactory/AvatarFactoryModule.cs` |
| AIS hook for edited worn wearables | `Source/OpenSim.Region.ClientStack.LindenCaps/AIS/AisWornAssets.cs` |
| Tests | `Tests/OpenSim.Region.OptionalModules.ServerSideBaking.Tests/` |
