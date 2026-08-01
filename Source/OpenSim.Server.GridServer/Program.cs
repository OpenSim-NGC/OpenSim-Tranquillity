/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.CommandLine;

using Autofac.Extensions.DependencyInjection;
using Autofac;

using OpenSim.Framework.Console;
using OpenSim.Server.Base;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Server.Base.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;

namespace OpenSim.Server.GridServer;

class Program
{
    public static IHost GridHost { get; private set; }

    public static int Main(string[] args)
    {
        var logconfigOption = new Option<string>("--logconfig")
        {
            Description = "Instruct log4net to use this file as configuration file.",
            DefaultValueFactory = ParseResult => "OpenSim.Server.GridServer.dll.config",
        };
        var inifileOption = new Option<List<string>>("--inifile")
        {
            Description = "Specify the location of zero or more .ini file(s) to read."
        };
        var inimasterOption = new Option<string>("--inimaster")
        {
            Description = "The path to the master ini file. The master ini file will be read first and then overridden by any .ini files specified by --inifile or --inidirectory options.",
            DefaultValueFactory = ParseResult => "OpenSim.Server.GridServer.ini",
        };
        var inidirectoryOption = new Option<string>("--inidirectory")
        {
            Description = "The path to folder for config ini files. The GridServer will read all of *.ini files " +
                              "in this directory and override GridServer.ini settings",
            DefaultValueFactory = ParseResult => "config",
        };
        var consoleOption = new Option<string>("--console")
        {
            Description = "console type, one of local or rest.",
            DefaultValueFactory = ParseResult => "local",
        };

        consoleOption.AcceptOnlyFromAmong("basic", "local", "rest", "mock");

        RootCommand rootCommand = new RootCommand("Launch the OpenSim Grid Server");

        rootCommand.Options.Add(logconfigOption);
        rootCommand.Options.Add(inifileOption);
        rootCommand.Options.Add(inimasterOption);
        rootCommand.Options.Add(inidirectoryOption);
        rootCommand.Options.Add(consoleOption);

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
                logConfig: parseResult.GetValue(logconfigOption), 
                iniFile: parseResult.GetValue(inifileOption), 
                iniMaster: parseResult.GetValue(inimasterOption), 
                iniDirectory: parseResult.GetValue(inidirectoryOption), 
                consoleType: parseResult.GetValue(consoleOption)
                )
            );
        }
         
        rootCommand.Parse(args).Invoke();

        return 0;
    }

    static void Configure(
        string logConfig, 
        List<string> iniFile, 
        string iniMaster, 
        string iniDirectory, 
        string consoleType
        )
    {
        ILog4NetBootstrapper log4NetBootstrapper = new Log4NetBootstrapper();
        var logPath = Environment.GetEnvironmentVariable("LOGDIR");
        if (string.IsNullOrWhiteSpace(logPath) is false)
            log4NetBootstrapper.LogPath = logPath;
        string effectiveLogConfig = log4NetBootstrapper.Configure(logConfig, "OpenSim.Server.GridServer.dll.config");

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

        // Bridges the legacy console "quit"/"shutdown" commands to the host lifetime.
        // Assigned inside ConfigureContainer (once the console/config exist) and its
        // HostLifetime is wired after the host is built (see below).
        HostLifetimeServerBase serverBase = null;

        builder.ConfigureContainer<ContainerBuilder>(registryBuilder =>
        {
            // The registry we're building into
            var registry = registryBuilder.ComponentRegistryBuilder;

            // Search the Service Runtime directory First
            var directoryPath = AppDomain.CurrentDomain.BaseDirectory;
            RegisterServices.Register(registry, directoryPath, "OpenSim.*.dll");

            // Register any plugins dropped into the addons directory also
            directoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addon-modules");
            RegisterServices.Register(registry, directoryPath);

            var gridConfig = new GridServerConfigSource(iniMaster, iniFile, iniDirectory);
            registryBuilder.RegisterInstance(gridConfig).AsSelf().SingleInstance();

            var prompt = "GridServer> ";
            ICommandConsole console = null;

            if (consoleType == "basic")
                console = new CommandConsole(prompt);
            else if (consoleType == "rest")
                console = new RemoteConsole(prompt);
            else if (consoleType == "mock")
                console = new MockConsole();
            else if (consoleType == "local")
                console = new LocalConsole(prompt);

            serverBase = new HostLifetimeServerBase { Console = console, Config = gridConfig.m_config };
            registryBuilder.RegisterInstance<IServerBase>(serverBase).
                AsImplementedInterfaces().
                SingleInstance();
        })
        .ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddLog4Net(log4NetConfigFile: effectiveLogConfig);
            loggingBuilder.AddConsole();
        })
        .ConfigureServices(services =>
        {
            // GridServer currently serves all HTTP through the legacy BaseHttpServer
            // (via service connectors), not ASP.NET controllers, so Kestrel/MVC is
            // disabled to avoid a port clash on the [Network] port. Re-enable together
            // with the ConfigureWebHostDefaults block below if controllers are added.
            // services.AddControllers().AddControllersAsServices();
            services.AddSingleton<IProcessSetupService, ProcessSetupService>();
            services.AddSingleton<IPidFileManager, PidFileManager>();
            services.AddSingleton<IMainServerAccessor, MainServerAccessor>();
            services.AddSingleton<IRuntimeMonitoringController, RuntimeMonitoringController>();
            services.AddSingleton<IStartupFailureCoordinator, StartupFailureCoordinator>();
            services.AddSingleton<IServiceConnectorLoader, GridServiceConnectorLoader>();
            services.AddSingleton<IGridHttpServerFactory, GridHttpServerFactory>();
            services.AddSingleton<IGridCertificateProvisioner, GridCertificateProvisioner>();
            services.AddSingleton<IGridServerRuntime, GridServerRuntime>();

            services.AddHostedService<ProcessSetupHostedService>();
            services.AddHostedService<PidFileHostedService>();

            services.AddSingleton<GridService>();
            services.AddHostedService(sp => sp.GetRequiredService<GridService>());
            services.AddHostedService<GridConsoleRunnerService>();
        });

        // ASP.NET / Kestrel is disabled: GridServer has no controllers and the legacy
        // BaseHttpServer already owns the [Network] port (e.g. 8002). Leaving Kestrel
        // enabled would bind GridServer:AspNetPort (default 8002) and clash with it.
        // Re-enable this block (and services.AddControllers() above) when ASP.NET
        // endpoints are introduced.
        // builder.ConfigureWebHostDefaults(webBuilder =>
        // {
        //     webBuilder.ConfigureServices((context, services) =>
        //     {
        //         string urls = context.Configuration.GetValue<string>("GridServer:AspNetUrls", string.Empty);
        //         if (!string.IsNullOrWhiteSpace(urls))
        //         {
        //             webBuilder.UseSetting(WebHostDefaults.ServerUrlsKey, urls);
        //         }
        //         else
        //         {
        //             int port = context.Configuration.GetValue<int>("GridServer:AspNetPort", 8002);
        //             webBuilder.UseUrls($"http://*:{port}");
        //         }
        //     });
        //
        //     webBuilder.Configure(app =>
        //     {
        //         app.UseRouting();
        //         app.UseEndpoints(endpoints => endpoints.MapControllers());
        //     });
        // });

        GridHost = builder.Build();

        // Now that the host exists, let the console "quit"/"shutdown" commands stop it.
        serverBase.HostLifetime = GridHost.Services.GetRequiredService<IHostApplicationLifetime>();

        GridHost.Run();
    }
}