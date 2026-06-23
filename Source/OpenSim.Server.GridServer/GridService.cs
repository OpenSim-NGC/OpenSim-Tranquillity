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
using log4net;
using System.Reflection;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using OpenSim.Framework.Monitoring;

namespace OpenSim.Server.GridServer;

public class GridService : IHostedService
{
    private readonly ILog m_log = LogManager.GetLogger( MethodBase.GetCurrentMethod().DeclaringType);

    private readonly HttpServerBase m_Server = null;
    private readonly List<IServiceConnector> m_ServiceConnectors = new();

    private readonly PluginLoader loader;
    private readonly bool m_NoVerifyCertChain = false;
    private readonly bool m_NoVerifyCertHostname = false;
    
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GridService> _logger;
    private readonly IServerBase _serverBase;

    public GridService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<GridService> logger,
        IServerBase serverBase
        )
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _serverBase = serverBase;

        // Deal with the old fashioned config here for now.  This will go away when we're fully converted.
        MainConsole.Instance = serverBase.Console;
        
         // Old fashioned initialization. Get Args
        string[] args = Environment.GetCommandLineArgs();      
        m_Server = new HttpServerBase("R.O.B.U.S.T.", args);

        string registryLocation;

        IConfig serverConfig = m_Server.Config.Configs["Startup"];
        if (serverConfig == null)
        {
            System.Console.WriteLine("Startup config section missing in .ini file");
            throw new Exception("Configuration error");
        }

        int dnsTimeout = serverConfig.GetInt("DnsTimeout", 30000);
        try { ServicePointManager.DnsRefreshTimeout = dnsTimeout; } catch { }

        m_NoVerifyCertChain = serverConfig.GetBoolean("NoVerifyCertChain", m_NoVerifyCertChain);
        m_NoVerifyCertHostname = serverConfig.GetBoolean("NoVerifyCertHostname", m_NoVerifyCertHostname);

        WebUtil.SetupHTTPClients(m_NoVerifyCertChain, m_NoVerifyCertHostname, null, 32);

        string connList = serverConfig.GetString("ServiceConnectors", string.Empty);

        registryLocation = serverConfig.GetString("RegistryLocation",".");

        IConfig servicesConfig = m_Server.Config.Configs["ServiceList"];
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

        string[] conns = connList.Split(new char[] {',', ' ', '\n', '\r', '\t'});

        foreach (string c in conns)
        {
            if (string.IsNullOrEmpty(c))
                continue;

            string configName = string.Empty;
            string conn = c;
            uint port = 0;

            string[] split1 = conn.Split(new char[] {'/'});
            if (split1.Length > 1)
            {
                conn = split1[1];

                string[] split2 = split1[0].Split(new char[] {'@'});
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
            string[] parts = conn.Split(new char[] {':'});
            string friendlyName = parts[0];
            if (parts.Length > 1)
                friendlyName = parts[1];

            IHttpServer server;

            if (port != 0)
                server = MainServer.Instance.GetHttpServer(port);
            else
                server = MainServer.Instance.DefaultServer;

            if (friendlyName == "LLLoginServiceInConnector")
                server.AddSimpleStreamHandler(new IndexPHPHandler(server));

            m_log.InfoFormat("[SERVER]: Loading {0} on port {1}", friendlyName, server.Port);

            IServiceConnector connector = null;

            object[] modargs = new object[] { m_Server.Config, server, configName };
            connector = ServerUtils.LoadPlugin<IServiceConnector>(conn, modargs);

            if (connector == null)
            {
                modargs = new object[] { m_Server.Config, server };
                connector = ServerUtils.LoadPlugin<IServiceConnector>(conn, modargs);
            }

            if (connector != null)
            {
                m_ServiceConnectors.Add(connector);
                m_log.InfoFormat("[SERVER]: {0} loaded successfully", friendlyName);
            }
            else
            {
                m_log.ErrorFormat("[SERVER]: Failed to load {0}", conn);
            }
        }

        PrintFileToConsole("robuststartuplogo.txt");

        loader = new PluginLoader(m_Server.Config, registryLocation);

        int res = m_Server.Run();

        m_Server?.Shutdown();

        Environment.Exit(res);
    }

    public bool ValidateServerCertificate(
        object sender,
        X509Certificate certificate,
        X509Chain chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (m_NoVerifyCertChain)
            sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateChainErrors;

        if (m_NoVerifyCertHostname)
            sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;

        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        return false;
    }

    /// <summary>
    /// Opens a file and uses it as input to the console command parser.
    /// </summary>
    /// <param name="fileName">name of file to use as input to the console</param>
    private void PrintFileToConsole(string fileName)
    {
        if (File.Exists(fileName))
        {
            using(StreamReader readFile = File.OpenText(fileName))
            {
                string currentLine;
                while ((currentLine = readFile.ReadLine()) is not null)
                {
                    m_log.InfoFormat("[!]" + currentLine);
                }
            }
        }
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
        // //The timer checks the transactions table every 60 seconds
        // System.Timers.Timer checkTimer = new Timer
        // {
        //     Interval = 60 * 1000,
        //     Enabled = true
        // };

        // checkTimer.Elapsed += new ElapsedEventHandler(CheckTransaction);
        // checkTimer.Start();

        while (true)
        {
            _serverBase.Console.Prompt();
        }
    }

    protected void Shutdown()
    {
        Watchdog.Enabled = false;
        MainServer.Instance.Stop();

        Thread.Sleep(500);
        WorkManager.Stop();

        _serverBase.Shutdown();
    }


    public Task StartAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} is running.", nameof(GridService));

        Startup();
        Work();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} is stopping.", nameof(GridService));

        Shutdown();

        return Task.CompletedTask;
    }
}
