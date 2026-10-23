/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;   // IPresenceService, for the online-only filter

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using Microsoft.Extensions.Logging;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice;

// Avatar-to-avatar calls between region-server processes: the module's side of A2ACarrierTransport.
//
// The caller's process rings a callee it cannot reach itself through a signed carrier; the callee's
// process adopts the call and delivers the invitation; the callee's join, decline and leave are carried
// back the same way, so the caller's viewer gets the agent-list updates it needs (see the transport's
// header for what the stock viewer reads). Under the per-process session store there is no shared record,
// so the call's token travels in the ring and each process keeps its own copy of the pair's record; without a
// [WebRtcVoice] CarrierSecret none of this is sent and every carrier is refused: a callee who is not here is not
// rung, exactly as before. Under SessionStoreMode grid the record is shared: the receiver asks the grid session
// service whether the ring is genuine and whether the join, decline or leave is true, so no secret is needed and
// no token travels.
public partial class WebRtcVoiceRegionModule
{
    private List<Scene> SceneSnapshot()
    {
        lock (m_scenes)
            return new List<Scene>(m_scenes);
    }

    // Whether the presence service reports the agent online somewhere in the grid. An offline agent cannot answer
    // a carrier, and a ring delivered at their next login would announce a call that ended long ago.
    private static bool IsOnlineInGrid(IEnumerable<Scene> scenes, UUID agent)
    {
        IPresenceService presence = null;
        foreach (Scene sc in scenes)
        {
            presence = sc.PresenceService;
            if (presence is not null) break;
        }
        if (presence is null)
            return false;
        try
        {
            OpenSim.Services.Interfaces.PresenceInfo[] found = presence.GetAgents(new[] { agent.ToString() });
            return found is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    // Sends one carrier to the process that holds the target's root presence. Returns the decision word for the
    // instrument line. onResult runs with the grid's answer when the message was handed on.
    private string SendA2ACarrier(string step, UUID target, UUID about, string aboutName, A2ASession session,
                                  UUID originRegion, Action<bool> onResult = null)
    {
        if (!m_carrierAuth.Enabled && !RingsAreVerifiedByTheStore)
        {
            LogCarrierOff(session.SessionId);
            return A2ACarrierTransport.DecisionCarrierOff;
        }

        List<Scene> scenes = SceneSnapshot();
        IMessageTransferModule transfer = null;
        foreach (Scene sc in scenes)
        {
            transfer ??= sc.RequestModuleInterface<IMessageTransferModule>();
            if (transfer is not null) break;
        }
        if (transfer is null)
            return A2ACarrierTransport.DecisionNoTransfer;
        if (!IsOnlineInGrid(scenes, target))
            return A2ACarrierTransport.DecisionOffline;

        try
        {
            // The token travels only where the receiver has no store to ask: it is what lets a process adopt the call.
            GridInstantMessage im = A2ACarrierTransport.Build(step, target, about, aboutName, session.SessionId,
                                                              session.RoomKey, RingsAreVerifiedByTheStore ? string.Empty : session.Token,
                                                              originRegion, m_carrierAuth);
            // Root only: a child presence of the target in this process must not receive the carrier as an
            // instant message; it is meant for the process where the target is root.
            transfer.SendInstantMessage(im, ok => onResult?.Invoke(ok), true);
            return A2ACarrierTransport.DecisionSent;
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "{LogHeader} cross-instance {Step} to {Target} failed", logHeader, step, target);
            return A2ACarrierTransport.DecisionNoTransfer;
        }
    }

    // The callee is not rung in this process (no root presence here). Rings it in its own process through the
    // carrier when that is possible; otherwise leaves the local decision as it was.
    private string RingA2ACalleeAcrossProcesses(ChatSessionOutcome.Invitation invite, string callerName, Scene origin,
                                                string localDecision)
    {
        if (localDecision != A2AInviteDelivery.DecisionNoPresence && localDecision != A2AInviteDelivery.DecisionChildOnly)
            return localDecision;
        A2ASession session = A2A.TryGet(invite.SessionId, invite.Caller);
        if (session is null || string.IsNullOrEmpty(session.Token))
            return localDecision;

        string decision = SendA2ACarrier(A2ACarrierTransport.StepRing, invite.Callee, invite.Caller, callerName, session,
                                         origin.RegionInfo.RegionID,
                                         ok => { if (ok) A2A.MarkInviteSent(invite.SessionId); });
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2ACarrierTransport.Line(A2ACarrierTransport.StepRing, invite.Callee, invite.Caller, invite.SessionId, decision));
        return decision == A2ACarrierTransport.DecisionSent ? decision : localDecision;
    }

    // Tells one party of a call about the other: here when the party has a root presence here, through the carrier
    // when the party is root in another process, else as it always was (a child presence here, or nobody).
    private void NotifyA2AParty(A2ASession session, UUID recipient, UUID about, string step,
                                List<GroupChatListAgentUpdateData> updates, string transition, UUID originRegion)
    {
        List<Scene> scenes = SceneSnapshot();
        Scene root = A2AInviteDelivery.ResolveCalleeScene(scenes, recipient, out bool isChild);
        if (root is null || isChild)
        {
            string sent = SendA2ACarrier(step, recipient, about, string.Empty, session, originRegion);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                A2ACarrierTransport.Line(step, recipient, about, session.SessionId, sent));
            if (sent == A2ACarrierTransport.DecisionSent)
                return;
        }
        string decision = A2AAgentListDelivery.Deliver(scenes, recipient, session.SessionId, updates, null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(recipient, session.SessionId, transition, about, decision));
    }

    // The callee's admitted provision made the call Active: both viewers' panels get the other's ENTER.
    private void SendA2AActivePair(A2ASession s, Scene scene)
    {
        UUID origin = scene?.RegionInfo?.RegionID ?? UUID.Zero;
        NotifyA2AParty(s, s.Caller, s.Callee, A2ACarrierTransport.StepJoin,
                       A2AAgentListDelivery.EnterUpdates(s.Callee, s.Caller), A2AAgentListDelivery.TransitionEnter, origin);
        // The callee provisioned here; its own panel is told here, never carried.
        string d = A2AAgentListDelivery.Deliver(SceneSnapshot(), s.Callee, s.SessionId,
                                                A2AAgentListDelivery.EnterUpdates(s.Caller, s.Callee), null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(s.Callee, s.SessionId, A2AAgentListDelivery.TransitionEnter, s.Caller, d));
    }

    // An Active call lost a party: the other party's panel gets the LEAVE, here or in the process where it is root.
    private void SendA2ALeave(A2ASession gone, UUID departed, Scene scene)
    {
        UUID remaining = gone.OtherParty(departed);
        if (remaining == UUID.Zero)
            return;
        NotifyA2AParty(gone, remaining, departed, A2ACarrierTransport.StepLeave,
                       A2AAgentListDelivery.LeaveUpdates(departed), A2AAgentListDelivery.TransitionLeave,
                       scene?.RegionInfo?.RegionID ?? UUID.Zero);
    }

    // A party left a call that stays up for the other, who is in another process: that process needs to know, so it
    // can tell the other's viewer and clear its record when the other leaves too. A call that ended is announced by
    // SendA2ALeave; a call whose other party is root here is left as it always was.
    private void SendA2ALeaveAcrossProcesses(IReadOnlyList<NonSpatialVoiceSession> touched, UUID agent, Scene scene)
    {
        if (touched is null || touched.Count == 0)
            return;
        List<Scene> scenes = SceneSnapshot();
        foreach (NonSpatialVoiceSession raw in touched)
        {
            if (raw.Type != NonSpatialSessionType.P2P || raw.IsDead)
                continue;
            A2ASession s = new A2ASession(raw);
            if (s.State != A2ASessionState.Active || !s.IsParty(agent))
                continue;   // the call was never answered: the other side has nothing to hear
            UUID other = s.OtherParty(agent);
            Scene root = A2AInviteDelivery.ResolveCalleeScene(scenes, other, out bool isChild);
            if (root is not null && !isChild)
                continue;
            string sent = SendA2ACarrier(A2ACarrierTransport.StepLeave, other, agent, string.Empty, s,
                                         scene?.RegionInfo?.RegionID ?? UUID.Zero);
            m_log.LogDebug("{LogHeader} {Line}", logHeader,
                A2ACarrierTransport.Line(A2ACarrierTransport.StepLeave, other, agent, s.SessionId, sent));
        }
    }

    // The callee declined: the caller's viewer is told, so the call ends on its screen instead of ringing out.
    private void SendA2ADeclined(A2ASession declined, Scene scene)
    {
        NotifyA2AParty(declined, declined.Caller, declined.Callee, A2ACarrierTransport.StepDecline,
                       A2AAgentListDelivery.DeclinedUpdates(declined.Callee), "DECLINED",
                       scene?.RegionInfo?.RegionID ?? UUID.Zero);
    }

    private static bool TokenMatches(string held, string presented)
    {
        if (string.IsNullOrEmpty(held) || string.IsNullOrEmpty(presented))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(held), Encoding.UTF8.GetBytes(presented));
    }

    private void LogA2ACarrier(in A2ACarrier c, string decision, string detail = null)
    {
        string line = A2ACarrierTransport.Line(c.Step, c.Target, c.About, c.SessionId, decision, detail);
        // A step for a call this process no longer holds is what a hang-up looks like from the far side.
        if (decision == A2ACarrierTransport.DecisionSent || decision == A2ACarrierTransport.DecisionNoRecord)
            m_log.LogDebug("{LogHeader} {Line}", logHeader, line);
        else
            WarnRefusal("a2a-carrier-" + c.Step + "-" + decision, UUID.Zero, () => logHeader + " " + line);
    }

    // A carrier arrived from another region server. It is acted on only when it is authentic, fresh and not seen
    // before, it names the pair its session id belongs to, and the call it speaks of is the one this process holds.
    private void OnIncomingA2ACarrier(GridInstantMessage msg)
    {
        if (!A2ACarrierTransport.TryParse(msg, out A2ACarrier c))
            return;   // not ours: every other subscriber still sees it untouched
        if (!m_a2aVoiceEnabled)
            return;   // avatar-to-avatar calls are switched off on this instance

        string kind = A2ACarrierTransport.KindOf(c.Step);
        // With the grid session service the carrier needs no secret: what it claims is checked against the service
        // below. A region that has a secret still requires the MAC.
        string reason = RingsAreVerifiedByTheStore && !m_carrierAuth.Enabled
            ? null
            : m_carrierAuth.Verify(A2ACarrierTransport.Fields(c), c.Auth);
        if (reason is not null)
        {
            LogCarrierRefusal(kind, reason, m_carrierAuth.RecordRefusal(reason));
            return;
        }
        LogCarrierAccepted(kind, c.SessionId, NonSpatialSessionType.P2P, c.OriginRegion);

        if (!A2ACarrierTransport.SessionIsThePair(c))
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionBadPair);
            return;
        }
        if (string.IsNullOrEmpty(c.Room))
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionBadRoom);
            return;
        }

        switch (c.Step)
        {
            case A2ACarrierTransport.StepRing: AcceptA2ARing(c); break;
            case A2ACarrierTransport.StepJoin: AcceptA2AJoin(c); break;
            case A2ACarrierTransport.StepDecline: AcceptA2ADecline(c); break;
            case A2ACarrierTransport.StepLeave: AcceptA2ALeave(c); break;
        }
    }

    private void AcceptA2ARing(in A2ACarrier c)
    {
        List<Scene> scenes = SceneSnapshot();
        // Only the callee's root process rings. Adopting the call where the callee is just a child presence would
        // record a call nobody there can answer.
        Scene scene = A2AInviteDelivery.ResolveCalleeScene(scenes, c.Target, out bool isChild);
        if (scene is null || isChild)
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionNotRootHere);
            return;
        }

        A2ASession s;
        if (RingsAreVerifiedByTheStore)
        {
            // Nothing is adopted from the wire: the service says whether the call exists and the target is its callee,
            // and the room and credential the viewer is shown are the service's, not the carrier's.
            SessionOutcome verified = m_nonSpatial.VerifyRing(c.SessionId, c.Target, c.About, NonSpatialSessionType.P2P);
            if (!verified.Ok)
            {
                LogA2ACarrier(c, verified.Decision == "refused-already-active" ? A2ACarrierTransport.DecisionAlreadyActive
                                                                               : A2ACarrierTransport.DecisionNoRecord,
                              "reason=" + verified.Decision);
                return;
            }
            if (verified.Session is null || string.IsNullOrEmpty(verified.Session.Token)
                || !string.Equals(c.Room, verified.Session.RoomKey, StringComparison.Ordinal))
            {
                LogA2ACarrier(c, A2ACarrierTransport.DecisionBadRoom);
                return;
            }
            s = new A2ASession(verified.Session);
        }
        else
        {
            s = A2A.AdoptRemote(c.About, c.Target, c.Room, c.Token, out string refusal);
            if (s is null)
            {
                LogA2ACarrier(c, refusal == "already-active" ? A2ACarrierTransport.DecisionAlreadyActive
                                  : refusal == "call-in-progress" ? A2ACarrierTransport.DecisionGlare
                                  : refusal == "refused-room-key-mismatch" ? A2ACarrierTransport.DecisionBadRoom
                                  : A2ACarrierTransport.DecisionNoRecord, "reason=" + refusal);
                return;
            }
        }

        // The invitation is rebuilt here from the signed fields, as a call placed here would have built it.
        string decision = A2AInviteDelivery.Deliver(scenes, c.Target, A2AInvitation.BuildBody(s, c.About, c.AboutName),
                                                    null, out string region);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AInviteDelivery.Line(c.Target, c.About, c.SessionId, region, decision));
    }

    // The far party's step concerns a call this process can see: its own copy of the record, or the service's.
    // The call must name both agents, and the carrier must name the room the call derives to. Without the service
    // the step must also carry the call's credential when needsToken (a step sent from a process that has already
    // ended the call has none to carry).
    private NonSpatialVoiceSession HeldCallFor(in A2ACarrier c, bool needsToken)
    {
        NonSpatialVoiceSession s = m_nonSpatial?.Get(c.SessionId, c.Target);
        if (s is null || s.Type != NonSpatialSessionType.P2P || !IsPartyOfCall(s, c.Target) || !IsPartyOfCall(s, c.About)
            || (!RingsAreVerifiedByTheStore && s.IsDead))
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionNoRecord);
            return null;
        }
        if (!string.Equals(c.Room, s.RoomKey, StringComparison.Ordinal))
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionBadRoom);
            return null;
        }
        if (!RingsAreVerifiedByTheStore && (needsToken || !string.IsNullOrEmpty(c.Token)) && !TokenMatches(s.Token, c.Token))
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionBadToken);
            return null;
        }
        return s;
    }

    private static bool IsPartyOfCall(NonSpatialVoiceSession s, UUID agent)
        => agent != UUID.Zero && (agent == s.Creator || agent == s.Callee?.AgentId);

    private void AcceptA2AJoin(in A2ACarrier c)
    {
        NonSpatialVoiceSession held = HeldCallFor(c, needsToken: true);
        if (held is null)
            return;
        if (RingsAreVerifiedByTheStore)
        {
            // The callee joined in its own process and wrote that to the service; nothing changes here.
            if (!NonSpatialVoiceSession.IsJoined(held.Find(c.About)))
            {
                LogA2ACarrier(c, A2ACarrierTransport.DecisionNotTrue, "reason=not-joined");
                return;
            }
        }
        else if (A2A.MarkRemoteJoined(c.SessionId, c.About, c.OriginRegion) is null)
            return;
        string decision = A2AAgentListDelivery.Deliver(SceneSnapshot(), c.Target, c.SessionId,
                                                       A2AAgentListDelivery.EnterUpdates(c.About, c.Target), null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(c.Target, c.SessionId, A2AAgentListDelivery.TransitionEnter, c.About, decision));
    }

    private void AcceptA2ADecline(in A2ACarrier c)
    {
        NonSpatialVoiceSession held = HeldCallFor(c, needsToken: false);
        if (held is null)
            return;
        // Only the callee can decline, and only a call whose callee had not answered.
        if (c.About != held.Callee?.AgentId || c.Target != held.Creator)
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionNoRecord, "reason=not-an-unanswered-call-for-this-callee");
            return;
        }
        if (RingsAreVerifiedByTheStore)
        {
            // The callee declined in its own process and the service ended the call; nothing changes here.
            if (held.Find(c.About)?.State != MemberState.Declined)
            {
                LogA2ACarrier(c, A2ACarrierTransport.DecisionNotTrue, "reason=not-declined");
                return;
            }
        }
        else
        {
            A2ASession before = A2A.TryGet(c.SessionId, c.Target);
            if (before is null || before.State != A2ASessionState.Invited || !A2A.Decline(c.SessionId, c.About, out _))
            {
                LogA2ACarrier(c, A2ACarrierTransport.DecisionNoRecord, "reason=not-an-unanswered-call-for-this-callee");
                return;
            }
        }
        string decision = A2AAgentListDelivery.Deliver(SceneSnapshot(), c.Target, c.SessionId,
                                                       A2AAgentListDelivery.DeclinedUpdates(c.About), null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(c.Target, c.SessionId, "DECLINED", c.About, decision));
    }

    private void AcceptA2ALeave(in A2ACarrier c)
    {
        NonSpatialVoiceSession held = HeldCallFor(c, needsToken: false);
        if (held is null)
            return;
        if (RingsAreVerifiedByTheStore)
        {
            // The party left in its own process and the service recorded it; nothing changes here.
            NonSpatialMember gone = held.Find(c.About);
            if (gone is null || gone.HoldsSeat)
            {
                LogA2ACarrier(c, A2ACarrierTransport.DecisionNotTrue, "reason=still-seated");
                return;
            }
        }
        else
            A2A.MarkRemoteGone(c.SessionId, c.About, out _);
        string decision = A2AAgentListDelivery.Deliver(SceneSnapshot(), c.Target, c.SessionId,
                                                       A2AAgentListDelivery.LeaveUpdates(c.About), null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(c.Target, c.SessionId, A2AAgentListDelivery.TransitionLeave, c.About, decision));
    }
}
