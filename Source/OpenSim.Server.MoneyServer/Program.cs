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

using OpenSim.Server.Base;
using OpenSim.Server.Base.Hosting;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using Microsoft.AspNetCore.Hosting;
using OpenSim.Server.MoneyServer.Models;
using Microsoft.AspNetCore.Builder;

namespace OpenSim.Server.MoneyServer;

class Program
{
    public static IHost MoneyHost { get; private set; }

    public static int Main(string[] args)
    {
        var logconfigOption = new Option<string>("--logconfig")
        {
            Description = "Instruct log4net to use this file as configuration file.",
            DefaultValueFactory = ParseResult => "OpenSim.Server.MoneyServer.dll.config",
        };
        var inifileOption = new Option<List<string>>("--inifile")
        {
            Description = "Specify the location of zero or more .ini file(s) to read."
        };
        var inimasterOption = new Option<string>("--inimaster")
        {
            Description = "The path to the master ini file. The master ini file will be read first and then overridden by any .ini files specified by --inifile or --inidirectory options.",
            DefaultValueFactory = ParseResult => "MoneyServer.ini",
        };
        var inidirectoryOption = new Option<string>("--inidirectory")
        {
            Description = "The path to folder for config ini files. The MoneyServer will read all of *.ini files " +
                              "in this directory and override MoneyServer.ini settings",
            DefaultValueFactory = ParseResult => "config",
        };
        var consoleOption = new Option<string>("--console")
        {
            Description = "console type, one of local or rest.",
            DefaultValueFactory = ParseResult => "local",
        };

        consoleOption.AcceptOnlyFromAmong("basic", "local", "rest", "mock");

        RootCommand rootCommand = new RootCommand("Launch the OpenSim Money Server");

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
            rootCommand.SetAction(parseResult => Configure(new ServerStartupOptions
                {
                    LogConfig    = parseResult.GetValue(logconfigOption),
                    IniFiles     = parseResult.GetValue(inifileOption) ?? [],
                    IniMaster    = parseResult.GetValue(inimasterOption),
                    IniDirectory = parseResult.GetValue(inidirectoryOption),
                    ConsoleType  = parseResult.GetValue(consoleOption),
                })
            );
        }
         
        rootCommand.Parse(args).Invoke();

        return 0;
    }

    static void Configure(ServerStartupOptions options)
    {
        ILog4NetBootstrapper log4NetBootstrapper = new Log4NetBootstrapper();
        string effectiveLogConfig = log4NetBootstrapper.Configure(options.LogConfig, "OpenSim.Server.MoneyServer.dll.config");

        IHostBuilder builder = Host.CreateDefaultBuilder();

        // Transitional bridge for services that still require Nini IConfigSource.
        ILegacyConfigSourceAccessor legacyConfigAccessor = new LegacyIniConfigSourceAccessor(options);

        // Transitional bridge for static MainConsole usage.
        IConsoleContext consoleContext = new ConsoleContext(new ConsoleFactory().Create(options.ConsoleType, "MoneyServer> "));

        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddOpenSimIniFiles(options);
        });

        builder.UseServiceProviderFactory(new AutofacServiceProviderFactory());

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

            registryBuilder.RegisterInstance<IConsoleContext>(consoleContext).AsImplementedInterfaces().SingleInstance();
            registryBuilder.RegisterInstance<ILegacyConfigSourceAccessor>(legacyConfigAccessor).AsImplementedInterfaces().SingleInstance();

            registryBuilder.RegisterInstance<IServerBase>(
                new ServerBase { Console = consoleContext.Console, Config = legacyConfigAccessor.ConfigSource }).AsImplementedInterfaces().SingleInstance();

            registryBuilder.RegisterType<MoneyDBService>().As<IMoneyDBService>().AsSelf().SingleInstance();
        })
        .ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddLog4Net(log4NetConfigFile: effectiveLogConfig);
            loggingBuilder.AddConsole();
        })
        .ConfigureServices(services =>
        {
            services.AddControllers().AddControllersAsServices();
            services.AddSingleton<IProcessSetupService, ProcessSetupService>();
            services.AddSingleton<IPidFileManager, PidFileManager>();
            services.AddSingleton<IMainServerAccessor, MainServerAccessor>();
            services.AddSingleton<IRuntimeMonitoringController, RuntimeMonitoringController>();
            services.AddSingleton<MoneySessionStore>();
            services.AddSingleton<IStartupFailureCoordinator, StartupFailureCoordinator>();
            services.AddSingleton<IMoneyServerRuntime, MoneyServerRuntime>();

            services.AddHostedService<ProcessSetupHostedService>();
            services.AddHostedService<PidFileHostedService>();

            services.AddSingleton<MoneyService>();
            services.AddSingleton<IMoneyServiceCore>(sp => sp.GetRequiredService<MoneyService>());
            services.AddHostedService(sp => sp.GetRequiredService<MoneyService>());
            services.AddHostedService<MoneyConsoleRunnerService>();
        });

        builder.ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.ConfigureServices((context, services) =>
            {
                string urls = context.Configuration.GetValue<string>("MoneyServer:AspNetUrls", string.Empty);
                if (!string.IsNullOrWhiteSpace(urls))
                {
                    webBuilder.UseSetting(WebHostDefaults.ServerUrlsKey, urls);
                }
                else
                {
                    int port = context.Configuration.GetValue<int>("MoneyServer:AspNetPort", 8009);
                    webBuilder.UseUrls($"http://*:{port}");
                }
            });

            webBuilder.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });
        });

        MoneyHost = builder.Build();
        MoneyHost.Run();
    }
}