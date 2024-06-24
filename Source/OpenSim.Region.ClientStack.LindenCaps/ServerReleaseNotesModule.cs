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

// Dedicated to Quill Littlefeather

using OpenMetaverse;

using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Caps = OpenSim.Framework.Capabilities.Caps;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ClientStack.Linden
{
    class ServerReleaseNotesModule : ISharedRegionModule
    {
        private bool m_enabled;
        private string m_ServerReleaseNotesURL;
        
        private readonly IConfiguration m_configuration;
        private readonly ILogger<ServerReleaseNotesModule> m_logger;

        public ServerReleaseNotesModule(IConfiguration configuration, ILogger<ServerReleaseNotesModule> logger)
        {
            m_configuration = configuration;
            m_logger = logger;
        }

        public string Name { get { return "ServerReleaseNotesModule"; } }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfiguration pConfiguration)
        {
            m_enabled = false; // whatever

            var config = m_configuration.GetSection("ClientStack.LindenCaps");
            if (config.Exists() is false)
                return;

            string capURL = config.GetValue("Cap_ServerReleaseNotes", string.Empty);
            if (string.IsNullOrEmpty(capURL) || capURL != "localhost")
                return;

            config = m_configuration.GetSection("ServerReleaseNotes");
            if (config.Exists() is false)
                return;

            m_ServerReleaseNotesURL = config.GetValue("ServerReleaseNotesURL", m_ServerReleaseNotesURL);
            if (string.IsNullOrEmpty(m_ServerReleaseNotesURL))
                return;

            Uri dummy;
            if (Uri.TryCreate(m_ServerReleaseNotesURL,UriKind.Absolute, out dummy) is false)
            {
                m_logger.LogError("[Cap_ServerReleaseNotes]: Invalid ServerReleaseNotesURL. Cap Disabled");
                return;
            }

            m_enabled = true;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void RegionLoaded(Scene scene) { }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnRegisterCaps -= RegisterCaps;
        }

        public void PostInitialise() { }

        public void Close() { }

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            string capPath = "/" + UUID.Random();
            caps.RegisterSimpleHandler("ServerReleaseNotes",
                           new SimpleStreamHandler(capPath, delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                           {
                               ProcessServerReleaseNotes(httpResponse);
                           }));
        }

        private void ProcessServerReleaseNotes(IOSHttpResponse httpResponse)
        {
            httpResponse.Redirect(m_ServerReleaseNotesURL);
        }
    }
}
