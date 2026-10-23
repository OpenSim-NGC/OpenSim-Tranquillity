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
using System.Threading.Tasks;
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

// Non-spatial voice sessions: the shared engine, the avatar-to-avatar calls kept in it and the chat session
// plumbing the group and conference features build on.
public partial class WebRtcVoiceRegionModule
{
    // [WebRtcVoice] A2AVoiceEnabled: avatar-to-avatar calls (the "call" and "start p2p voice" chat session
    // requests answered with an invitation registry and per-session credentials). Off by default: the
    // chat session handler then answers as it did before the feature existed.
    public const bool DefaultA2AVoiceEnabled = false;

    public static bool ReadA2AVoiceEnabled(IConfig pConfig)
        => pConfig.GetBoolean("A2AVoiceEnabled", DefaultA2AVoiceEnabled);

    // Avatar-to-avatar calls. They are sessions of the same non-spatial store as group and conference calls: in this
    // process, or in the grid session service when SessionStoreMode is grid. Shared by every scene this module
    // serves; thread-safe because ChatSessionRequest arrives on cap HTTP threads. The store is read through
    // m_nonSpatial on every use, so these follow whichever store was set up. The invitation itself is delivered
    // only to a callee on this instance; a callee who is root in another process is rung through the carrier
    // (WebRtcVoiceRegionModule.A2ACarrier.cs).
    private A2ACalls m_a2aCalls;
    private A2ACalls A2A => m_a2aCalls ??= new A2ACalls(() => m_nonSpatial);

    // The non-spatial engine and the group policy. The engine is constructed unconditionally and costs nothing
    // when idle; the POLICY is what gates every group arm, and it is GroupVoicePolicy.Disabled unless
    // [WebRtcVoice] GroupVoiceEnabled is set. The store is the in-process one; a service-backed store can
    // replace it behind INonSpatialSessionStore without touching anything here.
    private readonly InMemoryNonSpatialSessionStore m_nonSpatialStore = new();

    private INonSpatialSessions m_nonSpatial;

    // Set only when [WebRtcVoice] SessionStoreMode is grid: the region's client for the grid session service,
    // the timer that keeps this region's seats alive there, and the queue that holds departures it could not
    // deliver. The outbox is worked every few seconds; the service itself is behind a circuit breaker.
    private GridNonSpatialSessions m_gridSessions;
    private GridSeatRenewer m_gridRenewer;
    private DepartOutbox m_gridOutbox;
    private SessionCircuitBreaker m_gridBreaker;
    private string m_gridServerUri = string.Empty;
    private int m_gridProbe;   // 0 still checking, 1 the service answered, 2 it did not
    private static bool s_sessionsCommandRegistered;
    private static readonly TimeSpan GridOutboxInterval = TimeSpan.FromSeconds(5);
    private long m_gridOutboxLostLoggedAt;

    // [WebRtcVoice] CarrierSecret signs and verifies the two cross-process carriers (the ring and the
    // conference text line). Unset, or shorter than CarrierAuth.MinSecretLength, it is off: no carrier is
    // sent and every incoming one is refused. A grid that runs its regions in more than one process sets
    // the same value on every region server.
    private CarrierAuth m_carrierAuth = CarrierAuth.Off();

    // True when the grid session service judges rings (SessionStoreMode grid). A ring then needs no shared secret:
    // the receiving region asks the service whether the ring is genuine instead of trusting what it carries.
    private bool RingsAreVerifiedByTheStore => m_nonSpatial is { StoreVerifiesRings: true };

    // Reads [WebRtcVoice] CarrierSecret and says once, at startup, whether cross-process carriers are on.
    // The secret itself is never logged.
    private void InitialiseCarrierAuth()
    {
        string secret = m_Config.GetString("CarrierSecret", string.Empty);
        string problem = CarrierAuth.CheckSecret(secret);
        m_carrierAuth = problem is null ? new CarrierAuth(secret) : CarrierAuth.Off();
        (bool isError, string text) = CarrierAuth.StartupLine(problem, SessionStoreSettings.Read(m_Config).Grid);
        if (isError)
            m_log.LogError("{LogHeader} {Line}", logHeader, text);
        else
            m_log.LogInformation("{LogHeader} {Line}", logHeader, text);
    }

    private void LogCarrierOff(UUID session)
        => m_log.LogDebug("{LogHeader} carrier not sent: session={Session} decision={Decision}", logHeader, session,
                          CarrierIntake.DecisionCarrierOff);

    // An accepted carrier (one that passed authentication) is logged at Debug with the session id, its type and
    // the region it came from, so a call can be followed across processes. Never contents.
    private void LogCarrierAccepted(string kind, UUID session, NonSpatialSessionType type, UUID origin)
        => m_log.LogDebug("{LogHeader} {Line}", logHeader, CarrierAuth.AcceptedLine(kind, session, type, origin));

    // A refusal that a viewer or a sender can repeat as fast as it likes is worth one warning a minute for each kind of
    // refusal and each agent. The line says how many it stands for.
    private readonly RefusalLogGate m_refusalWarnGate = new RefusalLogGate();
    private int m_refusalWarnCalls;

    private void WarnRefusal(string kind, UUID key, Func<string> line)
    {
        DateTime now = DateTime.UtcNow;
        if (m_refusalWarnGate.ShouldLog(kind, key, now, out int held))
            m_log.LogWarning("{Line}{More}", line(), held > 0 ? $" ({held} more like this in the last minute)" : string.Empty);
        if (Interlocked.Increment(ref m_refusalWarnCalls) % 1024 == 0)
            m_refusalWarnGate.Prune(now, NonSpatialVoiceSessionEngine.TransientStateMaxAge);
    }

    // A refused carrier is logged by reason and running count only, never by contents, and not on every
    // refusal: the first of each reason and then every hundredth, so a flood cannot flood the log.
    private void LogCarrierRefusal(string kind, string reason, long count)
    {
        if (CarrierAuth.ShouldLog(count))
            m_log.LogWarning("{LogHeader} carrier refused: kind={Kind} reason={Reason} count={Count}",
                logHeader, kind, reason, count);
    }

    // [WebRtcVoice] MaxNonSpatialSessions / MaxSessionsPerAgent: how many non-spatial sessions this process
    // holds, and how many live ones one agent may have started. ConferenceRequestBurst /
    // ConferenceRequestRefillSeconds: the per-agent token bucket on "start conference" and "invite".
    // A value below 1 (above 0 for the refill) is not usable, so it reads as the default.
    public static NonSpatialLimits ReadNonSpatialLimits(IConfig pConfig)
        => new NonSpatialLimits
        {
            MaxNonSpatialSessions = PositiveOrDefault(pConfig.GetInt("MaxNonSpatialSessions", NonSpatialLimits.DefaultMaxNonSpatialSessions),
                                                      NonSpatialLimits.DefaultMaxNonSpatialSessions),
            MaxSessionsPerAgent = PositiveOrDefault(pConfig.GetInt("MaxSessionsPerAgent", NonSpatialLimits.DefaultMaxSessionsPerAgent),
                                                    NonSpatialLimits.DefaultMaxSessionsPerAgent),
            ConferenceRequestBurst = PositiveOrDefault(pConfig.GetInt("ConferenceRequestBurst", NonSpatialLimits.DefaultConferenceRequestBurst),
                                                       NonSpatialLimits.DefaultConferenceRequestBurst),
            ConferenceRequestRefillSeconds = ReadPositiveDouble(pConfig, "ConferenceRequestRefillSeconds",
                                                                NonSpatialLimits.DefaultConferenceRequestRefillSeconds),
        };

    private static double ReadPositiveDouble(IConfig pConfig, string key, double fallback)
    {
        double value = pConfig.GetDouble(key, fallback);
        return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;
    }

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
        SubscribeConferenceEvents(scene);
        // A client that drops without a logout provision (crash, kill) would otherwise leave its Active A2A
        // call until the idle backstop, suppressing a re-ring between the same pair. Treat the close as that
        // party gone from every call it is in; the call ends only when the other party is gone too
        // (both-logout semantics), and a later admitted provision seats the party again, so this is reversible.
        scene.EventManager.OnClientClosed += (clientID, s) => OnVoiceClientClosed(scene, clientID, s);
        // Steps of a call placed in or answered in another region-server process.
        scene.EventManager.OnIncomingInstantMessage += OnIncomingA2ACarrier;
    }

    // A client closed: drop its avatar-to-avatar records, then release its group and conference seats.
    private void OnVoiceClientClosed(Scene scene, UUID clientID, Scene s)
    {
        // Look the presence up in the scene the close fired for. OnClientClosed runs
        // inside Scene.RemoveClient before the presence is removed, so it is resolvable here. Same guard
        // as WebRtcVoiceServiceModule.Event_OnClientClosed.
        Scene closedIn = s ?? scene;
        ScenePresence sp = closedIn.GetScenePresence(clientID);
        if (!A2ACalls.ShouldMarkGone(sp != null, sp != null && sp.IsChildAgent))
            return;   // child teardown (border crossing / draw distance) — the agent's voice lives in its root region
        // The agent leaves every call, group and conference it holds a seat in. One departure serves them all; the
        // avatar-to-avatar calls it ended are handled here, the rest in ReleaseSeatsOnClientClosed.
        IReadOnlyList<NonSpatialVoiceSession> left = m_nonSpatial?.DepartAll(clientID, DepartureReason.PresenceLost)
            ?? (IReadOnlyList<NonSpatialVoiceSession>)Array.Empty<NonSpatialVoiceSession>();
        foreach (A2ASession gone in A2A.Ended(left))
        {
            m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} region={RegionName} decision=removed-client-closed",
                logHeader, clientID, gone.SessionId, s?.Name ?? scene.Name);
            // Tell the remaining party the departed one LEFT, here or in the process where it is root.
            SendA2ALeave(gone, clientID, closedIn);
        }
        SendA2ALeaveAcrossProcesses(left, clientID, closedIn);
        ReleaseSeatsOnClientClosed(scene, clientID, s, left);
    }

    // What a provision answer changes in the non-spatial records, by the kind admission decided.
    private void OnProvisionAnswered(OSDMap resp, ProvisionAdmission admission, string a2aVs, UUID agentID, Scene scene)
    {
        if (A2AProvisionAdmission.RecordsListenerRoom(admission.Kind))
        {
            RecordListenerRoom(resp, agentID, scene);
            if (resp.ContainsKey("viewer_session"))
                ApplyStockMuteOnJoin(agentID, scene);
        }
        else if (A2AProvisionAdmission.RecordsA2ASession(admission) && resp.TryGetString("viewer_session", out string provVs) && !string.IsNullOrEmpty(provVs))
        {
            // Admitted AND joined -- only the service's success map carries viewer_session
            // (ProvisionResponseBuilder.BuildSuccess); a failure map ({response:"failed"}, with or
            // without error_code) leaves the call as it was. The callee's admitted provision is
            // the accept (Invited -> Active).
            string vs = provVs;
            A2ASession s = A2A.MarkProvisioned(admission.Session.SessionId, agentID, scene.RegionInfo.RegionID, vs, out bool becameActive);
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
        else if ((admission.Kind == ProvisionKind.Group || admission.Kind == ProvisionKind.Adhoc)
                 && resp.TryGetString("viewer_session", out string groupVs) && !string.IsNullOrEmpty(groupVs))
        {
            RecordGroupSeatPresent(admission, groupVs, agentID, scene);
        }
        else if (admission.Kind == ProvisionKind.Logout && a2aVs != "-")
        {
            // Teardown by viewer session: only the seat this party joined under that session is
            // released; a spatial logout matches nothing and is a no-op here. Both parties gone
            // ends the Active call (both-logout). The remaining party (if still on
            // this instance) gets the departed party's LEAVE. The same teardown releases a group or
            // conference seat held under that viewer session.
            IReadOnlyList<NonSpatialVoiceSession> left = m_nonSpatial?.DepartByViewerSession(agentID, a2aVs, DepartureReason.VoiceTeardown)
                ?? (IReadOnlyList<NonSpatialVoiceSession>)Array.Empty<NonSpatialVoiceSession>();
            foreach (A2ASession gone in A2A.Ended(left))
            {
                m_log.LogDebug("{LogHeader} [A2A PROVISION] agent={AgentId} session-id={SessionId} viewer_session={ViewerSession} decision=removed-both-logout",
                    logHeader, agentID, gone.SessionId, a2aVs);
                SendA2ALeave(gone, agentID, scene);
            }
            SendA2ALeaveAcrossProcesses(left, agentID, scene);

            ReleaseGroupSeatsOnLogout(agentID, a2aVs, left);
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
            admission = A2AProvisionAdmission.Decide(map, agentID, A2A);
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
            string refusedType = channelType, refusedDecision = admission.Decision;   // not out parameters inside the lambda
            WarnRefusal("provision-channel-type", agentID, () =>
                $"{logHeader}[ProvisionVoice]: refusing provision with channel_type \"{refusedType}\" ({refusedDecision}) from agent {agentID} in region \"{scene.Name}\"");
            response.RawBuffer = llsdUndefAnswerBytes;
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return false;
        }
        return true;
    }

    // True when avatar-to-avatar calls, group voice or conferences are switched on.
    private bool NonSpatialVoiceFeatureOn() => m_a2aVoiceEnabled || m_groupVoice.IsUsable || m_adhocVoiceEnabled;

    // A call request must come from the agent's ROOT region, the same presence rule the close path uses
    // (sp == null || sp.IsChildAgent). A child agent's request is refused before any registry state, but only
    // when a call feature is on: otherwise the request is answered as it always was.
    private bool RefuseChildChatSession(ScenePresence sp, UUID agentID, Scene scene, IOSHttpResponse response)
    {
        if (sp.IsChildAgent && NonSpatialVoiceFeatureOn())
        {
            WarnRefusal("child-agent", agentID, () =>
                $"{logHeader} ChatSessionRequest: refusing child agent {agentID} in region \"{scene.RegionInfo.RegionName}\" (chat session requests come from the agent's root region)");
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return true;
        }
        return false;
    }

    // Handles the chat session requests the call features answer: the group and conference arms, then the
    // avatar-to-avatar decision and the shared reply. Returns false when none of them applies, so the stock
    // handler in ChatSessionRequest answers.
    private bool TryHandleNonSpatialChatSession(OSDMap reqmap, UUID agentID, ScenePresence sp, Scene scene, IOSHttpResponse response)
    {
        // The body the viewer actually sent, single-line and greppable, before any decision. It never carries a
        // token (the request has none).
        m_log.LogDebug("{LogHeader} {Tag} agent={AgentId} region={RegionName} body={Body}",
            logHeader, ChatSessionRequestLogic.InstrumentTag, agentID, scene.RegionInfo.RegionName,
            OSDParser.SerializeJsonString(reqmap));

        // Call moderation first: a "mute update" or "session update" is not a call, an accept or a start, and with
        // the feature off this declines and the request goes the way it always went.
        if (TryHandleCallModeration(reqmap, agentID, response))
            return true;

        if (TryHandleGroupChatSession(reqmap, agentID, sp, scene, response))
            return true;

        // The decision is pure and unit-tested (ChatSessionRequestLogic); this adapter applies it.
        // "start p2p voice" records the pair as a call in the session store (params = callee; absent -> 400,
        // replacing the old UUID.Random fallback); "call" mints the per-session token and answers in the
        // HTTP body with voice_credentials { channel_uri, channel_credentials } (llvoicechannel.cpp:687).
        // Nothing in this method admits a multiagent provision; that is decided in ProvisionVoiceAccountRequest.
        // "start conference" opens the ad-hoc session and produces the two ids the viewer is waiting on. It is
        // deliberately not an early return like the group and conference arms: the outcome falls into the shared
        // tail below, whose Reply path already sends the event-queue ChatterBoxSessionStartReply. One sender for
        // that event, not two.
        //
        // The order of the arms does not matter. The group arms take a "call" only when the voice session-id is a
        // group the agent belongs to; a conference id is a server-minted random UUID and is never a group id. The
        // conference arms take it only when the store holds an ad-hoc session with this agent as a member. The two
        // discriminators are disjoint by construction.
        if (TryHandleAdhocChatSession(reqmap, agentID, sp, scene, response))
            return true;

        if (!AdhocVoiceChatSession.TryHandleStartConference(reqmap, agentID, m_nonSpatial,
                                                            scene.RegionInfo.RegionID, m_adhocVoiceEnabled,
                                                            out ChatSessionOutcome outcome))
        {
            if (!m_a2aVoiceEnabled)
                return false;   // the stock handler in ChatSessionRequest answers
            outcome = ChatSessionRequestLogic.Decide(reqmap, agentID, sp.Name, A2A, scene.RegionInfo.RegionID);
        }

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
                A2A.MarkInviteSent(outcome.Invite.SessionId);
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

            // The conference just opened, so ring the invitees the viewer named in `params`.
            // Fired on the engine's 0 -> 1 seat transition, which for AD-HOC is the creator's own
            // seat inside Start -- there is no other accept to hang it on. Placed AFTER the re-key
            // event so a ring failure can never cost the initiator the reply it is blocked on.
            if (outcome.StartedRinging)
                RingAdhocMembers(m_nonSpatial?.Get(outcome.Reply.SessionId), agentID, sp.Name, null);
            // The creator took the first seat inside Start, so the roster exists from
            // this moment. One entry, the creator's own -- which is what the group path also sends
            // to an agent opening a voice session (GroupsMessagingModule.cs).
            SendConferenceRoster(m_nonSpatial?.Get(outcome.Reply.SessionId), agentID, joined: true);
        }

        ApplyChatSessionOutcome(outcome, response);
        return true;
    }

    // Reads the avatar-to-avatar, group and conference settings and builds the non-spatial engine.
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

        m_adhocVoiceEnabled = ReadAdhocVoiceEnabled(m_Config);
        if (m_adhocVoiceEnabled)
            m_log.LogInformation("{LogHeader} ADHOC VOICE enabled ([WebRtcVoice] AdhocVoiceEnabled): "
                + "\"start conference\" answers with an event-queue ChatterBoxSessionStartReply", logHeader);

        // The SAME grid id the room numbers are hashed from, so a room key derived here
        // and a room number derived in the bridge agree across the grid.
        INonSpatialAdmission[] admissions =
            { new P2PAdmission(), new AdhocAdmission(), m_groupVoice.ToAdmissionOrNull() };

        SessionStoreSettings store = SessionStoreSettings.Read(m_Config);
        if (store.Problem is not null)
            m_log.LogError("{LogHeader} [WebRtcVoice] {Problem}; non-spatial sessions stay in this process", logHeader, store.Problem);
        RegisterSessionsCommand();
        if (store.Grid)
        {
            InitialiseGridSessions(config, store, admissions);
            return;
        }

        NonSpatialVoiceSessionEngine engine = new NonSpatialVoiceSessionEngine(
            m_nonSpatialStore,
            admissions,
            JanusAudioBridge.ReadGridId(config),
            limits: ReadNonSpatialLimits(m_Config));
        m_nonSpatial = engine;

        engine.IsAgentPresent = AgentIsHere;

        engine.LimitRefused += (agent, decision, detail) =>
            m_log.LogWarning("{LogHeader} non-spatial session limit: agent={AgentId} decision={Decision} {Detail}",
                logHeader, agent, decision, detail);

        m_nonSpatialMaintenance = new NonSpatialMaintenance(engine,
            line => m_log.LogDebug("{LogHeader} {Line}", logHeader, line),
            (message, e) => m_log.LogWarning(e, "{LogHeader} {Message}", logHeader, message));
        m_nonSpatialMaintenance.Start();
    }

    // [WebRtcVoice] SessionStoreMode = grid: every session operation goes to the grid session service in Robust.
    // The service decides the seat cap, the ring-once latch and the start races, and sweeps expired sessions, so
    // none of that runs here. When it does not answer a request is refused; there is no fallback to a store in
    // this process.
    private void InitialiseGridSessions(IConfigSource config, SessionStoreSettings store, INonSpatialAdmission[] admissions)
    {
        string authProblem = VoiceSessionServerConnector.CheckAuthConfig(config, "WebRtcVoice");
        if (authProblem is not null)
            m_log.LogError("{LogHeader} grid session store: {Problem}; the service will refuse these calls", logHeader, authProblem);

        VoiceSessionServicesConnector connector = new(config, "WebRtcVoice", store.ServerUri, store.TimeoutMs);
        SessionCircuitBreaker breaker = new(store.BreakerFailures, TimeSpan.FromSeconds(store.BreakerWindowSeconds),
                                            TimeSpan.FromSeconds(store.BreakerOpenSeconds));
        breaker.Changed += state =>
        {
            if (state == BreakerState.Open)
                m_log.LogWarning("{LogHeader} grid session store at {Uri} did not answer {Failures} calls in {Window} s; "
                    + "calls are refused for {Open} s without waiting for it", logHeader, store.ServerUri,
                    store.BreakerFailures, store.BreakerWindowSeconds, store.BreakerOpenSeconds);
            else if (state == BreakerState.Closed)
                m_log.LogInformation("{LogHeader} grid session store at {Uri} is answering again", logHeader, store.ServerUri);
        };
        GuardedVoiceSessionService service = new(connector, breaker);
        DepartOutbox outbox = new(service, store.OutboxMax, TimeSpan.FromSeconds(store.OutboxSeconds));
        outbox.Lost += why =>
        {
            // At most one line a minute; the seat lease returns the seat in any case.
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref m_gridOutboxLostLoggedAt);
            if (now - last >= 60000 && Interlocked.CompareExchange(ref m_gridOutboxLostLoggedAt, now, last) == last)
                m_log.LogWarning("{LogHeader} grid session store: gave up a queued departure ({Why}); the seat lease will return the seat",
                    logHeader, why);
        };
        m_gridOutbox = outbox;
        GridNonSpatialSessions grid = new(service, admissions, outbox: outbox);
        grid.Unavailable += operation =>
            m_log.LogWarning("{LogHeader} grid session store did not answer {Operation} at {Uri}; new calls are refused until it does",
                logHeader, operation, store.ServerUri);
        m_gridSessions = grid;
        m_gridBreaker = breaker;
        m_gridServerUri = store.ServerUri;
        m_nonSpatial = grid;
        m_gridRenewer = new GridSeatRenewer(grid, PresentAgents,
            (message, e) => m_log.LogWarning(e, "{LogHeader} {Message}", logHeader, message),
            TimeSpan.FromSeconds(store.RenewSeconds), seatRemoved: OnGridSeatRemoved);
        m_log.LogInformation("{LogHeader} non-spatial sessions are kept by the grid session store at {Uri} "
            + "(timeout {Timeout} ms, seats renewed every {Renew} s, up to {Queue} departures queued for {Age} s, "
            + "refusing calls for {Open} s after {Failures} failures in {Window} s)", logHeader, store.ServerUri, store.TimeoutMs,
            store.RenewSeconds, store.OutboxMax, store.OutboxSeconds, store.BreakerOpenSeconds, store.BreakerFailures, store.BreakerWindowSeconds);

        // Ask the service once whether it is there, so an address with no service behind it (the wrong URI, a Robust
        // without the service, a wrong password) is reported when the region starts, not by the first refused call.
        // Asked in the background, straight to the connector, so it neither holds up start-up nor counts against the breaker.
        Task.Run(() =>
        {
            bool answered = connector.Get(UUID.Zero) is not null;
            Interlocked.Exchange(ref m_gridProbe, answered ? 1 : 2);
            if (answered)
                m_log.LogInformation("{LogHeader} grid session store at {Uri} answered the start-up check", logHeader, store.ServerUri);
            else
                m_log.LogError("{LogHeader} grid session store at {Uri} did not answer the start-up check: check SessionStoreServerURI, "
                    + "that the Robust that serves it has the voice session service enabled, and the service credentials. "
                    + "New calls are refused until it answers", logHeader, store.ServerUri);
        });
    }

    // "show voice sessions": where this region keeps its call sessions, and whether the service is answering.
    private void RegisterSessionsCommand()
    {
        lock (typeof(WebRtcVoiceRegionModule))
        {
            if (s_sessionsCommandRegistered || MainConsole.Instance is null)
                return;   // unit tests / embedded hosts have no console
            s_sessionsCommandRegistered = true;
        }
        MainConsole.Instance.Commands.AddCommand("Voice", false, "show voice sessions",
            "show voice sessions",
            "Show where this region keeps its call sessions and, when it is a grid service, whether it is answering",
            (module, args) =>
            {
                foreach (string line in BuildSessionStoreReport().Format())
                    MainConsole.Instance.Output(line);
            });
    }

    private SessionStoreReport BuildSessionStoreReport()
    {
        bool grid = m_gridSessions is not null;
        int probe = Volatile.Read(ref m_gridProbe);
        return new SessionStoreReport
        {
            Grid = grid,
            ServerUri = grid ? m_gridServerUri : string.Empty,
            ProbeAnswered = probe == 0 ? null : probe == 1,
            Breaker = m_gridBreaker?.State ?? BreakerState.Closed,
            LocalSessions = grid ? 0 : m_nonSpatialStore.All().Count,
            HeldSeats = m_gridSessions?.HeldSeats().Count ?? 0,
            QueuedDeparts = m_gridOutbox?.Count ?? 0,
            DroppedDeparts = m_gridOutbox?.Dropped ?? 0,
            CarriersSigned = m_carrierAuth.Enabled,
        };
    }

    // The agents with a root presence in a scene this module serves; the grid store keeps their seats.
    private IReadOnlyList<UUID> PresentAgents()
    {
        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);
        HashSet<UUID> agents = new();
        foreach (Scene scene in scenes)
            scene.ForEachRootScenePresence(sp => agents.Add(sp.UUID));
        return new List<UUID>(agents);
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
        m_gridRenewer?.Stop();
        m_gridOutbox?.Stop();
        StopCallModerationTick();
        StopCallMuteSink();
    }

    // Tracks the scenes this shared module serves, for callee resolution on THIS instance (the group
    // module's m_sceneList / GetActiveClient pattern).
    private void RegisterSceneForVoiceSessions(Scene scene)
    {
        lock (m_scenes)
            if (!m_scenes.Contains(scene))
                m_scenes.Add(scene);
        m_nonSpatialMaintenance?.Start();
        m_gridRenewer?.Start();
        m_gridOutbox?.Start(GridOutboxInterval);
        ReleaseSeatsOfStartedRegion(scene);
        RegisterCallModerationScene(scene);
    }

    // A region that has just started holds no viewers, so any seat the grid store still has for it is stale (the
    // process before it ended without leaving). Asked once per region, off the loading thread, since the store
    // may be slow to answer.
    private void ReleaseSeatsOfStartedRegion(Scene scene)
    {
        GridNonSpatialSessions grid = m_gridSessions;
        if (grid is null)
            return;
        UUID region = scene.RegionInfo.RegionID;
        string name = scene.RegionInfo.RegionName;
        Task.Run(() =>
        {
            int released = grid.DepartRegion(region);
            if (released > 0)
                m_log.LogInformation("{LogHeader} grid session store: released {Seats} seat(s) left by an earlier run of region {Region}",
                    logHeader, released, name);
        });
    }

    private void UnregisterSceneForVoiceSessions(Scene scene)
    {
        bool noScenesLeft;
        lock (m_scenes)
        {
            m_scenes.Remove(scene);
            noScenesLeft = m_scenes.Count == 0;
        }
        UnregisterCallModerationScene(scene, noScenesLeft);
        if (noScenesLeft)
            StopNonSpatialMaintenance();
    }
}
