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

using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.Handlers.Neighbour
{
    public class NeighbourServiceInConnector : IServiceConnector
    {
        private INeighbourService m_NeighbourService;
        private IScene m_Scene;
        private IAuthenticationService m_AuthenticationService = null;
        private static string _ConfigName = "NeighbourService";

        public string ConfigName { get; private set; } = _ConfigName;

        public IConfiguration Config { get; private set; }
        public ILogger Logger { get; private set; }
        public IHttpServer HttpServer { get; private set; }

        public NeighbourServiceInConnector(
            IConfiguration config, 
            ILogger<NeighbourServiceInConnector> logger,
            INeighbourService nService, 
            IScene scene
            )
        {
            Config = config;
            Logger = Logger;
            m_NeighbourService = nService;
            m_Scene = scene;
        }

//      XXX  public NeighbourServiceInConnector(IConfiguration source, IHttpServer server, INeighbourService nService, IScene scene)
        public void Initialize(IHttpServer httpServer)
        {
            HttpServer = httpServer;

            if (m_NeighbourService == null)
            {
                Logger.LogError("Neighbour service was not provided");
                return;
            }

            //bool authentication = neighbourConfig.GetBoolean("RequireAuthentication", false);
            //if (authentication)
            //    m_AuthenticationService = scene.RequestModuleInterface<IAuthenticationService>();

            HttpServer.AddSimpleStreamHandler(new NeighbourSimpleHandler(Logger, m_NeighbourService, m_AuthenticationService), true);
        }
    }
}
