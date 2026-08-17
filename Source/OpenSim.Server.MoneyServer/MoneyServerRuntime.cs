/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Timers;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.MoneyServer.Controllers;

using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace OpenSim.Server.MoneyServer;

public sealed class MoneyServerRuntime : IMoneyServerRuntime
{
    private uint _moneyServerPort = 8008;
    private int _deadTime = 120;

    private readonly IConfiguration _configuration;
    private readonly ILogger<MoneyServerRuntime> _logger;
    private readonly IServerBase _serverBase;
    private readonly IConsoleContext _consoleContext;
    private readonly IStartupFailureCoordinator _startupFailureCoordinator;
    private readonly IMainServerAccessor _mainServerAccessor;
    private readonly IRuntimeMonitoringController _runtimeMonitoringController;
    private readonly IMoneyDBService _moneyDBService;
    private readonly MoneyXmlRpcController _moneyXmlRpcController;

    private readonly object _initializeLock = new();
    private bool _initialized;
    private Timer _checkTimer;

    public BaseHttpServer HttpServer { get; private set; }

    public MoneyServerRuntime(
        IConfiguration configuration,
        ILogger<MoneyServerRuntime> logger,
        IServerBase serverBase,
        IConsoleContext consoleContext,
        IStartupFailureCoordinator startupFailureCoordinator,
        IMainServerAccessor mainServerAccessor,
        IRuntimeMonitoringController runtimeMonitoringController,
        IMoneyDBService moneyDBService,
        MoneyXmlRpcController moneyXmlRpcController)
    {
        _configuration = configuration;
        _logger = logger;
        _serverBase = serverBase;
        _consoleContext = consoleContext;
        _startupFailureCoordinator = startupFailureCoordinator;
        _mainServerAccessor = mainServerAccessor;
        _runtimeMonitoringController = runtimeMonitoringController;
        _moneyDBService = moneyDBService;
        _moneyXmlRpcController = moneyXmlRpcController;
    }

    public void Initialize()
    {
        if (_initialized)
            return;

        lock (_initializeLock)
        {
            if (_initialized)
                return;

            // Transitional sync with legacy static console access.
            MainConsole.Instance = _consoleContext.Console;

            // [Startup]
            var startupConfig = _configuration.GetSection("Startup");
            if (!startupConfig.Exists())
            {
                _logger.LogInformation("[MONEY SERVER]: [Startup] section is not found. Using default settings");
            }
            else
            {
                _deadTime = startupConfig.GetValue<int>("ExpiredTime", _deadTime);
                _moneyServerPort = startupConfig.GetValue<uint>("ServerPort", _moneyServerPort);
            }

            // [MoneyServer]
            var serverConfig = _configuration.GetSection("MoneyServer");
            if (!serverConfig.Exists())
            {
                _logger.LogInformation("[MONEY SERVER]: [MoneyServer] section is not found. Using default settings");
            }
            else
            {
                _deadTime = serverConfig.GetValue<int>("ExpiredTime", _deadTime);
                _moneyServerPort = serverConfig.GetValue<uint>("ServerPort", _moneyServerPort);
            }

            _logger.LogInformation("[MONEY SERVER]: Setup HTTP Server process");
            try
            {
                HttpServer = new BaseHttpServer(_moneyServerPort);
                HttpServer.Start();
            }
            catch (Exception e)
            {
                _startupFailureCoordinator.ThrowFatal(
                    "[MONEY SERVER]: Failed to start HTTP process. Check certificate configuration.", e);
            }

            _logger.LogInformation("[MONEY SERVER]: Connecting to Money Storage Server");
            _moneyDBService.Initialise();
            _moneyXmlRpcController.RegisterLegacyHandlers(HttpServer);

            _initialized = true;
        }
    }

    public void StartMaintenance()
    {
        if (_checkTimer is not null)
            return;

        _checkTimer = new Timer
        {
            Interval = 60 * 1000,
            Enabled = true,
        };

        _checkTimer.Elapsed += CheckTransaction;
        _checkTimer.Start();
    }

    public void Stop()
    {
        if (_checkTimer is not null)
        {
            _checkTimer.Stop();
            _checkTimer.Elapsed -= CheckTransaction;
            _checkTimer.Dispose();
            _checkTimer = null;
        }

        _runtimeMonitoringController.DisableWatchdog();
        _mainServerAccessor.Stop();

        Thread.Sleep(500);
        _runtimeMonitoringController.StopWorkManager();

        _serverBase.Shutdown();
    }

    private void CheckTransaction(object sender, ElapsedEventArgs e)
    {
        long ticksToEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        int unixEpochTime = (int)((DateTime.UtcNow.Ticks - ticksToEpoch) / 10000000);
        int deadTime = unixEpochTime - _deadTime;

        _moneyDBService.SetTransExpired(deadTime);
    }
}
