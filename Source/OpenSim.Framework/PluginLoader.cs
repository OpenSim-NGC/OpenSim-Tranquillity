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
using log4net;

namespace OpenSim.Framework;

/// <summary>
/// Exception thrown if an incorrect number of plugins are loaded
/// </summary>
public class PluginConstraintViolatedException : Exception
{
    public PluginConstraintViolatedException() : base() { }
    public PluginConstraintViolatedException(string msg) : base(msg) { }
    public PluginConstraintViolatedException(string msg, Exception e) : base(msg, e) { }
}

/// <summary>
/// Classes wishing to impose constraints on plugin loading must implement
/// this class and pass it to PluginLoader AddConstraint()
/// </summary>
public interface IPluginConstraint
{
    string Message { get; }
    bool Apply(string extpoint, Type pluginTypeHint, IPluginDiscovery discovery);
}

/// <summary>
/// Classes wishing to select specific plugins from a range of possible options
/// must implement this class and pass it to PluginLoader Load()
/// </summary>
public interface IPluginFilter
{
    bool Apply(PluginExtensionNode plugin);
}

/// <summary>
/// Generic Plugin Loader
/// </summary>
public class PluginLoader<T> : IDisposable where T : IPlugin
{
    private const int max_loadable_plugins = 10000;

    private List<T> loaded = new List<T>();
    private List<string> extpoints = new List<string>();
    private PluginInitialiserBase initialiser;
    private readonly IPluginDiscovery discovery;

    private Dictionary<string, IPluginConstraint> constraints
        = new Dictionary<string, IPluginConstraint>();

    private Dictionary<string, IPluginFilter> filters
        = new Dictionary<string, IPluginFilter>();

    private static readonly ILog log
        = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public PluginInitialiserBase Initialiser
    {
        set { initialiser = value; }
        get { return initialiser; }
    }

    public List<T> Plugins
    {
        get { return loaded; }
    }

    public T Plugin
    {
        get { return (loaded.Count == 1) ? loaded[0] : default(T); }
    }

    public PluginLoader()
    {
        Initialiser = new PluginInitialiserBase();
        discovery = PluginDiscoveryFactory.Create(log);
        initialise_plugin_dir(".");
    }

    public PluginLoader(PluginInitialiserBase init)
    {
        Initialiser = init;
        discovery = PluginDiscoveryFactory.Create(log);
        initialise_plugin_dir(".");
    }

    public PluginLoader(PluginInitialiserBase init, string dir)
    {
        Initialiser = init;
        discovery = PluginDiscoveryFactory.Create(log);
        initialise_plugin_dir(dir);
    }

    public PluginLoader(PluginInitialiserBase init, string dir, IPluginDiscovery pluginDiscovery)
    {
        Initialiser = init;
        discovery = pluginDiscovery ?? throw new ArgumentNullException(nameof(pluginDiscovery));
        initialise_plugin_dir(dir);
    }

    public void Add(string extpoint)
    {
        if (extpoints.Contains(extpoint))
            return;

        extpoints.Add(extpoint);
    }

    public void Add(string extpoint, IPluginConstraint cons)
    {
        Add(extpoint);
        AddConstraint(extpoint, cons);
    }

    public void Add(string extpoint, IPluginFilter filter)
    {
        Add(extpoint);
        AddFilter(extpoint, filter);
    }

    public void AddConstraint(string extpoint, IPluginConstraint cons)
    {
        constraints.Add(extpoint, cons);
    }

    public void AddFilter(string extpoint, IPluginFilter filter)
    {
        filters.Add(extpoint, filter);
    }

    public void Load(string extpoint)
    {
        Add(extpoint);
        Load();
    }

    public void Load()
    {
        foreach (string ext in extpoints)
        {
            log.Info("[PLUGINS]: Loading extension point " + ext);

            IReadOnlyList<PluginExtensionNode> discoveredNodes = discovery.GetExtensionNodes(ext, typeof(T));
            log.InfoFormat(
                "[PLUGINS]: Extension point {0} discovered {1} candidate plugin(s) using {2}",
                ext,
                discoveredNodes.Count,
                discovery.GetType().Name);

            if (constraints.TryGetValue(ext , out IPluginConstraint cons))
            {
                if (cons.Apply(ext, typeof(T), discovery))
                    log.Error("[PLUGINS]: " + ext + " failed constraint: " + cons.Message);
            }

            filters.TryGetValue(ext, out IPluginFilter filter);

            List<T> loadedPlugins = new List<T>();
            foreach (PluginExtensionNode node in discoveredNodes)
            {
                log.Info("[PLUGINS]: Trying plugin " + node.Path);

                if ((filter != null) && (filter.Apply(node) == false))
                    continue;

                T plugin = (T)node.CreateInstance();
                loadedPlugins.Add(plugin);
            }

            // We do Initialise() in a second loop after CreateInstance
            // So that modules who need init before others can do it
            // Example: Script Engine Component System needs to load its components before RegionLoader starts
            foreach (T plugin in loadedPlugins)
            {
                Initialiser.Initialise(plugin);
                Plugins.Add(plugin);
            }
        }
    }

    /// <summary>
    /// Unregisters Mono.Addins event handlers, allowing temporary Mono.Addins
    /// data to be garbage collected. Since the plugins created by this loader
    /// are meant to outlive the loader itself, they must be disposed separately
    /// </summary>
    public void Dispose()
    {
        discovery.Dispose();
    }

    private void initialise_plugin_dir(string dir)
    {
        log.Info("[PLUGINS]: Initializing addin manager");
        discovery.Initialize(dir);
    }
}

public class PluginExtensionNode
{
    private readonly Func<object> m_factory;

    public string ID { get; }
    public string Provider { get; }
    public string Path { get; }
    public Type TypeObject { get; }
    public string TypeName => TypeObject?.FullName ?? string.Empty;

    public Type Type => TypeObject;

    public PluginExtensionNode(string id, string provider, string path, Type typeObject, Func<object> factory)
    {
        ID = id ?? string.Empty;
        Provider = provider ?? string.Empty;
        Path = path ?? string.Empty;
        TypeObject = typeObject;
        m_factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public object CreateInstance()
    {
        if (TypeObject == null)
            throw new InvalidOperationException("Type object not specified.");

        return m_factory();
    }
}

/// <summary>
/// Constraint that bounds the number of plugins to be loaded.
/// </summary>
public class PluginCountConstraint : IPluginConstraint
{
    private int min;
    private int max;

    public PluginCountConstraint(int exact)
    {
        min = exact;
        max = exact;
    }

    public PluginCountConstraint(int minimum, int maximum)
    {
        min = minimum;
        max = maximum;
    }

    public string Message
    {
        get
        {
            return "The number of plugins is constrained to the interval ["
                + min + ", " + max + "]";
        }
    }

    public bool Apply(string extpoint, Type pluginTypeHint, IPluginDiscovery discovery)
    {
        int count = discovery.GetExtensionNodeCount(extpoint, pluginTypeHint);

        if ((count < min) || (count > max))
            throw new PluginConstraintViolatedException(Message);

        return true;
    }
}

/// <summary>
/// Filters out which plugin to load based on the plugin name or names given.  Plugin names are contained in
/// their addin.xml
/// </summary>
public class PluginProviderFilter : IPluginFilter
{
    private string[] m_filters;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="p">
    /// Plugin name or names on which to filter.  Multiple names should be separated by commas.
    /// </param>
    public PluginProviderFilter(string p)
    {
        m_filters = p.Split(',');

        for (int i = 0; i < m_filters.Length; i++)
        {
            m_filters[i] = m_filters[i].Trim();
        }
    }

    /// <summary>
    /// Apply this filter to the given plugin.
    /// </summary>
    /// <param name="plugin"></param>
    /// <returns>true if the plugin's name matched one of the filters, false otherwise.</returns>
    public bool Apply(PluginExtensionNode plugin)
    {
        for (int i = 0; i < m_filters.Length; i++)
        {
            if (m_filters[i] == plugin.Provider)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Filters plugins according to their ID. Plugin IDs are contained in their addin.xml
/// </summary>
public class PluginIdFilter : IPluginFilter
{
    private string[] m_filters;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="p">
    /// Plugin ID or IDs on which to filter. Multiple names should be separated by commas.
    /// </param>
    public PluginIdFilter(string p)
    {
        m_filters = p.Split(',');

        for (int i = 0; i < m_filters.Length; i++)
        {
            m_filters[i] = m_filters[i].Trim();
        }
    }

    /// <summary>
    /// Apply this filter to <paramref name="plugin" />.
    /// </summary>
    /// <param name="plugin">PluginExtensionNode instance to check whether it passes the filter.</param>
    /// <returns>true if the plugin's ID matches one of the filters, false otherwise.</returns>
    public bool Apply(PluginExtensionNode plugin)
    {
        for (int i = 0; i < m_filters.Length; i++)
        {
            if (m_filters[i] == plugin.ID)
            {
                return true;
            }
        }

        return false;
    }
}
