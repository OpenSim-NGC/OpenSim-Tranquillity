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
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice;

// Per-participant mute in a call's room through the AudioBridge's own moderator mute.
//
// A participant is found by the id this service set when it joined the room: the AudioBridge "display" of every
// participant this service joins is the agent id (JanusRoom.JoinRoom), taken from the authenticated request, never
// from anything the viewer sends and never an avatar name. So the agent id names the same participants whichever
// region server joined them, and every participant of that agent is acted on together (an agent with two viewer
// sessions in one room has two participants). A display that is not exactly that agent id is never a match.
public partial class WebRtcJanusService : INonSpatialParticipantMute
{
    // INonSpatialParticipantMute
    public ParticipantMuteResult SetCallMute(UUID pAgentID, int pRoomNumber, bool pMuted)
    {
        try
        {
            return SetCallMuteAsync(pAgentID, pRoomNumber, pMuted).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, $"{LogHeader} SetCallMute failed for agent {pAgentID}");
            return ParticipantMuteResult.Failed;
        }
    }

    // INonSpatialParticipantMute
    public bool HasCallParticipant(UUID pAgentID, int pRoomNumber)
    {
        try
        {
            (List<long> found, ParticipantMuteResult why) = ParticipantsOfAsync(pAgentID, pRoomNumber).GetAwaiter().GetResult();
            return found.Count > 0;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, $"{LogHeader} HasCallParticipant failed for agent {pAgentID}");
            return false;
        }
    }

    /// <summary>
    /// The ids of every participant in the room whose display is exactly this agent's id, as the gateway lists them.
    /// The gateway is asked each time, so a participant joined through another region server is found as well as one
    /// this process joined. The result code is Applied when the room could be read and NotInRoom/Failed otherwise.
    /// </summary>
    private async Task<(List<long> Participants, ParticipantMuteResult Read)> ParticipantsOfAsync(UUID pAgentID, int pRoomNumber)
    {
        var found = new List<long>();
        JanusViewerSession svc = await EnsureServiceSessionAsync().ConfigureAwait(false);
        if (svc?.AudioBridge is null)
            return (found, ParticipantMuteResult.Failed);

        AudioBridgeResp listed = await svc.AudioBridge.SendAudioBridgeMsg(new AudioBridgeListParticipantsReq(pRoomNumber)).ConfigureAwait(false);
        if (listed is null)
            return (found, ParticipantMuteResult.Failed);
        if (listed.AudioBridgeReturnCode != "participants")
            return (found, listed.AudioBridgeErrorCode == WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE
                ? ParticipantMuteResult.NotInRoom : ParticipantMuteResult.Failed);

        if (listed.PluginRespData.TryGetValue("participants", out OSD all) && all is OSDArray entries)
            foreach (OSD one in entries)
                if (one is OSDMap entry && UUID.TryParse(entry["display"].AsString(), out UUID display) && display == pAgentID)
                {
                    long id = entry["id"].AsLong();
                    if (id != 0 && !found.Contains(id))
                        found.Add(id);
                }
        return (found, ParticipantMuteResult.Applied);
    }

    private async Task<ParticipantMuteResult> SetCallMuteAsync(UUID pAgentID, int pRoomNumber, bool pMuted)
    {
        (List<long> participants, ParticipantMuteResult read) = await ParticipantsOfAsync(pAgentID, pRoomNumber).ConfigureAwait(false);
        if (read != ParticipantMuteResult.Applied)
            return read;
        if (participants.Count == 0)
            return ParticipantMuteResult.NotInRoom;

        // The service's own handle sends the request: the mute is a room-level request, and a participant's handle
        // may be closing.
        JanusViewerSession svc = await EnsureServiceSessionAsync().ConfigureAwait(false);
        if (svc?.AudioBridge is null)
            return ParticipantMuteResult.Failed;

        // All of the agent's participants or none that the caller can rely on: the first one that cannot be muted ends
        // the attempt, and the caller takes the record back and asks again for the earlier state.
        foreach (long participant in participants)
        {
            ParticipantMuteResult r = await svc.AudioBridge.SetParticipantMute(pRoomNumber, participant, pMuted).ConfigureAwait(false);
            if (r != ParticipantMuteResult.Applied)
                return r;
        }
        return ParticipantMuteResult.Applied;
    }
}
