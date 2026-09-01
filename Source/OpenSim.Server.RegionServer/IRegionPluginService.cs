/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Owns the region application-plugin lifecycle: discovery/loading via the Mono.Addins
/// plugin loader, post-initialisation and disposal. Extracted from
/// <see cref="OpenSimBase"/> so the plugin lifecycle can be composed and tested
/// independently of the startup inheritance chain.
/// </summary>
public interface IRegionPluginService
{
    /// <summary>
    /// Loads the <c>/OpenSim/Startup</c> application plugins for <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The application instance passed to each plugin's initialiser.</param>
    /// <param name="startupConfig">The <c>[Startup]</c> config section (may be null).</param>
    /// <returns>The loaded plugins.</returns>
    List<IApplicationPlugin> Load(IOpenSimBase app, IConfig startupConfig);

    /// <summary>
    /// Calls <see cref="IApplicationPlugin.PostInitialise"/> on each plugin.
    /// </summary>
    void PostInitialise(IEnumerable<IApplicationPlugin> plugins);

    /// <summary>
    /// Disposes each plugin.
    /// </summary>
    void Dispose(IEnumerable<IApplicationPlugin> plugins);
}
