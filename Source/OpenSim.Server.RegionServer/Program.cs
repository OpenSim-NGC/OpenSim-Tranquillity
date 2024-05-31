/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

using Autofac.Extensions.DependencyInjection;
using Autofac;

using OpenSim.ApplicationPlugins.LoadRegions;
using OpenSim.ApplicationPlugins.RegionModulesController;
using OpenSim.ApplicationPlugins.RemoteController;
using OpenSim.Groups;
using OpenSim.Region.OptionalModules;
using OpenSim.Region.CoreModules;
using OpenSim.Region.Framework;
using OpenSim.Region.PhysicsModule.BasicPhysics;
using OpenSim.Region.ClientStack.LindenUDP;
using OpenSim.Region.ClientStack.Linden;
using OpenSim.Region.PhysicsModule.Meshing;
using OpenSim.Region.PhysicsModule.POS;
using OpenSim.Region.PhysicsModule.BulletS;
using OpenSim.Region.PhysicsModule.ubOde;
using OpenSim.Region.PhysicsModule.ubOdeMeshing;
using OpenSim.Region.ScriptEngine.Yengine;
using OpenSim.Server.Common;

using ConfigurationSubstitution;

namespace OpenSim.Server.RegionServer
{
    class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand();

            var logconfigOption = new Option<string>
                (name: "--logconfig", description: "Instruct log4net to use this file as configuration file.",
                getDefaultValue: () => "OpenSim.exe.config");
            var backgroundOption = new Option<bool>
                (name: "--background", description: "If true, OpenSimulator will run in the background",
                getDefaultValue: () => false);
            var inifileOption = new Option<List<string>>
                (name: "--inifile", description: "Specify the location of zero or more .ini file(s) to read.");
            var inimasterOption = new Option<string>
                (name: "--inimaster", description: "The path to the master ini file.",
                getDefaultValue: () => "OpenSimDefaults.ini");
            var inidirectoryOption = new Option<string>(
                    name: "--inidirectory", 
                    description:    "The path to folder for config ini files.OpenSimulator will read all of *.ini files " +
                                    "in this directory and override OpenSim.ini settings",
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
                StartRegion(args, logconfig, background, inifile, inimaster, inidirectory, console);
            },
            logconfigOption,
            backgroundOption, 
            inifileOption, 
            inimasterOption, 
            inidirectoryOption, 
            consoleOption);

            return await rootCommand.InvokeAsync(args);
        }

        private static void StartRegion(
            string[] args, 
            string logconfig, 
            bool background, 
            List<string> inifile, 
            string inimaster,
            string inidirectory,
            string console
            )
        {
            IHostBuilder builder = Host.CreateDefaultBuilder()
                .UseServiceProviderFactory(new AutofacServiceProviderFactory())
                .ConfigureAppConfiguration(configuration =>
                {
                    configuration.AddIniFile(inimaster, optional: true, reloadOnChange: true);
                    foreach (var item in inifile)
                    {
                        configuration.AddIniFile(item, optional: true, reloadOnChange: true);
                    }

                    if (string.IsNullOrEmpty(inidirectory) is false)
                    {
                        foreach (var item in Directory.GetFiles(inidirectory, "*.ini"))
                        {
                            configuration.AddIniFile(item, optional: true, reloadOnChange: true);
                        }
                    }
                    configuration.EnableSubstitutions("$(", ")");
                })
                .ConfigureServices(services =>
                {
                    services.AddHostedService<RegionService>();
                    services.AddHostedService<PidFileService>();
                })
                .ConfigureContainer<ContainerBuilder>(builder =>
                {
                    // Register the main configuration
                    builder.Register(x => Application.Configuration)
                        .As<IConfiguration>()
                        .SingleInstance();

                    // Startup Application Plugins
                    builder.RegisterType<RegionModulesControllerPlugin>()
                        .As<IApplicationPlugin>()
                        .SingleInstance();

                    builder.RegisterType<LoadRegionsPlugin>().
                        As<IApplicationPlugin>()
                        .SingleInstance();

                    builder.RegisterType<RemoteAdminPlugin>()
                        .As<IApplicationPlugin>()
                        .SingleInstance();

                    // Data Services
                    builder.RegisterModule(new SimulationDataServiceModule());
                    builder.RegisterModule(new EstateDataServiceModule());

                    // Register Region Modules
                    builder.RegisterModule(new CoreModulesModule());
                    builder.RegisterModule(new OptionalModulesModule());
                    builder.RegisterModule(new LindenUDPModule());
                    builder.RegisterModule(new LindenCapsModule());
                    builder.RegisterModule(new BasicPhysicsModule());
                    builder.RegisterModule(new POSModule());
                    builder.RegisterModule(new BulletSModule());
                    builder.RegisterModule(new ubOdePhysicsModule());
                    builder.RegisterModule(new MeshingModule());
                    builder.RegisterModule(new ubOdePhysicsMeshingModule());
                    builder.RegisterModule(new YEngineModule());
                    builder.RegisterModule(new OpenSimSearchModule());
                    builder.RegisterModule(new GroupsAddonModule());
                    builder.RegisterModule(new GloebitModule());
                });

            IHost host = builder.Build();

            XmlConfigurator.Configure();
            Application.Configure(args);

            host.Run();
        }
    }
}
