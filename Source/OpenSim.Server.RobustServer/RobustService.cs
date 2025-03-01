using Nini.Config;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using log4net.Config;
using Autofac;

namespace OpenSim.Server.RobustServer
{
    public class RobustService : IHostedService
    {
        private readonly IComponentContext _componentContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RobustService> _logger;

        // Legacy Stuff
        protected HttpServerBase m_Server = null;
        protected List<IServiceConnector> m_ServiceConnectors = new();
        private bool m_NoVerifyCertChain = false;
        private bool m_NoVerifyCertHostname = false;
        

        public RobustService(
            IComponentContext componentContext,
            IConfiguration configuration,
            ILogger<RobustService> logger)
        {
            _componentContext = componentContext;
            _configuration = configuration;
            _logger = logger;
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
                        _logger.LogInformation($"[!] {currentLine}");
                    }
                }
            }
        }

        private IConfig LoadConfiguration(string[] args)
        {
            XmlConfigurator.Configure();

            IConfig serverConfig = m_Server.Config.Configs["Startup"];
            if (serverConfig == null)
            {
                System.Console.WriteLine("Startup config section missing in .ini file");
                throw new Exception("Configuration error");
            }

            return serverConfig;
        }

        private void InitializeNetwork(IConfig serverConfig, string[] args)
        {
            Culture.SetCurrentCulture();
            Culture.SetDefaultCurrentCulture();

            // ServicePointManager.DefaultConnectionLimit = 64;
            // ServicePointManager.MaxServicePointIdleTime = 30000;

            // ServicePointManager.Expect100Continue = false;
            // ServicePointManager.UseNagleAlgorithm = false;
            // ServicePointManager.ServerCertificateValidationCallback = ValidateServerCertificate;

            m_Server = new HttpServerBase("R.O.B.U.S.T.", args);

            // int dnsTimeout = serverConfig.GetInt("DnsTimeout", 30000);
            // try { ServicePointManager.DnsRefreshTimeout = dnsTimeout; } catch { }

            m_NoVerifyCertChain = serverConfig.GetBoolean("NoVerifyCertChain", m_NoVerifyCertChain);
            m_NoVerifyCertHostname = serverConfig.GetBoolean("NoVerifyCertHostname", m_NoVerifyCertHostname);

            WebUtil.SetupHTTPClients(m_NoVerifyCertChain, m_NoVerifyCertHostname, null, 32);
        }

        private static void ParseServiceEntry(string c, out string configName, out string conn, out uint port, out string friendlyName)
        {
            configName = string.Empty;
            conn = c;
            port = 0;
            
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
            friendlyName = parts[0];
            
            if (parts.Length > 1)
                friendlyName = parts[1];
        }

        private void InitalizeServiceConnectors(IConfig serverConfig)
        {
            string connList = serverConfig.GetString("ServiceConnectors", string.Empty);
            IConfig servicesConfig = m_Server.Config.Configs["ServiceList"];

            if (servicesConfig == null)
            {
                _logger.LogError("ServiceList config section missing in .ini file");
                throw new Exception("Configuration error");
            }

            List<string> servicesList = new();
            if (!string.IsNullOrEmpty(connList))
                servicesList.Add(connList);

            foreach (string k in servicesConfig.GetKeys())
            {
                string v = servicesConfig.GetString(k);
                if (!string.IsNullOrEmpty(v))
                    servicesList.Add(v);
            }

            if (servicesList.Count == 0)
            {
                _logger.LogError("No service connectors found in ServiceList config section");
                throw new Exception("Configuration error");
            }

            connList = string.Join(",", servicesList.ToArray());
            string[] conns = connList.Split(new char[] { ',', ' ', '\n', '\r', '\t' });

            foreach (string c in conns)
            {
                if (string.IsNullOrEmpty(c))
                    continue;

                string configName, conn, friendlyName;
                uint port;

                ParseServiceEntry(c, out configName, out conn, out port, out friendlyName);

                BaseHttpServer server;

                if (port != 0)
                    server = (BaseHttpServer)MainServer.GetHttpServer(port);
                else
                    server = MainServer.Instance;

                if (friendlyName == "LLLoginServiceInConnector")
                    server.AddSimpleStreamHandler(new IndexPHPHandler(server));

                _logger.LogInformation("[SERVER]: Loading {0} on port {1}", friendlyName, server.Port);

                try
                {
                    IServiceConnector connector = _componentContext.ResolveNamed<IServiceConnector>(friendlyName);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"[SERVER]: Failed to load {friendlyName}");
                    continue;
                }

                // object[] modargs = new object[] { m_Server.Config, server, configName };
                // connector = ServerUtils.LoadPlugin<IServiceConnector>(conn, modargs);

                // if (connector == null)
                // {
                //     modargs = new object[] { m_Server.Config, server };
                //     connector = ServerUtils.LoadPlugin<IServiceConnector>(conn, modargs);
                // }

                // if (connector != null)
                // {
                //     m_ServiceConnectors.Add(connector);
                //     _logger.LogInformation($"[SERVER]: {friendlyName} loaded successfully");
                // }
                // else
                // {
                //     _logger.LogError($"[SERVER]: Failed to load {conn}");
                // }
            }
        }

        public Task StartAsync(CancellationToken stoppgToken)
        {
            var args = Environment.GetCommandLineArgs();

            _logger.LogInformation($"{nameof(RobustServer)} is running.");

            IConfig serverConfig = LoadConfiguration(args);

            InitializeNetwork(serverConfig, args);
            InitalizeServiceConnectors(serverConfig);

            PrintFileToConsole("robuststartuplogo.txt");

            int res = m_Server.Run();
            m_Server?.Shutdown();

            Environment.Exit(res);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("{Service} is stopping.", nameof(RobustServer));

            // Nothing to do here
            // OpenSimServer.Shutdown(m_res);

            return Task.CompletedTask;
        }
    }
}
