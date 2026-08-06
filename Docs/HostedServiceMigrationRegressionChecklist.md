# Hosted Service Migration Regression Checklist (X3)

Use this checklist to validate that the generic-host migration of **RegionServer**,
**GridServer**, and **MoneyServer** preserves the behaviour of the legacy
`Application.Main()` / `ServicesServerBase.Run()` entry points. It combines the automated
coverage that exists today with the manual smoke checks an operator should run before
shipping a migration change.

Legend: `[A]` automated (covered by a test), `[M]` manual smoke check.

## 1. Process startup

- [ ] `[M]` **Foreground start** — launching with no `--background` flag starts the server and presents an interactive console prompt.
- [ ] `[M]` **Background start** — launching with `--background` (or `background=true` in the ini) starts the server with no prompt and the process stays alive under the host.
- [ ] `[A]` **Runtime is initialized by the host** — `RegionServiceHostLifecycleTests.Host_StartAndStop_CallsRuntimeInitializeAndStop` (and Grid/Money equivalents) confirm `Initialize()` runs exactly once on host start.
- [ ] `[A]` **Idempotent initialize** — repeated start does not re-initialize (double-checked lock in each `*Runtime.Initialize`).
- [ ] `[M]` **Startup banner / version** — version, OS, architecture and runtime are logged at startup (parity with the legacy `Startup()` banner).

## 2. Startup failure handling

- [ ] `[A]` **Init failure surfaces through the host** — `RegionServiceHostLifecycleTests.Host_StartAsync_Throws_WhenRuntimeInitializeFails` asserts a runtime init exception faults `host.StartAsync()` rather than exiting the process.
- [ ] `[M]` **Missing/invalid config fails fast** — pointing at a missing ini or a malformed config aborts startup with a clear log message (see the [Environment.Exit audit](HostedServiceEnvironmentExitAudit.md), "pre-host configuration validation").
- [ ] `[M]` **Missing `[Network]` / `port` (Grid/Money)** — starting GridServer/MoneyServer without a `[Network]` section or `port` aborts with the documented error.

## 3. Console interaction

- [ ] `[A]` **No console read before host started** — `RegionConsoleRunnerServiceTests.ExecuteAsync_DoesNotPrompt_BeforeApplicationStarted` (and Grid/Money equivalents) confirm the prompt loop waits for `ApplicationStarted`.
- [ ] `[A]` **Prompt loop runs once started** — `*ConsoleRunnerServiceTests.ExecuteAsync_Prompts_AfterApplicationStarted`.
- [ ] `[A]` **Clean exit when cancelled before start** — `*ConsoleRunnerServiceTests.ExecuteAsync_ExitsCleanly_WhenCancelledBeforeStarted`.
- [ ] `[M]` **`quit` / `shutdown` console command** — issuing the shutdown command at the prompt stops the host and terminates the process cleanly.
- [ ] `[M]` **Ctrl-C / SIGTERM** — sending the interrupt triggers an orderly host shutdown (not an abrupt kill).

## 4. Service / listener registration

- [ ] `[M]` **HTTP listener bound** — the expected HTTP (and HTTPS, if configured) port is listening after startup.
- [ ] `[M]` **RegionServer status handlers** — `/simstatus`, extended status, and robots handlers respond (registered by `RegionStatusHandlerRegistrar`).
- [ ] `[M]` **Region registration with grid** — in a grid configuration, regions register successfully (failure path is the flagged `Environment.Exit(1)` in `OpenSimBase.CreateRegion`).
- [ ] `[M]` **Grid/Money service connectors** — the configured service connectors are loaded and their endpoints respond.
- [ ] `[M]` **Certificate provisioning** — with `EnableSelfsignedCertSupport` / `EnableCertConverter` enabled, certs are created/converted **before** the HTTP listeners start.
  - [ ] `[A]` Decision logic for cert provisioning is covered by `RegionCertificateProvisionerTests`.

## 5. Configuration precedence

- [ ] `[M]` **`--inifile` / `--inimaster` / `--inidirectory`** — command-line config switches are honoured (parity with the legacy `ArgvConfigSource` setup in `RegionRuntime.BuildConfigSource`).
- [ ] `[M]` **Config includes** — `Include-*` directives and URI includes are merged.
- [ ] `[M]` **Override precedence** — later sources / overrides win over earlier ones.

## 6. Runtime monitoring (watchdogs)

- [ ] `[A]` **Watchdogs gated on all-regions-ready** — `RegionReadyStatusMonitorTests` confirm watchdogs are enabled only when all regions are ready and disabled otherwise, in the original order (memory watchdog before the general watchdog).
- [ ] `[M]` **Watchdog/memory-watchdog active after ready** — once all regions report ready, watchdog warnings function as before.

## 7. Shutdown

- [ ] `[A]` **Runtime stopped by the host** — `Host_StartAndStop...` confirms `Stop()` runs exactly once on host stop.
- [ ] `[A]` **Bounded interactive shutdown** — `RegionServiceHostLifecycleTests.Host_Interactive_StartsRuntimeAndConsoleRunner_StopsCleanly` asserts the host stops within a bounded time (the blocking prompt loop does not hang shutdown).
- [ ] `[M]` **PID file lifecycle** — the PID file is created on start and removed on stop (RegionServer via the runtime; Grid/Money via `PidFileHostedService`).
- [ ] `[M]` **Host owns process exit** — on the hosted happy path the process exits via the host, not via `Environment.Exit` (`SuppressExit` guard verified in the [audit](HostedServiceEnvironmentExitAudit.md)).
- [ ] `[M]` **Reverse-order shutdown** — hosted services stop in reverse registration order (console runner first, then runtime, then PID/process-setup for Grid/Money), per the [sequence doc](HostedServiceStartupShutdownSequence.md).

## 8. Regression sign-off

- [ ] `[A]` **Full unit/integration suite green** — `dotnet test Tests/OpenSim.Server.Base.Tests/OpenSim.Server.Base.Tests.csproj --configuration Debug`.
- [ ] `[A]` **Solution builds clean** — `dotnet build Tranquillity.sln --configuration Debug` reports 0 errors.
- [ ] `[M]` **Smoke a standalone region** — start a standalone region end to end, log in (or run the region to a ready state), then shut down cleanly.

---

### How to run the automated coverage

```bash
# From the repository root
dotnet build Tranquillity.sln --configuration Debug
dotnet test Tests/OpenSim.Server.Base.Tests/OpenSim.Server.Base.Tests.csproj --configuration Debug
```

The hosted-service tests live under
[Tests/OpenSim.Server.Base.Tests/Hosting](Tests/OpenSim.Server.Base.Tests/Hosting).
