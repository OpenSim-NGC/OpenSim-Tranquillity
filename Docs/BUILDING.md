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

The output from the publish will be in build/<Configuration>/net10.0/<platform>/

Where Configuration is either Debug or Release and Platform is either linux-x64 or win-x64 as shown above.

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

### Garbage collector and thread pool

Each server's `runtimeconfig.template.json` turns on server GC, concurrent GC
and tiered compilation. The build writes these into the published
`<server>.runtimeconfig.json` under `runtimeOptions.configProperties`, where
the runtime reads them. The thread pool is left at the .NET defaults. The
RegionServer logs the settings in force at startup, for example on a
16-thread machine:

```
[OPENSIM MAIN] Running GC in server mode: concurrent True, dynamic adaptation on, up to 16 heaps, conserve memory 0; thread pool 16 to 1000 worker threads
```

Server GC is the default. Since .NET 9 it runs with dynamic adaptation: it
starts with one heap and adds heaps, up to one per core, as the load grows.
Workstation GC always uses one heap; it can suit small machines and hosts that
run many server processes. These .NET environment variables choose at start,
without editing a file:

```bash
export DOTNET_gcServer=0                  # 0: workstation GC, 1: server GC
export DOTNET_GCConserveMemory=5          # 0 (default) to 9: compact harder to keep the heap small
export DOTNET_GCHeapCount=0x4             # server GC: a fixed number of heaps (hexadecimal); turns dynamic adaptation off
export DOTNET_GCDynamicAdaptationMode=0   # server GC: dynamic adaptation off, one heap per core
```

Server GC without dynamic adaptation keeps a heap per core from the start and
uses noticeably more memory (about 90 MiB more for one idle region on a
16-thread machine). `DOTNET_gcConcurrent` does not override
`System.GC.Concurrent` from `runtimeconfig.json`; to turn concurrent GC off,
change it in the published `runtimeconfig.json`. With Compose, put the
variables under the service's `environment:` (commented examples are in
`compose.yaml`); with `docker run`, pass them with `-e`.

The thread pool starts with as many worker threads as the machine has
processors and adds more under load; the RegionServer caps its worker threads
at 1000 when it starts. To fix other bounds, add
`System.Threading.ThreadPool.MinThreads` and
`System.Threading.ThreadPool.MaxThreads` (plain integers) to
`configProperties` in the template before building or in the published
`runtimeconfig.json`, or set these environment variables (.NET 10;
hexadecimal):

```bash
export DOTNET_ThreadPool_ForceMinWorkerThreads=0x40    # at least 64 worker threads
export DOTNET_ThreadPool_ForceMaxWorkerThreads=0x3E8   # at most 1000 worker threads
```

A minimum below the processor count makes bursts of work wait for threads to
be added. While a bound is set this way, the program cannot change it: the
console's `debug threadpool set worker` fails for it, and with a maximum above
1000 the RegionServer logs "Unable to set thread pool max threads" at start and
keeps yours.

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
