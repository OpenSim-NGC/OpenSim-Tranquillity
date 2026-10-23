/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice;

// Janus AudioBridge's own "kick": the request names a room and a participant, and the plugin has the core close that
// participant's connection, after which the participant leaves the room.
public class AudioBridgeKickReq : PluginMsgReq
{
    public AudioBridgeKickReq(int pRoomId, long pParticipantId) : base(new OSDMap() {
                                            { "request", "kick" },
                                            { "room", pRoomId },
                                            { "id", pParticipantId }
                                        })
    {
    }
}

public partial class JanusAudioBridge
{
    /// <summary>Kick one participant out of a room. The answer is read by <see cref="ClassifyKickReply"/>.</summary>
    public async Task<ParticipantMuteResult> KickParticipant(int pRoomId, long pParticipantId)
    {
        AudioBridgeResp resp = await SendAudioBridgeMsg(new AudioBridgeKickReq(pRoomId, pParticipantId)).ConfigureAwait(false);
        return ClassifyKickReply(resp);
    }

    /// <summary>
    /// Reads the plugin's answer to a kick. The result type is the mute's: Applied (the participant was kicked),
    /// NotInRoom (no such room or participant), Unsupported (the plugin does not know the request) and Failed.
    /// </summary>
    public static ParticipantMuteResult ClassifyKickReply(AudioBridgeResp pResp)
        => ClassifyModeratorReply(pResp, "kick");
}
