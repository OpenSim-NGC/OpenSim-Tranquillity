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

namespace OpenSim.Server.Handlers.Profiles
{
    public class UserProfilesConnector: IServiceConnector
    {
        private const string _ConfigName = "UserProfilesService";

        protected IConfiguration m_configuration;
        protected ILogger<UserProfilesConnector> m_logger;
        protected IComponentContext m_context;

        public UserProfilesConnector(
            IConfiguration config, 
            ILogger<UserProfilesConnector> logger,
            IComponentContext componentContext)
        {
            m_configuration = config;
            m_logger = logger;
            m_context = componentContext;
        }

        public IUserProfilesService ServiceModule { get; private set; }

        public bool Enabled { get; private set; }

        public string ConfigName { get; private set; } = _ConfigName;

        public IHttpServer HttpServer { get; private set; }

        public void Initialize(IHttpServer httpServer)
        {
            HttpServer = httpServer;

            var serverConfig = m_configuration.GetSection(ConfigName);
            if (serverConfig.Exists() is false)
                throw new Exception(String.Format("No section {0} in config file", ConfigName));

            if(serverConfig.GetValue<bool>("Enabled",false) is false)
            {
                Enabled = false;
                return;
            }

            Enabled = true;

            string service = serverConfig.GetValue("LocalServiceModule", String.Empty);
            ServiceModule = m_context.ResolveNamed<IUserProfilesService>(service);

            JsonRpcProfileHandlers handler = new JsonRpcProfileHandlers(m_logger, ServiceModule);

            HttpServer.AddJsonRPCHandler("avatarclassifiedsrequest", handler.AvatarClassifiedsRequest);
            HttpServer.AddJsonRPCHandler("classified_update", handler.ClassifiedUpdate);
            HttpServer.AddJsonRPCHandler("classifieds_info_query", handler.ClassifiedInfoRequest);
            HttpServer.AddJsonRPCHandler("classified_delete", handler.ClassifiedDelete);
            HttpServer.AddJsonRPCHandler("avatarpicksrequest", handler.AvatarPicksRequest);
            HttpServer.AddJsonRPCHandler("pickinforequest", handler.PickInfoRequest);
            HttpServer.AddJsonRPCHandler("picks_update", handler.PicksUpdate);
            HttpServer.AddJsonRPCHandler("picks_delete", handler.PicksDelete);
            HttpServer.AddJsonRPCHandler("avatarnotesrequest", handler.AvatarNotesRequest);
            HttpServer.AddJsonRPCHandler("avatar_notes_update", handler.NotesUpdate);
            HttpServer.AddJsonRPCHandler("avatar_properties_request", handler.AvatarPropertiesRequest);
            HttpServer.AddJsonRPCHandler("avatar_properties_update", handler.AvatarPropertiesUpdate);
            HttpServer.AddJsonRPCHandler("avatar_interests_update", handler.AvatarInterestsUpdate);
            HttpServer.AddJsonRPCHandler("user_preferences_update", handler.UserPreferenecesUpdate);
            HttpServer.AddJsonRPCHandler("user_preferences_request", handler.UserPreferencesRequest);
            HttpServer.AddJsonRPCHandler("image_assets_request", handler.AvatarImageAssetsRequest);
            HttpServer.AddJsonRPCHandler("user_data_request", handler.RequestUserAppData);
            HttpServer.AddJsonRPCHandler("user_data_update", handler.UpdateUserAppData);
        }
    }
}