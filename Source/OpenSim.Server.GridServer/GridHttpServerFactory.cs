/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Default <see cref="IGridHttpServerFactory"/> implementation. Contains the
/// listener-creation logic that previously lived inline in
/// <c>HttpServerBase.ReadConfig</c>/<c>Initialise</c>; the behaviour is preserved
/// and merely composed out of the inheritance chain. Configuration errors are
/// surfaced as exceptions instead of calling <c>Environment.Exit</c>.
/// </summary>
public sealed class GridHttpServerFactory : IGridHttpServerFactory
{
    private readonly ILogger<GridHttpServerFactory> _logger;

    public GridHttpServerFactory(ILogger<GridHttpServerFactory> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void CreateAndStart(IConfigSource config, ICommandConsole console)
    {
        ArgumentNullException.ThrowIfNull(config);

        IConfig networkConfig = config.Configs["Network"];

        if (networkConfig == null)
            throw new InvalidOperationException("Section [Network] not found, server can't start");

        uint port = (uint)networkConfig.GetInt("port", 0);

        if (port == 0)
            throw new InvalidOperationException("No 'port' entry found in [Network].  Server can't start");

        bool ssl_main = networkConfig.GetBoolean("https_main", false);
        bool ssl_listener = networkConfig.GetBoolean("https_listener", false);
        bool ssl_external = networkConfig.GetBoolean("https_external", false);

        uint consolePort = (uint)networkConfig.GetInt("ConsolePort", 0);

        BaseHttpServer httpServer;

        //
        // Make the base server according to the port, etc.
        // Then, check for https settings and add a server to MainServer.
        //
        if (!ssl_main)
        {
            httpServer = new BaseHttpServer(port);
        }
        else
        {
            string cert_path = networkConfig.GetString("cert_path", string.Empty);
            if (cert_path.Length == 0)
                throw new InvalidOperationException("Path to X509 certificate is missing, server can't start.");

            string cert_pass = networkConfig.GetString("cert_pass", string.Empty);
            if (cert_pass.Length == 0)
                throw new InvalidOperationException("Password for X509 certificate is missing, server can't start.");

            httpServer = new BaseHttpServer(port, ssl_main, cert_path, cert_pass);
        }

        MainServer.Instance.AddHttpServer(httpServer);

        // If https_listener = true, then add an ssl listener on the https_port...
        if (ssl_listener == true)
        {
            uint https_port = (uint)networkConfig.GetInt("https_port", 0);

            _logger.LogWarning("[SSL]: External flag is {SslExternal}", ssl_external);
            if (!ssl_external)
            {
                string cert_path = networkConfig.GetString("cert_path", string.Empty);
                if (cert_path.Length == 0)
                    _logger.LogError("[SSL]: Path to X509 certificate is missing, server can't start.");

                string cert_pass = networkConfig.GetString("cert_pass", string.Empty);
                if (cert_pass.Length == 0)
                    _logger.LogError("[SSL]: Password for X509 certificate is missing, server can't start.");

                MainServer.Instance.AddHttpServer(new BaseHttpServer(https_port, ssl_listener, cert_path, cert_pass));
            }
            else
            {
                _logger.LogWarning("[SSL]: SSL port is active but no SSL is used because external SSL was requested.");
                MainServer.Instance.AddHttpServer(new BaseHttpServer(https_port));
            }
        }

        // Start every registered server and wire up the console.
        foreach (BaseHttpServer s in MainServer.Instance.Servers.Values)
            s.Start();

        MainServer.Instance.RegisterHttpConsoleCommands(console);

        MethodInfo mi = console.GetType().GetMethod(
            "SetServer", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(BaseHttpServer) }, null);

        if (mi != null)
        {
            if (consolePort == 0)
                mi.Invoke(console, new object[] { MainServer.Instance.DefaultServer });
            else
                mi.Invoke(console, new object[] { MainServer.Instance.GetHttpServer(consolePort) });
        }
    }
}
