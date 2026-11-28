# git clone

get or update source from git

 `git clone https://github.com/OpenSim-NGC/OpenSim-Tranquillity.git`
  
### Building
 Prebuild is no longer used.  There is a top level Solution (sln) and csproj files for each
 of the projects in the solution. The projects are designed to be publishable, which
 optimizes the build for a specific platform:
 
 To run a build from a CLI run:

 dotnet publish --configuration Debug -r linux-x64
 dotnet publish --configuration Release -r linux-x64

 dotnet publish --configuration Debug -r win-x64
 dotnet publish --configuration Release -r win-x64

If no configuration is specified the default is a release build. If no platform is specified default 
is the platform being used for the compilation.

Publishing the solution publishes every project except the test projects, each to its own project folder.
The servers, which include all the libraries and plugins they need, land in:

 Source/OpenSim.Server.RegionServer/bin/<Configuration>/net10.0/<platform>/publish/
 Source/OpenSim.Server.GridServer/bin/<Configuration>/net10.0/<platform>/publish/
 Source/OpenSim.Server.MoneyServer/bin/<Configuration>/net10.0/<platform>/publish/

Where Configuration is either Debug or Release and Platform is either linux-x64 or win-x64 as shown above.
Run a server, and configure it (see below), from its publish folder.

To publish one server to a folder of your choice, publish its project with `-o`, as
[tools/publish.sh](../tools/publish.sh) and the Dockerfiles do:

 dotnet publish Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj --configuration Release -r linux-x64 -o publish/linux-x64/OpenSim.Server.RegionServer

For running and writing tests (including YEngine state-load telemetry), see [TESTING.md](TESTING.md).

For the completed solution warning cleanup, verification commands and excluded-project backlog, see
[WarningCleanup.md](WarningCleanup.md).

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

# NuGet restore and Docker builds #

The repository's `nuget.config` uses nuget.org. Restore and build with the .NET
SDK version specified by `global.json`:

```bash
dotnet restore
dotnet build --configuration Debug
```

To build the GridServer, MoneyServer and RegionServer images, run from the
repository root:

```bash
docker compose build
```

The Dockerfiles use the .NET 10 SDK and runtime images. Restore requires access
to nuget.org; GitHub credentials are not required with the included NuGet configuration.

### Container startup and optional mounts

The container entrypoints run the packaged startup scripts in the foreground.
Set `GRID_SERVICE_NAME` and `REGION_NAME` for Compose; MoneyServer needs no name:

```bash
export GRID_SERVICE_NAME=mygrid
export REGION_NAME=myregion
```

GridServer reads `/app/config/GridServer.mygrid.ini`. RegionServer reads
`/app/config/RegionServer.ini`, its packaged `RegionServerDefaults.ini`, and
the `.ini` files in `/app/config/regions/myregion`. MoneyServer reads
`/app/config/MoneyServer.ini`. GridServer and MoneyServer do not scan other
files in `/app/config`; use includes as needed. The scripts accept one name
argument for GridServer and RegionServer, and none for MoneyServer.

The scripts default `CONFIGDIR` to `/app/config` and these environment variables 
and `CONFIGFILE` can be overridden. RegionServer also accepts `DEFAULTCONFIG` and 
`REGIONDIR`. The directories and selected configuration must exist. Set `SERVER_NAME` 
if the hostname cannot be resolved to the fully qualified name required by your 
configuration. The scripts run the server directly, including outside containers; 
the existing screen-based command and stop scripts cannot control these processes. 
The servers use a local interactive console, so Compose allocates a TTY, keeps 
stdin open, and allows 60 seconds for shutdown. When using `docker run`, use `-it` 
for the console and `--stop-timeout 60` to allow time for shutdown.

To supply host directories, create separate config and log directories for each
server, then set their absolute paths and use the optional Compose file:

```bash
export GRID_CONFIG_DIR=/path/to/grid/config
export GRID_LOG_DIR=/path/to/grid/logs
export MONEY_CONFIG_DIR=/path/to/money/config
export MONEY_LOG_DIR=/path/to/money/logs
export REGION_CONFIG_DIR=/path/to/region/config
export REGION_LOG_DIR=/path/to/region/logs

docker compose -f compose.yaml -f compose.mounts.yaml up
```

All six directory variables and directories are required when using this override,
even when starting just one service. Config directories are mounted read-only at
`/app/config`. Any referenced include files must also be accessible inside the
container.

Log directories are mounted at `/app/logdata`. The images run as the `app`
user (UID 1654 in the current images), so each host log directory must be
writable by that user. Config directories must be readable by it. The images
default `LOGDIR` to `/app/logdata`; it can be overridden with an environment
variable. Without the mount, logs there are lost when the container
is removed.

The log mount does not automatically redirect database files, caches, region
configuration, or explicitly configured log paths. Configure and mount those paths
separately where persistence is needed, and back up external databases
independently. Do not mount over `/app/data`: the RegionServer image contains
packaged resources there.

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

# Runtime memory settings (opt-in) #

RegionServer, GridServer and MoneyServer run with workstation GC, concurrent GC and the
.NET runtime's default thread-pool limits; that is today's behaviour and it stays the
default. An operator on a small host can opt in to the knobs below without editing any
shipped file (`runtimeconfig.template.json` and the built `*.runtimeconfig.json` included):
the .NET runtime reads these as process environment variables, which take effect for a
single deployment (a container's `environment:`/`-e`, a systemd unit's `Environment=`, or
the shell that starts the binary) and override the compiled-in configuration. See
[.NET runtime configuration settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/).

Number values are read differently in each place: a `runtimeconfig.json`/`runtimeconfig.template.json`
property takes a decimal number, while the same setting as a `DOTNET_`-prefixed environment variable
is read as **hexadecimal**, with or without a leading `0x` ([.NET runtime configuration
settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/), "Ways to specify the
configuration"; this is Microsoft's general rule for numeric settings, not something the heap-limit
variables alone state). The two heap-hard-limit variables below name "hexadecimal value" in their own
documentation, so a decimal value sets the wrong limit. `DOTNET_GCConserveMemory` falls under the
same general rule but its own page does not call hex out by name for it; since its only valid values
are the single digits 0-9 (the same digit in hex and in decimal), the distinction makes no practical
difference for it.

| setting | variable | what it costs or saves |
|---|---|---|
| Server GC | `DOTNET_gcServer=1` (`0` workstation GC / `1` server GC; not a hexadecimal value) | A separate GC heap and thread per CPU core instead of one shared heap. Raises throughput on a busy, many-core host and raises memory use. Leave at the default (`0`, workstation GC) on a small host; see the measured effect below. |
| Conserve memory | `DOTNET_GCConserveMemory=9` (valid range `0`-`9`; see the hex note above) | Trades GC pause time and throughput for a smaller heap budget on workstation GC. `0` (default) favours throughput; higher values shrink the committed-but-not-resident portion of the heap at the cost of more frequent, longer collections. See [GCConserveMemory](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#conserve-memory) and the measured effect below. |
| Heap hard limit (bytes) | `DOTNET_GCHeapHardLimit=<hexadecimal bytes>` | Caps committed GC memory instead of leaving it unbounded (today: no hard limit). Protects a small host from a runaway heap at the cost of an `OutOfMemoryException` instead of growth if the limit is too low. Worked example: a 200 MiB limit is `209715200` as the `runtimeconfig.json` decimal value, `DOTNET_GCHeapHardLimit=0xC800000` (or `C800000`) as the environment variable. See [GCHeapHardLimit](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-limit). |
| Heap hard limit (percent) | `DOTNET_GCHeapHardLimitPercent=<hexadecimal percent, 1-100>` | Caps committed GC memory as a share of physical memory instead of a byte count. Writing the percentage in decimal sets the wrong limit: `=90` is read as hex `0x90` (144), outside the valid 1-100 range. Worked example: a 90% limit is `90` as the `runtimeconfig.json` decimal value, `DOTNET_GCHeapHardLimitPercent=0x5A` (or `5A`) as the environment variable. See [GCHeapHardLimitPercent](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-limit). |
| Invariant globalization | `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` (`0` access cultural data / `1` invariant mode; not a hexadecimal value) | Skips loading ICU and switches all culture-sensitive string comparison and formatting to ordinal/invariant rules. Saves that memory (shared file pages on Windows; a real process cost on Linux, where ICU is not already resident) but changes string sorting, casing and formatting for any culture-sensitive text. See [Invariant mode](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/globalization#invariant-mode). |

These are runtime knobs, not `OpenSim.ini`/`GridServer.ini`/`MoneyServer.ini` settings: they
take effect before OpenSimulator's own configuration is read, and `config show` on the
console does not list them.

### Measured effect (RegionServer)

Measured with Windows process private bytes and .NET's own committed-GC-memory counter
(`dotnet-counters`, `dotnet.gc.last_collection.memory.committed_size`), 60 seconds after the
region(s) reported ready, on one 256 m region and on four 256 m regions sharing one
RegionServer process (RegionServer can host several regions in one process, sharing one GC heap
between them).

| configuration | 1 region, private bytes | 1 region, GC committed | 4 regions, private bytes | 4 regions, GC committed |
|---|---:|---:|---:|---:|
| today's behaviour (the bug in effect: workstation GC regardless of the template) | 86.4 MiB (1 start) | not measured | 191.6 MiB (1 start) | not measured |
| server GC as the templates intended (`DOTNET_gcServer=1`) | 105.6 MiB (1 start) | not measured | 235.8 MiB (median of 2 starts: 234.6, 237.0) | 74.0 MiB (median of 2 starts: 71.4, 76.5) |
| workstation GC, `DOTNET_GCConserveMemory=9` | 73.8 MiB (1 start) | not measured | 194.8 MiB (median of 2 starts: 196.2, 193.3) | 62.5 MiB (median of 2 starts: 62.5, 62.6) |

Each cell is one or two starts, as marked, and readings at one size vary by several MiB between
starts. Server GC cost about 19 MiB more than today's behaviour with one region and about 44 MiB
more with four. `GCConserveMemory=9` showed 12.6 MiB less than today's behaviour with one region
and no saving with four.
