/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Default <see cref="IServiceConnectorLoader"/> implementation that mirrors the
/// legacy GridServer connector activation logic.
/// </summary>
public sealed class GridServiceConnectorLoader : IServiceConnectorLoader
{
    private readonly ILogger<GridServiceConnectorLoader> _logger;
    private readonly IMainServerAccessor _mainServerAccessor;

    public GridServiceConnectorLoader(
        ILogger<GridServiceConnectorLoader> logger,
        IMainServerAccessor mainServerAccessor)
    {
        _logger = logger;
        _mainServerAccessor = mainServerAccessor;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IServiceConnector> LoadConnectors(IConfigSource config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var connectors = new List<IServiceConnector>();

        IConfig serverConfig = config.Configs["Startup"];
        if (serverConfig == null)
            throw new InvalidOperationException("Startup config section missing in .ini file");

        string connList = serverConfig.GetString("ServiceConnectors", string.Empty);

        IConfig servicesConfig = config.Configs["ServiceList"];
        if (servicesConfig != null)
        {
            List<string> servicesList = new();
            if (!string.IsNullOrEmpty(connList))
                servicesList.Add(connList);

            foreach (string k in servicesConfig.GetKeys())
            {
                string v = servicesConfig.GetString(k);
                if (!string.IsNullOrEmpty(v))
                    servicesList.Add(v);
            }

            connList = string.Join(",", servicesList.ToArray());
        }

        string[] conns = connList.Split(new char[] { ',', ' ', '\n', '\r', '\t' });

        foreach (string c in conns)
        {
            if (string.IsNullOrEmpty(c))
                continue;

            string configName = string.Empty;
            string conn = c;
            uint port = 0;

            string[] split1 = conn.Split(new char[] { '/' });
            if (split1.Length > 1)
            {
                conn = split1[1];

                string[] split2 = split1[0].Split(new char[] { '@' });
                if (split2.Length > 1)
                {
                    configName = split2[0];
                    port = Convert.ToUInt32(split2[1]);
                }
                else
                {
                    port = Convert.ToUInt32(split1[0]);
                }
            }

            string[] parts = conn.Split(new char[] { ':' });
            string friendlyName = parts[0];
            if (parts.Length > 1)
                friendlyName = parts[1];

            IHttpServer server;

            if (port != 0)
                server = _mainServerAccessor.GetHttpServer(port);
            else
                server = _mainServerAccessor.DefaultServer;

            if (friendlyName == "LLLoginServiceInConnector")
                server.AddSimpleStreamHandler(new IndexPHPHandler(server));

            _logger.LogInformation("[SERVER]: Loading {FriendlyName} on port {Port}", friendlyName, server.Port);

            object[] modargs = new object[] { config, server, configName };
            IServiceConnector connector = ServerUtils.LoadPlugin<IServiceConnector>(conn, modargs);

            if (connector == null)
            {
                modargs = new object[] { config, server };
                connector = ServerUtils.LoadPlugin<IServiceConnector>(conn, modargs);
            }

            if (connector != null)
            {
                connectors.Add(connector);
                _logger.LogInformation("[SERVER]: {FriendlyName} loaded successfully", friendlyName);
            }
            else
            {
                _logger.LogError("[SERVER]: Failed to load {Conn}", conn);
            }
        }

        return connectors;
    }
}
