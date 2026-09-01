# Hosted Service Migration Plan

## Goal

Migrate MoneyServer, GridServer, and RegionServer from legacy startup patterns to .NET generic host and hosted services so that:

- the host owns application lifetime
- startup and shutdown are cancellable and testable
- configuration can move toward `IConfiguration` and typed options
- DI can progressively replace static singletons and inheritance-driven bootstrapping
- long-running work uses modern background-service patterns instead of blocking main-thread loops

## Design Principles

1. The generic host must be the single owner of startup and shutdown.
2. Constructors must become cheap and side-effect free.
3. `StartAsync()` must initialize and return promptly.
4. Long-running console or server loops must become cooperative background tasks.
5. `Environment.Exit(...)` must be removed from normal control flow.
6. Legacy static globals may be wrapped initially, but new code should depend on interfaces.
7. Shared startup responsibilities should be extracted once and reused across all three servers.

## Target Architecture

Each server executable should converge on the same high-level structure:

1. `Program.cs` parses command-line options and builds the generic host.
2. A shared startup library loads ini files, configures logging, creates console services, and applies process-level settings.
3. Server-specific startup is implemented as one or more hosted services.
4. Console prompting runs as a hosted background task or console adapter, not in `Main()` or a constructor.
5. Shutdown is coordinated through `CancellationToken`, `IHostApplicationLifetime`, and disposable services.

Conceptually, the target runtime split is:

- bootstrap services
- server runtime services
- console services
- legacy compatibility adapters

## Shared Workstream

This workstream should be done before deep server-by-server rewrites.

### Phase 1: Extract a shared hosted startup substrate

Create a shared library or shared set of abstractions for:

- typed startup options for `logconfig`, `inifile`, `inimaster`, `inidirectory`, `console`, and server-specific switches
- ini loading with include expansion and environment merge
- legacy Nini-to-host configuration adapter
- log4net bootstrap service
- console factory returning `ICommandConsole`
- PID file service
- process setup service for culture, thread pool, DNS, and HTTP client defaults
- wrapper service for `MainConsole.Instance` and other required globals

Deliverables:

- one reusable host builder path shared by MoneyServer, GridServer, and RegionServer
- one compatibility adapter that exposes a Nini `IConfigSource` for legacy components still not migrated

Why first:

- it prevents three parallel rewrites of the same plumbing
- it makes the transition incremental instead of all-or-nothing

### Phase 2: Define compatibility interfaces around global runtime objects

Introduce small interfaces for process-wide services that are currently static or base-class-owned.

Suggested seams:

- `IConsoleProvider` or `IConsoleContext`
- `IMainServerAccessor`
- `IWatchdogController`
- `IWorkManagerController`
- `IPidFileManager`
- `ILegacyConfigSourceAccessor`

These wrappers do not need to remove the static globals immediately. Their job is to centralize access so server runtimes stop reaching into static state directly.

### Phase 3: Separate initialization from execution

Define a pattern such as:

- `InitializeAsync(CancellationToken)`
- `RunAsync(CancellationToken)`
- `StopAsync(CancellationToken)`

Or equivalent runtime interfaces.

The core rule is that each runtime service must:

- build resources during initialization
- start listeners and background loops explicitly
- stop cooperatively without exiting the process

This is the key step that allows `ServicesServerBase` and `BaseOpenSimServer` behavior to be decomposed.

## Server-by-Server Plan

## MoneyServer

### Current state

MoneyServer already has the closest outer shape to the target design. The remaining issues are lifecycle correctness and residual legacy startup coupling.

### Phase M1: Make `MoneyService` a real hosted service

Refactor `MoneyService` so the constructor only captures dependencies.

Move out of the constructor:

- config reads
- HTTP server creation and start
- database initialization
- XML-RPC handler registration
- any other startup side effects

Move that work into `StartAsync()` or into extracted collaborators invoked by `StartAsync()`.

### Phase M2: Split console prompting into its own service

Replace the `while (true)` prompt loop with a dedicated hosted service, for example a console runner.

Requirements:

- the loop must respect cancellation
- stop should unblock the console loop
- server startup should complete without waiting for console input forever

### Phase M3: Replace process exit with host stop semantics

Replace `Environment.Exit(1)` failure paths with:

- thrown exceptions during startup for fatal initialization errors
- `IHostApplicationLifetime.StopApplication()` for controlled shutdown requests

### Phase M4: Formalize configuration bridging

Stop constructing ad hoc legacy config objects inside `Program.cs`.

Instead:

- register a shared legacy config adapter as a service
- inject it where older components still need `IConfigSource`

### MoneyServer milestone exit criteria

- host starts and stops cleanly under Ctrl+C or service stop
- `StartAsync()` returns promptly
- no constructor does meaningful startup work
- no normal control path uses `Environment.Exit(...)`

## GridServer

### Current state

GridServer has the new host shell, but the runtime is still structurally legacy. It is currently the best candidate for proving the shared startup substrate because it still depends on `HttpServerBase` and `ServicesServerBase` behavior.

### Phase G1: Stop using constructor-time `HttpServerBase` boot

Refactor `GridService` so the constructor does not:

- create `HttpServerBase`
- read command-line args
- load service connectors
- instantiate plugins
- call `Run()`
- shut down the server
- exit the process

That logic should move into explicit runtime methods.

### Phase G2: Replace `HttpServerBase` ownership with composable services

Break the legacy `HttpServerBase` responsibilities into hosted-service-era components:

- config reader or adapter
- HTTP listener factory
- main server registry setup
- connector loader
- console-to-server attachment

Initially these can still delegate to existing internals, but the orchestration must move out of the old base class constructor.

### Phase G3: Replace `ServicesServerBase.Run()` with a cooperative console runtime

GridServer currently still relies on the old `Run()` semantics even though it is registered as a hosted service.

Introduce:

- a startup service that initializes listeners and connectors
- a console runner hosted service for prompt handling
- a shutdown path that disposes listeners and stops `MainServer` without process exit

### Phase G4: Extract connector and plugin loading behind interfaces

Suggested services:

- `IServiceConnectorLoader`
- `IGridPluginLoader`
- `IGridServerRuntime`

This turns GridServer from one large startup constructor into a graph of explicit runtime collaborators.

### GridServer milestone exit criteria

- host lifetime is no longer nested inside `ServicesServerBase.Run()`
- GridService constructor is side-effect free
- listeners and connectors start in `StartAsync()`
- graceful stop does not call `Environment.Exit(...)`

## RegionServer

### Current state

RegionServer is the largest migration because its startup flow is spread across static entry-point logic and several inheritance layers.

The migration should avoid a single rewrite. Instead, keep the domain runtime intact while replacing the shell around it in steps.

### Phase R1: Introduce a new `Program.cs` and host bootstrap

Replace `Application.Main()` as the executable entry point with a generic-host bootstrap equivalent.

Move out of `Application.Main()` into shared bootstrap services:

- culture setup
- unhandled exception wiring
- `ServicePointManager` defaults
- thread pool tuning
- command-line switch parsing
- log4net initialization
- background or foreground mode selection

`Application` should become either:

- a thin adapter used temporarily by hosted services, or
- a removed entry point after equivalent services exist

### Phase R2: Introduce a region runtime adapter hosted service

Create a first hosted service that adapts the existing region runtime instead of fully rewriting it.

That adapter should:

- build or receive the legacy `IConfigSource`
- instantiate the existing region runtime object
- call startup explicitly
- coordinate stop through host cancellation

The initial adapter may still use `OpenSim` and `OpenSimBackground`, but it must own them through host lifecycle rather than static `Main()` logic.

### Phase R3: Separate foreground console mode from background mode

Today `OpenSim` and `OpenSimBackground` mainly differ by how they block for lifetime.

Replace that split with:

- one region runtime service
- one optional console runner service

This removes one of the least useful inheritance distinctions in the current stack.

### Phase R4: Decompose `BaseOpenSimServer` and `OpenSimBase` startup responsibilities

Extract collaborators for:

- diagnostics timer setup
- plugin loading
- scene initialization
- HTTP server startup
- startup and shutdown scripts
- stats endpoints and stream handlers

Do not attempt to remove all inheritance immediately. The first target is to stop inheritance from owning lifecycle and side effects.

### Phase R5: Remove process exit and event-wait lifetime ownership

Replace:

- `Environment.Exit(...)`
- `ManualResetEvent` lifetime blocking
- direct console loops in entry-point code

with host lifetime and cooperative cancellation.

### RegionServer milestone exit criteria

- no server lifetime is owned by `Application.Main()`
- background mode is represented by host configuration, not a separate blocking subclass
- hosted services control startup and shutdown
- the region runtime can be integration-tested through host startup and stop

## Recommended Order

1. Build the shared startup substrate.
2. Finish MoneyServer as the reference hosted-service implementation.
3. Use the shared substrate to convert GridServer off `HttpServerBase` constructor semantics.
4. Introduce a RegionServer hosted adapter.
5. Incrementally decompose RegionServer inheritance into composed services.

This order keeps the smallest surface area first, validates the shared abstractions on two service-style servers, and delays the riskiest region rewrite until the substrate is proven.

## Risk Register

### Risk: dual configuration systems drift apart

Mitigation:

- define one canonical host configuration pipeline
- generate legacy Nini config from that pipeline during transition

### Risk: console prompting blocks host shutdown

Mitigation:

- isolate console prompting in a dedicated service
- ensure cancellation and shutdown can interrupt prompt waiting

### Risk: hidden `Environment.Exit(...)` calls remain in deep runtime code

Mitigation:

- treat all exit calls as migration bugs
- replace them with exceptions or host stop requests
- add grep-based checks during migration

### Risk: static singletons leak state across tests

Mitigation:

- centralize singleton access behind services
- reset or isolate legacy globals in integration tests

### Risk: RegionServer decomposition becomes too large for one milestone

Mitigation:

- land a hosted adapter first
- keep domain runtime behavior stable while changing only lifecycle ownership

## Suggested Work Items

1. Create shared startup options and ini adapter services.
2. Create console factory and console runner hosted service.
3. Create PID and process setup services.
4. Refactor MoneyService constructor to be side-effect free.
5. Move MoneyServer console loop into a dedicated hosted service.
6. Refactor GridService to stop calling `HttpServerBase.Run()` in the constructor.
7. Extract Grid connector loading into a dedicated service.
8. Add a generic-host `Program.cs` path for RegionServer.
9. Add a RegionServer runtime adapter hosted service.
10. Remove or wrap remaining `Environment.Exit(...)` calls in touched startup paths.

## Acceptance Criteria For The Overall Project

The migration can be considered functionally complete when all three executables share these properties:

- startup is orchestrated by the generic host
- server runtimes are composed from injected services rather than startup inheritance
- constructors do not perform boot or shutdown work
- console handling is isolated and cancellable
- no normal startup or shutdown path calls `Environment.Exit(...)`
- integration tests can start and stop each server through host lifetime APIs