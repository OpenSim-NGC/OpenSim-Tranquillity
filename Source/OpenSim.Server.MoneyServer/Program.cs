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

using OpenSim.Server.Base;
using System.CommandLine.Parsing;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Server.MoneyServer;

class Program
{
    public static IHost MoneyHost { get; private set; }

    public static async Task<int> Main(string[] args)
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
                Description = "console type, one of basic, local or rest.",
                DefaultValueFactory = ParseResult => "local",
            };
        consoleOption.AcceptOnlyFromAmong("basic", "local", "rest");

        RootCommand rootCommand = new RootCommand
            {
                logconfigOption,
                inifileOption,
                inimasterOption,
                inidirectoryOption,
                consoleOption
            };  

        ParseResult parseResult = rootCommand.Parse(args);
        if (parseResult.Errors.Count != 0)
        {
            foreach (ParseError parseError in parseResult.Errors)
            {
                Console.Error.WriteLine(parseError.Message);
            }

            return 1;
        }

        var logConfig = parseResult.GetValue(logconfigOption);
        var iniFile = parseResult.GetValue(inifileOption);
        var iniMaster = parseResult.GetValue(inimasterOption);
        var iniDirectory = parseResult.GetValue(inidirectoryOption);
        var console = parseResult.GetValue(consoleOption);

        IHostBuilder builder = Host.CreateDefaultBuilder(args);

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
            
            // Search the Service Runtime directory First
            var directoryPath = AppDomain.CurrentDomain.BaseDirectory;
            // RegisterServices.Register(registry, directoryPath, "OpenSim.*.dll");
                
            // Register any plugins dropped into the addons directory also
            directoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addon-modules");
            // RegisterServices.Register(registry, directoryPath);                
        })
        .ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddLog4Net(log4NetConfigFile: logConfig);
            loggingBuilder.AddConsole();
        })
        .ConfigureServices(services =>
        {
            services.AddHostedService<MoneyService>();
            // services.AddHostedService<PidFileService>();
        });

        MoneyHost = builder.Build();
        MoneyHost.Run();

        return 0;
    }
}