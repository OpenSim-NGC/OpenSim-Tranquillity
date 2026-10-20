/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace osWebRtcVoice;

// Per-participant mute through the AudioBridge's own moderator mute, for moderation on the stock plugin.
public partial class WebRtcJanusService : ISpatialParticipantMute
{
    // ISpatialParticipantMute
    public ParticipantMuteResult SetSpatialMute(UUID pSceneID, UUID pAgentID, bool pMuted)
    {
        try
        {
            return SetSpatialMuteAsync(pSceneID, pAgentID, pMuted).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, $"{LogHeader} SetSpatialMute failed for agent {pAgentID}");
            return ParticipantMuteResult.Failed;
        }
    }

    private async Task<ParticipantMuteResult> SetSpatialMuteAsync(UUID pSceneID, UUID pAgentID, bool pMuted)
    {
        // The agent's joined spatial sessions in this region. The registry is process-wide, so copy under its lock.
        var targets = new List<(int Room, long Participant)>();
        lock (VoiceViewerSession.ViewerSessions)
        {
            foreach (IVoiceViewerSession s in VoiceViewerSession.ViewerSessions.Values)
            {
                if (s is JanusViewerSession js && js.AgentId == pAgentID && js.RegionId == pSceneID && js.Spatial
                    && js.Room is not null && js.ParticipantId != 0)
                    targets.Add((js.Room.RoomId, js.ParticipantId));
            }
        }
        if (targets.Count == 0)
            return ParticipantMuteResult.NotInRoom;

        // The service's own handle sends the request: the mute is a room-level request, and a participant's handle
        // may be closing.
        JanusViewerSession svc = await EnsureServiceSessionAsync().ConfigureAwait(false);
        if (svc?.AudioBridge is null)
            return ParticipantMuteResult.Failed;
        ParticipantMuteResult worst = ParticipantMuteResult.NotInRoom;
        foreach ((int room, long participant) in targets)
        {
            ParticipantMuteResult r = await svc.AudioBridge.SetParticipantMute(room, participant, pMuted).ConfigureAwait(false);
            if (r == ParticipantMuteResult.Unsupported || r == ParticipantMuteResult.Failed)
                return r;
            if (r == ParticipantMuteResult.Applied)
                worst = ParticipantMuteResult.Applied;
        }
        return worst;
    }
}
