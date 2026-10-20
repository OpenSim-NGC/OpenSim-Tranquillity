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

using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice;

// Encapsulization of a Session to the Janus server
public class JanusRoom : IDisposable
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[JANUS ROOM]";

    public int RoomId { get; private set; }

    private JanusPlugin _AudioBridge;

    // Wrapper around the session connection to Janus-gateway
    public JanusRoom(JanusPlugin pAudioBridge, int pRoomId)
    {
        _AudioBridge = pAudioBridge;
        RoomId = pRoomId;
    }

    public void Dispose()
    {
        // Close the room
    }

    // Returns (Joined, ErrorCode). ErrorCode is the mixer's error_code on failure (0 if
    // none/success), so the caller can distinguish a capacity rejection (ROOM_FULL=495)
    // from every other join failure. A tuple rather than an out-parameter because async
    // methods cannot have out params, and rather than a field because JanusRoom is shared
    // across joiners (a mutable field would race). JoinRoom stays policy-free.
    public async Task<(bool Joined, int ErrorCode)> JoinRoom(JanusViewerSession pVSession)
    {
        bool joined = false;
        int errorCode = 0;
        try
        {
            // m_log.LogDebug("{0} JoinRoom. New joinReq for room {1}", LogHeader, RoomId);

            // Discovered that AudioBridge doesn't care if the data portion is present
            //    and, if removed, the viewer complains that the "m=" sections are
            //    out of order. Not "cleaning" (removing the data section) seems to work.
            // string cleanSdp = CleanupSdp(pSdp);
            var joinReq = new AudioBridgeJoinRoomReq(RoomId, pVSession.AgentId.ToString());
            // Mint the capability for this join, bound to the agent, the viewer session, the room and the arming
            // state the sim believes current. Off by default, and then this request is unchanged. The capability
            // is never logged, and the viewer never sees it: this request goes from the region to the mixer.
            if (_AudioBridge is JanusAudioBridge capBridge && capBridge.JoinCapabilityEnabled)
            {
                (string epoch, uint generation) = JoinCapabilityAuthority.Resolve(RoomId);
                string capability = JoinCapability.Mint(capBridge.JoinCapabilitySecret, pVSession.AgentId.ToString(),
                    pVSession.ViewerSessionID, RoomId, epoch, generation, JoinCapability.NowUnix());
                if (capability is not null)
                    joinReq.SetJoinCapability(capability, pVSession.ViewerSessionID);
                else
                    m_log.LogWarning("{LogHeader} JoinRoom. JoinCapabilityEnabled but nothing was minted for room {RoomId} " +
                        "(is [JanusWebRtcVoice] JoinCapabilitySecret set?); joining without one", LogHeader, RoomId);
            }
            // joinReq.SetJsep("offer", cleanSdp);
            joinReq.SetJsep("offer", pVSession.Offer);

            JanusMessageResp resp = await _AudioBridge.SendPluginMsg(joinReq);
            AudioBridgeJoinRoomResp joinResp = new AudioBridgeJoinRoomResp(resp);

            if (joinResp is not null && joinResp.AudioBridgeReturnCode == "joined")
            {
                pVSession.ParticipantId = joinResp.ParticipantId;
                pVSession.Answer = joinResp.Jsep;
                joined = true;
                m_log.LogDebug("{0} JoinRoom. Joined room {1}. Participant={2}", LogHeader, RoomId, pVSession.ParticipantId);
            }
            else
            {
                // Surface the mixer's error_code (0 if absent; ROOM_FULL=495 on a capacity
                // rejection). AudioBridgeErrorCode already parses it — it was just discarded.
                errorCode = joinResp?.AudioBridgeErrorCode ?? 0;
                // A capability refusal answers 496 with a machine-readable `reason` (cap_expired, cap_replayed, ...).
                // Named on its own line so an operator sees which check failed; the capability itself is never
                // logged, and the mixer never echoes it.
                string capReason = joinResp?.PluginRespDataString("reason");
                if (!string.IsNullOrEmpty(capReason))
                    m_log.LogError("{LogHeader} JoinRoom. Room {RoomId} refused the join: {JoinRefusalReason} (error_code={JoinErrorCode})",
                        LogHeader, RoomId, capReason, errorCode);
                // A 485 means the room vanished between the create and this join; the caller logs it
                // ONCE at WARN and fails the provision back to the viewer, so here it is only DEBUG. Every other
                // failure is still an ERROR.
                if (errorCode == WebRtcJanusService.JANUS_NO_SUCH_ROOM_ERROR_CODE)
                    m_log.LogDebug("{LogHeader} JoinRoom. Room {RoomId} answered 485 No such room. Resp={JoinResponse}",
                        LogHeader, RoomId, joinResp?.ToString());
                else
                    m_log.LogError("{LogHeader} JoinRoom. Failed to join room {RoomId} (error_code={JoinErrorCode}). Resp={JoinResponse}",
                        LogHeader, RoomId, errorCode, joinResp?.ToString());
            }
        }
        catch (Exception e)
        {
            m_log.LogError("{0} JoinRoom. Exception {1}", LogHeader, e);
        }
        return (joined, errorCode);
    }

    // TODO: this doesn't work.
    // Not sure if it is needed. Janus generates Hangup events when the viewer leaves.
    /*
    public async Task<bool> Hangup(JanusViewerSession pAttendeeSession)
    {
        bool ret = false;
        try
        {
        }
        catch (Exception e)
        {
            m_log.LogError("{0} LeaveRoom. Exception {1}", LogHeader, e);
        }
        return ret;
    }
    */

    // Mixer JANUS_SLVOICE_ERROR_NOT_JOINED (janus_slvoice.c): the participant is already out of the room.
    public const int NOT_JOINED_ERROR_CODE = 487;

    // TRUE when the mixer confirms the
    // leave ("left"/"success") or answers NOT_JOINED (487) - the participant is already gone, which is now
    // the normal case after a PeerConnection hangup (the mixer leaves on hangup_media), so it must not
    // WARN on every teardown. Anything else (an error, a timeout, no reply) is FALSE with a WARN.
    public async Task<bool> LeaveRoom(JanusViewerSession pAttendeeSession)
    {
        bool ret = false;
        try
        {
            JanusMessageResp resp = await _AudioBridge.SendPluginMsg(
                new AudioBridgeLeaveRoomReq(RoomId, pAttendeeSession.ParticipantId));
            AudioBridgeResp leaveResp = resp is null ? null : new AudioBridgeResp(resp);
            if (leaveResp is not null && (leaveResp.AudioBridgeReturnCode == "left" || leaveResp.isSuccess))
            {
                ret = true;
            }
            else if (leaveResp is not null && leaveResp.AudioBridgeErrorCode == NOT_JOINED_ERROR_CODE)
            {
                ret = true;
                m_log.LogDebug("{LogHeader} LeaveRoom. Participant {ParticipantId} already out of room {RoomId} (NOT_JOINED)",
                    LogHeader, pAttendeeSession.ParticipantId, RoomId);
            }
            else
            {
                m_log.LogWarning("{LogHeader} LeaveRoom. Leave of participant {ParticipantId} from room {RoomId} not confirmed (audiobridge={ReturnCode}, error_code={ErrorCode}). Resp={Resp}",
                    LogHeader, pAttendeeSession.ParticipantId, RoomId, leaveResp?.AudioBridgeReturnCode ?? "-",
                    leaveResp?.AudioBridgeErrorCode ?? 0, resp?.ToString() ?? "null");
            }
        }
        catch (Exception e)
        {
            m_log.LogError("{0} LeaveRoom. Exception {1}", LogHeader, e);
        }
        return ret;
    }

}
