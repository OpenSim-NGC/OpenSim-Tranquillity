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
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autofac;

namespace OpenSim.Server.Handlers.Login
{
    public class LLLoginServiceInConnector : IServiceConnector
    {
        private ILoginService m_LoginService;
        private IScene m_Scene;
        private bool m_Proxy;
        private BasicDosProtectorOptions m_DosProtectionOptions;
        private static string _configName = "LoginService";

        protected IConfiguration m_configuration;
        protected ILogger<LLLoginServiceInConnector> m_logger;
        protected IComponentContext m_context;

        public LLLoginServiceInConnector(
            IConfiguration configuration,
            ILogger<LLLoginServiceInConnector> logger,
            IComponentContext componentContext,
            IScene scene = null
            )
        {
            m_configuration = configuration;
            m_logger = logger;
            m_context = componentContext;
            m_Scene = scene;
        }

        public string ConfigName { get; private set; } = _configName;
        public IHttpServer HttpServer { get; private set; } 

        public void Initialize(IHttpServer httpServer)
        {
            HttpServer = httpServer;

            m_logger.LogDebug($"Starting...");
            string loginService = ReadLocalServiceFromConfig(m_configuration, ConfigName);

            // ISimulationService simService = null;
            // ILibraryService libService  = null;

            // Object[] args = null;

            // if (m_Scene != null)
            // {
            //     simService = m_Scene.RequestModuleInterface<ISimulationService>();
            //     libService = m_Scene.RequestModuleInterface<ILibraryService>();
            //     args = new Object[] { Config, simService, libService };
            // }
            // else
            // {
            //     args = new Object[] { Config };
            // }

            m_LoginService = m_context.ResolveNamed<ILoginService>(loginService);

            InitializeHandlers(HttpServer);
        }

        private string ReadLocalServiceFromConfig(IConfiguration config, string configName)
        {
            var serverConfig = config.GetSection(configName);
            if (serverConfig.Exists() is false)
                throw new Exception($"No section LoginService in config file");

            string loginService = serverConfig.GetValue("LocalServiceModule", String.Empty);
            if (string.IsNullOrEmpty(loginService))
                throw new Exception(String.Format("No LocalServiceModule for LoginService in config file"));

            m_Proxy = serverConfig.GetValue<bool>("HasProxy", false);
            m_DosProtectionOptions = new BasicDosProtectorOptions();

            // Dos Protection Options
            m_DosProtectionOptions.AllowXForwardedFor = serverConfig.GetValue<bool>("DOSAllowXForwardedForHeader", false);
            m_DosProtectionOptions.RequestTimeSpan =
                TimeSpan.FromMilliseconds(serverConfig.GetValue<int>("DOSRequestTimeFrameMS", 10000));
            m_DosProtectionOptions.MaxRequestsInTimeframe = serverConfig.GetValue<int>("DOSMaxRequestsInTimeFrame", 5);
            m_DosProtectionOptions.ForgetTimeSpan =
                TimeSpan.FromMilliseconds(serverConfig.GetValue<int>("DOSForgiveClientAfterMS", 120000));
            m_DosProtectionOptions.ReportingName = "LOGINDOSPROTECTION";

            return loginService;
        }

        private void InitializeHandlers(IHttpServer server)
        {
            LLLoginHandlers loginHandlers = new LLLoginHandlers(m_logger, m_LoginService, m_Proxy);
//            server.AddXmlRPCHandler("login_to_simulator",
//                new XmlRpcBasicDOSProtector(loginHandlers.HandleXMLRPCLogin, loginHandlers.HandleXMLRPCLoginBlocked,
//                    m_DosProtectionOptions).Process, false);
            server.AddXmlRPCHandler("login_to_simulator",loginHandlers.HandleXMLRPCLogin, false);
            server.AddXmlRPCHandler("set_login_level", loginHandlers.HandleXMLRPCSetLoginLevel, false);
            server.SetDefaultLLSDHandler(loginHandlers.HandleLLSDLogin);
            //server.AddWebSocketHandler("/WebSocket/GridLogin", loginHandlers.HandleWebSocketLoginEvents);
        }

    }
}
