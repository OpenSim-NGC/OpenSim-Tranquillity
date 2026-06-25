/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Server.Base.Hosting;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Generic-host entry point for the RegionServer.
/// </summary>
/// <remarks>
/// Replaces <c>Application.Main()</c> as the executable shell. The region runtime is
/// hosted behind <see cref="RegionService"/> and started/stopped through the host
/// lifecycle instead of a static main loop. Foreground mode hosts an interactive
/// console prompt loop; background mode relies on the host to keep the process alive
/// (no <c>ManualResetEvent</c> ownership and no <c>Environment.Exit</c> in this path).
/// </remarks>
public static class Program
{
    public static IHost RegionHost { get; private set; }

    public static async Task<int> Main(string[] args)
    {
        // Hook the appdomain to the crash reporter before anything else runs.
        Application.RegisterCrashDumpHandler();

        // Configure log4net up front so that early startup output is captured.
        ILog4NetBootstrapper log4NetBootstrapper = new Log4NetBootstrapper();
        string effectiveLogConfig = log4NetBootstrapper.Configure(
            ResolveLogConfig(args), "OpenSim.Server.RegionServer.dll.config");

        bool background = IsBackground(args);

        IHostBuilder builder = Host.CreateDefaultBuilder();

        builder.ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddLog4Net(log4NetConfigFile: effectiveLogConfig);
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton(new RegionHostOptions(args, background));
            services.AddSingleton<IProcessSetupService, ProcessSetupService>();
            services.AddSingleton<IStartupFailureCoordinator, StartupFailureCoordinator>();
            services.AddSingleton<IRuntimeMonitoringController, RuntimeMonitoringController>();
            services.AddSingleton<IRegionDiagnosticsService, RegionDiagnosticsService>();
            services.AddSingleton<IRegionRuntime, RegionRuntime>();

            services.AddHostedService<RegionService>();

            // Interactive console prompt loop is a host configuration concern, not
            // an inheritance concern: only host it in foreground mode.
            if (!background)
                services.AddHostedService<RegionConsoleRunnerService>();
        });

        RegionHost = builder.Build();

        await RegionHost.RunAsync().ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Reads the <c>--logconfig</c> switch value (if any) from the raw arguments
    /// without disturbing the full Nini configuration build performed later.
    /// </summary>
    private static string ResolveLogConfig(string[] args)
    {
        ArgvConfigSource probe = new ArgvConfigSource(args);
        probe.AddSwitch("Startup", "logconfig");
        return probe.Configs["Startup"].GetString("logconfig", string.Empty);
    }

    /// <summary>
    /// Determines whether the server should run without an interactive console.
    /// </summary>
    private static bool IsBackground(string[] args)
    {
        ArgvConfigSource probe = new ArgvConfigSource(args);
        probe.Alias.AddAlias("On", true);
        probe.Alias.AddAlias("Off", false);
        probe.Alias.AddAlias("True", true);
        probe.Alias.AddAlias("False", false);
        probe.Alias.AddAlias("Yes", true);
        probe.Alias.AddAlias("No", false);
        probe.AddSwitch("Startup", "background");
        return probe.Configs["Startup"].GetBoolean("background", false);
    }
}
