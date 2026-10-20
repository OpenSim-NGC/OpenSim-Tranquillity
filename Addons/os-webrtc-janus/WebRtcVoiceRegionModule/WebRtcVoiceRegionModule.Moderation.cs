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
// Adapted 2026 by Legion Builds

using System.Collections.Generic;
using System.Net;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Caps = OpenSim.Framework.Capabilities.Caps;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using Nini.Config;
using Microsoft.Extensions.Logging;
using OpenSim.Services.Interfaces;   // IPresenceService, for the online-only ring filter
using osWebRtcVoice.NonSpatial;   // the non-spatial engine and the group arm

namespace osWebRtcVoice;

// Parcel voice moderation: the SpatialVoiceModerationRequest capability.
public partial class WebRtcVoiceRegionModule
{
    // [WebRtcVoice] VoiceModerationEnabled: parcel voice moderation (the SpatialVoiceModerationRequest
    // capability and the moderation console commands). Off by default: the capability is not advertised.
    public const bool DefaultVoiceModerationEnabled = false;

    public static bool ReadVoiceModerationEnabled(IConfig pConfig)
        => pConfig.GetBoolean("VoiceModerationEnabled", DefaultVoiceModerationEnabled);

    private bool m_moderationEnabled = DefaultVoiceModerationEnabled;

    /// <summary>
    /// Handles the viewer's SpatialVoiceModerationRequest CAP (parity with SL viewer 26.1 parcel
    /// voice moderation). First half: authorise and record sticky per-parcel moderation
    /// state in memory. NOTHING consumes the store yet — the matrix enforcement rule is a separate
    /// commit. The body shape is fixed by the viewer (llnearbyvoicemoderation.cpp):
    ///   individual: { "operand": "mute" | "unmute", "agent_id": &lt;uuid&gt; }
    ///   everyone:   { "operand": "mute_all" | "unmute_all" }
    /// The body carries NO parcel id, so scope is resolved from the requester's position — this is
    /// what makes moderation parcel-bound rather than viewer-declared.
    /// </summary>
    public void SpatialVoiceModerationRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        if (request.HttpMethod != "POST")
        {
            m_log.LogDebug($"{logHeader}[Moderation]: not a POST request. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        OSDMap map = BodyToMap(request, "SpatialVoiceModerationRequest");
        if (map is null)
        {
            m_log.LogError($"{logHeader}[Moderation]: no request data. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        // (2) Operand — conform to the viewer's shape exactly; reject anything else. mute/unmute
        // carry an agent_id; mute_all/unmute_all do not. No other fields are read.
        if (!map.TryGetString("operand", out string operand))
        {
            m_log.LogWarning($"{logHeader}[Moderation]: missing 'operand'. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }
        bool everyoneOp   = operand == "mute_all" || operand == "unmute_all";
        bool individualOp = operand == "mute" || operand == "unmute";
        if (!everyoneOp && !individualOp)
        {
            m_log.LogWarning($"{logHeader}[Moderation]: unknown operand \"{operand}\". Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }
        UUID targetAgent = UUID.Zero;
        if (individualOp)
        {
            if (!map.ContainsKey("agent_id") || (targetAgent = map["agent_id"].AsUUID()).IsZero())
            {
                m_log.LogWarning($"{logHeader}[Moderation]: operand \"{operand}\" without a valid agent_id. Agent={agentID}");
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }
        }

        // (3) Resolve the target parcel from the REQUESTER's position. The body names no parcel,
        // so this is the only trustworthy scope and it pins mute_all to the moderator's own parcel
        // rather than a viewer-declared region.
        if (scene.LandChannel is null || !scene.TryGetScenePresence(agentID, out ScenePresence sp))
        {
            m_log.LogWarning($"{logHeader}[Moderation]: cannot resolve requester presence/land in region \"{scene.Name}\". Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }
        ILandObject parcel = scene.LandChannel.GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
        LandData land = parcel?.LandData;
        if (land is null)
        {
            m_log.LogWarning($"{logHeader}[Moderation]: could not resolve a parcel at the requester's position. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // (4) Authorise server-side; never trust the viewer's own isNearbyChatModerator() gate.
        // Compose owner / estate-manager / group-ModerateChat, the pieces the ban path uses.
        if (!MayModerateVoice(scene, land, agentID))
        {
            m_log.LogWarning($"{logHeader}[Moderation]: DENIED {operand} on parcel {land.GlobalID} (\"{land.Name}\") for {agentID}: not owner, estate manager, or group moderator");
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // On the stock plugin there is no visibility matrix; the plugin's own participant mute does it.
        if (m_stockPlugin)
        {
            ApplyStockSpatialMute(operand, targetAgent, land, agentID, scene, response);
            return;
        }

        // (5) The store lives on the per-region visibility service. The matrix is the single
        // enforcement point, so if the feeder is disabled there is no way to enforce a mute —
        // refuse loudly rather than silently accept an unenforceable one.
        VoiceVisibilityService svc;
        lock (m_visibilityServices)
            m_visibilityServices.TryGetValue(scene, out svc);
        if (svc is null)
        {
            m_log.LogWarning($"{logHeader}[Moderation]: {operand} authorised on parcel {land.GlobalID} but the visibility feeder is disabled in region \"{scene.Name}\"; cannot enforce, not recorded.");
            response.StatusCode = (int)HttpStatusCode.NotImplemented;
            return;
        }

        switch (operand)
        {
            case "mute_all":   svc.Moderation.SetMuteEveryone(land.GlobalID, true);  break;
            case "unmute_all": svc.Moderation.SetMuteEveryone(land.GlobalID, false); break;
            case "mute":       svc.Moderation.MuteAgent(land.GlobalID, targetAgent);   break;
            case "unmute":     svc.Moderation.UnmuteAgent(land.GlobalID, targetAgent); break;
        }

        // (6) Diagnosable from day one — accepted op with parcel GlobalID, operand, requester.
        if (individualOp)
            m_log.LogInformation($"{logHeader}[Moderation]: {operand} agent {targetAgent} on parcel {land.GlobalID} (\"{land.Name}\") by {agentID}");
        else
            m_log.LogInformation($"{logHeader}[Moderation]: {operand} on parcel {land.GlobalID} (\"{land.Name}\") by {agentID}");

        response.RawBuffer = llsdUndefAnswerBytes;
        response.StatusCode = (int)HttpStatusCode.OK;
    }

    // SL's three authorisation cases: land owner, estate manager/owner, or a member with
    // GroupPowers.ModerateChat on a group-owned parcel. The composition now lives in the shared
    // VoiceModerationAuth so the matrix's moderator-exemption uses exactly the same rule; behaviour
    // is unchanged. Server-side only — the viewer's own gate is UI and spoofable.
    private bool MayModerateVoice(Scene scene, LandData land, UUID agentID)
        => VoiceModerationAuth.MayModerate(scene, land, agentID);

    // Parcel voice moderation (parity with SL viewer 26.1), only when [WebRtcVoice] VoiceModerationEnabled is
    // set. RegisterSimpleHandler both ADVERTISES the capability in the seed set (the viewer's getCapability
    // resolves it) and routes the POST, identical to the three above.
    private void RegisterModerationCapability(Scene scene, UUID agentID, Caps caps)
    {
        if (m_moderationEnabled)
            caps.RegisterSimpleHandler("SpatialVoiceModerationRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        SpatialVoiceModerationRequest(httpRequest, httpResponse, agentID, scene);
                    }));
    }

    // Reads whether parcel voice moderation is on and registers its console commands.
    private void InitialiseModeration()
    {
        m_moderationEnabled = ReadVoiceModerationEnabled(m_Config);

        if (m_moderationEnabled)
            new VoiceModerationCommands(SnapshotVisibilityServices).Register();
    }
}
