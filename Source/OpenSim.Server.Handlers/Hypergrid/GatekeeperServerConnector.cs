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

using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Autofac;
using System.ComponentModel;

namespace OpenSim.Server.Handlers.Hypergrid
{
    public class GatekeeperServiceInConnector : IServiceConnector
    {
        private IGatekeeperService m_GatekeeperService = null;
        private bool m_Proxy = false;

                
        protected readonly IConfiguration m_configuration;
        protected readonly ILogger<GatekeeperServiceInConnector> m_logger;
        protected readonly IComponentContext m_context;

        public GatekeeperServiceInConnector(
            IConfiguration config,
            ILogger<GatekeeperServiceInConnector> logger,
            IComponentContext componentContext)
        {
            m_configuration = config;
            m_logger = logger;
            m_context = componentContext;
        }

        public IGatekeeperService GateKeeper
        {
            get { return m_GatekeeperService; }
        }

        public string ConfigName { get; private set; } = "GatekeeperService";

        public IHttpServer HttpServer { get; private set; }

        public void Initialize(IHttpServer httpServer)
        {
            HttpServer = httpServer;

            var gridConfig = m_configuration.GetSection(ConfigName);
            if (gridConfig.Exists() is false)
            {
                string? service = gridConfig.GetValue("LocalServiceModule", string.Empty);
                if (string.IsNullOrWhiteSpace(service))
                    throw new Exception("No LocalServiceModule in config file");

                m_GatekeeperService = m_context.ResolveNamed<IGatekeeperService>(service);
            }

            if (m_GatekeeperService == null)
                throw new Exception("Gatekeeper server connector cannot proceed because of missing service");

            m_Proxy = gridConfig.GetValue<bool>("HasProxy", false);

            HypergridHandlers hghandlers = new HypergridHandlers(m_logger, m_GatekeeperService);

            HttpServer.AddXmlRPCHandler("link_region", hghandlers.LinkRegionRequest, false);
            HttpServer.AddXmlRPCHandler("get_region", hghandlers.GetRegion, false);

            HttpServer.AddSimpleStreamHandler(new GatekeeperAgentHandler(m_logger, m_GatekeeperService, m_Proxy),true);
        }

    }
}
