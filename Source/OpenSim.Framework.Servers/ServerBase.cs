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

using System.Diagnostics;
using System.Runtime;
using System.Text;
using System.Text.RegularExpressions;
using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenSim.Framework.Console;
using OpenSim.Framework.Monitoring;

namespace OpenSim.Framework.Servers
{
    public class ServerBase
    {
        private readonly ILogger _logger;
        protected readonly IConfiguration _config;
        private readonly IComponentContext _componentContext;
        protected readonly ServerStatsCollector _serverStatsCollector;
        
        protected readonly string _consoleType;
        protected readonly string _prompt;
        protected readonly uint _remoteConsolePort;
        
        public bool Running
        {
            get { return m_Running; }
        }

        /// <summary>
        /// Console to be used for any command line output.  Can be null, in which case there should be no output.
        /// </summary>
        protected ICommandConsole m_console;
        protected DateTime m_startuptime;
        
        protected string m_startupDirectory = Environment.CurrentDirectory;

        protected string m_pidFile = string.Empty;

        protected ServerStatsCollector m_serverStatsCollector;

        /// <summary>
        /// Server version information.  Usually VersionInfo + information about git commit, operating system, etc.
        /// </summary>
        protected string m_version;

        protected string[] m_Arguments;
        protected string m_configDirectory = ".";
        private bool m_Running = true;
        private bool DoneShutdown = false;

        public ServerBase(
            IConfiguration config,
            ILogger logger,
            IComponentContext componentContext)
        {
            _logger = logger;
            _config = config;
            _componentContext = componentContext;
            _serverStatsCollector = null; // Should new one here.  serverStatsCollector;

            m_startuptime = DateTime.Now;
            m_version = VersionInfo.Version;

            EnhanceVersionInformation();

            InitializeConsole(consoleType: "local", prompt: "$ ");

            InitializeNetwork();
        }
        
        protected void Initialise()
        {
            foreach (var s in MainServer.Servers.Values)
                s.Start();

            MainServer.RegisterHttpConsoleCommands(MainConsole.Instance);
            //
            // MethodInfo mi = m_console.GetType().GetMethod("SetServer", BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(BaseHttpServer) }, null);
            //
            // if (mi != null)
            // {
            //     if (m_consolePort == 0)
            //         mi.Invoke(MainConsole.Instance, new object[] { MainServer.Instance });
            //     else
            //         mi.Invoke(MainConsole.Instance, new object[] { MainServer.GetHttpServer(m_consolePort) });
            // }
        }
        
        public void InitializeConsole(string consoleType = "", string prompt = "")
        {
            var startupConfig = _config.GetSection("Startup");
            if (startupConfig.Exists())
            {
                consoleType = startupConfig.GetValue("console", consoleType);
                prompt = startupConfig.GetValue("Prompt", prompt);
            }

            if (string.IsNullOrEmpty(consoleType) is false)
            {
                if (consoleType == "basic")
                    MainConsole.Instance = _componentContext.ResolveNamed<ICommandConsole>("CommandConsole");
                else if (consoleType == "rest")
                    MainConsole.Instance = _componentContext.ResolveNamed<ICommandConsole>("RemoteConsole");
                else if (consoleType == "mock")
                    MainConsole.Instance = _componentContext.ResolveNamed<ICommandConsole>("MockConsole");
                else if (consoleType == "local")
                    MainConsole.Instance = _componentContext.ResolveNamed<ICommandConsole>("Console");
            }
            else
            {
                MainConsole.Instance = null;
            }

            //  FIXME MainConsole.Instance?.DefaultPrompt(prompt);
        }
        
        private void InitializeNetwork( )
        {
            var startupConfig = _config.GetSection("Startup");
            if (startupConfig.Exists())
            {
                if (startupConfig.GetValue<bool>("EnableRobustSelfsignedCertSupport", false))
                {
                    if (!File.Exists("SSL\\ssl\\"+ startupConfig.GetValue<string>("RobustCertFileName") +".p12") || 
                        startupConfig.GetValue<bool>("RobustCertRenewOnStartup"))
                    {
                        var certFileName = startupConfig.GetValue("RobustCertFileName", "Robust");
                        var certHostName = startupConfig.GetValue("RobustCertHostName", "localhost");
                        var certHostIp = startupConfig.GetValue("RobustCertHostIp", "127.0.0.1");
                        var certPassword = startupConfig.GetValue("RobustCertPassword", string.Empty);
                        
                        Util.CreateOrUpdateSelfsignedCert(certFileName, certHostName, certHostIp, certPassword);
                    }
                }
            }
            
            // var networkConfig = _config.GetSection("Network");
            // if (networkConfig.Exists())
            // {
            //
            //     LogEnvironmentInformation();
            //
            //     RegisterCommonCommands();
            //     RegisterCommonComponents(Config);
            //
            //     // Allow derived classes to perform initialization that
            //     // needs to be done after the console has opened
            //     Initialise();
            //     
            //
            //     uint port = (uint)networkConfig.GetInt("port", 0);
            //
            //     if (port == 0)
            //     {
            //         System.Console.WriteLine("ERROR: No 'port' entry found in [Network].  Server can't start");
            //         Environment.Exit(1);
            //     }
            //
            //     bool ssl_main = networkConfig.GetBoolean("https_main", false);
            //     bool ssl_listener = networkConfig.GetBoolean("https_listener", false);
            //     bool ssl_external = networkConfig.GetBoolean("https_external", false);
            //
            //     _consolePort = networkConfig.GetValue<uint>("ConsolePort", 0);
            //
            //     BaseHttpServer httpServer = null;
            //
            //     //
            //     // This is where to make the servers:
            //     //
            //     //
            //     // Make the base server according to the port, etc.
            //     // ADD: Possibility to make main server ssl
            //     // Then, check for https settings and ADD a server to
            //     // m_Servers
            //     //
            //     if (!ssl_main)
            //     {
            //         httpServer = new BaseHttpServer(port);
            //     }
            //     else
            //     {
            //         string cert_path = networkConfig.GetString("cert_path", string.Empty);
            //         if (cert_path.Length == 0)
            //         {
            //             System.Console.WriteLine("ERROR: Path to X509 certificate is missing, server can't start.");
            //             Environment.Exit(1);
            //         }
            //
            //         string cert_pass = networkConfig.GetString("cert_pass", string.Empty);
            //         if (cert_pass.Length == 0)
            //         {
            //             System.Console.WriteLine(
            //                 "ERROR: Password for X509 certificate is missing, server can't start.");
            //             Environment.Exit(1);
            //         }
            //
            //         httpServer = new BaseHttpServer(port, ssl_main, cert_path, cert_pass);
            //     }
            //
            //     MainServer.AddHttpServer(httpServer);
            //     MainServer.Instance = httpServer;
            //
            //     // If https_listener = true, then add an ssl listener on the https_port...
            //     if (ssl_listener == true)
            //     {
            //         uint https_port = (uint)networkConfig.GetInt("https_port", 0);
            //
            //         _logger.LogWarning($"[SSL]: External flag is {ssl_external}");
            //
            //         if (!ssl_external)
            //         {
            //             string cert_path = networkConfig.GetString("cert_path", string.Empty);
            //             if (cert_path.Length == 0)
            //             {
            //                 System.Console.WriteLine("Path to X509 certificate is missing, server can't start.");
            //                 //Thread.CurrentThread.Abort();
            //             }
            //
            //             string cert_pass = networkConfig.GetString("cert_pass", string.Empty);
            //             if (cert_pass.Length == 0)
            //             {
            //                 System.Console.WriteLine("Password for X509 certificate is missing, server can't start.");
            //                 //Thread.CurrentThread.Abort();
            //             }
            //
            //             MainServer.AddHttpServer(new BaseHttpServer(https_port, ssl_listener, cert_path, cert_pass));
            //         }
            //         else
            //         {
            //             _logger.LogWarning(
            //                 $"[SSL]: SSL port is active but no SSL is used because external SSL was requested.");
            //             MainServer.AddHttpServer(new BaseHttpServer(https_port));
            //         }
            //     }
            // }
            

            Culture.SetCurrentCulture();
            Culture.SetDefaultCurrentCulture();

            // m_Server = new HttpServerBase("R.O.B.U.S.T.", args);
        protected void RemovePIDFile()
        {
            if (!string.IsNullOrEmpty(m_pidFile))
            {
                try
                {
                    File.Delete(m_pidFile);
                }
                catch (Exception e)
                {
                    m_log.Error($"[SERVER BASE]: Error whilst removing {m_pidFile}", e);
                }
                m_pidFile = string.Empty;
            }
        }
        
        /// <summary>
        /// Log information about the circumstances in which we're running (OpenSimulator version number, CLR details,
        /// etc.).
        /// </summary>
        public void LogEnvironmentInformation()
        {
            _logger.LogInformation($"[SERVER BASE]: Starting in {m_startupDirectory}");
            _logger.LogInformation($"[SERVER BASE]: Tranquillity version: {m_version}");
            _logger.LogInformation(
                $"[SERVER BASE]: Operating system version: {Environment.OSVersion}, " +
                $".NET platform {Util.RuntimePlatformStr}, {(Environment.Is64BitProcess ? "64" : "32")}-bit");
        }

        /// <summary>
        /// Register common commands once m_console has been set if it is going to be set
        /// </summary>
        public void RegisterCommonCommands()
        {
            if (m_console == null)
                return;

            m_console.Commands.AddCommand(
                "General", false, "show info", "show info", "Show general information about the server", HandleShow);

            m_console.Commands.AddCommand(
                "General", false, "show version", "show version", "Show server version", HandleShow);

            m_console.Commands.AddCommand(
                "General", false, "show uptime", "show uptime", "Show server uptime", HandleShow);

            m_console.Commands.AddCommand(
                "General", false, "command-script",
                "command-script <script>",
                "Run a command script from file", HandleScript);

            m_console.Commands.AddCommand(
                "General", false, "show threads",
                "show threads",
                "Show thread status", HandleShow);

            m_console.Commands.AddCommand(
                "Debug", false, "threads abort",
                "threads abort <thread-id>",
                "Abort a managed thread.  Use \"show threads\" to find possible threads.", HandleThreadsAbort);

            m_console.Commands.AddCommand(
                "General", false, "threads show",
                "threads show",
                "Show thread status.  Synonym for \"show threads\"",
                (string module, string[] args) => Notice(GetThreadsReport()));

            m_console.Commands.AddCommand (
                "Debug", false, "debug threadpool set",
                "debug threadpool set worker|iocp min|max <n>",
                "Set threadpool parameters.  For debug purposes.",
                HandleDebugThreadpoolSet);

            m_console.Commands.AddCommand (
                "Debug", false, "debug threadpool status",
                "debug threadpool status",
                "Show current debug threadpool parameters.",
                HandleDebugThreadpoolStatus);

            m_console.Commands.AddCommand(
                "Debug", false, "debug threadpool level",
                "debug threadpool level 0.." + Util.MAX_THREADPOOL_LEVEL,
                "Turn on logging of activity in the main thread pool.",
                "Log levels:\n"
                    + "  0 = no logging\n"
                    + "  1 = only first line of stack trace; don't log common threads\n"
                    + "  2 = full stack trace; don't log common threads\n"
                    + "  3 = full stack trace, including common threads\n",
                HandleDebugThreadpoolLevel);

            //m_console.Commands.AddCommand(
            //    "Debug", false, "show threadpool calls active",
            //    "show threadpool calls active",
            //    "Show details about threadpool calls that are still active (currently waiting or in progress)",
            //       HandleShowThreadpoolCallsActive);

            m_console.Commands.AddCommand(
                "Debug", false, "show threadpool calls complete",
                "show threadpool calls complete",
                "Show details about threadpool calls that have been completed.",
                HandleShowThreadpoolCallsComplete);

            m_console.Commands.AddCommand(
                "Debug", false, "force gc",
                "force gc",
                "Manually invoke runtime garbage collection.  For debugging purposes",
                HandleForceGc);

            m_console.Commands.AddCommand(
                "General", false, "quit",
                "quit",
                "Quit the application", (mod, args) => Shutdown());

            m_console.Commands.AddCommand(
                "General", false, "shutdown",
                "shutdown",
                "Quit the application", (mod, args) => Shutdown());

            m_console.SetCntrCHandler(Shutdown);

            ChecksManager.RegisterConsoleCommands(m_console);
            StatsManager.RegisterConsoleCommands(m_console);
        }

        public void RegisterCommonComponents()
        {
            if (_serverStatsCollector.Enabled is true)
            {
                _serverStatsCollector.Start();
            }
        }

        private void HandleShowThreadpoolCallsActive(string module, string[] args)
        {
            List<KeyValuePair<string, int>> calls = Util.GetFireAndForgetCallsInProgress().ToList();
            calls.Sort((kvp1, kvp2) => kvp2.Value.CompareTo(kvp1.Value));
            int namedCalls = 0;

            ConsoleDisplayList cdl = new ConsoleDisplayList();
            foreach (KeyValuePair<string, int> kvp in calls)
            {
                if (kvp.Value > 0)
                {
                    cdl.AddRow(kvp.Key, kvp.Value);
                    namedCalls += kvp.Value;
                }
            }

            cdl.AddRow("TOTAL NAMED", namedCalls);

            long allQueuedCalls = Util.TotalQueuedFireAndForgetCalls;
            long allRunningCalls = Util.TotalRunningFireAndForgetCalls;

            cdl.AddRow("TOTAL QUEUED", allQueuedCalls);
            cdl.AddRow("TOTAL RUNNING", allRunningCalls);
            cdl.AddRow("TOTAL ANONYMOUS", allQueuedCalls + allRunningCalls - namedCalls);
            cdl.AddRow("TOTAL ALL", allQueuedCalls + allRunningCalls);

            MainConsole.Instance.Output(cdl.ToString());
        }

        private void HandleShowThreadpoolCallsComplete(string module, string[] args)
        {
            List<KeyValuePair<string, int>> calls = Util.GetFireAndForgetCallsMade().ToList();
            calls.Sort((kvp1, kvp2) => kvp2.Value.CompareTo(kvp1.Value));
            int namedCallsMade = 0;

            ConsoleDisplayList cdl = new ConsoleDisplayList();
            foreach (KeyValuePair<string, int> kvp in calls)
            {
                cdl.AddRow(kvp.Key, kvp.Value);
                namedCallsMade += kvp.Value;
            }

            cdl.AddRow("TOTAL NAMED", namedCallsMade);

            long allCallsMade = Util.TotalFireAndForgetCallsMade;
            cdl.AddRow("TOTAL ANONYMOUS", allCallsMade - namedCallsMade);
            cdl.AddRow("TOTAL ALL", allCallsMade);

            MainConsole.Instance.Output(cdl.ToString());
        }

        private void HandleDebugThreadpoolStatus(string module, string[] args)
        {
            int workerThreads, iocpThreads;

            ThreadPool.GetMinThreads(out workerThreads, out iocpThreads);
            Notice("Min worker threads:       {0}", workerThreads);
            Notice("Min IOCP threads:         {0}", iocpThreads);

            ThreadPool.GetMaxThreads(out workerThreads, out iocpThreads);
            Notice("Max worker threads:       {0}", workerThreads);
            Notice("Max IOCP threads:         {0}", iocpThreads);

            ThreadPool.GetAvailableThreads(out workerThreads, out iocpThreads);
            Notice("Available worker threads: {0}", workerThreads);
            Notice("Available IOCP threads:   {0}", iocpThreads);
        }

        private void HandleDebugThreadpoolSet(string module, string[] args)
        {
            if (args.Length != 6)
            {
                Notice("Usage: debug threadpool set worker|iocp min|max <n>");
                return;
            }

            if (!ConsoleUtil.TryParseConsoleInt(m_console, args[5], out int newThreads))
                return;

            string poolType = args[3];
            string bound = args[4];

            bool fail = false;
            int workerThreads, iocpThreads;

            if (poolType == "worker")
            {
                if (bound == "min")
                {
                    ThreadPool.GetMinThreads(out workerThreads, out iocpThreads);

                    if (!ThreadPool.SetMinThreads(newThreads, iocpThreads))
                        fail = true;
                }
                else
                {
                    ThreadPool.GetMaxThreads(out workerThreads, out iocpThreads);

                    if (!ThreadPool.SetMaxThreads(newThreads, iocpThreads))
                        fail = true;
                }
            }
            else
            {
                if (bound == "min")
                {
                    ThreadPool.GetMinThreads(out workerThreads, out iocpThreads);

                    if (!ThreadPool.SetMinThreads(workerThreads, newThreads))
                        fail = true;
                }
                else
                {
                    ThreadPool.GetMaxThreads(out workerThreads, out iocpThreads);

                    if (!ThreadPool.SetMaxThreads(workerThreads, newThreads))
                        fail = true;
                }
            }

            if (fail)
            {
                Notice("ERROR: Could not set {0} {1} threads to {2}", poolType, bound, newThreads);
            }
            else
            {
                ThreadPool.GetMinThreads(out int minWorkerThreads, out int minIocpThreads);
                ThreadPool.GetMaxThreads(out int maxWorkerThreads, out int maxIocpThreads);

                Notice("Min worker threads now {0}", minWorkerThreads);
                Notice("Min IOCP threads now {0}", minIocpThreads);
                Notice("Max worker threads now {0}", maxWorkerThreads);
                Notice("Max IOCP threads now {0}", maxIocpThreads);
            }
        }

        private static void HandleDebugThreadpoolLevel(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 4)
            {
                MainConsole.Instance.Output("Usage: debug threadpool level 0.." + Util.MAX_THREADPOOL_LEVEL);
                return;
            }

            string rawLevel = cmdparams[3];
            int newLevel;

            if (!int.TryParse(rawLevel, out newLevel))
            {
                MainConsole.Instance.Output("{0} is not a valid debug level", rawLevel);
                return;
            }

            if (newLevel < 0 || newLevel > Util.MAX_THREADPOOL_LEVEL)
            {
                MainConsole.Instance.Output("{0} is outside the valid debug level range of 0.." + Util.MAX_THREADPOOL_LEVEL, newLevel);
                return;
            }

            Util.LogThreadPool = newLevel;
            MainConsole.Instance.Output("LogThreadPool set to {0}", newLevel);
        }

        private void HandleForceGc(string module, string[] args)
        {
            Notice("Manually invoking runtime garbage collection");
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
        }

        public virtual void HandleShow(string module, string[] cmd)
        {
            if(cmd.Length < 2)
                return;

            switch (cmd[1])
            {
                case "info":
                    ShowInfo();
                    break;

                case "version":
                    Notice(GetVersionText());
                    break;

                case "uptime":
                    Notice(GetUptimeReport());
                    break;

                case "threads":
                    Notice(GetThreadsReport());
                    break;
            }
        }
        
        protected virtual void HandleScript(string module, string[] parms)
        {
            if (parms.Length != 2)
            {
                Notice("Usage: command-script <path-to-script");
                return;
            }

            RunCommandScript(parms[1]);
        }

        /// <summary>
        /// Run an optional startup list of commands
        /// </summary>
        /// <param name="fileName"></param>
        protected void RunCommandScript(string fileName)
        {
            if (m_console == null)
                return;

            if (File.Exists(fileName))
            {
                _logger.LogInformation("[SERVER BASE]: Running " + fileName);

                using (StreamReader readFile = File.OpenText(fileName))
                {
                    string currentCommand;
                    while ((currentCommand = readFile.ReadLine()) != null)
                    {
                        currentCommand = currentCommand.Trim();
                        if (!(currentCommand.Length == 0
                            || currentCommand.StartsWith(";")
                            || currentCommand.StartsWith("//")
                            || currentCommand.StartsWith("#")))
                        {
                            _logger.LogInformation("[SERVER BASE]: Running '" + currentCommand + "'");
                            m_console.RunCommand(currentCommand);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Return a report about the uptime of this server
        /// </summary>
        /// <returns></returns>
        protected string GetUptimeReport()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.AppendFormat("Time now is {0}\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendFormat("Server has been running since {0}, {1}\n", m_startuptime.DayOfWeek, m_startuptime.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendFormat("That is an elapsed time of {0}\n", DateTime.Now - m_startuptime);
            return sb.ToString();
        }

        protected void ShowInfo()
        {
            Notice(GetVersionText());
            Notice("Startup directory: " + m_startupDirectory);
        }

        /// <summary>
        /// Enhance the version string with extra information if it's available.
        /// </summary>
        protected void EnhanceVersionInformation()
        {
            const string manualVersionFileName = ".version";
            string buildVersion = string.Empty;

            if (File.Exists(manualVersionFileName))
            {
                using (StreamReader CommitFile = File.OpenText(manualVersionFileName))
                    buildVersion = CommitFile.ReadLine();

                if (!string.IsNullOrEmpty(buildVersion))
                {
                    m_version += buildVersion;
                    return;
                }
            }

            string gitDir = Path.Combine("..", ".git");
            string gitRefPointerPath = Path.Combine(gitDir, "HEAD");
            if (File.Exists(gitRefPointerPath))
            {
                //_logger.DebugFormat("[SERVER BASE]: Found {0}", gitRefPointerPath);

                string rawPointer = "";

                using (StreamReader pointerFile = File.OpenText(gitRefPointerPath))
                    rawPointer = pointerFile.ReadLine();

                //_logger.DebugFormat("[SERVER BASE]: rawPointer [{0}]", rawPointer);

                Match m = Regex.Match(rawPointer, "^ref: (.+)$");

                if (m.Success)
                {
                    //_logger.DebugFormat("[SERVER BASE]: Matched [{0}]", m.Groups[1].Value);

                    string gitRef = m.Groups[1].Value;
                    string gitRefPath = Path.Combine(gitDir, gitRef);
                    if (File.Exists(gitRefPath))
                    {
                        //_logger.DebugFormat("[SERVER BASE]: Found gitRefPath [{0}]", gitRefPath);
                        using (StreamReader refFile = File.OpenText(gitRefPath))
                            buildVersion = refFile.ReadLine();

                        if (!string.IsNullOrEmpty(buildVersion))
                            m_version += buildVersion[..7];
                    }
                }
            }
        }

        public string GetVersionText()
        {
            return $"Version: {m_version} (SIM-{VersionInfo.SimulationServiceVersionSupportedMin}/{VersionInfo.SimulationServiceVersionSupportedMax})";
        }

        /// <summary>
        /// Get a report about the registered threads in this server.
        /// </summary>
        protected string GetThreadsReport()
        {
            // This should be a constant field.
            string reportFormat = "{0,6}   {1,35}   {2,16}   {3,13}   {4,10}   {5,30}";

            StringBuilder sb = new StringBuilder();
            Watchdog.ThreadWatchdogInfo[] threads = Watchdog.GetThreadsInfo();

            sb.Append(threads.Length + " threads are being tracked:" + Environment.NewLine);

            int timeNow = Environment.TickCount & Int32.MaxValue;

            sb.AppendFormat(reportFormat, "ID", "NAME", "LAST UPDATE (MS)", "LIFETIME (MS)", "PRIORITY", "STATE");
            sb.Append(Environment.NewLine);

            foreach (Watchdog.ThreadWatchdogInfo twi in threads)
            {
                Thread t = twi.Thread;

                sb.AppendFormat(
                    reportFormat,
                    t.ManagedThreadId,
                    t.Name,
                    timeNow - twi.LastTick,
                    timeNow - twi.FirstTick,
                    t.Priority,
                    t.ThreadState);

                sb.Append("\n");
            }

            sb.Append(GetThreadPoolReport());

            sb.Append("\n");
            int totalThreads = Process.GetCurrentProcess().Threads.Count;
            if (totalThreads > 0)
                sb.AppendFormat("Total process threads: {0}\n\n", totalThreads);

            return sb.ToString();
        }

        /// <summary>
        /// Get a thread pool report.
        /// </summary>
        /// <returns></returns>
        public static string GetThreadPoolReport()
        {

            StringBuilder sb = new StringBuilder();

            // framework pool is alwasy active
            int maxWorkers;
            int minWorkers;
            int curWorkers;
            int maxComp;
            int minComp;
            int curComp;

            try
            {
                ThreadPool.GetMaxThreads(out maxWorkers, out maxComp);
                ThreadPool.GetMinThreads(out minWorkers, out minComp);
                ThreadPool.GetAvailableThreads(out curWorkers, out curComp);
                curWorkers = maxWorkers - curWorkers;
                curComp = maxComp - curComp;

                sb.Append("\nFramework main threadpool \n");
                sb.AppendFormat("workers:    {0} ({1} / {2})\n", curWorkers, maxWorkers, minWorkers);
                sb.AppendFormat("Completion: {0} ({1} / {2})\n", curComp, maxComp, minComp);
            }
            catch { }

            if (Util.FireAndForgetMethod == FireAndForgetMethod.QueueUserWorkItem)
            {
                sb.AppendFormat("\nThread pool used: Framework main threadpool\n");
                return sb.ToString();
            }

            string threadPoolUsed = null;
            int maxThreads = 0;
            int minThreads = 0;
            int allocatedThreads = 0;
            int inUseThreads = 0;
            int waitingCallbacks = 0;
 
            if (threadPoolUsed != null)
            {
                sb.Append("\nThreadpool (excluding script engine pools)\n");
                sb.AppendFormat("Thread pool used           : {0}\n", threadPoolUsed);
                sb.AppendFormat("Max threads                : {0}\n", maxThreads);
                sb.AppendFormat("Min threads                : {0}\n", minThreads);
                sb.AppendFormat("Allocated threads          : {0}\n", allocatedThreads < 0 ? "not applicable" : allocatedThreads.ToString());
                sb.AppendFormat("In use threads             : {0}\n", inUseThreads);
                sb.AppendFormat("Work items waiting         : {0}\n", waitingCallbacks < 0 ? "not available" : waitingCallbacks.ToString());
            }
            else
            {
                sb.AppendFormat("Thread pool not used\n");
            }

            return sb.ToString();
        }

        public virtual void HandleThreadsAbort(string module, string[] cmd)
        {
            if (cmd.Length != 3)
            {
                MainConsole.Instance.Output("Usage: threads abort <thread-id>");
                return;
            }

            int threadId;
            if (!int.TryParse(cmd[2], out threadId))
            {
                MainConsole.Instance.Output("ERROR: Thread id must be an integer");
                return;
            }

            if (Watchdog.AbortThread(threadId))
                MainConsole.Instance.Output("Aborted thread with id {0}", threadId);
            else
                MainConsole.Instance.Output("ERROR - Thread with id {0} not found in managed threads", threadId);
        }

        /// <summary>
        /// Console output is only possible if a console has been established.
        /// That is something that cannot be determined within this class. So
        /// all attempts to use the console MUST be verified.
        /// </summary>
        /// <param name="msg"></param>
        protected void Notice(string msg)
        {
            if (m_console != null)
            {
                m_console.Output(msg);
            }
        }

        /// <summary>
        /// Console output is only possible if a console has been established.
        /// That is something that cannot be determined within this class. So
        /// all attempts to use the console MUST be verified.
        /// </summary>
        /// <param name="format"></param>
        /// <param name="components"></param>
        protected void Notice(string format, params object[] components)
        {
            if (m_console != null)
                m_console.Output(format, components);
        }

        public virtual void Shutdown()
        {
            if (_serverStatsCollector != null)
            {
                _serverStatsCollector.Close();
            }

            ShutdownSpecific();
        }

        public virtual int Run()
        {
            Watchdog.Enabled = true;
            MemoryWatchdog.Enabled = true;

            while (m_Running)
            {
                try
                {
                    MainConsole.Instance.Prompt();
                }
                catch (Exception e)
                {
                    LoggerExtensions.LogError(_logger, e, $"Command error");
                }
            }

            if (!DoneShutdown)
            {
                DoneShutdown = true;
                MainServer.Stop();

                MemoryWatchdog.Enabled = false;
                Watchdog.Enabled = false;
                WorkManager.Stop();
            }
            return 0;
        }

        protected void ShutdownSpecific()
        {
            if(!m_Running)
                return;
            
            m_Running = false;
            
            LoggerExtensions.LogInformation(_logger, "[CONSOLE] Quitting");
            
            MainServer.Stop();
            MemoryWatchdog.Enabled = false;
        }

        /// <summary>
        /// Check if we can convert the string to a URI
        /// </summary>
        /// <param name="file">String uri to the remote resource</param>
        /// <returns>true if we can convert the string to a Uri object</returns>
        private bool IsUri(string file)
        {
            Uri configUri;

            return Uri.TryCreate(file, UriKind.Absolute,
                out configUri) && (configUri.Scheme == Uri.UriSchemeHttp || configUri.Scheme == Uri.UriSchemeHttps);
        }

    }
}
