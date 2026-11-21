# Testing

How to run Tranquillity's tests, where new tests go, and what each kind of test can and cannot prove. For build
instructions see [BUILDING.md](BUILDING.md); for how CI judges a run see [Tests/README-CI.md](../Tests/README-CI.md).

## Running tests

Use the .NET SDK selected by `global.json`. From the repository root:

```sh
dotnet build Tranquillity.sln -c Release
dotnet test Tranquillity.sln -c Release --no-build                 # every test project in the solution
dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Release      # one project
dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Release \
    --filter "FullyQualifiedName~WorldMap"                          # matching tests only
```

A few tests already fail on `develop`. They are listed in [Tests/known-failures.txt](../Tests/known-failures.txt),
and CI fails only on a failure that is not listed. To run the same comparison locally:

```sh
dotnet test Tranquillity.sln -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory TestResults
python3 tools/check-test-results.py TestResults Tests/known-failures.txt Tranquillity.sln
```

Some test projects do not compile and are not in the solution; [Tests/README-CI.md](../Tests/README-CI.md) lists
them.

## Where tests go

- Tests live in `Tests/`, never in production assemblies. A project is tested by `Tests/<Project>.Tests`, for
  example `Source/OpenSim.Region.CoreModules` by `Tests/OpenSim.Region.CoreModules.Tests`. Inside it, follow the
  folder of the code under test.
- Use the framework the project already uses. Most test projects use **xUnit**. These use **NUnit**:
  `OpenSim.Region.ClientStack.LindenCaps.AIS.Tests`, `OpenSim.Framework.Servers.Tests`,
  `OpenSim.Server.Handlers.Tests` (which also has xUnit tests), `OpenSim.Tests.Clients.AssetClient`,
  `WebRtcJanusService.Tests` and `WebRtcVoiceRegionModule.Tests`.
- A new test project must be added to `Tranquillity.sln`, or CI will not run it.

## Writing a test

`Tests/OpenSim.Tests.Common` holds the shared helpers:

| Helper | Use |
| --- | --- |
| `OpenSimTestCase` | Base class for xUnit tests that build scenes; resets shared static state between tests |
| `SceneHelpers` | `new SceneHelpers().SetupScene()` builds a region with in-memory services; `SetupSceneModules` adds region modules with a config; `AddScenePresence` and `AddSceneObject` add avatars and objects |
| `UserAccountHelpers`, `UserInventoryHelpers` | Accounts with inventory, inventory items and folders |
| `AssetHelpers`, `TaskInventoryHelpers` | Assets, and items (scripts, notecards, objects) inside an object |
| `EntityTransferHelpers`, `ClientStackHelpers` | Teleports and crossings between test scenes; the UDP client stack |
| `StubProxy` | A stand-in for any interface, answering only the calls the test names |
| `LogCapture` | Captures log output so a test can check what was logged |

Good tests for this code base:

- **A fix starts with a failing test.** Write the test, run it, see it fail for the reason the issue describes,
  then fix the code and see it pass.
- **Test through the public path** a viewer or service uses: the capability handler, the packet handler, the
  Robust handler, the module's event. A test of a private helper alone can miss the bug.
- **Check refusals as well as successes**: a request that must be refused, a value out of range, a missing item.
- **No timing guesses.** Wait for the event or state you need, with a timeout, instead of sleeping.
- **No shared state between tests.** Each test builds its own scene, database or temporary folder and cleans it up.

## Tests that need a server

These tests are skipped, and reported as skipped, unless their environment variables are set. Point them only at
servers you started for testing: they create and drop their own databases, rooms or files.

| Variable | Tests | Value |
| --- | --- | --- |
| `OPENSIM_TEST_MYSQL` | MySQL/MariaDB data store tests in `OpenSim.Region.Framework.Tests` | Connection string for the server, no database named |
| `OPENSIM_TEST_PGSQL` | PostgreSQL data store tests in `OpenSim.Region.Framework.Tests` | Connection string for the server, no database named |
| `TRUSTED_HG_MYSQL_CONN` | MySQL tests in `OpenSim.TrustedHypergrid.Tests` | Connection string |
| `WEBRTC_JANUS_TEST_URL` and others | Janus voice integration tests | See [Tests/WebRtcJanusService.Tests/StockJanus/README.md](../Tests/WebRtcJanusService.Tests/StockJanus/README.md) |

[HUMANS.md](../HUMANS.md#databases-for-tests) shows how to start throw-away database containers. Some tests also
read optional variables that only change where they write files or regenerate golden files (for example
`SQLITE_QUERY_TEST_DIR`, `PHLOX_REGEN_BASELINE`, `VIS_GOLDEN_UPDATE`); see the test that reads them.

## Checks with a viewer

Unit tests prove the server's logic, not that a viewer shows the result. For a change visible in-world, run a local
standalone or test grid (see [BUILDING.md](BUILDING.md) and the [README](../README.md)), log in with a viewer and
check it. Say in the pull request which viewer and version you used and what you saw, or mark the check as pending.
Use test accounts on a server you control.

Other area guides with their own test tools:

- Physics: [JoltPhysics.md](JoltPhysics.md)
- Scripts: [PhloxSetup.md](PhloxSetup.md), [PhloxKnownDefects.md](PhloxKnownDefects.md),
  [Phlox-compiler-correctness.md](Phlox-compiler-correctness.md)
- Voice: [voice/README.md](voice/README.md)

## YEngine state-load telemetry

When testing script state restore and reset in live regions, YEngine can report aggregate state-load failure
reasons.

In the `[YEngine]` section:

```ini
StateLoadMetricsEnabled = true
StateLoadMetricsPeriodic = true
```

With periodic metrics enabled, summary lines are logged during maintenance cycles (`MaintenanceInterval`) only
when the totals change.

Region console commands:

```text
yeng stateload show
yeng stateload reset
```

Suggested workflow on a test system:

1. Run `yeng stateload reset` before a test scenario.
2. Run the teleport, crossing or restart scenarios that may restore script state.
3. Run `yeng stateload show` and collect the reason counters.
