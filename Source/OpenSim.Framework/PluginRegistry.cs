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
using System.Text.Json;
using Nini.Config;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>
/// Implemented by assemblies that provide explicit plugin registrations
/// without relying on Mono.Addins XML manifests.
/// </summary>
public interface IPluginRegistryProvider
{
    void RegisterPlugins(PluginRegistry registry);
}

/// <summary>
/// Descriptor for a plugin that can be registered for an extension point.
/// Replaces Mono.Addins XML manifest functionality.
/// </summary>
public class PluginDescriptor
{
    /// <summary>
    /// Unique identifier for this plugin
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Type implementing the plugin interface
    /// </summary>
    public Type PluginType { get; set; }

    /// <summary>
    /// Display name for the plugin
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Optional version identifier
    /// </summary>
    public string Version { get; set; }

    /// <summary>
    /// Optional description
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Optional enabled flag (default: true)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional priority (higher loads first; default: 0)
    /// </summary>
    public int Priority { get; set; } = 0;

    public PluginDescriptor() { }

    public PluginDescriptor(string id, Type pluginType, string name = null, string version = null)
    {
        Id = id;
        PluginType = pluginType;
        Name = name ?? pluginType.Name;
        Version = version ?? "1.0";
    }
}

/// <summary>
/// Registry mapping extension points to plugin descriptors.
/// Replaces .addin.xml file-based registration.
/// </summary>
public class PluginRegistry
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private readonly Dictionary<string, List<PluginDescriptor>> m_registry = 
        new Dictionary<string, List<PluginDescriptor>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a plugin for an extension point
    /// </summary>
    public void Register(string extensionPath, PluginDescriptor descriptor)
    {
        if (descriptor?.PluginType == null)
            throw new ArgumentNullException(nameof(descriptor));

        if (!m_registry.ContainsKey(extensionPath))
            m_registry[extensionPath] = new List<PluginDescriptor>();

        m_registry[extensionPath].Add(descriptor);
        m_log.LogDebug("[PLUGIN-REGISTRY]: Registered {0} for {1}", descriptor.Id, extensionPath);
    }

    /// <summary>
    /// Register multiple plugins for an extension point
    /// </summary>
    public void RegisterAll(string extensionPath, params PluginDescriptor[] descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            Register(extensionPath, descriptor);
        }
    }

    /// <summary>
    /// Get all registered plugins for an extension point
    /// </summary>
    public IReadOnlyList<PluginDescriptor> GetPlugins(string extensionPath)
    {
        if (!m_registry.TryGetValue(extensionPath, out var plugins))
            return new List<PluginDescriptor>();

        return plugins
            .Where(p => p.Enabled)
            .OrderByDescending(p => p.Priority)
            .ToList();
    }

    /// <summary>
    /// Get plugin types for an extension point
    /// </summary>
    public IReadOnlyList<Type> GetPluginTypes(string extensionPath)
    {
        return GetPlugins(extensionPath)
            .Select(p => p.PluginType)
            .ToList();
    }

    /// <summary>
    /// Check if an extension point has registered plugins
    /// </summary>
    public bool HasPlugins(string extensionPath)
    {
        return GetPlugins(extensionPath).Count > 0;
    }

    /// <summary>
    /// Get count of registered plugins for an extension point
    /// </summary>
    public int GetPluginCount(string extensionPath)
    {
        return GetPlugins(extensionPath).Count;
    }

    /// <summary>
    /// Load plugin registrations from INI configuration
    /// </summary>
    public static PluginRegistry FromIniConfig(IConfigSource config, ILogger log = null)
    {
        var registry = new PluginRegistry();
        var logger = log ?? m_log;

        var pluginConfig = config.Configs["PluginRegistry"];
        if (pluginConfig == null)
        {
            logger.LogWarning("[PLUGIN-REGISTRY]: No [PluginRegistry] section in config");
            return registry;
        }

        // Read extension point registrations from config
        // Format example:
        // [PluginRegistry]
        // /OpenSim/RegionModules = OpenSim.Region.CoreModules.dll, OpenSim.Addons.Groups.dll
        // /OpenSim/WindModule = OpenSim.Region.CoreModules.dll

        foreach (var key in pluginConfig.GetKeys())
        {
            if (!key.StartsWith("/"))
                continue; // Skip non-extension-point keys

            string extensionPath = key;
            string assemblyList = pluginConfig.GetString(key, string.Empty);

            if (string.IsNullOrWhiteSpace(assemblyList))
            {
                logger.LogWarning("[PLUGIN-REGISTRY]: Empty assembly list for {0}", extensionPath);
                continue;
            }

            var assemblies = assemblyList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim());

            foreach (var assemblyName in assemblies)
            {
                try
                {
                    // This will be handled by plugin loader discovery
                    logger.LogDebug("[PLUGIN-REGISTRY]: Noted assembly {0} for {1}", assemblyName, extensionPath);
                }
                catch (Exception e)
                {
                    logger.LogWarning("[PLUGIN-REGISTRY]: Failed to process assembly {0} for {1}: {2}",
                        assemblyName, extensionPath, e.Message);
                }
            }
        }

        return registry;
    }

    /// <summary>
    /// Load plugin registrations from JSON file
    /// Format:
    /// {
    ///   "extensionPoints": [
    ///     {
    ///       "path": "/OpenSim/RegionModules",
    ///       "plugins": [
    ///         { "id": "CoreModules", "type": "OpenSim.Region.CoreModules.Scripting.ScriptEngine" },
    ///         { "id": "GroupsModule", "type": "OpenSim.Groups.GroupsModule" }
    ///       ]
    ///     }
    ///   ]
    /// }
    /// </summary>
    public static PluginRegistry FromJsonFile(string jsonPath, ILogger log = null)
    {
        var registry = new PluginRegistry();
        var logger = log ?? m_log;

        if (!File.Exists(jsonPath))
        {
            logger.LogWarning("[PLUGIN-REGISTRY]: JSON config file not found: {0}", jsonPath);
            return registry;
        }

        try
        {
            string json = File.ReadAllText(jsonPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var config = JsonSerializer.Deserialize<PluginRegistryConfig>(json, options);

            if (config?.ExtensionPoints == null)
            {
                logger.LogWarning("[PLUGIN-REGISTRY]: Invalid JSON structure in " + jsonPath);
                return registry;
            }

            foreach (var extPoint in config.ExtensionPoints)
            {
                if (string.IsNullOrEmpty(extPoint.Path))
                    continue;

                foreach (var plugin in extPoint.Plugins ?? new List<PluginRegistryEntry>())
                {
                    if (string.IsNullOrEmpty(plugin.Type))
                        continue;

                    try
                    {
                        var type = Type.GetType(plugin.Type, false);
                        if (type != null)
                        {
                            var descriptor = new PluginDescriptor
                            {
                                Id = plugin.Id ?? plugin.Type.Split('.').Last(),
                                PluginType = type,
                                Name = plugin.Name ?? type.Name,
                                Version = plugin.Version ?? "1.0",
                                Description = plugin.Description,
                                Enabled = plugin.Enabled ?? true,
                                Priority = plugin.Priority ?? 0
                            };
                            registry.Register(extPoint.Path, descriptor);
                        }
                        else
                        {
                            logger.LogWarning("[PLUGIN-REGISTRY]: Type not found: {0}", plugin.Type);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning("[PLUGIN-REGISTRY]: Failed to register {0}: {1}",
                            plugin.Type, e.Message);
                    }
                }
            }

            logger.LogInformation("[PLUGIN-REGISTRY]: Loaded {0} extension points from {1}",
                config.ExtensionPoints.Count, jsonPath);
        }
        catch (Exception e)
        {
            logger.LogError("[PLUGIN-REGISTRY]: Error loading JSON from {0}: {1}", jsonPath, e.Message);
        }

        return registry;
    }

    /// <summary>
    /// Build a registry from in-assembly code providers.
    /// </summary>
    public static PluginRegistry FromProviders(IEnumerable<Assembly> assemblies, ILogger log = null)
    {
        var registry = new PluginRegistry();
        var logger = log ?? m_log;

        if (assemblies == null)
            return registry;

        foreach (Assembly assembly in assemblies.Where(a => a != null).Distinct())
        {
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (type == null || type.IsAbstract || type.IsInterface)
                    continue;

                if (!typeof(IPluginRegistryProvider).IsAssignableFrom(type))
                    continue;

                try
                {
                    var provider = (IPluginRegistryProvider)Activator.CreateInstance(type, true);
                    provider.RegisterPlugins(registry);
                    logger.LogDebug("[PLUGIN-REGISTRY]: Loaded provider {0} from {1}",
                        type.FullName, assembly.GetName().Name);
                }
                catch (Exception e)
                {
                    logger.LogWarning("[PLUGIN-REGISTRY]: Failed to run provider {0} from {1}: {2}",
                        type.FullName,
                        assembly.GetName().Name,
                        e.Message);
                }
            }
        }

        return registry;
    }

    /// <summary>
    /// Merge another registry into this one
    /// </summary>
    public void MergeWith(PluginRegistry other)
    {
        if (other == null)
            return;

        foreach (var kvp in other.m_registry)
        {
            if (!m_registry.ContainsKey(kvp.Key))
                m_registry[kvp.Key] = new List<PluginDescriptor>();

            m_registry[kvp.Key].AddRange(kvp.Value);
        }
    }

    /// <summary>
    /// Get all registered extension points
    /// </summary>
    public IReadOnlyList<string> GetExtensionPoints()
    {
        return m_registry.Keys.ToList();
    }

    /// <summary>
    /// Clear all registrations
    /// </summary>
    public void Clear()
    {
        m_registry.Clear();
    }

    // JSON deserialization helper classes
    private class PluginRegistryConfig
    {
        public List<PluginExtensionPointConfig> ExtensionPoints { get; set; } = new List<PluginExtensionPointConfig>();
    }

    private class PluginExtensionPointConfig
    {
        public string Path { get; set; }
        public List<PluginRegistryEntry> Plugins { get; set; } = new List<PluginRegistryEntry>();
    }

    private class PluginRegistryEntry
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public bool? Enabled { get; set; }
        public int? Priority { get; set; }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException rtle)
        {
            return rtle.Types.Where(t => t != null);
        }
    }
}
