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

using OpenSim.Framework;
using OpenSim.Services.Base;

using OpenMetaverse.StructuredData;
using OpenMetaverse;

using Nini.Config;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

public partial class WebRtcJanusService : ServiceBase, IWebRtcVoiceService
{
    private static readonly ILogger _log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[JANUS WEBRTC SERVICE]";

    private readonly IConfigSource _Config;
    private bool _Enabled = false;

    private string _JanusServerURI = string.Empty;
    private string _JanusAPIToken = string.Empty;
    private string _JanusAdminURI = string.Empty;
    private string _JanusAdminToken = string.Empty;

    private bool _MessageDetails = false;

    // An extra "viewer session" that is created initially. Used to verify the service
    //     is working and for a handle for the console commands.
    private JanusViewerSession _ViewerSession;

    // Test seam: builds the HttpMessageHandler for each JanusSession this service creates (null = the network).
    // A factory, not one handler, because disposing the HttpClient of a Janus session disposes its handler.
    private readonly Func<HttpMessageHandler> _httpHandlerFactory;

    public WebRtcJanusService(IConfigSource pConfig) : this(pConfig, null)
    {
    }

    public WebRtcJanusService(IConfigSource pConfig, Func<HttpMessageHandler> pHttpHandlerFactory) : base(pConfig)
    {
        _httpHandlerFactory = pHttpHandlerFactory;
        Assembly assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetName().Version?.ToString() ?? "unknown";

        _log.LogDebug("{0} WebRtcJanusService version {1}", LogHeader, version);
        _Config = pConfig;
        IConfig webRtcVoiceConfig = _Config.Configs["WebRtcVoice"];

        if (webRtcVoiceConfig is not null)
        {
            _Enabled = webRtcVoiceConfig.GetBoolean("Enabled", false);
            IConfig janusConfig = _Config.Configs["JanusWebRtcVoice"];
            if (_Enabled && janusConfig is not null)
            {
                _JanusServerURI = janusConfig.GetString("JanusGatewayURI", string.Empty);
                _JanusAPIToken = janusConfig.GetString("APIToken", string.Empty);
                _JanusAdminURI = janusConfig.GetString("JanusGatewayAdminURI", string.Empty);
                _JanusAdminToken = janusConfig.GetString("AdminAPIToken", string.Empty);
                WarnOnShortJanusSecrets();
                ReadTlsConfig(janusConfig);
                ReadPluginNameConfig(janusConfig);
                MixerOnlySwitches.DisableOnStockPlugin(_Config, _log);
                ReadGridIdConfig();
                ReadArmingConfig(webRtcVoiceConfig, janusConfig);
                ReadRoomReaperConfig(janusConfig);
                // Debugging options
                _MessageDetails = janusConfig.GetBoolean("MessageDetails", false);
                ReadRequestTimeoutConfig(janusConfig);

                if (string.IsNullOrEmpty(_JanusServerURI) || string.IsNullOrEmpty(_JanusAPIToken) ||
                    string.IsNullOrEmpty(_JanusAdminURI) || string.IsNullOrEmpty(_JanusAdminToken))
                {
                    _log.LogError($"{LogHeader} JanusWebRtcVoice configuration section missing required fields");
                    _Enabled = false;
                }

                if (_Enabled)
                {
                    if(!StartConnectionToJanus())
                    {
                        _log.LogError($"{LogHeader} failed connection to Janus Gateway. Disabled");
                        _Enabled=false;
                        return;
                    }
                    RegisterConsoleCommands();
                    StartRoomReaper();
                    _log.LogInformation($"{LogHeader} Enabled");
                }
            }
            else
            {
                _log.LogError($"{LogHeader} No JanusWebRtcVoice configuration section");
                _Enabled = false;
            }
        }
        else
        {
            _log.LogError($"{LogHeader} No WebRtcVoice configuration section");
            _Enabled = false;
        }
    }

    // Here an initial session is created and then a handle to the audio bridge plugin
    //    is created for the console commands. Since webrtc PeerConnections that are created
    //    my Janus are per-session, the other sessions will be created by the viewer requests.
    private bool StartConnectionToJanus()
    {
        _log.LogDebug("{0} StartConnectionToJanus", LogHeader);
            _ViewerSession = new JanusViewerSession(this);
        //bad
        return ConnectToSessionAndAudioBridge(_ViewerSession).Result;
    }

    private async Task<bool> ConnectToSessionAndAudioBridge(JanusViewerSession pViewerSession)
    {
        JanusSession janusSession = new JanusSession(_JanusServerURI, _JanusAPIToken, _JanusAdminURI, _JanusAdminToken, _MessageDetails,
            CreateHttpHandler());
        janusSession.JanusRequestTimeout = TimeSpan.FromMilliseconds(_JanusRequestTimeoutMs);
        if (await janusSession.CreateSession().ConfigureAwait(false))
        {
            _log.LogDebug("{0} JanusSession created", LogHeader);

            // Once the session is created, create a handle to the plugin for rooms
            JanusAudioBridge audioBridge = new JanusAudioBridge(janusSession, _JanusPluginName, _GridId, _DeclareVisAuthority)
            {
                // What JanusRoom.JoinRoom mints with. Both stay at their defaults with the switch off.
                JoinCapabilityEnabled = _JoinCapabilityEnabled,
                JoinCapabilitySecret = _JoinCapabilitySecret,
                // Rooms this bridge creates are watched by the empty-room reaper (null when it is off).
                RoomCreated = _roomReaper is null ? null : _roomReaper.NoteCreated,
            };

            if (await audioBridge.Activate(_Config).ConfigureAwait(false))
            {
                _log.LogDebug($"{LogHeader} AudioBridgePluginHandle created");
                // Requests through the capabilities will create rooms

                janusSession.AddPlugin(audioBridge);
                    
                pViewerSession.VoiceServiceSessionId = janusSession.SessionId;
                pViewerSession.Session = janusSession;
                pViewerSession.AudioBridge = audioBridge;
                janusSession.OnDisconnect += Handle_Disconnect;
                janusSession.OnHangup += Handle_Hangup;
                return true;
            }
            _log.LogError($"{LogHeader} JanusPluginHandle not created");
        }
        else
        {
            _log.LogError($"{LogHeader} JanusSession not created");
        }
        // Never leave a half-built session behind. Created-but-attach-failed is a live Janus session
        // (with its long poll) that nothing references; destroy it (bounded by RequestTimeoutMs,), then
        // release its HttpClient.
        if (janusSession.IsConnected)
            await janusSession.DestroySession().ConfigureAwait(false);
        janusSession.Dispose();
        return false;
    }

    private void Handle_Hangup(EventResp pResp)
    {
        if (pResp is not null)
        {
            var sessionId = pResp.sessionId;
            _log.LogDebug($"{LogHeader} Handle_Hangup: {pResp.RawBody}, sessionId={sessionId}");
            if (VoiceViewerSession.TryGetViewerSessionByVSSessionId(sessionId, out IVoiceViewerSession viewerSession))
            {
                // There is a viewer session associated with this session
                DisconnectViewerSession(viewerSession as JanusViewerSession);
            }
            else if (!MarkServiceSessionDisconnected(pResp))
            {
                _log.LogDebug($"{LogHeader} Handle_Hangup: no session found. SessionId={sessionId}");
            }
        }
    }

    // Disconnect the viewer session. This is called when the viewer logs out or hangs up.
    private void DisconnectViewerSession(JanusViewerSession pViewerSession)
    {
        if (pViewerSession is not null)
        {
            Task.Run(() =>
            {
                VoiceViewerSession.RemoveViewerSession(pViewerSession.ViewerSessionID);
                // No need to wait for the session to be shutdown
                _ = pViewerSession.Shutdown();
            });
        }
    }   

    // The pRequest parameter is a straight conversion of the JSON request from the client.
    // This is the logic that takes the client's request and converts it into
    //     operations on rooms in the audio bridge.
    // IWebRtcVoiceService.ProvisionVoiceAccountRequest
    public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        return ProvisionVoiceAccountRequestBAD(pSession, pRequest, pUserID, pSceneID).Result;
    }

    public async Task<OSDMap> ProvisionVoiceAccountRequestBAD(IVoiceViewerSession pSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        OSDMap ret = null;
        string errorMsg = null;
        int errorCode = 0;   // mixer error_code to propagate on failure (0 = none); only ROOM_FULL is acted on downstream
        JanusViewerSession viewerSession = pSession as JanusViewerSession;
        if (viewerSession is not null)
        {
            if (viewerSession.Session is null)
            {
                // This is a new session so we must create a new session and handle to the audio bridge
                if (!await ConnectToSessionAndAudioBridge(viewerSession).ConfigureAwait(false))
                {
                    // The connect failed (Janus unreachable, or the plugin attach failed;
                    // ConnectToSessionAndAudioBridge has already destroyed anything it half-built). The result used
                    // to be ignored, and the offer's SelectRoom then threw NullReferenceException on the null
                    // AudioBridge — a 500, not a failure map. Answer the failure-map shape the other arms return.
                    _log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: janus unavailable - no Janus session/plugin handle for agent {pUserID}");
                    return ProvisionResponseBuilder.BuildFailure("janus unavailable", 0);
                }
            }

            // TODO: need to keep count of users in a room to know when to close a room
            bool isLogout = pRequest.TryGetBool("logout", out bool lgout) && lgout;
            if (isLogout)
            {
                // The client is logging out. Exit the room.
                if (viewerSession.Room is not null)
                {
                    await viewerSession.Room.LeaveRoom(viewerSession);
                    viewerSession.Room = null;
                }
                // Remove the registry entry the same way hangup does — DisconnectViewerSession,
                // not bare RemoveViewerSession, because the live Janus session/handle must be shut
                // down, not just forgotten (a forgotten entry is unreachable by the close capture).
                DisconnectViewerSession(viewerSession);
                return ProvisionResponseBuilder.BuildClosed();
            }

            // Get the parameters that select the room
            // To get here, voice_server_type has already been checked to be 'webrtc' and the region module
            // has admitted channel_type as 'local' (parcel/estate checks) or 'multiagent' (a call or conference
            // the module knows: channel + member + credentials), or has passed the body through unexamined
            // while no call feature is on.
            int parcel_local_id = pRequest.TryGetInt("parcel_local_id", out int pli) ? pli : JanusAudioBridge.REGION_ROOM_ID;
            // The viewer's multiagent body names the session under `channel` (SL llvoicewebrtc.cpp:3341-3343,
            // Firestorm llvoicewebrtc.cpp:3695-3697), not `channel_id`. Reading only channel_id gave "" and put every
            // call on the grid into one room. `channel_id` stays as a fallback.
            string channel_id = pRequest.TryGetString("channel", out string chn) && !string.IsNullOrEmpty(chn)
                ? chn
                : pRequest.TryGetString("channel_id", out string cli) ? cli : string.Empty;
            string channel_credentials = pRequest.TryGetString("credentials", out string cred) ? cred : string.Empty;
            string channel_type = pRequest["channel_type"].AsString();
            bool isSpatial = channel_type == "local";
            string voice_server_type = pRequest["voice_server_type"].AsString();

            _log.LogDebug("{0} ProvisionVoiceAccountRequest: parcel_id={1} channel_id={2} channel_type={3} voice_server_type={4}", LogHeader, parcel_local_id, channel_id, channel_type, voice_server_type); 

            if (pRequest.TryGetOSDMap("jsep", out OSDMap jsep))
            {
                // The jsep is the SDP from the client. This is the client's request to connect to the audio bridge.
                string jsepType = jsep["type"].AsString();
                string jsepSdp = jsep["sdp"].AsString();
                if (jsepType == "offer")
                {
                    // The client is sending an offer. Find the right room and join it.
                    // _log.LogDebug("{0} ProvisionVoiceAccountRequest: jsep type={1} sdp={2}", LogHeader, jsepType, jsepSdp);
                    viewerSession.Room = await viewerSession.AudioBridge.SelectRoom(pSceneID.ToString(),
                                                        channel_type, isSpatial, parcel_local_id, channel_id).ConfigureAwait(false);
                    if (viewerSession.Room is null)
                    {
                        errorMsg = "room selection failed";
                        _log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: room selection failed");
                    }
                    else {
                        viewerSession.Offer = jsepSdp;
                        viewerSession.OfferOrig = jsepSdp;
                        viewerSession.Spatial = isSpatial;
                        viewerSession.AgentId = pUserID;
                        // ONE join. SelectRoom above has just had the mixer create (or confirm) the room, so
                        // this JSEP join never meets a room the sim merely believed in. It is never re-sent on this handle:
                        // Janus core builds the handle's ICE agent from the offer before the plugin looks at the room, and
                        // refuses a second JSEP join on the same handle with error 490.
                        var joinResult = await viewerSession.Room.JoinRoom(viewerSession).ConfigureAwait(false);
                        if (joinResult.Joined)
                        {
                            // Additive: the joined room number, so the region can record which mixer room this agent is actually in.
                            // Success branch only; failure maps carry no room.
                            ret = ProvisionResponseBuilder.BuildSuccess(viewerSession.Answer, viewerSession.ViewerSessionID, viewerSession.Room.RoomId);
                        }
                        else if (joinResult.ErrorCode == JANUS_ROOM_FULL_ERROR_CODE)
                        {
                            // Capacity rejection: carry the code so the CAP handler returns HTTP
                            // 409 Conflict (viewer -> ERROR_CHANNEL_FULL). Distinct from every
                            // other JoinRoom failure below, which stay generic (no error_code).
                            errorMsg = "room is full";
                            errorCode = joinResult.ErrorCode;
                            _log.LogWarning($"{LogHeader} ProvisionVoiceAccountRequest: room full (ROOM_FULL {joinResult.ErrorCode})");
                            viewerSession.Room = null;   // never joined: a later logout must not send a leave for it
                        }
                        else if (joinResult.ErrorCode == JANUS_NO_SUCH_ROOM_ERROR_CODE)
                        {
                            // The room vanished between the create a moment ago and this join - the
                            // mixer's grace sweep landing in between. Not retried here (see above); the viewer's own
                            // retry sends a fresh create and a fresh handle. One WARN, no ERROR.
                            errorMsg = "JoinRoom failed";   // the failure map is what it always was: no error_code
                            _log.LogWarning("{LogHeader} ProvisionVoiceAccountRequest: room {Room} was gone at the join (485) " +
                                "although the mixer had just answered its create; failing back to the viewer, whose retry " +
                                "creates it again", LogHeader, viewerSession.Room.RoomId);
                            viewerSession.Room = null;   // never joined: a later logout must not send a leave for it
                        }
                        else
                        {
                            errorMsg = "JoinRoom failed";
                            _log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: JoinRoom failed (error_code={joinResult.ErrorCode})");
                            viewerSession.Room = null;   // never joined: a later logout must not send a leave for it
                        }
                    }
                }
                else
                {
                    errorMsg = "jsep type not offer";
                    _log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: jsep type={jsepType} not offer");
                }
            }
            else
            {
                errorMsg = "no jsep";
                _log.LogDebug($"{LogHeader} ProvisionVoiceAccountRequest: no jsep. req={pRequest}");
            }
        }
        else
        {
            errorMsg = "viewersession not JanusViewerSession";
            _log.LogError("{0} ProvisionVoiceAccountRequest: viewersession not JanusViewerSession", LogHeader);
        }

        if (!string.IsNullOrEmpty(errorMsg) && ret is null)
        {
            // The provision failed so build an error message to return.
            // Only a capacity rejection sets errorCode; the CAP handler maps 495 -> HTTP 409.
            // Every other failure leaves it 0 (no error_code field) and keeps its status.
            ret = ProvisionResponseBuilder.BuildFailure(errorMsg, errorCode);
        }

        return ret;
    }

    // IWebRtcVoiceService.VoiceAccountBalanceRequest
    public OSDMap VoiceSignalingRequest(IVoiceViewerSession pSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        return VoiceSignalingRequestBAD(pSession, pRequest, pUserID, pSceneID).Result;
    }

    public async Task<OSDMap> VoiceSignalingRequestBAD(IVoiceViewerSession pSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        OSDMap ret = null;
        JanusViewerSession viewerSession = pSession as JanusViewerSession;
        JanusMessageResp resp = null;
        if (viewerSession is not null && viewerSession.Session is null)
        {
            // Signalling for a viewer session with no Janus session (never provisioned, or
            // already shut down) used to throw NullReferenceException on Session. Answer the error map instead.
            _log.LogWarning($"{LogHeader} VoiceSignalingRequest: viewer session {viewerSession.ViewerSessionID} of {pUserID} has no Janus session");
            return new OSDMap
            {
                { "response", "error" },
                { "error", "no voice session" }
            };
        }
        if (viewerSession is not null)
        {
            // The request should be an array of candidates
            if (pRequest.TryGetOSDMap("candidate", out OSDMap candidate))
            {
                if (candidate.TryGetBool("completed", out bool iscompleted) && iscompleted)
                {
                    // The client has finished sending candidates
                    resp = await viewerSession.Session.TrickleCompleted(viewerSession).ConfigureAwait(false);
                    _log.LogDebug($"{LogHeader} VoiceSignalingRequest: candidate completed");
                }
                else if (candidate.TryGetString("candidate", out string candidateLine) && !string.IsNullOrEmpty(candidateLine))
                {
                    // The singular form {candidate:{candidate, sdpMid, sdpMLineIndex}} used to fall
                    // into an empty else and was silently dropped. Pass it on as a one-element candidate list.
                    OSDArray single = new OSDArray
                    {
                        new OSDMap
                        {
                            { "candidate", candidateLine },
                            { "sdpMid", candidate["sdpMid"].AsString() },
                            { "sdpMLineIndex", candidate["sdpMLineIndex"].AsLong() }
                        }
                    };
                    resp = await viewerSession.Session.TrickleCandidates(viewerSession, single).ConfigureAwait(false);
                    _log.LogDebug($"{LogHeader} VoiceSignalingRequest: 1 candidate (singular form)");
                }
                else
                {
                    _log.LogWarning($"{LogHeader} VoiceSignalingRequest: 'candidate' has neither 'completed' nor a candidate line");
                }
            }
            else if (pRequest.TryGetOSDArray("candidates", out OSDArray candidates))
            {
                OSDArray candidatesArray = new OSDArray();
                foreach (OSDMap cand in candidates)
                {
                    candidatesArray.Add(new OSDMap() {
                        { "candidate", cand["candidate"].AsString() },
                        { "sdpMid", cand["sdpMid"].AsString() },
                        { "sdpMLineIndex", cand["sdpMLineIndex"].AsLong() }
                    });
                }
                resp = await viewerSession.Session.TrickleCandidates(viewerSession, candidatesArray).ConfigureAwait(false);
                _log.LogDebug($"{LogHeader} VoiceSignalingRequest: {candidatesArray.Count} candidates");
            }
            else
            {
                _log.LogError($"{LogHeader} VoiceSignalingRequest: no 'candidate' or 'candidates'");
            }
        }
        if (resp is null)
        {
            _log.LogError($"{LogHeader} VoiceSignalingRequest: no response so returning error");
            ret = new OSDMap
            {
                { "response", "error" }
            };
        }
        else
        {
            ret = resp.RawBody;
        }
        return ret;
    }

    // This module should not be invoked with this signature
    // IWebRtcVoiceService.ProvisionVoiceAccountRequest
    public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        throw new NotImplementedException();
    }

    // This module should not be invoked with this signature
    // IWebRtcVoiceService.VoiceSignalingRequest
    public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        throw new NotImplementedException();
    }

    // The viewer session object holds all the connection information to Janus.
    // IWebRtcVoiceService.CreateViewerSession
    public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        return new JanusViewerSession(this)
        {
            AgentId = pUserID,
            RegionId = pSceneID
        };
    }

    // ======================================================================================================
    private void RegisterConsoleCommands()
    {
        // No console when the service is hosted without one (unit tests construct it directly).
        if (_Enabled && MainConsole.Instance is not null) {
            MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus info",
                "janus info",
                "Show Janus server information",
                HandleJanusInfo);
            MainConsole.Instance.Commands.AddCommand("Webrtc", false, "janus list rooms",
                "janus list rooms",
                "List the rooms on the Janus server",
                HandleJanusListRooms);
            // List rooms
            // List participants in a room
        }
    }

    private void HandleJanusInfo(string module, string[] cmdparms)
    {
        // Reconnects a dead service session first instead of using it as-is.
        JanusViewerSession svc = EnsureServiceSessionAsync().Result;
        if (svc is not null && svc.Session is not null)
        {
            WriteOut("{0} Janus session: {1}", LogHeader, svc.Session.SessionId);
            string infoURI = svc.Session.JanusServerURI + "/info";

            var resp = svc.Session.GetFromJanus(infoURI).Result;

            if (resp is not null)
                MainConsole.Instance.Output(resp.ToJson());
        }
        else
        {
            MainConsole.Instance.Output("No Janus service session (Janus unreachable)");
        }
    }

    private void HandleJanusListRooms(string module, string[] cmdparms)
    {
        // ServiceListRoomsAsync reconnects the service session when it is dead and retries once,
        // so "janus list rooms" heals after a mixer restart instead of failing until the region restarts.
        var resp = ServiceListRoomsAsync().Result;
        var ab = _ViewerSession?.AudioBridge;
        if (ab is null)
        {
            MainConsole.Instance.Output("Failed to get room list (no Janus service session)");
        }
        else
        {
            if (resp is not null && resp.isSuccess)
            {
                if (resp.PluginRespData.TryGetValue("list", out OSD list))
                {
                    MainConsole.Instance.Output("");
                    MainConsole.Instance.Output(
                        "  {0,10} {1,15} {2,5} {3,10} {4,7} {5,7}",
                        "Room", "Description", "Num", "SampleRate", "Spatial", "Recording");
                    foreach (OSDMap room in list as OSDArray)
                    {
                        int roomid = room["room"].AsInteger();
                        MainConsole.Instance.Output(
                            "  {0,10} {1,15} {2,5} {3,10} {4,7} {5,7}",
                            roomid, room["description"], room["num_participants"],
                            room["sampling_rate"], room["spatial_audio"], room["record"]);

                        var participantResp = ab.SendAudioBridgeMsg(new AudioBridgeListParticipantsReq(roomid)).Result;

                        if (participantResp is not null && participantResp.AudioBridgeReturnCode == "participants")
                        {
                            if (participantResp.PluginRespData.TryGetValue("participants", out OSD participants))
                            {
                                foreach (OSDMap participant in participants as OSDArray)
                                {
                                    MainConsole.Instance.Output("      {0}/{1},muted={2},talking={3},pos={4}",
                                        participant["id"].AsLong(), participant["display"], participant["muted"],
                                        participant["talking"], participant["spatial_position"]);
                                }
                            }
                        }
                    }
                }
                else
                {
                    MainConsole.Instance.Output("No rooms");
                }
            }
            else
            {
                MainConsole.Instance.Output("Failed to get room list");
            }
        }
    }

    private void WriteOut(string msg, params object[] args)
    {
        // m_log.LogInformation(msg, args);
        MainConsole.Instance.Output(msg, args);
    }

}
