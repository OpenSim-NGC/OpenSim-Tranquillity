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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using log4net;

namespace OpenSim.Framework
{
    /// <summary>
    /// Plugin loader using DotNetCorePlugins discovery.
    /// Replaces Mono.Addins-based loading with folder scanning.
    /// </summary>
    public class DotNetCorePluginLoader<T> : IDisposable where T : class, IPlugin
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IPluginDiscovery m_discovery;
        private readonly List<T> m_loadedPlugins = new List<T>();
        private readonly PluginInitialiserBase m_initialiser;
        private bool m_disposed = false;

        /// <summary>
        /// Loaded plugin instances
        /// </summary>
        public IReadOnlyList<T> LoadedPlugins => m_loadedPlugins.AsReadOnly();

        /// <summary>
        /// Create a new loader with custom discovery backend
        /// </summary>
        public DotNetCorePluginLoader(IPluginDiscovery discovery, PluginInitialiserBase initialiser = null)
        {
            m_discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
            m_initialiser = initialiser ?? new PluginInitialiserBase();
        }

        /// <summary>
        /// Load plugins for an extension point using the discovery backend.
        /// Instantiates all discovered plugin types and initializes them.
        /// </summary>
        public void Load(string extensionPoint, Type typeHint = null)
        {
            ThrowIfDisposed();

            if (typeHint == null)
            {
                m_log.WarnFormat("[PLUGIN-LOADER]: Load called for {0} without type hint", extensionPoint);
                return;
            }

            m_log.InfoFormat("[PLUGIN-LOADER]: Loading extension point {0}", extensionPoint);

            try
            {
                // Use discovery backend to find plugins
                IReadOnlyList<PluginExtensionNode> nodes = m_discovery.GetExtensionNodes(extensionPoint, typeHint);

                if (nodes.Count == 0)
                {
                    m_log.InfoFormat("[PLUGIN-LOADER]: No plugins found for {0}", extensionPoint);
                    return;
                }

                m_log.InfoFormat("[PLUGIN-LOADER]: Found {0} candidates for {1}", nodes.Count, extensionPoint);

                // Instantiate plugins
                var instantiated = new List<T>();
                foreach (var node in nodes)
                {
                    try
                    {
                        m_log.DebugFormat("[PLUGIN-LOADER]: Instantiating plugin {0}", node.TypeName);
                        object instance = node.CreateInstance();
                        
                        if (instance is T typedInstance)
                        {
                            instantiated.Add(typedInstance);
                            m_log.DebugFormat("[PLUGIN-LOADER]: Successfully instantiated {0}", node.TypeName);
                        }
                        else
                        {
                            m_log.WarnFormat("[PLUGIN-LOADER]: Plugin {0} does not implement {1}",
                                node.TypeName, typeof(T).Name);
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[PLUGIN-LOADER]: Failed to instantiate {0}: {1}",
                            node.TypeName, e.Message);
                    }
                }

                // Initialize plugins (separate phase for dependencies)
                foreach (var plugin in instantiated)
                {
                    try
                    {
                        m_log.DebugFormat("[PLUGIN-LOADER]: Initializing plugin {0}", plugin.GetType().Name);
                        m_initialiser.Initialise(plugin);
                        m_loadedPlugins.Add(plugin);
                        m_log.DebugFormat("[PLUGIN-LOADER]: Successfully initialized {0}", plugin.GetType().Name);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[PLUGIN-LOADER]: Failed to initialize {0}: {1}",
                            plugin.GetType().Name, e.Message);
                    }
                }

                m_log.InfoFormat("[PLUGIN-LOADER]: Loaded {0} plugins for {1}", m_loadedPlugins.Count, extensionPoint);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[PLUGIN-LOADER]: Exception loading {0}: {1}", extensionPoint, e.Message);
            }
        }

        /// <summary>
        /// Load plugins using a registry to find types
        /// </summary>
        public void LoadFromRegistry(PluginRegistry registry, string extensionPoint, Type typeHint = null)
        {
            ThrowIfDisposed();

            if (registry == null)
                throw new ArgumentNullException(nameof(registry));

            if (typeHint == null)
            {
                m_log.WarnFormat("[PLUGIN-LOADER]: LoadFromRegistry called for {0} without type hint", extensionPoint);
                return;
            }

            m_log.InfoFormat("[PLUGIN-LOADER]: Loading from registry for {0}", extensionPoint);

            var plugins = registry.GetPlugins(extensionPoint);
            if (plugins.Count == 0)
            {
                m_log.InfoFormat("[PLUGIN-LOADER]: No plugins registered for {0}", extensionPoint);
                return;
            }

            m_log.InfoFormat("[PLUGIN-LOADER]: Found {0} registered plugins for {1}", plugins.Count, extensionPoint);

            // Instantiate and initialize registered plugins
            foreach (var descriptor in plugins)
            {
                try
                {
                    if (!typeHint.IsAssignableFrom(descriptor.PluginType))
                    {
                        m_log.WarnFormat("[PLUGIN-LOADER]: Plugin {0} does not implement {1}",
                            descriptor.PluginType.Name, typeHint.Name);
                        continue;
                    }

                    m_log.DebugFormat("[PLUGIN-LOADER]: Instantiating registered plugin {0}", descriptor.PluginType.Name);
                    var instance = (T)Activator.CreateInstance(descriptor.PluginType, true);

                    m_log.DebugFormat("[PLUGIN-LOADER]: Initializing plugin {0}", descriptor.PluginType.Name);
                    m_initialiser.Initialise(instance);
                    m_loadedPlugins.Add(instance);

                    m_log.InfoFormat("[PLUGIN-LOADER]: Successfully loaded registered plugin {0} ({1})",
                        descriptor.Name, descriptor.PluginType.Name);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[PLUGIN-LOADER]: Failed to load registered plugin {0}: {1}",
                        descriptor.PluginType.Name, e.Message);
                }
            }

            m_log.InfoFormat("[PLUGIN-LOADER]: Loaded {0} plugins from registry for {1}", 
                m_loadedPlugins.Count, extensionPoint);
        }

        /// <summary>
        /// Clear loaded plugins and optionally unload discovery resources
        /// </summary>
        public void Dispose()
        {
            if (m_disposed)
                return;

            m_loadedPlugins.Clear();
            
            if (m_discovery != null)
            {
                m_discovery.Dispose();
            }

            m_disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (m_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }
    }

    /// <summary>
    /// Static factory for creating plugin loaders with appropriate backend
    /// </summary>
    public static class DotNetCorePluginLoaderFactory
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Create a plugin loader using the configured discovery backend
        /// </summary>
        public static DotNetCorePluginLoader<T> Create<T>(
            string configuredBackend = null,
            PluginInitialiserBase initialiser = null) where T : class, IPlugin
        {
            var discovery = PluginDiscoveryFactory.Create(m_log, configuredBackend);
            return new DotNetCorePluginLoader<T>(discovery, initialiser);
        }

        /// <summary>
        /// Create a plugin loader with explicit discovery instance
        /// </summary>
        public static DotNetCorePluginLoader<T> Create<T>(
            IPluginDiscovery discovery,
            PluginInitialiserBase initialiser = null) where T : class, IPlugin
        {
            return new DotNetCorePluginLoader<T>(discovery, initialiser);
        }
    }
}
