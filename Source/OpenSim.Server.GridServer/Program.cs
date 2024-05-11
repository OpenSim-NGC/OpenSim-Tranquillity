/*
 * OpenSim.NGC Tranquillity 
 * Copyright (C) 2024 Utopia Skye LLC and its affiliates.
 * All rights reserved.

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */


using Autofac;
using Autofac.Extensions.DependencyInjection;

using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using ConfigurationSubstitution;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers.HttpServer;

using OpenSim.Server.Base;
using OpenSim.Server.Common;
using OpenSim.Server.Handlers;
using OpenSim.Data.MySQL;
using OpenSim.Services;

namespace OpenSim.Server.GridServer
{
    class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand();

            var consoleOption = new Option<string>
                (name: "--console", description: "console type, one of basic, local or rest.", getDefaultValue: () => "local")
                .FromAmong("basic", "local", "rest");
            var inifileOption = new Option<List<string>>
                (name: "--inifile", description: "Specify the location of zero or more .ini file(s) to read.");
            var promptOption = new Option<string>
                (name: "--prompt", description: "Overide the server prompt",
                getDefaultValue: () => "GRID> ");

            rootCommand.AddGlobalOption(consoleOption);
            rootCommand.AddGlobalOption(inifileOption);
            rootCommand.AddGlobalOption(promptOption);

            rootCommand.SetHandler((console, inifile, prompt) =>
            {
                StartGrid(console, inifile, prompt);
            },
            consoleOption, inifileOption, promptOption);

            return await rootCommand.InvokeAsync(args);
        }

        private static void StartGrid(string console, List<string> inifile, string prompt)
        {
            IHostBuilder builder = Host.CreateDefaultBuilder()
                .UseServiceProviderFactory(new AutofacServiceProviderFactory())
                .ConfigureAppConfiguration(configuration =>
                {
                    //configuration.AddCommandLine(args, switchMappings);
                    configuration.AddIniFile("GridServer.ini", optional: true, reloadOnChange: false);
                    foreach (var item in inifile)
                    {
                        configuration.AddIniFile(item, optional: true, reloadOnChange: true);
                    }
                    configuration.EnableSubstitutions("$(", ")");
                })
                .ConfigureContainer<ContainerBuilder>(builder =>
                {
                    builder.RegisterType<OpenSimServer>().SingleInstance();
                    builder.RegisterType<GridServer>().SingleInstance();

                    builder.RegisterType<BaseHttpServer>().As<IHttpServer>();

                    if (console == "basic")
                        builder.RegisterType<MainConsole>().As<ICommandConsole>().SingleInstance();
                    else if (console == "rest")
                        builder.RegisterType<RemoteConsole>().As<ICommandConsole>().SingleInstance();
                    else if (console == "mock")
                        builder.RegisterType<MockConsole>().As<ICommandConsole>().SingleInstance();
                    else
                        builder.RegisterType<LocalConsole>().As<ICommandConsole>().SingleInstance();
                        
                    // Register Grid Modules
                    builder.RegisterModule(new MySQLDataModule());
                    builder.RegisterModule(new OpenSimServicesModule());
                    builder.RegisterModule(new OpenSimServerHandlersModule());
                })
                .ConfigureServices(services =>
                {
                    services.AddHostedService<GridService>();
                    services.AddHostedService<PidFileService>();
                });

            IHost host = builder.Build();

            host.Run();
        }
    }
}
