using System.Net.Security;
using System.Runtime.InteropServices.Swift;
using System.Security.Cryptography.X509Certificates;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using Autofac;
using OpenSim.Framework;

namespace OpenSim.Server.RobustServer
{
    public class RobustService : IHostedService     // ServerBase
    {
        private readonly IComponentContext _componentContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RobustService> _logger;
        private readonly ICommandConsole _console;
        
        // Legacy Stuff
        // protected ServerBase m_Server = null;

        private readonly object _consoleType;

        private readonly string _prompt;
        //protected List<IServiceConnector> m_ServiceConnectors = new();

        public RobustService(
            IConfiguration configuration,
            ILogger<RobustService> logger,
            IComponentContext componentContext)
        {
            _configuration = configuration;
            _logger = logger;
            _componentContext = componentContext;
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

        private void InitalizeServiceConnectors()
        {
            var serverConfig = _configuration.GetSection("Startup");
            if (serverConfig.Exists() is false)
            {
                _logger.LogError("ServiceList config section missing in .ini file");
                throw new Exception("Configuration error");
            }
            
            string connList = serverConfig.GetValue("ServiceConnectors", string.Empty);
            var servicesConfig = _configuration.GetSection("ServiceList");

            List<string> servicesList = new();
            if (!string.IsNullOrEmpty(connList))
                servicesList.Add(connList);

            foreach (var kvp in servicesConfig.AsEnumerable())
            {
                var v = kvp.Value;
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

                //BaseHttpServer server;

                //if (port != 0)
                //    server = (BaseHttpServer)MainServer.GetHttpServer(port);
                //else
                //    server = MainServer.Instance;

                //if (friendlyName == "LLLoginServiceInConnector")
                //    server.AddSimpleStreamHandler(new IndexPHPHandler(server));

                // _logger.LogInformation("[SERVER]: Loading {0} on port {1}", friendlyName, server.Port);

                // try
                // {
                //     var connector = _componentContext.ResolveNamed<IServiceConnector>(friendlyName);
                //     if (connector is not null)
                //     {
                //         connector.Initialize(server, configName);
                //         m_ServiceConnectors.Add(connector);
                //         _logger.LogInformation($"[SERVER]: {friendlyName} loaded successfully");
                //     }
                //     else
                //     {
                //         _logger.LogError($"[SERVER]: Failed to load {conn}");
                //     }
                // }
                // catch (Exception e)
                // {
                //     _logger.LogError(e, $"[SERVER]: Failed to load {friendlyName}");
                //     continue;
                // }
            }
        }

        public Task StartAsync(CancellationToken stoppgToken)
        {
            var args = Environment.GetCommandLineArgs();

            _logger.LogInformation($"{nameof(RobustServer)} is running.");
            //
            // InitializeConsole(args);
            // InitializeNetwork(args);
            // InitalizeServiceConnectors();

            PrintFileToConsole("robuststartuplogo.txt");

            // int res = m_Server.Run();
            // m_Server?.Shutdown();

            Environment.Exit(0);

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
