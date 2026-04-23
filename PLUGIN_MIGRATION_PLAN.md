# OpenSim Plugin System Migration Plan
## From Mono.Addins to DotNetCorePlugins

**Target**: Replace Mono.Addins with McMaster.NETCore.Plugins library

---

## Executive Summary

OpenSim currently uses Mono.Addins for plugin management with:
- **27+ plugin assemblies** across Addons and Core modules
- **XML-based manifest system** (.addin.xml files)
- **Runtime registry management** for enable/disable functionality
- **Multiple extension points** for different plugin types
- **Dependency resolution** between plugins

DotNetCorePlugins offers a simpler, .NET Core-native alternative that uses folder-based plugin discovery instead of XML manifests. This migration will:
1. Remove Mono.Addins dependencies (simpler, cleaner codebase)
2. Maintain plugin functionality while adapting architecture
3. Require architectural changes to support runtime enable/disable if desired
4. Simplify plugin discovery and loading

---

## Implementation Status (Current Branch)

### Completed in Step 1

- Introduced `IPluginDiscovery` abstraction and wired `PluginLoader<T>` to use it.
- Added a backend factory with runtime selection:
    - `monoaddins` (default)
    - `reflection`/`dotnet`/`dotnetcore` mapped to DotNetCorePlugins discovery backend
- Implemented the non-Mono backend using `McMaster.NETCore.Plugins`
    (shared-type loading keyed by plugin interface hints).
- Added type-hinted discovery API calls so non-XML backends can discover by interface type.
- Added startup discovery summary counters at migrated extension points for backend parity checks:
        - `/OpenSim/Startup` (generic loader)
        - `/OpenSim/RegionModules`
        - `/OpenSim/WindModule`
        - `/Robust/Connector`
- Added explicit discovery capability metadata to the abstraction (`PluginDiscoveryCapabilities`) so
    call sites can branch on backend features rather than backend-name strings.
- Migrated key extension paths off direct `AddinManager` calls:
    - `/OpenSim/RegionModules`
    - `/OpenSim/WindModule`
    - `/Robust/Connector`
- Reduced runtime Mono.Addins coupling in robust connector loading:
        - `ServerUtils.PluginLoader` now checks discovery capabilities and only initializes
            `AddinRegistry`/`CommandManager` when registry metadata is supported by the active backend.
        - `AddinRegistry` and plugin/repository management command wiring are now best-effort;
            startup continues and connector loading falls back cleanly if management command setup fails.
        - Added startup switch `EnablePluginManagementCommands` (default `true`) so plugin/repository
            management command registration can be disabled without impacting connector loading.
        - Connector `PluginPath` resolution now falls back to plugin assembly location when
            addin metadata is unavailable.
- Removed direct `Mono.Addins.TypeExtensionNode` inheritance from wind plugins.
- Removed several stale `using Mono.Addins;` directives in source modules.
- Removed `Mono.Addins` extension-point attributes from core plugin interfaces now driven by
    manifest/provider discovery metadata:
        - `IRegionModuleBase` (`/OpenSim/RegionModules`)
        - `IWindModelPlugin` (`/OpenSim/WindModule`)
- Removed `Mono.Addins` extension-point attribute from `IRobustConnector` in
    `OpenSim.Server.Base` while preserving existing addin-registry command support and
    connector loading via discovery backends.
- Completed low-risk Mono.Addins attribute/using cleanup sweep in migrated extension-point
    interfaces. Remaining references are currently intentional in transitional management/
    registry code paths (`PluginManager`, `CommandManager`, mono backend in `IPluginDiscovery`,
    and robust addin-root compatibility metadata in `ServerUtils`).
- Additional low-risk cleanup pass removed unused imports in transitional command wiring
    (`CommandManager`), while preserving required `Mono.Addins.Description` usage in
    `PluginManager` (`DependencyCollection`) to keep framework/server builds green.
- Completed first controlled `.addin.xml` EmbeddedResource removal pilot:
        - Removed `Resources/OpenSim.ApplicationPlugins.LoadRegions.addin.xml` from
            `OpenSim.ApplicationPlugins.LoadRegions.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.ApplicationPlugins.LoadRegions`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed second controlled `.addin.xml` EmbeddedResource removal pilot:
        - Removed `Resources/OpenSim.ApplicationPlugins.RegionModulesController.addin.xml` from
            `OpenSim.ApplicationPlugins.RegionModulesController.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.ApplicationPlugins.RegionModulesController`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed third controlled `.addin.xml` EmbeddedResource removal pilot:
        - Removed `Resources/OpenSim.ApplicationPlugins.RemoteController.addin.xml` from
            `OpenSim.ApplicationPlugins.RemoteController.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.ApplicationPlugins.RemoteController`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed fourth controlled `.addin.xml` EmbeddedResource removal pilot:
        - Removed `Resources/OpenSim.Region.ClientStack.LindenUDP.addin.xml` from
            `OpenSim.Region.ClientStack.LindenUDP.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.Region.ClientStack.LindenUDP`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed fifth controlled `.addin.xml` EmbeddedResource removal pilot:
        - Removed `Resources/OpenSim.Region.ClientStack.LindenCaps.addin.xml` from
            `OpenSim.Region.ClientStack.LindenCaps.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.Region.ClientStack.LindenCaps`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed sixth controlled `.addin.xml` EmbeddedResource removal pilot:
        - Removed `Resources/OpenSim.Region.OptionalModules.addin.xml` from
            `OpenSim.Region.OptionalModules.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.Region.OptionalModules`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed seventh controlled `.addin.xml` EmbeddedResource removal pilot:
        - Removed `Resources/OpenSim.Region.CoreModules.addin.xml` from
            `OpenSim.Region.CoreModules.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.Region.CoreModules`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed eighth controlled `.addin.xml` EmbeddedResource removal pilot (small physics batch):
        - Removed `Resources/OpenSim.Region.PhysicsModules.BasicPhysics.addin.xml` from
            `OpenSim.Region.PhysicsModules.BasicPhysics.csproj` embedded resources.
        - Removed `Resources/OpenSim.Region.PhysicsModules.BulletS.addin.xml` from
            `OpenSim.Region.PhysicsModules.BulletS.csproj` embedded resources.
        - Removed `Resources/OpenSim.Region.PhysicsModules.Meshing.addin.xml` from
            `OpenSim.Region.PhysicsModules.Meshing.csproj` embedded resources.
        - Added explicit allowlist entries in migration guard tests for these manifest paths.
        - Validation gates passed:
                - normal Debug builds of `OpenSim.Region.PhysicsModules.BasicPhysics`,
                  `OpenSim.Region.PhysicsModules.BulletS`, and
                  `OpenSim.Region.PhysicsModules.Meshing`
                - reflection-smoke Debug builds (`OPENSIM_PLUGIN_DISCOVERY=reflection`) for all three
                - plugin migration test suite (`34/34` passing)
- Completed ninth controlled `.addin.xml` EmbeddedResource removal pilot (remaining physics batch):
        - Removed `Resources/OpenSim.Region.PhysicsModules.ubODE.addin.xml` from
            `OpenSim.Region.PhysicsModules.ubODE.csproj` embedded resources.
        - Removed `Resources/OpenSim.Region.PhysicsModules.ubODEMeshing.addin.xml` from
            `OpenSim.Region.PhysicsModules.ubODEMeshing.csproj` embedded resources.
        - Removed `Resources/OpenSim.Region.PhysicsModules.POS.addin.xml` from
            `OpenSim.Region.PhysicsModules.POS.csproj` embedded resources.
        - Added explicit allowlist entries in migration guard tests for these manifest paths.
        - Validation gates passed:
                - normal Debug builds of `OpenSim.Region.PhysicsModules.ubODE`,
                  `OpenSim.Region.PhysicsModules.ubODEMeshing`, and
                  `OpenSim.Region.PhysicsModules.POS`
                - reflection-smoke Debug builds (`OPENSIM_PLUGIN_DISCOVERY=reflection`) for all three
                - plugin migration test suite (`34/34` passing)
- Completed tenth controlled `.addin.xml` EmbeddedResource removal pilot (addons small batch):
        - Removed `Resources/OpenSimSearch.Modules.addin.xml` from
            `OpenSimSearch.Modules.csproj` embedded resources.
        - Removed `Resources/OpenSimMuteList.Modules.addin.xml` from
            `OpenSimMutelist.Modules.csproj` embedded resources.
        - Removed `Resources/OpenSim.OfflineIM.addin.xml` from
            `OpenSim.Addons.OfflineIM.csproj` embedded resources.
        - Added explicit allowlist entries in migration guard tests for these manifest paths.
        - Validation gates passed:
                - normal Debug builds of `OpenSimSearch.Modules`,
                  `OpenSimMutelist.Modules`, and
                  `OpenSim.Addons.OfflineIM`
                - reflection-smoke Debug builds (`OPENSIM_PLUGIN_DISCOVERY=reflection`) for all three
                - plugin migration test suite (`34/34` passing)
- Completed eleventh controlled `.addin.xml` EmbeddedResource removal pilot (addons provider batch):
        - Removed `Resources/OpenSim.Groups.addin.xml` from
            `OpenSim.Addons.Groups.csproj` embedded resources.
        - Removed `Resources/Gloebit.GloebitMoneyModule.addin.xml` from
            `Gloebit.GloebitMoneyModule.csproj` embedded resources.
        - Removed `Resources/WebRtcVoice.WebRtcRegionModule.addin.xml` from
            `WebRtcVoiceRegionModule.csproj` embedded resources.
        - Added explicit allowlist entries in migration guard tests for these manifest paths.
        - Validation gates passed:
                - normal Debug builds of `OpenSim.Addons.Groups`,
                  `Gloebit.GloebitMoneyModule`, and
                  `WebRtcVoiceRegionModule`
                - reflection-smoke Debug builds (`OPENSIM_PLUGIN_DISCOVERY=reflection`) for all three
                - plugin migration test suite (`34/34` passing)
- Completed twelfth controlled `.addin.xml` EmbeddedResource removal pilot (provider pair batch):
        - Removed `Resources/WebRtcVoice.WebRtcVoiceServiceModule.addin.xml` from
            `WebRtcVoiceServiceModule.csproj` embedded resources.
        - Removed `Resources/OpenSim.Region.ScriptEngine.YEngine.addin.xml` from
            `OpenSim.Region.ScriptEngine.YEngine.csproj` embedded resources.
        - Added explicit allowlist entries in migration guard tests for these manifest paths.
        - Validation gates passed:
                - normal Debug builds of `WebRtcVoiceServiceModule` and
                  `OpenSim.Region.ScriptEngine.YEngine`
                - reflection-smoke Debug builds (`OPENSIM_PLUGIN_DISCOVERY=reflection`) for both
                - plugin migration test suite (`34/34` passing)
- Completed thirteenth controlled `.addin.xml` EmbeddedResource removal pilot (higher-risk single):
        - Removed `Resources/OpenSim.Data.addin.xml` from
            `OpenSim.Data.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.Data`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)
- Completed fourteenth controlled `.addin.xml` EmbeddedResource removal pilot (final remaining manifest):
        - Removed `Resources/OpenSim.Server.RegionServer.addin.xml` from
            `OpenSim.Server.RegionServer.csproj` embedded resources.
        - Added explicit allowlist entry in migration guard tests for this manifest path.
        - Validation gates passed:
                - normal Debug build of `OpenSim.Server.RegionServer`
                - reflection-smoke Debug build (`OPENSIM_PLUGIN_DISCOVERY=reflection`)
                - plugin migration test suite (`34/34` passing)

### Completed in Step 1.1: Eliminated Zlib.net Binding Dependency

**Problem**: The reflection-based plugin loader triggered `zlib.net 1.0.4.0` binding errors when ApplicationPlugins loaded,
even though zlib was not directly used by plugins. Root cause was transitive dependency from Region.CoreModules
(via Ionic.Zlib.Core) trying to satisfy expectations of compiled zlib.net bindings.

**Solution**: Replaced external Ionic.Zlib.Core dependency with .NET's built-in System.IO.Compression:
- Updated `InventoryArchiveWriteRequest.cs`: GZipStream now uses System.IO.Compression
- Updated `ArchiveWriteRequest.cs`: GZipStream now uses System.IO.Compression
- Updated `MaterialsModule.cs`: ZlibStream replaced with System.IO.Compression.DeflateStream
- Removed `Ionic.Zlib.Core` NuGet package references from:
  - `OpenSim.Region.CoreModules.csproj`
  - `OpenSim.Region.OptionalModules.csproj`
- Removed legacy zlib assembly resolver from `DotNetCorePluginsDiscovery`:
  - Removed `ResolveLegacyAssembly()` method that attempted zlib.net -> Ionic.Zlib.Core fallback
  - Removed `EnsureLegacyAssemblyAliases()` method that created zlib.net.dll aliases
  - Removed `AttachLegacyAssemblyResolver()` / `DetachLegacyAssemblyResolver()` methods
  - Removed `Ionic.Zlib` from skipped assembly prefix list

**Result**: Plugin discovery no longer requires legacy assembly binding resolution, eliminating a source of
reflection-backend discovery failures. All compression now uses .NET Framework built-ins (no external dependency).

### Completed in Step 2: Config-Based Registry and Plugin Loader Infrastructure

**Phase 2.1: PluginRegistry.cs**
- Created `PluginRegistry` class for programmatic plugin management without XML manifests
- Features:
  * `PluginDescriptor` class encapsulates plugin metadata (id, type, name, version, enabled, priority)
  * `Register()` / `RegisterAll()` for programmatic registration
  * `FromIniConfig()` loads registrations from INI configuration
  * `FromJsonFile()` loads registrations from JSON configuration
  * Query methods: `GetPlugins()`, `GetPluginTypes()`, `HasPlugins()`, `GetPluginCount()`
  * Registry merging for composable configurations
  * Full logging for debugging

**Phase 2.2: DotNetCorePluginLoader<T>**
- Created `DotNetCorePluginLoader<T>` class to replace Mono.Addins loader
- Features:
  * Generic loader constrained to `where T : class, IPlugin`
  * `Load(extensionPoint, typeHint)` uses discovery backend to find plugins
  * `LoadFromRegistry(registry, extensionPoint, typeHint)` loads from explicit registry
  * Two-phase initialization: instantiation then initialization
  * Comprehensive error handling and diagnostics
  * `DotNetCorePluginLoaderFactory` for easy instantiation with backend selection
  * Both `reflection` (new DotNetCorePlugins) and `monoaddins` (legacy) backends supported

**Phase 2.3: PluginLoaderHelper.cs and Integration**
- Created helper methods for migration:
  * `LoadPluginsUsingRegistry()`: Pure registry-based approach
  * `LoadPluginsUsingDiscovery()`: Pure discovery-based approach
  * `LoadPluginsHybrid()`: Try registry first, fall back to discovery (recommended)
  * `DebugPluginLoader<T>`: Verbose logging for diagnostics
- Design allows coexistence with existing Mono.Addins code during transition
- No breaking changes to existing plugin infrastructure

**Result**: Core infrastructure for Phase 2 complete. Both backends can operate in parallel.
RegionModulesController and other loaders can now optionally use the new infrastructure via
helper methods, or continue using existing discovery backend until full migration.

### Completed in Step 3 (Pilot): Code-Based Registrations for Startup Plugins

- Added `IPluginRegistryProvider` and `PluginRegistry.FromProviders(...)` to support
    in-assembly code registrations as a replacement for `.addin.xml` extension entries.
- Updated `DotNetCorePluginsDiscovery` to prefer explicit code registrations for a requested
    extension path, with reflection discovery as fallback when no code registrations exist.
- Added pilot registration providers in startup plugin assemblies:
    - `OpenSim.ApplicationPlugins.LoadRegions`
    - `OpenSim.ApplicationPlugins.RegionModulesController`
    - `OpenSim.ApplicationPlugins.RemoteController`
- Added unit test coverage in `OpenSim.Framework.PluginMigration.Tests` for provider-based
    registry loading.

**Pilot result**: Reflection backend can now resolve `/OpenSim/Startup` plugins through explicit,
code-defined registrations without requiring XML manifest metadata for these assemblies.
Existing XML manifests are still retained in this pilot for transition compatibility.

### Completed in Step 3 (Batch 2): RegionModules Pilot and Parity-Safe Discovery

- Updated `DotNetCorePluginsDiscovery` to make code registrations **additive** with reflection
    discovery (with type de-duplication), rather than replacing reflection results.
    This preserves plugin parity while extension points are only partially converted.
- Added code registration providers for additional pilot assemblies:
    - `OpenSim.Region.ClientStack.LindenCaps` (`/OpenSim/RegionModules` entries)
    - `OpenSim.Region.ClientStack.LindenUDP` (`/OpenSim/RegionModules` entry)

**Batch 2 result**: Phase 3 conversion can proceed incrementally by assembly without losing
non-converted plugins at shared extension paths.

### Completed in Step 3 (Batch 3): CoreModules Registration Conversion (RegionModules + Wind)

- Added `OpenSim.Region.CoreModules/PluginRegistration.cs` with code-based registrations generated
    from `OpenSim.Region.CoreModules.addin.xml` for:
    - `/OpenSim/RegionModules`
    - `/OpenSim/WindModule`
- Registration uses assembly-local type-name resolution (`Assembly.GetType`) to preserve resilience
    if individual module type names diverge across build variants.

**Batch 3 result**: The largest remaining manifest-backed assembly now provides explicit
code registrations while preserving compatibility with XML-backed discovery during transition.

### Completed in Step 3 (Batch 4): OptionalModules Registration Conversion (RegionModules)

- Added `OpenSim.Region.OptionalModules/PluginRegistration.cs` with code-based registrations
    generated from `OpenSim.Region.OptionalModules.addin.xml` for:
    - `/OpenSim/RegionModules`
- Registration uses assembly-local type-name resolution (`Assembly.GetType`) to preserve
    transition safety where optional feature classes may vary by build/runtime environment.

**Batch 4 result**: Optional region-module registrations are now available through provider-based
code metadata while additive discovery still preserves any remaining reflection-only plugins.

### Completed in Step 3 (Batch 5): Physics Module Registration Conversion (RegionModules)

- Added provider-based code registrations for physics assemblies:
    - `OpenSim.Region.PhysicsModules.BasicPhysics`
    - `OpenSim.Region.PhysicsModules.BulletS`
    - `OpenSim.Region.PhysicsModules.Meshing`
    - `OpenSim.Region.PhysicsModules.POS`
    - `OpenSim.Region.PhysicsModules.ubODE`
    - `OpenSim.Region.PhysicsModules.ubODEMeshing`
- Each provider mirrors its `.addin.xml` `/OpenSim/RegionModules` entries using
    assembly-local type-name resolution (`Assembly.GetType`) for transition safety.

**Batch 5 result**: All current Source-tree physics module manifests now have corresponding
code-based registrations for region module discovery.

### Completed in Step 3 (Batch 6): Script Engine Registration Conversion (YEngine)

- Added `OpenSim.Region.ScriptEngine.YEngine/PluginRegistration.cs` with code-based
    registration generated from `OpenSim.Region.ScriptEngine.YEngine.addin.xml` for:
    - `/OpenSim/RegionModules` (`YEngine`)
- Registration uses assembly-local type-name resolution (`Assembly.GetType`) for transition safety.

**Batch 6 result**: YEngine module registration is now represented in provider-based code metadata.

### Completed in Step 3 (Batch 7): Addons Registration Conversion (RegionModules)

- Added provider-based code registrations for addon assemblies:
    - `Addons/Gloebit.GloebitMoneyModule`
    - `Addons/OpenSim.Addons.Groups`
    - `Addons/OpenSim.Addons.OfflineIM`
    - `Addons/OpenSimMutelist`
    - `Addons/OpenSimSearch`
    - `Addons/os-webrtc-janus/WebRtcVoiceRegionModule`
    - `Addons/os-webrtc-janus/WebRtcVoiceServiceModule`
- Each provider mirrors `.addin.xml` `/OpenSim/RegionModules` entries using assembly-local
    type-name resolution (`Assembly.GetType`) and preserves declared manifest version strings.

**Batch 7 result**: Addon region-module manifests now have corresponding code registrations
for provider-based discovery.

### Step 3 Scope Clarification: RegionServer Manifest

- `OpenSim.Server.RegionServer.addin.xml` currently defines extension points only
    (`/OpenSim/Startup`, `/OpenSim/AssetCache`, `/OpenSim/AssetClient`, `/OpenSim/WindModule`,
    `/OpenSim/RegionModules`) and does not contain extension entries to convert into
    `PluginDescriptor` registrations.

### Step 3 Coverage Snapshot

- All current `Source/` and `Addons/` manifests containing `<Extension path=...>` plugin entries
    now have corresponding provider-based `PluginRegistration.cs` mappings.
- Added migration guard-rail tests in `OpenSim.Framework.PluginMigration.Tests` to assert:
    - manifests with extension entries have a corresponding `PluginRegistration.cs`, and
        - each manifest extension entry `id`/`class` pair is represented in the provider source
            across both provider styles (`RegisterByName(..., "full.class.Name", ...)` and
            `new PluginDescriptor(..., typeof(TypeName), ...)`).
        - provider registrations are parity-checked bidirectionally against manifest
            `(path, id, class)` triplets (missing and unexpected entries).
        - reverse-parity checks now support an explicit keyed allowlist for intentional
            provider-only triplets (`relative/provider/path|path|id|type`) while keeping strict
            failure behavior by default.
        - manifest parsing accepts both `<... class="..." />` and `<... type="..." />`
            extension entry attributes to support startup-plugin manifests.
        - remaining `Mono.Addins` usage is constrained by tests to known intentional
            transitional locations (attribute and using-directive footprint guards).
        - provider-backed manifests remain embedded as csproj `EmbeddedResource` entries
            unless explicitly allowlisted for controlled removal pilots.
- Remaining manifest-only files without provider mappings are extension-point-only roots:
    - `Source/OpenSim.Server.RegionServer/Resources/OpenSim.Server.RegionServer.addin.xml`
    - `Source/OpenSim.Data/Resources/OpenSim.Data.addin.xml`
- These extension-point-only manifests are part of later cleanup work and do not block
    provider-based plugin registration migration for extension entries.

### Backend Selection Configuration

You can choose the discovery backend using either config or environment:

```ini
[Startup]
; monoaddins | reflection
PluginDiscovery = monoaddins

; Optional: disable plugin/repository management command registration
; while keeping connector discovery/loading behavior.
EnablePluginManagementCommands = true

[Modules]
; Optional override for region module discovery
PluginDiscovery = monoaddins

[Wind]
; Optional override for wind discovery
PluginDiscovery = monoaddins
```

Environment override (highest precedence where supported):

```bash
export OPENSIM_PLUGIN_DISCOVERY=reflection
```

Implementation note:
- `OPENSIM_PLUGIN_DISCOVERY` now strictly overrides configured backend values when both are present,
    and startup logs an explicit override message when the values differ.

### Transitional Scope Note

- The `reflection` selector now routes to the DotNetCorePlugins implementation and remains
    an incremental migration path that does not yet replace all Mono.Addins features
    (for example, repository/registry management and XML metadata semantics).
- Default behavior remains `monoaddins` until full migration is complete.
- Runtime startup parity capture is environment-dependent in current dev setup; compile-time
    parity validation with backend overrides is currently used as the stable smoke check.

---

## Current Architecture Analysis

### 1. Plugin Discovery & Loading Model

**Current (Mono.Addins)**:
```
1. .addin.xml embedded resources define extension points & extensions
2. AddinManager.Initialize() scans registry folder
3. Reflection + XML parsing discovers available plugins
4. AddinManager.GetExtensionNodes() retrieves typed nodes
5. node.CreateInstance() instantiates via Activator
```

**Change with DotNetCorePlugins**:
```
1. DLLs placed in designated plugin folders
2. PluginLoader scans folders directly
3. Reflection discovers types implementing plugin interfaces
4. Manual type discovery (no XML metadata)
5. System.Reflection.Activator.CreateInstance() for instantiation
```

### 2. Extension Points (Critical to Preserve)

| Extension Path | Interface | Usage | Plugins |
|---|---|---|---|
| `/OpenSim/Startup` | `IApplicationPlugin` | Server startup | RemoteController, LoadRegions, RegionModulesController |
| `/OpenSim/AssetCache` | `IAssetCache` | Asset caching backends | FlotsamAssetCache |
| `/OpenSim/AssetClient` | `IAssetServer` | Asset server backends | Multiple in CoreModules |
| `/OpenSim/WindModule` | `IWindModelPlugin` | Wind simulation models | Multiple wind models |
| `/OpenSim/RegionModules` | `IRegionModuleBase` | Region functionality | **20+ modules** (largest category) |
| `/Robust/Connector` | `IRobustConnector` | Robust server extensions | Grid, asset, user connectors |

### 3. Files to Modify/Replace

**Core Infrastructure** (must change):
- `Source/OpenSim.Framework/PluginManager.cs` → Redesign/remove
- `Source/OpenSim.Framework/PluginLoader.cs` → Replace with DotNetCorePlugins
- `Source/OpenSim.Framework/PluginExtensionNode.cs` → Simplify/adapt
- `Source/OpenSim.Server.Base/ServerUtils.cs` → Redesign PluginLoader
- `Source/OpenSim.Server.Base/CommandManager.cs` → Remove Mono.Addins dependencies

**Plugin Manifests** (28+ files to replace):
- All `Resources/*.addin.xml` files → Replace with code-based configuration

**NuGet Dependencies** (18 projects):
- Remove `Mono.Addins` refs
- Remove `Mono.Addins.Setup` refs
- Remove `Mono.Addins.CecilReflector` refs
- Add `McMaster.NETCore.Plugins` refs

---

## Phase 1: Infrastructure & Abstraction

### Phase 1.1: Create Plugin Loading Abstraction Layer

Before replacing, create interfaces to decouple from Mono.Addins:

```csharp
// New file: Source/OpenSim.Framework/IPluginDiscovery.cs
namespace OpenSim.Framework
{
    /// <summary>
    /// Abstraction for plugin discovery mechanism
    /// </summary>
    public interface IPluginDiscovery
    {
        /// <summary>
        /// Discover all plugin types for a given interface
        /// </summary>
        IEnumerable<Type> DiscoverPlugins(Type requiredInterface, Type nodeType);
        
        /// <summary>
        /// Get plugin metadata/configuration
        /// </summary>
        object GetPluginMetadata(Type pluginType);
    }
    
    /// <summary>
    /// Plugin loader abstraction
    /// </summary>
    public interface IPluginLoader<T> where T : class
    {
        IReadOnlyList<T> LoadedPlugins { get; }
        void LoadPlugins(string extensionPath, string pluginDirectory);
        void Dispose();
    }
}
```

### Phase 1.2: Create DotNetCorePlugins Adapter

```csharp
// New file: Source/OpenSim.Framework/DotNetCorePluginsAdapter.cs
namespace OpenSim.Framework
{
    /// <summary>
    /// Adapter for DotNetCorePlugins library
    /// </summary>
    public class DotNetCorePluginsAdapter : IPluginDiscovery
    {
        private readonly string _pluginPath;
        private readonly PluginLoadContextOptions _options;
        
        public DotNetCorePluginsAdapter(string pluginPath)
        {
            _pluginPath = pluginPath;
            _options = new PluginLoadContextOptions { PreferSharedTypes = true };
        }
        
        public IEnumerable<Type> DiscoverPlugins(Type requiredInterface, Type nodeType)
        {
            // Use reflection to scan plugin folder DLLs
            // Load each DLL dynamically and find types implementing requiredInterface
        }
        
        public object GetPluginMetadata(Type pluginType)
        {
            // Extract plugin attributes or return null
        }
    }
}
```

**Why**: Keeps Mono.Addins abstracted during transition; easier to test both implementations.

### Phase 1.3: Update PluginLoader<T> to Use Abstraction

Modify existing `PluginLoader<T>` to accept an `IPluginDiscovery`:

```csharp
public class PluginLoader<T> : IDisposable where T : IPlugin
{
    private IPluginDiscovery _discovery;
    private List<T> loaded = new List<T>();
    
    public PluginLoader(IPluginDiscovery discovery = null)
    {
        _discovery = discovery ?? new MonoAddinsDiscovery(); // Default to current
    }
    
    public void Load(string extpoint)
    {
        var pluginTypes = _discovery.DiscoverPlugins(typeof(T), typeof(PluginExtensionNode));
        // ... instantiate and initialize
    }
}
```

**Why**: Allows gradual migration; both systems can coexist during transition.

---

## Phase 2: Create DotNetCorePlugins Infrastructure

### Phase 2.1: Implement Config-Based Plugin Registration

Since DotNetCorePlugins doesn't have XML manifests, use code + config:

```csharp
// New file: Source/OpenSim.Framework/PluginRegistry.cs
namespace OpenSim.Framework
{
    /// <summary>
    /// Registry mapping extension points to plugin types
    /// Replaces .addin.xml functionality
    /// </summary>
    public class PluginRegistry
    {
        private Dictionary<string, List<Type>> _extensionRegistry = new();
        
        /// <summary>
        /// Register a plugin implementation for an extension point
        /// </summary>
        public void RegisterExtension(string extensionPath, Type pluginType)
        {
            if (!_extensionRegistry.ContainsKey(extensionPath))
                _extensionRegistry[extensionPath] = new List<Type>();
            
            _extensionRegistry[extensionPath].Add(pluginType);
        }
        
        /// <summary>
        /// Load registrations from config file (JSON/INI)
        /// </summary>
        public static PluginRegistry FromConfig(string configPath)
        {
            // Parse JSON/INI config specifying:
            // - Extension points
            // - Plugin types for each point
            // - Dependencies
        }
    }
}
```

### Phase 2.2: Implement Folder-Based Plugin Loader

```csharp
// New file: Source/OpenSim.Framework/DotNetCorePluginLoader.cs
namespace OpenSim.Framework
{
    public class DotNetCorePluginLoader<T> : IDisposable where T : class
    {
        private readonly string _pluginDirectory;
        private readonly PluginCollection<T> _loadedPlugins;
        
        public DotNetCorePluginLoader(string pluginDirectory)
        {
            _pluginDirectory = pluginDirectory;
            _loadedPlugins = new PluginCollection<T>();
        }
        
        /// <summary>
        /// Scan plugin directory and load matching plugins
        /// </summary>
        public void DiscoverAndLoad(string extensionPath)
        {
            var pluginDlls = Directory.GetFiles(_pluginDirectory, "*.dll");
            
            foreach (var dll in pluginDlls)
            {
                try
                {
                    var context = new AssemblyLoadContext(Path.GetFileName(dll), isCollectible: true);
                    var asm = context.LoadFromAssemblyPath(dll);
                    
                    var pluginTypes = asm.GetTypes()
                        .Where(t => typeof(T).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                    
                    foreach (var type in pluginTypes)
                    {
                        var instance = (T)Activator.CreateInstance(type);
                        _loadedPlugins.Add(instance);
                    }
                }
                catch (Exception ex)
                {
                    // Log error, continue
                }
            }
        }
        
        public IReadOnlyList<T> LoadedPlugins => _loadedPlugins.AsReadOnly();
        
        public void Dispose()
        {
            // Unload assemblies if using isolated AssemblyLoadContext
        }
    }
}
```

### Phase 2.3: Update Initialization Points

Identify where plugins are currently loaded:

**Location**: Various server startup code
- `Source/OpenSim.Server.RegionServer/*` - Region server plugin init
- `Source/OpenSim.Server.RobustServer/*` - Robust server plugin init  
- `Source/OpenSim.ApplicationPlugins.RegionModulesController/*` - Region module loading

---

## Phase 3: Adapt Extension Points

### Phase 3.1: Remove XML Manifests

**For each plugin assembly**:
1. Delete `Resources/*.addin.xml`
2. Remove `<EmbeddedResource>` entry from .csproj
3. Add static registration method or attributes

**Example conversion**:

**Before (XML)**:
```xml
<!-- OpenSim.Groups.addin.xml -->
<Extension path="/OpenSim/RegionModules">
    <RegionModule id="GroupsModule" class="OpenSim.Groups.GroupsModule" />
    <RegionModule id="GroupsMessagingModule" class="OpenSim.Groups.GroupsMessagingModule" />
</Extension>
```

**After (Code-based)**:
```csharp
// OpenSim/Addons/OpenSim.Addons.Groups/GroupsPlugin.cs
namespace OpenSim.Groups
{
    [PluginRegistration(ExtensionPath = "/OpenSim/RegionModules")]
    public class GroupsPluginRegistration
    {
        public static readonly PluginDescriptor[] Modules = new[]
        {
            new PluginDescriptor("GroupsModule", typeof(GroupsModule)),
            new PluginDescriptor("GroupsMessagingModule", typeof(GroupsMessagingModule)),
            new PluginDescriptor("GroupsServiceRemoteConnectorModule", typeof(GroupsServiceRemoteConnectorModule)),
            new PluginDescriptor("GroupsServiceLocalConnectorModule", typeof(GroupsServiceLocalConnectorModule)),
            new PluginDescriptor("GroupsServiceHGConnectorModule", typeof(GroupsServiceHGConnectorModule)),
        };
    }
}
```

### Phase 3.2: Create Plugin Discovery Methods

For each major extension point, create a static discovery:

```csharp
// New file: Source/OpenSim.Framework/PluginDiscoveryHelpers.cs
namespace OpenSim.Framework
{
    public static class PluginDiscoveryHelpers
    {
        /// <summary>
        /// Discover all RegionModuleBase implementations
        /// </summary>
        public static IEnumerable<Type> DiscoverRegionModules(string pluginPath)
        {
            return DiscoverPluginsInPath(pluginPath, typeof(IRegionModuleBase));
        }
        
        /// <summary>
        /// Discover all IWindModelPlugin implementations  
        /// </summary>
        public static IEnumerable<Type> DiscoverWindModels(string pluginPath)
        {
            return DiscoverPluginsInPath(pluginPath, typeof(IWindModelPlugin));
        }
        
        /// <summary>
        /// Generic plugin discovery by interface
        /// </summary>
        private static IEnumerable<Type> DiscoverPluginsInPath(string path, Type requiredInterface)
        {
            if (!Directory.Exists(path))
                yield break;
            
            foreach (var dll in Directory.GetFiles(path, "*.dll"))
            {
                try
                {
                    var asm = Assembly.Load(File.ReadAllBytes(dll));
                    foreach (var type in asm.GetTypes())
                    {
                        if (requiredInterface.IsAssignableFrom(type) && 
                            !type.IsInterface && !type.IsAbstract)
                        {
                            yield return type;
                        }
                    }
                }
                catch { /* Skip problematic DLLs */ }
            }
        }
    }
}
```

---

## Phase 4: Migration of Major Extension Points

### Phase 4.1: Region Modules (`/OpenSim/RegionModules`)

**Largest migration - 20+ modules**

**Changes needed**:
1. Update `ApplicationPlugins.RegionModulesController` to use new loader
2. Replace Mono.Addins registry scanning with folder scanning
3. Keep `IRegionModuleBase` interface unchanged
4. Update scene module loading logic

**File to update**:
- `Source/OpenSim.ApplicationPlugins.RegionModulesController/RegionModulesControllerPlugin.cs`

### Phase 4.2: Wind Modules

**Minimal changes**:
- Scan `Source/OpenSim.Region.CoreModules/World/Wind/` for implementations
- Keep `IWindModelPlugin` interface unchanged
- Update wind model selection logic

### Phase 4.3: Asset Cache & Servers

**Changes**:
- Asset cache plugins in CoreModules
- Keep interface unchanged
- Update config-driven instantiation

### Phase 4.4: Startup Plugins

**Changes**:
- Startup application plugins in `ApplicationPlugins/`
- Keep `IApplicationPlugin` interface unchanged
- Update startup sequence plugin discovery

### Phase 4.5: Robust Connectors

**Changes**:
- Update `ServerUtils.cs` plugin loader
- Keep `IRobustConnector` interface unchanged  
- Update connector discovery in Robust server

---

## Phase 5: Remove Mono.Addins

### Phase 5.1: Remove Package References

Remove from all 18 .csproj files:
```xml
<PackageReference Include="Mono.Addins" Version="1.4.1" />
<PackageReference Include="Mono.Addins.Setup" Version="1.4.1" />
<PackageReference Include="Mono.Addins.CecilReflector" Version="1.4.1" />
```

Add to affected projects:
```xml
<PackageReference Include="McMaster.NETCore.Plugins" Version="18.9" />
```

### Phase 5.2: Remove Deprecated Infrastructure

**Delete files**:
- `Source/OpenSim.Framework/PluginManager.cs` (no longer needed)
- `Source/OpenSim.Framework/PluginExtensionNode.cs` (XML-based, no longer needed)

**Modify files**:
- `Source/OpenSim.Framework/PluginLoader.cs` → Adapt or rebuild
- `Source/OpenSim.Server.Base/ServerUtils.cs` → Replace PluginLoader implementation
- `Source/OpenSim.Server.Base/CommandManager.cs` → Remove Mono.Addins code

### Phase 5.3: Clean Up Attributes

Remove Mono.Addins attributes:
```csharp
// Remove these:
[assembly:AddinRoot("...", "...")]
[TypeExtensionPoint(Path="...", Name="...")]
[Extension(Path="...", NodeName="...")]

// Replace with custom equivalents if needed:
[PluginRegistration(ExtensionPath="...", ...)]
```

---

## Phase 6: Testing & Validation

### Test Plan:

1. **Unit tests** for discovery mechanisms
2. **Integration tests** for plugin loading paths
3. **Regression tests** for each plugin type
4. **Manual testing** of startup sequences
5. **Performance benchmarks** (compared to Mono.Addins)

### Configuration Testing:

```ini
[Modules]
; New config format (INI-based plugin registry)
RegionModulesPath = "./bin/plugins/regionmodules"
WindModelsPath = "./bin/plugins/windmodels"
AssetCachePath = "./bin/plugins/assetcache"
```

### Smoke Test Commands (Current Transitional Backend)

Use these commands from repo root to verify both backends compile and load paths are reachable:

```bash
# Default backend (monoaddins)
dotnet build Source/OpenSim.Framework/OpenSim.Framework.csproj -c Debug
dotnet build Source/OpenSim.ApplicationPlugins.RegionModulesController/OpenSim.ApplicationPlugins.RegionModulesController.csproj -c Debug
dotnet build Source/OpenSim.Server.Base/OpenSim.Server.Base.csproj -c Debug
dotnet build Source/OpenSim.Region.CoreModules/OpenSim.Region.CoreModules.csproj -c Debug

# Reflection backend override
OPENSIM_PLUGIN_DISCOVERY=reflection dotnet build Source/OpenSim.Framework/OpenSim.Framework.csproj -c Debug
OPENSIM_PLUGIN_DISCOVERY=reflection dotnet build Source/OpenSim.ApplicationPlugins.RegionModulesController/OpenSim.ApplicationPlugins.RegionModulesController.csproj -c Debug
OPENSIM_PLUGIN_DISCOVERY=reflection dotnet build Source/OpenSim.Server.Base/OpenSim.Server.Base.csproj -c Debug
OPENSIM_PLUGIN_DISCOVERY=reflection dotnet build Source/OpenSim.Region.CoreModules/OpenSim.Region.CoreModules.csproj -c Debug
```

---

## Migration Timeline & Effort Estimates

| Phase | Tasks | Effort | Duration |
|-------|-------|--------|----------|
| 1.1 | Create abstraction layer | 8 hours | 1 day |
| 1.2 | Build DotNetCorePlugins adapter | 12 hours | 1.5 days |
| 1.3 | Update PluginLoader<T> | 6 hours | 1 day |
| 2.1 | Config-based registry | 10 hours | 1 day |
| 2.2 | Folder-based loader | 12 hours | 1.5 days |
| 2.3 | Init point updates | 8 hours | 1 day |
| 3.1 | Remove XML manifests (28 files) | 20 hours | 2 days |
| 3.2 | Create discovery helpers | 8 hours | 1 day |
| 4.1 | Region modules migration | 20 hours | 2.5 days |
| 4.2-4.5 | Other extension points | 12 hours | 1.5 days |
| 5.1-5.3 | Remove Mono.Addins | 10 hours | 1 day |
| 6 | Testing & validation | 30 hours | 3 days |
| **TOTAL** | | **156 hours** | **~3 weeks** |

---

## Risk Analysis & Mitigation

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|-----------|
| Plugins fail to load | Critical | Medium | Phase 1 abstraction; parallel testing |
| Performance regression | High | Medium | Benchmark each phase |
| Runtime enable/disable lost | Medium | High | Redesign registry system |
| Breaking changes for addon devs | High | High | Documentation; example migrations |
| Circular dependencies in plugins | Medium | Low | Dependency resolver or flatten structure |
| Memory leaks from AssemblyLoadContext | High | Medium | Careful lifecycle management; unit tests |

---

## Key Implementation Details

### 1. AssemblyLoadContext Considerations
- DotNetCorePlugins uses `AssemblyLoadContext` for isolation
- These **cannot be unloaded in .NET 8** if they reference shared types
- Consider `isCollectible: false` for long-lived plugins
- Shared types (interfaces) should be referenced via host, not plugin copy

### 2. Dependency Handling
- Mono.Addins had built-in dependency resolution
- DotNetCorePlugins relies on `PreferSharedTypes` option
- May need to implement a dependency graph or topological sort

### 3. Configuration Format
Consider standardized approach:
```json
{
  "extensions": [
    {
      "path": "/OpenSim/RegionModules",
      "assemblies": [
        "OpenSim.Region.CoreModules.dll",
        "OpenSim.Addons.Groups.dll"
      ]
    }
  ]
}
```

### 4. Backward Compatibility
- Addon developers familiar with Mono.Addins XML need documentation
- Create migration guide for existing addon developers
- Example addon showing new code-based approach

---

## Open Questions for Review

1. **Runtime enable/disable**: Do we need capability to enable/disable plugins at runtime without restart?
   - Current Mono.Addins supports this
   - DotNetCorePlugins doesn't
   - Workaround: Hot reload using separate AppDomain/AssemblyLoadContext

2. **Addon repository**: Is there a need to maintain addon repository functionality?
   - Current PluginManager supports installing from remote repos
   - DotNetCorePlugins is file-based only
   - Workaround: Build custom NuGet-based delivery system

3. **Configuration source**: Should plugin lists be defined in code or config?
   - Proposed: Config file (similar to current INI system)
   - Benefit: No recompilation needed to enable/disable plugins

4. **Plugin versioning**: How to handle multiple versions of same plugin?
   - Mono.Addins has version resolution
   - DotNetCorePlugins doesn't
   - Workaround: Folder-based versioning or assembly versions

---

## Appendix: File Inventory

### NuGet Packages to Remove (18 projects)
OpenSim.Framework.csproj, OpenSim.Server.Base.csproj, OpenSim.Services.Connectors.csproj, OpenSim.Data.MySQL.csproj, OpenSim.Region.ClientStack.LindenCaps.csproj, OpenSim.Tests.Common.csproj, plus all 11 addon projects

### .addin.xml Files to Replace (28 files)
All under `Resources/` in respective assemblies

### Core Infrastructure Files to Modify
PluginLoader.cs, PluginManager.cs, PluginExtensionNode.cs, ServerUtils.cs, CommandManager.cs

### Extension Point Interfaces to Preserve
- IApplicationPlugin
- IAssetCache
- IAssetServer
- IWindModelPlugin
- IRegionModuleBase
- IRobustConnector

---

**Document Version**: 1.0  
**Date**: 2026-04-15  
**Status**: Analysis Complete - Ready for Implementation Planning
