/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *	 * Redistributions of source code must retain the above copyright
 *	   notice, this list of conditions and the following disclaimer.
 *	 * Redistributions in binary form must reproduce the above copyright
 *	   notice, this list of conditions and the following disclaimer in the
 *	   documentation and/or other materials provided with the distribution.
 *	 * Neither the name of the OpenSim Project nor the
 *	   names of its contributors may be used to endorse or promote products
 *	   derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Xml;
using Nini.Config;
using OpenSim.Framework;

/// <summary>
/// OpenSim Server GridServer
/// </summary>
namespace OpenSim.Server.GridServer;

/// <summary>
/// class Grid Server Config Source
/// </summary>
public class GridServerConfigSource
{
    private readonly string m_configDirectory = ".";

    /// <summary>
    /// Ini Config Source
    /// </summary>
    public IniConfigSource m_config;

    /// <summary>
    /// Loads the master ini plus any explicitly requested ini files and ini
    /// directory, expands any <c>Include-*</c> directives, merges operating-system
    /// environment variables and resolves <c>${...}</c> key references. This produces
    /// the same fully merged configuration the legacy <c>ServicesServerBase</c>
    /// constructor used to build, and mirrors the set of sources Program.cs feeds to
    /// the host <c>IConfiguration</c> (<c>--inimaster</c>, <c>--inifile</c>,
    /// <c>--inidirectory</c>).
    /// </summary>
    public GridServerConfigSource(string iniMaster = "", IEnumerable<string> iniFiles = null, string iniDirectory = null)
    {
        m_config = new IniConfigSource();

        // Base sources, in precedence order: master first, then explicit ini files,
        // then every ini file in the ini directory (each overrides the previous).
        List<string> sources = new();

        if (string.IsNullOrEmpty(iniMaster))
            iniMaster = "OpenSim.Server.GridServer.ini";

        LoadSource(iniMaster, sources);

        if (iniFiles is not null)
        {
            foreach (string iniFile in iniFiles)
                LoadSource(iniFile, sources);
        }

        if (!string.IsNullOrEmpty(iniDirectory) && Directory.Exists(iniDirectory))
        {
            foreach (string iniFile in Directory.GetFiles(iniDirectory, "*.ini"))
                LoadSource(iniFile, sources);
        }

        // Expand any Include-* directives referenced by the loaded sources. Only the
        // newly added include files are merged on each pass.
        int sourceIndex = sources.Count;

        while (AddIncludes(m_config, sources))
        {
            for (; sourceIndex < sources.Count; ++sourceIndex)
            {
                m_config.Merge(ReadConfigSource(sources[sourceIndex]));
            }
        }

        // Merge OpSys env vars
        Util.MergeEnvironmentToConfig(m_config);

        m_config.ReplaceKeyValues();
    }

    /// <summary>
    /// Loads a single ini source if it exists and records it so its includes can be
    /// expanded and to avoid loading the same file twice.
    /// </summary>
    private void LoadSource(string iniFile, List<string> sources)
    {
        if (string.IsNullOrEmpty(iniFile) || sources.Contains(iniFile) || !File.Exists(iniFile))
            return;

        m_config.Merge(ReadConfigSource(iniFile));
        sources.Add(iniFile);
    }

    /// <summary>
    /// Save config
    /// </summary>
    public void Save(string path)
    {
        m_config.Save(path);
    }

    /// <summary>
    /// Adds any files referenced by <c>Include-*</c> keys to the list of sources.
    /// </summary>
    private bool AddIncludes(IConfigSource configSource, List<string> sources)
    {
        bool sourcesAdded = false;

        foreach (IConfig config in configSource.Configs)
        {
            string[] keys = config.GetKeys();
            foreach (string k in keys)
            {
                if (k.StartsWith("Include-"))
                {
                    string file = config.GetString(k);
                    if (IsUri(file))
                    {
                        if (!sources.Contains(file))
                        {
                            sourcesAdded = true;
                            sources.Add(file);
                        }
                    }
                    else
                    {
                        string basepath = Path.GetFullPath(m_configDirectory);
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
                            Console.WriteLine($"[CONFIG]: Could not find include file {path}");
                        }
                        else
                        {
                            foreach (string p in paths)
                            {
                                if (!sources.Contains(p))
                                {
                                    sourcesAdded = true;
                                    sources.Add(p);
                                }
                            }
                        }
                    }
                }
            }
        }

        return sourcesAdded;
    }

    /// <summary>
    /// Check if we can convert the string to a URI.
    /// </summary>
    private static bool IsUri(string file)
    {
        return Uri.TryCreate(file, UriKind.Absolute, out Uri configUri)
            && (configUri.Scheme == Uri.UriSchemeHttp || configUri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Reads a config source from a local file or a remote URI.
    /// </summary>
    private static IConfigSource ReadConfigSource(string iniFile)
    {
        if (Uri.TryCreate(iniFile, UriKind.Absolute, out Uri configUri) &&
            (configUri.Scheme == Uri.UriSchemeHttp || configUri.Scheme == Uri.UriSchemeHttps))
        {
            XmlReader r = XmlReader.Create(iniFile);
            return new XmlConfigSource(r);
        }

        return new IniConfigSource(iniFile);
    }
}
