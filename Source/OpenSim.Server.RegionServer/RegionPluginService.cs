/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Default <see cref="IRegionPluginService"/> implementation. Contains the plugin
/// load/post-initialise/dispose logic that previously lived inline in
/// <see cref="OpenSimBase"/>; behavior is preserved verbatim and merely composed out
/// of the inheritance chain.
/// </summary>
public sealed class RegionPluginService : IRegionPluginService
{
    public List<IApplicationPlugin> Load(IOpenSimBase app, IConfig startupConfig)
    {
        string registryLocation = (startupConfig != null) ? startupConfig.GetString("RegistryLocation", String.Empty) : String.Empty;

        // The location can also be specified in the environment. If there
        // is no location in the configuration, we must call the constructor
        // without a location parameter to allow that to happen.
        if (registryLocation.Length == 0)
        {
            using (PluginLoader<IApplicationPlugin> loader = new PluginLoader<IApplicationPlugin>(new ApplicationPluginInitialiser(app)))
            {
                loader.Load("/OpenSim/Startup");
                return loader.Plugins;
            }
        }
        else
        {
            using (PluginLoader<IApplicationPlugin> loader = new PluginLoader<IApplicationPlugin>(new ApplicationPluginInitialiser(app), registryLocation))
            {
                loader.Load("/OpenSim/Startup");
                return loader.Plugins;
            }
        }
    }

    public void PostInitialise(IEnumerable<IApplicationPlugin> plugins)
    {
        foreach (IApplicationPlugin plugin in plugins)
            plugin.PostInitialise();
    }

    public void Dispose(IEnumerable<IApplicationPlugin> plugins)
    {
        foreach (IApplicationPlugin plugin in plugins)
            plugin.Dispose();
    }
}
