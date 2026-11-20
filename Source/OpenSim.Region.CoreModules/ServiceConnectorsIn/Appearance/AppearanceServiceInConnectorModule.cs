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

using System.Reflection;
using Nini.Config;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Region.CoreModules.ServiceConnectorsIn.Appearance;

/// <summary>
/// Serves server-side bakes to viewers from a standalone (the login's <c>agent_appearance_service</c>), as the
/// AppearanceServiceConnector does on a grid's Robust. With server-side baking on, a viewer fetches every avatar's
/// bakes from that service, its own included, so a standalone without it textures nobody.
/// Turned on with <c>[Modules] AppearanceServiceInConnector = true</c>; the service is set in
/// <c>[AppearanceService]</c>.
/// </summary>
public class AppearanceServiceInConnectorModule : ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private bool m_Enabled;
    private bool m_Registered;
    private IConfigSource m_Config;

    public void Initialise(IConfigSource config)
    {
        m_Config = config;
        m_Enabled = config.Configs["Modules"]?.GetBoolean("AppearanceServiceInConnector", false) ?? false;
        if (m_Enabled)
            m_log.LogInformation("[APPEARANCE IN CONNECTOR]: AppearanceServiceInConnector enabled");
    }

    public void PostInitialise()
    {
    }

    public void Close()
    {
    }

    public Type ReplaceableInterface => null;

    public string Name => "AppearanceServiceInConnectorModule";

    public void AddRegion(Scene scene)
    {
        if (!m_Enabled || m_Registered)
            return;

        m_Registered = true;
        object[] args = new object[] { m_Config, MainServer.Instance.DefaultServer, "AppearanceService" };
        ServerUtils.LoadPlugin<IServiceConnector>("OpenSim.Server.Handlers.dll:AppearanceServerConnector", args);
    }

    public void RemoveRegion(Scene scene)
    {
    }

    public void RegionLoaded(Scene scene)
    {
    }
}
