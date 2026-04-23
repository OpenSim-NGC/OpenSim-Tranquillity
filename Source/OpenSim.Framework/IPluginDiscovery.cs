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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using log4net;

namespace OpenSim.Framework
{
    public sealed class PluginDiscoveryCapabilities
    {
        public bool SupportsAddinRegistryMetadata { get; }

        public PluginDiscoveryCapabilities(bool supportsAddinRegistryMetadata)
        {
            SupportsAddinRegistryMetadata = supportsAddinRegistryMetadata;
        }
    }

    /// <summary>
    /// Abstraction for plugin discovery backends.
    /// </summary>
    public interface IPluginDiscovery : IDisposable
    {
        PluginDiscoveryCapabilities Capabilities { get; }
        void Initialize(string pluginDirectory);
        IReadOnlyList<PluginExtensionNode> GetExtensionNodes(string extensionPoint, Type requiredTypeHint = null);
        int GetExtensionNodeCount(string extensionPoint, Type requiredTypeHint = null);
    }

    public static class PluginDiscoveryFactory
    {
        private const string BackendOverrideVariableName = "OPENSIM_PLUGIN_DISCOVERY";

        public static IPluginDiscovery Create(ILog log, string configuredBackend = null)
        {
            string backend = configuredBackend;
            string envBackend = Environment.GetEnvironmentVariable(BackendOverrideVariableName);

            if (!string.IsNullOrWhiteSpace(envBackend))
            {
                if (!string.IsNullOrWhiteSpace(configuredBackend) &&
                    !string.Equals(configuredBackend.Trim(), envBackend.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    log.InfoFormat(
                        "[PLUGINS]: Environment override {0}={1} supersedes configured backend '{2}'",
                        BackendOverrideVariableName,
                        envBackend,
                        configuredBackend);
                }

                backend = envBackend;
            }

            if (string.IsNullOrWhiteSpace(backend))
                backend = "dotnetcore";

            switch (backend.Trim().ToLowerInvariant())
            {
                case "monoaddins":
                    log.WarnFormat(
                        "[PLUGINS]: Mono.Addins backend has been removed. Using DotNetCorePlugins discovery backend instead.");
                    return new DotNetCorePluginsDiscovery(log);

                case "dotnet":
                case "dotnetcore":
                case "reflection":
                default:
                    log.InfoFormat("[PLUGINS]: Using DotNetCorePlugins discovery backend ({0})", backend);
                    return new DotNetCorePluginsDiscovery(log);
            }
        }
    }

    public class DotNetCorePluginsDiscovery : IPluginDiscovery
    {
        private readonly ILog m_log;
        private string m_pluginDirectory = ".";
        private Type m_cachedRequiredType;
        private List<Assembly> m_assemblies = new List<Assembly>();
        private PluginRegistry m_registeredPlugins = new PluginRegistry();
        private int m_lastScannedAssemblyCount;
        private int m_lastSkippedAssemblyCount;
        private int m_lastLoadFailureCount;
        private readonly List<McMaster.NETCore.Plugins.PluginLoader> m_pluginLoaders = new List<McMaster.NETCore.Plugins.PluginLoader>();

        private static readonly string[] s_skippedAssemblyPrefixes =
        {
            "System.",
            "Microsoft.",
            "Mono.",
            "Autofac",
            "BouncyCastle",
            "BulletXNA",
            "C5",
            "CoreJ2K",
            "DotNetOpenId",
            "ICSharpCode",
            "log4net",
            "LukeSkywalker",
            "MailKit",
            "McMaster.NETCore.Plugins",
            "MimeKit",
            "MySqlConnector",
            "NDesk.Options",
            "netcd",
            "Nini",
            "Npgsql",
            "OpenMetaverse",
            "RestSharp",
            "SkiaSharp",
            "SmartThreadPool",
            "Warp3D",
            "xmlrpc"
        };

        public PluginDiscoveryCapabilities Capabilities { get; } =
            new PluginDiscoveryCapabilities(supportsAddinRegistryMetadata: false);

        public DotNetCorePluginsDiscovery(ILog log)
        {
            m_log = log;
        }

        public void Initialize(string pluginDirectory)
        {
            m_pluginDirectory = string.IsNullOrWhiteSpace(pluginDirectory) ? "." : pluginDirectory;
            m_cachedRequiredType = null;
            m_assemblies = new List<Assembly>();
            m_registeredPlugins = new PluginRegistry();
            DisposePluginLoaders();
        }

        public IReadOnlyList<PluginExtensionNode> GetExtensionNodes(string extensionPoint, Type requiredTypeHint = null)
        {
            List<PluginExtensionNode> nodes = new List<PluginExtensionNode>();
            HashSet<string> seenTypes = new HashSet<string>(StringComparer.Ordinal);

            if (requiredTypeHint == null)
            {
                m_log.WarnFormat("[PLUGINS]: DotNetCorePlugins discovery for {0} requires a plugin type hint.", extensionPoint);
                return nodes;
            }

            IReadOnlyList<PluginDescriptor> explicitRegistrations =
                m_registeredPlugins.GetPlugins(extensionPoint);

            int explicitCount = 0;
            int reflectionCount = 0;

            if (explicitRegistrations.Count > 0)
            {
                foreach (PluginDescriptor descriptor in explicitRegistrations)
                {
                    Type type = descriptor.PluginType;

                    if (type == null || type.IsAbstract || type.IsInterface)
                        continue;

                    if (!requiredTypeHint.IsAssignableFrom(type))
                        continue;

                    Assembly assembly = type.Assembly;
                    string provider = assembly.GetName().Name ?? string.Empty;
                    string path = string.IsNullOrEmpty(assembly.Location)
                        ? provider
                        : string.Format("{0}:{1}", assembly.Location, type.FullName);

                    nodes.Add(new PluginExtensionNode(
                        descriptor.Id ?? type.Name,
                        provider,
                        path,
                        type,
                        () => Activator.CreateInstance(type, true)));

                    seenTypes.Add(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
                    explicitCount++;
                }
            }

            foreach (Assembly assembly in GetAssemblies(requiredTypeHint))
            {
                foreach (Type type in GetLoadableTypes(assembly))
                {
                    if (type.IsAbstract || type.IsInterface)
                        continue;

                    if (!requiredTypeHint.IsAssignableFrom(type))
                        continue;

                    string typeKey = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
                    if (seenTypes.Contains(typeKey))
                        continue;

                    string provider = assembly.GetName().Name ?? string.Empty;
                    string path = string.IsNullOrEmpty(assembly.Location)
                        ? provider
                        : string.Format("{0}:{1}", assembly.Location, type.FullName);

                    nodes.Add(new PluginExtensionNode(
                        type.Name,
                        provider,
                        path,
                        type,
                        () => Activator.CreateInstance(type, true)));

                    seenTypes.Add(typeKey);
                    reflectionCount++;
                }
            }

            m_log.InfoFormat(
                "[PLUGINS]: Discovery summary [{0}] scanned={1}, skipped={2}, loadFailures={3}, code={4}, reflected={5}, candidates={6} using {7}",
                extensionPoint,
                m_lastScannedAssemblyCount,
                m_lastSkippedAssemblyCount,
                m_lastLoadFailureCount,
                explicitCount,
                reflectionCount,
                nodes.Count,
                nameof(DotNetCorePluginsDiscovery));

            return nodes;
        }

        public int GetExtensionNodeCount(string extensionPoint, Type requiredTypeHint = null)
        {
            return GetExtensionNodes(extensionPoint, requiredTypeHint).Count;
        }

        public void Dispose()
        {
            m_cachedRequiredType = null;
            m_assemblies.Clear();
            DisposePluginLoaders();
        }

        private IReadOnlyList<Assembly> GetAssemblies(Type requiredTypeHint)
        {
            if (m_cachedRequiredType == requiredTypeHint && m_assemblies.Count > 0)
                return m_assemblies;

            m_cachedRequiredType = requiredTypeHint;
            m_assemblies.Clear();
            m_registeredPlugins.Clear();
            m_lastScannedAssemblyCount = 0;
            m_lastSkippedAssemblyCount = 0;
            m_lastLoadFailureCount = 0;
            DisposePluginLoaders();

            if (!Directory.Exists(m_pluginDirectory))
            {
                m_log.WarnFormat("[PLUGINS]: Plugin discovery directory does not exist: {0}", m_pluginDirectory);
                return m_assemblies;
            }

            foreach (string dllPath in Directory.GetFiles(m_pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                m_lastScannedAssemblyCount++;

                if (!ShouldProbeAssembly(dllPath, requiredTypeHint))
                {
                    m_lastSkippedAssemblyCount++;
                    continue;
                }

                try
                {
                    string assemblyPath = Path.IsPathRooted(dllPath)
                        ? dllPath
                        : Path.GetFullPath(dllPath);

                    McMaster.NETCore.Plugins.PluginLoader loader =
                        McMaster.NETCore.Plugins.PluginLoader.CreateFromAssemblyFile(
                            assemblyPath,
                            sharedTypes: new[] { requiredTypeHint });

                    m_pluginLoaders.Add(loader);
                    m_assemblies.Add(loader.LoadDefaultAssembly());
                }
                catch (BadImageFormatException)
                {
                    // Ignore native or incompatible binaries.
                }
                catch (Exception e)
                {
                    m_lastLoadFailureCount++;
                    m_log.WarnFormat("[PLUGINS]: Unable to load assembly {0}: {1}", dllPath, e.Message);
                }
            }

            m_registeredPlugins = PluginRegistry.FromProviders(m_assemblies, m_log);

            return m_assemblies;
        }

        private static bool ShouldProbeAssembly(string dllPath, Type requiredTypeHint)
        {
            string assemblySimpleName = Path.GetFileNameWithoutExtension(dllPath) ?? string.Empty;

            if (requiredTypeHint != null)
            {
                if (requiredTypeHint.Name.Equals("IApplicationPlugin", StringComparison.Ordinal))
                {
                    if (assemblySimpleName.StartsWith("OpenSim.ApplicationPlugins.", StringComparison.OrdinalIgnoreCase) ||
                        assemblySimpleName.Contains(".ApplicationPlugins.", StringComparison.OrdinalIgnoreCase) ||
                        assemblySimpleName.EndsWith("ApplicationPlugin", StringComparison.OrdinalIgnoreCase) ||
                        assemblySimpleName.EndsWith("ApplicationPlugins", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return false;
                }
            }

            if (assemblySimpleName.StartsWith("OpenSim.", StringComparison.OrdinalIgnoreCase))
                return true;

            if (assemblySimpleName.StartsWith("WebRtcVoice", StringComparison.OrdinalIgnoreCase))
                return true;

            if (assemblySimpleName.EndsWith("Plugin", StringComparison.OrdinalIgnoreCase) ||
                assemblySimpleName.EndsWith("Plugins", StringComparison.OrdinalIgnoreCase) ||
                assemblySimpleName.EndsWith("Module", StringComparison.OrdinalIgnoreCase) ||
                assemblySimpleName.EndsWith("Modules", StringComparison.OrdinalIgnoreCase) ||
                assemblySimpleName.Contains(".Plugin.", StringComparison.OrdinalIgnoreCase) ||
                assemblySimpleName.Contains(".Module.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string prefix in s_skippedAssemblyPrefixes)
            {
                if (assemblySimpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Keep probing unknown assemblies so external plugin names still work by default.
            return true;
        }

        private void DisposePluginLoaders()
        {
            foreach (McMaster.NETCore.Plugins.PluginLoader loader in m_pluginLoaders)
            {
                loader.Dispose();
            }

            m_pluginLoaders.Clear();
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

    // Compatibility alias for existing backend name references in docs/config.
    public sealed class ReflectionPluginDiscovery : DotNetCorePluginsDiscovery
    {
        public ReflectionPluginDiscovery(ILog log)
            : base(log)
        {
        }
    }
}
