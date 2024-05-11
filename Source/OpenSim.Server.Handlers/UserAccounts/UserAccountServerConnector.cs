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
using OpenSim.Framework.ServiceAuth;
using OpenSim.Server.Handlers.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Autofac;

namespace OpenSim.Server.Handlers.UserAccounts
{
    public class UserAccountServiceConnector : IServiceConnector
    {
        private IUserAccountService m_UserAccountService;
        private static string _ConfigName = "UserAccountService";

        protected IConfiguration m_configuration;
        protected ILogger<UserAccountServiceConnector> m_logger;
        protected IComponentContext m_context;


        public UserAccountServiceConnector(
            IConfiguration config, 
            ILogger<UserAccountServiceConnector> logger,
            IComponentContext componentContext
            )
        {
            m_configuration = config;
            m_logger = logger;
            m_context = componentContext;
        }

        public string ConfigName {get; private set; } = _ConfigName;

        public IHttpServer HttpServer {get; private set; }

        public void Initialize(IHttpServer httpServer)
        {
            HttpServer = httpServer;

            var serverConfig = m_configuration.GetSection(ConfigName);
            if (serverConfig.Exists() is false)
                throw new Exception($"No section {ConfigName} in config file");

            string service = serverConfig.GetValue("LocalServiceModule", String.Empty);
            if (string.IsNullOrEmpty(service))
                throw new Exception("No LocalServiceModule in config file");

            m_UserAccountService = m_context.ResolveNamed<IUserAccountService>(service);

            IServiceAuth auth = ServiceAuth.Create(m_configuration, ConfigName);

            HttpServer.AddStreamHandler(new UserAccountServerPostHandler(m_logger, m_UserAccountService, serverConfig, auth));
        }
    }
}
