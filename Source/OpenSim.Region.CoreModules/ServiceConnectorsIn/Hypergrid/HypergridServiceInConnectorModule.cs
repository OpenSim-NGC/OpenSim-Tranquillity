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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Hypergrid;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.ServiceConnectorsIn.Hypergrid
{
    public class HypergridServiceInConnectorModule : ISharedRegionModule
    {
        private static ILogger? m_logger;
        private static bool m_Enabled = false;
        private static IConfiguration m_Config = null;

        private bool m_Registered = false;
        private string m_LocalServiceDll = string.Empty;
        private GatekeeperServiceInConnector m_HypergridHandler;
        private UserAgentServerConnector m_UASHandler;

        #region Region Module interface

        public void Initialise(IConfiguration config)
        {
            m_logger ??= OpenSimServer.Instance.ServiceProvider.GetRequiredService<ILogger<HypergridServiceInConnectorModule>>();
            m_Config = config;

            IConfigurationSection moduleConfig = m_Config.GetSection("Modules");
            m_Enabled = moduleConfig.GetValue<bool>("HypergridServiceInConnector", false);
            if (m_Enabled)
            {
                m_logger?.LogInformation("[HGGRID IN CONNECTOR]: Hypergrid Service In Connector enabled");

                IConfigurationSection fconfig = m_Config.GetSection("FriendsService");
                m_LocalServiceDll = fconfig.GetValue<string>("LocalServiceModule", m_LocalServiceDll);
                if (String.IsNullOrEmpty(m_LocalServiceDll))
                    m_logger?.LogWarning("[HGGRID IN CONNECTOR]: Friends LocalServiceModule config missing");
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "HypergridService"; }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            if (!m_Registered)
            {
                m_Registered = true;

                m_logger?.LogInformation("[HypergridService]: Starting...");

                ISimulationService simService = scene.RequestModuleInterface<ISimulationService>();
                IFriendsSimConnector friendsConn = scene.RequestModuleInterface<IFriendsSimConnector>();
                object[] args = new object[] { m_Config };
//                IFriendsService friendsService = ServerUtils.LoadPlugin<IFriendsService>(m_LocalServiceDll, args)
                ServerUtils.LoadPlugin<IFriendsService>(m_LocalServiceDll, args);

                m_HypergridHandler = new GatekeeperServiceInConnector(m_Config, MainServer.Instance, simService);

                m_UASHandler = new UserAgentServerConnector(m_Config, MainServer.Instance, friendsConn);

                new HeloServiceInConnector(m_Config, MainServer.Instance, "HeloService");

                new HGFriendsServerConnector(m_Config, MainServer.Instance, "HGFriendsService", friendsConn);
            }
            scene.RegisterModuleInterface<IGatekeeperService>(m_HypergridHandler.GateKeeper);
            scene.RegisterModuleInterface<IUserAgentService>(m_UASHandler.HomeUsersService);
        }

        #endregion

    }
}
