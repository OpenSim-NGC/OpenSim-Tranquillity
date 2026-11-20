# HUMANS.md: guide for contributors

Tranquillity is a virtual world server derived from OpenSimulator, rebuilt on current .NET. It is maintained by
volunteers in the [OpenSim-NGC](https://github.com/OpenSim-NGC) organization. This guide is for the people who
contribute to it, with or without an AI coding agent. Agents follow [AGENTS.md](AGENTS.md); the code is mapped in
[ARCHITECTURE.md](ARCHITECTURE.md).

## Where to talk

- **Bugs and feature requests**: [issues](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/issues). See
  [Docs/BUG_REPORTING.md](Docs/BUG_REPORTING.md) for what to include.
- **Questions, ideas and help with setup**:
  [Discussions](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/discussions).
- **Security problems**: privately, never in public. See [Docs/SECURITY.md](Docs/SECURITY.md).

## Getting set up

1. Install the .NET SDK version that [global.json](global.json) selects (.NET 10), and Git. The
   [GitHub CLI](https://cli.github.com/) (`gh`) is optional but makes pull requests easier.
2. Fork the repository on GitHub, then clone your fork and add the project as `upstream`:

   ```sh
   git clone https://github.com/<your-account>/OpenSim-Tranquillity.git
   cd OpenSim-Tranquillity
   git remote add upstream https://github.com/OpenSim-NGC/OpenSim-Tranquillity.git
   git fetch upstream
   ```

3. Build and run the tests:

   ```sh
   dotnet build Tranquillity.sln -c Release
   dotnet test Tranquillity.sln -c Release --no-build
   ```

   A few tests fail on `develop` already; they are listed in [Tests/known-failures.txt](Tests/known-failures.txt).
   [Tests/README-CI.md](Tests/README-CI.md) shows how to run the same comparison CI does.

4. To run the servers, publish them and configure them as [Docs/BUILDING.md](Docs/BUILDING.md) and the
   [README](README.md) describe. The example configuration files are in each server's `AppData` folder, for
   example `Source/OpenSim.Server.RegionServer/AppData/RegionServer.ini.example`.

## Branches

The project follows GitFlow ([Docs/ReleaseEngineering.md](Docs/ReleaseEngineering.md)):

- `develop` is where work lands. **Open pull requests against `develop`.**
- `release/vX.Y` branches stabilise a release; `master` holds released code; `hotfix/*` branches fix a release.
  The maintainers manage these.

Work on one task per branch, started from the latest `upstream/develop`. A separate
[worktree](https://git-scm.com/docs/git-worktree) per task keeps tasks, and agents, from treading on each other:

```sh
git fetch upstream
git worktree add ../work/tranquillity/<task> -b fix/<task> upstream/develop
```

Remove the worktree (`git worktree remove`) once its pull request is merged.

## Life of a change

1. **Find or open an issue.** Say on the issue that you are working on it, so that work is not done twice. For a
   larger change, agree the approach in the issue or in Discussions first.
2. **Branch** from `upstream/develop` in a worktree.
3. **Change the code and add tests.** A bug fix comes with a test that fails without it. See
   [Docs/TESTING.md](Docs/TESTING.md).
4. **Run the tests** of the projects you touched, and the full CI comparison for larger changes.
5. **Check with a viewer** when the change is visible in-world, on a local standalone or test grid.
6. **Open a pull request** against `develop` using the template. Say what is still pending.
7. **CI** builds the solution and runs every test project, then compares failures with
   `Tests/known-failures.txt`; CodeQL also runs. Fix anything red.
8. **Review.** Answer comments with new commits. A maintainer merges.

Commit titles name the area and say what is now true, for example
`Parcels: a landing point is a place on the parcel`. Keep each pull request to one issue or one task.

## Working with an AI agent

Agents are welcome, and you stay responsible for what yours submits.

1. Set up as above and pick an issue.
2. Start the agent in the repository and give it a clear task, for example:

   ```text
   You are contributing to Tranquillity (https://github.com/OpenSim-NGC/OpenSim-Tranquillity) on issue #<N>.
   Read AGENTS.md and ARCHITECTURE.md, then the issue. Work only on that issue, in a git worktree on a new branch
   from upstream/develop. For a fix, first write a test that fails, then fix it. Run the test projects you
   touched and report the counts. Do not push or open a pull request until I ask. If the issue is wrong or too
   big, stop and tell me.
   ```

3. Read the diff yourself before the pull request is opened: you are its author.
4. Anything only a viewer or a running grid can confirm, check it yourself, or mark it as pending in the pull
   request. Never give an agent your grid or viewer passwords.
5. Tell the agent which of its actions need your approval first: pushing, opening pull requests, commenting.

## Databases for tests

Most tests use in-memory stores. Tests that need MySQL, MariaDB or PostgreSQL run only when their environment
variable is set, and create and drop their own databases. A throw-away container is enough:

```sh
docker run -d --name tranq-test-mariadb -e MARIADB_ROOT_PASSWORD=<password> -p 127.0.0.1:3306:3306 mariadb:11
docker run -d --name tranq-test-pg -e POSTGRES_PASSWORD=<password> -p 127.0.0.1:5432:5432 postgres:16

export OPENSIM_TEST_MYSQL="Server=127.0.0.1;Port=3306;User ID=root;Password=<password>"
export OPENSIM_TEST_PGSQL="Host=127.0.0.1;Port=5432;Username=postgres;Password=<password>"
```

Use a server that holds nothing else. Never point the tests at a grid's database.

## Releases

The maintainers cut releases with Nerdbank.GitVersioning; the version is in [version.json](version.json). The
steps are in [Docs/ReleaseEngineering.md](Docs/ReleaseEngineering.md), and the changes for each release in
[Docs/RELEASE.md](Docs/RELEASE.md) and the `Docs/RELEASE_NOTES_*.md` files.

## Where things are

| Path | What it holds |
| --- | --- |
| `Source/` | The servers, services, region modules, script engines and physics engines |
| `Tests/` | Test projects, shared test helpers, and the known-failures list |
| `Addons/` | Optional modules (groups, offline IM, search, mute list, money, WebRTC voice) |
| `Library/` | Prebuilt assemblies the build still references |
| `Docs/` | Guides and design documents |
| `ThirdPartyLicenses/` | Licence texts of third-party code; summary in [NOTICE.md](NOTICE.md) |
| `tools/` | Build, publish and CI helper scripts |

## Security and privacy

- Report vulnerabilities privately ([Docs/SECURITY.md](Docs/SECURITY.md)).
- Keep passwords, connection strings, private grid addresses and other people's names out of issues, logs, pull
  requests and commits. Remove them from logs and configuration files before attaching them.
- You can commit with your GitHub `noreply` address if you do not want your e-mail address in the history.
