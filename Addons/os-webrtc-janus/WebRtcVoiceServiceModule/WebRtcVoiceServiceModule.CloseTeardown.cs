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
// Adapted 2026 by Legion Builds

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

// Close-time voice teardown, the refusal throttle and viewer session ownership checks.
public partial class WebRtcVoiceServiceModule
{
    // Replays a failed provision's answer to the viewer's immediate retries (RefusalCacheSeconds).
    private ProvisionRefusalCache m_refusalCache = new ProvisionRefusalCache(TimeSpan.FromSeconds(ProvisionRefusalCache.DefaultSeconds));

    // Scenes this shared module serves, for resolving a Scene from the pSceneID the provision
    // path carries — needed to read the provisioning client's SessionId (the generation token).
    private readonly Dictionary<UUID, Scene> m_scenes = new Dictionary<UUID, Scene>();

    // =====================================================================
    // Close-time voice teardown. Replaces the never-wired Event_OnRemovePresence, which was
    // broken three ways as written: it enumerated a deferred registry query outside the lock,
    // mutated the registry inside that enumeration, and fire-and-forgot Shutdown unobserved.
    //
    // This handler runs INSIDE Scene.RemoveClient under the (global) removal lock, after
    // TriggerClientClosed fires and BEFORE the presence is removed — so the presence is
    // resolvable here and only here. Everything up to the capture must complete synchronously
    // and cheaply; the Janus cleanup must never run on this thread.
    private void Event_OnClientClosed(UUID pClientID, Scene pScene)
    {
        ScenePresence sp = pScene.GetScenePresence(pClientID);
        if (sp == null || sp.IsChildAgent)
            return;   // child teardown (border crossing / draw distance) — the agent's voice lives in its root region

        // The dying login's SessionId — the generation token captured at provision. Selection is
        // token-strict so a successor login's freshly-provisioned session can never be captured,
        // even if that provision races this close.
        UUID generation = sp.ControllingClient is not null ? sp.ControllingClient.SessionId : UUID.Zero;

        // Retry hook FIRST, capture SECOND — deliberate and load-bearing. CaptureSessionsForClose
        // parks its captures in ClosingSessions in the same locked statement, so reading the
        // closing-set AFTER capturing observes this handler's own just-parked work and logs a
        // false "prior teardown pending/failed (age 0s)" WARN on every clean logout (observed
        // live 2026-08-22). Snapshot-before-capture is stateless and race-free: genuinely stale
        // entries (parked by an earlier failed close) still surface with honest ages, the
        // just-captured set cannot appear, and no per-agent suppression window exists to
        // wrongly swallow a genuine second close of a different session generation.
        List<IVoiceViewerSession> toShutdown = new List<IVoiceViewerSession>();
        foreach ((IVoiceViewerSession s, long ageMs) in VoiceViewerSession.GetClosingSessions(pClientID))
        {
            m_log.LogWarning("{LogHeader} Event_OnClientClosed: prior teardown for {ClientId} still pending/failed (session {ViewerSessionId}, age {AgeSeconds:F0}s) - retrying",
                LogHeader, pClientID, s.ViewerSessionID, ageMs / 1000.0);
            toShutdown.Add(s);
        }

        // A ROOT close captures the agent's voice sessions in EVERY region of this instance, not only
        // pScene's. The neighbour-region sessions (the viewer opens one spatial connection per adjacent
        // region) belong to the same dying viewer, and the viewer does NOT reliably log them out on Quit
        // (observed: a neighbour-region session survived an ordinary quit as a ghost holding a
        // mixer slot, because the neighbour's close is a CHILD close and the guard above ignores it).
        // Teleport cannot lose the new root's session to this: EntityTransferModule flags the source child
        // (TransferAgent_V2 :1194, crossing :1855) before the destination becomes root and closes the
        // source only after MakeChildAgent (:1232 -> :1260), so that close is a child close. As a guard
        // that does not depend on that ordering, regions where this module sees the agent ROOT right now
        // are skipped.
        HashSet<UUID> rootElsewhere = RegionsWhereAgentIsRoot(pClientID, pScene.RegionInfo.RegionID);
        List<IVoiceViewerSession> captured = CaptureForClientClose(pClientID, sp.IsChildAgent, generation, rootElsewhere);
        foreach (IVoiceViewerSession s in captured)
        {
            if (!toShutdown.Contains(s))
                toShutdown.Add(s);
        }

        if (toShutdown.Count == 0)
            return;

        List<UUID> regions = new List<UUID>();
        foreach (IVoiceViewerSession s in captured)
        {
            if (!regions.Contains(s.RegionId))
                regions.Add(s.RegionId);
        }
        foreach (UUID region in regions)
        {
            int n = captured.Count(s => s.RegionId == region);
            m_log.LogDebug("{LogHeader} Event_OnClientClosed: captured {CapturedSessionCount} voice session(s) for {ClientId} in {SceneName}",
                LogHeader, n, pClientID, RegionName(region));
        }
        foreach (UUID region in rootElsewhere)
            m_log.LogDebug("{LogHeader} Event_OnClientClosed: {ClientId} is root in {SceneName}; its voice session(s) there are kept",
                LogHeader, pClientID, RegionName(region));
        m_log.LogDebug("{LogHeader} Event_OnClientClosed: root close of {ClientId} in {SceneName} captured {CapturedSessionCount} voice session(s) across {RegionCount} region(s)",
            LogHeader, pClientID, pScene.Name, toShutdown.Count, regions.Count);

        // Asynchronous, on the captured references only — never a re-query by avatar, so a
        // session provisioned after the capture is untouchable by this teardown.
        _ = Task.Run(() => ShutdownCapturedSessions(toShutdown, $"client close {pClientID}"));
    }

    /// The close-time capture decision, Scene-free so it unit-tests. A child close (border
    /// crossing, draw distance, the source of a completed teleport) captures nothing. A root close
    /// captures the dying login's sessions in every region except pRootRegionsElsewhere.
    public static List<IVoiceViewerSession> CaptureForClientClose(UUID pAgentId, bool pClosingPresenceIsChild,
        UUID pGeneration, ICollection<UUID> pRootRegionsElsewhere)
    {
        if (pClosingPresenceIsChild)
            return new List<IVoiceViewerSession>();
        return VoiceViewerSession.CaptureSessionsForCloseAllRegions(pAgentId, pGeneration, pRootRegionsElsewhere);
    }

    // Regions (other than the closing one) where this module's scenes hold the agent as a ROOT presence.
    private HashSet<UUID> RegionsWhereAgentIsRoot(UUID pAgentId, UUID pClosingRegionId)
    {
        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes.Values);
        HashSet<UUID> result = new HashSet<UUID>();
        foreach (Scene scene in scenes)
        {
            if (scene.RegionInfo.RegionID == pClosingRegionId)
                continue;
            ScenePresence other = scene.GetScenePresence(pAgentId);
            if (other is not null && !other.IsChildAgent)
                result.Add(scene.RegionInfo.RegionID);
        }
        return result;
    }

    private string RegionName(UUID pRegionId)
    {
        lock (m_scenes)
            return m_scenes.TryGetValue(pRegionId, out Scene scene) ? scene.Name : pRegionId.ToString();
    }

    // Observed asynchronous teardown of captured sessions. Per-session failure isolation: one
    // failure is logged and that voice session stays parked in ClosingSessions — discoverable and
    // retried by the provision/close hooks — while the rest proceed. Never remove-and-forget.
    private async Task ShutdownCapturedSessions(List<IVoiceViewerSession> pSessions, string pReason)
    {
        foreach (IVoiceViewerSession s in pSessions)
        {
            try
            {
                await s.Shutdown();
                VoiceViewerSession.CloseCompleted(s);
                m_log.LogDebug("{LogHeader} voice-session teardown complete ({TeardownReason}): session {ViewerSessionId} agent {AgentId}",
                    LogHeader, pReason, s.ViewerSessionID, s.AgentId);
            }
            catch (Exception e)
            {
                // Record the cause on the parked entry (console: "show voice closing") and log the
                // FULL exception - type, message, stack, inners - via the (Exception, message)
                // overload. A parked session with no recorded cause was the gap this closes.
                VoiceViewerSession.RecordCloseFailure(s, $"{e.GetType().Name}: {e.Message}");
                m_log.LogWarning(e, $"{LogHeader} voice-session teardown FAILED ({pReason}): session {s.ViewerSessionID} agent {s.AgentId} region {s.RegionId} - retained for retry");
            }
        }
    }

    // Read-only console observability for the closing-set: "show voice closing" lists every
    // parked session with agent, session id, age, and the last recorded failure. Closes the gap
    // that ClosingSessions was otherwise inspectable only by log-accounting inference.
    private void RegisterConsoleCommands()
    {
        if (MainConsole.Instance == null)
            return;   // unit tests / embedded hosts have no console
        MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice closing",
            "show voice closing",
            "Show voice sessions parked in the closing-set (teardown in flight or failed), with age and last failure reason",
            HandleShowVoiceClosing);
    }

    private void HandleShowVoiceClosing(string module, string[] args)
    {
        List<(UUID AgentId, string SessionId, long AgeMs, string LastFailure)> snap =
            VoiceViewerSession.GetClosingSnapshot();
        if (snap.Count == 0)
        {
            MainConsole.Instance.Output("No voice sessions in closing state.");
            return;
        }
        MainConsole.Instance.Output("{0} voice session(s) in closing state:", snap.Count);
        foreach ((UUID agentId, string sessionId, long ageMs, string lastFailure) in snap)
            MainConsole.Instance.Output("  agent {0} session {1} age {2:F0}s failure: {3}",
                agentId, sessionId, ageMs / 1000.0, lastFailure ?? "(none - first attempt in flight)");
    }

    // Capture the generation token onto a freshly-created session: the provisioning client's
    // login SessionId, read from the live presence. Runs before AddViewerSession so the voice session
    // enters the registry fully formed. UUID.Zero (capture failed) makes the voice session sweepable
    // by ANY close for its agent — logged loudly because it should not happen in practice.
    private void CaptureGenerationToken(IVoiceViewerSession pSession, UUID pUserID, UUID pSceneID)
    {
        Scene scene;
        lock (m_scenes)
            m_scenes.TryGetValue(pSceneID, out scene);
        ScenePresence sp = scene?.GetScenePresence(pUserID);
        pSession.ClientSessionId = sp?.ControllingClient?.SessionId ?? UUID.Zero;
        if (pSession.ClientSessionId == UUID.Zero)
            m_log.LogWarning("{LogHeader} provision for {UserId}: could not capture client SessionId (scene {SceneState}, presence {PresenceState}) - session sweepable by any close for this agent",
                LogHeader, pUserID, scene is null ? "unresolved" : "resolved", sp is null ? "absent" : "present");
    }

    // =====================================================================
    // IWebRtcVoiceService

    // Binds a viewer_session id to the requesting agent. VoiceViewerSession.TryGetViewerSession
    // looks up by id string only, so any agent presenting another agent's viewer_session id could
    // drive that voice session (re-provision it, feed it ICE, log it out). Every registered session
    // carries the agent it was created for (WebRtcJanusService.CreateViewerSession sets AgentId
    // from the cap-bound requester), so both cap sites resolve through here: found AND owned by
    // the requester, else exactly the caller's existing not-found path -- no new error shape, and
    // the voice session itself is untouched. A mismatch is a spoof attempt or a viewer bug, so it is
    // logged at WARN naming both agents; a plain miss is not (the call sites keep their ERROR).
    // UUID.Zero is never a wildcard. Static and logger-injected so it is unit-testable.
    public static bool TryGetViewerSessionFor(string pViewerSessionId, UUID pRequester, string pSite, ILogger pLog, out IVoiceViewerSession pSession)
    {
        pSession = null;
        if (!VoiceViewerSession.TryGetViewerSession(pViewerSessionId, out IVoiceViewerSession found) || found is null)
            return false;
        if (pRequester == UUID.Zero || found.AgentId != pRequester)
        {
            pLog?.LogWarning("{LogHeader} {Site}: viewer session {ViewerSessionId} is bound to agent {BoundAgentId} but was presented by agent {RequesterId} - treated as not found",
                LogHeader, pSite, pViewerSessionId, found.AgentId, pRequester);
            return false;
        }
        pSession = found;
        return true;
    }

    // A viewer_session that is absent, empty, or the zero UUID indicates an INITIAL
    // provision request (no session created yet), NOT a lookup of an existing session.
    // NOTE: OSDMap.TryGetString returns true for a *present* OSDUUID(UUID.Zero), yielding
    // "00000000-0000-0000-0000-000000000000" -- so a present-but-zero value must be
    // treated as "no session yet", otherwise the first provision is routed to the lookup
    // branch and fails with "viewer session 00000000-... not found". Registered session
    // ids are UUID strings (see VoiceViewerSession ctor), so parse and reject UUID.Zero.
    public static bool HasRealViewerSession(OSDMap pRequest, out string viewerSessionId)
    {
        viewerSessionId = null;
        if (!pRequest.TryGetString("viewer_session", out string vs))
            return false;                                      // absent
        if (string.IsNullOrEmpty(vs))
            return false;                                      // empty
        if (UUID.TryParse(vs, out UUID vsid) && vsid == UUID.Zero)
            return false;                                      // zero UUID
        viewerSessionId = vs;
        return true;
    }

    // The failure signature of a provision response — no map, or a map without viewer_session
    // (only ProvisionResponseBuilder.BuildSuccess carries one).
    public static bool IsFailedProvision(OSDMap pResponse)
    {
        return pResponse is null || !pResponse.ContainsKey("viewer_session");
    }

    // The cached failure answer is copied in and out, so no caller can alter what later retries receive.
    private static OSDMap CopyAnswer(OSDMap pAnswer)
    {
        if (pAnswer is null)
            return null;
        OSDMap copy = new OSDMap();
        foreach (KeyValuePair<string, OSD> kvp in pAnswer)
            copy[kvp.Key] = kvp.Value;
        return copy;
    }
}
