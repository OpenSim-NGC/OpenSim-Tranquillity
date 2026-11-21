# ARCHITECTURE.md: a map of the code

This file says where things are and how the parts connect. It does not set rules or describe the workflow:
see [HUMANS.md](HUMANS.md) for contributors and [AGENTS.md](AGENTS.md) for automated agents. Build steps are in
[Docs/BUILDING.md](Docs/BUILDING.md).

Tranquillity is a virtual world server derived from OpenSimulator. It speaks the Second Life viewer protocol
(UDP messages plus HTTP "capabilities") and splits into region servers, which run the simulation, and grid
services, which hold users, inventory, assets and the region directory.

## Build basics

- C# on .NET 10, one solution: [Tranquillity.sln](Tranquillity.sln). [global.json](global.json) pins the SDK.
  [Directory.Build.props](Directory.Build.props) sets `net10.0`, `ImplicitUsings` on and `Nullable` off for every
  project, and adds Nerdbank.GitVersioning ([version.json](version.json)), `nini-core` (INI configuration) and
  the `UtopiaSkye.OpenMetaverse*` packages (viewer protocol types and packets).
- Each server project references its libraries and plugins, so they land in its output folder, where plugins are
  found at run time (see [Plugin discovery](#plugin-discovery)).

## Top-level layout

| Path | What it holds |
| --- | --- |
| [Source/](Source/) | The 72 projects of the servers and their libraries. |
| [Addons/](Addons/) | Optional modules built as separate projects: groups, offline IM, search, mute list, Gloebit money, WebRTC voice. |
| [Tests/](Tests/) | Test projects, shared test helpers, test runners and the CI known-failures list. |
| [Library/](Library/) | Prebuilt third-party DLLs referenced by `HintPath` (`xmlrpc`, `C5`, `NDesk.Options`, `BulletXNA`, ...). |
| [Docs/](Docs/) | Design notes, setup guides and feature specifications. |
| [tools/](tools/) | Build, pack and publish scripts, the CI test-result checker, an NUnit-to-xUnit converter. |
| [ThirdPartyLicenses/](ThirdPartyLicenses/) | Licence texts of bundled third-party code. |
| [.github/](.github/) | CI workflows (`dotnetci.yml` builds and tests; `codeql-analysis.yml`). |
| [compose.yaml](compose.yaml), [compose.mounts.yaml](compose.mounts.yaml) | Docker Compose for the three servers; each server has a `Dockerfile` in its project folder. |

## The three server processes

All three are .NET generic-host programs with the same shape: `Program.Main` parses the command line with
System.CommandLine, adds the INI files to the host `IConfiguration`, uses Autofac as the service provider, and
calls [`RegisterServices.Register`](Source/OpenSim.Server.Base/RegisterServices.cs) to run every Autofac
`IModule` found in `OpenSim.*.dll` beside the executable and in `addon-modules/`. The real work runs in an
`IHostedService`. Shared hosting pieces (console, logging, PID file, startup failure handling) are in
[Source/OpenSim.Server.Base/Hosting/](Source/OpenSim.Server.Base/Hosting/). The hosting design is described in
[Docs/HostedServiceArchitecture.md](Docs/HostedServiceArchitecture.md).

Each server project has an `AppData/` folder that is copied flat into the output folder: example INI files,
start scripts and data files.

### RegionServer (the simulator)

Project: [Source/OpenSim.Server.RegionServer](Source/OpenSim.Server.RegionServer/).

1. [`Program.cs`](Source/OpenSim.Server.RegionServer/Program.cs): options `--inimaster` (default
   `RegionServerDefaults.ini`), `--inifile` (repeatable), `--inidirectory` (default `config`), `--console`
   (`basic`, `local`, `rest`) and `--background`. Registers `RegionService` and, in the foreground,
   `RegionConsoleRunnerService`.
2. [`RegionService`](Source/OpenSim.Server.RegionServer/RegionService.cs) calls
   [`RegionRuntime.Initialize`](Source/OpenSim.Server.RegionServer/RegionRuntime.cs), which turns the options
   into a Nini `[Startup]` section and creates [`OpenSim`](Source/OpenSim.Server.RegionServer/OpenSim.cs)
   (console commands), a subclass of [`OpenSimBase`](Source/OpenSim.Server.RegionServer/OpenSimBase.cs) and
   [`RegionApplicationBase`](Source/OpenSim.Server.RegionServer/RegionApplicationBase.cs).
3. [`ConfigurationLoader`](Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs) builds the Nini
   `IConfigSource` that modules read: master file, then each `--inifile`, then `Include-*` files, then the INI
   directory, then environment variables, then `${Section|Key}` substitution.
4. `OpenSimBase.StartupSpecific` loads the region datastore (`[SimulationDataStore] LocalServiceModule`) and the
   estate datastore (`[EstateDataStore]`), starts the HTTP listener
   ([`RegionHttpServerFactory`](Source/OpenSim.Server.RegionServer/RegionHttpServerFactory.cs)), then loads the
   application plugins through [`RegionPluginService`](Source/OpenSim.Server.RegionServer/RegionPluginService.cs)
   (extension point `/OpenSim/Startup`):
   - [`LoadRegionsPlugin`](Source/OpenSim.ApplicationPlugins.LoadRegions/LoadRegionsPlugin.cs) reads region
     definitions from `Regions/*.ini` (`RegionLoaderFileSystem`) or from a URL (`RegionLoaderWebServer`) and
     creates one `Scene` per region.
   - [`RegionModulesControllerPlugin`](Source/OpenSim.ApplicationPlugins.RegionModulesController/RegionModulesControllerPlugin.cs)
     discovers region modules and attaches them to each new scene (see [Region modules](#region-modules)).
   - [`RemoteAdminPlugin`](Source/OpenSim.ApplicationPlugins.RemoteController/RemoteAdminPlugin.cs) serves the
     XML-RPC remote admin interface.
5. For each region `OpenSimBase.SetupScene` creates the `Scene`, calls
   `IRegionModulesController.AddRegionToModules(scene)` and then `scene.SetModuleInterfaces()`.

Shipped configuration lives in
[Source/OpenSim.Server.RegionServer/AppData/](Source/OpenSim.Server.RegionServer/AppData/):
`RegionServerDefaults.ini` (all defaults), `RegionServer.ini.example`, `config-include/` (architectures),
`Regions/Regions.ini.example`, `Estates/Estates.ini.example`, and `Scripts/region-start.sh`, which passes
`--inimaster RegionServerDefaults.ini --inifile <config dir>/RegionServer.ini`.

### GridServer (grid services, the former "Robust")

Project: [Source/OpenSim.Server.GridServer](Source/OpenSim.Server.GridServer/).

1. [`Program.cs`](Source/OpenSim.Server.GridServer/Program.cs): same options; `--inimaster` defaults to
   `OpenSim.Server.GridServer.ini`. Registers `GridService` and `GridConsoleRunnerService`.
2. [`GridService`](Source/OpenSim.Server.GridServer/GridService.cs) starts
   [`GridServerRuntime`](Source/OpenSim.Server.GridServer/GridServerRuntime.cs), which reads the INI files with
   [`GridServerConfigSource`](Source/OpenSim.Server.GridServer/GridServerConfigSource.cs) (same merge rules as
   the region server) and opens the HTTP ports with
   [`GridHttpServerFactory`](Source/OpenSim.Server.GridServer/GridHttpServerFactory.cs).
3. [`GridServiceConnectorLoader`](Source/OpenSim.Server.GridServer/GridServiceConnectorLoader.cs) reads
   `[Startup] ServiceConnectors` and every entry of `[ServiceList]`. Each entry has the form
   `[configName@]port/Assembly.dll:ClassName`, for example
   `8003/OpenSim.Server.Handlers.dll:AssetServiceConnector`. It loads each class with `ServerUtils.LoadPlugin`
   as an `IServiceConnector` bound to that port.
4. After that, `ServerUtils.PluginLoader` loads any `IRobustConnector` plugins (extension point
   `/Robust/Connector`) from `[Startup] RegistryLocation`.

Shipped configuration: `GridServer.ini.example` and `GridServer.HG.ini.example` (hypergrid) in
[Source/OpenSim.Server.GridServer/AppData/](Source/OpenSim.Server.GridServer/AppData/), and
`Scripts/grid-start.sh`, which passes `--inifile GridServer.<SERVICENAME>.ini`.

### MoneyServer (currency)

Project: [Source/OpenSim.Server.MoneyServer](Source/OpenSim.Server.MoneyServer/).

- [`Program.cs`](Source/OpenSim.Server.MoneyServer/Program.cs): `--inimaster` defaults to `MoneyServer.ini`.
  Unlike the other two it also hosts ASP.NET Core: controllers are mapped with `MapControllers`, on
  `[MoneyServer] AspNetUrls` or `AspNetPort` (default 8009).
- [`MoneyXmlRpcController`](Source/OpenSim.Server.MoneyServer/Controllers/MoneyXmlRpcController.cs) (route
  `xmlrpc`) dispatches XML-RPC calls to the client, transaction and web controllers in `Controllers/`.
- [`MoneyService`](Source/OpenSim.Server.MoneyServer/MoneyService.cs) and `MoneyDBService` hold the logic;
  storage is [Source/OpenSim.Data.MySQL.MoneyData](Source/OpenSim.Data.MySQL.MoneyData/) (MySQL only).
- The region-side client is `DTLNSLMoneyModule` in
  [Source/OpenSim.Region.OptionalModules/World/Currency/](Source/OpenSim.Region.OptionalModules/World/Currency/).
- Shipped configuration: `AppData/MoneyServer.ini.example` and `AppData/RegionServer.ini.sample`.

## Configuration

Configuration is INI text read through Nini. Three mechanisms choose what runs.

| Mechanism | Where | Effect |
| --- | --- | --- |
| `Include-*` keys | any section, usually `[Architecture]` and `[Includes]` | Pulls in another INI file. `RegionServer.ini.example` selects one of `config-include/Standalone.ini`, `StandaloneHypergrid.ini`, `Grid.ini` or `GridHypergrid.ini`; those include the matching `*Common.ini`. `RegionServerDefaults.ini` includes `addon-modules/*/config/*.ini`. |
| `[Modules]` names | region server | Each service-connector module enables itself only when its key names it, for example `AssetServices = "RegionAssetConnector"`, `InventoryServices = "RemoteXInventoryServicesConnector"`, `EntityTransferModule = "BasicEntityTransferModule"`. `Setup_<ModuleId> = disabled` stops a module from loading at all. |
| `Assembly.dll:ClassName` strings | `LocalServiceModule`, `StorageProvider`, `[ServiceList]`, connector keys | Loaded by reflection with `ServerUtils.LoadPlugin` ([Source/OpenSim.Server.Base/ServerUtils.cs](Source/OpenSim.Server.Base/ServerUtils.cs)) or `ServiceBase.LoadPlugin` ([Source/OpenSim.Services.Base/ServiceBase.cs](Source/OpenSim.Services.Base/ServiceBase.cs)). Example: `[AssetService] LocalServiceModule = "OpenSim.Services.AssetService.dll:AssetService"`. A bare DLL name (`StorageProvider = "OpenSim.Data.MySQL.dll"`) means the first type in it that implements the wanted interface. |

Other selectors worth knowing, all in `RegionServerDefaults.ini`: `[Startup] physics` (default `ubODE`),
`[Startup] meshing` (default `ubODEMeshmerizer`), `[Startup] DefaultScriptEngine` (default `InWorldz.Phlox`;
see [Docs/ScriptEngineSelection.md](Docs/ScriptEngineSelection.md)), and `[Startup] permissionmodules`.

Shipped INI files are in each server's `AppData/` (listed above). The region server's
[config-include/](Source/OpenSim.Server.RegionServer/AppData/config-include/) also holds `GridCommon.ini.example`,
`StandaloneCommon.ini.example`, `FlotsamCache.ini.example`, `osslEnable.ini.example` and
`storage/SQLiteStandalone.ini`. Default asset sets and the inventory library are in
[Source/OpenSim.Services.InventoryService/AppData/](Source/OpenSim.Services.InventoryService/AppData/).

## Plugin discovery

Plugins are found by [`DotNetCorePluginsDiscovery`](Source/OpenSim.Framework/IPluginDiscovery.cs) (built on
McMaster.NETCore.Plugins) and loaded by
[`PluginLoader<T>`](Source/OpenSim.Framework/PluginLoader.cs). It scans the `*.dll` files of one folder (the
current directory, `.`, for region modules; the start scripts `cd` into the binary folder first). It skips
known third-party assemblies by name prefix, then returns, for an extension point:

1. types registered in code by an `IPluginRegistryProvider` (a `PluginRegistration.cs` in the assembly), then
2. any other concrete type in the scanned assemblies that implements the requested interface.

| Extension point | Interface | Loaded by |
| --- | --- | --- |
| `/OpenSim/Startup` | `IApplicationPlugin` | `RegionPluginService` |
| `/OpenSim/RegionModules` | `IRegionModuleBase` | `RegionModulesControllerPlugin` |
| `/OpenSim/WindModule` | `IWindModelPlugin` | the wind module in `CoreModules/World/Wind` |
| `/Robust/Connector` | `IRobustConnector` | `ServerUtils.PluginLoader` (GridServer) |

How to write a plugin: [Docs/PLUGIN_DEVELOPMENT.md](Docs/PLUGIN_DEVELOPMENT.md). History of the move from
Mono.Addins: [Docs/PLUGIN_MIGRATION_PLAN.md](Docs/PLUGIN_MIGRATION_PLAN.md).

## Region modules

Almost every region feature is a region module. The interfaces are in
[Source/OpenSim.Region.Framework/Interfaces/](Source/OpenSim.Region.Framework/Interfaces/):
`IRegionModuleBase`, `ISharedRegionModule` (one instance for the whole process) and `INonSharedRegionModule`
(one instance per scene).

Lifecycle, as driven by `RegionModulesControllerPlugin`:

| Step | Shared module | Non-shared module |
| --- | --- | --- |
| Construct | once, at startup | once per scene, when the scene is added |
| `Initialise(IConfigSource)` | once | once per instance |
| `PostInitialise()` | once, after all shared modules are initialised | not called |
| `AddRegion(Scene)` | for every scene | once |
| `RegionLoaded(Scene)` | for every scene, after all modules ran `AddRegion` | once |
| `RemoveRegion(Scene)` | for every scene removed | once |
| `Close()` | at shutdown | when its scene closes |

- `ReplaceableInterface`: a module that returns an interface type here is loaded last, and only if no other
  module registered that interface. It is used for default implementations that others may replace.
- Modules publish services on the scene with `scene.RegisterModuleInterface<T>(this)` (one implementation) or
  `StackModuleInterface<T>` (several), usually in `AddRegion`. Other modules get them with
  `scene.RequestModuleInterface<T>()` or `RequestModuleInterfaces<T>()`, usually in `RegionLoaded`. These live in
  [SceneBase.cs](Source/OpenSim.Region.Framework/Scenes/SceneBase.cs); `Scene.SetModuleInterfaces()` caches the
  common ones on the scene.
- Modules react to the world through the events of
  [`EventManager`](Source/OpenSim.Region.Framework/Scenes/EventManager.cs) (`scene.EventManager`), for example
  `OnNewClient`, `OnNewPresence`, `OnRegisterCaps`.
- Registration: listed in the assembly's `PluginRegistration.cs` (for example
  [CoreModules](Source/OpenSim.Region.CoreModules/PluginRegistration.cs)) or found by type scan. Minimal modules:
  [Source/OpenSim.Region.OptionalModules/Example/](Source/OpenSim.Region.OptionalModules/Example/).

## Request paths

### Login and arrival

```
viewer XML-RPC login
  -> LLLoginServiceInConnector        Source/OpenSim.Server.Handlers/Login/ (GridServer; in standalone,
                                      CoreModules/ServiceConnectorsIn/Login/LLLoginServiceInConnectorModule)
  -> LLLoginService                   Source/OpenSim.Services.LLLoginService/LLLoginService.cs
  -> ISimulationService.CreateAgent   SimulationServiceConnector (HTTP) or LocalSimulationConnector
  -> region: SimulationServiceInConnector / AgentSimpleHandler   Source/OpenSim.Server.Handlers/Simulation/
  -> LocalSimulationConnector -> Scene.NewUserConnection -> CapabilitiesModule.CreateCaps
```

### Viewer UDP

```
viewer UDP packet
  -> LLUDPServer           Source/OpenSim.Region.ClientStack.LindenUDP/LLUDPServer.cs
                           (created per region by LLUDPServerShim, an INonSharedRegionModule)
  -> UseCircuitCode: LLUDPServer.AddClient -> new LLClientView (IClientAPI)
  -> Scene.AddNewAgent -> ScenePresence; Scene.SubscribeToClientEvents; EventManager.OnNewClient
  -> later packets: LLClientView raises IClientAPI events
  -> handlers in Scene (Scene.PacketHandlers.cs, Scene.Inventory.cs, ...) and in modules that subscribed
```

Outgoing traffic goes the other way: a module calls an `IClientAPI` method on the presence's client, and
`LLClientView` builds the packet and queues it on `LLUDPServer` (throttles in `TokenBucket.cs`,
`ThrottleRates.cs`).

### Viewer HTTP capabilities

```
Scene.NewUserConnection -> CapabilitiesModule.CreateCaps   Source/OpenSim.Region.CoreModules/Framework/Caps/
  -> new Caps (on the region HTTP server)                  Source/OpenSim.Capabilities/Caps.cs
  -> EventManager.OnRegisterCaps -> each caps module registers its handlers on that Caps object:
       BunchOfCapsModule -> BunchOfCaps (SEED and most upload and inventory caps)   LindenCaps/BunchOfCaps/
       EventQueueGetModule (server-to-viewer event queue)                           LindenCaps/EventQueue/
       AISv3Module -> AisHandler (InventoryAPIv3, LibraryAPIv3; [AIS] Enabled)      LindenCaps/AIS/
       GetAssetsModule, FetchInventory2Module, WebFetchInvDescModule, ...           LindenCaps/
  -> handlers call Scene methods or scene services (IInventoryService, IAssetService, ...)
```

Shared handler classes (GetTexture, GetMesh, GetAssets, FetchInventory) are in
[Source/OpenSim.Capabilities.Handlers](Source/OpenSim.Capabilities.Handlers/). AIS v3 is specified in
[Docs/feature/ais-v3/AIS-V3-SPEC.md](Docs/feature/ais-v3/AIS-V3-SPEC.md).

### Region to grid services

```
region module or Scene code
  -> scene.RequestModuleInterface<IXxxService>()
  -> outbound connector module           Source/OpenSim.Region.CoreModules/ServiceConnectorsOut/<Service>/
       Local*   : calls the OpenSim.Services.* class in-process (standalone)
       Remote*  : wraps an HTTP client from OpenSim.Services.Connectors (grid)
  -> HTTP client connector                Source/OpenSim.Services.Connectors/<Service>/
  -> GridServer handler (IServiceConnector + stream handlers)   Source/OpenSim.Server.Handlers/<Service>/
  -> service implementation               Source/OpenSim.Services.*/
  -> data plugin                          Source/OpenSim.Data.{MySQL,PGSQL,SQLite,Null}/
```

Example, inventory in grid mode: `RemoteXInventoryServicesConnector` → `XInventoryServicesConnector` →
`XInventoryInConnector` → `XInventoryService` → `MySQLXInventoryData`. Service contracts (`IAssetService`,
`IInventoryService`, `IGridService`, ...) are in
[Source/OpenSim.Services.Interfaces](Source/OpenSim.Services.Interfaces/).

[Source/OpenSim.Region.CoreModules/ServiceConnectorsIn/](Source/OpenSim.Region.CoreModules/ServiceConnectorsIn/)
does the reverse: it mounts `OpenSim.Server.Handlers` connectors on the region's own HTTP server, which a
standalone uses for login and map, and a grid region uses for incoming simulation, neighbour and land calls.

### Region to region

```
EntityTransferModule (teleport, border crossing)   Source/OpenSim.Region.CoreModules/Framework/EntityTransfer/
  -> scene.SimulationService (ISimulationService)
  -> RemoteSimulationConnector -> SimulationServiceConnector (HTTP)   or LocalSimulationConnector (same process)
  -> destination: SimulationServiceInConnectorModule -> SimulationServiceInConnector
  -> AgentSimpleHandler / ObjectSimpleHandler -> LocalSimulationConnector
  -> destination Scene (NewUserConnection, UpdateAgent, object arrival)
```

Neighbour notifications go through `NeighbourServiceOutConnector` and the neighbour handlers in
`OpenSim.Server.Handlers/Neighbour`.

### Hypergrid

- Region side: `HGEntityTransferModule` (a subclass of `EntityTransferModule`) reaches the remote gatekeeper and
  the user's home grid through `GatekeeperServiceConnector` and `UserAgentServiceConnector`
  ([Source/OpenSim.Services.Connectors/Hypergrid/](Source/OpenSim.Services.Connectors/Hypergrid/)).
  `HGInventoryBroker`, `HGInventoryAccessModule` and `HGAssetMapper` handle foreign inventory and assets.
- Grid side: handlers in [Source/OpenSim.Server.Handlers/Hypergrid/](Source/OpenSim.Server.Handlers/Hypergrid/)
  (`GatekeeperServerConnector`, `UserAgentServerConnector`, `HeloServerConnector`, ...) call
  [Source/OpenSim.Services.HypergridService/](Source/OpenSim.Services.HypergridService/) (`GatekeeperService`,
  `UserAgentService`, `HGAssetService`, `HGSuitcaseInventoryService`, `TrustedGridRegistryService`, ...).
- Trusted hypergrid (signed grid-to-grid trust): [Source/OpenSim.Framework/TrustedHypergrid/](Source/OpenSim.Framework/TrustedHypergrid/),
  design in [Docs/feature/trusted-hypergrid/](Docs/feature/trusted-hypergrid/). Config:
  `config-include/GridHypergrid.ini`, `StandaloneHypergrid.ini`, `GridServer.HG.ini.example`.

## Project map

Every project in `Source/`, grouped. Paths are `Source/<name>/` unless stated.

### Framework, hosting and executables

| Project | Role |
| --- | --- |
| `OpenSim.Framework` | Core types (`RegionInfo`, `AgentCircuitData`, `IClientAPI`, utilities), plugin discovery and registry. |
| `OpenSim.Framework.Console` | Command console: local, basic and REST (remote) consoles. |
| `OpenSim.Framework.Monitoring` | Stats, health checks, watchdog. |
| `OpenSim.Framework.Serialization` | Tar archive reader and writer and archive constants used by OAR and IAR files. |
| `OpenSim.Framework.Servers` | `BaseOpenSimServer`, `ServerBase`, `MainServer` (the process-wide HTTP server registry). |
| `OpenSim.Framework.Servers.HttpServer` | `BaseHttpServer`, the embedded HTTP server (`OSHttpServer/`) and handler base classes. |
| `OpenSim.Framework.AssetLoader.Filesystem` | Loads default asset sets from `assets/AssetSets.xml`. |
| `OpenSim.ConsoleClient` | Executable that connects to a server's REST console. |
| `OpenSim.Server.Base` | Shared host code (`Hosting/`), `ServerUtils` (`LoadPlugin`, `IServiceConnector`, `IRobustConnector`), `RegisterServices`. |
| `OpenSim.Server.RegionServer` | Region simulator executable. |
| `OpenSim.Server.GridServer` | Grid services executable. |
| `OpenSim.Server.MoneyServer` | Currency server executable (ASP.NET Core + XML-RPC). |
| `OpenSim.Server.Handlers` | HTTP handlers ("in connectors") for every grid service, one folder per service. |
| `OpenSim.ApplicationPlugins.LoadRegions` | Startup plugin that reads region definitions and creates scenes. |
| `OpenSim.ApplicationPlugins.RegionModulesController` | Startup plugin that discovers and runs region modules. |
| `OpenSim.ApplicationPlugins.RemoteController` | Remote admin (XML-RPC) startup plugin. |

### Region, client protocol, rendering and appearance

| Project | Role |
| --- | --- |
| `OpenSim.Region.Framework` | The simulation model: `Scene`, `SceneGraph`, `SceneObjectGroup`, `SceneObjectPart`, `ScenePresence`, `EventManager`, `SceneManager` (in `Scenes/`), and the module and service interfaces (in `Interfaces/`). |
| `OpenSim.Region.CoreModules` | Standard region modules: `Agent/`, `Asset/` (`FlotsamAssetCache`), `Avatar/` (attachments, chat, friends, inventory, IM, ...), `Framework/` (caps, entity transfer, inventory access, user management), `Scripting/` (HTTP requests, listen, XML-RPC), `World/` (land, estate, terrain, archiver, permissions, map, wind), `ServiceConnectorsIn/`, `ServiceConnectorsOut/`. |
| `OpenSim.Region.OptionalModules` | Modules that are off by default: NPC, JsonStore, money modules (`World/Currency`, `World/MoneyModule`), server-side baking (`Avatar/ServerSideBaking`), XML-RPC groups, materials, data snapshot, examples. |
| `OpenSim.Region.ClientStack.LindenUDP` | Viewer UDP protocol: `LLUDPServer`, `LLClientView`, packet pooling, throttles, texture sending. |
| `OpenSim.Region.ClientStack.LindenCaps` | Viewer capability modules: `BunchOfCaps/`, `EventQueue/`, `AIS/`, `ObjectCaps/` and single-file modules. |
| `OpenSim.Capabilities` | `Caps` (per-agent capability set) and LLSD types. |
| `OpenSim.Capabilities.Handlers` | Reusable capability handlers: `GetTexture`, `GetMesh`, `GetAssets`, `FetchInventory`. |
| `Warp3D` | Software 3D renderer used by `CoreModules/World/Warp3DMap` for map tiles. |
| `OpenSimNGC.Appearance.Baking` | Server-side avatar bake compositor (SkiaSharp), used by `OptionalModules/Avatar/ServerSideBaking`; design in [Docs/feature/ssb-appearance/](Docs/feature/ssb-appearance/). |
| `OpenSimNGC.Appearance.Baking.Tests` | Its xUnit tests (the one test project under `Source/`). |

### Script engines

| Project | Role |
| --- | --- |
| `OpenSim.Region.ScriptEngine.Shared` | Script API shared by engines: `Api/LSL_Api.cs`, `OSSL_Api.cs`, `LS_Api.cs`, `MOD_Api.cs`, `LSL_Types.cs`, engine interfaces. |
| `OpenSim.Region.ScriptEngine.YEngine` | YEngine: compiles LSL to .NET IL (`MMR*` compiler, `XMR*` runtime). |
| `Phlox.ScriptEngine` | Phlox region module (`PhloxEngine`) and its scheduler, loader and LSL system API. |
| `InWorldz.Phlox` | Phlox compiler and bytecode VM for LSL, OSSL and SLua (`Compiler/`, `ByteCompiler/`, `VM/`, `SLua/`, `Glue/`). |
| `InWorldz.Phlox.Tools` | Generator for Phlox glue code. Not in the solution. |

Engine choice per script: [Docs/ScriptEngineSelection.md](Docs/ScriptEngineSelection.md). Phlox:
[Docs/PhloxSetup.md](Docs/PhloxSetup.md), [Docs/PhloxSLua.md](Docs/PhloxSLua.md),
[Docs/PhloxKnownDefects.md](Docs/PhloxKnownDefects.md).

### Physics

Each engine is a non-shared region module that activates when `[Startup] physics` names it, and implements
`PhysicsScene` from `SharedBase`.

| Project | Role |
| --- | --- |
| `OpenSim.Region.PhysicsModules.SharedBase` | `PhysicsScene`, `PhysicsActor`, `IMesher` and other contracts. |
| `OpenSim.Region.PhysicsModules.ubODE` | ubODE engine (default); native ODE libraries under `runtimes/`. |
| `OpenSim.Region.PhysicsModules.ubODEMeshing` | Mesher used with ubODE (`ubODEMeshmerizer`). |
| `OpenSim.Region.PhysicsModules.BulletS` | BulletSim engine; native libraries under `runtimes/`. |
| `OpenSim.Region.PhysicsModules.Jolt` | Jolt engine region module (`JoltModule`, `JoltScene`). |
| `OpenSim.Region.PhysicsModules.Jolt.Backend` | Wrapper over JoltPhysicsSharp and the native Jolt library (`JoltNative.targets`). |
| `OpenSim.Region.PhysicsModules.Jolt.Vehicles` | Vehicle dynamics for Jolt. |
| `OpenSim.Region.PhysicsModules.Jolt.Diagnostics` | Optional diagnostic console commands; not referenced by the region server. |
| `OpenSim.Region.PhysicsModules.BasicPhysics` | Minimal engine. |
| `OpenSim.Region.PhysicsModules.POS` | Minimal engine. |
| `OpenSim.Region.PhysicsModules.Meshing` | `Meshmerizer` and `ZeroMesher` meshers. |
| `OpenSim.Region.PhysicsModules.ConvexDecompositionDotNet` | Convex decomposition library. |

Jolt: [Docs/JoltPhysics.md](Docs/JoltPhysics.md), [Docs/JoltcNative.md](Docs/JoltcNative.md).

### Grid services

`OpenSim.Services.Interfaces` holds the contracts, `OpenSim.Services.Base` holds `ServiceBase` (plugin
loading), and `OpenSim.Services.Connectors` holds the HTTP clients for every service (one folder per service).
The implementations:

| Project | Implements |
| --- | --- |
| `OpenSim.Services.AssetService` | `AssetService` (assets in the database) and the older de-duplicating `XAssetService`. |
| `OpenSim.Services.FSAssetService` | `FSAssetConnector`: asset data on disk (`BaseDirectory`, `SpoolDirectory`), metadata in the database. |
| `OpenSim.Services.AuthenticationService` | Password and web-key authentication. |
| `OpenSim.Services.AuthorizationService` | Region access authorisation. |
| `OpenSim.Services.AvatarService` | Stored avatar appearance; `AppearanceService` for server-side baking. |
| `OpenSim.Services.EstateService` | `EstateDataService` (estate datastore used by regions). |
| `OpenSim.Services.ExperienceService` | Experiences ([Docs/ExperienceSetup.md](Docs/ExperienceSetup.md)). |
| `OpenSim.Services.FreeswitchService` | FreeSWITCH voice configuration. |
| `OpenSim.Services.Friends` | Friends list (assembly `OpenSim.Services.FriendsService`). |
| `OpenSim.Services.GridService` | Region directory; `HypergridLinker`. |
| `OpenSim.Services.HypergridService` | Gatekeeper, user agent, HG asset/inventory/friends/IM, trusted grid registry. |
| `OpenSim.Services.InventoryService` | `XInventoryService`, `LibraryService`; default `assets/` and `inventory/` data. |
| `OpenSim.Services.LLLoginService` | Viewer login. |
| `OpenSim.Services.MapImageService` | Map tile storage. |
| `OpenSim.Services.MuteListService` | Mute lists. |
| `OpenSim.Services.PresenceService` | Who is online and where. |
| `OpenSim.Services.SimulationService` | `SimulationDataService` (region datastore wrapper). Region-to-region transfer itself is in the connectors above. |
| `OpenSim.Services.UserAccountService` | User accounts, grid user, agent preferences, user aliases. |
| `OpenSim.Services.UserProfilesService` | Profiles, picks, classifieds. |

### Data

| Project | Role |
| --- | --- |
| `OpenSim.Data` | Data interfaces (`IAssetDataPlugin`, `IXInventoryData`, `IRegionData`, `IEstateDataStore`, ...) and the `Migration` runner. |
| `OpenSim.Data.MySQL` | MySQL/MariaDB implementations (most complete set). |
| `OpenSim.Data.PGSQL` | PostgreSQL implementations. |
| `OpenSim.Data.SQLite` | SQLite implementations (standalone; fewer stores). |
| `OpenSim.Data.Null` | In-memory implementations, used in tests and for unneeded stores. |
| `OpenSim.Data.MySQL.MoneyData` | MoneyServer storage. |
| `OpenSim.Data.Model` | Entity Framework Core models for the MySQL schemas. No other project references it. |

### Addons

All are referenced by the region server project and found by plugin discovery.

| Folder | Role |
| --- | --- |
| [Addons/OpenSim.Addons.Groups](Addons/OpenSim.Addons.Groups/) | Groups V2: region module and `GroupsServiceRobustConnector` for the grid server. |
| [Addons/OpenSim.Addons.OfflineIM](Addons/OpenSim.Addons.OfflineIM/) | Offline instant messages; `OfflineIMServiceRobustConnector`. |
| [Addons/OpenSimSearch](Addons/OpenSimSearch/) | In-world search module. |
| [Addons/OpenSimMutelist](Addons/OpenSimMutelist/) | Mute list module. |
| [Addons/Gloebit.GloebitMoneyModule](Addons/Gloebit.GloebitMoneyModule/) | Gloebit currency module, with its own migrations. |
| [Addons/os-webrtc-janus](Addons/os-webrtc-janus/) | WebRTC voice: `Janus` (Janus gateway client), `WebRtcVoice`, `WebRtcVoiceRegionModule`, `WebRtcVoiceServiceModule`, `Visibility`. See [Docs/voice/](Docs/voice/README.md). |

## Data and storage

- **Assets.** On the grid server, `[AssetService] LocalServiceModule` picks `AssetService` (database),
  `FSAssetConnector` (file system plus database index) or a hypergrid variant (`HGAssetService`,
  `HGFSAssetService`, `HGRemoteAssetService`). On the region, `[Modules] AssetServices = "RegionAssetConnector"`
  ([RegionAssetConnectorModule.cs](Source/OpenSim.Region.CoreModules/ServiceConnectorsOut/Asset/RegionAssetConnectorModule.cs))
  serves `IAssetService` to the scene, backed by the local or remote service and the region asset cache
  (`FlotsamAssetCache`, configured by `config-include/FlotsamCache.ini.example`).
- **Region datastore.** `[SimulationDataStore] LocalServiceModule = "OpenSim.Services.SimulationService.dll:SimulationDataService"`
  loads an `ISimulationDataStore` (interface in
  [Source/OpenSim.Region.Framework/Interfaces/ISimulationDataStore.cs](Source/OpenSim.Region.Framework/Interfaces/ISimulationDataStore.cs))
  from `StorageProvider`, for example `MySQLSimulationData`, `PGSQLSimulationData`, `SQLiteSimulationData` or
  `NullSimulationData`. It stores objects, terrain, parcels and region settings. Estates follow the same
  pattern through `[EstateDataStore]` and `EstateDataService`.
- **Grid data.** Each service loads its data plugin from `StorageProvider` and `ConnectionString` in its own
  section or in `[DatabaseService]`.
- **Migrations.** Schema changes are embedded resource files named `<Store>.migrations`, in
  [Source/OpenSim.Data.MySQL/Resources/](Source/OpenSim.Data.MySQL/Resources/),
  [Source/OpenSim.Data.PGSQL/Resources/](Source/OpenSim.Data.PGSQL/Resources/) and
  [Source/OpenSim.Data.SQLite/Resources/](Source/OpenSim.Data.SQLite/Resources/) (and Gloebit's in
  `Addons/Gloebit.GloebitMoneyModule/Resources/`). [`Migration`](Source/OpenSim.Data/Migration.cs) applies the
  numbered steps newer than the version recorded in the `migrations` table, when a data plugin starts (for
  example `new Migration(dbcon, Assembly, "RegionStore")` applies `RegionStore.migrations`).

## Tests

Layout: one test project per area under [Tests/](Tests/), usually named after the project it tests
(`OpenSim.Region.CoreModules.Tests`, `OpenSim.Server.Handlers.Tests`, ...). Some folder and assembly names
differ, for example `Tests/OpenSim.Permissions.Tests/OpenSim.Tests.Permissions.csproj`. CI builds the
solution, runs `dotnet test`, then [tools/check-test-results.py](tools/check-test-results.py) compares the
`.trx` results with [Tests/known-failures.txt](Tests/known-failures.txt). Details:
[Tests/README-CI.md](Tests/README-CI.md) and [Docs/TESTING.md](Docs/TESTING.md).

Test frameworks, by package references in each project file:

| Framework | Projects |
| --- | --- |
| xUnit (in the solution) | `InWorldz.Phlox.Tests`, `OpenSim.Capabilities.Handlers.Tests`, `OpenSim.Framework.PluginMigration.Tests`, `OpenSim.Framework.Tests`, `OpenSim.Permissions.Tests`, `OpenSim.Region.ClientStack.LindenCaps.Tests`, `OpenSim.Region.ClientStack.LindenUDP.Tests`, `OpenSim.Region.CoreModules.Tests`, `OpenSim.Region.Framework.Tests`, `OpenSim.Region.OptionalModules.ServerSideBaking.Tests`, the seven `OpenSim.Region.PhysicsModules.Jolt.*.Tests`, `OpenSim.Region.ScriptEngine.Tests`, `OpenSim.Server.Base.Tests`, `OpenSim.Services.HypergridService.Tests`, `OpenSim.Tests`, `OpenSim.Tests.Common`, `OpenSim.TrustedHypergrid.Tests`, `Warp3D.Tests`, and `Source/OpenSimNGC.Appearance.Baking.Tests` |
| NUnit (in the solution) | `OpenSim.Clients.Assets.Tests` (no tests yet), `OpenSim.Framework.Servers.Tests`, `OpenSim.Region.ClientStack.LindenCaps.AIS.Tests`, `OpenSim.Server.Handlers.Tests`, `WebRtcJanusService.Tests`, `WebRtcVoiceRegionModule.Tests` |
| NUnit (not in the solution; do not compile) | `OpenSim.Data.Tests`, `OpenSim.Framework.Serialization.Tests`, `OpenSim.Performance.Tests`, `OpenSim.Region.PhysicsModules.BulletS.Tests`, `OpenSim.Robust.Tests`, `OpenSim.Services.InventoryService.Tests`, `OpenSim.Stress.Tests` (listed in `Tests/README-CI.md`) |
| Console programs, not test projects | `JoltPhysicsHarness` (Jolt scenario harness), `PhloxCompileProbe` (compiles scripts in a child process, used by `InWorldz.Phlox.Tests`), `SluaProofRunner` (runs SLua on the Phlox VM) |

`Tests/JoltTestShared/` has no project file; its sources are linked into the Jolt test projects.
[tools/nunit2xunit.py](tools/nunit2xunit.py) was used to convert NUnit sources to xUnit.

Shared helpers are in [Tests/OpenSim.Tests.Common](Tests/OpenSim.Tests.Common/):

- `OpenSimTestCase`: base class for xUnit tests; runs a virtual `SetUp()` before each test and cleans the
  shared `MainServer` state.
- `Helpers/`: `SceneHelpers` (build test scenes with modules and services), `UserAccountHelpers`,
  `UserInventoryHelpers`, `AssetHelpers`, `TaskInventoryHelpers`, `EntityTransferHelpers`,
  `ClientStackHelpers`, `BaseRequestHandlerHelpers`, `LogCapture`, `StubProxy`.
- `Mock/`: `TestScene`, `TestClient` (an `IClientAPI`), `TestLLUDPServer`, `TestEventQueueGetModule`,
  `MockScriptEngine`, HTTP request and response fakes, and in-memory data plugins.
- `TestHelpers.cs`, `TestsAssetCache.cs`, `ConcurrencyTestHelpers.cs`.

## Further reading in Docs/

| Area | Documents |
| --- | --- |
| Building and releasing | [BUILDING.md](Docs/BUILDING.md), [RELEASE.md](Docs/RELEASE.md), [ReleaseEngineering.md](Docs/ReleaseEngineering.md), [WarningCleanup.md](Docs/WarningCleanup.md) |
| Hosting and startup | [HostedServiceArchitecture.md](Docs/HostedServiceArchitecture.md), [HostedServiceStartupShutdownSequence.md](Docs/HostedServiceStartupShutdownSequence.md), [HostedServiceStartupAnalysis.md](Docs/HostedServiceStartupAnalysis.md), [HostedServiceEnvironmentExitAudit.md](Docs/HostedServiceEnvironmentExitAudit.md) |
| Plugins | [PLUGIN_DEVELOPMENT.md](Docs/PLUGIN_DEVELOPMENT.md), [PLUGIN_MIGRATION_PLAN.md](Docs/PLUGIN_MIGRATION_PLAN.md) |
| Regions | [RegionLoading/HOWTO_REMOTE_REGION_LOADING.txt](Docs/RegionLoading/HOWTO_REMOTE_REGION_LOADING.txt) |
| Scripting | [ScriptEngineSelection.md](Docs/ScriptEngineSelection.md), [PhloxSetup.md](Docs/PhloxSetup.md), [PhloxSLua.md](Docs/PhloxSLua.md), [PhloxKnownDefects.md](Docs/PhloxKnownDefects.md), [Phlox-compiler-correctness.md](Docs/Phlox-compiler-correctness.md), [PhloxYEngineConsolidation.md](Docs/PhloxYEngineConsolidation.md), [ExperienceSetup.md](Docs/ExperienceSetup.md) |
| Physics | [JoltPhysics.md](Docs/JoltPhysics.md), [JoltcNative.md](Docs/JoltcNative.md) |
| Viewer features | [feature/ais-v3/](Docs/feature/ais-v3/), [feature/ssb-appearance/](Docs/feature/ssb-appearance/) |
| Hypergrid | [feature/trusted-hypergrid/](Docs/feature/trusted-hypergrid/) |
| Voice | [voice/README.md](Docs/voice/README.md), [voice/architecture.md](Docs/voice/architecture.md) |
| Security | [SECURITY.md](Docs/SECURITY.md), [security/Hardening.md](Docs/security/Hardening.md) |
| Tests | [TESTING.md](Docs/TESTING.md), [feature/repo-audit/T1-TEST-FIXTURES.md](Docs/feature/repo-audit/T1-TEST-FIXTURES.md), [Tests/README-CI.md](Tests/README-CI.md) |
| OpenMetaverse package | [OPENMETAVERSE_NUGET_PATCH_REPORT.md](Docs/OPENMETAVERSE_NUGET_PATCH_REPORT.md), [OPENMETAVERSE_SYSTEM_DRAWING_SPIKE.md](Docs/OPENMETAVERSE_SYSTEM_DRAWING_SPIKE.md) |
