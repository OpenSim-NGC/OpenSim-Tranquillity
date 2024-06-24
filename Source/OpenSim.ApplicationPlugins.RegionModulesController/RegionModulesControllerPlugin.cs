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
 *     * Neither the name of the OpenSimulator Project nor the
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

using Autofac;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework;


namespace OpenSim.ApplicationPlugins.RegionModulesController
{
    public class RegionModulesControllerPlugin : IApplicationPlugin, IRegionModulesController
    {
        private readonly IServiceProvider m_serviceProvider;
        private readonly IConfiguration m_configuration;
        private readonly ILogger<RegionModulesControllerPlugin> m_logger;

        // Config access
        private IOpenSimBase m_openSim;

        // Base Directory for plugins
        private string m_pluginBaseDir = string.Empty;

        // Internal lists to collect information about modules present
        private List<Type> m_nonSharedModules = new List<Type>();
        private List<Type> m_sharedModules = new List<Type>();

        // List of shared module instances, for adding to Scenes
        private List<ISharedRegionModule> m_sharedInstances = new List<ISharedRegionModule>();

        // List of non shared module instances, for adding to Scenes
        private List<INonSharedRegionModule> m_nonSharedInstances = new List<INonSharedRegionModule>();

        public RegionModulesControllerPlugin(
            IServiceProvider serviceProvider,
            IConfiguration configuration, 
            ILogger<RegionModulesControllerPlugin> logger)
        {
            this.m_serviceProvider = serviceProvider;
            this.m_configuration = configuration;
            this.m_logger = logger;
        }

        #region IDisposable implementation

        // Cleanup
        //
        public void Dispose()
        {
            // We expect that all regions have been removed already
            while (m_sharedInstances.Count > 0)
            {
                m_sharedInstances[0].Close();
                m_sharedInstances.RemoveAt(0);
            }

            m_sharedModules.Clear();
            m_nonSharedModules.Clear();
        }

        #endregion

        public string Version
        {
            get
            {
                return "0.9.3.0";
            }
        }

        public string Name
        {
            get
            {
                return "RegionModulesControllerPlugin";
            }
        }

        #region IApplicationPlugin implementation

        public void Initialise(IOpenSimBase openSim)
        {
            m_logger.LogInformation($"Initializing...");

            m_openSim = openSim;
            m_openSim.ApplicationRegistry.RegisterInterface<IRegionModulesController>(this);

            // The [Modules] section in the ini file
            var modulesConfig = m_configuration.GetSection("Modules");

            // Who we are
            string id = this.Name;

            using (var scope = m_serviceProvider.CreateScope())
            {
                m_logger.LogInformation($"[REGIONMODULES]: Initializing ISharedRegionModules");

                var m_sharedModules = scope.ServiceProvider.GetServices<ISharedRegionModule>();

                foreach (var module in m_sharedModules)
                {
                    // Read the config again
                    string moduleString = modulesConfig.GetValue("Setup_" + module.Name, String.Empty);

                    if (moduleString == "disabled")
                        continue;

                    // OK, we're up and running
                    m_sharedInstances.Add(module);

                    m_logger.LogInformation($"[REGIONMODULES]: Initializing: {module.Name}");                    
                    module.Initialise(m_configuration);
                }

                m_logger.LogInformation($"[REGIONMODULES]: Initializing INonSharedRegionModules");
 
                var m_nonSharedModules = scope.ServiceProvider.GetServices<INonSharedRegionModule>();

                foreach (var module in m_nonSharedModules)
                {
                    // Read the config again
                    var moduleString = modulesConfig.GetValue("Setup_" + module.Name, String.Empty);
                    if (moduleString == "disabled")
                        continue;

                    // OK, we're up and running
                    m_nonSharedInstances.Add(module);

                    m_logger.LogInformation($"[REGIONMODULES]: Initializing: {module.Name}");
                    module.Initialise(m_configuration);
                }
            }
        }

        /// <summary>
        /// SearchModules - Given a string (possibly with wildcards) search for modules and pick out plugins that 
        /// should be loaded and initialized.
        /// </summary>
        /// <param name="modulesConfig">Region Modules Configuration</param>
        /// <param name="loadContext">The module loader to use when resolving assemblies</param>
        /// <param name="dir">The directory to search for target dlls</param>
        /// <param name="match">String to match (possibly with wildcard so 1:M</param>
        private void SearchModules(
            IConfigurationSection modulesConfig,
            AssemblyLoadContext loadContext,
            DirectoryInfo dir,
            string match
            )
        {
            m_logger.LogDebug($"[REGIONMODULES]: Looking for RegionModules in directory: {dir.FullName} that match {match}");

            foreach (var fileInfo in dir.GetFiles(match))
            {
                try
                {
                    Assembly pluginAssembly = loadContext.LoadFromAssemblyPath(fileInfo.FullName);

                    if (pluginAssembly != null)
                    {
                        foreach (var pluginType in pluginAssembly.GetTypes().Where((p) => p.IsPublic && !p.IsAbstract))
                        {

                            if (typeof(ISharedRegionModule).IsAssignableFrom(pluginType))
                            {
                                if (CheckModuleEnabled(pluginType, modulesConfig))
                                {
                                    m_logger.LogDebug($"[REGIONMODULES]: Found shared region module {pluginType.Name}, class {pluginType}");
                                    m_sharedModules.Add(pluginType);
                                }
                                else
                                {
                                    m_logger.LogDebug($"[REGIONMODULES]: Found disabled shared region module  {pluginType.Name}, class {pluginType}");
                                }
                            }
                            else if (typeof(INonSharedRegionModule).IsAssignableFrom(pluginType))
                            {
                                if (CheckModuleEnabled(pluginType, modulesConfig))
                                {
                                    m_logger.LogDebug($"[REGIONMODULES]: Found non-shared region module {pluginType.Name}, class {pluginType}");
                                    m_nonSharedModules.Add(pluginType);
                                }
                                else
                                {
                                    m_logger.LogDebug($"[REGIONMODULES]: Found disabled non-shared region module {pluginType.Name}, class {pluginType}");
                                }
                            }
                            else
                            {
                                m_logger.LogDebug($"[REGIONMODULES]: Found unknown type of module {pluginType.Name}, class {pluginType}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    m_logger.LogError($"[REGIONMODULES]: Error loading assembly {fileInfo.FullName} : {ex}");
                    continue;
                }
            }
        }

        public void PostInitialise ()
        {
            m_logger.LogDebug("[REGIONMODULES]: PostInitializing...");

            // Immediately run PostInitialise on shared modules
            foreach (ISharedRegionModule module in m_sharedInstances)
            {
                module.PostInitialise();
            }
        }

#endregion

#region IPlugin implementation

        // We don't do that here
        //OpenSim.ApplicationPlugins.LoadRegions
        public void Initialise ()
        {
            throw new System.NotImplementedException();
        }

#endregion


#region IRegionModulesController implementation

        /// <summary>
        /// Check that the given module is no disabled in the [Modules] section of the config files.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="modulesConfig">The config section</param>
        /// <returns>true if the module is enabled, false if it is disabled</returns>
        protected bool CheckModuleEnabled(Type node, IConfigurationSection modulesConfig)
        {
            // Get the config string
            string moduleString =  modulesConfig.GetValue("Setup_" + node.Name, String.Empty);

            // We have a selector
            if (!String.IsNullOrEmpty(moduleString))
            {
                // Allow disabling modules even if they don't have
                // support for it
                if (moduleString == "disabled")
                    return false;

                // Split off port, if present
                string[] moduleParts = moduleString.Split(new char[] { '/' }, 2);

                // Format is [port/][class]
                string className = moduleParts[0];
                if (moduleParts.Length > 1)
                    className = moduleParts[1];

                // Match the class name if given
                if (!(String.IsNullOrEmpty(className) || node.Name == className))
                    return false;
            }

            return true;
        }

        // The root of all evil.
        // This is where we handle adding the modules to scenes when they
        // load. This means that here we deal with replaceable interfaces,
        // nonshared modules, etc.
        //
        public void AddRegionToModules (Scene scene)
        {
            Dictionary<Type, ISharedRegionModule> deferredSharedModules =
                    new Dictionary<Type, ISharedRegionModule>();
            Dictionary<Type, INonSharedRegionModule> deferredNonSharedModules =
                    new Dictionary<Type, INonSharedRegionModule>();

            //var allHelloWorlds = container.Resolve<IEnumerable<IHelloWorld>>();
            //foreach (var helloWorld in allHelloWorlds) { log(helloWorld.Greeting); }

            // We need this to see if a module has already been loaded and
            // has defined a replaceable interface. It's a generic call,
            // so this can't be used directly. It will be used later
            Type s = scene.GetType();
            MethodInfo mi = s.GetMethod("RequestModuleInterface");

            // This will hold the shared modules we actually load
            List<ISharedRegionModule> sharedlist =
                    new List<ISharedRegionModule>();

            // Iterate over the shared modules that have been loaded
            // Add them to the new Scene
            foreach (ISharedRegionModule module in m_sharedInstances)
            {
                // Here is where we check if a replaceable interface
                // is defined. If it is, the module is checked against
                // the interfaces already defined. If the interface is
                // defined, we simply skip the module. Else, if the module
                // defines a replaceable interface, we add it to the deferred
                // list.
                Type replaceableInterface = module.ReplaceableInterface;
                if (replaceableInterface != null)
                {
                    MethodInfo mii = mi.MakeGenericMethod(replaceableInterface);

                    if (mii.Invoke(scene, new object[0]) != null)
                    {
                        m_logger.LogDebug("[REGIONMODULE]: Not loading {0} because another module has registered {1}", module.Name, replaceableInterface.ToString());
                        continue;
                    }

                    deferredSharedModules[replaceableInterface] = module;
                    m_logger.LogDebug("[REGIONMODULE]: Deferred load of {0}", module.Name);
                    continue;
                }

                m_logger.LogDebug("[REGIONMODULE]: Adding scene {0} to shared module {1}",
                                  scene.RegionInfo.RegionName, module.Name);

                module.AddRegion(scene);
                scene.AddRegionModule(module.Name, module);

                sharedlist.Add(module);
            }

            var modulesConfig = m_configuration.GetSection("Modules");

            // Scan for, and load, nonshared modules
            List<INonSharedRegionModule> list = new List<INonSharedRegionModule>();
            foreach (Type node in m_nonSharedModules)
            {
                Object[] ctorArgs = new Object[] {0};

                // Read the config
                string moduleString = modulesConfig.GetValue("Setup_" + node.Name, string.Empty);

                // We may not want to load this at all
                if (moduleString == "disabled")
                    continue;

                // Get the port number, if there is one
                if (!string.IsNullOrEmpty(moduleString))
                {
                    // Get the port number from the string
                    string[] moduleParts = moduleString.Split(new char[] {'/'}, 2);
                    if (moduleParts.Length > 1)
                        ctorArgs[0] = Convert.ToUInt32(moduleParts[0]);
                }

                // Actually load it
                INonSharedRegionModule module = null;

                Type[] ctorParamTypes = new Type[ctorArgs.Length];
                for (int i = 0; i < ctorParamTypes.Length; i++)
                    ctorParamTypes[i] = ctorArgs[i].GetType();

                if (node.GetConstructor(ctorParamTypes) != null)
                    module = (INonSharedRegionModule)Activator.CreateInstance(node, ctorArgs);
                else
                    module = (INonSharedRegionModule)Activator.CreateInstance(node);

                // Check for replaceable interfaces
                Type replaceableInterface = module.ReplaceableInterface;
                if (replaceableInterface != null)
                {
                    MethodInfo mii = mi.MakeGenericMethod(replaceableInterface);

                    if (mii.Invoke(scene, new object[0]) != null)
                    {
                        m_logger.LogDebug("[REGIONMODULE]: Not loading {0} because another module has registered {1}", module.Name, replaceableInterface.ToString());
                        continue;
                    }

                    deferredNonSharedModules[replaceableInterface] = module;
                    m_logger.LogDebug("[REGIONMODULE]: Deferred load of {0}", module.Name);
                    continue;
                }

                m_logger.LogDebug("[REGIONMODULE]: Adding scene {0} to non-shared module {1}",
                                  scene.RegionInfo.RegionName, module.Name);

                // Initialise the module
                module.Initialise(m_configuration);

                list.Add(module);
            }

            // Now add the modules that we found to the scene. If a module
            // wishes to override a replaceable interface, it needs to
            // register it in Initialise, so that the deferred module
            // won't load.
            foreach (INonSharedRegionModule module in list)
            {
                module.AddRegion(scene);
                scene.AddRegionModule(module.Name, module);
            }

            // Now all modules without a replaceable base interface are loaded
            // Replaceable modules have either been skipped, or omitted.
            // Now scan the deferred modules here
            foreach (ISharedRegionModule module in deferredSharedModules.Values)
            {
                // Determine if the interface has been replaced
                Type replaceableInterface = module.ReplaceableInterface;
                MethodInfo mii = mi.MakeGenericMethod(replaceableInterface);

                if (mii.Invoke(scene, new object[0]) != null)
                {
                    m_logger.LogDebug("[REGIONMODULE]: Not loading {0} because another module has registered {1}", module.Name, replaceableInterface.ToString());
                    continue;
                }

                m_logger.LogDebug("[REGIONMODULE]: Adding scene {0} to shared module {1} (deferred)",
                                  scene.RegionInfo.RegionName, module.Name);

                // Not replaced, load the module
                module.AddRegion(scene);
                scene.AddRegionModule(module.Name, module);

                sharedlist.Add(module);
            }

            // Same thing for nonshared modules, load them unless overridden
            List<INonSharedRegionModule> deferredlist =
                    new List<INonSharedRegionModule>();

            foreach (INonSharedRegionModule module in deferredNonSharedModules.Values)
            {
                // Check interface override
                Type replaceableInterface = module.ReplaceableInterface;
                if (replaceableInterface != null)
                {
                    MethodInfo mii = mi.MakeGenericMethod(replaceableInterface);

                    if (mii.Invoke(scene, new object[0]) != null)
                    {
                        m_logger.LogDebug("[REGIONMODULE]: Not loading {0} because another module has registered {1}", module.Name, replaceableInterface.ToString());
                        continue;
                    }
                }

                m_logger.LogDebug("[REGIONMODULE]: Adding scene {0} to non-shared module {1} (deferred)",
                                  scene.RegionInfo.RegionName, module.Name);

                module.Initialise(m_configuration);

                list.Add(module);
                deferredlist.Add(module);
            }

            // Finally, load valid deferred modules
            foreach (INonSharedRegionModule module in deferredlist)
            {
                module.AddRegion(scene);
                scene.AddRegionModule(module.Name, module);
            }

            // This is needed for all module types. Modules will register
            // Interfaces with scene in AddScene, and will also need a means
            // to access interfaces registered by other modules. Without
            // this extra method, a module attempting to use another modules's
            // interface would be successful only depending on load order,
            // which can't be depended upon, or modules would need to resort
            // to ugly kludges to attempt to request interfaces when needed
            // and unneccessary caching logic repeated in all modules.
            // The extra function stub is just that much cleaner
            //
            foreach (ISharedRegionModule module in sharedlist)
            {
                module.RegionLoaded(scene);
            }

            foreach (INonSharedRegionModule module in list)
            {
                module.RegionLoaded(scene);
            }

            scene.AllModulesLoaded();
        }

        public void RemoveRegionFromModules (Scene scene)
        {
            foreach (IRegionModuleBase module in scene.RegionModules.Values)
            {
                m_logger.LogDebug("[REGIONMODULE]: Removing scene {0} from module {1}",
                                  scene.RegionInfo.RegionName, module.Name);
                module.RemoveRegion(scene);
                if (module is INonSharedRegionModule)
                {
                    // as we were the only user, this instance has to die
                    module.Close();
                }
            }
            scene.RegionModules.Clear();
        }

#endregion

    }
}
