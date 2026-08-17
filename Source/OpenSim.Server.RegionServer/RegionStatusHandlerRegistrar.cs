/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers.HttpServer;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Default <see cref="IRegionStatusHandlerRegistrar"/> implementation. Contains the
/// status/stats handler registration that previously lived inline in
/// <see cref="OpenSim.StartupSpecific"/>; behavior is preserved verbatim and merely
/// composed out of the inheritance chain.
/// </summary>
public sealed class RegionStatusHandlerRegistrar : IRegionStatusHandlerRegistrar
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public void Register(IHttpServer defaultServer, OpenSimBase app)
    {
        defaultServer.AddSimpleStreamHandler(new OpenSimBase.SimStatusHandler());
        defaultServer.AddSimpleStreamHandler(new OpenSimBase.XSimStatusHandler(app));
        if (app.userStatsURI != string.Empty)
            defaultServer.AddSimpleStreamHandler(new OpenSimBase.UXSimStatusHandler(app));
        defaultServer.AddSimpleStreamHandler(new OpenSimBase.SimRobotsHandler());
        defaultServer.AddSimpleStreamHandler(new IndexPHPHandler(defaultServer));

        if (!string.IsNullOrEmpty(app.managedStatsURI))
        {
            string urlBase = $"/{app.managedStatsURI}/";
            StatsManager.StatsPassword = app.managedStatsPassword;
            defaultServer.AddHTTPHandler(urlBase, StatsManager.HandleStatsRequest);
            m_log.LogInformation("[OPENSIM] Enabling remote managed stats fetch. URL = {0}", urlBase);
        }
    }
}
