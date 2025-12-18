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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.LindenCaps
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ServerReleaseNotesModule")]
    class ServerReleaseNotesModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_enabled;
        private string m_ServerReleaseNotesURL;

        public string Name { get { return "ServerReleaseNotesModule"; } }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            m_enabled = false; // whatever
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            string capURL = config.GetString("Cap_ServerReleaseNotes", string.Empty);
            // If capability not configured or explicitly turned off, leave disabled
            if (string.IsNullOrEmpty(capURL) ||
                capURL.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                capURL == "0")
            {
                m_log.DebugFormat("[ServerReleaseNotesModule]: Cap_ServerReleaseNotes not enabled in config");
                return;
            }

            config = source.Configs["ServerReleaseNotes"];
            if (config == null)
                return;

            m_ServerReleaseNotesURL = config.GetString("ServerReleaseNotesURL", m_ServerReleaseNotesURL);
            if (string.IsNullOrEmpty(m_ServerReleaseNotesURL))
            {
                m_log.Error("[ServerReleaseNotesModule]: ServerReleaseNotesURL not configured. Cap disabled.");
                return;
            }

            if (!Uri.IsWellFormedUriString(m_ServerReleaseNotesURL, UriKind.Absolute))
            {
                m_log.ErrorFormat("[ServerReleaseNotesModule]: Invalid ServerReleaseNotesURL '{0}'. Cap Disabled", m_ServerReleaseNotesURL);
                return;
            }

            m_enabled = true;
            m_log.InfoFormat("[ServerReleaseNotesModule]: Enabled. Redirecting ServerReleaseNotes cap to {0}", m_ServerReleaseNotesURL);
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
            caps.RegisterSimpleHandler("ServerReleaseNotes", new SimpleStreamHandler(capPath, ProcessServerReleaseNotes));
        }

        public void ProcessServerReleaseNotes(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            httpResponse.Redirect(m_ServerReleaseNotesURL);
        }
    }
}
