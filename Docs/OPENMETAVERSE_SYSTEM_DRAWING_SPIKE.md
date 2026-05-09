# OpenMetaverse System.Drawing Migration Spike

## Purpose

Track the investigation and migration options for removing the `System.Drawing.Common`
dependency chain that currently blocks reliable .NET 8 execution on Linux.

This spike is specifically about the `Library/OpenMetaverse*.dll` lane currently used by
 OpenSim-Tranquillity projects, and the transitive drawing dependencies brought in by
 LibOpenMetaverse-era rendering and meshing components.

## Current Findings

### Confirmed root cause

- The problem is not limited to a single plugin assembly.
- `OpenMetaverse.dll` itself carries `System.Drawing` references.
- Additional transitive binaries in the current `Library/` lane also carry
  `System.Drawing` references:
  - `Library/OpenMetaverse.Rendering.Meshmerizer.dll`
  - `Library/PrimMesher.dll`
  - `Library/Warp3D.dll`
- This means .NET 8 on Linux is blocked by the current binary lane even when most OpenSim
  source files no longer actively use `System.Drawing`.

### Scope in this repository

- Production projects referencing `OpenMetaverse*` assemblies: 68
- Production `Source/` and `Addons/` C# files with OpenMetaverse usage markers: about 830
- Production `Source/` and `Addons/` C# files mentioning `System.Drawing`: 3

### Hotspot counts by API area

- Structured data / LLSD usage: about 174 files
- Packet types: about 17 files
- Primitive / meshing / sculpt APIs: about 39 files
- Core value types such as `UUID`, `Vector3`, `Quaternion`, `Color4`: about 805 files
- `OpenMetaverse.Utils` helper usage: about 90 files

These counts reinforce that a full library swap is not primarily a rendering-only change.
The highest-churn areas are likely to be structured data, shared model types, and protocol
surfaces, with rendering and meshing concentrated in a smaller but more implementation-
specific slice.

### Direct source mentions of System.Drawing in current tree

- `Source/OpenSim.Region.CoreModules/Scripting/VectorRender/VectorRenderModule.cs`
  - legacy commented method signature only
- `Source/OpenSim.Region.CoreModules/World/Terrain/FileLoaders/JPEG.cs`
  - stale doc comment only
- `Source/OpenSim.Region.CoreModules/World/Warp3DMap/Warp3DImageModule.cs`
  - compatibility comments and a known sculpt rendering limitation

### Important implication

OpenSim source has already been partially moved away from direct `System.Drawing` use, but
the prebuilt OpenMetaverse dependency lane still makes drawing support part of the runtime
identity of core assemblies.

In practice, this is a dependency modernization problem, not a packaging problem.

## Loader Behavior Notes

- DotNetCorePlugins discovery catches assembly load exceptions and continues scanning.
- Reflection type load failures are partially tolerated by returning non-null loadable types.
- Region module instantiation is generally isolated, though some constructor fallback paths
  are broad and under-logged.
- This means a bad plugin or addon can fail without necessarily stopping the full server.
- However, because core framework and server projects reference `OpenMetaverse.dll`
  directly, the dependency issue is not purely an optional-addon problem.

## Decision Framing

### What will not work as a real fix

- Dropping a .NET 6 `System.Drawing.Common` assembly into the runtime directory.
- Treating this as only an assembly resolution issue.
- Keeping the current OpenMetaverse binary lane unchanged and expecting Linux/.NET 8 to be
  stable.

### Viable strategy families

1. Patch or fork the current LibOpenMetaverse lane to remove `System.Drawing`.
2. Port to LibRemetaverse, which has already moved away from `System.Drawing`.
3. Introduce an internal abstraction layer for image/meshing/rendering concerns and use it
   to isolate library replacement.
4. Use a temporary sidecar or helper process on .NET 6 for legacy image work as a short-term
   bridge only.

## Migration Matrix

### Summary table

| Option | Goal | Expected effort | Bootability impact | Main risks | Recommendation |
|---|---|---:|---|---|---|
| A. Patch current OpenMetaverse lane | Remove `System.Drawing` from current binaries with minimum API churn | Medium | Best short-term chance to restore Linux/.NET 8 bootability incrementally | Owning a fork; hidden drawing usage in meshing/rendering code | Best near-term unblock path |
| B. Port to LibRemetaverse | Replace current OpenMetaverse lane with a maintained, drawing-free fork | High | Likely requires coordinated code changes before full bootability | Broad API drift across 68 projects and many call sites | Best long-term direction if compatibility is acceptable |
| C. Add internal abstraction first | Decouple OpenSim from graphics/meshing APIs before library replacement | Medium to High | Improves staged migration safety | More up-front refactoring before visible runtime payoff | Strong supporting strategy, especially if B is chosen |
| D. .NET 6 sidecar bridge | Offload legacy drawing operations to helper service/process | Low to Medium | Can unblock isolated features without immediate full migration | Operational complexity, cross-process data flow, still carries legacy lane | Emergency bridge only |

### Staged matrix

| Stage | Objective | Must-change assemblies first | Likely code areas | Validation target | Notes |
|---|---|---|---|---|---|
| 0 | Confirm exact runtime blockers | `OpenMetaverse.dll`, `OpenMetaverse.Rendering.Meshmerizer.dll`, `PrimMesher.dll`, `Warp3D.dll` | Plugin discovery, startup plugin scan, core load path | `dotnet build` plus Linux startup smoke | Already confirmed at metadata level |
| 1 | Restore bootability on Linux/.NET 8 | Replace or patch binaries that directly carry `System.Drawing` | `Source/OpenSim.Framework`, `Source/OpenSim.Server.*`, `Source/OpenSim.Region.CoreModules`, `Source/OpenSim.Region.PhysicsModules.ubODEMeshing` | Startup smoke with plugin discovery enabled | Goal is boot first, feature parity second |
| 2 | Preserve critical runtime behaviors | Same as Stage 1 plus any missing rendering/meshing dependencies | Map tiles, terrain save, sculpt/mesh decode, region module loading | Focused runtime checks for region boot and map generation | Sculpt rendering is already degraded in current branch |
| 3 | Reduce API coupling in OpenSim code | Internal wrappers around texture decode, image encode, meshing entry points | Core modules, map generation, physics meshing, asset/image helpers | Narrow project builds and targeted tests | Makes later library swaps less invasive |
| 4 | Evaluate LibRemetaverse replacement | OpenMetaverse API consumers with highest protocol/model coupling | Capabilities, services, UDP stack, groups, presence, inventory, addons | Compatibility spike branch and compile matrix | Do this after bootability is restored or abstraction boundary exists |
| 5 | Finish parity and remove legacy lane | Remaining `OpenMetaverse*` references across all production projects | Broad sweep across services and addons | Full solution build and behavior checks | Final cleanup only after earlier gates pass |

## Subsystem Inventory

### Boot-critical foundation

These projects are closest to startup, plugin discovery, region/server boot, and common
runtime contracts. Changes here justify a separate server spike branch.

- `Source/OpenSim.Framework`
- `Source/OpenSim.Framework.AssetLoader.Filesystem`
- `Source/OpenSim.Framework.Console`
- `Source/OpenSim.Framework.Monitoring`
- `Source/OpenSim.Framework.Serialization`
- `Source/OpenSim.Framework.Servers`
- `Source/OpenSim.Framework.Servers.HttpServer`
- `Source/OpenSim.Server.Base`
- `Source/OpenSim.Server.GridServer`
- `Source/OpenSim.Server.RegionServer`
- `Source/OpenSim.Server.Handlers`
- `Source/OpenSim.Region.Framework`
- `Source/OpenSim.Capabilities`
- `Source/OpenSim.Capabilities.Handlers`
- `Source/OpenSim.Services.Interfaces`

### Region runtime and protocol surfaces

These carry viewer protocol, scene, simulation, and module behavior. They are likely to be
affected by any model or packet-level library change.

- `Source/OpenSim.Region.ClientStack.LindenCaps`
- `Source/OpenSim.Region.ClientStack.LindenUDP`
- `Source/OpenSim.Region.CoreModules`
- `Source/OpenSim.Region.OptionalModules`
- `Source/OpenSim.Region.ScriptEngine.Shared`
- `Source/OpenSim.Region.ScriptEngine.YEngine`
- `Source/OpenSim.ApplicationPlugins.LoadRegions`
- `Source/OpenSim.ApplicationPlugins.RemoteController`

### Physics and meshing slice

This is the best bounded implementation slice for the first compatibility spike because it
contains a concentrated share of the rendering and meshing risk.

- `Source/OpenSim.Region.PhysicsModules.Meshing`
- `Source/OpenSim.Region.PhysicsModules.ubODEMeshing`
- `Source/OpenSim.Region.PhysicsModules.ubODE`
- `Source/OpenSim.Region.PhysicsModules.BasicPhysics`
- `Source/OpenSim.Region.PhysicsModules.BulletS`
- `Source/OpenSim.Region.PhysicsModules.POS`
- `Source/OpenSim.Region.PhysicsModules.SharedBase`

### Services and data layer

These are broad but mostly model, serialization, and structured-data heavy rather than
graphics heavy. They become important after bootability is restored.

- `Source/OpenSim.Data`
- `Source/OpenSim.Data.MySQL`
- `Source/OpenSim.Data.MySQL.MoneyData`
- `Source/OpenSim.Data.Null`
- `Source/OpenSim.Data.PGSQL`
- `Source/OpenSim.Data.SQLite`
- `Source/OpenSim.Services.AssetService`
- `Source/OpenSim.Services.AuthenticationService`
- `Source/OpenSim.Services.AuthorizationService`
- `Source/OpenSim.Services.AvatarService`
- `Source/OpenSim.Services.Base`
- `Source/OpenSim.Services.Connectors`
- `Source/OpenSim.Services.EstateService`
- `Source/OpenSim.Services.ExperienceService`
- `Source/OpenSim.Services.FSAssetService`
- `Source/OpenSim.Services.FreeswitchService`
- `Source/OpenSim.Services.Friends`
- `Source/OpenSim.Services.GridService`
- `Source/OpenSim.Services.HypergridService`
- `Source/OpenSim.Services.InventoryService`
- `Source/OpenSim.Services.LLLoginService`
- `Source/OpenSim.Services.MapImageService`
- `Source/OpenSim.Services.MuteListService`
- `Source/OpenSim.Services.PresenceService`
- `Source/OpenSim.Services.SimulationService`
- `Source/OpenSim.Services.UserAccountService`
- `Source/OpenSim.Services.UserProfilesService`

### Optional addons

These should stay out of the first implementation slice unless one is specifically needed to
validate the patched binary lane.

- `Addons/Gloebit.GloebitMoneyModule`
- `Addons/OpenSim.Addons.Groups`
- `Addons/OpenSim.Addons.OfflineIM`
- `Addons/OpenSimMutelist`
- `Addons/OpenSimSearch`
- `Addons/os-webrtc-janus`

## Branch Boundary

### Safe to continue on the current plugin branch

- Documentation and analysis only
- Reference inventories and migration planning artifacts
- Assembly/API comparison notes that do not modify runtime code or shipped binaries

### Separate server spike branch required

Create the server spike branch from `develop` before any of the following work starts.

1. Replacing or rebuilding any `Library/OpenMetaverse*.dll`, `PrimMesher.dll`, or `Warp3D.dll`
2. Touching boot-critical runtime projects such as `OpenSim.Framework`, `OpenSim.Server.*`,
   `OpenSim.Region.*`, `OpenSim.Capabilities`, or `OpenSim.Services.*`
3. Adding abstraction seams for rendering, meshing, texture decode, or image encode in
   production source
4. Running implementation validation intended to prove Linux/.NET 8 server bootability for
   the new binary lane

### Suggested first implementation slice for the server branch

1. Binary lane replacement or patch for the four drawing-bound assemblies
2. Boot-critical validation through `OpenSim.Framework`, `OpenSim.Server.GridServer`, and
   `OpenSim.Server.RegionServer`
3. Focused follow-up in `OpenSim.Region.CoreModules` and
   `OpenSim.Region.PhysicsModules.ubODEMeshing`

This keeps the first spike narrow: restore bootability first, then expand outward only where
runtime validation proves additional source changes are required.

## Must-Change Assemblies First

These are the first assemblies to inspect or replace because they carry direct drawing
identity and are the most likely cause of Linux/.NET 8 load failures.

1. `Library/OpenMetaverse.dll`
2. `Library/OpenMetaverse.Rendering.Meshmerizer.dll`
3. `Library/PrimMesher.dll`
4. `Library/Warp3D.dll`

If these remain drawing-bound, boot failures or plugin load failures can persist even if
OpenSim source stops mentioning `System.Drawing` entirely.

## API Delta Hotspots To Expect In A LibRemetaverse Spike

These areas are likely to have the highest migration cost if switching libraries.

1. Structured data and LLSD usage
   - `OpenMetaverse.StructuredData`
   - heavily used in capabilities, Janus/webRTC modules, groups, login/service flows
2. Primitive and meshing/rendering APIs
   - `Primitive`, `TextureEntry`, `Face`, `FacetedMesh`, sculpt and mesh decode paths
3. Packet and client protocol types
   - `OpenMetaverse.Packets` usage in LindenUDP stack and tests
4. Shared model/value types
   - `UUID`, `Vector3`, `Quaternion`, permissions enums, flags, asset/message helpers
5. Utility helpers
   - `Utils` conversion helpers appear in service and messaging code

## Suggested Spike Order

1. Verify whether a patched or rebuilt OpenMetaverse binary set without `System.Drawing`
   can boot the server with current source unchanged.
2. If boot is restored, measure which runtime features still regress:
   - map tile generation
   - sculpt rendering
   - terrain export
   - physics meshing
3. In parallel, create a LibRemetaverse compile spike against one bounded subsystem:
   - recommended first target: map/meshing/rendering path
4. If LibRemetaverse compatibility delta is acceptable, decide whether to continue staged
   replacement or keep a local fork of the current lane.

## Working Recommendation

### Near term

Use a patched current-library lane to remove `System.Drawing` from the startup-critical
assemblies first.

### Medium term

Introduce small internal abstraction seams around rendering, texture decode, and image
encode/meshing entry points so the OpenSim codebase stops depending on library-specific
graphics assumptions.

### Long term

Prefer a maintained drawing-free upstream lane such as LibRemetaverse if compatibility and
behavior are acceptable after a bounded spike.

## Open Questions

1. Which exact public API members in the current OpenMetaverse lane still expose bitmap-typed
   signatures that would force downstream churn?
2. Can the required subset of meshing and rendering be patched locally without a large
   protocol/model migration?
3. Is there a narrow subset of production functionality that still genuinely requires the
   legacy Warp3D or Meshmerizer drawing behavior?
4. How much of current OpenMetaverse usage is model/protocol stable versus utility/helper
   usage that can be wrapped or replaced cheaply?

## Next Artifacts To Add

- Assembly-level API surface comparison between current OpenMetaverse binaries and candidate
  replacement binaries.
- Startup smoke results after swapping patched binaries.
- Feature verification notes for map, sculpt, terrain, and mesh behavior.
- A project-by-project OpenMetaverse reference inventory grouped by subsystem.