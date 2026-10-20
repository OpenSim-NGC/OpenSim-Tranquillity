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
        => ClassifyModeratorReply(pResp, "mute");

    /// <summary>
    /// What each AudioBridge error code means to a moderator request that names a room and a participant: mute, unmute
    /// and kick. The codes and where each is returned are those of Janus 1.4.1 (janus_audiobridge.c, the plugin's
    /// JANUS_AUDIOBRIDGE_ERROR_* at lines 2289-2304):
    ///
    ///   480 no message, 481 JSON that is not an object, 483/484 a missing or wrong-typed element (request, room, id,
    ///       secret)                                              the sim sent a request the plugin cannot read: Failed
    ///   482 unknown request (6187)                               the plugin version lacks the request: Unsupported
    ///   485 no such room (mute 4303, kick 4671)                  the participant is not there: NotInRoom
    ///   489 unauthorized (the room has a secret and the request carried none or a wrong one; mute 4313, kick 4680): Failed
    ///   492 no such user (mute 4336, kick 4702)                  the participant is not there: NotInRoom
    ///   499 unknown error (no session for the handle 6131, session already destroyed 6140, no response 6195): Failed
    ///   486, 487, 488, 490, 491, 493, 494 belong to other requests (create, join, destroy...) and are not returned by
    ///       these; if one ever is, it is a Failed that is logged.
    ///
    /// A reply with no plugin data at all (the plugin refusing while it shuts down, 6118, or the gateway refusing the
    /// request before the plugin sees it) has no code and is a Failed too. Only the success reply (mute 4348 and 4412,
    /// kick 4735) is Applied, and only a reply that says "success".
    /// </summary>
    private static readonly Dictionary<int, ParticipantMuteResult> s_moderatorErrors = new()
    {
        [480] = ParticipantMuteResult.Failed,
        [481] = ParticipantMuteResult.Failed,
        [UNKNOWN_REQUEST_ERROR_CODE] = ParticipantMuteResult.Unsupported,
        [483] = ParticipantMuteResult.Failed,
        [484] = ParticipantMuteResult.Failed,
        [WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE] = ParticipantMuteResult.NotInRoom,
        [486] = ParticipantMuteResult.Failed,
        [487] = ParticipantMuteResult.Failed,
        [488] = ParticipantMuteResult.Failed,
        [UNAUTHORIZED_ERROR_CODE] = ParticipantMuteResult.Failed,
        [490] = ParticipantMuteResult.Failed,
        [491] = ParticipantMuteResult.Failed,
        [NO_SUCH_USER_ERROR_CODE] = ParticipantMuteResult.NotInRoom,
        [493] = ParticipantMuteResult.Failed,
        [494] = ParticipantMuteResult.Failed,
        [499] = ParticipantMuteResult.Failed,
    };

    /// <summary>AudioBridge error 489: the room is protected by a secret the request did not carry correctly.</summary>
    public const int UNAUTHORIZED_ERROR_CODE = 489;

    /// <summary>What the table says about an error code, or null for a code it does not list.</summary>
    public static ParticipantMuteResult? ModeratorErrorMeaning(int pCode)
        => s_moderatorErrors.TryGetValue(pCode, out ParticipantMuteResult r) ? r : null;

    /// <summary>
    /// Reads the plugin's answer to a moderator request that names a room and a participant (mute, unmute, kick), by the
    /// table above. Every answer is one of Applied, NotInRoom, Unsupported or Failed; an answer that is none of the
    /// listed ones, or has no code, is a Failed that is logged with what the plugin said.
    /// </summary>
    public static ParticipantMuteResult ClassifyModeratorReply(AudioBridgeResp pResp, string pRequest)
    {
        if (pResp is null)
            return ParticipantMuteResult.Failed;
        if (pResp.isSuccess)
            return ParticipantMuteResult.Applied;
        int code = pResp.AudioBridgeErrorCode;
        if (s_moderatorErrors.TryGetValue(code, out ParticipantMuteResult meaning))
        {
            if (meaning == ParticipantMuteResult.Failed)
                m_log.LogWarning("{0} {1} request was refused: {2}", LogHeader, pRequest, pResp.ToString());
            return meaning;
        }
        m_log.LogWarning("{0} {1} request got an answer that is not a success and has no listed error code ({2}): {3}",
            LogHeader, pRequest, code, pResp.ToString());
        return ParticipantMuteResult.Failed;
    }
}
