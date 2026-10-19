/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice;

// The room list and destroy that the empty-room reaper uses (see EmptyRoomReaper).
public partial class JanusAudioBridge
{
    /// <summary>Called with the room number when a create is answered "created" (not for "already exists").</summary>
    public System.Action<int> RoomCreated { get; set; }

    /// <summary>Participant count of every room on the gateway, or null when the list could not be read.</summary>
    public async Task<IReadOnlyDictionary<int, int>> ListRoomParticipantCounts()
    {
        AudioBridgeResp resp = await SendAudioBridgeMsg(new AudioBridgeListRoomsReq()).ConfigureAwait(false);
        if (resp is null || !resp.isSuccess || !resp.PluginRespData.TryGetValue("list", out OSD list) || list is not OSDArray rooms)
            return null;
        var counts = new Dictionary<int, int>();
        foreach (OSD item in rooms)
        {
            if (item is OSDMap room)
                counts[room["room"].AsInteger()] = room["num_participants"].AsInteger();
        }
        return counts;
    }

    /// <summary>Destroy a room. True when it is gone afterwards: destroyed, or already absent (error 485).</summary>
    public async Task<bool> DestroyRoomById(int pRoomId)
    {
        AudioBridgeResp resp = await SendAudioBridgeMsg(new AudioBridgeDestroyRoomReq(pRoomId, false)).ConfigureAwait(false);
        if (resp is null)
            return false;
        if (resp.AudioBridgeReturnCode == "destroyed" || resp.isSuccess || resp.AudioBridgeErrorCode == WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE)
        {
            m_log.LogDebug("{0} DestroyRoom. Room {1} removed", LogHeader, pRoomId);
            return true;
        }
        m_log.LogWarning("{0} DestroyRoom. Room {1} was not removed: {2}", LogHeader, pRoomId, resp.ToString());
        return false;
    }
}
