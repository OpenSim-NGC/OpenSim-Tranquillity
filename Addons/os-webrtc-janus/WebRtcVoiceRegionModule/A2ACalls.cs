/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Avatar-to-avatar (person-to-person) calls, kept in the shared call session store.
 *
 * A call between two people is a two-seat session of the non-spatial engine (NonSpatialSessionType.P2P),
 * so it lives wherever the engine's sessions live: in this process, or in the grid session service when
 * [WebRtcVoice] SessionStoreMode is grid. This class gives the avatar-to-avatar code the operations it needs
 * (record a call, issue its credential, find it by channel, mark a party joined, end it) and returns copies
 * (A2ASession) for it to read.
 *
 * The session id is the viewer's XOR of the two agent ids (llimview.cpp:2530-2570 in the wire trace); the sim
 * re-derives it and must find it equal. A call is recorded at ChatSessionRequest "start p2p voice" (caller =
 * the requesting agent, callee = `params`), its credential is minted at the caller's "call", and that credential
 * is what the viewer sends back as provision `credentials`. The channel the viewer is given is the session id as
 * text.
 *
 * This class makes NO authorization decision itself. A2AProvisionAdmission asks it: does the provision's
 * `channel` name a live call, is the requesting agent one of its two parties, and does `credentials` equal the
 * credential.
 *
 * What a call is, in the engine's terms (the engine holds the rules; see "person-to-person calls" there):
 *   caller      the session's creator; seated when the call is recorded, joined once its provision is answered
 *   callee      invited when the call is recorded; the call is "active" once the callee has answered
 *   joined      a party that holds a seat tagged with the voice service's viewer session
 *   rung        the callee is recorded as invited once the invitation was delivered, never on a failed delivery
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice
{
    public enum A2ASessionState
    {
        /// <summary>Recorded at "start p2p voice"; the callee has not yet accepted (provisioned).</summary>
        Invited = 0,
        /// <summary>The callee has accepted: its provision was admitted and answered.</summary>
        Active = 1,
    }

    /// <summary>One call as the store held it when it was read. Changing it changes nothing in the store.</summary>
    public sealed class A2ASession
    {
        public UUID SessionId { get; }
        public UUID Caller { get; }
        public UUID Callee { get; }
        public DateTime CreatedUtc { get; }

        /// <summary>The per-session secret the viewer echoes as provision <c>credentials</c>. Null until "call".</summary>
        public string Token { get; }
        public A2ASessionState State { get; }
        /// <summary>Last activity (record, call, provision, logout); the invitation's lifetime is measured from here.</summary>
        public DateTime LastSeenUtc { get; }

        /// <summary>Whether each party currently holds an admitted multiagent provision.</summary>
        public bool CallerProvisioned { get; }
        public bool CalleeProvisioned { get; }

        /// <summary>
        /// The invitation has been DELIVERED to the callee once for this call: recorded after a confirmed
        /// enqueue, never on a failed delivery (so a caller retry can still reach a callee who was momentarily
        /// unreachable). It lasts as long as the call: one ring per invitation. This closes the Invited-state
        /// window of the invitation feedback loop in which each viewer answered a received ChatterBoxInvitation
        /// with its own bare "call", which re-invited the other side, about every 90 ms, without bound.
        /// </summary>
        public bool InviteSent { get; }

        /// <summary>The voice-service viewer_session ids each party's admitted provision was answered with; null when none.</summary>
        public string CallerViewerSession { get; }
        public string CalleeViewerSession { get; }

        /// <summary>The value the viewer sends as <c>channel</c> on a multiagent provision: the session id as a string.</summary>
        public string ChannelUri => SessionId.ToString();

        /// <summary>The mixer room key the call is derived to; a carrier names it so the receiver can check it derives the same.</summary>
        public string RoomKey { get; }

        public bool IsProvisioned(UUID agent) => agent == Caller ? CallerProvisioned : agent == Callee && CalleeProvisioned;

        public bool IsParty(UUID agent) => agent == Caller || agent == Callee;

        public UUID OtherParty(UUID agent) => agent == Caller ? Callee : agent == Callee ? Caller : UUID.Zero;

        internal A2ASession(NonSpatialVoiceSession s)
        {
            SessionId = s.SessionId;
            Caller = s.Creator;
            NonSpatialMember callee = s.Callee;
            Callee = callee?.AgentId ?? UUID.Zero;
            CreatedUtc = s.CreatedUtc;
            LastSeenUtc = s.LastSeenUtc;
            Token = s.Token;
            RoomKey = s.RoomKey;
            State = s.CalleeAnswered ? A2ASessionState.Active : A2ASessionState.Invited;
            NonSpatialMember caller = s.Find(s.Creator);
            CallerProvisioned = NonSpatialVoiceSession.IsJoined(caller);
            CalleeProvisioned = NonSpatialVoiceSession.IsJoined(callee);
            CallerViewerSession = CallerProvisioned ? caller.ViewerSession : null;
            CalleeViewerSession = CalleeProvisioned ? callee.ViewerSession : null;
            InviteSent = callee != null && s.WasInvited(callee.AgentId);
        }
    }

    public sealed class A2ACalls
    {
        private readonly Func<INonSpatialSessions> _sessions;

        /// <param name="sessions">
        /// The store the calls live in, asked for on every use so it can be set up after this object is made.
        /// When it gives null there is no store: nothing is recorded and nothing is found.
        /// </param>
        public A2ACalls(Func<INonSpatialSessions> sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        /// <summary>The viewer's P2P session id: XOR of the two agent ids (symmetric, so both parties derive it).</summary>
        public static UUID ComputeSessionId(UUID a, UUID b)
            => NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, a, b);

        /// <summary>
        /// should a client close mark the agent gone from its calls? Only a ROOT presence closing is the party
        /// leaving. A child agent closing (border crossing / draw distance) is not -- the agent's voice lives in
        /// its root region -- and no presence at all marks nothing. The same guard
        /// WebRtcVoiceServiceModule.Event_OnClientClosed applies.
        /// </summary>
        public static bool ShouldMarkGone(bool presenceFound, bool isChildAgent)
        {
            return presenceFound && !isChildAgent;
        }

        // ---- lifecycle ----------------------------------------------------------------------

        /// <summary>
        /// Record (or refresh) the call from <paramref name="caller"/> to <paramref name="callee"/>, with the
        /// caller seated from <paramref name="originRegion"/>. Idempotent for the same pair: a repeated "start p2p
        /// voice" refreshes the call and keeps its credential, so a caller that retries does not invalidate
        /// credentials already handed out. A start from the OTHER direction (the callee calling back) is the same
        /// call and is likewise refreshed, not replaced. Null when the store did not take it.
        /// </summary>
        public A2ASession Record(UUID caller, UUID callee, UUID originRegion, out bool created)
        {
            created = false;
            INonSpatialSessions sessions = _sessions();
            if (sessions == null)
                return null;
            UUID id = ComputeSessionId(caller, callee);
            SessionOutcome o = sessions.Start(NonSpatialSessionType.P2P, caller, callee, id, originRegion);
            if (!o.Ok)
                return null;
            A2ASession s = Live(o.Session);
            created = s != null && o.Created;
            return s;
        }

        /// <summary>
        /// Mint (or return the existing) credential for a party's "call". Returns the call as the party sees it, or
        /// null when the call is unknown or over, or the agent is not one of its parties.
        /// </summary>
        public A2ASession IssueToken(UUID sessionId, UUID agent) => IssueToken(sessionId, agent, out _);

        /// <summary>
        /// As <see cref="IssueToken(UUID, UUID)"/>. <paramref name="unavailable"/> is true when the null result is
        /// because the store could not be reached, so the call may well exist; false when the store answered or
        /// there is no store at all.
        /// </summary>
        public A2ASession IssueToken(UUID sessionId, UUID agent, out bool unavailable)
        {
            unavailable = false;
            INonSpatialSessions sessions = _sessions();
            if (sessions == null)
                return null;
            // Look first: only a call between two people is issued here, never a group or a conference that
            // happens to carry the same id.
            A2ASession live = TryGet(sessionId, agent, out unavailable);
            if (live == null || !live.IsParty(agent))
                return null;
            if (string.IsNullOrEmpty(sessions.IssueToken(sessionId, agent, out unavailable)))
                return null;
            return TryGet(sessionId, agent, out unavailable);
        }

        /// <summary>
        /// A party's multiagent provision was ADMITTED and answered by the voice service with
        /// <paramref name="viewerSession"/>. The callee's admitted provision is the accept: Invited -> Active.
        /// Returns null when the call is unknown or over or the agent is not a party.
        /// </summary>
        public A2ASession MarkProvisioned(UUID sessionId, UUID agent, UUID originRegion, string viewerSession)
            => MarkProvisioned(sessionId, agent, originRegion, viewerSession, out _);

        /// <summary>
        /// As above. <paramref name="becameActive"/> is true for the one join that seated the callee, which is the
        /// join that moved the call from Invited to Active. The store decides it inside its own mutation (the engine's
        /// first-seating outcome, also carried back by the grid session service), so joins that arrive together,
        /// from one process or several, cannot each claim the change as they could by reading the call before and
        /// after their own join.
        /// </summary>
        public A2ASession MarkProvisioned(UUID sessionId, UUID agent, UUID originRegion, string viewerSession, out bool becameActive)
        {
            becameActive = false;
            INonSpatialSessions sessions = _sessions();
            if (sessions == null)
                return null;
            SessionOutcome o = sessions.Accept(sessionId, agent, originRegion, viewerSession);
            A2ASession s = o.Ok ? Live(o.Session) : null;
            becameActive = s != null && o.Decision == SessionOutcome.Accepted && agent == s.Callee;
            return s;
        }

        /// <summary>
        /// The calls among <paramref name="touched"/> (what a departure returns) that the departure ended. The
        /// caller needs the parties to tell the remaining one who left. Only a call whose callee had answered ends
        /// this way, so every one returned was a formed call.
        /// </summary>
        public List<A2ASession> Ended(IEnumerable<NonSpatialVoiceSession> touched)
        {
            List<A2ASession> ended = new List<A2ASession>();
            if (touched == null)
                return ended;
            foreach (NonSpatialVoiceSession s in touched)
                if (s.Type == NonSpatialSessionType.P2P && s.IsDead)
                    ended.Add(new A2ASession(s));
            return ended;
        }

        /// <summary>
        /// "decline p2p voice" from a named party ends the call. Returns true when ended; false when the call is
        /// unknown or over (nothing to do) or the agent is not a party (ignored -- a stranger cannot cancel
        /// someone else's call).
        /// </summary>
        public bool Decline(UUID sessionId, UUID agent, out bool wasParty)
        {
            wasParty = false;
            INonSpatialSessions sessions = _sessions();
            A2ASession s = TryGet(sessionId, UUID.Zero);
            if (s == null || !s.IsParty(agent))
                return false;
            wasParty = true;
            return sessions.Decline(sessionId, agent).Ok;
        }

        /// <summary>
        /// Record that the callee's invitation was enqueued. Called by the region module only when delivery
        /// reported "sent" -- issuing an Invitation from the decision does not set it, so a callee-unreachable
        /// delivery leaves the next caller "call" free to ring again.
        /// </summary>
        public void MarkInviteSent(UUID sessionId)
        {
            INonSpatialSessions sessions = _sessions();
            A2ASession s = TryGet(sessionId, UUID.Zero);
            if (sessions != null && s != null && s.Callee != UUID.Zero)
                sessions.MarkInvited(sessionId, new[] { s.Callee });
        }

        // ---- calls placed or answered in another region-server process ------------------------------

        /// <summary>
        /// Stands in for the viewer session of a party who joined in another process: this process never sees that
        /// party's viewer session, only that the party is present.
        /// </summary>
        public const string RemoteViewerSession = "remote";

        /// <summary>
        /// A call placed in another region-server process arrived by its signed ring (the in-process store has no
        /// shared record). Records it here as the sender's process holds it: the caller seated, the callee invited
        /// and already rung, and the credential the one the caller's process minted. Null with
        /// <paramref name="refusal"/> when this process already holds the call and the ring must not replace it:
        /// one it started itself (two people calling each other at the same moment), an answered one, or one under
        /// a different credential.
        /// </summary>
        public A2ASession AdoptRemote(UUID caller, UUID callee, string roomKey, string token, out string refusal)
        {
            refusal = null;
            INonSpatialSessions sessions = _sessions();
            if (sessions == null) { refusal = "no-store"; return null; }
            if (string.IsNullOrEmpty(token)) { refusal = "no-token"; return null; }
            SessionOutcome o = sessions.AdoptRemoteRing(ComputeSessionId(caller, callee), roomKey, token,
                                                        NonSpatialCaps.P2PCap, caller, new[] { callee }, NonSpatialSessionType.P2P);
            if (!o.Ok)
            {
                refusal = o.Decision switch
                {
                    "refused-already-active" => "already-active",
                    "refused-call-in-progress" => "call-in-progress",
                    _ => o.Decision,
                };
                return null;
            }
            return Live(o.Session);
        }

        /// <summary>The far party joined in its own process and said so: it counts as present here. Null when this process holds no such call.</summary>
        public A2ASession MarkRemoteJoined(UUID sessionId, UUID joined, UUID originRegion)
        {
            INonSpatialSessions sessions = _sessions();
            A2ASession s = TryGet(sessionId, joined);
            if (sessions == null || s == null || !s.IsParty(joined))
                return null;
            SessionOutcome o = sessions.Accept(sessionId, joined, originRegion, RemoteViewerSession);
            return o.Ok ? Live(o.Session) : null;
        }

        /// <summary>
        /// The far party left. The call ends here when no one is joined in it any longer. Null when this process
        /// holds no such call; <paramref name="ended"/> says whether it is over.
        /// </summary>
        public A2ASession MarkRemoteGone(UUID sessionId, UUID departed, out bool ended)
        {
            ended = false;
            INonSpatialSessions sessions = _sessions();
            A2ASession s = TryGet(sessionId, departed);
            if (sessions == null || s == null || !s.IsParty(departed))
                return null;
            SessionOutcome o = sessions.Depart(sessionId, departed, DepartureReason.PresenceLost);
            if (!o.Ok)
                return null;
            ended = o.Session == null || o.Session.IsDead;
            return s;
        }

        // ---- lookup -------------------------------------------------------------------------

        /// <summary>
        /// Lookup by the provision's <c>channel</c> string (the session id). Null when unknown or over.
        /// <paramref name="reader"/> is who is asking: through the grid service the credential is in the copy only
        /// when that agent is a party.
        /// </summary>
        public A2ASession TryGetByChannel(string channel, UUID reader)
        {
            if (!UUID.TryParse(channel, out UUID sessionId))
                return null;
            return TryGet(sessionId, reader);
        }

        public A2ASession TryGet(UUID sessionId, UUID reader)
        {
            INonSpatialSessions sessions = _sessions();
            return sessions == null ? null : Live(sessions.Get(sessionId, reader));
        }

        /// <summary>As <see cref="TryGet(UUID, UUID)"/>, saying whether a null result is because the store could not be reached.</summary>
        public A2ASession TryGet(UUID sessionId, UUID reader, out bool unavailable)
        {
            INonSpatialSessions sessions = _sessions();
            if (sessions == null)
            {
                unavailable = false;
                return null;
            }
            return Live(sessions.Get(sessionId, reader, out unavailable));
        }

        /// <summary>The call, when the session is a call between two people that has not ended.</summary>
        private static A2ASession Live(NonSpatialVoiceSession s)
            => s != null && s.Type == NonSpatialSessionType.P2P && !s.IsDead ? new A2ASession(s) : null;
    }
}
