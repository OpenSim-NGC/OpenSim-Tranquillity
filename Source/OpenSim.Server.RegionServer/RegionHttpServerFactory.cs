/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;

using Microsoft.Extensions.Logging;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// Default <see cref="IRegionHttpServerFactory"/> implementation. This contains the
/// listener-creation logic that previously lived inline in
/// <see cref="RegionApplicationBase.StartupSpecific"/>; the behavior is preserved
/// verbatim and merely composed out of the inheritance chain.
/// </summary>
public sealed class RegionHttpServerFactory : IRegionHttpServerFactory
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public BaseHttpServer CreateAndStart(NetworkServersInfo serversInfo)
    {
        uint mainport = serversInfo.HttpListenerPort;
        uint mainSSLport = serversInfo.httpSSLPort;

        if (serversInfo.HttpUsesSSL && (mainport == mainSSLport))
        {
            m_log.LogError("[REGION SERVER]: HTTP Server config failed.   HTTP Server and HTTPS server must be on different ports");
        }

        BaseHttpServer mainHttpServer = null;

        if (serversInfo.HttpUsesSSL)
        {
            mainHttpServer = new BaseHttpServer(
                    mainSSLport, serversInfo.HttpUsesSSL,
                    serversInfo.HttpSSLCN,
                    serversInfo.HttpSSLCertPath, serversInfo.HttpSSLCNCertPass);
            mainHttpServer.Start();
            MainServer.Instance.AddHttpServer(mainHttpServer);
        }

        // unsecure main server
        BaseHttpServer server = new BaseHttpServer(mainport);
        if (!serversInfo.HttpUsesSSL)
        {
            mainHttpServer = server;
            server.Start();
        }
        else
        {
            server.Start();
        }

        MainServer.Instance.AddHttpServer(server);

        // "OOB" Server
        if (serversInfo.ssl_listener)
        {
            if (!serversInfo.ssl_external)
            {
                server = new BaseHttpServer(
                    serversInfo.https_port, serversInfo.ssl_listener,
                    serversInfo.cert_path,
                    serversInfo.cert_pass);

                m_log.LogInformation("[REGION SERVER]: Starting OOB HTTPS server on port {0}", server.SSLPort);
                server.Start();
                MainServer.Instance.AddHttpServer(server);
            }
            else
            {
                server = new BaseHttpServer(serversInfo.https_port);

                m_log.LogInformation("[REGION SERVER]: Starting HTTP server on port {0} for external HTTPS", server.Port);
                server.Start();
                MainServer.Instance.AddHttpServer(server);
            }
        }

        return mainHttpServer;
    }
}
