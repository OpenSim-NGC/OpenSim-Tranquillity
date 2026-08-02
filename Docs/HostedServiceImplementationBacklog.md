# Hosted Service Implementation Backlog

## Purpose

This backlog turns the hosted-service migration analysis and plan into implementation-sized work items that can be scheduled, executed, and validated incrementally.

It is organized around three levels:

1. Epics for major migration areas.
2. Tasks sized for one focused PR or short sequence of PRs.
3. Acceptance criteria and dependencies so execution order is explicit.

This backlog assumes the migration order defined in `Docs/HostedServiceMigrationPlan.md`:

1. shared startup substrate
2. MoneyServer completion
3. GridServer conversion
4. RegionServer host adapter
5. RegionServer decomposition

## Working Rules

1. No constructor may do runtime startup work after a migration task is complete.
2. No migrated startup path may call `Environment.Exit(...)` for normal control flow.
3. Every migrated service must start and stop through host lifetime.
4. New seams should prefer interfaces over class inheritance.
5. When a legacy static singleton remains, access to it should move behind an injected adapter.
6. Each task should leave the codebase in a runnable state.

## Epic A: Shared Hosted Startup Substrate

Goal: Create one reusable host bootstrap path and compatibility layer that all three servers can share.

### A1. Define shared startup option types

Scope:

- create typed option models for common switches such as `logconfig`, `inifile`, `inimaster`, `inidirectory`, and `console`
- separate common options from server-specific options
- normalize option defaults now scattered across individual `Program.cs` files

Deliverables:

- shared startup options types
- option binding or parsing helpers
- server-specific extensions for MoneyServer, GridServer, and RegionServer

Dependencies:

- none

Acceptance criteria:

- MoneyServer and GridServer can consume the same common option model
- defaults are defined in one place for common switches

Suggested PR slice:

- shared option types and parser helpers only, no runtime behavior change

### A2. Build a reusable ini configuration loader

Scope:

- load the master ini file
- load explicit additional ini files
- load ini directory contents
- support include expansion behavior currently embedded in `ServicesServerBase`
- merge environment data in a deliberate, testable way

Deliverables:

- shared configuration loader service
- tests for include expansion and precedence rules

Dependencies:

- A1

Acceptance criteria:

- MoneyServer and GridServer can stop duplicating ini loading logic in `Program.cs`
- include processing behavior is preserved for legacy consumers

Suggested PR slice:

- standalone loader plus tests, wired into one server later

### A3. Add a legacy Nini configuration adapter

Scope:

- produce a legacy `IConfigSource` from the canonical host configuration pipeline
- centralize the current dual-config bridging logic
- avoid each server constructing bespoke config source wrappers

Deliverables:

- `ILegacyConfigSourceAccessor` or equivalent
- adapter implementation backed by the shared loader

Dependencies:

- A2

Acceptance criteria:

- existing legacy runtime components can request a single injected Nini config source
- no migrated server builds its own ad hoc Nini config bridge in `Program.cs`

Suggested PR slice:

- adapter plus registration in one server

### A4. Extract log4net bootstrap service

Scope:

- move log4net setup out of individual entry points and legacy base constructors where practical
- standardize log configuration selection
- preserve existing log file behavior and config file compatibility

Deliverables:

- shared logging bootstrap service
- integration notes for server startup

Dependencies:

- A1

Acceptance criteria:

- MoneyServer and GridServer can share one log bootstrap path
- fatal startup logging still appears before host shutdown

Suggested PR slice:

- service extraction and adoption in MoneyServer first

### A5. Add console factory and console context services

Scope:

- create one console factory for `basic`, `local`, `rest`, and `mock`
- create an adapter that owns `MainConsole.Instance`
- make console creation explicit and injectable

Deliverables:

- `IConsoleFactory`
- `IConsoleContext` or equivalent singleton adapter

Dependencies:

- A1

Acceptance criteria:

- server runtimes receive a console dependency instead of instantiating console types directly
- writes to `MainConsole.Instance` are centralized

Suggested PR slice:

- factory and adapter without yet changing console prompt loops

### A6. Add process setup and PID services

Scope:

- extract PID file ownership
- extract culture, DNS, HTTP defaults, and thread pool tuning from entry points
- make process setup callable and testable

Deliverables:

- `IProcessSetupService`
- `IPidFileManager`

Dependencies:

- A1

Acceptance criteria:

- RegionServer entry-point setup concerns have a target home in host startup
- MoneyServer and GridServer can stop handling PID and process setup ad hoc

Suggested PR slice:

- PID service first, broader process setup second

## Epic B: Compatibility Wrappers Around Legacy Global State

Goal: Make host-managed services the owners of legacy global runtime objects without forcing immediate full rewrites.

### B1. Wrap `MainConsole.Instance`

Scope:

- centralize reads and writes to the global console instance
- support temporary compatibility for code that still assumes the static singleton exists

Dependencies:

- A5

Acceptance criteria:

- migrated startup paths no longer assign to `MainConsole.Instance` directly outside the adapter

### B2. Wrap `MainServer.Instance`

Scope:

- provide one injected accessor or coordinator for listener registration and shutdown
- isolate direct runtime dependence on the static registry

Dependencies:

- none

Acceptance criteria:

- new hosted services access the main server registry through an interface

### B3. Wrap `Watchdog`, `MemoryWatchdog`, and `WorkManager`

Scope:

- isolate start and stop calls behind a small service layer
- make hosted-service shutdown the only owner of those lifecycle transitions

Dependencies:

- none

Acceptance criteria:

- migrated services do not manipulate these statics directly

### B4. Add startup failure and shutdown coordination helpers

Scope:

- replace direct process exit calls with exceptions or host stop requests
- standardize how fatal startup failure is reported

Dependencies:

- A4

Acceptance criteria:

- there is a shared pattern for fatal startup failures
- migrated services can request stop without exiting the process directly

## Epic C: MoneyServer Completion

Goal: Make MoneyServer the first fully functional hosted-service implementation and the reference pattern for the other servers.

### C1. Move MoneyService startup side effects out of the constructor

Scope:

- move config reads, HTTP server creation, DB initialization, and XML-RPC registration into explicit lifecycle methods

Dependencies:

- A3
- A5
- B1
- B4

Acceptance criteria:

- constructing `MoneyService` does not start listeners or initialize storage
- startup happens during host-controlled service start

Suggested PR slice:

- first move HTTP and DB startup only, then move remaining side effects

### C2. Extract a MoneyServer runtime coordinator

Scope:

- separate hosted-service orchestration from Money-specific business logic
- define a runtime service that can initialize, run, and stop the legacy subsystems

Dependencies:

- C1

Acceptance criteria:

- `MoneyService` is primarily orchestration and lifecycle code
- Money-specific runtime pieces are separated into collaborators

### C3. Add a console runner hosted service for MoneyServer

Scope:

- move prompt handling into a dedicated background service
- make the prompt loop cooperative and cancellable

Dependencies:

- A5
- B1
- C1

Acceptance criteria:

- `StartAsync()` does not block on console prompting
- shutdown can stop the prompt loop cleanly

### C4. Remove `Environment.Exit(...)` from migrated MoneyServer paths

Scope:

- replace fatal startup exits with exceptions
- replace controlled shutdown exits with host stop semantics

Dependencies:

- B4
- C1

Acceptance criteria:

- no normal MoneyServer startup or stop path exits the process directly

### C5. Add host-lifetime integration tests for MoneyServer

Scope:

- validate startup, clean stop, and failure handling through host lifetime APIs

Dependencies:

- C3
- C4

Acceptance criteria:

- automated tests can create, start, and stop MoneyServer deterministically

## Epic D: GridServer Conversion

Goal: Replace the current hosted-service shell around `HttpServerBase` and `ServicesServerBase` behavior with explicit, host-owned runtime services.

### D1. Stop `GridService` constructor-time boot

Scope:

- remove config reads, connector loading, plugin loader setup, `Run()`, and shutdown from the constructor

Dependencies:

- A3
- A5
- B1
- B4

Acceptance criteria:

- `GridService` constructor is side-effect free
- the generic host can build without starting the server runtime

Suggested PR slice:

- first remove `Run()` and exit behavior from the constructor, then move remaining startup work

### D2. Extract HTTP listener bootstrap from `HttpServerBase`

Scope:

- move listener creation and startup orchestration into injected services
- preserve existing SSL and port configuration behavior

Dependencies:

- B2
- D1

Acceptance criteria:

- GridServer listener startup is explicit and host-controlled
- `HttpServerBase` is no longer required as the boot orchestrator

### D3. Extract connector loading service

Scope:

- build one `IServiceConnectorLoader` for GridServer connector activation
- keep plugin loading separate from listener startup

Dependencies:

- D1

Acceptance criteria:

- connector loading is invoked from a runtime coordinator rather than constructor code

### D4. Replace `ServicesServerBase.Run()` semantics with hosted console runtime

Scope:

- add a dedicated console runner service for GridServer
- ensure service start returns without entering the prompt loop

Dependencies:

- A5
- D1

Acceptance criteria:

- host lifetime is no longer nested inside `Run()` behavior

### D5. Remove `Environment.Exit(...)` from migrated GridServer paths

Scope:

- replace startup and shutdown exits with shared host-coordination behavior

Dependencies:

- B4
- D1

Acceptance criteria:

- no normal GridServer control path exits the process directly after migration

### D6. Add host-lifetime integration tests for GridServer

Scope:

- validate startup, connector registration, and clean stop under host lifetime control

Dependencies:

- D4
- D5

Acceptance criteria:

- GridServer can be started and stopped deterministically in tests

## Epic E: RegionServer Host Adapter

Goal: Put RegionServer under generic-host lifetime without requiring a full domain rewrite up front.

### E1. Introduce RegionServer `Program.cs` generic-host bootstrap

Scope:

- replace `Application.Main()` as the executable shell
- wire shared option parsing, logging, config loading, and process setup into the host

Dependencies:

- A1
- A2
- A4
- A6

Acceptance criteria:

- RegionServer startup no longer begins in a static, self-owned main loop

Suggested PR slice:

- add host bootstrap while temporarily delegating to a legacy adapter

### E2. Add a legacy region runtime adapter hosted service

Scope:

- host the existing `OpenSim` or equivalent runtime behind `IHostedService`
- centralize startup and stop through host lifecycle

Dependencies:

- E1
- A3
- B4

Acceptance criteria:

- RegionServer can be started and stopped by the generic host without relying on `Application.Main()` loops

### E3. Move foreground and background lifetime mode out of inheritance

Scope:

- stop using `OpenSimBackground` as the owner of process lifetime blocking
- represent background versus interactive behavior through hosted services and configuration

Dependencies:

- E2
- A5

Acceptance criteria:

- console mode is a host configuration concern, not a separate blocking subclass concern

### E4. Add a RegionServer console runner hosted service

Scope:

- host the interactive prompt loop separately from region runtime startup

Dependencies:

- E3

Acceptance criteria:

- the region runtime can start without also owning the prompt loop

### E5. Remove `Environment.Exit(...)` and event-wait lifetime ownership from migrated RegionServer paths

Scope:

- replace direct exits and `ManualResetEvent` lifetime blocking with host stop and cancellation

Dependencies:

- B4
- E2
- E3

Acceptance criteria:

- migrated RegionServer startup and shutdown paths no longer own process exit directly

## Epic F: RegionServer Decomposition

Goal: Incrementally replace startup inheritance with composition once RegionServer is already under host lifetime.

### F1. Extract diagnostics and watchdog lifecycle services

Scope:

- move diagnostics timer and watchdog lifecycle management into dedicated services

Dependencies:

- B3
- E2

Acceptance criteria:

- region runtime no longer starts or stops these static services directly

### F2. Extract HTTP server startup and stream-handler registration services

Scope:

- separate listener creation from handler registration and scene runtime

Dependencies:

- B2
- E2

Acceptance criteria:

- HTTP server boot is explicit, testable, and no longer buried in inheritance chains

### F3. Extract plugin loading and startup script services

Scope:

- separate plugin discovery, plugin load, post-initialize hooks, and startup or shutdown command scripts

Dependencies:

- E2

Acceptance criteria:

- startup script and plugin concerns are no longer owned directly by the region base classes

### F4. Extract scene runtime initialization services

Scope:

- isolate region and scene initialization from console and host concerns

Dependencies:

- E2

Acceptance criteria:

- scene initialization can be tested with reduced host bootstrap coupling

### F5. Add host-lifetime integration tests for RegionServer

Scope:

- validate startup, interactive mode, background mode, and clean shutdown under the generic host

Dependencies:

- E5
- F2

Acceptance criteria:

- RegionServer can be started and stopped deterministically through host lifetime APIs

## Cross-Cutting Cleanup Tasks

### X1. Audit `Environment.Exit(...)` usage in affected startup paths

Scope:

- maintain a migration checklist of remaining exit calls in MoneyServer, GridServer, RegionServer, and the shared startup code they still use

Acceptance criteria:

- each migrated path has an explicit disposition for every direct exit call

### X2. Add startup and shutdown sequence documentation

Scope:

- document the new hosted startup order once each server is migrated
- capture required compatibility shims and when they can be removed

Acceptance criteria:

- developers can understand the active hosted-service lifecycle without reading multiple legacy base classes

### X3. Add migration regression test checklist

Scope:

- define smoke checks for startup success, console interaction, service stop, config precedence, and listener registration

Acceptance criteria:

- each server migration PR can run the same basic validation set

## Suggested Execution Sequence

### Wave 1: Foundations

- A1 shared startup option types
- A2 reusable ini configuration loader
- A3 legacy Nini config adapter
- A5 console factory and context
- B1 wrap `MainConsole.Instance`
- B4 startup failure and shutdown coordination helpers

### Wave 2: MoneyServer reference implementation

- C1 move startup side effects out of the constructor
- C3 add console runner hosted service
- C4 remove exit calls from migrated MoneyServer paths
- C5 add host-lifetime integration tests

### Wave 3: Shared bootstrap hardening

- A4 log4net bootstrap service
- A6 process setup and PID services
- B2 wrap `MainServer.Instance`
- B3 wrap watchdog and work manager lifecycle

### Wave 4: GridServer conversion

- D1 stop constructor-time boot
- D2 extract HTTP listener bootstrap
- D3 extract connector loading service
- D4 add hosted console runtime
- D5 remove exit calls from migrated GridServer paths
- D6 add integration tests

### Wave 5: RegionServer host adoption

- E1 introduce RegionServer generic-host bootstrap
- E2 add legacy region runtime adapter hosted service
- E3 move foreground and background mode out of inheritance
- E4 add RegionServer console runner hosted service
- E5 remove exit and event-wait lifetime ownership from migrated paths

### Wave 6: RegionServer decomposition

- F1 diagnostics and watchdog lifecycle services
- F2 HTTP server startup and handler registration services
- F3 plugin loading and startup script services
- F4 scene runtime initialization services
- F5 integration tests

## Priority Candidates For The First 10 PRs

1. A1 shared startup option types.
2. A2 reusable ini configuration loader.
3. A3 legacy Nini config adapter.
4. A5 console factory and context.
5. B1 wrap `MainConsole.Instance`.
6. B4 startup failure and shutdown coordination helpers.
7. C1 move MoneyService startup out of the constructor.
8. C3 add MoneyServer console runner hosted service.
9. C4 remove MoneyServer exit calls from migrated paths.
10. C5 add MoneyServer host-lifetime integration tests.

## Definition Of Done

The hosted-service migration backlog is complete when:

1. MoneyServer, GridServer, and RegionServer all start through the generic host.
2. Service construction is side-effect free in migrated startup paths.
3. Interactive console handling is separated from runtime boot and respects cancellation.
4. Host stop semantics replace direct process exit in migrated startup and shutdown code.
5. Shared bootstrap concerns are implemented once and reused across all three servers.
6. Each server has host-lifetime integration coverage for start and stop behavior.