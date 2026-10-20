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

using System.Collections.Generic;
using System.Net;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Caps = OpenSim.Framework.Capabilities.Caps;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using Nini.Config;
using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

/// <summary>
/// This module provides the WebRTC voice interface for viewer clients..
/// 
/// In particular, it provides the following capabilities:
///      ProvisionVoiceAccountRequest, VoiceSignalingRequest and limited ChatSessionRequest
/// which are the user interface to the voice service.
/// 
/// Initially, when the user connects to the region, the region feature "VoiceServiceType" is
/// set to "webrtc" and the capabilities that support voice are enabled.
/// The capabilities then pass the user request information to the IWebRtcVoiceService interface
/// that has been registered for the reqion.
/// </summary>
public partial class WebRtcVoiceRegionModule : ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string logHeader = "[REGION WEBRTC VOICE]";

    private static byte[] llsdUndefAnswerBytes = Util.UTF8.GetBytes("<llsd><undef /></llsd>"); 
    private bool _MessageDetails = false;

    // Control info
    private static bool m_Enabled = false;

    private IConfig m_Config;

    // ISharedRegionModule.Initialize
    public void Initialise(IConfigSource config)
    {
        m_Config = config.Configs["WebRtcVoice"];
        if (m_Config is not null)
        {
            m_Enabled = m_Config.GetBoolean("Enabled", false);
            if (m_Enabled)
            {
                _MessageDetails = m_Config.GetBoolean("MessageDetails", false);
                InitialiseRefusalCache();
                InitialiseStunServers();
                MixerOnlySwitches.DisableOnStockPlugin(config, m_log);
                m_stockPlugin = MixerOnlySwitches.IsStockPlugin(MixerOnlySwitches.PluginNameOf(config));
                InitialiseVisibility(config);
                InitialiseNonSpatial(config);
                InitialiseCallModeration(config);
                InitialiseModeration();

                m_log.LogInformation($"{logHeader}: enabled");
            }
        }
    }

    // ISharedRegionModule.PostInitialize
    public void PostInitialise()
    {
    }

    // ISharedRegionModule.AddRegion
    public void AddRegion(Scene scene)
    {
        // todo register module to get parcels changes etc
        RegisterSceneForVoiceSessions(scene);
    }

    // ISharedRegionModule.RemoveRegion
    public void RemoveRegion(Scene scene)
    {
        UnregisterSceneForVoiceSessions(scene);
        StopVisibilityService(scene);
    }

    // ISharedRegionModule.RegionLoaded
    public void RegionLoaded(Scene scene)
    {
        if (m_Enabled)
        {
            scene.EventManager.OnRegisterCaps += delegate (UUID agentID, Caps caps)
            {
                OnRegisterCaps(scene, agentID, caps);
            };
            SubscribeVoiceSessionEvents(scene);

            ISimulatorFeaturesModule simFeatures = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            simFeatures?.AddFeature("VoiceServerType", OSD.FromString("webrtc"));

            AdvertiseStunServers(simFeatures);

            StartVisibilityService(scene);
        }
    }

    // ISharedRegionModule.Close
    public void Close()
    {
        StopNonSpatialMaintenance();
        m_refusalLog.Dispose();
        m_removalLog.Dispose();
        StopAllVisibilityServices();
    }

    // ISharedRegionModule.Name
    public string Name
    {
        get { return "RegionVoiceModule"; }
    }

    // ISharedRegionModule.ReplaceableInterface
    public Type ReplaceableInterface
    {
        get { return null; }
    }

    // <summary>
    // OnRegisterCaps is invoked via the scene.EventManager
    // everytime OpenSim hands out capabilities to a client
    // (login, region crossing). We contribute three capabilities to
    // the set of capabilities handed back to the client:
    // ProvisionVoiceAccountRequest, VoiceSignalingRequest and limited ChatSessionRequest
    //
    // ProvisionVoiceAccountRequest allows the client to obtain
    // voice communication information the the avater.
    //
    // VoiceSignalingRequest: Used for trickling ICE candidates.
    //
    // ChatSessionRequest
    //
    // Note that OnRegisterCaps is called here via a closure
    // delegate containing the scene of the respective region (see
    // Initialise()).
    // </summary>
    public void OnRegisterCaps(Scene scene, UUID agentID, Caps caps)
    {
        m_log.LogDebug(
            $"{logHeader}: OnRegisterCaps called with agentID {agentID} caps {caps} in scene {scene.Name}");

        caps.RegisterSimpleHandler("ProvisionVoiceAccountRequest",
                new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                {
                    ProvisionVoiceAccountRequest(httpRequest, httpResponse, agentID, scene);
                }));

        caps.RegisterSimpleHandler("VoiceSignalingRequest",
                new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                {
                    VoiceSignalingRequest(httpRequest, httpResponse, agentID, scene);
                }));

        caps.RegisterSimpleHandler("ChatSessionRequest",
                new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                {
                    ChatSessionRequest(httpRequest, httpResponse, agentID, scene);
                }));

        RegisterModerationCapability(scene, agentID, caps);
    }

    /// <summary>
    /// Callback for a client request for Voice Account Details
    /// </summary>
    /// <param name="scene">current scene object of the client</param>
    /// <param name="request"></param>
    /// <param name="path"></param>
    /// <param name="param"></param>
    /// <param name="agentID"></param>
    /// <param name="caps"></param>
    /// <returns></returns>
    private void ProvisionVoiceAccountRequestCore(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene,
        ProvisionCallContext ctx)
    {
        // Get the voice service. If it doesn't exist, return an error.
        IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
        if (voiceService is null)
        {
            m_log.LogError($"{logHeader}[ProvisionVoice]: voice service not loaded");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if(request.HttpMethod != "POST")
        {
            m_log.LogDebug($"[{logHeader}][ProvisionVoice]: Not a POST request. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // Deserialize the request. Convert the LLSDXml to OSD for our use
        OSDMap map = BodyToMap(request, "ProvisionVoiceAccountRequest");
        if (map is null)
        {
            m_log.LogError($"{logHeader}[ProvisionVoice]: No request data found. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        // Make sure the request is for WebRtc voice
        if (map.TryGetValue("voice_server_type", out OSD vstosd))
        {
            if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
            {
                // Firestorm's Vivox module probes this cap twice per login; refused as before, logged at DEBUG.
                m_log.LogDebug($"{logHeader}[ProvisionVoice]: voice_server_type is not 'webrtc' (viewer Vivox probe, expected). Request: {map}");
                response.RawBuffer = llsdUndefAnswerBytes;
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }
        }

        if (_MessageDetails) m_log.LogDebug($"{logHeader}[ProvisionVoice]: request: {map}");

        if (!TryAdmitProvision(map, agentID, scene, response, out ProvisionAdmission admission, out string channelType, out string a2aVs))
            return;

        // Record what admission decided for the wrapper, and answer a repeat of a refusal this (agent, region)
        // got within RefusalCacheSeconds straight from the cache, before the estate and parcel checks run.
        ctx.Kind = admission.Kind;
        if (TryReplayCachedRefusal(admission, agentID, scene, response, ctx))
            return;

        // The estate and parcel checks below apply to a "local" request. A call, a conference, a group call and a
        // teardown are admitted by TryAdmitProvision instead.
        if (admission.Kind == ProvisionKind.Local)
        {
            //do fully not trust viewers voice parcel requests
            if (channelType == "local")
            {
                if (!scene.RegionInfo.EstateSettings.AllowVoice)
                {
                    m_log.LogDebug($"{logHeader}[ProvisionVoice]:region \"{scene.Name}\": voice not enabled in estate settings");
                    response.RawBuffer = llsdUndefAnswerBytes;
                    response.StatusCode = (int)HttpStatusCode.NotImplemented;
                    return;
                }
                if (scene.LandChannel == null)
                {
                    m_log.LogError($"{logHeader}[ProvisionVoice] region \"{scene.Name}\" land data not yet available");
                    response.RawBuffer = llsdUndefAnswerBytes;
                    response.StatusCode = (int)HttpStatusCode.NotImplemented;
                    return;
                }

                if(!scene.TryGetScenePresence(agentID, out ScenePresence sp))
                {
                    m_log.LogDebug($"{logHeader}[ProvisionVoice]:avatar not found");
                    response.RawBuffer = llsdUndefAnswerBytes;
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                // For a ROOT agent the parcel is derived from the avatar's position, as
                // Vivox/FreeSwitch do (scene.GetLandData(avatar.AbsolutePosition)). It used to be looked up
                // by the viewer's parcel_local_id: the parcel was taken from what the viewer sent, rather than
                // from where the avatar is. The client id is now a hint: logged on mismatch, never refused (a
                // viewer mid-crossing can be stale).
                // A CHILD agent (the viewer's neighbour-region provision) is positioned outside this
                // region, so position finds no parcel. It keeps the earlier path: a client id selects the
                // parcel and every check runs against it; no id (the stock viewer, estate channel) means no
                // parcel checks and the service's -999 estate room. ProvisionParcelResolver decides; a hinted
                // parcel that is not near the child's known position is dropped below.
                int? clientParcelId = map.TryGetInt("parcel_local_id", out int c) ? c : null;
                ParcelResolveInput resolveInput = new ParcelResolveInput(clientParcelId, sp.IsChildAgent);

                ILandObject parcel = null;
                if (ProvisionParcelResolver.DerivesFromPosition(resolveInput))
                    parcel = scene.LandChannel.GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
                ParcelResolution res = ProvisionParcelResolver.Resolve(resolveInput, parcel?.LandData?.LocalID);
                if (res.Source == ParcelSource.ClientHint)
                {
                    // The id comes from the viewer, and this region only knows the child agent's position
                    // coarsely (the root region sends it after about 10 m of movement, at most every 2 s).
                    // Accept the named parcel only if it lies within that slack of the child's position;
                    // otherwise treat the request as sent without an id (estate room). Never a refusal.
                    parcel = scene.LandChannel.GetLandObject(res.LocalId.Value);
                    LandData hinted = parcel?.LandData;
                    if (hinted is null || !ChildParcelProximity.IsNear(sp.AbsolutePosition, hinted.AABBMin, hinted.AABBMax))
                    {
                        res = ProvisionParcelResolver.RejectHint(res);
                        parcel = null;
                        map.Remove("parcel_local_id"); // the service must not room by the dropped id
                    }
                }

                LandData land = parcel?.LandData;
                bool estateChan = land != null && (land.Flags & (uint)ParcelFlags.UseEstateVoiceChan) != 0;

                // Every path logs this line, including the refusals below and the child/no-hint estate path,
                // so no provision outcome is silent, the NotFound arm included.
                m_log.LogDebug("{LogHeader} [PARCEL RESOLVE] agent={AgentId} region={RegionName} child={Child} source={Source} client={ClientId} server={ServerLocalId} parcel={ParcelId} estate_chan={EstateChan} mismatch={Mismatch} hint_rejected={HintRejected}",
                    logHeader, agentID, scene.Name, sp.IsChildAgent, res.Source, clientParcelId?.ToString() ?? "-",
                    res.ServerLocalId?.ToString() ?? "-", land?.LocalID.ToString() ?? "-",
                    land == null ? "-" : estateChan.ToString(), res.ClientMismatch, res.HintRejected);

                // ParcelSource.None: child agent, no client id -- no parcel checks, parcel_local_id stays
                // absent, the service defaults to the estate room. The normal neighbour-region path.
                if (res.Source != ParcelSource.None)
                {
                    if (land == null)
                    {
                        if (res.Source == ParcelSource.ServerPosition)
                            m_log.LogWarning("{LogHeader}[ProvisionVoice]: no parcel at position for root agent {AgentId} in \"{RegionName}\" — refusing",
                                logHeader, agentID, scene.Name);
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }

                    if (res.ClientMismatch)
                        m_log.LogWarning("{LogHeader}[ProvisionVoice]: parcel_local_id {ClientId} from agent {AgentId} does not match the avatar's parcel {ServerLocalId} in \"{RegionName}\" — using the server parcel",
                            logHeader, clientParcelId, agentID, res.ServerLocalId, scene.Name);

                    // Voice applies the parcel's voice-off setting and ban list itself, on every estate,
                    // including TaxFree estates (ProvisionParcelAccess). Done before any parcel_local_id
                    // rewrite below.
                    EstateSettings estateSettings = scene.RegionInfo.EstateSettings;
                    ProvisionAccess access = ProvisionParcelAccess.Check(
                        land,
                        parcel.IsRestrictedFromLand(agentID) || parcel.IsBannedFromLand(agentID),
                        a => scene.Permissions.IsAdministrator(a),
                        a => estateSettings.IsEstateManagerOrOwner(a),
                        agentID,
                        Util.UnixTimeSinceEpoch());
                    if (access == ProvisionAccess.VoiceOff)
                    {
                        m_log.LogDebug($"{logHeader}[ProvisionVoice]:parcel voice not allowed");
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }
                    if (access == ProvisionAccess.Barred)
                    {
                        m_log.LogDebug($"{logHeader}[ProvisionVoice]:agent not allowed on parcel");
                        response.RawBuffer = llsdUndefAnswerBytes;
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }

                    if (estateChan)
                    {
                        map.Remove("parcel_local_id"); // estate channel
                    }
                    else if (res.Source == ParcelSource.ServerPosition)
                    {
                        // The service hashes this into the mixer room (CalcRoomNumber). An honest viewer sends
                        // the same number, so no live room renumbers.
                        map["parcel_local_id"] = OSD.FromInteger(land.LocalID);
                    }
                    // else ParcelSource.ClientHint: the client's parcel_local_id is forwarded unchanged (previous behaviour).

                }
            }
        }

        // The checks passed. Send the request to the voice service.
        OSDMap resp = voiceService.ProvisionVoiceAccountRequest(map, agentID, scene.RegionInfo.RegionID);

        if(resp is not null)
        {
            if (_MessageDetails) m_log.LogDebug($"{logHeader}[ProvisionVoice]: response: {resp}");
            ctx.Provisioned = resp.ContainsKey("viewer_session");   // a success map clears the refusal cache

            // Convert the OSD to LLSDXml for the response
            string xmlResp = OSDParser.SerializeLLSDXmlString(resp);
            response.RawBuffer = Util.UTF8.GetBytes(xmlResp);
            // A capacity rejection carries the mixer's ROOM_FULL code; return HTTP 409 Conflict,
            // which the viewer maps to ERROR_CHANNEL_FULL (llvoicewebrtc.cpp:2901). Every other
            // failure map carries no error_code and keeps its OK status. Referencing the service
            // constant keeps the 495 in one place (WebRtcJanusService.JANUS_ROOM_FULL_ERROR_CODE).
            if (resp.TryGetInt("error_code", out int provErrorCode) && provErrorCode == WebRtcJanusService.JANUS_ROOM_FULL_ERROR_CODE)
                response.StatusCode = (int)HttpStatusCode.Conflict;
            else
                response.StatusCode = (int)HttpStatusCode.OK;

            OnProvisionAnswered(resp, admission, a2aVs, agentID, scene);
        }
        else
        {
            m_log.LogDebug($"{logHeader}[ProvisionVoice]: got null response");
            response.StatusCode = (int)HttpStatusCode.OK;
        }
        return;
    }

    public void VoiceSignalingRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
        if (voiceService is null)
        {
            m_log.LogError($"{logHeader}[VoiceSignalingRequest]: avatar \"{agentID}\": no voice service");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if(request.HttpMethod != "POST")
        {
            m_log.LogError($"[{logHeader}][VoiceSignaling]: Not a POST request. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // Deserialize the request. Convert the LLSDXml to OSD for our use
        OSDMap map = BodyToMap(request, "VoiceSignalingRequest");
        if (map is null)
        {
            m_log.LogError($"{logHeader}[VoiceSignalingRequest]: No request data found. Agent={agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        // Make sure the request is for WebRTC voice
        if (map.TryGetValue("voice_server_type", out OSD vstosd))
        {
            if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
            {
                response.RawBuffer = llsdUndefAnswerBytes;
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }
        }

        OSDMap resp = voiceService.VoiceSignalingRequest(map, agentID, scene.RegionInfo.RegionID);

        if (_MessageDetails) m_log.LogDebug($"{logHeader}[VoiceSignalingRequest]: Response: {resp}");

        // TODO: check for errors and package the response

        response.RawBuffer = llsdUndefAnswerBytes;
        response.StatusCode = (int)HttpStatusCode.OK;
        return;
    }

    /// <summary>
    /// Callback for a client request for ChatSessionRequest.
    /// The viewer sends this request when the user tries to start a P2P text or voice session
    /// with another user. We need to generate a new session ID and return it to the client.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="response"></param>
    /// <param name="agentID"></param>
    /// <param name="scene"></param>
    public void ChatSessionRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
    {
        m_log.LogDebug("{0}: ChatSessionRequest received for agent {1} in scene {2}", logHeader, agentID, scene.RegionInfo.RegionName);
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if (!scene.TryGetScenePresence(agentID, out ScenePresence sp) || sp.IsDeleted)
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: scene presence not found or deleted for agent {agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if (RefuseChildChatSession(sp, agentID, scene, response))
            return;

        OSDMap reqmap = BodyToMap(request, "[ChatSessionRequest]");
        if (reqmap is null)
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: message body not parsable in request for agent {agentID}");
            response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        m_log.LogDebug($"{logHeader} ChatSessionRequest");

        if (TryHandleNonSpatialChatSession(reqmap, agentID, sp, scene, response))
            return;

        if (!reqmap.TryGetString("method", out string method))
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: missing required 'method' field in request for agent {agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if (!reqmap.TryGetUUID("session-id", out UUID sessionID))
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: missing required 'session-id' field in request for agent {agentID}");
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        switch (method.ToLower())
        {
            // Several different method requests that we don't know how to handle.
            // Just return OK for now.
            case "decline p2p voice":
            case "decline invitation":
            case "start conference":
            case "fetch history":
                response.StatusCode = (int)HttpStatusCode.OK;
                break;
            // Asking to start a P2P voice session. We need to generate a new session ID and return
            //     it to the client in a ChatterBoxSessionStartReply event.
            case "start p2p voice":
                UUID newSessionID;
                if (reqmap.TryGetUUID("params", out UUID otherID))
                    newSessionID = new(otherID.ulonga ^ agentID.ulonga, otherID.ulongb ^ agentID.ulongb);
                else
                    newSessionID = UUID.Random();

                IEventQueue queue = scene.RequestModuleInterface<IEventQueue>();
                if (queue is null)
                {
                    m_log.LogError("{0}: no event queue for scene {1}", logHeader, scene.RegionInfo.RegionName);
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
                else
                {
                    queue.ChatterBoxSessionStartReply(
                            newSessionID,
                            sp.Name,
                            2,
                            false,
                            true,
                            sessionID,
                            true,
                            string.Empty,
                            agentID);

                    response.StatusCode = (int)HttpStatusCode.OK;
                }
                break;
            default:
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                break;
        }
    }

    /// <summary>
    /// Convert the LLSDXml body of the request to an OSDMap for easier handling.
    /// Also logs the request if message details is enabled.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="pCaller"></param>
    /// <returns>'null' if the request body is empty or cannot be deserialized</returns>
    private OSDMap BodyToMap(IOSHttpRequest request, string pCaller)
    {
        try
        {
            using Stream inputStream = request.InputStream;
            if (inputStream.Length > 0)
            {
                OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                if (_MessageDetails)
                    m_log.LogDebug($"{pCaller} BodyToMap: Request: {tmp}");
                if(tmp is OSDMap map)
                    return map;
            }
        }
        catch
        {
            m_log.LogDebug($"{pCaller} BodyToMap: Fail to decode LLSDXml request");
        }
        return null;
    }
}
