/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

using Autofac.Extensions.DependencyInjection;
using Autofac;

using ConfigurationSubstitution;
using OpenSim.Server.Base;
using OpenSim.Framework;

namespace OpenSim.Server.MoneyServer
{
    class Program
    {
        public static IHost MoneyHost { get; private set; }

        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand();

            var logconfigOption = new Option<string>
                (name: "--logconfig", description: "Instruct log4net to use this file as configuration file.",
                getDefaultValue: () => "OpenSim.Server.MoneyServer.dll.config");
            var backgroundOption = new Option<bool>
                (name: "--background", description: "If true, the MoneyServer will run in the background",
                getDefaultValue: () => false);
            var inifileOption = new Option<List<string>>
                (name: "--inifile", description: "Specify the location of zero or more .ini file(s) to read.");
            var inimasterOption = new Option<string>
                (name: "--inimaster", description: "The path to the master ini file.",
                getDefaultValue: () => "MoneyServer.ini");
            var inidirectoryOption = new Option<string>(
                    name: "--inidirectory", 
                    description:    "The path to folder for config ini files. The MoneyServer will read all of *.ini files " +
                                    "in this directory and override MoneyServer.ini settings",
                    getDefaultValue: () => "config");
            var consoleOption = new Option<string>
                (name: "--console", description: "console type, one of basic, local or rest.", 
                getDefaultValue: () => "local")
                .FromAmong("basic", "local", "rest");

            rootCommand.AddGlobalOption(logconfigOption);
            rootCommand.AddGlobalOption(backgroundOption);
            rootCommand.AddGlobalOption(inifileOption);
            rootCommand.AddGlobalOption(inimasterOption);
            rootCommand.AddGlobalOption(inidirectoryOption);
            rootCommand.AddGlobalOption(consoleOption);

            rootCommand.SetHandler((logconfig, background, inifile, inimaster, inidirectory, console) =>
            {
                StartMoneyService(args, logconfig, background, inifile, inimaster, inidirectory, console);
            },
            logconfigOption,
            backgroundOption, 
            inifileOption, 
            inimasterOption, 
            inidirectoryOption, 
            consoleOption);

            return await rootCommand.InvokeAsync(args);
        }

        private static void StartMoneyService(
            string[] args, 
            string logconfig, 
            bool background, 
            List<string> inifile, 
            string inimaster,
            string inidirectory,
            string console
            )
        {
            IHostBuilder builder = Host.CreateDefaultBuilder(args);

            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddIniFile(inimaster, optional: true, reloadOnChange: true);
                foreach (var item in inifile)
                {
                    configuration.AddIniFile(item, optional: true, reloadOnChange: true);
                }

                if (string.IsNullOrEmpty(inidirectory) is false)
                {
                    if (Directory.Exists(inidirectory))
                    {
                        foreach (var item in Directory.GetFiles(inidirectory, "*.ini"))
                        {
                            configuration.AddIniFile(item, optional: true, reloadOnChange: true);
                        }
                    }
                }
                
                configuration.EnableSubstitutions("$(", ")");
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
            })
            .ConfigureLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddLog4Net(log4NetConfigFile: logconfig);
                loggingBuilder.AddConsole();
            })
            .ConfigureServices(services =>
            {
                services.AddHostedService<MoneyService>();
                // services.AddHostedService<PidFileService>();
            });

            MoneyHost = builder.Build();
            LogManager.LoggerFactory = MoneyHost.Services.GetService<ILoggerFactory>();
            //XmlConfigurator.Configure();
            //Application.Configure(args);

            MoneyHost.Run();
        }
    }
}