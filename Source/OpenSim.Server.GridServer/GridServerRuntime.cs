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

using Nini.Config;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Server.Base;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.GridServer;

/// <summary>
/// Default <see cref="IGridServerRuntime"/> implementation. Owns the GridServer
/// HTTP listener bootstrap, service connector activation and plugin loader setup.
/// </summary>
public sealed class GridServerRuntime : IGridServerRuntime
{
    private readonly ILogger<GridServerRuntime> _logger;
    private readonly IServerBase _serverBase;
    private readonly IStartupFailureCoordinator _startupFailureCoordinator;
    private readonly IMainServerAccessor _mainServerAccessor;
    private readonly IRuntimeMonitoringController _runtimeMonitoringController;
    private readonly IServiceConnectorLoader _connectorLoader;
    private readonly IGridHttpServerFactory _httpServerFactory;
    private readonly IGridCertificateProvisioner _certificateProvisioner;

    private readonly object _initializeLock = new();
    private bool _initialized;

    private PluginLoader _loader;
    private readonly List<IServiceConnector> _serviceConnectors = new();

    private bool _noVerifyCertChain;
    private bool _noVerifyCertHostname;

    public GridServerRuntime(
        ILogger<GridServerRuntime> logger,
        IServerBase serverBase,
        IStartupFailureCoordinator startupFailureCoordinator,
        IMainServerAccessor mainServerAccessor,
        IRuntimeMonitoringController runtimeMonitoringController,
        IServiceConnectorLoader connectorLoader,
        IGridHttpServerFactory httpServerFactory,
        IGridCertificateProvisioner certificateProvisioner)
    {
        _logger = logger;
        _serverBase = serverBase;
        _startupFailureCoordinator = startupFailureCoordinator;
        _mainServerAccessor = mainServerAccessor;
        _runtimeMonitoringController = runtimeMonitoringController;
        _connectorLoader = connectorLoader;
        _httpServerFactory = httpServerFactory;
        _certificateProvisioner = certificateProvisioner;
    }

    /// <inheritdoc/>
    public void Initialize()
    {
        if (_initialized)
            return;

        lock (_initializeLock)
        {
            if (_initialized)
                return;

            // Transitional sync with legacy static console access.
            MainConsole.Instance = _serverBase.Console;

            // The fully merged configuration is loaded by GridServerConfigSource and
            // shared through IServerBase.Config; the HTTP listeners are created by a
            // dedicated DI service instead of the legacy HttpServerBase.
            IConfigSource config = _serverBase.Config;

            IConfig serverConfig = config.Configs["Startup"];
            if (serverConfig == null)
                _startupFailureCoordinator.ThrowFatal("Startup config section missing in .ini file");

            _certificateProvisioner.Provision(serverConfig);

            int dnsTimeout = serverConfig.GetInt("DnsTimeout", 30000);
            try { ServicePointManager.DnsRefreshTimeout = dnsTimeout; } catch { }

            _noVerifyCertChain = serverConfig.GetBoolean("NoVerifyCertChain", _noVerifyCertChain);
            _noVerifyCertHostname = serverConfig.GetBoolean("NoVerifyCertHostname", _noVerifyCertHostname);

            WebUtil.SetupHTTPClients(_noVerifyCertChain, _noVerifyCertHostname, null, 32);

            string registryLocation = serverConfig.GetString("RegistryLocation", ".");

            // Create and start the HTTP listeners before the connectors are loaded,
            // since connectors bind to those servers.
            _httpServerFactory.CreateAndStart(config, _serverBase.Console);

            _serviceConnectors.AddRange(_connectorLoader.LoadConnectors(config));

            PrintFileToConsole("robuststartuplogo.txt");

            _loader = new PluginLoader(config, registryLocation);

            _initialized = true;
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _runtimeMonitoringController.DisableWatchdog();
        _mainServerAccessor.Stop();

        Thread.Sleep(500);
        _runtimeMonitoringController.StopWorkManager();

        _serverBase.Shutdown();
    }

    public bool ValidateServerCertificate(
        object sender,
        X509Certificate certificate,
        X509Chain chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (_noVerifyCertChain)
            sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateChainErrors;

        if (_noVerifyCertHostname)
            sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;

        return sslPolicyErrors == SslPolicyErrors.None;
    }

    /// <summary>
    /// Opens a file and uses it as input to the console command parser.
    /// </summary>
    /// <param name="fileName">name of file to use as input to the console</param>
    private void PrintFileToConsole(string fileName)
    {
        if (File.Exists(fileName))
        {
            using StreamReader readFile = File.OpenText(fileName);
            string currentLine;
            while ((currentLine = readFile.ReadLine()) is not null)
            {
                _logger.LogInformation("[!]" + currentLine);
            }
        }
    }
}
