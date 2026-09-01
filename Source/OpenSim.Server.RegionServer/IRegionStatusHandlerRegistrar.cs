/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Registers the region's simulator status and stats HTTP stream handlers
/// (simstatus, extended status, robots.txt, index.php and the optional managed
/// stats fetch endpoint) on the supplied default HTTP server.
/// </summary>
public interface IRegionStatusHandlerRegistrar
{
    /// <summary>
    /// Registers the status/stats handlers for <paramref name="app"/> on
    /// <paramref name="defaultServer"/>.
    /// </summary>
    /// <param name="defaultServer">The default HTTP server to register handlers on.</param>
    /// <param name="app">The running region application supplying status/stats data.</param>
    void Register(IHttpServer defaultServer, OpenSimBase app);
}
