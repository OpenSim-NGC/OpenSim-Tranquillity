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
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.TrustedHypergrid;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.Handlers.Hypergrid;

public class GatekeeperServiceInConnector : ServiceConnector
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private IGatekeeperService m_GatekeeperService;
    public IGatekeeperService GateKeeper
    {
        get { return m_GatekeeperService; }
    }

    bool m_Proxy = false;

    public GatekeeperServiceInConnector(IConfigSource config, IHttpServer server, ISimulationService simService) :
            base(config, server, String.Empty)
    {
        IConfig gridConfig = config.Configs["GatekeeperService"];
        if (gridConfig != null)
        {
            string serviceDll = gridConfig.GetString("LocalServiceModule", string.Empty);
            Object[] args = new Object[] { config, simService };
            m_GatekeeperService = ServerUtils.LoadPlugin<IGatekeeperService>(serviceDll, args);

        }
        if (m_GatekeeperService == null)
            throw new Exception("Gatekeeper server connector cannot proceed because of missing service");

        m_Proxy = gridConfig.GetBoolean("HasProxy", false);

        // Trusted Hypergrid (ADR-010): initialise the process identity from [TrustedHypergrid] in
        // Robust.HG.ini. Inert when Enabled=false. This inbound connector is the config-bearing
        // owner of the gatekeeper path; the sign/verify call sites themselves have no IConfigSource.
        // Slice 3: when enabled, also stand up the trust registry (data plugin from [DatabaseService]
        // / [TrustedHypergrid], plus the hgtrust console commands) and hand it to the verifier so a
        // verified caller resolves to its registry tier. The tier is reported, never enforced here.
        TrustedHypergridHooks.EnsureInitialized(config, LoadTrustRegistry(config));

        // Slice 3b: arm the ONE hard-refusal component (Design Brief §6, ADR-011) for the HG
        // XML-RPC pair, and only when the operator selected it for the gatekeeper — never the
        // general ServiceAuth chain, which Hypergrid XML-RPC has never carried. It refuses solely a
        // Blocked-tier caller; with it unconfigured nothing on this path can be refused.
        IServiceAuth trustAuth = null;
        if (TrustedGridAuthentication.IsConfigured(config, "GatekeeperService"))
        {
            trustAuth = new TrustedGridAuthentication();
            m_log.LogInformation("[TRUSTED HG]: TrustedGridAuthentication armed for the gatekeeper XML-RPC handlers; Blocked-tier callers will be refused link_region/get_region");
        }

        HypergridHandlers hghandlers = new HypergridHandlers(m_GatekeeperService, trustAuth);
        server.AddXmlRPCHandler("link_region", hghandlers.LinkRegionRequest, false);
        server.AddXmlRPCHandler("get_region", hghandlers.GetRegion, false);

        server.AddSimpleStreamHandler(new GatekeeperAgentHandler(m_GatekeeperService, m_Proxy),true);
    }

    /// <summary>
    /// Where the trust registry lives. Same assembly as the gatekeeper service this connector
    /// already loads by config; the class is fixed because Design Brief §8 defines no service
    /// selection key for it.
    /// </summary>
    public const string TrustRegistryModule = "OpenSim.Services.HypergridService.dll:TrustedGridRegistryService";

    /// <summary>
    /// Construct the Slice 3 trust registry when [TrustedHypergrid] Enabled=true. Returns null —
    /// leaving the verifier registry-less, i.e. every verified caller Open — when the feature is
    /// off or the registry cannot be built (no StorageProvider, migration failure). A registry
    /// fault must never stop Robust or refuse a caller (ADR-005).
    /// </summary>
    private static IGridTrustLookup LoadTrustRegistry(IConfigSource config)
    {
        IConfig trustConfig = config.Configs[TrustedHypergridRuntime.ConfigSection];
        if (trustConfig == null || !trustConfig.GetBoolean("Enabled", false))
            return null;

        try
        {
            IGridTrustLookup registry = ServerUtils.LoadPlugin<IGridTrustLookup>(TrustRegistryModule, new Object[] { config });
            if (registry == null)
                m_log.LogWarning("[TRUSTED HG]: trust registry {0} could not be loaded; verified callers will classify Open until it is", TrustRegistryModule);
            return registry;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "[TRUSTED HG]: trust registry failed to start; verified callers will classify Open");
            return null;
        }
    }

    public GatekeeperServiceInConnector(IConfigSource config, IHttpServer server, string configName)
        : this(config, server, (ISimulationService)null)
    {
    }

    public GatekeeperServiceInConnector(IConfigSource config, IHttpServer server)
        : this(config, server, String.Empty)
    {
    }
}
