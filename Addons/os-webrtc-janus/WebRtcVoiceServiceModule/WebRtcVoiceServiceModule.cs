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
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using Nini.Config;

using Microsoft.Extensions.Logging;

namespace osWebRtcVoice;

/// <summary>
/// Interface for the WebRtcVoiceService.
/// An instance of this is registered as the IWebRtcVoiceService for this region.
/// The function here is to direct the capability requests to the appropriate voice service.
/// For the moment, there are separate voice services for spatial and non-spatial voice
/// with the idea that a region could have a pre-region spatial voice service while
/// the grid could have a non-spatial voice service for group chat, etc.
/// Fancier configurations are possible.
/// </summary>
public partial class WebRtcVoiceServiceModule : ISharedRegionModule, IWebRtcVoiceService
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static string LogHeader = "[WEBRTC VOICE SERVICE MODULE]";

    private static bool m_Enabled = false;
    private IConfigSource m_Config;

    private IWebRtcVoiceService m_spatialVoiceService;
    private IWebRtcVoiceService m_nonSpatialVoiceService;

    // =====================================================================

    // ISharedRegionModule.Initialize
    // Get configuration and load the modules that will handle spatial and non-spatial voice.
    public void Initialise(IConfigSource pConfig)
    {
        m_Config = pConfig;
        IConfig moduleConfig = m_Config.Configs["WebRtcVoice"];

        if (moduleConfig is not null)
        {
            m_Enabled = moduleConfig.GetBoolean("Enabled", false);
            // NPC voice is refused by default: an NPC presence provisions voice only as a registered connector
            // identity, or when the operator opens the whole surface (see WebRtcVoiceServiceModule.NpcVoice.cs).
            m_allowNpcVoice = ReadAllowNpcVoice(moduleConfig);
            // [WebRtcVoice] RefusalCacheSeconds (default 5; 0 disables) - how long a failed provision's
            // answer is replayed to the viewer's immediate retries without creating a voice session or calling Janus.
            int refusalSeconds = moduleConfig.GetInt("RefusalCacheSeconds", ProvisionRefusalCache.DefaultSeconds);
            m_refusalLog.SetWindow(TimeSpan.FromSeconds(Math.Max(0, refusalSeconds)));
            m_refusalLog.Start();
            if (m_Enabled)
            {
                // Get the DLLs for the two voice services
                string spatialDllName = moduleConfig.GetString("SpatialVoiceService", string.Empty);
                string nonSpatialDllName = moduleConfig.GetString("NonSpatialVoiceService", string.Empty);
                if (string.IsNullOrEmpty(spatialDllName) && string.IsNullOrEmpty(nonSpatialDllName))
                {
                    m_log.LogError($"{LogHeader} No VoiceService specified in configuration");
                    m_Enabled = false;
                    return;
                }

                // Default non-spatial to spatial if not specified
                if (string.IsNullOrEmpty(nonSpatialDllName))
                {
                    m_log.LogDebug($"{LogHeader} nonSpatialDllName not specified. Defaulting to spatialDllName");
                    nonSpatialDllName = spatialDllName;
                }

                // Load the two voice services
                m_log.LogDebug($"{LogHeader} Loading SpatialVoiceService from {spatialDllName}");
                m_spatialVoiceService = ServerUtils.LoadPlugin<IWebRtcVoiceService>(spatialDllName, [m_Config]);
                if (m_spatialVoiceService is null)
                {
                    m_log.LogError($"{LogHeader} Could not load SpatialVoiceService from {spatialDllName}, module disabled");
                    m_Enabled = false;
                    return;
                }

                m_log.LogDebug($"{LogHeader} Loading NonSpatialVoiceService from {nonSpatialDllName}");
                if (spatialDllName != nonSpatialDllName)
                {
                    m_nonSpatialVoiceService = ServerUtils.LoadPlugin<IWebRtcVoiceService>(nonSpatialDllName, [ m_Config ]);
                    if (m_nonSpatialVoiceService is null)
                    {
                        m_log.LogError("{0} Could not load NonSpatialVoiceService from {1}", LogHeader, nonSpatialDllName);
                        m_Enabled = false;
                    }
                }
                else
                {
                    // Identical DLL names mean ONE service serving both roles -- share the
                    // instance. This branch used to leave m_nonSpatialVoiceService NULL, so the first
                    // non-"local" provision (the admitted A2A multiagent) threw NullReferenceException
                    // at the CreateViewerSession dispatch below -- swallowed unlogged by the caps
                    // wrapper's bare catch (SimpleStreamHandler.cs:91-101): the viewer saw an empty
                    // 500 with no viewer_session/jsep and sat in VOICE_STATE_SESSION_RETRY forever.
                    m_nonSpatialVoiceService = m_spatialVoiceService;
                }

                if (m_Enabled)
                {
                    m_log.LogInformation($"{LogHeader} WebRtcVoiceService enabled");
                    RegisterConsoleCommands();
                }
            }
        }
    }

    // ISharedRegionModule.PostInitialize
    public void PostInitialise()
    {
    }

    // ISharedRegionModule.Close
    public void Close()
    {
        m_refusalLog.Dispose();
    }

    // ISharedRegionModule.ReplaceableInterface
    public Type ReplaceableInterface
    {
        get { return null; }
    }

    // ISharedRegionModule.Name
    public string Name
    {
        get { return "WebRtcVoiceServiceModule"; }
    }

    // ISharedRegionModule.AddRegion
    public void AddRegion(Scene scene)
    {
        if (m_Enabled)
        {
            m_log.LogDebug($"{LogHeader} Adding WebRtcVoiceService to region {scene.Name}");
            scene.RegisterModuleInterface<IWebRtcVoiceService>(this);

            lock (m_scenes)
                m_scenes[scene.RegionInfo.RegionID] = scene;

            // Close-time voice teardown. OnClientClosed (NOT OnRemovePresence) because it fires
            // while the presence is still resolvable, so the handler can classify child-vs-root
            // and read the dying login's SessionId itself — no core EventManager change needed.
            scene.EventManager.OnClientClosed += Event_OnClientClosed;

            // Other candidate events, considered and not needed:
            // scene.EventManager.OnNewClient / OnClientLogin / OnNewPresence — provision-driven
            // scene.EventManager.OnClientMovement / OnSignificantClientMovement — feeder-driven
        }

    }

    // ISharedRegionModule.RemoveRegion
    public void RemoveRegion(Scene scene)
    {
        if (m_Enabled)
        {
            scene.EventManager.OnClientClosed -= Event_OnClientClosed;
            lock (m_scenes)
                m_scenes.Remove(scene.RegionInfo.RegionID);
            scene.UnregisterModuleInterface<IWebRtcVoiceService>(this);
        }
    }

    // ISharedRegionModule.RegionLoaded
    public void RegionLoaded(Scene scene)
    {
    }

    // IWebRtcVoiceService.ProvisionVoiceAccountRequest
        public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        // Retry hook (remove-and-forget guard): if an earlier close-time teardown for this agent
        // FAILED, its sessions are parked in ClosingSessions. A new provision is exactly the
        // moment that residue would become a duplicate handle in the mixer, so re-drive the
        // teardown first — observed, asynchronous, on the parked references only.
        List<(IVoiceViewerSession Session, long AgeMs)> stale = VoiceViewerSession.GetClosingSessions(pUserID);
        if (stale.Count > 0)
        {
            foreach ((IVoiceViewerSession s, long ageMs) in stale)
                m_log.LogWarning("{LogHeader} provision for {UserId}: prior voice-session teardown still pending/failed (session {ViewerSessionId}, age {AgeSeconds:F0}s) - retrying",
                    LogHeader, pUserID, s.ViewerSessionID, ageMs / 1000.0);
            List<IVoiceViewerSession> retry = stale.Select(t => t.Session).ToList();
            _ = Task.Run(() => ShutdownCapturedSessions(retry, $"provision retry {pUserID}"));
        }

        OSDMap response = null;
        IVoiceViewerSession vSession = null;
        bool createdHere = false;   // true when this call created and registered vSession
        // A teardown that cannot name a live session -- viewer_session absent, the zero
        // UUID (observed live from a viewer backing out of provision retries), or an id nothing
        // holds -- has nothing to tear down HERE. Answer the shape a successful logout answers
        // ({ response: "closed" }, ProvisionResponseBuilder.BuildClosed's map) instead of falling
        // into the create branch and erroring "no channel_type in request".
        bool isLogout = pRequest.TryGetBool("logout", out bool lg) && lg;
        if (isLogout)
            m_refusalLog.Ended(pUserID, pSceneID, "it logged out");   // a logout ends any cached refusal for this region
        if (HasRealViewerSession(pRequest, out string viewerSessionId))
        {
            // request has a real viewer session. Use that to find the voice service -- it must be
            // this agent's own session; another agent's id resolves to nothing.
            if (!TryGetViewerSessionFor(viewerSessionId, pUserID, "ProvisionVoiceAccountRequest", m_log, out vSession))
            {
                if (isLogout)
                {
                    m_log.LogDebug("{LogHeader} ProvisionVoiceAccountRequest: logout for unknown viewer session {ViewerSessionId} from {UserId} - nothing to close",
                        LogHeader, viewerSessionId, pUserID);
                    return new OSDMap { { "response", "closed" } };
                }
                m_log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: viewer session {viewerSessionId} not found");
            }
        }
        else if (isLogout)
        {
            m_log.LogDebug("{LogHeader} ProvisionVoiceAccountRequest: logout with no usable viewer session from {UserId} - nothing to close",
                LogHeader, pUserID);
            return new OSDMap { { "response", "closed" } };
        }
        else
        {
            if (RefuseNpcProvision(pUserID, pSceneID))
                return null;

            // The viewer re-provisions immediately after a failed provision. Inside RefusalCacheSeconds a
            // repeat attempt gets the same failure answer again, without creating a voice session or calling the
            // voice service (Janus); the window's single log line is written when the refusal is re-evaluated.
            if (m_refusalCache.TryGetRefusal(pUserID, pSceneID, out object cachedRefusal))
                return CopyAnswer(cachedRefusal as OSDMap);

            // no (usable) viewer session -> this is an initial request
            if (pRequest.TryGetString("channel_type", out string channelType))
            {
                if (channelType == "local")
                {
                    // TODO: check if this userId is making a new session (case that user is reconnecting)
                    vSession = m_spatialVoiceService.CreateViewerSession(pRequest, pUserID, pSceneID);
                    CaptureGenerationToken(vSession, pUserID, pSceneID);
                    VoiceViewerSession.AddViewerSession(vSession);
                    createdHere = true;
                }
                else
                {
                    // TODO: check if this userId is making a new session (case that user is reconnecting)
                    vSession = m_nonSpatialVoiceService.CreateViewerSession(pRequest, pUserID, pSceneID);
                    CaptureGenerationToken(vSession, pUserID, pSceneID);
                    VoiceViewerSession.AddViewerSession(vSession);
                    createdHere = true;
                }
            }
            else
            {
                m_log.LogError($"{LogHeader} ProvisionVoiceAccountRequest: no channel_type in request");
            }
        }
        if (vSession is not null)
        {
                response = vSession.VoiceService.ProvisionVoiceAccountRequest(vSession, pRequest, pUserID, pSceneID);

            // A voice session THIS call created and registered must not outlive a failed
            // provision. The viewer was never told its id, so it can never log it out; its Janus session and
            // plugin handle would stay alive and IsAgentInRegion would report the agent voiced — one more set
            // per viewer retry. Remove it and shut down its voice-service side (the pair
            // DisconnectViewerSession uses). A pre-existing session (the viewer holds its id) is left alone.
            if (createdHere && !IsFailedProvision(response))
            {
                m_refusalLog.Ended(pUserID, pSceneID, "it was admitted");   // a successful provision ends any cached refusal
            }
            if (createdHere && IsFailedProvision(response))
            {
                // Remember this answer for the viewer's immediate retries (RefusalCacheSeconds). The first refusal of a run
                // is logged, and the run's count when it ends.
                int suppressed = m_refusalCache.RecordRefusal(pUserID, pSceneID, CopyAnswer(response));
                m_refusalLog.Refused(pUserID, pSceneID, $" in {pSceneID}", string.Empty, suppressed);
                IVoiceViewerSession failed = vSession;
                m_log.LogDebug("{LogHeader} ProvisionVoiceAccountRequest: provision failed for new viewer session {ViewerSessionId} of {UserId} ({Error}) - removing it",
                    LogHeader, failed.ViewerSessionID, pUserID,
                    response is not null && response.TryGetString("error", out string provError) ? provError : "no response");
                VoiceViewerSession.RemoveViewerSession(failed.ViewerSessionID);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await failed.Shutdown().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        m_log.LogWarning("{LogHeader} ProvisionVoiceAccountRequest: shutdown of failed-provision viewer session {ViewerSessionId} threw: {Exception}",
                            LogHeader, failed.ViewerSessionID, e.Message);
                    }
                });
            }
        }
        return response;
    }

    // IWebRtcVoiceService.VoiceSignalingRequest
        public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        OSDMap response = null;
        IVoiceViewerSession vSession = null;
        if (pRequest.TryGetString("viewer_session", out string viewerSessionId))
        {
            // request has a viewer session. Use that to find the voice service -- it must be this
            // agent's own session; another agent's id resolves to nothing.
            if (TryGetViewerSessionFor(viewerSessionId, pUserID, "VoiceSignalingRequest", m_log, out vSession))
            {
                    response = vSession.VoiceService.VoiceSignalingRequest(vSession, pRequest, pUserID, pSceneID);
            }
            else
            {
                m_log.LogError("{0} VoiceSignalingRequest: viewer session {1} not found", LogHeader, viewerSessionId);
            }
        }   
        else
        {
            m_log.LogError("{0} VoiceSignalingRequest: no viewer_session in request", LogHeader);
        }
        return response;
    }

    // This module should never be called with this signature
        public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        throw new NotImplementedException();
    }

    // This module should never be called with this signature
        public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        throw new NotImplementedException();
    }

    // Rooms are a spatial-service concern, so this goes to the spatial service whatever channel_type a
    // request would have carried.
    public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID)
        => m_spatialVoiceService?.EnsureSpatialRoom(pSceneID, pParcelLocalID);

    // Dispatch to the configured service by channel_type, as the provision path does. Creating a viewer session
    // touches no Janus state (WebRtcJanusService.CreateViewerSession only builds the object), so this is safe for
    // an identity that will never send an offer: the connector registration path, which pairs it with
    // VoiceViewerSession.AddViewerSession.
    public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pSceneID)
    {
        bool isSpatial = pRequest.TryGetString("channel_type", out string channelType) && channelType == "local";
        IWebRtcVoiceService service = isSpatial ? m_spatialVoiceService : m_nonSpatialVoiceService;
        return service?.CreateViewerSession(pRequest, pUserID, pSceneID);
    }
}
