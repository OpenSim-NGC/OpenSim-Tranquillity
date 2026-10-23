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

// Removing an agent from a call's room: close the participants this process joined, and on the stock AudioBridge kick
// the ones another region server joined. A participant is found by the agent id the service set at join (the
// participant's display), the same id a mute names.
public partial class WebRtcJanusService : INonSpatialParticipantRemoval
{
    // INonSpatialParticipantRemoval
    public int DropCallParticipants(UUID pAgentID, int pRoomNumber)
    {
        try
        {
            return DropCallParticipantsAsync(pAgentID, pRoomNumber).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, $"{LogHeader} DropCallParticipants failed for agent {pAgentID}");
            return 0;
        }
    }

    private async Task<int> DropCallParticipantsAsync(UUID pAgentID, int pRoomNumber)
    {
        // This process's own sessions for the agent in that room. The registry is process-wide, so copy under its lock.
        var local = new List<JanusViewerSession>();
        lock (VoiceViewerSession.ViewerSessions)
        {
            foreach (IVoiceViewerSession s in VoiceViewerSession.ViewerSessions.Values)
                if (s is JanusViewerSession js && js.AgentId == pAgentID && !js.Spatial
                    && js.Room is not null && js.Room.RoomId == pRoomNumber)
                    local.Add(js);
        }

        int dropped = 0;
        var closed = new HashSet<long>();
        foreach (JanusViewerSession js in local)
        {
            if (js.ParticipantId != 0)
                closed.Add(js.ParticipantId);
            string id = js.ViewerSessionID;
            await js.Shutdown().ConfigureAwait(false);
            VoiceViewerSession.RemoveViewerSession(id);
            dropped++;
        }

        // The stock AudioBridge can kick a participant this process did not join. The mixer has no such request, so
        // there a participant another region server joined is dropped by that server.
        if (!MixerOnlySwitches.IsStockPlugin(_JanusPluginName))
            return dropped;

        (List<long> others, ParticipantMuteResult read) = await ParticipantsOfAsync(pAgentID, pRoomNumber).ConfigureAwait(false);
        if (read != ParticipantMuteResult.Applied)
            return dropped;
        JanusViewerSession svc = await EnsureServiceSessionAsync().ConfigureAwait(false);
        if (svc?.AudioBridge is null)
            return dropped;
        foreach (long participant in others)
        {
            if (closed.Contains(participant))
                continue;
            if (await svc.AudioBridge.KickParticipant(pRoomNumber, participant).ConfigureAwait(false) == ParticipantMuteResult.Applied)
                dropped++;
        }
        return dropped;
    }
}
