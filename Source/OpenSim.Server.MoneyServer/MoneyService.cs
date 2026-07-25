/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *	 * Redistributions of source code must retain the above copyright
 *	   notice, this list of conditions and the following disclaimer.
 *	 * Redistributions in binary form must reproduce the above copyright
 *	   notice, this list of conditions and the following disclaimer in the
 *	   documentation and/or other materials provided with the distribution.
 *	 * Neither the name of the OpenSim Project nor the
 *	   names of its contributors may be used to endorse or promote products
 *	   derived from this software without specific prior written permission.
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
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenMetaverse;
using System.Text;

using Timer = System.Timers.Timer;
using OpenSim.Framework.Monitoring;
using System.Runtime.InteropServices;
using OpenSim.Server.MoneyServer.Controllers;
using OpenSim.Server.MoneyServer.Models;

/// <summary>
/// OpenSim Server MoneyServer
/// </summary>
namespace OpenSim.Server.MoneyServer;

/// <summary>
/// class MoneyServerBase : BaseOpenSimServer, IMoneyServiceCore
/// Manni internal class
/// </summary>
public class MoneyService : IMoneyServiceCore, IHostedService
{
    private uint m_moneyServerPort = 8008;         // 8008 is default server port

    private int DEAD_TIME = 120;

    /// <summary>
    /// Random uuid for private data
    /// </summary>
    protected string m_osSecret = String.Empty;
    public string osSecret => m_osSecret;

    protected BaseHttpServer m_httpServer;
    public BaseHttpServer HttpServer => m_httpServer;

    private readonly MoneySessionStore m_sessionStore;
    public Dictionary<string, string> SessionDic => m_sessionStore.SessionDic;
    public Dictionary<string, string> SecureSessionDic => m_sessionStore.SecureSessionDic;
    public Dictionary<string, string> WebSessionDic => m_sessionStore.WebSessionDic;

    private readonly MoneyXmlRpcController m_moneyXmlRpcController;
    private readonly MoneyDBService m_moneyDBService;

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MoneyService> _logger;
    private readonly IServerBase _serverBase;

    public MoneyService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<MoneyService> logger,
        IServerBase serverBase,
        MoneySessionStore sessionStore
        )
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _serverBase = serverBase;
        m_sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));

        // Deal with the old fashioned config here for now.  This will go away when we're fully converted.
        MainConsole.Instance = serverBase.Console;

        // Random uuid for private data
        m_osSecret = UUID.Random().ToString();

        // [Startup]
        var startupConfig =_configuration.GetSection("Startup");
        if (startupConfig.Exists() is false)
        {
            _logger.LogInformation("[MONEY SERVER]: [Startup] section is not found. Using default settings");
        }
        else
        {
            DEAD_TIME = startupConfig.GetValue<int>("ExpiredTime", DEAD_TIME);
            m_moneyServerPort = startupConfig.GetValue<uint>("ServerPort", m_moneyServerPort);
        }

        // [MoneyServer]
        var serverConfig = _configuration.GetSection("MoneyServer");
        if (serverConfig.Exists() is false)
        {
            _logger.LogInformation("[MONEY SERVER]: [MoneyServer] section is not found. Using default settings");
        }
        else
        {
            DEAD_TIME = serverConfig.GetValue<int>("ExpiredTime", DEAD_TIME);
            m_moneyServerPort = serverConfig.GetValue<uint>("ServerPort", m_moneyServerPort);
        }


        _logger.LogInformation("[MONEY SERVER]: Setup HTTP Server process");
        try
        {
            m_httpServer = new BaseHttpServer(m_moneyServerPort);
            m_httpServer.Start();
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY SERVER]: StartupSpecific: Fail to start HTTPS process");
            _logger.LogError("[MONEY SERVER]: StartupSpecific: Please Check Certificate File or Password. Exit");
            _logger.LogError("[MONEY SERVER]: StartupSpecific: {0}", e);
            Environment.Exit(1);
        }

        _logger.LogInformation("[MONEY SERVER]: Connecting to Money Storage Server");
        m_moneyDBService = _serviceProvider.GetRequiredService<MoneyDBService>();
        m_moneyDBService.Initialise();

        m_moneyXmlRpcController = _serviceProvider.GetRequiredService<MoneyXmlRpcController>();
        m_moneyXmlRpcController.RegisterLegacyHandlers(m_httpServer);
    }

    public Task StartAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} is running.", nameof(MoneyService));

        Startup();
        Work();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} is stopping.", nameof(MoneyService));

        Shutdown();

        return Task.CompletedTask;
    }
    
    
    public virtual void Startup()
    {
        _logger.LogInformation("[STARTUP]: Beginning startup processing");
        _logger.LogInformation("[STARTUP]: Version: " + _serverBase.Version);
        _logger.LogInformation($"[STARTUP]: Operating system version: {Environment.OSVersion}, .NET platform {Util.RuntimePlatformStr}, Runtime {Environment.Version}");
        _logger.LogInformation($"[STARTUP]: Processor Architecture: {RuntimeInformation.ProcessArchitecture}({(BitConverter.IsLittleEndian ? "le" : "be")} {(Environment.Is64BitProcess ? "64" : "32")}bit)");
        _logger.LogInformation($"[STARTUP]: Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB");
        
        try
        {
            _serverBase.RegisterCommonCommands();
            _serverBase.RegisterCommonComponents(_serverBase.Config);
        }
        catch(Exception e)
        {
            _logger.LogCritical($"Fatal error: {e}");
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Work
    /// </summary>
    public void Work()
    {
        //The timer checks the transactions table every 60 seconds
        System.Timers.Timer checkTimer = new Timer
        {
            Interval = 60 * 1000,
            Enabled = true
        };

        checkTimer.Elapsed += new ElapsedEventHandler(CheckTransaction);
        checkTimer.Start();

        while (true)
        {
            _serverBase.Console.Prompt();
        }
    }

    /// <summary>
    /// Check the transactions table, set expired transaction state to failed
    /// </summary>
    private void CheckTransaction(object sender, ElapsedEventArgs e)
    {
        long ticksToEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks; //TickstartupTime;
        int unixEpochTime = (int)((DateTime.UtcNow.Ticks - ticksToEpoch) / 10000000);
        int deadTime = unixEpochTime - DEAD_TIME;

        m_moneyDBService.SetTransExpired(deadTime);
    }

    protected void Shutdown()
    {
        Watchdog.Enabled = false;
        MainServer.Instance.Stop();

        Thread.Sleep(500);
        WorkManager.Stop();

        _serverBase.Shutdown();
    }

    /// <summary>
    /// Provides a list of help topics that are available.  Overriding classes should append their topics to the
    /// information returned when the base method is called.
    /// </summary>
    ///
    /// <returns>
    /// A list of strings that represent different help topics on which more information is available
    /// </returns>
    protected virtual List<string> GetHelpTopics() { return new List<string>(); }

    /// <summary>
    /// Print statistics to the logfile, if they are active
    /// </summary>
    protected void LogDiagnostics(object source, ElapsedEventArgs e)
    {
        StringBuilder sb = new StringBuilder("DIAGNOSTICS\n\n");
        sb.Append(_serverBase.GetUptimeReport());
        sb.Append(Environment.NewLine);
        sb.Append(_serverBase.GetThreadsReport());
        _logger.LogDebug(sb.ToString());
    }
}
