# Tranquillity 1.1 — Release Notes

**Previous release:** `1.0`
**This release:** `1.1`

---

## Highlights

- **Complete Excision of Log4Net** — Log4Net has been fully removed from OpenSim-Tranquillity. All application and region code logs through the standard `Microsoft.Extensions.Logging.ILogger` interface.
- **Modernized Logging Pipeline with Serilog Backend** — File logging is handled via a Serilog rolling-file adapter that preserves the classic OpenSimulator log file format and output semantics.
- **Interactive Console Isolation** — Console log output routes through `OpenSimConsoleLoggerProvider` and `OpenSimConsoleLogSink`, preventing background log lines from corrupting the interactive command prompt or readline buffer.
- **Dedicated Stats Logging** — Support for splitting special statistics logging (`special.StatsLogger`) into a dedicated rolling stats file while keeping general application logs separate.

---

## Logging Architecture & Configuration

### Overview

OpenSimulator logging is now built on the standard .NET generic host logging pipeline using `Microsoft.Extensions.Logging` and Serilog:

- **Console Output**: Uses `OpenSimConsoleLoggerProvider` attached to `OpenSimConsoleLogSink`. Log levels can be adjusted dynamically at runtime via the `set log level <level>` console command.
- **File Output**: Configured in `OpenSimLoggingBuilderExtensions.AddOpenSimLogging(...)` using Serilog's rolling file sink with `RollingInterval.Day` and multi-process file sharing enabled (`shared: true`).
- **Format**: Formatted using `OpenSimLogFileFormatter`, preserving the standard format:
  ```text
  YYYY-MM-DD HH:mm:ss,fff LEVEL - [LoggerName] message
  ```
- **Stats Log Separation**: When `Startup:StatsLogFile` is configured, Serilog automatically routes events from `special.StatsLogger` to the dedicated stats file and excludes them from the primary application log.

---

### Log File Location Precedence

The effective path for the application log file is resolved in the following order:

1. **`Startup:logfile` Configuration Key** (Highest Priority):
   If explicitly specified in configuration or via CLI/ini, Serilog writes to this exact file path.
2. **`LOGDIR` Environment Variable**:
   If `Startup:logfile` is not specified, the path defaults to:
   ```text
   Path.Combine(LOGDIR, $"{serviceName}.log")
   ```
   (where `serviceName` is `OpenSim.Server.RegionServer`, `OpenSim.Server.GridServer`, etc.).
3. **Current Working Directory** (Default Fallback):
   If `LOGDIR` is unset or empty, the log file is written to `./{serviceName}.log`.

The stats log file defaults to `./OpenSimStats.log` unless overridden by `Startup:StatsLogFile`.

---

### Configuring the Log Location from the Command Line

You can configure or override the log file locations from the command line using any of the following approaches:

#### 1. Via Command-Line Key-Value Arguments
Because the generic host in RegionServer, GridServer, and MoneyServer parses command-line arguments into `IConfiguration`, you can pass the configuration keys directly:

```bash
# Set primary log file location
./OpenSim.Server.RegionServer --Startup:logfile=/var/log/opensim/region.log

# Set both primary log and stats log locations
./OpenSim.Server.RegionServer --Startup:logfile=/var/log/opensim/region.log --Startup:StatsLogFile=/var/log/opensim/stats.log
```

#### 2. Via the `LOGDIR` Environment Variable
To direct the default service logs to a custom directory without altering file naming conventions:

```bash
LOGDIR=/var/log/opensim ./OpenSim.Server.RegionServer
```

This writes daily rolling logs such as `/var/log/opensim/OpenSim.Server.RegionServer.log` (and `/var/log/opensim/OpenSimStats.log`).

#### 3. Via INI Configuration File Passed on the Command Line
Using the `--inifile` argument to supply a configuration snippet:

```bash
./OpenSim.Server.RegionServer --inifile mylogging.ini
```

Where `mylogging.ini` contains:

```ini
[Startup]
    logfile = /var/log/opensim/region.log
    StatsLogFile = /var/log/opensim/stats.log
```

---

## Breaking Changes & Removals

- **`log4net` Removed**: The `log4net` package and repository configuration have been removed from OpenSimulator projects (`OpenSim.Framework.Console`, `OpenSim.Framework.Servers`, etc.).
- **`--logconfig` CLI Option Removed**: The `--logconfig` command-line switch and `ILog4NetBootstrapper` / `Log4NetBootstrapper` classes have been removed. Log configuration is now fully managed via host configuration and Serilog.
- **`OpenSimAppender` Removed**: The custom Log4Net appender `OpenSimAppender` has been replaced by `OpenSimConsoleLoggerProvider`.
- **Legacy Config Files Removed**: `MoneyServer.exe.config` and `AppData/log4net.xml` have been removed.
