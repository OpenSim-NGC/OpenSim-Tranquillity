## Quick orientation for automated coding agents

This repo is OpenSim (Tranquillity branch) — a large C#/.NET virtual-world server.
Focus on the `OpenSim/` tree (Framework, Server, Region, Services) and the top-level
solution `OpenSim.sln`.

### Big picture
- Language / SDK: C#, .NET 8.0 (see `global.json` and `Directory.Build.props`).
- Entry point(s): `OpenSim/Server/Robust.csproj` builds the main server executable.
- Major areas: `OpenSim/Framework/*`, `OpenSim/Server/*`, `OpenSim/Region/*`, `OpenSim/Services/*`.
- Plugins & optional modules live in `addon-modules/` and use the Mono.Addins pattern.

### How the code is structured and why
- The solution is split into many small projects (core framework, servers, handlers,
  region modules). Services communicate via service connectors/handlers (naming
  patterns: `*Connector`, `*Handler`, `*Service`, `*Module`).
- Runtime configuration is INI-driven in `bin/` and `bin/config-include` (e.g. `OpenSim.ini`,
  `StandaloneCommon.ini`). Many services are selected by name in INI (e.g. `InventoryService` ->
  `LocalServiceModule` string). Search for `.Set("LocalServiceModule"` in tests to see examples.

### Build / test / debug workflows (concrete commands)
- Local build (repo root):
  - dotnet build --configuration Debug
  - dotnet build --configuration Release
- Run tests (repo root):
  - dotnet test --configuration Debug
- Publish / release: use `dotnet publish` or the included VS Code tasks (`build`, `publish`, `watch`).
- Docker: compose files reference `OpenSim/Server/Dockerfile` (`compose.yaml`, `compose.debug.yaml`).
  Use the existing VS Code docker tasks or `docker build` with that Dockerfile.
- Run the packaged distribution: `bin/opensim.sh` (see root README and `Docs/BUILDING.md`).

### Repo-specific conventions and patterns
- Naming: service classes and connectors follow `XxxServerConnector`, `XxxServerPostHandler`.
- Project references: many csproj files reference DLLs shipped in the repo `bin/` (e.g. `Nini`,
  `OpenMetaverse.dll`). Ensure `bin/` artifacts are present or run `dotnet restore` / build.
- Some server projects intentionally exclude old files via `Compile Remove` in csproj
  (see `OpenSim/Server/Robust.csproj`) — be careful when adding files; match existing folder layout.
- Tests build lightweight scenes with `OpenSim.Region.Framework.Scenes` — follow existing test
  fixtures in `Tests/` for creating scene/region mocks.

### Integration points to inspect when changing behavior
- HTTP and service handlers: `OpenSim/Server/Handlers/` (patterns for REST endpoints and connectors).
- Region modules: look under `OpenSim/Region/*` for avatar/physics/inventory interactions.
- Configuration-driven wiring: `bin/config-include/*` and `bin/OpenSim.ini` control which
  service module implementation is used.
- Addon modules: `addon-modules/` shows examples of optional modules (Gloebit, os-webrtc-janus).

### Examples of useful quick searches
- "ServerConnector" or "ServerPostHandler" to find integration points.
- `LocalServiceModule` to find where INI wires services.
- `OpenSim.Region.Framework.Scenes` to find test helpers and scene construction.

### Small practical rules for AI edits
- Prefer adding new modules under `OpenSim/Region` or `OpenSim/Server/Handlers` to match
  existing separation of concerns. Name classes with the `*Module`/`*Connector`/`*Handler`
  suffixes.
- When you change topology (add a project), add a ProjectReference and run `dotnet build`.
- Do not assume NuGet-only dependencies — some references are to in-repo DLLs under `bin/`.

If anything here is unclear or you want more detail on a particular subsystem (HTTP handlers,
region modules, tests, or Docker usage), tell me which area and I will expand this file.
