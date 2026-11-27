# Testing

How to run Tranquillity's tests, where new tests go, and what each kind of test can and cannot prove. For build
instructions see [BUILDING.md](BUILDING.md); for how CI judges a run see [Tests/README-CI.md](../Tests/README-CI.md).

All test projects in `Tranquillity.sln` now use **xUnit.net v3**, with no NUnit dependencies.
Excluded legacy projects in [Tests/](../Tests/) still contain NUnit tests and have not been converted.
`dotnet test` uses **VSTest**. The xUnit projects are executables, but
set `IsTestingPlatformApplication` to `false` so the .NET 10 SDK retains the existing VSTest workflow.
Do not switch `global.json` to MTP without updating the CI runner and its reporting arguments.

## Running Tests

From the repository root, use VSTest arguments directly with `dotnet test`:

* **Run all tests in this directory:**
  ```bash
  dotnet test Tranquillity.sln -c Debug
  ```
* **Run a single test class:**
  ```bash
  dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Debug \
      --filter "FullyQualifiedName~YourNamespace.YourTestClass"
  ```
* **Run a single test method (supports `*` wildcards at start/end):**
  ```bash
  dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Debug \
      --filter "FullyQualifiedName~MethodName"
  ```
* **Filter by namespace:**
  ```bash
  dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Debug \
      --filter "FullyQualifiedName~YourNamespace.SubNamespace"
  ```
* **Filter by trait (replaces `TestCategory`):**
  ```bash
  dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Debug --filter "Category=Unit"
  ```
* **Exclude traits (e.g., skipping slow or integration tests):**
  ```bash
  dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Debug --filter "Category!=Integration"
  ```
* **List all discovered tests without executing them:**
  ```bash
  dotnet test Tests/OpenSim.Region.CoreModules.Tests -c Debug --list-tests
  ```

Combine VSTest filters with `|` (OR) or `&` (AND). xUnit
`[Trait("Category", "...")]` uses `Category`. Use `--logger trx` to record results.

The xUnit executable can also be run directly with its native runner, for example
`dotnet run --project Tests/OpenSim.Tests.Common -- -class OpenSim.Tests.Common.OpenSimTestCaseLifecycleTests`.
Native runner arguments differ from VSTest arguments; use `-?` for help.

## xUnit v3 Architecture & Conventions

* **Standalone Executable Model:** xUnit projects use `<OutputType>Exe</OutputType>` and reference `xunit.v3` (not `xunit` v2), `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`.
* **Async Lifecycle (`IAsyncLifetime`):** In xUnit v3, `IAsyncLifetime.InitializeAsync()` and `DisposeAsync()` return `ValueTask` instead of `Task`. When a fixture implements both async and synchronous disposal, v3 only calls `DisposeAsync()`. `OpenSimTestCase` forwards this to its virtual `Dispose()` so existing teardown overrides still run.
* **Output and extension APIs:** `ITestOutputHelper` is in `Xunit`, not `Xunit.Abstractions`. `BeforeAfterTestAttribute` is in `Xunit.v3` and receives both the method and `IXunitTest`.
* **Async Assertions:** Prefer native async assertions (`Assert.ThrowsAsync<T>`, `Assert.RaisesAsync`) and avoid `.Result` or `.Wait()` on tasks to prevent thread-pool starvation.
* **Test Context & Cancellation:** Use `TestContext.Current.CancellationToken` when calling async APIs inside tests instead of `CancellationToken.None`.
* **Console & Output Capture:** Use `ITestOutputHelper` injected via constructor, or `TestContext.Current.TestOutputHelper`, rather than `Console.WriteLine`.
* **Theory Data:** Prefer strongly typed `TheoryData<T1, T2, ...>` over `IEnumerable<object[]>` for `[MemberData]` and `[ClassData]` sources to preserve compile-time type safety.
* **Voice fixtures:** The voice suites run serially, install their shared logging router through an assembly fixture, and retain opt-in gateway guards. Gateway clients and owned position-feed HTTP resources use class fixtures. Facts and theories retain their timeout limits and participate in test cancellation. Repeated race checks use theory data, so every repetition is reported separately.

## Structure & Naming

* **Project Mirroring:** Each `Source/<ProjectName>` project should map to `Tests/<ProjectName>.Tests`.
* **File & Class Naming:** Mirror the production folder structure. Name test classes `<TargetClassName>Tests.cs`.
* **Method Naming:** Use `MethodName_StateUnderTest_ExpectedBehavior` or clear descriptive names with `[Fact(DisplayName = "...")]` when appropriate.
* **AAA Pattern:** Organize every test body clearly into **Arrange**, **Act**, and **Assert** blocks separated by a blank line.

## Boundaries & Guardrails

* **Always:**
  * Run the affected test class or project before marking a task complete.
  * Keep unit tests isolated from network, filesystem, and system clock dependencies unless explicitly tagged with `[Trait("Category", "Integration")]`.
* **Never:**
  * Install `xunit` (v2), `xunit.runner.visualstudio` (v2), or mix v2 and v3 packages in the same project.
  * Disable or `[Fact(Skip = "...")]` failing tests to make the suite pass without explicit instruction.


## Running tests

Use the .NET SDK selected by `global.json`. From the repository root:

Release treats warnings as errors. Until the remaining warning backlog is resolved, use `-c Debug`
for validation; see [WarningCleanup.md](WarningCleanup.md) for the current build baseline.

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
- Use **xUnit v3** for projects in the solution. Excluded legacy NUnit projects remain outside this migration.
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
