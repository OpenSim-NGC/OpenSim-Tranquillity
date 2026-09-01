# Hosted Service Startup Analysis

## Scope

This document analyzes the current startup architecture for the three server executables on the `feature/hosted-services` branch and identifies the main migration constraints for moving to .NET hosted services and wider dependency injection.

The relevant entry points and runtime roots are:

- `Source/OpenSim.Server.MoneyServer/Program.cs`
- `Source/OpenSim.Server.MoneyServer/MoneyService.cs`
- `Source/OpenSim.Server.GridServer/Program.cs`
- `Source/OpenSim.Server.GridServer/GridService.cs`
- `Source/OpenSim.Server.RegionServer/Application.cs`
- `Source/OpenSim.Server.RegionServer/OpenSim.cs`
- `Source/OpenSim.Server.RegionServer/OpenSimBackground.cs`
- `Source/OpenSim.Server.RegionServer/OpenSimBase.cs`
- `Source/OpenSim.Server.RegionServer/RegionApplicationBase.cs`
- `Source/OpenSim.Server.Base/ServicesServerBase.cs`
- `Source/OpenSim.Server.Base/HttpServerBase.cs`
- `Source/OpenSim.Framework.Servers/BaseOpenSimServer.cs`
- `Source/OpenSim.Framework.Servers/ServerBase.cs`

## Executive Summary

The branch already contains the start of the desired direction for MoneyServer and GridServer: both now have a generic-host `Program.cs` with Autofac, configuration, logging, and ASP.NET Core wiring. However, the runtime services registered with that host still depend on the old startup model.

The legacy model has four properties that currently block a clean hosted-service migration:

1. Startup work is performed in constructors and static entry points instead of explicit lifecycle methods.
2. Runtime ownership is split across global singletons and static state such as `MainConsole.Instance`, `MainServer.Instance`, `Watchdog`, and `WorkManager`.
3. Long-running loops use blocking `while (true)` console prompting instead of cancellable background work.
4. Shutdown paths call `Environment.Exit(...)`, which conflicts with `IHost` lifetime and graceful stop semantics.

MoneyServer is the closest to a workable hosted-service shape, but it still blocks the host thread with a console loop inside `StartAsync()`. GridServer currently has the hosted-service shell but still executes almost the entire legacy startup pipeline inside the `GridService` constructor, including `Run()` and `Environment.Exit()`. RegionServer is still fully on the older inheritance-based boot path.

## Current Branch State

### MoneyServer

The new `Program.cs` creates a generic host, configures Autofac, loads ini files into `IConfiguration`, wires log4net and console logging, and registers:

- `MoneyService` as singleton and hosted service
- `MoneySessionStore` as singleton
- MVC controllers for ASP.NET Core endpoints
- `IServerBase` as a manually constructed `ServerBase`

This is a meaningful improvement over the legacy boot path because:

- command-line parsing has moved to `System.CommandLine`
- ASP.NET Core endpoint hosting is now explicit
- the service can eventually participate in host-controlled start and stop

However, `MoneyService` still behaves like an old server runtime object rather than a hosted service:

- the constructor reads config, creates the legacy HTTP server, starts it, initializes storage, and registers XML-RPC handlers
- it sets `MainConsole.Instance` directly from the injected `IServerBase`
- `StartAsync()` calls `Startup()` and then `Work()`
- `Work()` is an infinite prompt loop
- `StopAsync()` cannot reliably run unless that prompt loop is made cooperative
- several failure paths still call `Environment.Exit(1)`

The practical result is that MoneyServer has a modern host shell, but the application lifetime is still owned by the old runtime code.

### GridServer

`Program.cs` for GridServer mirrors MoneyServer structurally:

- generic host
- Autofac service provider factory
- ini-based configuration into `IConfiguration`
- log4net and console logging
- ASP.NET Core controller host
- singleton `GridService` registered as hosted service
- `IServerBase` registered as a manual `ServerBase`

The important difference is that `GridService` has not yet crossed the lifecycle boundary.

At present, `GridService` still:

- sets `MainConsole.Instance`
- creates `HttpServerBase` in the constructor using `Environment.GetCommandLineArgs()`
- uses `HttpServerBase` and `ServicesServerBase` to re-read config and perform legacy initialization
- loads service connectors in the constructor
- instantiates the plugin loader in the constructor
- calls `m_Server.Run()` in the constructor
- calls `m_Server.Shutdown()` and `Environment.Exit(res)` in the constructor

This means the hosted-service registration is mostly cosmetic at the moment. The object blocks or exits during creation, so the generic host does not actually own the service lifetime.

GridServer is therefore the clearest demonstration of the main migration rule: constructor-time startup must be eliminated before hosted services can work correctly.

### RegionServer

RegionServer still uses the full legacy startup chain.

`Application.Main()` owns:

- unhandled exception wiring
- culture setup
- `ServicePointManager` tuning
- thread pool tuning
- log4net bootstrapping
- `ArgvConfigSource` switch setup
- `background` mode branching
- direct instantiation of `OpenSim` or `OpenSimBackground`
- the foreground console prompt loop

The runtime then flows through a class hierarchy:

- `Application`
- `OpenSim` or `OpenSimBackground`
- `OpenSimBase`
- `RegionApplicationBase`
- `BaseOpenSimServer`
- `ServerBase`

This stack contains most of the concerns that need to be decomposed for a hosted-service design:

- config loading
- console creation and ownership
- HTTP server creation and registration
- plugin loading
- startup scripts
- scene and region lifecycle
- watchdog setup
- shutdown scripts
- direct process exit

The `OpenSimBackground` mode is especially important. It currently blocks on a `ManualResetEvent`, which is conceptually similar to a hosted background lifetime, but it is not integrated with `CancellationToken` or `IHostApplicationLifetime`.

## Shared Legacy Architecture

### `ServerBase`

`ServerBase` contains reusable concerns that are still useful, but its API shape is legacy:

- log appenders and log file setup
- version and uptime reporting
- PID file support
- common commands and environment logging

This class is the least problematic piece. It is mostly a utility base with mutable state. It can likely survive initially as an implementation detail behind new interfaces.

### `ServicesServerBase`

`ServicesServerBase` is a major obstacle because its constructor performs full application bootstrap:

- reads argv switches through Nini
- infers the ini file name from the entry assembly
- loads and merges ini files and includes
- merges environment variables and argv back into the config
- creates the console instance
- configures log4net
- registers common appenders, commands, and components
- creates PID files
- calls overridable `ReadConfig()` and `Initialise()` hooks

It also owns the long-running console loop in `Run()` and calls `Environment.Exit(0)` during shutdown.

This class is effectively a mini-host that conflicts with the real .NET host.

### `HttpServerBase`

`HttpServerBase` extends `ServicesServerBase` and adds network listener creation during config reading and listener startup during initialization.

Its main issues are:

- hard startup failures call `Environment.Exit(1)`
- HTTP listeners are created as a side effect of construction
- console/server coupling is done through reflection and `MainConsole.Instance`

GridServer currently depends heavily on this class.

### `BaseOpenSimServer`

`BaseOpenSimServer` is the equivalent legacy runtime base for RegionServer. It centralizes:

- startup banners and environment logging
- common startup registration
- HTTP client certificate validation setup
- diagnostics timer setup
- shutdown of `MainServer`, `WorkManager`, and PID files
- direct `Environment.Exit(0)` and `Environment.Exit(1)` behavior

This class is the main inheritance bottleneck in the RegionServer path.

## Hosted-Service Gaps By Concern

### Configuration

The branch currently has two configuration worlds in MoneyServer and GridServer:

- modern `IConfiguration` built by the generic host
- legacy Nini config objects still required by large parts of the runtime

MoneyServer partially bridges this by constructing `MoneyServerConfigSource` and handing its Nini config to `ServerBase`. GridServer bypasses the host configuration entirely for its main runtime by re-entering `HttpServerBase`.

The migration needs a deliberate adapter layer instead of ad hoc dual loading in each server.

### Console Lifetime

All three servers still treat console prompting as the primary run loop. In hosted-service terms that loop should become a dedicated background task or a console-hosted adapter.

Until that happens:

- `StartAsync()` cannot complete promptly
- host stop signals cannot be honored cleanly
- tests cannot control service lifetime deterministically

### Process Lifetime

Direct `Environment.Exit(...)` calls remain scattered across the stack. These must be replaced with exceptions, result objects, or `IHostApplicationLifetime.StopApplication()` requests.

Otherwise the application will continue bypassing host-managed shutdown.

### Static Singletons

The runtime still assumes mutable process-wide singletons:

- `MainConsole.Instance`
- `MainServer.Instance`
- `Watchdog`
- `MemoryWatchdog`
- `WorkManager`

These do not need to disappear in the first migration step, but they must be wrapped so hosted services control when they are initialized, started, and stopped.

### Inheritance

RegionServer in particular is tightly coupled to class inheritance for startup sequencing. That makes DI difficult because:

- behavior is split across overridable methods instead of injected collaborators
- startup ordering is implicit in `base.StartupSpecific()` chains
- side effects happen before the full dependency graph is available

The main architectural change for RegionServer is not just moving `Main()` into `Program.cs`; it is replacing startup inheritance with composition.

## Migration Implications

### What can be reused

- `ServerBase` utility behaviors
- existing Nini config types during a transition period
- existing controller classes and service connectors
- existing `MainServer` HTTP server registry, if wrapped behind services
- most server-specific business logic after startup concerns are extracted

### What must change first

- constructors must stop doing runtime startup work
- `Run()` loops must stop owning process lifetime
- shutdown paths must stop calling `Environment.Exit(...)`
- the generic host must become the single owner of configuration, startup, and shutdown

### Server readiness ranking

1. MoneyServer is closest. It already has the correct outer host shape and the smallest legacy surface.
2. GridServer is next. It shares the new host shell but still depends on `HttpServerBase` and `ServicesServerBase` semantics.
3. RegionServer is last. It has the deepest inheritance tree and the broadest startup surface.

## Recommended Migration Strategy

The first meaningful milestone should not be “convert each executable independently.” That would duplicate the same bootstrap rewrite three times.

The better first milestone is to extract a shared hosted startup substrate with these responsibilities:

- command-line options parsing into typed options
- ini loading and include expansion into a reusable adapter
- console factory and console runner
- log4net initialization
- PID file ownership
- process and environment initialization
- legacy singleton lifetime wrappers

Once that substrate exists, the three servers can move over in order of complexity without repeating the same host plumbing.

MoneyServer should become the reference implementation, GridServer should validate that the shared substrate can absorb `ServicesServerBase` behavior, and RegionServer should use the resulting patterns to replace the inheritance tree incrementally rather than in one rewrite.