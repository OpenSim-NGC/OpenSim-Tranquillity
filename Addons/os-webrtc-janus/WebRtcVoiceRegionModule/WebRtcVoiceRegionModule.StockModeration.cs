/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Net;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;

using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

// Parcel voice moderation on Janus's stock AudioBridge, which has no per-listener visibility to carry a mute.
// The stock plugin has its own moderator mute for one participant, so a mute here mutes that participant in its
// spatial room. It is the participant who is muted, not the parcel: an avatar muted on a parcel stays muted in
// its room if it walks to another parcel, until a moderator unmutes it. The record of who is muted lives in the
// region, so a mute-everyone survives the room emptying and applies to whoever joins after.
public partial class WebRtcVoiceRegionModule
{
    // True when [JanusWebRtcVoice] PluginName is the stock AudioBridge.
    private bool m_stockPlugin;

    // Who is muted, per parcel, when moderation runs on the stock plugin.
    private readonly VoiceModerationStore m_stockModeration = new VoiceModerationStore();

    // The moderator asked for one of the four operands; the stock plugin does it participant by participant.
    private void ApplyStockSpatialMute(string pOperand, UUID pTarget, LandData pLand, UUID pModerator, Scene pScene, IOSHttpResponse pResponse)
    {
        var mute = pScene.RequestModuleInterface<IWebRtcVoiceService>() as ISpatialParticipantMute;
        if (mute is null)
        {
            m_log.LogWarning($"{logHeader}[Moderation]: {pOperand} authorised on parcel {pLand.GlobalID} but the voice service cannot mute a participant; not recorded.");
            pResponse.StatusCode = (int)HttpStatusCode.NotImplemented;
            return;
        }

        bool individual = pOperand == "mute" || pOperand == "unmute";
        bool wasModerated = individual && m_stockModeration.IsModerated(pLand.GlobalID, pTarget);
        switch (pOperand)
        {
            case "mute_all":   m_stockModeration.SetMuteEveryone(pLand.GlobalID, true);  break;
            case "unmute_all": m_stockModeration.SetMuteEveryone(pLand.GlobalID, false); break;
            case "mute":       m_stockModeration.MuteAgent(pLand.GlobalID, pTarget);     break;
            case "unmute":     m_stockModeration.UnmuteAgent(pLand.GlobalID, pTarget);   break;
        }

        var agents = new List<UUID>();
        if (individual)
            agents.Add(pTarget);
        else
            pScene.ForEachRootScenePresence(sp =>
            {
                if (!sp.IsNPC && ParcelOf(pScene, sp)?.GlobalID == pLand.GlobalID)
                    agents.Add(sp.UUID);
            });

        ParticipantMuteResult worst = ParticipantMuteResult.Applied;
        foreach (UUID agent in agents)
        {
            // A moderator is never muted by moderation, so it is left alone whichever way the record goes.
            if (MayModerateVoice(pScene, pLand, agent))
                continue;
            ParticipantMuteResult r = mute.SetSpatialMute(pScene.RegionInfo.RegionID, agent, m_stockModeration.IsModerated(pLand.GlobalID, agent));
            if (r == ParticipantMuteResult.Unsupported || r == ParticipantMuteResult.Failed)
            {
                worst = r;
                break;
            }
        }

        if (worst != ParticipantMuteResult.Applied)
        {
            // Not done: put the record back so it does not say something the mixer does not do.
            switch (pOperand)
            {
                case "mute_all":   m_stockModeration.SetMuteEveryone(pLand.GlobalID, false); break;
                case "unmute_all": m_stockModeration.SetMuteEveryone(pLand.GlobalID, true);  break;
                case "mute":       if (!wasModerated) m_stockModeration.UnmuteAgent(pLand.GlobalID, pTarget); break;
                case "unmute":     if (wasModerated) m_stockModeration.MuteAgent(pLand.GlobalID, pTarget);   break;
            }
            m_log.LogWarning($"{logHeader}[Moderation]: {pOperand} on parcel {pLand.GlobalID} by {pModerator} was not applied: the voice server answered {worst}.");
            pResponse.StatusCode = worst == ParticipantMuteResult.Unsupported
                ? (int)HttpStatusCode.NotImplemented
                : (int)HttpStatusCode.ServiceUnavailable;
            return;
        }

        if (individual)
            m_log.LogInformation($"{logHeader}[Moderation]: {pOperand} agent {pTarget} on parcel {pLand.GlobalID} (\"{pLand.Name}\") by {pModerator} (participant mute)");
        else
            m_log.LogInformation($"{logHeader}[Moderation]: {pOperand} on parcel {pLand.GlobalID} (\"{pLand.Name}\") by {pModerator} (participant mute)");
        pResponse.RawBuffer = llsdUndefAnswerBytes;
        pResponse.StatusCode = (int)HttpStatusCode.OK;
    }

    // A spatial provision succeeded: an agent who is moderated on the parcel it stands on joins muted.
    private void ApplyStockMuteOnJoin(UUID pAgentID, Scene pScene)
    {
        if (!m_stockPlugin || !m_moderationEnabled)
            return;
        if (!pScene.TryGetScenePresence(pAgentID, out ScenePresence sp))
            return;
        LandData land = ParcelOf(pScene, sp);
        if (land is null || !m_stockModeration.IsModerated(land.GlobalID, pAgentID) || MayModerateVoice(pScene, land, pAgentID))
            return;
        if (pScene.RequestModuleInterface<IWebRtcVoiceService>() is not ISpatialParticipantMute mute)
            return;
        ParticipantMuteResult r = mute.SetSpatialMute(pScene.RegionInfo.RegionID, pAgentID, true);
        if (r != ParticipantMuteResult.Applied)
            m_log.LogWarning($"{logHeader}[Moderation]: agent {pAgentID} is muted on parcel {land.GlobalID} but could not be muted on joining: {r}");
    }

    private static LandData ParcelOf(Scene pScene, ScenePresence pPresence)
        => pScene.LandChannel?.GetLandObject(pPresence.AbsolutePosition.X, pPresence.AbsolutePosition.Y)?.LandData;
}
