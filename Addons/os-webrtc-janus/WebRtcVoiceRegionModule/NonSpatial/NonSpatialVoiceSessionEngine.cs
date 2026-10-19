/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The engine. Start / Invite / Accept / Decline / Depart, plus the sweep.
 *
 * Avatar-to-avatar calls do not use this engine: they keep their own registry (ChatSessionRequestLogic +
 * A2ASessionRegistry + A2AProvisionAdmission). Group voice sessions live here.
 *
 * IDEMPOTENCY is not sprinkled through the methods -- it falls out of two rules:
 *   - identity is DERIVED, never generated, everywhere it can be (DeriveSessionId), so the same
 *     request always addresses the same session. ADHOC is the one exception and it is handled by
 *     keying the creation on the viewer's temp id (see StartAdhoc);
 *   - every read-modify-write goes through INonSpatialSessionStore.StartOrGet / Mutate, which are
 *     atomic, so concurrent duplicates collapse instead of racing.
 * That is what makes a repeated start, a retried invite, a double accept, a reconnect and a region
 * crossing all no-ops rather than duplicates.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>What an engine operation did. Decision is the greppable instrument word.</summary>
    public readonly struct SessionOutcome
    {
        public bool Ok { get; }
        public string Decision { get; }
        public NonSpatialVoiceSession Session { get; }
        public bool Created { get; }

        /// <summary>
        /// True when this operation took the room from 0 seats to 1 -- the moment a group voice call
        /// BEGINS, and the only moment the fan-out fires. Decided inside the store mutation, so two
        /// simultaneous first-joiners cannot both claim it and double-ring the group.
        /// </summary>
        public bool StartedRinging { get; }

        public SessionOutcome(bool ok, string decision, NonSpatialVoiceSession session, bool created = false,
                              bool startedRinging = false)
        {
            Ok = ok;
            Decision = decision;
            Session = session;
            Created = created;
            StartedRinging = startedRinging;
        }

        public static SessionOutcome Fail(string decision, NonSpatialVoiceSession s = null) => new SessionOutcome(false, decision, s);

        public const string NoSuchSession = "refused-no-session";
        public const string Capacity = "refused-capacity";
        public const string AlreadyDeparted = "refused-departed";

        /// <summary>The process already holds the most sessions it is configured to.</summary>
        public const string GlobalSessionCap = "session-cap-global";

        /// <summary>The agent already has the most live sessions it may have started.</summary>
        public const string AgentSessionCap = "session-cap-agent";

        /// <summary>A start or invite would add more new people than the conference has room for.</summary>
        public const string TooManyInvitees = "too-many-invitees";

        /// <summary>The agent has made too many conference requests too quickly.</summary>
        public const string RateLimited = "rate-limited";
    }

    public sealed class NonSpatialVoiceSessionEngine
    {
        /// <summary>An unanswered invitation's lifetime. Matches the A2A invite TTL for consistency.</summary>
        public static readonly TimeSpan DefaultInviteTtl = TimeSpan.FromMinutes(2);

        /// <summary>Idle backstop for a formed session; the same reasoning as A2ASessionRegistry.DefaultActiveIdleTtl.</summary>
        public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromHours(8);

        private readonly INonSpatialSessionStore _store;
        private readonly Dictionary<NonSpatialSessionType, INonSpatialAdmission> _admission;
        private readonly string _gridId;
        private readonly Func<DateTime> _clock;
        private readonly TimeSpan _inviteTtl;
        private readonly TimeSpan _idleTtl;
        private readonly NonSpatialLimits _limits;
        private readonly RefusalLogGate _refusalGate = new RefusalLogGate();
        private readonly ConferenceRequestLimiter _requestLimiter;

        // Held across the check-then-create of every start, so the session caps and the ad-hoc
        // (creator, temp id) lookup are decided and applied as one step.
        private readonly object _startGate = new object();

        /// <summary>
        /// Raised when a limit refuses a request, at most once per agent per minute so a loop of refused
        /// requests cannot flood the log. Carries counts and the decision only, never lists of ids.
        /// </summary>
        public event Action<UUID, string, string> LimitRefused;

        public NonSpatialVoiceSessionEngine(INonSpatialSessionStore store,
                                            IEnumerable<INonSpatialAdmission> admissions,
                                            string gridId,
                                            Func<DateTime> clock = null,
                                            TimeSpan? inviteTtl = null,
                                            TimeSpan? idleTtl = null,
                                            NonSpatialLimits limits = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _admission = new Dictionary<NonSpatialSessionType, INonSpatialAdmission>();
            if (admissions != null)
                foreach (INonSpatialAdmission a in admissions)
                    _admission[a.Type] = a;
            _gridId = gridId ?? string.Empty;
            _clock = clock ?? (() => DateTime.UtcNow);
            _inviteTtl = inviteTtl ?? DefaultInviteTtl;
            _idleTtl = idleTtl ?? DefaultIdleTtl;
            _limits = limits ?? NonSpatialLimits.Default;
            _requestLimiter = new ConferenceRequestLimiter(_limits.ConferenceRequestBurst, _limits.ConferenceRequestRefillSeconds);
        }

        public NonSpatialLimits Limits => _limits;
        public ConferenceRequestLimiter RequestLimiter => _requestLimiter;

        /// <summary>
        /// Count one "start conference" or "invite" against <paramref name="agent"/>'s bucket. The two
        /// share it, so alternating between them gains nothing. False when the bucket is empty.
        /// </summary>
        public bool TryTakeConferenceRequest(UUID agent)
        {
            if (_requestLimiter.TryTake(agent, _clock()))
                return true;
            NoteRefusal(agent, SessionOutcome.RateLimited,
                        $"burst={_limits.ConferenceRequestBurst} refill-seconds={_limits.ConferenceRequestRefillSeconds}");
            return false;
        }

        /// <summary>
        /// Whether an agent is still present on this instance. A session whose idle time is up is kept while
        /// any member holding a seat is present, because someone in the call is activity however quiet the
        /// session has been. Unset, no agent counts as present and idle expiry works on the clock alone.
        /// </summary>
        public Func<UUID, bool> IsAgentPresent { get; set; }

        public INonSpatialSessionStore Store => _store;
        public TimeSpan InviteTtl => _inviteTtl;
        public TimeSpan IdleTtl => _idleTtl;

        // ---- identity ------------------------------------------------------------------------

        /// <summary>
        /// The AUTHORITATIVE session id. Derived wherever the viewer already agrees, so the start
        /// reply is a no-op re-key and a repeat addresses the same session:
        ///   P2P   the XOR of the two agents -- what the viewer computed (llimview.cpp:2551-2570)
        ///         and what the live path already re-derives (A2ASessionRegistry.ComputeSessionId).
        ///   GROUP the group id -- the viewer slams it (llimview.cpp:2535-2538).
        ///   ADHOC server-owned and RANDOM, because the viewer's id is itself random
        ///         (llimview.cpp:2542) and means nothing to anyone else. This is the only type that
        ///         needs the re-key, and giving the server the id is the point: it is what lets a
        ///         conference be addressed grid-wide by something other than one viewer's guess.
        /// </summary>
        public static UUID DeriveSessionId(NonSpatialSessionType type, UUID creator, UUID otherOrOwner)
        {
            switch (type)
            {
                case NonSpatialSessionType.P2P:
                    return new UUID(creator.ulonga ^ otherOrOwner.ulonga, creator.ulongb ^ otherOrOwner.ulongb);
                case NonSpatialSessionType.Group:
                    return otherOrOwner;
                case NonSpatialSessionType.Adhoc:
                    return UUID.Random();
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "unknown non-spatial session type");
            }
        }

        // ---- start ---------------------------------------------------------------------------

        /// <summary>
        /// Open (or re-find) a P2P or GROUP session. Idempotent by derivation: the id is a function
        /// of the parties, so a repeat -- including the other party starting it simultaneously --
        /// lands on the same record through StartOrGet.
        /// </summary>
        public SessionOutcome Start(NonSpatialSessionType type, UUID creator, UUID otherOrOwner,
                                    UUID tempSessionId, UUID originRegion, int requestedCap = 0)
        {
            if (type == NonSpatialSessionType.Adhoc)
                throw new ArgumentException("use StartAdhoc: an ad-hoc id is server-owned and keyed on the viewer's temp id", nameof(type));

            List<UUID> invitees = type == NonSpatialSessionType.P2P ? new List<UUID> { otherOrOwner } : new List<UUID>();
            AdmissionVerdict v = Admission(type).CanStart(creator, type == NonSpatialSessionType.Group ? otherOrOwner : UUID.Zero, invitees);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);

            UUID sessionId = DeriveSessionId(type, creator, otherOrOwner);
            return StartWithId(type, sessionId, tempSessionId == UUID.Zero ? sessionId : tempSessionId,
                               creator, type == NonSpatialSessionType.Group ? otherOrOwner : UUID.Zero,
                               originRegion, requestedCap,
                               type == NonSpatialSessionType.P2P ? new List<UUID> { otherOrOwner } : null);
        }

        /// <summary>
        /// Open (or re-find) an ADHOC conference. The authoritative id is server-owned, so
        /// idempotency cannot come from derivation -- it comes from the (creator, tempSessionId)
        /// pair the viewer retries with. A viewer that re-POSTs "start conference" after a timeout
        /// sends the SAME temp id (it is minted once, at session creation), so the second call finds
        /// the first session instead of opening a second room and a second start reply.
        /// </summary>
        public SessionOutcome StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion,
                                         IReadOnlyList<UUID> invitees = null, int requestedCap = 0)
        {
            AdmissionVerdict v = Admission(NonSpatialSessionType.Adhoc).CanStart(creator, UUID.Zero, invitees ?? new List<UUID>());
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);

            // The conference cap counts the person who starts the call, who takes the first seat, so the
            // people who can be rung are the cap minus one. An invitation reserves nothing, so a list that
            // fits can still be more than will accept; this bound only stops one request ringing the whole
            // grid. A list over it is refused whole, never cut to its first entries.
            List<UUID> distinct = DistinctInvitees(invitees, creator);
            int room = Math.Max(NonSpatialCaps.Effective(NonSpatialSessionType.Adhoc, requestedCap) - 1, 0);
            if (distinct.Count > room)
            {
                NoteRefusal(creator, SessionOutcome.TooManyInvitees,
                            $"asked={invitees?.Count ?? 0} new={distinct.Count} room={room}");
                return SessionOutcome.Fail(SessionOutcome.TooManyInvitees);
            }

            // Find-then-create as one step. A viewer retries a start with the same temp id, and two
            // retries that both looked before either created would each open a conference.
            lock (_startGate)
            {
                NonSpatialVoiceSession prior = FindAdhocByTemp(creator, tempSessionId);
                if (prior != null)
                {
                    Touch(prior.SessionId);
                    return new SessionOutcome(true, "start-idempotent", prior, created: false);
                }
                return StartWithId(NonSpatialSessionType.Adhoc, UUID.Random(), tempSessionId, creator, UUID.Zero,
                                   originRegion, requestedCap, distinct);
            }
        }

        /// <summary>
        /// The invitees once each, in first-seen order, without the nil id and without <paramref name="except"/>
        /// (the person asking). A set, not a scan of the list, so a long list costs linear time.
        /// </summary>
        public static List<UUID> DistinctInvitees(IEnumerable<UUID> asked, UUID except)
        {
            List<UUID> distinct = new List<UUID>();
            if (asked == null) return distinct;
            HashSet<UUID> seen = new HashSet<UUID>();
            foreach (UUID a in asked)
                if (a != UUID.Zero && a != except && seen.Add(a))
                    distinct.Add(a);
            return distinct;
        }

        /// <summary>
        /// Decide whether an "invite" naming <paramref name="asked"/> fits the conference. The new
        /// people in the request (not seated, and not already holding an invitation) may not outnumber the
        /// seats still free, and a request over that is refused whole. Nothing is invited by this call.
        /// </summary>
        public SessionOutcome CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyCollection<UUID> asked)
        {
            List<UUID> distinct = DistinctInvitees(asked, inviter);
            return _store.Mutate(sessionId, sess =>
            {
                int fresh = 0;
                foreach (UUID a in distinct)
                {
                    NonSpatialMember m = sess.Find(a);
                    if (m == null || (!m.HoldsSeat && m.State != MemberState.Invited))
                        fresh++;
                }
                int room = sess.Cap - sess.SeatsHeld;
                if (fresh > room)
                {
                    NoteRefusal(inviter, SessionOutcome.TooManyInvitees,
                                $"asked={asked?.Count ?? 0} new={fresh} room={Math.Max(room, 0)}");
                    // A room with no seat left is the capacity refusal an invite into it has always had.
                    return SessionOutcome.Fail(room <= 0 ? SessionOutcome.Capacity : SessionOutcome.TooManyInvitees, sess);
                }
                return new SessionOutcome(true, "invitees-fit", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        private SessionOutcome StartWithId(NonSpatialSessionType type, UUID sessionId, UUID tempSessionId,
                                           UUID creator, UUID owner, UUID originRegion, int requestedCap,
                                           IReadOnlyList<UUID> invitees)
        {
            DateTime now = _clock();
            int cap = NonSpatialCaps.Effective(type, requestedCap);
            string roomKey = NonSpatialRoomKey.Derive(_gridId, type, sessionId);

            NonSpatialVoiceSession s;
            bool created;
            lock (_startGate)
            {
                // A start that finds its session already there creates nothing, so only a genuinely
                // new session is counted against the caps.
                if (_store.Get(sessionId) == null)
                {
                    SessionOutcome refused = CheckSessionCaps(creator);
                    if (!refused.Ok) return refused;
                }
                s = _store.StartOrGet(sessionId, () =>
                {
                    NonSpatialVoiceSession made = new NonSpatialVoiceSession(type, sessionId, tempSessionId, roomKey,
                                                                             owner, creator, cap, now);
                    // the creator takes its seat at start; it is the one party that never needs inviting.
                    // Taking the first seat also JOINS THE CONVERSATION. The two axes are
                    // separate from here on -- a later voice hang-up clears the seat and leaves this.
                    made.Upsert(creator, MemberState.Accepted, originRegion, now).InChat = true;
                    if (invitees != null)
                        foreach (UUID a in invitees)
                            if (a != UUID.Zero && a != creator)
                                made.Upsert(a, MemberState.Invited, UUID.Zero, now);
                    return made;
                }, out created);
            }

            if (!created)
            {
                // A repeat start refreshes and re-seats the ORIGINAL creator (it may have departed
                // and come back) but never duplicates the voice session, the members or the room.
                //
                // The sharp edge here: for GROUP, every member calls Start, not just the
                // opener, so seating `creator` unconditionally would hand a seat to an arbitrary
                // caller WITHOUT the cap check that lives in Accept -- the 51st member would walk
                // straight in. Two guards: only the creator of the voice session is re-seated here, and
                // only when there is room. Everyone else takes their seat through Accept, which is
                // where admission and the cap are decided.
                _store.Mutate<object>(sessionId, sess =>
                {
                    sess.LastSeenUtc = now;
                    NonSpatialMember me = sess.Find(creator);
                    if (me == null || me.State == MemberState.Departed)
                    {
                        if (sess.Creator == creator && !sess.IsFull)
                            sess.Upsert(creator, MemberState.Accepted, originRegion, now).InChat = true;
                    }
                    else
                        me.LastSeenUtc = now;
                    if (invitees != null)
                        foreach (UUID a in invitees)
                            if (a != UUID.Zero && a != creator && sess.Find(a) == null)
                                sess.Upsert(a, MemberState.Invited, UUID.Zero, now);
                    return null;
                });
            }
            // The creator took the first seat inside the factory (or was re-seated above), so THIS
            // is where a group call begins for the agent that opens it.
            bool ring = ClaimRing(sessionId);
            return new SessionOutcome(true, created ? "started" : "start-idempotent", s, created, ring);
        }

        /// <summary>
        /// Refuse a new session when the process already holds the most it is configured to, or when
        /// the creator already has the most live sessions it may have started. Called with the start
        /// gate held, so two simultaneous starts cannot both slip under a cap.
        /// </summary>
        private SessionOutcome CheckSessionCaps(UUID creator)
        {
            IReadOnlyList<NonSpatialVoiceSession> all = _store.All();
            if (all.Count >= _limits.MaxNonSpatialSessions)
            {
                NoteRefusal(creator, SessionOutcome.GlobalSessionCap, $"sessions={all.Count} limit={_limits.MaxNonSpatialSessions}");
                return SessionOutcome.Fail(SessionOutcome.GlobalSessionCap);
            }
            int started = 0;
            foreach (NonSpatialVoiceSession existing in all)
                if (existing.Creator == creator && !_store.Mutate(existing.SessionId, sess => sess.IsDead, true))
                    started++;
            if (started >= _limits.MaxSessionsPerAgent)
            {
                NoteRefusal(creator, SessionOutcome.AgentSessionCap, $"started={started} limit={_limits.MaxSessionsPerAgent}");
                return SessionOutcome.Fail(SessionOutcome.AgentSessionCap);
            }
            return new SessionOutcome(true, "caps-ok", null);
        }

        /// <summary>Report a limit refusal, once per agent per minute. The detail carries counts only.</summary>
        private void NoteRefusal(UUID agent, string decision, string detail)
        {
            if (_refusalGate.ShouldLog(agent, _clock()))
                LimitRefused?.Invoke(agent, decision, detail);
        }

        /// <summary>
        /// Drop per-agent bookkeeping nobody has touched for a while. Called from the maintenance pass so
        /// the tables stay as small as the set of agents active recently.
        /// </summary>
        public void PruneTransientState()
        {
            DateTime now = _clock();
            _refusalGate.Prune(now, TransientStateMaxAge);
            _requestLimiter.Prune(now, TransientStateMaxAge);
        }

        /// <summary>How long an agent can go unseen before its bookkeeping is dropped.</summary>
        public static readonly TimeSpan TransientStateMaxAge = TimeSpan.FromMinutes(10);

        // ---- invite / accept / decline ---------------------------------------------------------

        /// <summary>
        /// Invite an agent. Idempotent: a repeated invite of the same agent refreshes the existing
        /// invitation rather than adding a second membership, and an invite of someone who already
        /// holds a seat is a no-op rather than a demotion back to Invited.
        /// </summary>
        public SessionOutcome Invite(UUID sessionId, UUID inviter, UUID invitee)
        {
            DateTime now = _clock();
            NonSpatialVoiceSession s = _store.Get(sessionId);
            if (s == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);

            AdmissionVerdict v = Admission(s.Type).CanInvite(s, inviter, invitee);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, s);

            // An invitation reserves no seat, but inviting INTO a room that is already full is a
            // refusal now rather than a disappointment later.
            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember existing = sess.Find(invitee);
                if (existing != null && existing.HoldsSeat)
                {
                    existing.LastSeenUtc = now;
                    sess.LastSeenUtc = now;
                    return new SessionOutcome(true, "invite-idempotent-seated", sess);
                }
                if (sess.IsFull)
                    return SessionOutcome.Fail(SessionOutcome.Capacity, sess);
                bool repeat = existing != null;
                sess.Upsert(invitee, MemberState.Invited, existing?.OriginRegion ?? UUID.Zero, now);
                sess.LastSeenUtc = now;
                return new SessionOutcome(true, repeat ? "invite-idempotent" : "invited", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>
        /// Take a seat. This is the cap's decision point and it is made INSIDE the store mutation,
        /// so N simultaneous accepts at the boundary produce exactly Cap seats and the rest a
        /// capacity refusal -- never Cap+1.
        /// </summary>
        public SessionOutcome Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
        {
            DateTime now = _clock();
            NonSpatialVoiceSession snapshot = _store.Get(sessionId);
            if (snapshot == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);

            AdmissionVerdict v = Admission(snapshot.Type).CanJoin(snapshot, agent);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, snapshot);

            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m != null && m.HoldsSeat)
                {
                    // Repeat accept, reconnect, or the same agent arriving from a new region: refresh
                    // in place. The region is re-pointed, NOT duplicated.
                    m.OriginRegion = originRegion;
                    m.LastSeenUtc = now;
                    if (!string.IsNullOrEmpty(viewerSession)) m.ViewerSession = viewerSession;
                    // An accept is an explicit join, so a repeat accept by someone who had left
                    // the CHAT while keeping their seat is a rejoin of the conversation. Without this
                    // they would sit in voice with no chat panel and the accept would do nothing
                    // they could see.
                    m.InChat = true;
                    sess.LastSeenUtc = now;
                    return new SessionOutcome(true, "accept-idempotent", sess);
                }
                if (sess.IsFull)
                    return SessionOutcome.Fail(SessionOutcome.Capacity, sess);
                bool wasEmpty = sess.SeatsHeld == 0;
                NonSpatialMember seated = sess.Upsert(agent, MemberState.Accepted, originRegion, now);
                // Accepting IS joining the conversation as well as taking the seat.
                seated.InChat = true;
                if (!string.IsNullOrEmpty(viewerSession)) seated.ViewerSession = viewerSession;
                sess.LastSeenUtc = now;
                // 0 -> 1 is the moment the call begins. It is claimed INSIDE the mutation and
                // latched by RingSent, so two simultaneous first-joiners cannot both ring the group.
                bool ring = wasEmpty && sess.SeatsHeld == 1 && !sess.RingSent;
                if (ring) sess.RingSent = true;
                return new SessionOutcome(true, "accepted", sess, created: false, startedRinging: ring);

            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>The member is in the media room (its provision was admitted). Accepted -> Present.</summary>
        public SessionOutcome MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
        {
            DateTime now = _clock();
            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m == null || m.State == MemberState.Departed || m.State == MemberState.Declined)
                    return SessionOutcome.Fail(SessionOutcome.AlreadyDeparted, sess);
                if (!m.HoldsSeat && sess.IsFull)
                    return SessionOutcome.Fail(SessionOutcome.Capacity, sess);
                m.State = MemberState.Present;
                m.OriginRegion = originRegion;
                m.LastSeenUtc = now;
                if (!string.IsNullOrEmpty(viewerSession)) m.ViewerSession = viewerSession;
                sess.LastSeenUtc = now;
                return new SessionOutcome(true, "present", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>
        /// Decline an invitation (the invitation-decline lifecycle). Separate from departing a voice session the agent
        /// had joined: declining never touches a seat, because an invitation never held one.
        /// </summary>
        public SessionOutcome Decline(UUID sessionId, UUID agent)
            => DepartInternal(sessionId, agent, DepartureReason.InvitationDeclined, MemberState.Declined);

        /// <summary>
        /// Leave, from any of the lifecycles. The engine does not care which one fired and never
        /// waits for the others: whichever arrives first releases the seat.
        /// </summary>
        public SessionOutcome Depart(UUID sessionId, UUID agent, DepartureReason reason)
            => DepartInternal(sessionId, agent, reason, MemberState.Departed);

        /// <summary>
        /// The chat-leave lifecycle ON ITS OWN -- the viewer's UDP chat-session leave, dialog 18
        /// (<c>IM_SESSION_LEAVE</c> = libomv <c>SessionDrop</c>; measured, they are the same value).
        /// The member leaves the CONVERSATION and KEEPS THEIR VOICE SEAT.
        ///
        /// THIS IS NOT <see cref="Depart"/> WITH A DIFFERENT REASON, and the distinction is the
        /// whole point of this method. <c>Depart(.., ChatLeave)</c> sets the member Departed, which
        /// releases the seat -- so closing the chat tab would hang up the voice call. The three
        /// lifecycles (chat leave, invitation decline, voice teardown) are independent and an agent
        /// may hold any one without the others; this method makes that true in the engine.
        ///
        /// Idempotent: leaving a conversation you are not in is a no-op, not a failure, because the
        /// viewer sends this on window close and can send it more than once.
        /// </summary>
        public SessionOutcome LeaveChat(UUID sessionId, UUID agent)
        {
            DateTime now = _clock();
            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession, sess);
                if (!m.InChat)
                {
                    sess.LastSeenUtc = now;
                    return new SessionOutcome(true, "chat-leave-idempotent", sess);
                }
                m.InChat = false;
                m.LastSeenUtc = now;
                sess.LastSeenUtc = now;
                // The seat, the invitation state and the ring latch are all deliberately untouched.
                return new SessionOutcome(true, "chat-left", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        private SessionOutcome DepartInternal(UUID sessionId, UUID agent, DepartureReason reason, MemberState terminal)
        {
            DateTime now = _clock();
            return _store.Mutate(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession, sess);
                if (m.State == terminal)
                {
                    sess.LastSeenUtc = now;
                    return new SessionOutcome(true, "depart-idempotent", sess);
                }
                m.State = terminal;
                m.Departure = reason;
                m.LastSeenUtc = now;
                m.ViewerSession = null;
                // The lifecycle rule in one line. Depart is a FULL departure -- seat and
                // conversation both -- with exactly one exception: VoiceTeardown is the
                // voice-teardown lifecycle by definition, so it releases the seat and leaves the member in the chat. That
                // is "still in the chat with voice hung up", the case this axis exists for.
                //
                // Chat-only departure is NOT Depart with some reason: it is LeaveChat. Keeping
                // Depart a full departure is what stops DepartureReason becoming a second, subtly
                // different way of saying which lifecycle fired.
                if (reason != DepartureReason.VoiceTeardown)
                    m.InChat = false;
                sess.LastSeenUtc = now;
                // The room emptied, so the ring cycle is over. Clearing it is what makes a
                // LATER start ring the group again; without it a group is rung once per process.
                if (sess.SeatsHeld == 0) sess.EndRingCycle();
                // A DECLINE also forgets that this agent was rung, so a later invite can ring
                // them again. A departure does not: leaving a call you were in is not an invitation
                // to be re-rung by every subsequent joiner.
                else if (terminal == MemberState.Declined) sess.ClearInvited(agent);
                return new SessionOutcome(true, terminal == MemberState.Declined ? "declined" : "departed", sess);
            }, SessionOutcome.Fail(SessionOutcome.NoSuchSession));
        }

        /// <summary>
        /// Release the seat the agent holds UNDER THIS VIEWER SESSION, and only that one. This is the
        /// voice-teardown lifecycle's entry point: the viewer's logout provision carries
        /// {logout, viewer_session} and no channel, so the viewer session is the only thing that
        /// identifies which seat to free.
        ///
        /// Without this, the logout arm would release A2A records and nothing else, so a group
        /// member who hung up would keep their seat until the idle TTL -- and with the 50 cap load-bearing,
        /// enough hang-ups would have locked a group out of its own room for eight hours.
        ///
        /// A null or empty viewer session matches NOTHING here on purpose. Departing every session an
        /// agent holds is <see cref="DepartAll"/>, which is the presence-close lifecycle, not this one.
        /// </summary>
        public IReadOnlyList<NonSpatialVoiceSession> DepartByViewerSession(UUID agent, string viewerSession, DepartureReason reason)
        {
            List<NonSpatialVoiceSession> touched = new List<NonSpatialVoiceSession>();
            if (string.IsNullOrEmpty(viewerSession))
                return touched;
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                NonSpatialMember m = s.Find(agent);
                if (m == null || !m.HoldsSeat || !string.Equals(m.ViewerSession, viewerSession, StringComparison.Ordinal))
                    continue;
                if (Depart(s.SessionId, agent, reason).Ok)
                    touched.Add(s);
            }
            return touched;
        }

        /// <summary>
        /// The agent left every voice session it was in, for one reason -- the shape a presence close or a
        /// grid-wide logout needs. Returns the sessions actually touched.
        /// </summary>
        public IReadOnlyList<NonSpatialVoiceSession> DepartAll(UUID agent, DepartureReason reason)
        {
            List<NonSpatialVoiceSession> touched = new List<NonSpatialVoiceSession>();
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                NonSpatialMember m = s.Find(agent);
                if (m == null || m.State == MemberState.Departed || m.State == MemberState.Declined) continue;
                SessionOutcome o = Depart(s.SessionId, agent, reason);
                if (o.Ok) touched.Add(s);
            }
            return touched;
        }

        /// <summary>
        /// Adopt a group session this instance learned about from an INCOMING RING rather
        /// than from a local start. Needed because the accept that follows a remote ring lands HERE,
        /// and TryHandleAcceptInvitation can only admit into a voice session this store knows.
        ///
        /// The room key is DERIVED locally, not taken from the wire, and compared against the one the
        /// ring carried. They must agree: the derivation is (gridId, tag, sessionId) and gridId is the
        /// shared GatekeeperURI, so a mismatch means the two instances disagree about the grid id --
        /// a misconfiguration that would silently put the two halves of a call in different mixer
        /// rooms. Better to refuse loudly here than to split a call silently.
        ///
        /// A ring that carries no room key is refused: the key is what ties the ring to this grid's
        /// derivation, so it cannot be optional.
        ///
        /// Born with RingSent LATCHED and the invited set pre-seeded, which is what makes send-once
        /// hold across instances: this instance must never re-ring a call it did not
        /// start. It carries NO seats -- the ringing instance owns those, and this instance's seat
        /// count is its own only (the per-instance cap limitation).
        ///
        /// A session this instance ALREADY holds is never overwritten, and a ring never latches RingSent
        /// on it or replaces its credential. The only thing a ring may still add to an existing session is
        /// for a session that was itself adopted from an earlier ring: the ringing target is marked as
        /// already rung and, for an ad-hoc conference, given an Invited record if it has none. That is
        /// needed because the second invitee of one conference on this instance arrives as a second ring,
        /// and an ad-hoc conference admits the accept on the member record alone. A session started on
        /// this instance, or a ring whose type differs from the session's, gets nothing added.
        /// </summary>
        public SessionOutcome AdoptRemoteRing(UUID groupId, string carriedRoomKey, string token, int cap,
                                              UUID creator, IEnumerable<UUID> alreadyRung,
                                              NonSpatialSessionType type = NonSpatialSessionType.Group)
        {
            if (groupId == UUID.Zero)
                return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            string local = NonSpatialRoomKey.Derive(_gridId, type, groupId);
            if (string.IsNullOrEmpty(carriedRoomKey))
                return SessionOutcome.Fail("refused-room-key-missing");
            if (!string.Equals(local, carriedRoomKey, StringComparison.Ordinal))
                return SessionOutcome.Fail("refused-room-key-mismatch");

            DateTime now = _clock();
            int effective = NonSpatialCaps.Effective(type, cap);
            // Owner is the group for a group session; an ad-hoc conference has no owning object.
            UUID owner = type == NonSpatialSessionType.Group ? groupId : UUID.Zero;
            NonSpatialVoiceSession s = _store.StartOrGet(groupId, () =>
            {
                NonSpatialVoiceSession made = new NonSpatialVoiceSession(type, groupId, groupId,
                                                                          local, owner, creator, effective, now);
                made.Token = token;                 // the ring carries the credential the viewer will echo
                made.RingSent = true;               // never re-ring a call this instance did not start
                made.AdoptedFromRing = true;
                if (alreadyRung != null)
                    foreach (UUID a2 in alreadyRung) made.MarkInvited(a2);
                SeedAdoptedMembership(made, type, alreadyRung, now);
                return made;
            }, out bool created);

            string decision = "ring-adopted";
            if (!created)
            {
                decision = _store.Mutate(groupId, sess =>
                {
                    if (sess.Type != type) return "refused-session-type-mismatch";
                    if (!sess.AdoptedFromRing) return "ring-session-held";   // started here: a ring adds nothing
                    if (alreadyRung != null)
                        foreach (UUID a2 in alreadyRung) sess.MarkInvited(a2);
                    SeedAdoptedMembership(sess, type, alreadyRung, now);
                    return "ring-adopt-idempotent";
                }, "refused-no-such-session");
                if (decision.StartsWith("refused-", StringComparison.Ordinal))
                    return SessionOutcome.Fail(decision);
            }
            return new SessionOutcome(true, decision, s, created);
        }

        /// <summary>
        /// An ADOPTED ad-hoc session must carry the membership, because AdhocAdmission.CanJoin
        /// admits on the member record alone -- an ad-hoc conference has no roster to fall back on.
        /// Without this, a member rung across instances would be refused at the accept that lands
        /// here, which is exactly the cross-instance case this method exists to make work.
        ///
        /// Group is deliberately left alone: its CanJoin re-checks membership and powers against the
        /// groups service, so records here would buy nothing and would change what
        /// GroupVoiceInvite.Targets sees on the adopting instance.
        /// </summary>
        private static void SeedAdoptedMembership(NonSpatialVoiceSession sess, NonSpatialSessionType type,
                                                  IEnumerable<UUID> rung, DateTime now)
        {
            if (type != NonSpatialSessionType.Adhoc || rung == null) return;
            foreach (UUID a in rung)
            {
                if (a == UUID.Zero) continue;
                if (sess.Find(a) == null) sess.Upsert(a, MemberState.Invited, UUID.Zero, now);
            }
        }

        // ---- credentials -----------------------------------------------------------------------

        /// <summary>Token size in bytes, rendered as lowercase hex. Matches A2ASessionRegistry.TokenBytes.</summary>
        public const int TokenBytes = 32;

        /// <summary>
        /// The credential for the voice session, minted on first call and stable afterwards so a retried "call"
        /// does not invalidate credentials already in a viewer's hands. Returns null when the agent
        /// is not a member of the voice session, so a token can never be handed to a stranger.
        /// </summary>
        public string IssueToken(UUID sessionId, UUID agent)
        {
            DateTime now = _clock();
            return _store.Mutate<string>(sessionId, sess =>
            {
                NonSpatialMember m = sess.Find(agent);
                if (m == null || m.State == MemberState.Departed || m.State == MemberState.Declined)
                    return null;
                if (string.IsNullOrEmpty(sess.Token))
                    sess.Token = NewToken();
                sess.LastSeenUtc = now;
                return sess.Token;
            });
        }

        /// <summary>
        /// Constant-time-ish comparison of a presented credential against the one held by the voice session. False for a
        /// session with no token yet, so a provision can never arrive before its "call".
        /// </summary>
        public static bool TokenMatches(NonSpatialVoiceSession session, string presented)
        {
            string held = session?.Token;
            if (string.IsNullOrEmpty(held) || string.IsNullOrEmpty(presented) || held.Length != presented.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < held.Length; i++) diff |= held[i] ^ presented[i];
            return diff == 0;
        }

        private static string NewToken()
        {
            byte[] raw = new byte[TokenBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(raw);
            return Convert.ToHexString(raw).ToLowerInvariant();
        }

        // ---- sweep ---------------------------------------------------------------------------

        /// <summary>
        /// Expire unanswered invitations and collect dead sessions. This is the backstop that makes
        /// "departure never waits on a POST that does not come" true even when NO lifecycle fires --
        /// a viewer that is simply gone leaves an invitation that ages out and a voice session that dies.
        /// Returns the ids removed.
        /// </summary>
        public IReadOnlyList<UUID> Sweep() => SweepWithSummary().Removed;

        /// <summary>What one sweep did: the sessions removed, the invitations that lapsed and the seats released.</summary>
        public readonly struct SweepSummary
        {
            public IReadOnlyList<UUID> Removed { get; }
            public int InvitationsExpired { get; }
            public int SeatsReleased { get; }

            public SweepSummary(IReadOnlyList<UUID> removed, int invitationsExpired, int seatsReleased)
            {
                Removed = removed;
                InvitationsExpired = invitationsExpired;
                SeatsReleased = seatsReleased;
            }
        }

        /// <summary>
        /// <see cref="Sweep"/> with the counts the maintenance pass logs. A session removed for being
        /// idle first has every member departed with the Expired reason, the same transition a normal
        /// departure makes, so a caller still holding the session never sees a seat in a room that
        /// no longer exists.
        /// </summary>
        public SweepSummary SweepWithSummary()
        {
            DateTime now = _clock();
            List<UUID> removed = new List<UUID>();
            int invitationsExpired = 0;
            int seatsReleased = 0;
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                // A session is idle only when nobody who holds a seat in it is still here. A present seat
                // holder is activity, so a long quiet call is not ended under the people in it.
                if (now - s.LastSeenUtc >= _idleTtl && SeatHolderIsPresent(s.SessionId))
                    Touch(s.SessionId);

                int[] counts = _store.Mutate(s.SessionId, sess =>
                {
                    int lapsed = 0;
                    foreach (NonSpatialMember m in sess.Members)
                        if (m.State == MemberState.Invited && now - m.InvitedUtc >= _inviteTtl)
                        {
                            m.State = MemberState.Departed;
                            m.Departure = DepartureReason.Expired;
                            m.LastSeenUtc = now;
                            lapsed++;
                        }
                    int released = 0;
                    if (now - sess.LastSeenUtc >= _idleTtl)
                    {
                        foreach (NonSpatialMember m in sess.Members)
                        {
                            if (m.HoldsSeat) released++;
                            if (m.State != MemberState.Departed && m.State != MemberState.Declined)
                            {
                                m.State = MemberState.Departed;
                                m.Departure = DepartureReason.Expired;
                            }
                            m.InChat = false;
                            m.ViewerSession = null;
                            m.LastSeenUtc = now;
                        }
                        sess.EndRingCycle();
                    }
                    return new[] { lapsed, released };
                }, null);
                if (counts == null) continue;
                invitationsExpired += counts[0];
                seatsReleased += counts[1];
                NonSpatialVoiceSession after = _store.Get(s.SessionId);
                if (after == null) continue;
                if (after.IsDead || now - after.LastSeenUtc >= _idleTtl)
                {
                    if (_store.Remove(after.SessionId)) removed.Add(after.SessionId);
                }
            }
            return new SweepSummary(removed, invitationsExpired, seatsReleased);
        }

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>
        /// True when a member holding a seat in the session is present on this instance. The lookup runs
        /// outside the store's lock. A lookup that fails counts as present: ending a call with someone in it
        /// is worse than keeping a dead one until the next pass.
        /// </summary>
        private bool SeatHolderIsPresent(UUID sessionId)
        {
            Func<UUID, bool> present = IsAgentPresent;
            if (present == null) return false;
            List<UUID> seated = _store.Mutate(sessionId, sess =>
            {
                List<UUID> ids = new List<UUID>();
                foreach (NonSpatialMember m in sess.Members)
                    if (m.HoldsSeat) ids.Add(m.AgentId);
                return ids;
            }, null);
            if (seated == null) return false;
            foreach (UUID agent in seated)
            {
                try { if (present(agent)) return true; }
                catch { return true; }
            }
            return false;
        }

        /// <summary>
        /// Record that these agents have been rung for the current ring cycle, so a later joiner's
        /// fan-out skips them (GroupVoiceInvite.Targets). Called after delivery whatever the outcome:
        /// a member we could not reach is then not re-rung on every subsequent join, which would be
        /// worse than one missed ring.
        /// </summary>
        public void MarkInvited(UUID sessionId, IEnumerable<UUID> agents)
        {
            if (agents == null) return;
            _store.Mutate<object>(sessionId, sess =>
            {
                foreach (UUID a in agents) sess.MarkInvited(a);
                return null;
            });
        }

        private INonSpatialAdmission Admission(NonSpatialSessionType type)
            => _admission.TryGetValue(type, out INonSpatialAdmission a)
                ? a
                : throw new InvalidOperationException("no admission policy registered for " + type);

        /// <summary>
        /// Claim the ring for this voice session if it is the first seat of a cycle. Atomic under the
        /// store, and latched by RingSent so exactly one caller ever claims it per cycle.
        ///
        /// Note that the 0 -> 1 transition does NOT happen in Accept for the
        /// agent that opens the room, because Start seats the creator itself. Claiming only in
        /// Accept meant the opener never rang the group -- which is the one case that always matters.
        /// </summary>
        private bool ClaimRing(UUID sessionId)
            => _store.Mutate(sessionId, sess =>
            {
                if (sess.SeatsHeld != 1 || sess.RingSent) return false;
                sess.RingSent = true;
                return true;
            }, false);

        private void Touch(UUID sessionId)
        {
            DateTime now = _clock();
            _store.Mutate<object>(sessionId, s => { s.LastSeenUtc = now; return null; });
        }

        private NonSpatialVoiceSession FindAdhocByTemp(UUID creator, UUID tempSessionId)
        {
            if (tempSessionId == UUID.Zero) return null;
            foreach (NonSpatialVoiceSession s in _store.All())
                if (s.Type == NonSpatialSessionType.Adhoc && s.TempSessionId == tempSessionId && s.Creator == creator)
                    return s;
            return null;
        }
    }
}
