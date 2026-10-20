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

// Janus AudioBridge's own moderator mute: the "mute" and "unmute" requests name a room and a participant.
public class AudioBridgeMuteReq : PluginMsgReq
{
    public AudioBridgeMuteReq(int pRoomId, long pParticipantId, bool pMuted) : base(new OSDMap() {
                                            { "request", pMuted ? "mute" : "unmute" },
                                            { "room", pRoomId },
                                            { "id", pParticipantId }
                                        })
    {
    }
}

public partial class JanusAudioBridge
{
    /// <summary>AudioBridge error 482: the plugin does not know the request (a version without moderator mute).</summary>
    public const int UNKNOWN_REQUEST_ERROR_CODE = 482;

    /// <summary>AudioBridge errors 485 (no such room) and 492 (no such user): the participant is not there.</summary>
    public const int NO_SUCH_USER_ERROR_CODE = 492;

    /// <summary>Mute or unmute one participant with the plugin's own moderator mute.</summary>
    public async Task<ParticipantMuteResult> SetParticipantMute(int pRoomId, long pParticipantId, bool pMuted)
    {
        AudioBridgeResp resp = await SendAudioBridgeMsg(new AudioBridgeMuteReq(pRoomId, pParticipantId, pMuted)).ConfigureAwait(false);
        return ClassifyMuteReply(resp);
    }

    /// <summary>Reads the plugin's answer to a mute or unmute request.</summary>
    public static ParticipantMuteResult ClassifyMuteReply(AudioBridgeResp pResp)
    {
        if (pResp is null)
            return ParticipantMuteResult.Failed;
        if (pResp.isSuccess)
            return ParticipantMuteResult.Applied;
        switch (pResp.AudioBridgeErrorCode)
        {
            case UNKNOWN_REQUEST_ERROR_CODE:
                return ParticipantMuteResult.Unsupported;
            case WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE:
            case NO_SUCH_USER_ERROR_CODE:
                return ParticipantMuteResult.NotInRoom;
            default:
                m_log.LogWarning("{0} mute request was refused: {1}", LogHeader, pResp.ToString());
                return ParticipantMuteResult.Failed;
        }
    }
}
