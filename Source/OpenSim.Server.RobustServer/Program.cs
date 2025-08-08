/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.CommandLine;
using System.Reflection;
using Autofac.Extensions.DependencyInjection;
using Autofac;
using Autofac.Core;
using Autofac.Core.Registration;
using ConfigurationSubstitution;
using Microsoft.AspNetCore.Builder;
using OpenSim.Server.Base;
using OpenSim.Framework;

namespace OpenSim.Server.RobustServer
{
    class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand();

            var logconfigOption = new Option<string>
                ( name: "--logconfig", description: "Instruct log4net to use this file as configuration file.",
                getDefaultValue: () => "OpenSim.Server.RobustServer.dll.config");
            var backgroundOption = new Option<bool>
                ( name: "--background", description: "If true, OpenSimulator will run in the background",
                getDefaultValue: () => false);
            var inifileOption = new Option<List<string>>
                ( name: "--inifile", description: "Specify the location of zero or more .ini file(s) to read.");
            var inimasterOption = new Option<string>
                ( name: "--inimaster", description: "The path to the master ini file.",
                getDefaultValue: () => "OpenSimDefaults.ini");
            var inidirectoryOption = new Option<string>
                ( name: "--inidirectory", 
                description:    "The path to folder for config ini files.OpenSimulator will read all of *.ini files " +
                                "in this directory and override OpenSim.ini settings",
                getDefaultValue: () => "config");

            rootCommand.AddGlobalOption(logconfigOption);
            rootCommand.AddGlobalOption(backgroundOption);
            rootCommand.AddGlobalOption(inifileOption);
            rootCommand.AddGlobalOption(inimasterOption);
            rootCommand.AddGlobalOption(inidirectoryOption);

            rootCommand.SetHandler((logconfig, background, inifile, inimaster, inidirectory) =>
                {
                    StartRobust(args, logconfig, background, inifile, inimaster, inidirectory);
                },
                logconfigOption,
                backgroundOption,
                inifileOption,
                inimasterOption,
                inidirectoryOption);

            return await rootCommand.InvokeAsync(args);
        }
        
        private static void Register(
            IComponentRegistryBuilder builder, 
            string directoryPath, 
            string searchPattern = "*.dll"
        )
        {
            if (Directory.Exists(directoryPath) is false)
                return;
        
            // Load assemblies from the directory
            var assemblies = Directory.GetFiles(directoryPath, searchPattern)
                .Select(Assembly.LoadFrom)
                .ToArray();
        
            // Register services from the modules in the assemblies
            foreach (var assembly in assemblies)
            {
                var moduleTypes = assembly.GetTypes()
                    .Where(t => typeof(IModule).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
                    .ToList();

                foreach (var moduleType in moduleTypes)
                {
                    var moduleInstance = (IModule)Activator.CreateInstance(moduleType);
                    moduleInstance?.Configure(builder);
                }
            }
        }
        
        private static void StartRobust(
            string[] args, 
            string logconfig, 
            bool background, 
            List<string> inifile, 
            string inimaster,
            string inidirectory
            )
        {
            var builder = WebApplication.CreateBuilder(args);
            
            builder.Configuration.AddIniFile(inimaster, optional: true, reloadOnChange: true);
            
            foreach (var item in inifile)
            {
                builder.Configuration.AddIniFile(item, optional: true, reloadOnChange: true);
            }

            if (string.IsNullOrEmpty(inidirectory) is false)
            {
                if (Directory.Exists(inidirectory))
                {
                    foreach (var item in Directory.GetFiles(inidirectory, "*.ini"))
                    {
                        builder.Configuration.AddIniFile(item, optional: true, reloadOnChange: true);
                    }
                }
            }
                
            builder.Configuration.EnableSubstitutions("$(", ")");
            
            builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory())
                .ConfigureContainer<ContainerBuilder>(registryBuilder =>
                {
                    // The registry we're building into
                    var registry = registryBuilder.ComponentRegistryBuilder;
                    
                    // Search the Service Runtime directory First
                    var directoryPath = AppDomain.CurrentDomain.BaseDirectory;
                    Register(registry, directoryPath, "OpenSim.*.dll");
                        
                    // Register any plugins dropped into the addons directory also
                    directoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addon-modules");
                    Register(registry, directoryPath);    
                }); 
            
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();
            
            builder.Services.AddHostedService<RobustService>();
            builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
            builder.Services.BuildServiceProvider();
         
            builder.Services.AddControllers();
            builder.Services.AddOpenApi();
            builder.Services.AddEndpointsApiExplorer();
            
            builder.Services.AddHostedService<PIDFileService>();
            builder.Services.AddHostedService<TimerService>();
            builder.Services.AddHostedService<RobustService>();
            
            var app = builder.Build();
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseHttpsRedirection();
            app.UseAuthorization();
            app.MapControllers();
            
            LogManager.LoggerFactory = app.Services.GetService<ILoggerFactory>();
            
            app.Run();
        }
    }
}