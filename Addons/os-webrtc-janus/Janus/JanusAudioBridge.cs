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

using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

namespace osWebRtcVoice;

// Encapsulization of a Session to the Janus server
public partial class JanusAudioBridge : JanusPlugin
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[JANUS AUDIO BRIDGE]";

    // Wrapper around the Janus session connection to Janus-gateway. The Janus plugin
    // name is supplied by the caller (from [JanusWebRtcVoice] PluginName) rather
    // than hardcoded, so a config-compatible mixer (e.g. janus.plugin.slvoice)
    // can be selected without a code change. Where it is read (WebRtcJanusService),
    // an unset key defaults to janus.plugin.audiobridge.
    public JanusAudioBridge(JanusSession pSession, string pPluginName) : this(pSession, pPluginName, string.Empty) { }

    /// <param name="pGridId">The grid's identity for non-spatial room numbers: see
    /// <see cref="ReadGridId"/>. Empty keeps the grid-less derivation.</param>
    public JanusAudioBridge(JanusSession pSession, string pPluginName, string pGridId) : this(pSession, pPluginName, pGridId, false) { }

    /// <param name="pDeclareVisAuthority">Create spatial "local" rooms with vis_authority=true.
    /// False omits that field from the create body.</param>
    public JanusAudioBridge(JanusSession pSession, string pPluginName, string pGridId, bool pDeclareVisAuthority) : base(pSession, pPluginName)
    {
        GridId = pGridId ?? string.Empty;
        DeclareVisAuthority = pDeclareVisAuthority;
        // m_log.LogDebug("{0} JanusAudioBridge constructor (plugin={1})", LogHeader, pPluginName);
    }

    public override void Dispose()
    {
        if (IsConnected)
        {
            // Close the handle

        }
        base.Dispose();
    }

    public async Task<AudioBridgeResp> SendAudioBridgeMsg(PluginMsgReq pMsg)
    {
        AudioBridgeResp ret = null;
        try
        {
            ret = new AudioBridgeResp(await SendPluginMsg(pMsg));
        }
        catch (Exception e)
        {
            m_log.LogError("{0} SendPluginMsg. Exception {1}", LogHeader, e);
        }
        return ret;
    }

    /// <summary>
    /// Create a room with the given criteria. This talks to Janus to create the room.
    /// If the room with this RoomId already exists, just return it.
    /// Janus could create and return the RoomId but this presumes that the Janus server
    /// is only being used for our voice service.
    /// </summary>
    /// <param name="pRoomId">integer room ID to create</param>
    /// <param name="pSpatial">boolean on whether room will be spatial or non-spatial</param>
    /// <param name="pRoomDesc">added as "description" to the created room</param>
    /// <returns></returns>
    // Create room 'pRoomId', treating an already-existing room (Janus error 486) as
    // reuse. If the first attempt is inconclusive (non-486 error, unexpected return
    // code, or an exception), re-attempt ONCE: under a cross-PROCESS race (another
    // region server process created the room in the same instant) the room now exists and the
    // re-attempt returns 486 -> reuse. No in-process lock can cover that cross-process
    // case, so this re-check is the load-bearing correctness. Janus keys rooms by
    // number, so re-attempting create cannot produce a duplicate room.
    public Task<JanusRoom> CreateRoom(int pRoomId, bool pSpatial, string pRoomDesc)
        => CreateRoom(pRoomId, pSpatial, pRoomDesc, false);

    /// <param name="pVisAuthority">Send "vis_authority": true in the create body.</param>
    public async Task<JanusRoom> CreateRoom(int pRoomId, bool pSpatial, string pRoomDesc, bool pVisAuthority)
    {
        JanusRoom ret = await CreateWithRecheck(() => TryCreateRoomOnce(pRoomId, pSpatial, pRoomDesc, pVisAuthority)).ConfigureAwait(false);
        if (ret is null)
        {
            m_log.LogError("{LogHeader} CreateRoom. Room {RoomId} creation failed after re-check", LogHeader, pRoomId);
        }
        return ret;
    }

    public async Task<bool> DestroyRoom(JanusRoom janusRoom)
    {
        bool ret = false;
        try
        {
            JanusMessageResp resp = await SendPluginMsg(new AudioBridgeDestroyRoomReq(janusRoom.RoomId));
            ret = true;
        }
        catch (Exception e)
        {
            m_log.LogError("{0} DestroyRoom. Exception {1}", LogHeader, e);
        }
        return ret;
    }

    // Constant used to denote that this is a spatial audio room for the region (as opposed to parcels)
    public const int REGION_ROOM_ID = -999;

    // Calculate a room number for the given parameters. The room number is a hash of the parameters.
    // The attempt is to deterministicly create a room number so all regions will generate the
    //     same room number across sessions and across the grid.
    // getHashCode() is not deterministic across sessions.
    /// <param name="pGridId">Enters ONLY the "multiagent" arm. The "local" arm is per
    /// region+parcel and ignores it, so a spatial room number does not depend on the grid id.</param>
    public static int CalcRoomNumber(string pGridId, string pRegionId, string pChannelType, int pParcelLocalID, string pChannelID)
    {
        var hasher = new BHasherMdjb2();
        // If there is a channel specified it must be group
        switch (pChannelType)
        {
            case "local":
                // A "local" channel is unique to the region and parcel
                hasher.Add(pRegionId);
                hasher.Add(pChannelType);
                hasher.Add(pParcelLocalID);
                break;
            case "multiagent":
                // A "multiagent" channel is unique to the grid: grid id + channel (the session id of the call
                // or conference) + type. An empty grid id (unconfigured grid) adds nothing to the hash,
                // which gives the grid-less derivation. Any channel_type added below must fold the grid id
                // in the same way before it is admitted.
                if (!string.IsNullOrEmpty(pGridId))
                    hasher.Add(pGridId);
                hasher.Add(pChannelID);
                hasher.Add(pChannelType);
                break;
            default:
                throw new Exception("Unknown channel type: " + pChannelType);
        }   
        var hashed = hasher.Finish();
        // The "Abs()" is because Janus room number must be a positive integer
        // And note that this is the BHash.GetHashCode() and not Object.getHashCode().
        int roomNumber = FoldHashToRoom(hashed.GetHashCode());
        return roomNumber;
    }
    /// <summary>The room for these parameters, made to exist by ASKING THE MIXER: a create is sent every time, and
    /// "already exists" (486) is success on every path. The viewer provision calls
    /// this before its JSEP join, so the join never meets a room this process merely believed in. Returns a JanusRoom
    /// bound to THIS bridge's plugin handle, or null when the create failed.</summary>
    public async Task<JanusRoom> SelectRoom(string pRegionId, string pChannelType, bool pSpatial, int pParcelLocalID, string pChannelID)
    {
        int roomNumber = CalcRoomNumber(GridId, pRegionId, pChannelType, pParcelLocalID, pChannelID);
        m_log.LogDebug("{0} SelectRoom: roomNumber={1}", LogHeader, roomNumber);
        string roomDesc = pRegionId + "/" + pChannelType + "/" + pParcelLocalID + "/" + pChannelID;
        bool visAuthority = ShouldDeclareVisAuthority(DeclareVisAuthority, pChannelType);
        return await SelectRoomCoalesced(roomNumber, () => CreateRoom(roomNumber, pSpatial, roomDesc, visAuthority))
            .ConfigureAwait(false);
    }

    public override void Handle_Event(JanusMessageResp pResp)
    {
        base.Handle_Event(pResp);
        AudioBridgeResp abResp = new AudioBridgeResp(pResp);
        if (abResp is not null && abResp.AudioBridgeReturnCode == "event")
        {
            // An audio bridge event!
            m_log.LogDebug("{0} Handle_Event. {1}", LogHeader, abResp.ToString());
        }

    }
    public override void Handle_Message(JanusMessageResp pResp)
    {
        base.Handle_Message(pResp);
        AudioBridgeResp abResp = new AudioBridgeResp(pResp);
        if (abResp is not null && abResp.AudioBridgeReturnCode == "event")
        {
            // An audio bridge event!
            m_log.LogDebug("{0} Handle_Event. {1}", LogHeader, abResp.ToString());
        }

    }
}
