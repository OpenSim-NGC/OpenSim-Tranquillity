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

using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;

using OpenMetaverse;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Autofac;


namespace OpenSim.Capabilities.Handlers
{
    public class FetchInvDescServerConnector : IServiceConnector
    {
        private IInventoryService m_InventoryService;
        private ILibraryService m_LibraryService;
        private const string _ConfigName = "CapsService";
        
        protected readonly IConfiguration m_configuration;
        protected readonly ILogger<FetchInvDescServerConnector> m_logger;
        protected readonly IComponentContext m_context;
        
        public FetchInvDescServerConnector(
            IConfiguration config,
            ILogger<FetchInvDescServerConnector> logger,
            IComponentContext componentContext)
        {
            m_configuration = config;
            m_logger = logger;
            m_context = componentContext;
        }

        public string ConfigName { get; } = _ConfigName;

        public IHttpServer HttpServer { get; private set; }

        public void Initialize(IHttpServer httpServer)
        {
            HttpServer = httpServer;

            var serverConfig = m_configuration.GetSection(ConfigName);
            if (serverConfig.Exists() is false)
                throw new Exception($"No section '{ConfigName}' in config file");

            string invService = serverConfig.GetValue("InventoryService", String.Empty);

            if (string.IsNullOrEmpty(invService))
                throw new Exception("No InventoryService in config file");

            m_InventoryService = m_context.ResolveNamed<IInventoryService>(invService);
            if (m_InventoryService == null)
                throw new Exception($"Failed to load InventoryService from {invService}; config is {ConfigName}");

            string libService = serverConfig.GetValue("LibraryService", String.Empty);
            m_LibraryService = m_context.ResolveNamed<ILibraryService>(libService);

            ExpiringKey<UUID> m_badRequests = new ExpiringKey<UUID>(30000);

            FetchInvDescHandler webFetchHandler = new FetchInvDescHandler(m_logger, m_InventoryService, m_LibraryService, null);
            ISimpleStreamHandler reqHandler
                = new SimpleStreamHandler("/CAPS/WebFetchInvDesc/", delegate(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                { 
                    webFetchHandler.FetchInventoryDescendentsRequest(httpRequest, httpResponse, m_badRequests);
                });

            HttpServer.AddSimpleStreamHandler(reqHandler);
        }
    }
}
