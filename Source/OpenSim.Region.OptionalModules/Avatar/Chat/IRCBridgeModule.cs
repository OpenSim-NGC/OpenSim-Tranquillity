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

using System.Collections;
using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;

using Nwc.XmlRpc;

namespace OpenSim.Region.OptionalModules.Avatar.Chat
{
    public class IRCBridgeModule : INonSharedRegionModule
    {
        private static ILogger? m_logger;

        internal static bool Enabled = false;
        internal static IConfigurationSection m_config = null;

        internal static List<ChannelState> m_channels = new List<ChannelState>();
        internal static List<RegionState> m_regions = new List<RegionState>();

        internal static string m_password = String.Empty;
        internal RegionState m_region = null;

        #region INonSharedRegionModule Members

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "IRCBridgeModule"; }
        }

        public void Initialise(IConfiguration config)
        {
            m_logger ??= OpenSimServer.Instance.ServiceProvider.GetRequiredService<ILogger<IRCBridgeModule>>();

            m_config = config.GetSection("IRC");
            Enabled = m_config.GetValue<bool>("enabled", false);

            if (!Enabled)
            {
                //                m_logger?.LogInformation("[IRC-Bridge] module disabled in configuration");
                m_config = null;
                return;
            }

            var remoteAdmin = config.GetSection("RemoteAdmin");
            m_password = remoteAdmin.GetValue<string>("access_password", m_password);

            m_logger?.LogInformation("[IRC-Bridge]: Module is enabled");
        }

        public void AddRegion(Scene scene)
        {
            if (Enabled)
            {
                try
                {
                    m_logger?.LogInformation("[IRC-Bridge] Connecting region {0}", scene.RegionInfo.RegionName);

                    if (!String.IsNullOrEmpty(m_password))
                        MainServer.Instance.AddXmlRPCHandler("irc_admin", XmlRpcAdminMethod, false);

                    m_region = new RegionState(scene, m_config);
                    lock (m_regions)
                        m_regions.Add(m_region);
                    m_region.Open();
                }
                catch (Exception e)
                {
                    m_logger?.LogWarning("[IRC-Bridge] Region {0} not connected to IRC : {1}", scene.RegionInfo.RegionName, e.Message);
                    m_logger?.LogDebug("{0}", e);
                }
            }
            else
            {
                //m_log.DebugFormat("[IRC-Bridge] Not enabled. Connect for region {0} ignored", scene.RegionInfo.RegionName);
            }
        }


        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Enabled)
                return;

            if (m_region == null)
                return;

            if (!String.IsNullOrEmpty(m_password))
                MainServer.Instance.RemoveXmlRPCHandler("irc_admin");

            m_region.Close();

            if (m_regions.Contains(m_region))
            {
                lock (m_regions) m_regions.Remove(m_region);
            }
        }

        public void Close()
        {
        }
        #endregion

        public static XmlRpcResponse XmlRpcAdminMethod(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            m_logger?.LogDebug("[IRC-Bridge]: XML RPC Admin Entry");

            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            try
            {
                Hashtable requestData = (Hashtable)request.Params[0];
                bool found = false;
                string region = String.Empty;

                if (m_password != String.Empty)
                {
                    if (!requestData.ContainsKey("password"))
                        throw new Exception("Invalid request");
                    if ((string)requestData["password"] != m_password)
                        throw new Exception("Invalid request");
                }

                if (!requestData.ContainsKey("region"))
                    throw new Exception("No region name specified");
                region = (string)requestData["region"];

                foreach (RegionState rs in m_regions)
                {
                    if (rs.Region == region)
                    {
                        responseData["server"] = rs.cs.Server;
                        responseData["port"] = (int)rs.cs.Port;
                        responseData["user"] = rs.cs.User;
                        responseData["channel"] = rs.cs.IrcChannel;
                        responseData["enabled"] = rs.cs.irc.Enabled;
                        responseData["connected"] = rs.cs.irc.Connected;
                        responseData["nickname"] = rs.cs.irc.Nick;
                        found = true;
                        break;
                    }
                }

                if (!found) throw new Exception(String.Format("Region <{0}> not found", region));

                responseData["success"] = true;
            }
            catch (Exception e)
            {
                m_logger?.LogError("[IRC-Bridge] XML RPC Admin request failed : {0}", e.Message);

                responseData["success"] = "false";
                responseData["error"] = e.Message;
            }
            finally
            {
                response.Value = responseData;
            }

            m_logger?.LogDebug("[IRC-Bridge]: XML RPC Admin Exit");

            return response;
        }
    }
}
