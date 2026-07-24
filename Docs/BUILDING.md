# git clone

get or update source from git

 `git clone https://github.com/OpenSim-NGC/OpenSim-Tranquillity.git`
  
### Building
 Prebuild is no longer used.  There is a top level Solution (sln) and csproj files for each
 of the projects in the solution.  The projects are designed to be publishable which 
 optimizes the build for a specific platform.  Future versions will support building into a '
 container and AOT compilation:
 
 To run a build from a CLI run:

 dotnet publish --configuration Debug -r linux-x64
 dotnet publish --configuration Release -r linux-x64

 dotnet publish --configuration Debug -r win-x64
 dotnet publish --configuration Release -r win-x64

If no configuration is specified the default is a release build. If no platform is specified default 
is the platform being used for the compilation.

The output from the publish will be in build/<Configuration>/net8.0/<platform>/

Where Configuration is either Debug or Release and Platform is either linux-x64 or win-x64 as shown above.

For testing workflows (including YEngine state-load telemetry configuration and commands),
see Docs/TESTING.txt.

## Plugin discovery

Plugin discovery now always uses DotNetCorePlugins. The old `PluginDiscovery`
setting and `OPENSIM_PLUGIN_DISCOVERY` override are no longer used.

Either configuration will do a NuGet restore (dotnet restore) to restore any required NuGet package references prior to
kicking off a build using a current version of msbuild.  The Csproj and SLN files are all designed to use the new
format for Msbuild which is simplified and really directly replaces what prebuild provided.

Configure. See below

For rebuilding and debugging use the dotnet command options
  *  clean:  `dotnet clean
  *  restore: dotnet restore
  *  debug:   dotnet publish --configuration Debug
  *  release: dotnet publish --configuration Release

# NuGet packages from the private GitHub Packages feed #

Some OpenSim-Tranquillity dependencies are published to a **private GitHub Packages
NuGet feed** owned by the `OpenSim-NGC` organization. Before you can `dotnet restore`
(or build/publish, which restore automatically), you must authenticate to this feed.

The repository already contains a `nuget.config` at the solution root that declares the
feed and reads your credentials from two environment variables:

```xml
<packageSources>
  <clear />
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  <add key="github" value="https://nuget.pkg.github.com/OpenSim-NGC/index.json" />
</packageSources>
<packageSourceCredentials>
  <github>
    <add key="Username" value="%GITHUB_ACTOR%" />
    <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
  </github>
</packageSourceCredentials>
```

You do **not** need to edit `nuget.config`. You only need to provide the two
environment variables it references:

* `GITHUB_ACTOR` — your GitHub username.
* `GITHUB_TOKEN` — a GitHub Personal Access Token (PAT) with permission to read packages.

## 1. Create a Personal Access Token (PAT)

1. Sign in to GitHub and confirm you are a member of the `OpenSim-NGC` organization
   (you need read access to its packages). If you are not a member, ask a maintainer
   to add you.
2. Go to **Settings → Developer settings → Personal access tokens**.
3. Create a token:
   * **Classic token:** enable the `read:packages` scope (add `write:packages`
     only if you intend to publish packages).
   * **Fine-grained token:** grant the `OpenSim-NGC` organization access and set
     **Packages → Read-only** permission.
4. Copy the token value now — GitHub only shows it once.

> Keep the token secret. Never commit it to the repository or paste it into
> `nuget.config`. If a token is ever exposed, revoke it immediately on GitHub.

## 2. Export the environment variables

### Linux / macOS (bash / zsh)

Add these to your shell profile (`~/.bashrc`, `~/.zshrc`, or `~/.profile`) so they
persist across sessions, then open a new terminal:

```bash
export GITHUB_ACTOR="your-github-username"
export GITHUB_TOKEN="ghp_your_token_value"
```

To set them for the current session only:

```bash
GITHUB_ACTOR="your-github-username" GITHUB_TOKEN="ghp_your_token_value" dotnet restore
```

### Windows (PowerShell)

For the current session:

```powershell
$env:GITHUB_ACTOR = "your-github-username"
$env:GITHUB_TOKEN = "ghp_your_token_value"
```

To persist for your user account:

```powershell
[Environment]::SetEnvironmentVariable("GITHUB_ACTOR", "your-github-username", "User")
[Environment]::SetEnvironmentVariable("GITHUB_TOKEN", "ghp_your_token_value", "User")
```

Open a new terminal after setting persistent variables so they take effect.

## 3. Restore and build

With the variables set, restore resolves packages from both nuget.org and the
private GitHub feed:

```bash
dotnet restore
dotnet build --configuration Debug
```

## Troubleshooting

* **401 Unauthorized** during restore: the token is missing, expired, or lacks the
  `read:packages` scope. Verify `echo $GITHUB_ACTOR` / `echo $GITHUB_TOKEN`
  (PowerShell: `$env:GITHUB_ACTOR`), and confirm the token has package read access.
* **403 Forbidden**: your account is authenticated but not authorized for the
  `OpenSim-NGC` packages — ask a maintainer to grant organization access.
* **Package not found**: ensure the `github` source is listed by running
  `dotnet nuget locals all --list` and `dotnet restore --verbosity normal`, and that
  you are running the command from the solution root where `nuget.config` lives.
* **Variables not picked up**: environment variable changes only apply to terminals
  opened *after* they were set. Restart the terminal (or your IDE) and try again.

> CI note: the Docker Compose build tasks pass `GITHUB_ACTOR` / `GITHUB_TOKEN` as
> BuildKit secrets, so the same two variables must be present in your shell before
> running `docker compose build`.

# Configure #
## Standalone mode ##
Copy `OpenSim.ini.example` to `OpenSim.ini` in the `bin/` directory, and verify the `[Const]` section, correcting for your case.

On `[Architecture]` section uncomment only the line with Standalone.ini if you do now want HG, or the line with StandaloneHypergrid.ini if you do

copy the `StandaloneCommon.ini.example` to `StandaloneCommon.ini` in the `bin/config-include` directory.

The StandaloneCommon.ini file describes the database and backend services that OpenSim will use, and is set to use sqlite by default, which requires no setup.


## Grid mode ##
Each grid may have its own requirements, so FOLLOW your Grid instructions!
in general:
Copy `OpenSim.ini.example` to `OpenSim.ini` in the `bin/` directory, and verify the `[Const]` section, correcting for your case
 
On `[Architecture]` section uncomment only the line with Grid.ini if you do not want HG, or the line with GridHypergrid.ini if you do

and copy the `GridCommon.ini.example` file to `GridCommon.ini` inside the `bin/config-include` directory and edit as necessary
