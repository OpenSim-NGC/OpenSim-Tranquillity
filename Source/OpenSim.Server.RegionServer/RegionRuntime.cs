/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Server.Base.Hosting;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Host-managed adapter around the legacy <see cref="OpenSim"/> region runtime.
/// </summary>
/// <remarks>
/// Replicates the configuration-source construction that previously lived in
/// <c>Application.Main()</c> (Nini aliases, command-line switches and default
/// config sections) but no longer owns the process lifetime. Foreground and
/// background modes both use the non-blocking foreground <see cref="OpenSim"/>
/// runtime; the difference between the two is whether an interactive console
/// prompt loop is hosted, which is decided by the host rather than by subclass
/// inheritance.
/// </remarks>
public sealed class RegionRuntime : IRegionRuntime
{
    private readonly ILogger<RegionRuntime> _logger;
    private readonly RegionHostOptions _options;
    private readonly IProcessSetupService _processSetupService;
    private readonly IStartupFailureCoordinator _startupFailureCoordinator;
    private readonly IRuntimeMonitoringController _monitoringController;
    private readonly IRegionDiagnosticsService _diagnosticsService;
    private readonly IHostApplicationLifetime _hostLifetime;

    private readonly object _initializeLock = new();
    private bool _initialized;
    private OpenSimBase _sim;

    public RegionRuntime(
        ILogger<RegionRuntime> logger,
        RegionHostOptions options,
        IProcessSetupService processSetupService,
        IStartupFailureCoordinator startupFailureCoordinator,
        IRuntimeMonitoringController monitoringController,
        IRegionDiagnosticsService diagnosticsService,
        IHostApplicationLifetime hostLifetime)
    {
        _logger = logger;
        _options = options;
        _processSetupService = processSetupService;
        _startupFailureCoordinator = startupFailureCoordinator;
        _monitoringController = monitoringController;
        _diagnosticsService = diagnosticsService;
        _hostLifetime = hostLifetime;
    }

    public void Initialize()
    {
        if (_initialized)
            return;

        lock (_initializeLock)
        {
            if (_initialized)
                return;

            // Apply process-level defaults (culture, ServicePointManager, thread
            // pool) that previously executed at the top of Application.Main().
            _processSetupService.Apply(new ProcessSetupOptions
            {
                ConfigureThreadPoolMaxThreads = true,
            });

            string error = string.Empty;
            if (Util.IsEnvironmentSupported(ref error))
                _logger.LogInformation("[OPENSIM MAIN]: Environment is supported by OpenSimulator.");
            else
                _logger.LogWarning("[OPENSIM MAIN]: Environment is not supported by OpenSimulator: {Error}", error);

            _logger.LogInformation("[OPENSIM MAIN]: Default culture changed to {Culture}",
                Culture.GetDefaultCurrentCulture().DisplayName);

            IConfigSource configSource = BuildConfigSource(_options.Args);

            try
            {
                _sim = new OpenSim(configSource);

                // Route interactive "quit"/"shutdown" (and Ctrl-C) through the host lifetime
                // instead of the legacy inline teardown + Environment.Exit(0). The host then
                // performs the single teardown via ShutdownHosted() in Stop().
                _sim.HostShutdownRequested = () => _hostLifetime.StopApplication();

                // Non-blocking: StartupSpecific runs and returns. The host keeps
                // the process alive instead of a blocking main loop.
                _sim.Startup();

                // Diagnostics and watchdog lifetime are owned by the runtime, not
                // by the startup inheritance chain.
                _diagnosticsService.Start(
                    _sim.Config?.Configs["Startup"],
                    _sim.GetUptimeReport,
                    _sim.GetThreadsReport);
            }
            catch (Exception e)
            {
                _startupFailureCoordinator.ThrowFatal("[OPENSIM MAIN]: Fatal error while starting the region runtime.", e);
            }

            _initialized = true;
        }
    }

    public void Stop()
    {
        if (_sim is null)
            return;

        try
        {
            _diagnosticsService.Stop();
            _monitoringController.DisableWatchdog();

            // The host owns process exit; ShutdownHosted() suppresses the legacy
            // Environment.Exit(0) and performs the single runtime teardown.
            _sim.ShutdownHosted();

            _monitoringController.StopWorkManager();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[OPENSIM MAIN]: Error while shutting down the region runtime.");
        }
        finally
        {
            _sim = null;
        }
    }

    /// <summary>
    /// Builds the Nini configuration source from the original command-line
    /// arguments, preserving the aliases, switches and default sections that
    /// <c>Application.Main()</c> historically configured.
    /// </summary>
    internal static IConfigSource BuildConfigSource(string[] args)
    {
        ArgvConfigSource configSource = new ArgvConfigSource(args);

        configSource.Alias.AddAlias("On", true);
        configSource.Alias.AddAlias("Off", false);
        configSource.Alias.AddAlias("True", true);
        configSource.Alias.AddAlias("False", false);
        configSource.Alias.AddAlias("Yes", true);
        configSource.Alias.AddAlias("No", false);

        configSource.AddSwitch("Startup", "background");
        configSource.AddSwitch("Startup", "inifile");
        configSource.AddSwitch("Startup", "inimaster");
        configSource.AddSwitch("Startup", "inidirectory");
        configSource.AddSwitch("Startup", "physics");
        configSource.AddSwitch("Startup", "gui");
        configSource.AddSwitch("Startup", "console");
        configSource.AddSwitch("Startup", "save_crashes");
        configSource.AddSwitch("Startup", "crash_dir");
        configSource.AddSwitch("Startup", "logconfig");

        configSource.AddConfig("StandAlone");
        configSource.AddConfig("Network");

        // Crash dump settings are read by the static crash handler in Application.
        Application.m_saveCrashDumps = configSource.Configs["Startup"].GetBoolean("save_crashes", false);
        Application.m_crashDir = configSource.Configs["Startup"].GetString("crash_dir", Application.m_crashDir);

        return configSource;
    }
}
