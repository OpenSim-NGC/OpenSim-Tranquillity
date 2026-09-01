# Hosted Service `Environment.Exit` Audit (X1)

This document inventories every `Environment.Exit(...)` reachable on the migrated
hosted-service startup/shutdown paths (RegionServer, GridServer, MoneyServer) and the
shared base classes they inherit. The goal of the audit is to confirm that, under the
generic-host model, **process termination is owned by the host** and that any remaining
hard exits are either (a) intentionally guarded so the host path does not hit them, or
(b) explicitly flagged as known residuals to be addressed in a follow-up.

Scope: the audit covers code that runs during hosted startup/shutdown. It does **not**
cover legacy standalone entry points (`Application.Main`, `ServicesServerBase.Run`,
`OpenSim.ConsoleClient`) that are retained only for reference/compatibility and are not
invoked on the hosted path, nor deep region/scene runtime code (e.g. `Scene.cs`) that is
out of scope for the composition refactor.

## Summary

| Category | Count | Hosted-path disposition |
| --- | --- | --- |
| Guarded by `SuppressExit` (host owns exit) | 1 | Not hit on hosted path |
| Pre-host configuration validation (fail-fast before host is running) | 8 | Acceptable: fails fast before the host owns the process |
| Residual hard-exit inside hosted lifecycle | 4 | Flagged: should propagate to the host instead of exiting |

## Detailed inventory

### Guarded — host owns process exit

| Location | Trigger | Disposition |
| --- | --- | --- |
| [Source/OpenSim.Framework.Servers/BaseOpenSimServer.cs](Source/OpenSim.Framework.Servers/BaseOpenSimServer.cs#L120) | End of `ShutdownSpecific()` | **Guarded.** Wrapped in `if (!SuppressExit)`. `RegionRuntime.Stop()` sets `_sim.SuppressExit = true` before `_sim.Shutdown()`, so the hosted path returns to the host instead of exiting. The legacy standalone path keeps the exit. No change required. |

`RegionRuntime.Stop()` suppression site: [Source/OpenSim.Server.RegionServer/RegionRuntime.cs](Source/OpenSim.Server.RegionServer/RegionRuntime.cs#L121).

### Pre-host configuration validation (fail-fast)

These run while configuration is being assembled, before the generic host has taken
ownership of the process lifetime. A misconfiguration here is unrecoverable and exiting is
acceptable; the host has not started any hosted services yet, so there is nothing to drain.

| Location | Trigger |
| --- | --- |
| [Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs](Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs#L106) | Referenced master ini file not found |
| [Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs](Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs#L193) | No configuration could be loaded |
| [Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs](Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs#L199) | Configuration exists but failed to load |
| [Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs](Source/OpenSim.Server.RegionServer/ConfigurationLoader.cs#L318) | Exception reading config from a URI include |
| [Source/OpenSim.Server.Base/ServicesServerBase.cs](Source/OpenSim.Server.Base/ServicesServerBase.cs#L360) | Error reading the config source (Grid/Money base) |
| [Source/OpenSim.Server.Base/HttpServerBase.cs](Source/OpenSim.Server.Base/HttpServerBase.cs#L56) | `[Network]` section missing |
| [Source/OpenSim.Server.Base/HttpServerBase.cs](Source/OpenSim.Server.Base/HttpServerBase.cs#L64) | No `port` entry in `[Network]` |
| [Source/OpenSim.Server.Base/HttpServerBase.cs](Source/OpenSim.Server.Base/HttpServerBase.cs#L94) | SSL enabled but `cert_path` / `cert_pass` missing (two sites) |

Disposition: **retain.** These are boundary validations that run before the host owns the
process. They are consistent with fail-fast configuration behaviour and do not interfere
with graceful host shutdown.

### Residual hard-exits inside the hosted lifecycle (flagged)

These execute *after* the host has started a hosted service, so a hard `Environment.Exit`
bypasses the host's orderly shutdown (other hosted services are not stopped, `StopAsync`
never completes). They are retained as legacy behaviour for this sprint and flagged for a
follow-up that converts them into exceptions surfaced through the host.

| Location | Trigger | Recommended follow-up |
| --- | --- | --- |
| [Source/OpenSim.Framework.Servers/BaseOpenSimServer.cs](Source/OpenSim.Framework.Servers/BaseOpenSimServer.cs#L151) | Fatal exception in `StartupSpecific()` | On the hosted path `RegionRuntime.Initialize()` calls `_sim.Startup()`; a fatal here exits instead of faulting `host.StartAsync()`. Let the exception propagate (rethrow) so the host reports startup failure (the F5 tests already assert host start propagates runtime init failures). |
| [Source/OpenSim.Server.RegionServer/OpenSimBase.cs](Source/OpenSim.Server.RegionServer/OpenSimBase.cs#L402) | `RegionModulesController` missing during `CreateRegion` | Throw a descriptive startup exception instead of `Exit(0)`. |
| [Source/OpenSim.Server.RegionServer/OpenSimBase.cs](Source/OpenSim.Server.RegionServer/OpenSimBase.cs#L452) | Required permissions module missing (secure perms loading) | Throw a descriptive startup exception instead of `Exit(0)`. |
| [Source/OpenSim.Server.RegionServer/OpenSimBase.cs](Source/OpenSim.Server.RegionServer/OpenSimBase.cs#L504) | Grid registration failed | Throw to abort startup via the host instead of `Exit(1)`. |

Shared-base note (Grid/Money): [Source/OpenSim.Server.Base/ServicesServerBase.cs](Source/OpenSim.Server.Base/ServicesServerBase.cs#L245)
calls `Environment.Exit(0)` at the end of `ShutdownSpecific()`. On the hosted Grid/Money
path, `GridServerRuntime.Stop()` calls `_serverBase.Shutdown()`
([Source/OpenSim.Server.GridServer/GridServerRuntime.cs](Source/OpenSim.Server.GridServer/GridServerRuntime.cs#L134)).
Because the legacy `Run()` loop is not used on the hosted path, `DoneShutdown` is still
`false` when shutdown runs, so this exit can fire during host shutdown. The recommended
follow-up is to apply the same `SuppressExit`-style guard used by `BaseOpenSimServer` so the
shared base returns control to the host. This is recorded here as a cross-cutting residual;
no behavioural change is made in this sprint.

## Conclusion

- The RegionServer shutdown exit is correctly guarded; the host owns process termination on
  the hosted shutdown path.
- All remaining exits are either pre-host configuration fail-fast (acceptable) or legacy
  in-lifecycle hard-exits that are now explicitly flagged with a concrete follow-up
  (convert to exceptions / apply `SuppressExit` guard).
- No hosted-path regressions are introduced by leaving the flagged residuals in place for
  this sprint; they are tracked for a dedicated follow-up change.
