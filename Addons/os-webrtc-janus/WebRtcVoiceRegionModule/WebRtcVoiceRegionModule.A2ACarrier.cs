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
// so the call's token travels in the ring and each process keeps its own copy of the pair's record.
// Without a [WebRtcVoice] CarrierSecret none of this is sent and every carrier is refused: a callee who is
// not here is not rung, exactly as before.
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
        if (!m_carrierAuth.Enabled)
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
            GridInstantMessage im = A2ACarrierTransport.Build(step, target, about, aboutName, session.SessionId,
                                                              session.Token, originRegion, m_carrierAuth);
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
        A2ASession session = m_a2aSessions.TryGet(invite.SessionId);
        if (session is null || string.IsNullOrEmpty(session.Token))
            return localDecision;

        string decision = SendA2ACarrier(A2ACarrierTransport.StepRing, invite.Callee, invite.Caller, callerName, session,
                                         origin.RegionInfo.RegionID,
                                         ok => { if (ok) m_a2aSessions.MarkInviteSent(invite.SessionId); });
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
    // can tell the other's viewer and clear its record when the other leaves too. A call whose other party is root
    // here is left as it always was (the record's removal sends the LEAVE).
    private void SendA2ALeaveAcrossProcesses(List<A2ASession> departed, UUID agent, Scene scene)
    {
        if (departed.Count == 0)
            return;
        List<Scene> scenes = SceneSnapshot();
        foreach (A2ASession s in departed)
        {
            if (m_a2aSessions.TryGet(s.SessionId) is null)
                continue;   // removed: SendA2ALeave has told the other party
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

    private static bool TokenMatches(A2ASession s, string presented)
    {
        if (s is null || string.IsNullOrEmpty(s.Token) || string.IsNullOrEmpty(presented))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s.Token), Encoding.UTF8.GetBytes(presented));
    }

    private void LogA2ACarrier(in A2ACarrier c, string decision, string detail = null)
    {
        string line = A2ACarrierTransport.Line(c.Step, c.Target, c.About, c.SessionId, decision, detail);
        // A step for a call this process no longer holds is what a hang-up looks like from the far side.
        if (decision == A2ACarrierTransport.DecisionSent || decision == A2ACarrierTransport.DecisionNoRecord)
            m_log.LogDebug("{LogHeader} {Line}", logHeader, line);
        else
            m_log.LogWarning("{LogHeader} {Line}", logHeader, line);
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
        string reason = m_carrierAuth.Verify(A2ACarrierTransport.Fields(c), c.Auth);
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
        if (!string.Equals(c.Room, c.SessionId.ToString(), StringComparison.Ordinal))
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

        A2ASession s = m_a2aSessions.AdoptRemote(c.About, c.Target, c.Token, out string refusal);
        if (s is null)
        {
            LogA2ACarrier(c, refusal == "already-active" ? A2ACarrierTransport.DecisionAlreadyActive
                                                         : A2ACarrierTransport.DecisionGlare, "reason=" + refusal);
            return;
        }

        // The invitation is rebuilt here from the signed fields, as a call placed here would have built it.
        string decision = A2AInviteDelivery.Deliver(scenes, c.Target, A2AInvitation.BuildBody(s, c.About, c.AboutName),
                                                    null, out string region);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AInviteDelivery.Line(c.Target, c.About, c.SessionId, region, decision));
        if (decision != A2AInviteDelivery.DecisionSent)
            m_a2aSessions.Remove(c.SessionId);   // nobody was rung, so nothing here is waiting for an answer
    }

    // The far party's step concerns a call this process must already hold, under the same token.
    private A2ASession HeldCallFor(in A2ACarrier c)
    {
        A2ASession s = m_a2aSessions.TryGet(c.SessionId);
        if (s is null || !s.IsParty(c.Target) || !s.IsParty(c.About))
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionNoRecord);
            return null;
        }
        if (!TokenMatches(s, c.Token))
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionBadToken);
            return null;
        }
        return s;
    }

    private void AcceptA2AJoin(in A2ACarrier c)
    {
        A2ASession held = HeldCallFor(c);
        if (held is null)
            return;
        A2ASession s = m_a2aSessions.MarkRemoteJoined(c.SessionId, c.About);
        if (s is null)
            return;
        string decision = A2AAgentListDelivery.Deliver(SceneSnapshot(), c.Target, s.SessionId,
                                                       A2AAgentListDelivery.EnterUpdates(c.About, c.Target), null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(c.Target, s.SessionId, A2AAgentListDelivery.TransitionEnter, c.About, decision));
    }

    private void AcceptA2ADecline(in A2ACarrier c)
    {
        A2ASession held = HeldCallFor(c);
        if (held is null)
            return;
        // Only the callee can decline a call that has not been answered.
        if (c.About != held.Callee || held.State != A2ASessionState.Invited)
        {
            LogA2ACarrier(c, A2ACarrierTransport.DecisionNoRecord, "reason=not-an-unanswered-call-for-this-callee");
            return;
        }
        m_a2aSessions.Remove(c.SessionId);
        string decision = A2AAgentListDelivery.Deliver(SceneSnapshot(), c.Target, held.SessionId,
                                                       A2AAgentListDelivery.DeclinedUpdates(c.About), null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(c.Target, held.SessionId, "DECLINED", c.About, decision));
    }

    private void AcceptA2ALeave(in A2ACarrier c)
    {
        A2ASession held = HeldCallFor(c);
        if (held is null)
            return;
        m_a2aSessions.MarkRemoteGone(c.SessionId, c.About, out _);
        string decision = A2AAgentListDelivery.Deliver(SceneSnapshot(), c.Target, held.SessionId,
                                                       A2AAgentListDelivery.LeaveUpdates(c.About), null);
        m_log.LogDebug("{LogHeader} {Line}", logHeader,
            A2AAgentListDelivery.Line(c.Target, held.SessionId, A2AAgentListDelivery.TransitionLeave, c.About, decision));
    }
}
