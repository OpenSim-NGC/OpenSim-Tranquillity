/*
 * Copyright (c) 2026 Legion Builds
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
using OpenSim.Services.Interfaces;   // IPresenceService, for the online-only ring filter
using osWebRtcVoice.NonSpatial;   // the non-spatial engine and the group arm

namespace osWebRtcVoice;

// Ad-hoc conference voice and conference text: the roster, the text fan-out and the invitation ring.
public partial class WebRtcVoiceRegionModule
{
    // [WebRtcVoice] AdhocVoiceEnabled. OFF by default, so this deploy changes nothing until
    // it is set: "start conference" keeps its earlier 200-and-no-body behaviour and the viewer
    // keeps timing out exactly as it does today. Opt-in only.
    public const bool DefaultAdhocVoiceEnabled = false;

    public static bool ReadAdhocVoiceEnabled(IConfig pConfig)
        => pConfig.GetBoolean("AdhocVoiceEnabled", DefaultAdhocVoiceEnabled);

    /// <summary>[WebRtcVoice] AdhocVoiceEnabled. False keeps the earlier stub behaviour.</summary>
    private bool m_adhocVoiceEnabled = DefaultAdhocVoiceEnabled;

    // ---- ad-hoc conference TEXT -------------------------------------------------------------

    private void OnMakeRootAgentConferenceText(ScenePresence sp)
    {
        if (sp?.ControllingClient is not null)
            sp.ControllingClient.OnInstantMessage += OnViewerConferenceText;
    }

    private void OnMakeChildAgentConferenceText(ScenePresence sp)
    {
        if (sp?.ControllingClient is not null)
            sp.ControllingClient.OnInstantMessage -= OnViewerConferenceText;
    }

    /// <summary>
    /// A member typed into a conference. The viewer sends ImprovedInstantMessage with
    /// dialog SessionSend and the AUTHORITATIVE session id (the start reply re-keyed it); without
    /// this handler nothing claims it -- InstantMessageModule drops SessionSend in its default arm
    /// and GroupsMessagingModule only takes a GROUP id.
    ///
    /// GROUP TEXT IS UNTOUCHED: TryPlan returns false for any id the engine does not hold as ADHOC,
    /// and this handler then does nothing at all, so GroupsMessagingModule sees the event exactly as
    /// it does today.
    /// </summary>
    private void OnViewerConferenceText(IClientAPI client, GridInstantMessage im)
    {
        if (im is null || m_nonSpatial is null)
            return;

        // The chat-session LEAVE. Dialog 18 is IM_SESSION_LEAVE and libomv's
        // SessionDrop at once -- one value, two names, measured from the deployed enum -- so this
        // single arm covers both. It releases CHAT membership and never the voice seat.
        if (im.dialog == AdhocChatMembership.DialogSessionLeave)
        {
            OnViewerConferenceChatLeave(client, im);
            return;
        }

        if (im.dialog != AdhocTextSession.DialogSessionSend)
            return;

        UUID sessionId = new UUID(im.imSessionID);
        UUID sender = new UUID(im.fromAgentID);
        if (!AdhocTextSession.TryPlan(m_nonSpatial, m_adhocVoiceEnabled, sessionId, sender, im.message,
                                      out AdhocTextPlan plan, out string decision))
            return;   // not a conference of ours -- the group module and everyone else still see it

        if (plan is null)
        {
            // A non-member's SessionSend for this voice session is refused AND LOGGED. Warning,
            // not debug: a stranger addressing a conference id is worth seeing without turning
            // debug on.
            WarnRefusal("conference-text-" + decision, sender, () => logHeader + " " +
                AdhocTextSession.Line(sender, sessionId, decision,
                    "region=\"" + (client?.Scene?.RegionInfo?.RegionName ?? string.Empty) + "\""));
            return;
        }

        FanOutConferenceText(plan, client?.Name ?? im.fromAgentName,
                             client?.Scene?.RegionInfo?.RegionID ?? UUID.Zero, decision);
    }

    /// <summary>
    /// The viewer left the conference CHAT (UDP dialog 18). The member leaves the roster and
    /// KEEPS THEIR VOICE SEAT -- the lifecycles are independent, and this is lifecycle (1)
    /// alone. The roster update goes out after the engine has recorded it, so the departing member
    /// is already off the roster and is not sent their own LEAVE.
    /// </summary>
    private void OnViewerConferenceChatLeave(IClientAPI client, GridInstantMessage im)
    {
        UUID sessionId = new UUID(im.imSessionID);
        UUID agent = new UUID(im.fromAgentID);

        if (!AdhocChatMembership.TryHandleLeave(m_nonSpatial, m_adhocVoiceEnabled, sessionId, agent,
                                                out string decision, out bool stillSeated))
            return;   // a group or P2P leave -- not ours, and every other subscriber still sees it

        NonSpatialVoiceSession session = m_nonSpatial.Get(sessionId);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            AdhocChatMembership.Line(agent, sessionId, decision, stillSeated,
                $"region=\"{client?.Scene?.RegionInfo?.RegionName ?? string.Empty}\" "
                + $"roster={(session is null ? 0 : AdhocConferenceRoster.Roster(session).Count)} "
                + $"seats={session?.SeatsHeld ?? 0}"));

        if (decision == AdhocChatMembership.DecisionLeft)
            SendConferenceRoster(session, agent, joined: false);
    }

    /// <summary>
    /// Deliver one conference line to every other member: a member with a ROOT presence in this process directly,
    /// everyone else over the carrier with the text magic, which reaches the process that holds their root
    /// presence. That process delivers only to a root presence recorded as in the conversation in its own copy
    /// of the record, so a member who declined or left there gets nothing. The SENDER IS NEVER IN the plan, so
    /// there is no echo -- a deliberate divergence from the group path, which does echo; see
    /// AdhocTextSession's header for why.
    /// </summary>
    private void FanOutConferenceText(AdhocTextPlan plan, string fromName, UUID originRegion, string decision)
    {
        if (plan is null)
            return;

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            AdhocTextSession.Line(plan.Sender, plan.Session.SessionId, decision,
                $"recipients={plan.Recipients.Count} elsewhere={plan.Elsewhere.Count}"));
        if (plan.Recipients.Count == 0 && plan.Elsewhere.Count == 0)
            return;

        // Local vs remote by the same root-presence test the ring fan-out uses, so text and ring agree
        // about who is reachable in this process.
        HashSet<UUID> rootHere = new HashSet<UUID>(GroupVoiceInvite.RootAgents(scenes));
        List<UUID> remote = new List<UUID>();
        foreach (UUID target in plan.Recipients)
        {
            if (!rootHere.Contains(target))
            {
                remote.Add(target);
                continue;
            }
            DeliverConferenceTextLocally(scenes, target, plan.Sender, fromName, plan.Session.SessionId,
                                         originRegion, plan.Message);
        }
        // Members this process cannot rule in or out: only ever carried. One who has a root presence here and
        // is not recorded as in the conversation here is not in it, and gets nothing. A store shared by every
        // process (the one that keys the conference) records an answer wherever it is given, so there is nobody
        // to guess about there: an invitee it records as invited has not answered, and is not written to.
        if (m_nonSpatial is not { StoreIssuesCarrierKeys: true })
            foreach (UUID target in plan.Elsewhere)
                if (!rootHere.Contains(target))
                    remote.Add(target);

        SendRemoteConferenceText(plan, fromName, originRegion, scenes, remote);
    }

    /// <summary>Hand one rebuilt SessionSend to a client in this process.</summary>
    private void DeliverConferenceTextLocally(List<Scene> scenes, UUID target, UUID sender, string fromName,
                                              UUID sessionId, UUID originRegion, string message)
    {
        // Root presence only: a child presence here answers from another process.
        Scene scene = A2AInviteDelivery.ResolveCalleeScene(scenes, target, out bool childOnly);
        IClientAPI to = childOnly ? null : scene?.GetScenePresence(target)?.ControllingClient;
        if (to is null)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocTextSession.Line(sender, sessionId, A2AAgentListDelivery.DecisionNoPresence,
                    "target=" + target));
            return;
        }
        try
        {
            to.SendInstantMessage(AdhocTextSession.BuildForViewer(target, sender, fromName, sessionId,
                                                                  originRegion, message));
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocTextSession.Line(sender, sessionId, AdhocTextSession.DecisionDelivered,
                    "target=" + target + " path=local"));
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} local conference text to {Target} failed", logHeader, target);
        }
    }

    /// <summary>
    /// Carry the line to members on another region server. Same transport family as the ring, same
    /// online-only rule: an offline member is simply not sent to. Nothing is stored for them -- both
    /// offline-IM allowlists reject SessionSend (OfflineMessageModule.cs,
    /// OfflineIMRegionModule.cs) and the carrier sets offline = 0 besides.
    /// </summary>
    private void SendRemoteConferenceText(AdhocTextPlan plan, string fromName, UUID originRegion,
                                          List<Scene> scenes, List<UUID> candidates)
    {
        if (candidates is null || candidates.Count == 0)
            return;
        bool storeKeys = m_nonSpatial is { StoreIssuesCarrierKeys: true };
        if (!m_carrierAuth.Enabled && !storeKeys)
        {
            LogCarrierOff(plan.Session.SessionId);
            return;
        }
        // A store that keys conferences gives the sender's region the key to sign with; a sender who is not seated
        // in the conference is given none and sends nothing across.
        string sessionKey = storeKeys ? m_nonSpatial.CarrierKey(plan.Session.SessionId, plan.Sender) : null;
        if (storeKeys && string.IsNullOrEmpty(sessionKey))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocTextTransport.Line(UUID.Zero, plan.Session.SessionId, CarrierIntake.DecisionNoCarrierKey));
            return;
        }

        IMessageTransferModule transfer = null;
        foreach (Scene sc in scenes)
        {
            transfer ??= sc.RequestModuleInterface<IMessageTransferModule>();
            if (transfer is not null) break;
        }
        if (transfer is null)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocTextTransport.Line(UUID.Zero, plan.Session.SessionId, AdhocTextTransport.DecisionNoTransfer));
            return;
        }

        IPresenceService presence = null;
        foreach (Scene sc in scenes)
        {
            presence = sc.PresenceService;
            if (presence is not null) break;
        }
        List<UUID> online = OnlineMembers(presence, candidates);

        foreach (UUID target in online)
        {
            try
            {
                // Root only: if the member is a child presence in this process the transfer module would hand the
                // carrier to that viewer as an instant message; it is meant for the process where they are root.
                transfer.SendInstantMessage(
                    AdhocTextTransport.Build(target, plan.Sender, fromName, plan.Session.SessionId,
                                             originRegion, plan.Message, null, plan.Session.RoomKey, m_carrierAuth, sessionKey),
                    _ => { }, true);
                m_log.LogDebug("{LogHeader} {Line}", logHeader,
                    AdhocTextTransport.Line(target, plan.Session.SessionId, AdhocTextTransport.DecisionSent));
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{LogHeader} cross-instance conference text to {Target} failed", logHeader, target);
            }
        }
    }

    /// <summary>
    /// Receive half: a conference line arrived from another region server. Rebuild the
    /// viewer's SessionSend locally so the two paths are indistinguishable at the client.
    ///
    /// THE MEMBERSHIP RE-CHECK IS NOT REDUNDANT. The sending instance already decided the sender is
    /// a member, but this instance holds its own adopted copy of the voice session, and a carrier is a
    /// grid IM anyone could synthesise. The carrier's MAC is verified first (CarrierIntake), and the
    /// re-check here means even a validly signed line cannot reach a conference its sender is not in.
    /// </summary>
    private void OnIncomingConferenceText(GridInstantMessage msg)
    {
        if (m_nonSpatial is null || !m_adhocVoiceEnabled)
            return;
        TextIntake text = CarrierIntake.AcceptText(msg, m_carrierAuth, m_nonSpatial);
        if (!text.Claimed)
            return;   // not ours -- a ring lands here and falls straight through
        if (text.Authenticated)
            LogCarrierAccepted(CarrierAuth.KindText, text.SessionId, NonSpatialSessionType.Adhoc, text.OriginRegion);
        if (text.Reason is not null)
        {
            LogCarrierRefusal(CarrierAuth.KindText, text.Reason, text.RefusalCount);
            return;
        }
        if (!text.Ok)
        {
            if (text.Decision == SessionOutcome.NoSuchSession || text.Decision == AdhocTextSession.DecisionRecipientNotInChat)
                m_log.LogDebug("{LogHeader} {Line}", logHeader,
                    AdhocTextTransport.Line(text.Target, text.SessionId, text.Decision));
            else
                WarnRefusal("conference-text-carrier-" + text.Decision, text.Sender, () => logHeader + " " +
                    AdhocTextTransport.Line(text.Target, text.SessionId, text.Decision,
                        text.Decision == AdhocTextSession.DecisionNotMember ? "from=" + text.Sender : null));
            return;
        }

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);
        DeliverConferenceTextLocally(scenes, text.Target, text.Sender, text.FromName, text.SessionId,
                                     text.OriginRegion, text.Message);
    }

    /// <summary>
    /// Push the participant list after a seat changed hands. Local-only by nature -- the
    /// event queue is per-instance -- and every recipient's outcome is logged, so an unreachable
    /// member is visible rather than silent.
    /// </summary>
    private void SendConferenceRoster(NonSpatialVoiceSession session, UUID who, bool joined)
    {
        if (session is null || session.Type != NonSpatialSessionType.Adhoc || !m_adhocVoiceEnabled)
            return;

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        List<string> lines = joined
            ? AdhocConferenceRoster.SendJoin(scenes, session, who)
            : AdhocConferenceRoster.SendLeave(scenes, session, who);
        foreach (string line in lines)
            m_log.LogDebug("{LogHeader} {Line}", logHeader, line);
    }

    /// <summary>
    /// Ring an ad-hoc conference's invitees -- local first, then anyone this process cannot
    /// reach, over the group ring carrier.
    ///
    /// SIMPLER THAN THE GROUP FAN-OUT IN ONE IMPORTANT WAY: there is no roster to consult. An ad-hoc
    /// conference's membership IS its invitation list, so the targets come from the voice session itself
    /// and the groups service is never touched. That is also why the target list and
    /// AdhocAdmission.CanJoin agree by construction -- both read the same records.
    ///
    /// <paramref name="only"/> narrows the ring to specific agents, which is what "invite" needs:
    /// adding one person to a running call must ring that person and nobody already in it.
    /// </summary>
    private void RingAdhocMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName,
                                  List<UUID> only)
    {
        if (session is null || m_nonSpatial is null || !m_adhocVoiceEnabled)
            return;

        List<UUID> targets = AdhocVoiceInvite.Targets(session, initiator, only);
        if (targets.Count == 0)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocVoiceInvite.Line(UUID.Zero, session.SessionId, "no-targets"));
            return;
        }

        string token = m_nonSpatial.IssueToken(session.SessionId, initiator);
        if (string.IsNullOrEmpty(token))
        {
            m_log.LogWarning("{LogHeader} conference ring for {SessionId} has no token; not ringing",
                logHeader, session.SessionId);
            return;
        }

        List<Scene> scenes;
        lock (m_scenes)
            scenes = new List<Scene>(m_scenes);

        OSDMap body = AdhocVoiceInvite.BuildBody(session, token, initiator, initiatorName);

        // (a) anyone with a ROOT presence in THIS process, exactly as a group ring reaches them. An invitee
        // who is only a child presence here is rung through the carrier like one on another process.
        HashSet<UUID> presentHere = new HashSet<UUID>(GroupVoiceInvite.RootAgents(scenes));
        List<UUID> local = new List<UUID>();
        List<UUID> remote = new List<UUID>();
        foreach (UUID t in targets)
            (presentHere.Contains(t) ? local : remote).Add(t);

        foreach (UUID target in local)
        {
            string decision = A2AInviteDelivery.Deliver(scenes, target, body, null, out string region);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocVoiceInvite.Line(target, session.SessionId, decision, "region=\"" + region + "\""));
        }

        // (b) everyone else: another region server, or offline. The presence service decides which,
        // and an offline invitee is simply not rung -- a conference ring is worthless once stored and
        // replayed at the next login, announcing a call that ended hours ago.
        RingRemoteAdhocMembers(session, initiator, initiatorName, body, scenes, remote);

        // Marked whatever delivery said, local and remote alike: a member we could not reach must not
        // be re-rung by every later join. One ring per member per cycle.
        m_nonSpatial.MarkInvited(session.SessionId, local);
    }

    /// <summary>
    /// Carry a conference ring to invitees on another region server, over exactly the transport
    /// used for groups. Nothing in GroupVoiceRingTransport is group-specific -- it is a
    /// module-to-module carrier -- so the only thing that distinguishes the two on receipt is the
    /// invitation_type already inside the body.
    /// </summary>
    private void RingRemoteAdhocMembers(NonSpatialVoiceSession session, UUID initiator, string initiatorName,
                                        OSDMap body, List<Scene> scenes, List<UUID> candidates)
    {
        if (candidates is null || candidates.Count == 0 || body is null)
            return;
        if (!m_carrierAuth.Enabled && !RingsAreVerifiedByTheStore)
        {
            LogCarrierOff(session.SessionId);
            return;
        }

        IMessageTransferModule transfer = null;
        Scene originScene = null;
        foreach (Scene sc in scenes)
        {
            transfer ??= sc.RequestModuleInterface<IMessageTransferModule>();
            if (sc.GetScenePresence(initiator) is not null) originScene = sc;
        }
        if (transfer is null)
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                GroupVoiceRingTransport.Line(UUID.Zero, session.SessionId, GroupVoiceRingTransport.DecisionNoTransfer));
            return;
        }

        IPresenceService presence = null;
        foreach (Scene sc in scenes)
        {
            presence = sc.PresenceService;
            if (presence is not null) break;
        }
        int before = candidates.Count;
        List<UUID> online = OnlineMembers(presence, candidates);
        if (online.Count != before)
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                AdhocVoiceInvite.Line(UUID.Zero, session.SessionId, GroupVoiceInvite.DecisionOffline,
                    $"{before - online.Count} of {before} candidate(s) are offline"));
        if (online.Count == 0)
            return;

        UUID originRegion = originScene?.RegionInfo?.RegionID ?? UUID.Zero;
        // Marked before the rings go out, so a store that verifies rings finds the target marked (see the group ring).
        m_nonSpatial?.MarkInvited(session.SessionId, online);
        foreach (UUID target in online)
        {
            try
            {
                GridInstantMessage im = GroupVoiceRingTransport.Build(target, initiator, initiatorName,
                                                                      session.SessionId, originRegion, body, m_carrierAuth);
                // Root only: if the member is a child presence in this process the transfer module would hand the
                // carrier to that viewer as an instant message; it is meant for the process where they are root.
                transfer.SendInstantMessage(im, _ => { }, true);
                m_log.LogDebug("{LogHeader} {Line}", logHeader,
                    GroupVoiceRingTransport.Line(target, session.SessionId, GroupVoiceRingTransport.DecisionSent));
            }
            catch (Exception e)
            {
                m_log.LogWarning(e, "{LogHeader} cross-instance conference ring to {Target} failed", logHeader, target);
            }
        }
    }

    private void SubscribeConferenceEvents(Scene scene)
    {
        // Conference TEXT from another region server arrives the same way a ring does. The
        // two are told apart by the bucket magic, never by dialog -- AdhocTextTransport's header
        // has the whole argument. Order of these two subscriptions is irrelevant: the magics are
        // distinct prefixes, so at most one of the two parsers can claim any given message.
        scene.EventManager.OnIncomingInstantMessage += OnIncomingConferenceText;
        // A member's OWN typed line is a CLIENT IM, not a grid IM, so it needs the client
        // hook -- the same one GroupsMessagingModule uses for group chat (:427-436). Root only:
        // a child agent's client would deliver the line a second time on every border crossing.
        scene.EventManager.OnMakeRootAgent += OnMakeRootAgentConferenceText;
        scene.EventManager.OnMakeChildAgent += OnMakeChildAgentConferenceText;
    }

    // The conference "call", "accept invitation", "invite" and "decline invitation" arms. Returns true when one of them answered.
    private bool TryHandleAdhocChatSession(OSDMap reqmap, UUID agentID, ScenePresence sp, Scene scene, IOSHttpResponse response)
    {
        if (AdhocVoiceChatSession.TryHandleCall(reqmap, agentID, m_nonSpatial, scene.RegionInfo.RegionID,
                                                m_adhocVoiceEnabled, out ChatSessionOutcome adhocCall))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, adhocCall.Instrument);
            ApplyChatSessionOutcome(adhocCall, response);
            if (adhocCall.StartedRinging)
                RingAdhocMembers(m_nonSpatial.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, null);
            // This arm seats the caller, so the roster moved. AFTER the response is
            // applied, for the same reason the ring is: a roster send must never cost the caller
            // the answer it is blocked on.
            if (adhocCall.Status == HttpStatusCode.OK)
                SendConferenceRoster(m_nonSpatial.Get(GroupSessionIdOf(reqmap)), agentID, joined: true);
            return true;
        }

        if (AdhocVoiceChatSession.TryHandleAcceptInvitation(reqmap, agentID, m_nonSpatial, scene.RegionInfo.RegionID,
                                                            m_adhocVoiceEnabled, out ChatSessionOutcome adhocAccept))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, adhocAccept.Instrument);
            ApplyChatSessionOutcome(adhocAccept, response);
            if (adhocAccept.StartedRinging)
                RingAdhocMembers(m_nonSpatial.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, null);
            // Accepting IS the join (llimview.cpp:3382-3385), so this is the roster's
            // main event -- the invitee's panel gets the whole room and the room gets the invitee.
            if (adhocAccept.Status == HttpStatusCode.OK)
                SendConferenceRoster(m_nonSpatial.Get(GroupSessionIdOf(reqmap)), agentID, joined: true);
            return true;
        }

        // "invite" adds people to a conference already running. Early return like the group
        // arms, because the ring list is the arm's own output and there is nothing for the shared
        // tail to do with it.
        if (AdhocVoiceChatSession.TryHandleInvite(reqmap, agentID, m_nonSpatial, m_adhocVoiceEnabled,
                                                  out ChatSessionOutcome inviteOutcome, out List<UUID> addedToCall))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, inviteOutcome.Instrument);
            ApplyChatSessionOutcome(inviteOutcome, response);
            if (addedToCall.Count > 0)
                RingAdhocMembers(m_nonSpatial.Get(GroupSessionIdOf(reqmap)), agentID, sp.Name, addedToCall);
            return true;
        }

        // "decline invitation" for a conference. No ring, no seat, nothing to fan out.
        if (AdhocVoiceChatSession.TryHandleDeclineInvitation(reqmap, agentID, m_nonSpatial, m_adhocVoiceEnabled,
                                                             out ChatSessionOutcome declineOutcome))
        {
            m_log.LogDebug("{LogHeader} {Line}", logHeader, declineOutcome.Instrument);
            ApplyChatSessionOutcome(declineOutcome, response);
            return true;
        }
        return false;
    }
}
