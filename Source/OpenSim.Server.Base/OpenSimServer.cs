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

using System.Text;
using System.Timers;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

using Timer = System.Timers.Timer;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.Servers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;


namespace OpenSim.Server.Base
{
    /// <summary>
    /// Common base for the main OpenSimServers (user, grid, inventory, region, etc)
    /// </summary>
    public class OpenSimServer
    {
        protected readonly IServiceProvider m_serviceProvider;
        protected readonly IConfiguration m_configuration;
        protected readonly ILogger<OpenSimServer> m_logger;


        protected ICommandConsole m_console;

        protected IHttpServer m_httpServer;
        
        protected ServerStatsCollector? m_serverStatsCollector;      // XXX Should be DI instantiated

        /// <summary>
        /// Random uuid for private data
        /// </summary>
        protected string m_osSecret = String.Empty;

        protected string m_startupDirectory = Environment.CurrentDirectory;

        protected DateTime m_startuptime;

        private static bool m_NoVerifyCertChain = false;

        private static bool m_NoVerifyCertHostname = false;

        private int m_periodDiagnosticTimerMS = 60 * 60 * 1000;

        /// <summary>
        /// This will control a periodic log printout of the current 'show stats' (if they are active) for this
        /// server.
        /// </summary>
        private Timer m_periodicDiagnosticsTimer = new Timer(60 * 60 * 1000);

        /// <summary>
        /// Server version information.  Usually VersionInfo + information about git commit, operating system, etc.
        /// </summary>
        private string m_version;

        /// <summary>
        /// Static reference to the base of information about the server.
        /// This exists only for the global reference to base services (configuration, logging, ...) from
        /// routines that have not been converted to DI (dependency injection).
        /// Eventually, everything will be converted to DI and this reference would not be needed.
        /// TODO: Someday, remove all references to this.
        /// </summary>
        public static OpenSimServer Instance { get; private set; }
        public IConfiguration Configuration { get { return m_configuration; } }
        public IServiceProvider ServiceProvider {  get { return m_serviceProvider; } }
        // END of global references for those poor, old, un-converted classes

        public OpenSimServer(
            IServiceProvider provider,
            IConfiguration configuration, 
            ILogger<OpenSimServer> logger,
            ICommandConsole console, 
            IHttpServer httpServer
            )
        {
            m_serviceProvider = provider;
            m_configuration = configuration;
            m_logger = logger;
            m_console = console;
            m_httpServer = httpServer;

            MainConsole.Instance = m_console;

            MainServer.AddHttpServer(m_httpServer);
            MainServer.Instance = m_httpServer;

            m_osSecret = UUID.Random().ToString();

            m_startuptime = DateTime.Now;

            m_version = VersionInfo.Version;

            Instance = this;
        }

        public ICommandConsole Console { get => m_console; set => m_console = value; }

        public IHttpServer HttpServer { get => m_httpServer; set => m_httpServer = value; }

        public string osSecret { get => m_osSecret; }            // Secret uuid for the simulator

        /// <summary>
        /// Used by tests to suppress Environment.Exit(0) so that post-run operations are possible.
        /// </summary>
        public bool SuppressExit { get; set; }

        public string Version { get => m_version; set => m_version = value; }

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

            if (Util.FireAndForgetMethod == FireAndForgetMethod.SmartThreadPool)
            {
                STPInfo stpi = Util.GetSmartThreadPoolInfo();

                // ROBUST currently leaves this the FireAndForgetMethod but never actually initializes the threadpool.
                if (stpi != null)
                {
                    threadPoolUsed = "SmartThreadPool";
                    maxThreads = stpi.MaxThreads;
                    minThreads = stpi.MinThreads;
                    inUseThreads = stpi.InUseThreads;
                    allocatedThreads = stpi.ActiveThreads;
                    waitingCallbacks = stpi.WaitingCallbacks;
                }
            }

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

        public static bool ValidateServerCertificate(
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

        public string GetVersionText()
        {
            return String.Format("Version: {0} (SIMULATION/{1} - SIMULATION/{2})",
                Version, VersionInfo.SimulationServiceVersionSupportedMin, VersionInfo.SimulationServiceVersionSupportedMax);
        }

        public virtual void HandleShow(string module, string[] cmd)
        {
            if (cmd.Length < 2)
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
        /// Log information about the circumstances in which we're running (OpenSimulator version number, CLR details,
        /// etc.).
        /// </summary>
        public void LogEnvironmentInformation()
        {
            m_logger.LogInformation($"Starting in {m_startupDirectory}");
            m_logger.LogInformation($"OpenSimulator version: {Version}");

            m_logger.LogInformation($"Virtual machine runtime version: {Environment.Version}");
            m_logger.LogInformation(
                $"Operating system version: {Environment.OSVersion}, " +
                $".NET platform {Util.RuntimePlatformStr}, " +
                $"{(Environment.Is64BitProcess ? "64" : "32")}-bit");
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

            m_console.Commands.AddCommand(
                "Debug", false, "debug threadpool set",
                "debug threadpool set worker|iocp min|max <n>",
                "Set threadpool parameters.  For debug purposes.",
                HandleDebugThreadpoolSet);

            m_console.Commands.AddCommand(
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

            m_console.Commands.AddCommand(
                "Debug", false, "show threadpool calls active",
                "show threadpool calls active",
                "Show details about threadpool calls that are still active (currently waiting or in progress)",
                HandleShowThreadpoolCallsActive);

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

        public void RegisterCommonComponents(IConfiguration configSource)
        {
            m_serverStatsCollector = null;
            //m_serverStatsCollector = new ServerStatsCollector();      // XXX
            //m_serverStatsCollector.Initialise(configSource);
            //m_serverStatsCollector.Start();
        }

        /// <summary>
        /// Performs initialization of the scene, such as loading configuration from disk.
        /// </summary>
        public virtual void Startup()
        {
            m_logger.LogInformation($"Beginning startup processing");
            m_logger.LogInformation($"Version: {Version}");
            m_logger.LogInformation($"Operating system version: {Environment.OSVersion}, .NET platform {Util.RuntimePlatformStr}, Runtime {Environment.Version}");
            m_logger.LogInformation(
                $"Processor Architecture: {RuntimeInformation.ProcessArchitecture} " +
                $"({(BitConverter.IsLittleEndian ? "le" : "be")} " +
                $"{(Environment.Is64BitProcess ? "64" : "32")}bit)");

            var startupConfig = m_configuration.GetSection("Startup");
            
            try
            {
                StatsManager.SimExtraStats = new SimExtraStatsCollector();

                RegisterCommonCommands();
                RegisterCommonComponents(m_configuration);

                m_NoVerifyCertChain = startupConfig.GetValue<bool>("NoVerifyCertChain", m_NoVerifyCertChain);
                m_NoVerifyCertHostname = startupConfig.GetValue<bool>("NoVerifyCertHostname", m_NoVerifyCertHostname);

                ServicePointManager.ServerCertificateValidationCallback = ValidateServerCertificate;

                WebUtil.SetupHTTPClients(m_NoVerifyCertChain, m_NoVerifyCertHostname, null, 32);

                int logShowStatsSeconds = startupConfig.GetValue<int>("LogShowStatsSeconds", m_periodDiagnosticTimerMS / 1000);

                m_periodDiagnosticTimerMS = logShowStatsSeconds * 1000;
                m_periodicDiagnosticsTimer.Elapsed += new ElapsedEventHandler(LogDiagnostics);

                if (m_periodDiagnosticTimerMS != 0)
                {
                    m_periodicDiagnosticsTimer.Interval = m_periodDiagnosticTimerMS;
                    m_periodicDiagnosticsTimer.Enabled = true;
                }
            }
            catch (Exception e)
            {
                m_logger.LogError(e, "Fatal error");
                Environment.Exit(1);
            }
        }

        public string StatReport(IOSHttpRequest httpRequest)
        {
            httpRequest.QueryAsDictionary.TryGetValue("region", out string id);

            // If we catch a request for "callback", wrap the response in the value for jsonp
            if (httpRequest.QueryAsDictionary.TryGetValue("callback", out string cb) && !string.IsNullOrEmpty(cb))
            {
                return cb + "(" + StatsManager.SimExtraStats.XReport((DateTime.Now - m_startuptime).ToString(), Version, id) + ");";
            }
            else
            {
                return StatsManager.SimExtraStats.XReport((DateTime.Now - m_startuptime).ToString(), Version, id);
            }
        }

        /// <summary>
        /// Provides a list of help topics that are available.  Overriding classes should append their topics to the
        /// information returned when the base method is called.
        /// </summary>
        ///
        /// <returns>
        /// A list of strings that represent different help topics on which more information is available
        /// </returns>
        protected virtual List<string> GetHelpTopics()
        { return new List<string>(); }

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
        /// Print statistics to the logfile, if they are active
        /// </summary>
        protected void LogDiagnostics(object source, ElapsedEventArgs e)
        {
            StringBuilder sb = new StringBuilder("DIAGNOSTICS\n\n");
            sb.Append(GetUptimeReport());
            sb.Append(StatsManager.SimExtraStats.Report());
            sb.Append(Environment.NewLine);
            sb.Append(GetThreadsReport());

            m_logger.LogDebug(sb.ToString());
        }

        /// <summary>
        /// Console output is only possible if a console has been established.
        /// That is something that cannot be determined within this class. So
        /// all attempts to use the console MUST be verified.
        /// </summary>
        /// <param name="msg"></param>
        protected void Notice(string msg)
        {
            m_console?.Output(msg);
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
            m_console?.Output(format, components);
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
                m_logger.LogInformation($"[SERVER BASE]: Running {fileName}");

                using (StreamReader readFile = File.OpenText(fileName))
                {
                    string currentCommand;
                    while ((currentCommand = readFile.ReadLine()) != null)
                    {
                        currentCommand = currentCommand.Trim();
                        if (!(string.IsNullOrEmpty(currentCommand) 
                            || currentCommand.StartsWith(";")
                            || currentCommand.StartsWith("//")
                            || currentCommand.StartsWith("#")))
                        {
                            m_logger.LogInformation($"[SERVER BASE]: Running '{currentCommand}'");
                            m_console.RunCommand(currentCommand);
                        }
                    }
                }
            }
        }

        protected void ShowInfo()
        {
            Notice(GetVersionText());
            Notice("Startup directory: " + m_startupDirectory);
        }

        protected void Shutdown()
        {
            Watchdog.Enabled = false;

            if (m_serverStatsCollector != null)
            {
                m_serverStatsCollector.Close();
            }

            MainServer.Stop();

            Thread.Sleep(500);
            Util.StopThreadPool();

            WorkManager.Stop();

            m_logger.LogInformation("[SHUTDOWN]: Shutdown processing on main thread complete.  Exiting...");

            if (!SuppressExit)
                Environment.Exit(0);
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

        private void HandleDebugThreadpoolSet(string module, string[] args)
        {
            if (args.Length != 6)
            {
                Notice("Usage: debug threadpool set worker|iocp min|max <n>");
                return;
            }

            int newThreads;

            if (!ConsoleUtil.TryParseConsoleInt(m_console, args[5], out newThreads))
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
                int minWorkerThreads, maxWorkerThreads, minIocpThreads, maxIocpThreads;

                ThreadPool.GetMinThreads(out minWorkerThreads, out minIocpThreads);
                ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxIocpThreads);

                Notice("Min worker threads now {0}", minWorkerThreads);
                Notice("Min IOCP threads now {0}", minIocpThreads);
                Notice("Max worker threads now {0}", maxWorkerThreads);
                Notice("Max IOCP threads now {0}", maxIocpThreads);
            }
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

        private void HandleForceGc(string module, string[] args)
        {
            Notice("Manually invoking runtime garbage collection");
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
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
    }
}