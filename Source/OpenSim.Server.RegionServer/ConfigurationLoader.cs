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

using System.Xml;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenSim.Framework;
using OpenSim.Server.Base;

namespace OpenSim.Server.RegionServer
{
    /// <summary>
    /// Loads the Configuration files into nIni
    /// </summary>
    public class ConfigurationLoader
    {
        /// <summary>
        /// A source of Configuration data
        /// </summary>
        protected IConfiguration m_config;

        /// <summary>
        /// Various Config settings the region needs to start
        /// Physics Engine, Mesh Engine, GridMode, PhysicsPrim allowed, Neighbor,
        /// StorageDLL, Storage Connection String, Estate connection String, Client Stack
        /// Standalone settings.
        /// </summary>
        public ConfigSettings ConfigSettings { get; private set; } = new ConfigSettings();

        /// <summary>
        /// Grid Service Information.  This refers to classes and addresses of the grid service
        /// </summary>
        public NetworkServersInfo NetworkServersInfo { get; set; } = new NetworkServersInfo();

        private static ILogger? m_logger;


        static ConfigurationLoader() {
            m_logger ??= OpenSimServer.Instance.ServiceProvider.GetRequiredService<ILogger<ConfigurationLoader>>();
        }
        /// <summary>
        /// Loads the region configuration
        /// </summary>
        /// <param name="argvSource">Parameters passed into the process when started</param>
        /// <param name="configSettings"></param>
        /// <param name="networkInfo"></param>
        /// <returns>A configuration that gets passed to modules</returns>
        public IConfiguration LoadConfigSettings(IConfiguration argvSource)
        {
            bool iniFileExists = false;

            IConfigurationSection startupConfig = argvSource.GetSection("Startup");

            List<string> sources = new List<string>();

            string masterFileName = startupConfig.GetValue<string>("inimaster", "OpenSimDefaults.ini");

            if (masterFileName == "none")
                masterFileName = String.Empty;

            if (IsUri(masterFileName))
            {
                if (!sources.Contains(masterFileName))
                    sources.Add(masterFileName);
            }
            else
            {
                string masterFilePath = Path.GetFullPath(
                        Path.Combine(Util.configDir(), masterFileName));

                if (masterFileName != String.Empty)
                {
                    if (File.Exists(masterFilePath))
                    {
                        if (!sources.Contains(masterFilePath))
                            sources.Add(masterFilePath);
                    }
                    else
                    {
                        m_logger?.LogError("Master ini file {0} not found", Path.GetFullPath(masterFilePath));
                        Environment.Exit(1);
                    }
                }
            }

            string iniFileName = startupConfig.GetValue<string>("inifile", "OpenSim.ini");

            if (IsUri(iniFileName))
            {
                if (!sources.Contains(iniFileName))
                    sources.Add(iniFileName);
                Application.iniFilePath = iniFileName;
            }
            else
            {
                Application.iniFilePath = Path.GetFullPath(
                    Path.Combine(Util.configDir(), iniFileName));

                if (!File.Exists(Application.iniFilePath))
                {
                    iniFileName = "OpenSim.xml";
                    Application.iniFilePath = Path.GetFullPath(Path.Combine(Util.configDir(), iniFileName));
                }

                if (File.Exists(Application.iniFilePath))
                {
                    if (!sources.Contains(Application.iniFilePath))
                        sources.Add(Application.iniFilePath);
                }
            }

            m_config = new IniConfigSource();

            m_logger?.LogInformation("[CONFIG]: Reading configuration settings");

            for (int i = 0; i < sources.Count; i++)
            {
                if (ReadConfig(m_config, sources[i]))
                {
                    iniFileExists = true;
                    AddIncludes(m_config, sources);
                }
            }

            // Override distro settings with contents of inidirectory
            string iniDirName = startupConfig.GetValue<string>("inidirectory", "config");
            string iniDirPath = Path.Combine(Util.configDir(), iniDirName);

            if (Directory.Exists(iniDirPath))
            {
                m_logger?.LogInformation("[CONFIG]: Searching folder {0} for config ini files", iniDirPath);
                List<string> overrideSources = new List<string>();

                string[] fileEntries = Directory.GetFiles(iniDirName);
                foreach (string filePath in fileEntries)
                {
                    if (Path.GetExtension(filePath).ToLower() == ".ini")
                    {
                        if (!sources.Contains(Path.GetFullPath(filePath)))
                        {
                            overrideSources.Add(Path.GetFullPath(filePath));
                            // put it in sources too, to avoid circularity
                            sources.Add(Path.GetFullPath(filePath));
                        }
                    }
                }

                if (overrideSources.Count > 0)
                {
                    IConfiguration overrideConfig = new IniConfigSource();

                    for (int i = 0; i < overrideSources.Count; i++)
                    {
                        if (ReadConfig(overrideConfig, overrideSources[i]))
                        {
                            iniFileExists = true;
                            AddIncludes(overrideConfig, overrideSources);
                        }
                    }
                    m_config.Merge(overrideConfig);
                }
            }

            if (sources.Count == 0)
            {
                m_logger?.LogCritical("[CONFIG]: Could not load any configuration");
                Environment.Exit(1);
            }
            else if (!iniFileExists)
            {
                m_logger?.LogCritical("[CONFIG]: Could not load any configuration");
                m_logger?.LogCritical("[CONFIG]: Configuration exists, but there was an error loading it!");
                Environment.Exit(1);
            }

            //// Merge OpSys env vars
            ///XXX
            //m_logger?.LogInformation("[CONFIG]: Loading environment variables for Config");
            //Util.MergeEnvironmentToConfig(m_config.Source);

            // Make sure command line options take precedence
            m_config.Merge(argvSource);

            m_config.ReplaceKeyValues();

            ReadConfigSettings();

            return m_config;
        }

        /// <summary>
        /// Read initial region settings from the ConfigSource
        /// </summary>
        protected virtual void ReadConfigSettings()
        {
            IConfigurationSection startupConfig = m_config.GetSection("Startup");
            if (startupConfig != null)
            {
                ConfigSettings.PhysicsEngine = startupConfig.GetValue<string>("physics");
                ConfigSettings.MeshEngineName = startupConfig.GetValue<string>("meshing");

                ConfigSettings.ClientstackDll
                    = startupConfig.GetValue<string>("clientstack_plugin", "OpenSim.Region.ClientStack.LindenUDP.dll");
            }

            NetworkServersInfo.loadFromConfiguration(m_config);
        }

        /// <summary>
        /// Adds the included files as ini configuration files
        /// </summary>
        /// <param name="sources">List of URL strings or filename strings</param>
        private void AddIncludes(IConfiguration configSource, List<string> sources)
        {
            /* TODO: The Includes- stuff needs to be re-implemented
            foreach (IConfig config in configSource.Configs)
            {
                // Look for Include-* in the key name
                string[] keys = config.GetKeys();
                foreach (string k in keys)
                {
                    if (k.StartsWith("Include-"))
                    {
                        // read the config file to be included.
                        string file = config.GetValue<string>(k);
                        if (IsUri(file))
                        {
                            if (!sources.Contains(file))
                                sources.Add(file);
                        }
                        else
                        {
                            string basepath = Path.GetFullPath(Util.configDir());
                            // Resolve relative paths with wildcards
                            string chunkWithoutWildcards = file;
                            string chunkWithWildcards = string.Empty;
                            int wildcardIndex = file.IndexOfAny(new char[] { '*', '?' });
                            if (wildcardIndex != -1)
                            {
                                chunkWithoutWildcards = file.Substring(0, wildcardIndex);
                                chunkWithWildcards = file.Substring(wildcardIndex);
                            }
                            string path = Path.Combine(basepath, chunkWithoutWildcards);
                            path = Path.GetFullPath(path) + chunkWithWildcards;
                            string[] paths = Util.Glob(path);

                            // If the include path contains no wildcards, then warn the user that it wasn't found.
                            if (wildcardIndex == -1 && paths.Length == 0)
                            {
                                m_logger?.LogWarning("[CONFIG]: Could not find include file {0}", path);
                            }
                            else
                            {
                                foreach (string p in paths)
                                {
                                    if (!sources.Contains(p))
                                        sources.Add(p);
                                }
                            }
                        }
                    }
                }
            }
            */
        }

        /// <summary>
        /// Check if we can convert the string to a URI
        /// </summary>
        /// <param name="file">String uri to the remote resource</param>
        /// <returns>true if we can convert the string to a Uri object</returns>
        private bool IsUri(string file)
        {
            Uri configUri;

            return Uri.TryCreate(file, UriKind.Absolute,
                    out configUri) && configUri.Scheme == Uri.UriSchemeHttp;
        }

        /// <summary>
        /// Provide same ini loader functionality for standard ini and master ini - file system or XML over http
        /// </summary>
        /// <param name="iniPath">Full path to the ini</param>
        /// <returns></returns>
        private bool ReadConfig(IConfiguration configSource, string iniPath)
        {
            bool success = false;

            if (!IsUri(iniPath))
            {
                m_logger?.LogInformation("[CONFIG]: Reading configuration file {0}", Path.GetFullPath(iniPath));

                configSource.Merge(new IniConfigSource(iniPath));
                success = true;
            }
            else
            {
                m_logger?.LogInformation("[CONFIG]: {0} is a http:// URI, fetching ...", iniPath);

                // The ini file path is a http URI
                // Try to read it
                try
                {
                    XmlReader r = XmlReader.Create(iniPath);
                    XmlConfigSource cs = new XmlConfigSource(r);
                    configSource.Merge(cs);

                    success = true;
                }
                catch (Exception e)
                {
                    m_logger?.LogCritical("[CONFIG]: Exception reading config from URI {0}\n" + e.ToString(), iniPath);
                    Environment.Exit(1);
                }
            }
            return success;
        }
    }
}