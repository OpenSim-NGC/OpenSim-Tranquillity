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

using System.Reflection;
using Nini.Config;
using log4net;

namespace OpenSim.Framework;

/// <summary>
/// Helper for Phase 2 integration: Manages plugin loading using the new
/// PluginRegistry + DotNetCorePluginLoader pattern.
/// 
/// This demonstrates how to:
/// 1. Create a PluginRegistry from configuration
/// 2. Load plugins using DotNetCorePluginLoader
/// 3. Initialize plugins with proper error handling
/// 
/// During the migration, this can coexist with Mono.Addins loading.
/// </summary>
public class PluginLoaderHelper
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>
    /// Phase 2 integration: Load plugins using registry + new loader.
    /// This is an optional/experimental path during migration.
    /// </summary>
    public static List<T> LoadPluginsUsingRegistry<T>(
        string extensionPath,
        IConfigSource config,
        PluginInitialiserBase initialiser = null) where T : class, IPlugin
    {
        var result = new List<T>();

        try
        {
            // Step 1: Create registry from configuration
            m_log.InfoFormat("[PLUGIN-HELPER]: Loading registry for {0}", extensionPath);
            var registry = PluginRegistry.FromIniConfig(config, m_log);

            // Step 2: Create plugin loader
            var loader = DotNetCorePluginLoaderFactory.Create<T>(
                initialiser: initialiser);

            // Step 3: Load plugins from registry
            loader.LoadFromRegistry(registry, extensionPath, typeof(T));

            // Step 4: Collect loaded plugins
            result.AddRange(loader.LoadedPlugins);

            m_log.InfoFormat("[PLUGIN-HELPER]: Loaded {0} plugins for {1} from registry",
                result.Count, extensionPath);
        }
        catch (Exception e)
        {
            m_log.ErrorFormat("[PLUGIN-HELPER]: Error loading plugins for {0}: {1}",
                extensionPath, e.Message);
        }

        return result;
    }

    /// <summary>
    /// Phase 2 integration: Load plugins using new loader with discovery backend.
    /// Useful for the transition period where registry may not yet be populated.
    /// </summary>
    public static List<T> LoadPluginsUsingDiscovery<T>(
        string extensionPath,
        string pluginDirectory,
        PluginInitialiserBase initialiser = null) where T : class, IPlugin
    {
        var result = new List<T>();

        try
        {
            m_log.InfoFormat("[PLUGIN-HELPER]: Loading plugins for {0} using discovery backend",
                extensionPath);

            // Create loader with the default discovery backend.
            var loader = DotNetCorePluginLoaderFactory.Create<T>(
                initialiser: initialiser);

            // Initialize discovery with plugin directory
            var discovery = loader.GetType()
                .GetProperty("m_discovery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(loader) as IPluginDiscovery;

            if (discovery != null)
            {
                discovery.Initialize(pluginDirectory ?? ".");
            }

            // Load plugins
            loader.Load(extensionPath, typeof(T));

            // Collect results
            result.AddRange(loader.LoadedPlugins);

            m_log.InfoFormat("[PLUGIN-HELPER]: Loaded {0} plugins for {1} from discovery backend",
                result.Count, extensionPath);
        }
        catch (Exception e)
        {
            m_log.ErrorFormat("[PLUGIN-HELPER]: Error loading plugins for {0}: {1}",
                extensionPath, e.Message);
        }

        return result;
    }

    /// <summary>
    /// Phase 2 integration: Hybrid approach - try registry first, fall back to discovery.
    /// This provides a smooth migration path where:
    /// 1. New code can provide explicit plugin registry
    /// 2. Legacy code that doesn't use registry still works via discovery
    /// </summary>
    public static List<T> LoadPluginsHybrid<T>(
        string extensionPath,
        IConfigSource config,
        string pluginDirectory = ".",
        PluginInitialiserBase initialiser = null) where T : class, IPlugin
    {
        var result = new List<T>();

        try
        {
            // Check if registry has plugins configured for this path
            var registry = PluginRegistry.FromIniConfig(config, m_log);
            if (registry.HasPlugins(extensionPath))
            {
                m_log.InfoFormat("[PLUGIN-HELPER]: Using registry for {0}", extensionPath);
                
                var loader = DotNetCorePluginLoaderFactory.Create<T>(
                    initialiser: initialiser);
                
                loader.LoadFromRegistry(registry, extensionPath, typeof(T));
                result.AddRange(loader.LoadedPlugins);
            }
            else
            {
                m_log.InfoFormat("[PLUGIN-HELPER]: Registry empty for {0}, using discovery", extensionPath);
                
                var loader = DotNetCorePluginLoaderFactory.Create<T>(
                    initialiser: initialiser);
                
                var discovery = loader.GetType()
                    .GetProperty("m_discovery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(loader) as IPluginDiscovery;

                if (discovery != null)
                {
                    discovery.Initialize(pluginDirectory);
                }

                loader.Load(extensionPath, typeof(T));
                result.AddRange(loader.LoadedPlugins);
            }

            m_log.InfoFormat("[PLUGIN-HELPER]: Loaded {0} plugins for {1} (hybrid mode)",
                result.Count, extensionPath);
        }
        catch (Exception e)
        {
            m_log.ErrorFormat("[PLUGIN-HELPER]: Error in hybrid loading for {0}: {1}",
                extensionPath, e.Message);
        }

        return result;
    }
}

/// <summary>
/// Phase 2 example: Extension to DotNetCorePluginLoader that provides
/// additional monitoring/debugging capabilities.
/// </summary>
public class DebugPluginLoader<T> : DotNetCorePluginLoader<T> where T : class, IPlugin
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private readonly bool m_verbose;

    public DebugPluginLoader(IPluginDiscovery discovery, PluginInitialiserBase initialiser = null, bool verbose = false)
        : base(discovery, initialiser)
    {
        m_verbose = verbose;
    }

    /// <summary>
    /// Load with detailed logging
    /// </summary>
    public void LoadVerbose(string extensionPoint, Type typeHint = null)
    {
        if (m_verbose)
        {
            m_log.InfoFormat("[DEBUG-PLUGIN-LOADER]: Starting load for {0}", extensionPoint);
            m_log.InfoFormat("[DEBUG-PLUGIN-LOADER]: Type hint: {0}", typeHint?.Name ?? "(none)");
        }

        Load(extensionPoint, typeHint);

        if (m_verbose)
        {
            m_log.InfoFormat("[DEBUG-PLUGIN-LOADER]: Load complete for {0}", extensionPoint);
            m_log.InfoFormat("[DEBUG-PLUGIN-LOADER]: Loaded {0} plugins", LoadedPlugins.Count);
            foreach (var plugin in LoadedPlugins)
            {
                m_log.InfoFormat("[DEBUG-PLUGIN-LOADER]:   - {0} ({1})",
                    plugin.GetType().Name, plugin.Name);
            }
        }
    }
}
