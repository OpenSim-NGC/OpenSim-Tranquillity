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

using System;
using System.Linq;
using System.Reflection;
using System.Timers;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Timer=System.Timers.Timer;
using Mono.Addins;
using OpenMetaverse.StructuredData;

namespace OpenSim.Region.CoreModules.World.Region
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RestartModule")]
    public class RestartModule : INonSharedRegionModule, IRestartModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Scene m_Scene;
        protected Timer m_CountdownTimer = null;
        protected DateTime m_RestartBegin;
        protected List<int> m_Alerts;
        protected UUID m_Initiator;
        protected IDialogModule m_DialogModule = null;
        protected string m_MarkerPath = String.Empty;
        private int[] m_CurrentAlerts = null;
        protected bool m_shortCircuitDelays = false;
        protected bool m_rebootAll = false;

        public void Initialise(IConfigSource config)
        {
            IConfig restartConfig = config.Configs["RestartModule"];
            if (restartConfig != null)
            {
                m_MarkerPath = restartConfig.GetString("MarkerPath", String.Empty);
            }
            IConfig startupConfig = config.Configs["Startup"];
            m_shortCircuitDelays = startupConfig.GetBoolean("SkipDelayOnEmptyRegion", false);
            m_rebootAll = startupConfig.GetBoolean("InworldRestartShutsDown", false);
        }

        public void AddRegion(Scene scene)
        {
            if (m_MarkerPath != String.Empty)
                File.Delete(Path.Combine(m_MarkerPath,
                        scene.RegionInfo.RegionID.ToString()));

            m_Scene = scene;

            scene.RegisterModuleInterface<IRestartModule>(this);

            MainConsole.Instance.Commands.AddCommand("Regions",
                    false, "region restart",
                    "region restart <delta seconds>",
                    "Schedule a region restart",
                    "Schedule a region restart after a given number of seconds.  The region is restarted in delta seconds time.",
                    HandleRegionRestart);

            MainConsole.Instance.Commands.AddCommand("Regions",
                    false, "region restart abort",
                    "region restart abort [<message>]",
                    "Abort a region restart", HandleRegionRestart);
        }

        public void RegionLoaded(Scene scene)
        {
            m_DialogModule = m_Scene.RequestModuleInterface<IDialogModule>();
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "RestartModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return typeof(IRestartModule); }
        }

        public TimeSpan TimeUntilRestart
        {
            get { return DateTime.Now - m_RestartBegin; }
        }

        public void ScheduleRestart(UUID initiator, int seconds)
        {
            if (m_CountdownTimer != null)
            {
                m_CountdownTimer.Stop();
                m_CountdownTimer = null;
            }

            if (seconds == 0)
            {
                CreateMarkerFile();
                m_Scene.RestartNow();
                return;
            }

            if (m_Scene.GetScenePresences().Count == 0)
            {
                m_log.InfoFormat("No avatars in region {0}, restarting now...", m_Scene.Name);

                CreateMarkerFile();
                m_Scene.RestartNow();
                return;
            }

            List<int> times = new List<int>();
            while (seconds > 0)
            {
                times.Add(seconds);
                if (seconds > 300)
                    seconds -= 120;
                else if (seconds > 30)
                    seconds -= 30;
                else
                    seconds -= 15;
            }

            times.Sort();
            times.Reverse();

            int[] alerts = times.ToArray();
			
            m_Initiator = initiator;
            m_CurrentAlerts = alerts;
            m_Alerts = new List<int>(alerts);
            m_Alerts.Sort();
            m_Alerts.Reverse();

            if (m_Alerts[0] == 0)
            {
                CreateMarkerFile();
                m_Scene.RestartNow();
                return;
            }

            int nextInterval = DoOneNotice(true);

            SetTimer(nextInterval);
        }

        public int DoOneNotice(bool sendOut)
        {
            if (m_Alerts.Count == 0 || m_Alerts[0] == 0)
            {
                CreateMarkerFile();
                m_Scene.RestartNow();
                return 0;
            }

            int nextAlert = 0;
            while (m_Alerts.Count > 1)
            {
                if (m_Alerts[1] == m_Alerts[0])
                {
                    m_Alerts.RemoveAt(0);
                    continue;
                }
                nextAlert = m_Alerts[1];
                break;
            }

            int currentAlert = m_Alerts[0];

            m_Alerts.RemoveAt(0);

            if (sendOut)
            {
                string msg_id = "RegionRestartSeconds";

                OSDMap osd = new OSDMap();

                osd.Add("NAME", m_Scene.RegionInfo.RegionName);
                osd.Add("SECONDS", currentAlert);

                byte[] extra = Util.StringToBytes256(OSDParser.SerializeLLSDXmlString(osd));

                m_Scene.ForEachRootClient(delegate (IClientAPI client)
                {
                    client.SendAlertMessage(msg_id, msg_id, extra);
                });

                m_log.InfoFormat("{0} will restart in {1} seconds", m_Scene.Name, currentAlert);
            }

            return currentAlert - nextAlert;
        }

        public void SetTimer(int intervalSeconds)
        {
            if (intervalSeconds > 0)
            {
                m_CountdownTimer = new Timer();
                m_CountdownTimer.AutoReset = false;
                m_CountdownTimer.Interval = intervalSeconds * 1000;
                m_CountdownTimer.Elapsed += OnTimer;
                m_CountdownTimer.Start();
            }
            else if (m_CountdownTimer != null)
            {
                m_CountdownTimer.Stop();
                m_CountdownTimer = null;
            }
            else
            {
                m_log.WarnFormat(
                    "[RESTART MODULE]: Tried to set restart timer to {0} in {1}, which is not a valid interval",
                    intervalSeconds, m_Scene.Name);
            }
        }

        private void OnTimer(object source, ElapsedEventArgs e)
        {
            int nextInterval = DoOneNotice(true);
            if (m_shortCircuitDelays)
            {
                if (CountAgents() == 0)
                {
                    m_Scene.RestartNow();
                    return;
                }
            }

            SetTimer(nextInterval);
        }

        public void DelayRestart(int seconds, string message)
        {
            if (m_CountdownTimer == null)
                return;

            MainConsole.Instance.Output("Region restart delayed for " + seconds.ToString() + " seconds");
            
            if (m_DialogModule != null)
                m_DialogModule.SendNotificationToUsersInRegion(UUID.Zero, "System", "Region restart has been delayed.");

            m_CountdownTimer.Stop();
            m_CountdownTimer = null;

            m_Alerts = new List<int>(m_CurrentAlerts);
            m_Alerts.Add(seconds);
            m_Alerts.Sort();
            m_Alerts.Reverse();

            int nextInterval = DoOneNotice(false);

            SetTimer(nextInterval);
        }

        public void AbortRestart(string message)
        {
            if (m_CountdownTimer != null)
            {
                m_CountdownTimer.Stop();
                m_CountdownTimer = null;
                if (m_DialogModule != null && message != String.Empty)
                    m_DialogModule.SendNotificationToUsersInRegion(UUID.Zero, "System", message);
                    //m_DialogModule.SendGeneralAlert(message);
            }
            if (m_MarkerPath != String.Empty)
                File.Delete(Path.Combine(m_MarkerPath,
                        m_Scene.RegionInfo.RegionID.ToString()));
        }

        private void HandleRegionRestart(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene is Scene))
                return;

            if (MainConsole.Instance.ConsoleScene != m_Scene)
                return;

            int seconds = 120;
			
			if (args.Length >= 3)
            {
                if (args[2] == "abort")
                {
					string msg = String.Empty;
					if (args.Length > 3)
						msg = args[3];

					AbortRestart(msg);

					MainConsole.Instance.Output("Region restart aborted");
					return;
                }
				else if (!int.TryParse(args[2], out seconds))
                {
                    MainConsole.Instance.Output("Error: restart region <abort/delta seconds>");
                    return;
				}
            }

            MainConsole.Instance.Output(
                "Region {0} scheduled for restart in {1} seconds", m_Scene.Name, seconds);

            ScheduleRestart(UUID.Zero, seconds);
        }

        protected void CreateMarkerFile()
        {
            if (m_MarkerPath.Length == 0)
                return;

            string path = Path.Combine(m_MarkerPath, m_Scene.RegionInfo.RegionID.ToString());
            try
            {
                string pidstring = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                FileStream fs = File.Create(path);
                System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
                Byte[] buf = enc.GetBytes(pidstring);
                fs.Write(buf, 0, buf.Length);
                fs.Close();
            }
            catch (Exception)
            {
            }
        }

        int CountAgents()
        {
            m_log.Info("[RESTART MODULE]: Counting affected avatars");
            int agents = 0;

            if (m_rebootAll)
            {
                foreach (Scene s in SceneManager.Instance.Scenes)
                {
                    foreach (ScenePresence sp in s.GetScenePresences())
                    {
                        if (!sp.IsChildAgent && !sp.IsNPC)
                            agents++;
                    }
                }
            }
            else
            {
                foreach (ScenePresence sp in m_Scene.GetScenePresences())
                {
                    if (!sp.IsChildAgent && !sp.IsNPC)
                        agents++;
                }
            }

            m_log.InfoFormat("[RESTART MODULE]: Avatars in region: {0}", agents);

            return agents;
        }
    }
}
