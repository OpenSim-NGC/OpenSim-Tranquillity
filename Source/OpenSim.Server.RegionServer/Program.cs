/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.CommandLine;

using Autofac;
using Autofac.Extensions.DependencyInjection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Server.Base.Hosting;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Generic-host entry point for the RegionServer.
/// </summary>
/// <remarks>
/// Replaces <c>Application.Main()</c> as the executable shell and mirrors the
/// GridServer startup: command-line parsing is handled by System.CommandLine, the
/// service provider is backed by Autofac (so plugin <c>IModule</c>s discovered by
/// <see cref="RegisterServices"/> can register themselves), and the merged .ini
/// configuration is fed to the host <c>IConfiguration</c>. The region runtime is
/// hosted behind <see cref="RegionService"/> and started/stopped through the host
/// lifecycle instead of a static main loop. Foreground mode hosts an interactive
/// console prompt loop; background mode relies on the host to keep the process alive
/// (no <c>ManualResetEvent</c> ownership and no <c>Environment.Exit</c> in this path).
/// </remarks>
public static class Program
{
    public static IHost RegionHost { get; private set; }

    public static int Main(string[] args)
    {
        var inifileOption = new Option<List<string>>("--inifile")
        {
            Description = "Specify the location of zero or more .ini file(s) to read."
        };
        var inimasterOption = new Option<string>("--inimaster")
        {
            Description = "The path to the master ini file. The master ini file will be read first and then overridden by any .ini files specified by --inifile or --inidirectory options.",
            DefaultValueFactory = ParseResult => "OpenSimDefaults.ini",
        };
        var inidirectoryOption = new Option<string>("--inidirectory")
        {
            Description = "The path to folder for config ini files. The RegionServer will read all of *.ini files " +
                              "in this directory and override OpenSim.ini settings",
            DefaultValueFactory = ParseResult => "config",
        };
        var consoleOption = new Option<string>("--console")
        {
            Description = "console type, one of basic, local or rest.",
            DefaultValueFactory = ParseResult => "local",
        };

        consoleOption.AcceptOnlyFromAmong("basic", "local", "rest");

        var backgroundOption = new Option<bool>("--background")
        {
            Description = "Run without an interactive console prompt loop.",
            DefaultValueFactory = ParseResult => false,
        };

        RootCommand rootCommand = new RootCommand("Launch the OpenSim Region Server");

        rootCommand.Options.Add(inifileOption);
        rootCommand.Options.Add(inimasterOption);
        rootCommand.Options.Add(inidirectoryOption);
        rootCommand.Options.Add(consoleOption);
        rootCommand.Options.Add(backgroundOption);

        ParseResult parseResult = rootCommand.Parse(args);

        if (parseResult.Errors.Count != 0)
        {
            foreach (var parseError in parseResult.Errors)
            {
                Console.Error.WriteLine(parseError.Message);
            }

            return 1;
        }
        else
        {
            rootCommand.SetAction(parseResult => Configure(
                args: args,
                iniFile: parseResult.GetValue(inifileOption),
                iniMaster: parseResult.GetValue(inimasterOption),
                iniDirectory: parseResult.GetValue(inidirectoryOption),
                consoleType: parseResult.GetValue(consoleOption),
                background: parseResult.GetValue(backgroundOption)
                )
            );
        }

        rootCommand.Parse(args).Invoke();

        return 0;
    }


    static void Configure(
        string[] args,
        List<string> iniFile,
        string iniMaster,
        string iniDirectory,
        string consoleType,
        bool background
        )
    {
        // Hook the appdomain to the crash reporter before anything else runs.
        Application.RegisterCrashDumpHandler();

        string logPath = Environment.GetEnvironmentVariable("LOGDIR");
        if (string.IsNullOrWhiteSpace(logPath))
            logPath = ".";

        IHostBuilder builder = Host.CreateDefaultBuilder();

        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddIniFile(iniMaster, optional: true, reloadOnChange: true);

            foreach (var item in iniFile)
            {
                configuration.AddIniFile(item, optional: true, reloadOnChange: true);
            }

            if (string.IsNullOrEmpty(iniDirectory) is false)
            {
                if (Directory.Exists(iniDirectory))
                {
                    foreach (var item in Directory.GetFiles(iniDirectory, "*.ini"))
                    {
                        configuration.AddIniFile(item, optional: true, reloadOnChange: true);
                    }
                }
            }
        });

        builder.UseServiceProviderFactory(new AutofacServiceProviderFactory());

        builder.ConfigureContainer<ContainerBuilder>(registryBuilder =>
        {
            // The registry we're building into
            var registry = registryBuilder.ComponentRegistryBuilder;

            // Search the Service Runtime directory first
            var directoryPath = AppDomain.CurrentDomain.BaseDirectory;
            RegisterServices.Register(registry, directoryPath, "OpenSim.*.dll");

            // Register any plugins dropped into the addons directory also
            directoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addon-modules");
            RegisterServices.Register(registry, directoryPath);
        })
        .ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddOpenSimLogging("OpenSim.Server.RegionServer", logPath);

            LoggerProvider.LoggerFactory = loggingBuilder.Services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton(new RegionHostOptions(iniFile, iniMaster, iniDirectory, consoleType, background));
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

        RegionHost.Run();
    }
}
