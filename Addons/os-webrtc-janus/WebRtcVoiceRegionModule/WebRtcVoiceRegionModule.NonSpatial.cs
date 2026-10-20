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

using System.Collections.Generic;
using System.Net;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using Nini.Config;
using Microsoft.Extensions.Logging;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice;

// Non-spatial voice sessions: the shared engine, the avatar-to-avatar invitation registry and the chat session
// plumbing the group voice feature builds on.
public partial class WebRtcVoiceRegionModule
{
    // [WebRtcVoice] A2AVoiceEnabled: avatar-to-avatar calls (the "call" and "start p2p voice" chat session
    // requests answered with an invitation registry and per-session credentials). Off by default: the
    // chat session handler then answers as it did before the feature existed.
    public const bool DefaultA2AVoiceEnabled = false;

    public static bool ReadA2AVoiceEnabled(IConfig pConfig)
        => pConfig.GetBoolean("A2AVoiceEnabled", DefaultA2AVoiceEnabled);

    // Avatar-to-avatar invitation registry. One per module
    // instance (one per region-server process), shared by every scene this shared module serves; thread-safe
    // because ChatSessionRequest arrives on cap HTTP threads. A callee in another process is rung through the
    // signed carrier (WebRtcVoiceRegionModule.A2ACarrier.cs), which gives that process its own copy of the record.
    private readonly A2ASessionRegistry m_a2aSessions = new();

    // The non-spatial engine and the group policy. The engine is constructed unconditionally and costs nothing
    // when idle; the POLICY is what gates every group arm, and it is GroupVoicePolicy.Disabled unless
    // [WebRtcVoice] GroupVoiceEnabled is set. The store is the in-process one; a service-backed store can
    // replace it behind INonSpatialSessionStore without touching anything here.
    private readonly InMemoryNonSpatialSessionStore m_nonSpatialStore = new();

    private NonSpatialVoiceSessionEngine m_nonSpatial;

    // [WebRtcVoice] CarrierSecret signs and verifies the cross-process ring carrier. Unset, or shorter than
    // CarrierAuth.MinSecretLength, it is off: no carrier is sent and every incoming one is refused. A grid that
    // runs its regions in more than one process sets the same value on every region server.
    private CarrierAuth m_carrierAuth = CarrierAuth.Off();

    // Reads [WebRtcVoice] CarrierSecret and says once, at startup, whether cross-process carriers are on.
    // The secret itself is never logged.
    private void InitialiseCarrierAuth()
    {
        string secret = m_Config.GetString("CarrierSecret", string.Empty);
        string problem = CarrierAuth.CheckSecret(secret);
        if (problem is null)
        {
            m_carrierAuth = new CarrierAuth(secret);
            m_log.LogInformation("{LogHeader} cross-process voice carriers on: group rings and "
                + "avatar-to-avatar calls are signed with [WebRtcVoice] CarrierSecret", logHeader);
            return;
        }
        m_carrierAuth = CarrierAuth.Off();
        if (problem == "too-short")
            m_log.LogError("{LogHeader} [WebRtcVoice] CarrierSecret is shorter than {Min} characters and is refused; "
                + "cross-process voice carriers are off", logHeader, CarrierAuth.MinSecretLength);
        else
            m_log.LogInformation("{LogHeader} cross-process voice carriers are off: [WebRtcVoice] CarrierSecret is not set. "
                + "None is sent and every incoming one is refused. A grid that runs regions in more than one process "
                + "sets the same CarrierSecret (at least {Min} characters) on every region server for group and "
                + "avatar-to-avatar calls to ring across them", logHeader, CarrierAuth.MinSecretLength);
    }

    private void LogCarrierOff(UUID session)
        => m_log.LogDebug("{LogHeader} carrier not sent: session={Session} decision={Decision}", logHeader, session,
                          CarrierIntake.DecisionCarrierOff);

    // An accepted carrier (one that passed authentication) is logged at Debug with the session id, its type and
    // the region it came from, so a call can be followed across processes. Never contents.
    private void LogCarrierAccepted(string kind, UUID session, NonSpatialSessionType type, UUID origin)
        => m_log.LogDebug("{LogHeader} {Line}", logHeader, CarrierAuth.AcceptedLine(kind, session, type, origin));

    // A refused carrier is logged by reason and running count only, never by contents, and not on every
    // refusal: the first of each reason and then every hundredth, so a flood cannot flood the log.
    private void LogCarrierRefusal(string kind, string reason, long count)
    {
        if (CarrierAuth.ShouldLog(count))
            m_log.LogWarning("{LogHeader} carrier refused: kind={Kind} reason={Reason} count={Count}",
                logHeader, kind, reason, count);
    }

    // [WebRtcVoice] MaxNonSpatialSessions / MaxSessionsPerAgent: how many non-spatial sessions this process
    // holds, and how many live ones one agent may have started.
    // A value below 1 is not usable, so it reads as the default.
    public static NonSpatialLimits ReadNonSpatialLimits(IConfig pConfig)
        => new NonSpatialLimits
        {
            MaxNonSpatialSessions = PositiveOrDefault(pConfig.GetInt("MaxNonSpatialSessions", NonSpatialLimits.DefaultMaxNonSpatialSessions),
                                                      NonSpatialLimits.DefaultMaxNonSpatialSessions),
            MaxSessionsPerAgent = PositiveOrDefault(pConfig.GetInt("MaxSessionsPerAgent", NonSpatialLimits.DefaultMaxSessionsPerAgent),
                                                    NonSpatialLimits.DefaultMaxSessionsPerAgent),
        };

    private static int PositiveOrDefault(int value, int fallback) => value >= 1 ? value : fallback;

    // Drives the engine's Sweep() so invitations lapse and idle sessions are collected. Created with the
    // engine, running while this module serves at least one region, disposed when the last region is
    // removed and when the module closes.
    private NonSpatialMaintenance m_nonSpatialMaintenance;

    private bool m_a2aVoiceEnabled = DefaultA2AVoiceEnabled;

    // Scenes this shared module serves, for callee resolution on THIS instance (the group
    // module's m_sceneList / GetActiveClient pattern). A callee in another process is reached by the carrier.
    private readonly List<Scene> m_scenes = new();

    /// <summary>Write a ChatSessionRequest outcome's body and status.</summary>
    private static void ApplyChatSessionOutcome(ChatSessionOutcome outcome, IOSHttpResponse response)
    {
        if (outcome.Body is not null)
            response.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(outcome.Body));
        // "fetch history" answers with an ARRAY, which the viewer type-checks before reading
        // (llimview.cpp:816-822). Same serialiser, different root node.
        else if (outcome.BodyArray is not null)
            response.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(outcome.BodyArray));

        response.StatusCode = (int)outcome.Status;
    }

    /// <summary>The voice session-id a ChatSessionRequest body carries, or Zero.</summary>
    private static UUID GroupSessionIdOf(OSDMap reqmap)
        => reqmap is not null && reqmap.TryGetUUID("session-id", out UUID id) ? id : UUID.Zero;

    // Subscribes the scene events the voice sessions rely on.
    private void SubscribeVoiceSessionEvents(Scene scene)
    {
        SubscribeGroupVoiceEvents(scene);
        // A client that drops without a logout provision (crash, kill) would otherwise leave its Active A2A
        // record until the idle backstop, suppressing a re-ring between the same pair. Treat the close as that
        // party gone from every record it is in; the record is removed only when the other party is gone too
        // (both-logout semantics), and a later admitted provision re-marks the party present, so this is reversible.
        scene.EventManager.OnClientClosed += (clientID, s) => OnVoiceClientClosed(scene, clientID, s);
        // Steps of a call placed in or answered in another region-server process.
        scene.EventManager.OnIncomingInstantMessage += OnIncomingA2ACarrier;
    }

    // A client closed: drop its avatar-to-avatar records, then release its group seats.
    private void OnVoiceClientClosed(Scene scene, UUID clientID, Scene s)
    {
        // Look the presence up in the scene the close fired for. OnClientClosed runs
        // inside Scene.RemoveClient before the presence is removed, so it is resolvable here. Same guard
        // as WebRtcVoiceServiceModule.Event_OnClientClosed.
        Scene closedIn = s ?? scene;
        ScenePresence sp = closedIn.GetScenePresence(clientID);
        if (!A2ASessionRegistry.ShouldMarkGone(sp != null, sp != null && sp.IsChildAgent))
            return;   // child teardown (border crossing / draw distance) — the agent's voice lives in its root region
        List<A2ASession> departed = new();
        foreach (A2ASession gone in m_a2aSessions.MarkGoneSessions(clientID, null, departed))
        {
            m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} region={RegionName} decision=removed-client-closed",
                logHeader, clientID, gone.SessionId, s?.Name ?? scene.Name);
            // Tell the remaining party the departed one LEFT, here or in the process where it is root.
            SendA2ALeave(gone, clientID, closedIn);
        }
        SendA2ALeaveAcrossProcesses(departed, clientID, closedIn);
        ReleaseSeatsOnClientClosed(scene, clientID, s);
    }

    // What a provision answer changes in the non-spatial records, by the kind admission decided.
    private void OnProvisionAnswered(OSDMap resp, ProvisionAdmission admission, string a2aVs, UUID agentID, Scene scene)
    {
        if (A2AProvisionAdmission.RecordsA2ASession(admission) && resp.TryGetString("viewer_session", out string provVs) && !string.IsNullOrEmpty(provVs))
        {
            // Admitted AND joined -- only the service's success map carries viewer_session
            // (ProvisionResponseBuilder.BuildSuccess); a failure map ({response:"failed"}, with or
            // without error_code) leaves the record as it was. The callee's admitted provision is
            // the accept (Invited -> Active).
            string vs = provVs;
            A2ASession s = m_a2aSessions.MarkProvisioned(admission.Session.SessionId, agentID, vs, out bool becameActive);
            m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} viewer_session={ViewerSession} state={State} decision=provisioned",
                logHeader, agentID, admission.Session.SessionId, vs ?? "-",
                s?.State.ToString() ?? "gone");
            // On the TRANSITION to Active (the callee's accept), both parties'
            // IM panels get ChatterBoxSessionAgentListUpdates -- each receives the other's ENTER
            // plus its own entry, can_voice_chat:true by construction (false hangs up the call,
            // llimview.cpp:4366-4382). Not re-sent on a reconnect re-provision of an already-
            // Active record. This is the participant/moderation surface only; the caller's
            // connected state comes from mixer presence.
            // The caller may be root in another process; its update then travels as a signed join.
            if (becameActive)
                SendA2AActivePair(s, scene);
        }
        else if (admission.Kind == ProvisionKind.Group
                 && resp.TryGetString("viewer_session", out string groupVs) && !string.IsNullOrEmpty(groupVs))
        {
            RecordGroupSeatPresent(admission, groupVs, agentID, scene);
        }
        else if (admission.Kind == ProvisionKind.Logout && a2aVs != "-")
        {
            // Teardown by viewer session: only the record this party joined under that session is
            // affected; a spatial logout matches nothing and is a no-op here. Both parties gone
            // removes the Active record (both-logout). The remaining party (if still on
            // this instance) gets the departed party's LEAVE.
            List<A2ASession> departed = new();
            foreach (A2ASession gone in m_a2aSessions.MarkGoneSessions(agentID, a2aVs, departed))
            {
                m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} viewer_session={ViewerSession} decision=removed-both-logout",
                    logHeader, agentID, gone.SessionId, a2aVs);
                SendA2ALeave(gone, agentID, scene);
            }
            SendA2ALeaveAcrossProcesses(departed, agentID, scene);

            ReleaseGroupSeatsOnLogout(agentID, a2aVs);
        }
    }

    // Decides whether a provision request is admitted, and as what kind. A "local" request goes on to the
    // parcel and estate checks in the caller; a "multiagent" one is admitted by the group or avatar-to-avatar registry.
    // Writes the refusal and returns false when the request is not admitted.
    private bool TryAdmitProvision(OSDMap map, UUID agentID, Scene scene, IOSHttpResponse response,
        out ProvisionAdmission admission, out string channelType, out string a2aVs)
    {
        channelType = string.Empty;
        a2aVs = "-";
        // With no call feature on, a body whose channel_type is "multiagent" or absent goes to the voice service
        // unexamined, as it did before these features existed (see the first branch below). Any other channel type
        // is refused 403 with the features off too; the service throws on one and the request answered 500.
        //
        // Otherwise the parcel and estate checks run only for a "local" request, so every other body needs an
        // authorization of its own, and is refused 403 unless it has one:
        //  - "multiagent": the body's `channel` (not channel_id) must name a live call, the agent must be one of
        //    its parties, and `credentials` must equal the call's token. A group call's `channel` is instead one of
        //    our room keys ("nsv1:group:<uuid>"), which can never be an A2A channel because that lookup is a
        //    UUID.TryParse; NonSpatialProvisionAdmission decides those.
        //  - a teardown ({logout, viewer_session}, no channel_type): routed to the voice service by viewer_session.
        //    It can only end the caller's own voice session, so it needs no channel authorization.
        if (!NonSpatialVoiceFeatureOn() && A2AProvisionAdmission.IsPassthroughBody(map))
        {
            // No call feature is on, so nothing here issues a channel and there is no registry to check one
            // against. The body goes to the voice service unexamined, as it did before these features existed; the
            // service answers it (a room for a multiagent body, nothing for a body with no channel_type).
            admission = A2AProvisionAdmission.Passthrough(map);
        }
        else if (NonSpatialProvisionAdmission.IsGroupProvision(map))
        {
            if (!TryAdmitGroupProvision(map, agentID, scene, response, out admission))
                return false;
        }
        else
        {
            admission = A2AProvisionAdmission.Decide(map, agentID, m_a2aSessions);
        }
        channelType = admission.ChannelType;
        a2aVs = map.TryGetString("viewer_session", out string vsRaw) && !string.IsNullOrEmpty(vsRaw) ? vsRaw : "-";

        // One greppable DEBUG line per provision naming the fields the A2A authorization decides on; the token
        // itself is never logged.
        m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} region={RegionName} channel_type=\"{ChannelType}\" channel={Channel} credentials={Credentials} viewer_session={ViewerSession} logout={Logout} decision={Decision}",
            logHeader, agentID, scene.Name, channelType, admission.Channel,
            map.TryGetString("credentials", out string a2aCreds) && !string.IsNullOrEmpty(a2aCreds) ? "present" : "absent",
            a2aVs, admission.Kind == ProvisionKind.Logout, admission.Decision);

        if (!admission.Admitted)
        {
            m_log.LogWarning($"{logHeader}[ProvisionVoice]: refusing provision with channel_type \"{channelType}\" ({admission.Decision}) from agent {agentID} in region \"{scene.Name}\"");
            response.RawBuffer = llsdUndefAnswerBytes;
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return false;
        }
        return true;
    }

    // True when avatar-to-avatar calls or group voice is switched on.
    private bool NonSpatialVoiceFeatureOn() => m_a2aVoiceEnabled || m_groupVoice.IsUsable;

    // A call request must come from the agent's ROOT region, the same presence rule the close path uses
    // (sp == null || sp.IsChildAgent). A child agent's request is refused before any registry state, but only
    // when a call feature is on: otherwise the request is answered as it always was.
    private bool RefuseChildChatSession(ScenePresence sp, UUID agentID, Scene scene, IOSHttpResponse response)
    {
        if (sp.IsChildAgent && NonSpatialVoiceFeatureOn())
        {
            m_log.LogWarning($"{logHeader} ChatSessionRequest: refusing child agent {agentID} in region \"{scene.RegionInfo.RegionName}\" (chat session requests come from the agent's root region)");
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return true;
        }
        return false;
    }

    // Handles the chat session requests the call features answer: the group arm, then the avatar-to-avatar
    // decision and the shared reply. Returns false when none of them applies, so the stock handler in
    // ChatSessionRequest answers.
    private bool TryHandleNonSpatialChatSession(OSDMap reqmap, UUID agentID, ScenePresence sp, Scene scene, IOSHttpResponse response)
    {
        // The body the viewer actually sent, single-line and greppable, before any decision. It never carries a
        // token (the request has none).
        m_log.LogDebug("{LogHeader} {Tag} agent={AgentId} region={RegionName} body={Body}",
            logHeader, ChatSessionRequestLogic.InstrumentTag, agentID, scene.RegionInfo.RegionName,
            OSDParser.SerializeJsonString(reqmap));

        if (TryHandleGroupChatSession(reqmap, agentID, sp, scene, response))
            return true;

        // The decision is pure and unit-tested (ChatSessionRequestLogic); this adapter applies it.
        // "start p2p voice" records the pair in the invitation registry (params = callee; absent -> 400,
        // replacing the old UUID.Random fallback); "call" mints the per-session token and answers in the
        // HTTP body with voice_credentials { channel_uri, channel_credentials } (llvoicechannel.cpp:687).
        // Nothing in this method admits a multiagent provision; that is decided in ProvisionVoiceAccountRequest.
        if (!m_a2aVoiceEnabled)
            return false;   // the stock handler in ChatSessionRequest answers
        ChatSessionOutcome outcome = ChatSessionRequestLogic.Decide(reqmap, agentID, sp.Name, m_a2aSessions);

        m_log.LogDebug("{LogHeader} {Line}", logHeader, outcome.Instrument);

        // "call" produced an invitation for the other party. Deliver it as a generic
        // ChatterBoxInvitation event (BuildEvent + Enqueue) to the callee on THIS instance; an
        // unreachable callee (offline, or in another region server with no carrier secret set) gets nothing and the
        // caller rings out. A callee whose root presence is in another process is rung through the signed carrier.
        // Never affects this request's outcome: the caller's credentials are returned regardless.
        if (outcome.Invite is not null)
        {
            List<Scene> scenes;
            lock (m_scenes)
                scenes = new List<Scene>(m_scenes);
            string decision = A2AInviteDelivery.Deliver(scenes, outcome.Invite.Callee, outcome.Invite.Body, null, out string calleeRegion);
            decision = RingA2ACalleeAcrossProcesses(outcome.Invite, sp.Name, scene, decision);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                A2AInviteDelivery.Line(outcome.Invite.Callee, outcome.Invite.Caller, outcome.Invite.SessionId, calleeRegion, decision));
            // One ring per Invited record. Marked only on a confirmed enqueue, so a
            // callee-unreachable delivery leaves the flag clear and a caller retry can ring later.
            if (decision == A2AInviteDelivery.DecisionSent)
                m_a2aSessions.MarkInviteSent(outcome.Invite.SessionId);
        }

        // The callee turned the call down: the caller's viewer is told, so the call ends on its screen.
        if (outcome.Declined is not null && outcome.Declined.Callee == agentID)
            SendA2ADeclined(outcome.Declined, scene);

        if (outcome.Reply is not null)
        {
            IEventQueue queue = scene.RequestModuleInterface<IEventQueue>();
            if (queue is null)
            {
                m_log.LogError("{0}: no event queue for scene {1}", logHeader, scene.RegionInfo.RegionName);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return true;
            }

            // The viewer reads only success / temp_session_id / session_id from this event; it never reads
            // voice_enabled, session_name or type (LLViewerChatterBoxSessionStartReply::post, llimview.cpp:4877-4887).
            queue.ChatterBoxSessionStartReply(
                    outcome.Reply.SessionId,
                    sp.Name,
                    2,
                    false,
                    true,
                    outcome.Reply.TempSessionId,
                    true,
                    string.Empty,
                    agentID);
        }

        ApplyChatSessionOutcome(outcome, response);
        return true;
    }

    // Reads the avatar-to-avatar and group settings and builds the non-spatial engine.
    private void InitialiseNonSpatial(IConfigSource config)
    {
        m_a2aVoiceEnabled = ReadA2AVoiceEnabled(m_Config);
        InitialiseCarrierAuth();

        // Group voice, opt-in. The membership and power lookups are bound per-request
        // to the scene's IGroupsModule (grid state via the groups service, so the same answer
        // on every region and every host), not captured here, because scenes are added later.
        if (ReadGroupVoiceEnabled(m_Config))
        {
            m_groupVoice = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = ReadGroupVoiceRequireVoicePower(m_Config),
                Cap = ReadGroupVoiceCap(m_Config),
                IsMember = GroupIsMember,
                Powers = GroupPowersOf,
            };
            m_log.LogInformation(
                "{LogHeader} GROUP VOICE enabled ([WebRtcVoice] GroupVoiceEnabled): cap {Cap}, required powers 0x{Mask:X}{Note}",
                logHeader, NonSpatialCaps.Effective(NonSpatialSessionType.Group, m_groupVoice.Cap),
                m_groupVoice.RequiredMask,
                m_groupVoice.RequireVoicePower ? " (GP_SESSION_JOIN + GP_SESSION_VOICE)" : " (GP_SESSION_JOIN only)");
        }

        // The SAME grid id the room numbers are hashed from, so a room key derived here
        // and a room number derived in the bridge agree across the grid.
        m_nonSpatial = new NonSpatialVoiceSessionEngine(
            m_nonSpatialStore,
            new INonSpatialAdmission[] { new P2PAdmission(), m_groupVoice.ToAdmissionOrNull() },
            JanusAudioBridge.ReadGridId(config),
            limits: ReadNonSpatialLimits(m_Config));

        m_nonSpatial.IsAgentPresent = AgentIsHere;

        m_nonSpatial.LimitRefused += (agent, decision, detail) =>
            m_log.LogWarning("{LogHeader} non-spatial session limit: agent={AgentId} decision={Decision} {Detail}",
                logHeader, agent, decision, detail);

        m_nonSpatialMaintenance = new NonSpatialMaintenance(m_nonSpatial,
            line => m_log.LogDebug("{LogHeader} {Line}", logHeader, line),
            (message, e) => m_log.LogWarning(e, "{LogHeader} {Message}", logHeader, message));
        m_nonSpatialMaintenance.Start();
    }

    // Whether an agent has a root presence in a scene this module serves. The engine asks this before it
    // ends a session for being idle, because a session with a present member is not idle.
    private bool AgentIsHere(UUID agent)
    {
        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);
        foreach (Scene scene in scenes)
        {
            ScenePresence sp = scene.GetScenePresence(agent);
            if (sp is not null && !sp.IsChildAgent && !sp.IsDeleted)
                return true;
        }
        return false;
    }

    // Stops the session sweep timer. Start() brings it back when a region is added again.
    private void StopNonSpatialMaintenance()
    {
        m_nonSpatialMaintenance?.Stop();
    }

    // Tracks the scenes this shared module serves, for callee resolution on THIS instance (the group
    // module's m_sceneList / GetActiveClient pattern).
    private void RegisterSceneForVoiceSessions(Scene scene)
    {
        lock (m_scenes)
            if (!m_scenes.Contains(scene))
                m_scenes.Add(scene);
        m_nonSpatialMaintenance?.Start();
    }

    private void UnregisterSceneForVoiceSessions(Scene scene)
    {
        bool noScenesLeft;
        lock (m_scenes)
        {
            m_scenes.Remove(scene);
            noScenesLeft = m_scenes.Count == 0;
        }
        if (noScenesLeft)
            StopNonSpatialMaintenance();
    }
}
