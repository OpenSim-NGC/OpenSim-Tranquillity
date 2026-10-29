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
using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Reflection;

using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.TrustedHypergrid;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

using Nwc.XmlRpc;
using OpenMetaverse;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace OpenSim.Server.Handlers.Hypergrid;

public class HypergridHandlers
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private IGatekeeperService m_GatekeeperService;

    /// <summary>
    /// Slice 3b: the ONLY authenticator these XML-RPC handlers consult, and only when the
    /// operator configured <c>AuthType = TrustedGridAuthentication</c> for the gatekeeper
    /// (null otherwise, in which case nothing is ever refused here). It refuses solely a caller
    /// whose published <see cref="GridTrustContext"/> is Blocked (ADR-011); the general
    /// <c>ServiceAuth.Create</c> chain (Basic HTTP auth etc.) is deliberately NOT applied to
    /// Hypergrid XML-RPC, which has never carried it.
    /// </summary>
    private readonly IServiceAuth m_TrustAuth;

    public const string RefusalMessage = "Refused: this grid's operator has blocked your grid.";

    public HypergridHandlers(IGatekeeperService gatekeeper) : this(gatekeeper, null)
    {
    }

    public HypergridHandlers(IGatekeeperService gatekeeper, IServiceAuth trustAuth)
    {
        m_GatekeeperService = gatekeeper;
        m_TrustAuth = trustAuth;
        m_log.LogDebug("[HYPERGRID HANDLERS]: Active");
    }

    /// <summary>
    /// True when the configured trust authenticator refuses the caller classified for the
    /// current request. With no authenticator configured, or no Blocked classification, this is
    /// always false and the request proceeds exactly as before Slice 3b.
    /// </summary>
    private bool Refused(string method, IPEndPoint remoteClient)
    {
        if (m_TrustAuth == null)
            return false;

        if (m_TrustAuth.Authenticate(new NameValueCollection(), (k, v) => { }, out _))
            return false;

        GridTrustContext ctx = GridTrustContext.Current;
        m_log.LogInformation("[TRUSTED HG]: refused {0} from {1}: grid {2} is Blocked",
            method, remoteClient?.Address, ctx?.GridId);
        return true;
    }

    private static XmlRpcResponse Refusal()
    {
        Hashtable hash = new Hashtable();
        hash["result"] = "False";
        hash["message"] = RefusalMessage;
        XmlRpcResponse response = new XmlRpcResponse();
        response.Value = hash;
        return response;
    }

    /// <summary>
    /// Someone wants to link to us
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public XmlRpcResponse LinkRegionRequest(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        // Trusted Hypergrid: classify the caller (verify + log tier) and publish the context for
        // this request only; the scope clears it on every exit path. Only a Blocked caller, and
        // only with TrustedGridAuthentication configured, is refused (Slice 3b, ADR-011).
        using (TrustedHypergridHooks.Classify(requestData, "link_region"))
        {
            if (Refused("link_region", remoteClient))
                return Refusal();

            //string host = (string)requestData["host"];
            //string portstr = (string)requestData["port"];
            string name = (string)requestData["region_name"];
            if (name == null)
                name = string.Empty;

            m_log.LogDebug("[HG Handler]: XMLRequest to link to {0} from {1}", (name.Length == 0) ? "default region" : name, remoteClient.Address.ToString());
            bool success = m_GatekeeperService.LinkLocalRegion(name, out UUID regionID, out ulong regionHandle, out string externalName,
                out string imageURL, out string reason, out int sizeX, out int sizeY);

            Hashtable hash = new Hashtable();
            hash["result"] = success.ToString();
            hash["uuid"] = regionID.ToString();
            hash["handle"] = regionHandle.ToString();
            hash["size_x"] = sizeX.ToString();
            hash["size_y"] = sizeY.ToString();
            hash["region_image"] = imageURL;
            hash["external_name"] = externalName;

            XmlRpcResponse response = new XmlRpcResponse();
            response.Value = hash;
            return response;
        }
    }

    public XmlRpcResponse GetRegion(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        // Trusted Hypergrid: classify and publish for this request only (see LinkRegionRequest).
        using (TrustedHypergridHooks.Classify(requestData, "get_region"))
        {
            if (Refused("get_region", remoteClient))
                return Refusal();

            return GetRegionCore(requestData);
        }
    }

    private XmlRpcResponse GetRegionCore(Hashtable requestData)
    {
        //string host = (string)requestData["host"];
        //string portstr = (string)requestData["port"];
        string regionID_str = (string)requestData["region_uuid"];
        UUID regionID = UUID.Zero;
        UUID.TryParse(regionID_str, out regionID);

        UUID agentID = UUID.Zero;
        string agentHomeURI = null;
        if (requestData.ContainsKey("agent_id"))
            agentID = UUID.Parse((string)requestData["agent_id"]);
        if (requestData.ContainsKey("agent_home_uri"))
            agentHomeURI = (string)requestData["agent_home_uri"];

        string message;
        GridRegion regInfo = m_GatekeeperService.GetHyperlinkRegion(regionID, agentID, agentHomeURI, out message);

        Hashtable hash = new Hashtable();
        if (regInfo == null)
        {
            hash["result"] = "false";
        }
        else
        {
            hash["result"] = "true";
            hash["uuid"] = regInfo.RegionID.ToString();
            hash["x"] = regInfo.RegionLocX.ToString();
            hash["y"] = regInfo.RegionLocY.ToString();
            hash["size_x"] = regInfo.RegionSizeX.ToString();
            hash["size_y"] = regInfo.RegionSizeY.ToString();
            hash["region_name"] = regInfo.RegionName;
            hash["hostname"] = regInfo.ExternalHostName;
            hash["http_port"] = regInfo.HttpPort.ToString();
            hash["internal_port"] = regInfo.InternalEndPoint.Port.ToString();
            hash["server_uri"] = regInfo.ServerURI;
        }

        if (message != null)
            hash["message"] = message;

        XmlRpcResponse response = new XmlRpcResponse();
        response.Value = hash;
        return response;

    }

}
