# Tests in CI

The build job (`.github/workflows/dotnetci.yml`) runs every test project in `Tranquillity.sln` with
`dotnet test` and then `tools/check-test-results.py`, which reads the `.trx` result files.
All active test projects now use xUnit v3. VSTest, the `.trx` logger and the known-failure comparison
remain unchanged; excluded legacy NUnit projects are not part of this run. See [the testing guide](../Docs/TESTING.md)
for project properties and filters.

The job fails when:

- a test fails that is not listed in `known-failures.txt`;
- a test project in the solution produced no results (its test host crashed, hung past the 15 minute
  limit, or found no tests);
- a test run did not complete.

A listed test that passes is printed, not failed, because some entries are flaky. The `.trx` files are
uploaded as the `test-results` artifact of each run.

## known-failures.txt

Tests that already fail on `develop` on `ubuntu-latest`, so that they do not block unrelated pull
requests. One per line:

```
<assembly> :: <test name as the .trx file names it>
```

Copy the name from the checker's "new failures" output. Text after ` # ` is a note.

- **Fixing a listed test:** remove its line in the same pull request.
- **A new failure:** fix it. Add it to the list only when it is unrelated to the change, with a note
  saying why.
- The list should only shrink.

## Locally skipped known failures

Some behavior-sensitive failures are marked in test code with
`KnownFailure.Skip`, `KnownFailure.SkipWhen`, `KnownFailureFact` or
`KnownFailureTheory`. Local and VS Code test runs skip them and show the
reference and reason in Test Explorer. Runtime guards skip only selected theory
rows; attributes mark a whole fact or theory method. This is used for failures
that should remain visible during development without making an ordinary local
run red.

CI sets `RUN_KNOWN_FAILURES=1`, so marked tests execute there. Their exact test
names must still be listed in `known-failures.txt`; the result checker remains
the authority that decides whether the failure is accepted. A fix removes both the marker and every matching `known-failures.txt` entry.

To run known failures while investigating or verifying a fix:

```sh
RUN_KNOWN_FAILURES=1 dotnet test <test-project> -c Debug
```

## Test projects not in the solution

These test projects do not compile on `develop`, so they are not in the solution and CI does not run
them. Each needs its own repair before it is added:

| Project | Build errors |
| --- | --- |
| `OpenSim.Data.Tests` | about 137 |
| `OpenSim.Framework.Serialization.Tests` | about 61 |
| `OpenSim.Services.InventoryService.Tests` | about 30 |
| `OpenSim.Robust.Tests` (`Robust.Tests`) | about 28 |
| `OpenSim.Stress.Tests` (`OpenSim.Tests.Stress`) | 2 |
| `OpenSim.Region.PhysicsModules.BulletS.Tests` | 2 |
| `OpenSim.Performance.Tests` (`OpenSim.Tests.Performance`) | 1 |

Two projects in the solution report no tests, and are listed in `known-failures.txt` as `(no tests)`:
`OpenSim.Tests.Clients.AssetClient` contains none, and `Warp3D.Tests` excludes its three test files with
`<Compile Remove>`.

## Running the same check locally

```sh
dotnet build Tranquillity.sln -c Release
dotnet test Tranquillity.sln -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory TestResults
python3 tools/check-test-results.py TestResults Tests/known-failures.txt Tranquillity.sln
```

Failures can differ from CI on another operating system or with native libraries missing; the list
describes `ubuntu-latest`.
