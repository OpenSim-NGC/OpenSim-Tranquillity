# Tests in CI

`.github/workflows/dotnetci.yml` runs every test project in `Tranquillity.sln` with `dotnet test`, as
several jobs at once, and then `tools/check-test-results.py`, which reads the `.trx` result files of all
of them.

## The jobs

- **plan** decides whether the tests run, and which test jobs there are. A pull request that changes
  only documentation (`*.md`, `Docs/`, licence files) skips the tests.
- **test (…)** jobs each restore, build and test their own share of the test projects, at the same time.
  `tools/ci-test-groups.json` lists them; `tools/ci-test-plan.py` turns that into jobs:
  - a group names test projects; the group marked `rest` takes every test project no other group names,
    so a new test project runs without any change here;
  - a group with `shards` runs as that many jobs over the same projects, split by test class. Shards 1
    to N-1 each take a set of classes, balanced by the seconds in `tools/ci-test-times.json`; the last
    shard takes every test that is in none of them, so new classes always run.
- **build** waits for the test jobs, checks that no test ran in two shards, and compares the results with
  `known-failures.txt`. It is the one check to require on a branch.

A new push to a pull request cancels that pull request's older run.

The job fails when:

- a test fails that is not listed in `known-failures.txt`;
- a test project in the solution produced no results (its test host crashed, hung past the 15 minute
  limit, or found no tests);
- a test run did not complete;
- a test job failed to restore or build, or a test ran in two shards.

A listed test that passes is printed, not failed, because some entries are flaky. The `.trx` files of
every job, and `ci-test-times.json`, are uploaded as the `test-results` artifact of each run.

## Keeping the jobs balanced

When a split project's shards drift apart in time, refresh the class times from a recent run: download
its `test-results` artifact and copy its `ci-test-times.json` over `tools/ci-test-times.json` (or run
`python3 tools/ci-test-plan.py times <results-dir>`). To split a project further, or another one, change
`tools/ci-test-groups.json`. `python3 tools/ci-test-plan.py prepare <job>` prints what a job would run.

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
