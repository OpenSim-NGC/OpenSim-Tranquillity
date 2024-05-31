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

using System.Net;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenSim.Framework;
using OpenSim.Server.Base;

using Nini.Config;

namespace OpenSim.Server.RegionServer
{
    /// <summary>
    /// Starting class for the OpenSimulator Region
    /// </summary>
    public static class Application
    {
        /// <summary>
        /// Path to the main ini Configuration file
        /// </summary>
        public static string iniFilePath = "";

        /// <summary>
        /// Directory to save crash reports to.  Relative to bin/
        /// </summary>
        public static string m_crashDir = "crashes";

        /// <summary>
        /// Save Crashes in the bin/crashes folder.  Configurable with m_crashDir
        /// </summary>
        public static bool m_saveCrashDumps = false;

        /// <summary>
        /// Text Console Logger
        /// </summary>
        private static ILogger? m_logger;

        private static bool _IsHandlingException = false;

        public static IServiceProvider ServiceProvider { get; set; }

        public static ConfigurationLoader ConfigLoader { get; set; }

        public static IConfiguration Configuration { get; set; }
        
        public static void Configure(string[] args)
        {
            m_logger ??= OpenSimServer.Instance.ServiceProvider.GetRequiredService<ILogger>();
            // Add the arguments supplied when running the application to the configuration
            var configSource = new ArgvConfigSource(args);

            // Configure Log4Net
            configSource.AddSwitch("Startup", "logconfig");

            string logConfigFile = configSource.Configs["Startup"].GetString("logconfig", String.Empty);

            if (!string.IsNullOrEmpty(logConfigFile))
            {
                XmlConfigurator.Configure(new System.IO.FileInfo(logConfigFile));
                m_logger?.LogInformation("[OPENSIM MAIN]: configured log4net using \"{0}\" as configuration file", logConfigFile);
            }
            else
            {
                XmlConfigurator.Configure(new System.IO.FileInfo("OpenSim.exe.config"));
                m_logger?.LogInformation("[OPENSIM MAIN]: configured log4net using default OpenSim.exe.config");
            }

            m_logger?.LogInformation("[OPENSIM MAIN]: System Locale is {0}", System.Threading.Thread.CurrentThread.CurrentCulture);

            configSource.Alias.AddAlias("On", true);
            configSource.Alias.AddAlias("Off", false);
            configSource.Alias.AddAlias("True", true);
            configSource.Alias.AddAlias("False", false);
            configSource.Alias.AddAlias("Yes", true);
            configSource.Alias.AddAlias("No", false);

            configSource.AddSwitch("Startup", "inifile");
            configSource.AddSwitch("Startup", "inimaster");
            configSource.AddSwitch("Startup", "inidirectory");
            configSource.AddSwitch("Startup", "physics");
            configSource.AddSwitch("Startup", "gui");
            configSource.AddSwitch("Startup", "console");
            configSource.AddSwitch("Startup", "save_crashes");
            configSource.AddSwitch("Startup", "crash_dir");

            configSource.AddConfig("StandAlone");
            configSource.AddConfig("Network");

            // Check if we're saving crashes
            m_saveCrashDumps = configSource.Configs["Startup"].GetBoolean("save_crashes", false);

            // load Crash directory config
            m_crashDir = configSource.Configs["Startup"].GetString("crash_dir", m_crashDir);

            ConfigLoader = new ConfigurationLoader();
            Configuration = ConfigLoader.LoadConfigSettings(configSource);
        }

        //could move our main function into OpenSimMain and kill this class
        public static void Start()
        {
            // First line, hook the appdomain to the crash reporter
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            System.AppContext.SetSwitch("System.Drawing.EnableUnixSupport", true);

            Culture.SetCurrentCulture();
            Culture.SetDefaultCurrentCulture();

            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.MaxServicePointIdleTime = 30000;

            try { ServicePointManager.DnsRefreshTimeout = 5000; } catch { }
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            // Verify the Threadpool allocates or uses enough worker and IO completion threads
            // .NET 2.0, workerthreads default to 50 *  numcores
            // .NET 3.0, workerthreads defaults to 250 * numcores
            // .NET 4.0, workerthreads are dynamic based on bitness and OS resources
            // Max IO Completion threads are 1000 on all 3 CLRs
            //
            // Mono 2.10.9 to at least Mono 3.1, workerthreads default to 100 * numcores, iocp threads to 4 * numcores
            int workerThreadsMin = 500;
            int workerThreadsMax = 1000; // may need further adjustment to match other CLR
            int iocpThreadsMin = 1000;
            int iocpThreadsMax = 2000; // may need further adjustment to match other CLR

            System.Threading.ThreadPool.GetMinThreads(out int currentMinWorkerThreads, out int currentMinIocpThreads);
            m_logger?.LogInformation(
                "[OPENSIM MAIN]: Runtime gave us {0} min worker threads and {1} min IOCP threads",
                currentMinWorkerThreads, currentMinIocpThreads);

            System.Threading.ThreadPool.GetMaxThreads(out int workerThreads, out int iocpThreads);
            m_logger?.LogInformation("[OPENSIM MAIN]: Runtime gave us {0} max worker threads and {1} max IOCP threads", workerThreads, iocpThreads);

            if (workerThreads < workerThreadsMin)
            {
                workerThreads = workerThreadsMin;
                m_logger?.LogInformation("[OPENSIM MAIN]: Bumping up max worker threads to {0}", workerThreads);
            }
            if (workerThreads > workerThreadsMax)
            {
                workerThreads = workerThreadsMax;
                m_logger?.LogInformation("[OPENSIM MAIN]: Limiting max worker threads to {0}", workerThreads);
            }

            // Increase the number of IOCP threads available.
            // Mono defaults to a tragically low number (24 on 6-core / 8GB Fedora 17)
            if (iocpThreads < iocpThreadsMin)
            {
                iocpThreads = iocpThreadsMin;
                m_logger?.LogInformation("[OPENSIM MAIN]: Bumping up max IOCP threads to {0}", iocpThreads);
            }
            // Make sure we don't overallocate IOCP threads and thrash system resources
            if (iocpThreads > iocpThreadsMax)
            {
                iocpThreads = iocpThreadsMax;
                m_logger?.LogInformation("[OPENSIM MAIN]: Limiting max IOCP completion threads to {0}", iocpThreads);
            }
            // set the resulting worker and IO completion thread counts back to ThreadPool
            if (System.Threading.ThreadPool.SetMaxThreads(workerThreads, iocpThreads))
            {
                m_logger?.LogInformation(
                    "[OPENSIM MAIN]: Threadpool set to {0} max worker threads and {1} max IOCP threads",
                    workerThreads, iocpThreads);
            }
            else
            {
                m_logger?.LogWarning("[OPENSIM MAIN]: Threadpool reconfiguration failed, runtime defaults still in effect.");
            }

            // Check if the system is compatible with OpenSimulator.
            // Ensures that the minimum system requirements are met
            string supported = String.Empty;
            if (Util.IsEnvironmentSupported(ref supported))
            {
                m_logger?.LogInformation("[OPENSIM MAIN]: Environment is supported by OpenSimulator.");
            }
            else
            {
                m_logger?.LogWarning("[OPENSIM MAIN]: Environment is not supported by OpenSimulator (" + supported + ")\n");
            }

            /* Start Up the Simulator */
            var m_sim = new OpenSim(Configuration);
            m_sim.Startup();

            while (true)
            {
                try
                {
                    // Block thread here for input
                    MainConsole.Instance.Prompt();
                }
                catch (Exception e)
                {
                    m_logger?.LogError("Command error: {0}", e);
                }
            }
        }

        // Make sure we don't go recursive on ourself

        /// <summary>
        /// Global exception handler -- all unhandlet exceptions end up here :)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (_IsHandlingException)
            {
                return;
            }

            _IsHandlingException = true;
            // TODO: Add config option to allow users to turn off error reporting
            // TODO: Post error report (disabled for now)

            string msg = String.Empty;
            msg += "\r\n";
            msg += "APPLICATION EXCEPTION DETECTED: " + e.ToString() + "\r\n";
            msg += "\r\n";

            msg += "Exception: " + e.ExceptionObject.ToString() + "\r\n";
            Exception ex = (Exception)e.ExceptionObject;
            if (ex.InnerException != null)
            {
                msg += "InnerException: " + ex.InnerException.ToString() + "\r\n";
            }

            msg += "\r\n";
            msg += "Application is terminating: " + e.IsTerminating.ToString() + "\r\n";

            m_logger?.LogError("[APPLICATION]: {0}", msg);

            if (m_saveCrashDumps)
            {
                // Log exception to disk
                try
                {
                    if (!Directory.Exists(m_crashDir))
                    {
                        Directory.CreateDirectory(m_crashDir);
                    }
                    string log = Util.GetUniqueFilename(ex.GetType() + ".txt");
                    using (StreamWriter m_crashLog = new StreamWriter(Path.Combine(m_crashDir, log)))
                    {
                        m_crashLog.WriteLine(msg);
                    }

                    File.Copy("OpenSim.ini", Path.Combine(m_crashDir, log + "_OpenSim.ini"), true);
                }
                catch (Exception e2)
                {
                    m_logger?.LogError("[CRASH LOGGER CRASHED]: {0}", e2);
                }
            }

            _IsHandlingException = false;
        }
    }
}