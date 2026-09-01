/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Creates, starts and registers the region's HTTP listeners (the main listener,
/// the optional HTTPS listener and the optional "OOB" server) with
/// <see cref="OpenSim.Framework.Servers.MainServer"/>.
/// </summary>
public interface IRegionHttpServerFactory
{
    /// <summary>
    /// Builds and starts the HTTP listeners described by <paramref name="serversInfo"/>,
    /// registering each with the shared <c>MainServer</c> instance.
    /// </summary>
    /// <param name="serversInfo">Network configuration for the region's listeners.</param>
    /// <returns>The primary <see cref="BaseHttpServer"/> for the region.</returns>
    BaseHttpServer CreateAndStart(NetworkServersInfo serversInfo);
}
