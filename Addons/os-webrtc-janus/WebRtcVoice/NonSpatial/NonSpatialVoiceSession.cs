/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The NonSpatialVoiceSession engine -- the record types.
 *
 * A "non-spatial" voice session is any voice session whose room is NOT derived from region+parcel:
 * P2P, ADHOC (conference) and GROUP. Avatar-to-avatar (A2A) calls are the P2P type (see A2ACalls).
 *
 * TWO IDENTITIES, DELIBERATELY DISTINCT:
 *   TempSessionId  what the VIEWER minted and is waiting on. For a conference it is
 *                  session_id.generate() (llimview.cpp:2542) -- a fresh random id the server has
 *                  never seen. For a group it is the GROUP id (:2535-2538, "slam group session_id
 *                  to the group_id"). For P2P it is the XOR of the two agents.
 *   SessionId      the AUTHORITATIVE id the server assigns. The viewer adopts it only when a
 *                  ChatterBoxSessionStartReply arrives carrying {temp_session_id, session_id}: that
 *                  is what re-keys mId2SessionMap, updates the voice channel id and fires the queued
 *                  call (llimview.cpp:4877-4887 -> :1705-1737 -> :1731-1734). A session id in the
 *                  HTTP body is read by nobody, so the engine models BOTH ids and says when they differ.
 *
 * ROOM IDENTITY IS A THIRD THING (see NonSpatialRoomKey): the viewer treats channel_uri as opaque,
 * so the media room is NOT the session id. The A2A path chose ChannelUri => SessionId.ToString()
 * (A2ASession.ChannelUri); this engine deliberately does not repeat that.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>Which kind of non-spatial session this is. Admission and identity differ per type.</summary>
    public enum NonSpatialSessionType
    {
        /// <summary>Two named parties. Session id is the viewer's XOR, so no re-key.</summary>
        P2P = 0,
        /// <summary>An ad-hoc conference. The viewer's id is random and MUST be re-keyed.</summary>
        Adhoc = 1,
        /// <summary>A group session. Session id is the group id, so no re-key.</summary>
        Group = 2,
    }

    /// <summary>
    /// Where a member stands. Only <see cref="MemberState.Accepted"/> and
    /// <see cref="MemberState.Present"/> hold a seat against the cap.
    /// </summary>
    public enum MemberState
    {
        /// <summary>Invited, not answered. Holds no seat, so an invite storm cannot reserve the room.</summary>
        Invited = 0,
        /// <summary>Answered yes; not yet seen in the media room. Holds a seat.</summary>
        Accepted = 1,
        /// <summary>Provisioned / seen in the room. Holds a seat.</summary>
        Present = 2,
        /// <summary>Answered no. Terminal for this invitation; a fresh invite may re-open it.</summary>
        Declined = 3,
        /// <summary>Gone, for one of the reasons in <see cref="DepartureReason"/>.</summary>
        Departed = 4,
    }

    /// <summary>
    /// Why a member left. The first three belong to three INDEPENDENT lifecycles, and the
    /// engine accepts departure from any of them without waiting on the others:
    ///   ChatLeave           UDP IM_SESSION_LEAVE (llimview.cpp:2160-2179) -- chat membership only.
    ///   InvitationDeclined  the cap methods decline invitation / decline p2p voice (:3437, :3422).
    ///   VoiceTeardown       the provision teardown body {logout, viewer_session}, no channel_type.
    /// The last two are backstops so departure NEVER waits on a POST that does not come:
    ///   PresenceLost        the root presence closed (A2ACalls.ShouldMarkGone's rule).
    ///   Expired             the TTL swept it.
    /// </summary>
    public enum DepartureReason
    {
        None = 0,
        ChatLeave = 1,
        InvitationDeclined = 2,
        VoiceTeardown = 3,
        PresenceLost = 4,
        Expired = 5,
        /// <summary>A moderator or an operator removed the member from the call (see <see cref="ModerationOp.Remove"/>).</summary>
        Removed = 6,
        /// <summary>A group member no longer held the powers the call needs, so the call let them go (no removal is recorded).</summary>
        PowerLost = 7,
    }

    /// <summary>What a moderation request changes in the record.</summary>
    public enum ModerationOp
    {
        /// <summary>One agent's voice.</summary>
        VoiceMute = 0,
        /// <summary>One agent's text.</summary>
        TextMute = 1,
        /// <summary>The whole call's voice ("mute all" / "unmute all").</summary>
        VoiceModerated = 2,
        /// <summary>
        /// Remove one agent from the call (on) or let them back (off). A removed agent is refused every way back into the
        /// call until the call ends, or for the lifetime the removal was given.
        /// </summary>
        Remove = 3,
    }

    /// <summary>Why and until when an agent may not rejoin one call.</summary>
    public sealed class CallRemoval
    {
        public UUID By { get; }
        public DateTime AtUtc { get; }
        public string Reason { get; }

        /// <summary>When the removal ends. Null: when the call ends.</summary>
        public DateTime? ExpiresUtc { get; }

        public CallRemoval(UUID by, DateTime atUtc, string reason, DateTime? expiresUtc)
        {
            By = by;
            AtUtc = atUtc;
            Reason = reason ?? string.Empty;
            ExpiresUtc = expiresUtc;
        }

        public bool IsLive(DateTime nowUtc) => ExpiresUtc is null || nowUtc < ExpiresUtc.Value;
    }

    /// <summary>One agent's membership of one non-spatial voice session. Mutated only under the store's lock.</summary>
    public sealed class NonSpatialMember
    {
        public UUID AgentId { get; }
        public MemberState State { get; internal set; }
        public DepartureReason Departure { get; internal set; }

        /// <summary>
        /// The region the agent was last seen acting from. Carried, never assumed: a region crossing
        /// re-points it and must NOT create a second membership.
        /// </summary>
        public UUID OriginRegion { get; internal set; }

        /// <summary>The voice-service viewer_session this member's admitted provision was answered with.</summary>
        public string ViewerSession { get; internal set; }

        public DateTime InvitedUtc { get; }
        public DateTime LastSeenUtc { get; internal set; }

        /// <summary>Accepted or Present. This, and only this, counts against the cap.</summary>
        public bool HoldsSeat => State == MemberState.Accepted || State == MemberState.Present;

        /// <summary>
        /// Chat membership, which is a SEPARATE AXIS from the voice seat and from the
        /// invitation. The three lifecycles (chat, invitation, voice) must not be conflated:
        /// if "in the chat" and "holding a voice seat" were the same bit, a voice hang-up would
        /// silently remove the member from the text conversation.
        ///
        /// Set when the agent joins the conversation (the creator at start, an invitee at accept)
        /// and cleared ONLY by the chat lifecycle -- UDP dialog 18 -- or by the presence backstop,
        /// which ends everything at once because the client itself is gone.
        ///
        /// A voice teardown does NOT clear it: "still in the chat with voice hung up" is the exact
        /// case this flag exists to represent.
        /// </summary>
        public bool InChat { get; internal set; }

        internal NonSpatialMember(UUID agentId, MemberState state, UUID originRegion, DateTime nowUtc)
        {
            AgentId = agentId;
            State = state;
            OriginRegion = originRegion;
            InvitedUtc = nowUtc;
            LastSeenUtc = nowUtc;
            Departure = DepartureReason.None;
        }
    }

    /// <summary>
    /// One non-spatial voice session. Identity fields are immutable; membership and LastSeen are
    /// mutated only inside <see cref="INonSpatialSessionStore.Mutate{T}"/>, which is the single
    /// atomic unit a grid-wide implementation has to honour.
    /// </summary>
    public sealed class NonSpatialVoiceSession
    {
        public NonSpatialSessionType Type { get; }

        /// <summary>The authoritative id the SERVER assigns; the start reply carries it as session_id.</summary>
        public UUID SessionId { get; }

        /// <summary>The id the VIEWER minted; the start reply carries it as temp_session_id.</summary>
        public UUID TempSessionId { get; }

        /// <summary>
        /// True when the viewer must re-key from <see cref="TempSessionId"/> to
        /// <see cref="SessionId"/>. Only ADHOC does; P2P and GROUP already agree, so their start
        /// reply is a no-op re-key exactly like the group one we already ship
        /// (GroupsMessagingModule.cs).
        /// </summary>
        public bool RequiresRekey => SessionId != TempSessionId;

        /// <summary>The media room identity. NOT the session id -- see <see cref="NonSpatialRoomKey"/>.</summary>
        public string RoomKey { get; }

        /// <summary>The group id for GROUP; <see cref="UUID.Zero"/> otherwise.</summary>
        public UUID Owner { get; }

        public UUID Creator { get; }
        public DateTime CreatedUtc { get; }
        public DateTime LastSeenUtc { get; internal set; }

        /// <summary>The seat ceiling actually in force; see <see cref="NonSpatialCaps"/>.</summary>
        public int Cap { get; }

        /// <summary>
        /// The per-session secret the viewer echoes back as provision <c>credentials</c>, exactly as
        /// the A2A path does (<c>A2ASession.Token</c>). Minted once on first issue and stable
        /// afterwards, so a retried "call" does not invalidate credentials already handed out.
        /// It BINDS a provision to a non-spatial voice session; it is NOT the authority on its own -- the provision
        /// arm re-checks membership and powers, because in a group anyone who can call can learn it.
        /// </summary>
        public string Token { get; internal set; }

        /// <summary>
        /// The key a conference's members sign the text lines they send across region servers with. Minted on
        /// first request by a member, and never part of a copy of the session.
        /// </summary>
        internal string CarrierKey { get; set; }

        // The store's lock covers every change, but callers also read a session they got from Store.Get
        // without holding it (a log line's seat count, a ring walking the members). This lock makes
        // those reads see the table either before or after a change. It is only ever taken inside the
        // store's lock or alone, never the other way round.
        private readonly object _sync = new object();

        private readonly Dictionary<UUID, NonSpatialMember> _members = new Dictionary<UUID, NonSpatialMember>();

        /// <summary>
        /// Send-once latch: who has already been rung for the CURRENT ring cycle. A ring cycle begins
        /// when the room goes 0 -> 1 seats and ends when it returns to 0, at which point this is cleared
        /// (<see cref="EndRingCycle"/>) so that starting the call again later rings everyone again.
        /// Without the clear, a group could be rung exactly once for the lifetime of the process.
        /// </summary>
        private readonly HashSet<UUID> _invited = new HashSet<UUID>();

        /// <summary>True once the 0 -> 1 transition has fired its fan-out for this cycle.</summary>
        public bool RingSent { get; internal set; }

        /// <summary>
        /// True for a session this instance created from an incoming ring rather than from a local start.
        /// A later ring may add invitations to such a session; it adds nothing to one started here.
        /// </summary>
        public bool AdoptedFromRing { get; internal set; }

        public bool WasInvited(UUID agent) { lock (_sync) return _invited.Contains(agent); }

        // ---- moderation ------------------------------------------------------------------------
        //
        // What the call's moderators have decided, kept in the record so it follows the call wherever the record
        // lives. Authorisation is not here: the region decides who may ask, the record only holds the result.
        // Moderation belongs to the call: when the last seat is released (EndRingCycle, or a removal of the last member)
        // the mute-all and every voice and text mute go, and the next call begins unmuted. A removal is not part of that;
        // it keeps its own lifetime (see below).

        /// <summary>Most agents one of the moderation sets holds. A request past it is refused.</summary>
        public const int MaxModerationEntries = 1000;

        private readonly HashSet<UUID> _voiceMuted = new HashSet<UUID>();
        private readonly HashSet<UUID> _voiceAllowed = new HashSet<UUID>();
        private readonly HashSet<UUID> _textMuted = new HashSet<UUID>();

        /// <summary>The moderators muted every voice in the call ("mute all"). Later joiners are muted too.</summary>
        public bool VoiceModerated { get; internal set; }

        /// <summary>Bumped by exactly one for each moderation change that changed something.</summary>
        public int ModerationGeneration { get; internal set; }

        /// <summary>Agents a moderator muted by name.</summary>
        public IReadOnlyCollection<UUID> VoiceMuted => _voiceMuted;

        /// <summary>While the call is moderated: agents a moderator let speak by name.</summary>
        public IReadOnlyCollection<UUID> VoiceAllowed => _voiceAllowed;

        /// <summary>Agents whose text a moderator muted.</summary>
        public IReadOnlyCollection<UUID> TextMuted => _textMuted;

        public bool IsTextMuted(UUID agent) => _textMuted.Contains(agent);

        /// <summary>
        /// Whether moderation silences this agent's voice. A name beats the mode: a muted agent is silent, an allowed
        /// agent speaks while the call is moderated. Who is a moderator (never muted) is for the region to say.
        /// </summary>
        public bool IsVoiceMuted(UUID agent)
            => _voiceMuted.Contains(agent) || (VoiceModerated && !_voiceAllowed.Contains(agent));

        public bool HasModeration => VoiceModerated || _voiceMuted.Count > 0 || _textMuted.Count > 0 || HasLongRemoval;

        // ---- removal ---------------------------------------------------------------------------
        //
        // An agent a moderator or an operator removed. A removal with no end time lasts until the call ends: the moment
        // the last seat is released (EndRingCycle) it goes with the call. One given a lifetime outlasts the call and
        // keeps the record until it runs out.

        private readonly Dictionary<UUID, CallRemoval> _removed = new Dictionary<UUID, CallRemoval>();

        /// <summary>Every removal in the record, live or not yet cleared.</summary>
        public IReadOnlyDictionary<UUID, CallRemoval> Removed => _removed;

        /// <summary>True while a removal that outlasts the call stands.</summary>
        public bool HasLongRemoval
        {
            get
            {
                foreach (CallRemoval r in _removed.Values)
                    if (r.ExpiresUtc is not null) return true;
                return false;
            }
        }

        /// <summary>Whether the agent is barred from this call at <paramref name="nowUtc"/>.</summary>
        public bool IsRemoved(UUID agent, DateTime nowUtc)
            => _removed.TryGetValue(agent, out CallRemoval r) && r.IsLive(nowUtc);

        public CallRemoval RemovalOf(UUID agent) => _removed.TryGetValue(agent, out CallRemoval r) ? r : null;

        /// <summary>Records a removal. True when it changed something (the generation moves).</summary>
        internal bool ApplyRemoval(UUID target, UUID by, string reason, DateTime nowUtc, DateTime? expiresUtc)
        {
            PurgeRemovals(nowUtc);
            if (IsRemoved(target, nowUtc)) return false;
            _removed[target] = new CallRemoval(by, nowUtc, reason, expiresUtc);
            ModerationGeneration++;
            return true;
        }

        /// <summary>Lets the agent back. True when a removal stood (the generation moves).</summary>
        internal bool ClearRemoval(UUID target, DateTime nowUtc)
        {
            bool live = IsRemoved(target, nowUtc);
            bool had = _removed.Remove(target);
            if (live) ModerationGeneration++;
            return had && live;
        }

        internal bool RemovalFull(DateTime nowUtc)
        {
            PurgeRemovals(nowUtc);
            return _removed.Count >= MaxModerationEntries;
        }

        /// <summary>Forgets removals that have run out.</summary>
        internal void PurgeRemovals(DateTime nowUtc)
        {
            List<UUID> over = null;
            foreach (KeyValuePair<UUID, CallRemoval> kv in _removed)
                if (!kv.Value.IsLive(nowUtc))
                    (over ??= new List<UUID>()).Add(kv.Key);
            if (over != null)
                foreach (UUID a in over) _removed.Remove(a);
        }

        internal void RestoreRemoval(UUID target, CallRemoval removal) => _removed[target] = removal;

        /// <summary>Applies one change. True when it changed something (and the generation moved).</summary>
        internal bool ApplyModeration(ModerationOp op, UUID target, bool on)
        {
            bool changed;
            switch (op)
            {
                case ModerationOp.VoiceMute:
                    if (on)
                        changed = _voiceMuted.Add(target) | _voiceAllowed.Remove(target);
                    else
                        changed = _voiceMuted.Remove(target) | (VoiceModerated && _voiceAllowed.Add(target));
                    break;
                case ModerationOp.TextMute:
                    changed = on ? _textMuted.Add(target) : _textMuted.Remove(target);
                    break;
                case ModerationOp.VoiceModerated:
                    if (on)
                    {
                        changed = !VoiceModerated || _voiceAllowed.Count > 0;
                        VoiceModerated = true;
                        _voiceAllowed.Clear();
                    }
                    else
                    {
                        changed = VoiceModerated || _voiceMuted.Count > 0 || _voiceAllowed.Count > 0;
                        VoiceModerated = false;
                        _voiceMuted.Clear();
                        _voiceAllowed.Clear();
                    }
                    break;
                default:
                    return false;
            }
            if (changed) ModerationGeneration++;
            return changed;
        }

        internal bool ModerationFull(ModerationOp op, bool on)
            => on && ((op == ModerationOp.VoiceMute && _voiceMuted.Count >= MaxModerationEntries)
                      || (op == ModerationOp.TextMute && _textMuted.Count >= MaxModerationEntries))
               || (!on && op == ModerationOp.VoiceMute && VoiceModerated && _voiceAllowed.Count >= MaxModerationEntries);

        /// <summary>Restores moderation read from a copy of the record.</summary>
        internal void RestoreModeration(bool voiceModerated, int generation, IEnumerable<UUID> muted,
                                        IEnumerable<UUID> allowed, IEnumerable<UUID> textMuted)
        {
            VoiceModerated = voiceModerated;
            ModerationGeneration = generation;
            if (muted != null) foreach (UUID a in muted) _voiceMuted.Add(a);
            if (allowed != null) foreach (UUID a in allowed) _voiceAllowed.Add(a);
            if (textMuted != null) foreach (UUID a in textMuted) _textMuted.Add(a);
        }

        /// <summary>
        /// Who has already used the ring they were marked invited for (see the engine's VerifyRing). A ring is
        /// one-shot: a fresh invitation entitles the agent to one ring, and a second claim to the same ring is a replay.
        /// </summary>
        private readonly HashSet<UUID> _ringsUsed = new HashSet<UUID>();

        internal void MarkInvited(UUID agent)
        {
            lock (_sync)
                if (_invited.Add(agent) && Type != NonSpatialSessionType.P2P)
                    _ringsUsed.Remove(agent);
        }

        /// <summary>
        /// True the first time it is asked for the callee of a call between two people, false after that. A call is
        /// rung once, so unlike a group ring this does not wait for the callee to be marked invited and is not
        /// given back when they are.
        /// </summary>
        internal bool TakeCallRing(UUID agent) { lock (_sync) return _ringsUsed.Add(agent); }

        /// <summary>True the first time it is asked for an invited agent, false after that.</summary>
        internal bool TakeRing(UUID agent) { lock (_sync) return _invited.Contains(agent) && _ringsUsed.Add(agent); }

        /// <summary>
        /// Forget that ONE agent was rung, without ending the cycle for everyone else.
        /// Declining clears the latch so a later "invite" of the same agent rings them again -- a
        /// mis-clicked decline must not make someone unreachable for the rest of the call. Safe
        /// against a re-ring storm because only an explicit invite puts them back in Invited, and
        /// AdhocVoiceInvite.Targets rings Invited members only.
        /// </summary>
        internal void ClearInvited(UUID agent)
        {
            lock (_sync)
            {
                _invited.Remove(agent);
                _ringsUsed.Remove(agent);
            }
        }

        /// <summary>
        /// The call's moderators' decisions end with the call: mute-all, and every voice and text mute. True when
        /// something was cleared (the generation moves, so a reader of a copy sees the change). Removals are left to
        /// their own lifetime.
        /// </summary>
        internal bool EndModeration()
        {
            bool changed = VoiceModerated || _voiceMuted.Count > 0 || _voiceAllowed.Count > 0 || _textMuted.Count > 0;
            VoiceModerated = false;
            _voiceMuted.Clear();
            _voiceAllowed.Clear();
            _textMuted.Clear();
            if (changed) ModerationGeneration++;
            return changed;
        }

        /// <summary>The room emptied: forget the ring so a later start rings the group again, and end the moderation.</summary>
        internal void EndRingCycle()
        {
            lock (_sync)
            {
                _invited.Clear();
                _ringsUsed.Clear();
            }
            RingSent = false;
            EndModeration();
            // The call is over, so removals that last as long as the call are over with it. One given a lifetime stands.
            List<UUID> callBound = null;
            foreach (KeyValuePair<UUID, CallRemoval> kv in _removed)
                if (kv.Value.ExpiresUtc is null)
                    (callBound ??= new List<UUID>()).Add(kv.Key);
            if (callBound != null)
                foreach (UUID a in callBound) _removed.Remove(a);
        }

        internal NonSpatialVoiceSession(NonSpatialSessionType type, UUID sessionId, UUID tempSessionId,
                                        string roomKey, UUID owner, UUID creator, int cap, DateTime nowUtc)
        {
            Type = type;
            SessionId = sessionId;
            TempSessionId = tempSessionId;
            RoomKey = roomKey;
            Owner = owner;
            Creator = creator;
            Cap = cap;
            CreatedUtc = nowUtc;
            LastSeenUtc = nowUtc;
        }

        public NonSpatialMember Find(UUID agent) { lock (_sync) return _members.TryGetValue(agent, out NonSpatialMember m) ? m : null; }

        /// <summary>The members at the moment of the call. A copy, so the caller can walk it while others join.</summary>
        public IReadOnlyCollection<NonSpatialMember> Members { get { lock (_sync) return _members.Values.ToArray(); } }

        /// <summary>Seats taken. Invited, Declined and Departed do not count.</summary>
        public int SeatsHeld
        {
            get
            {
                int n = 0;
                lock (_sync)
                    foreach (NonSpatialMember m in _members.Values)
                        if (m.HoldsSeat) n++;
                return n;
            }
        }

        public bool IsFull => SeatsHeld >= Cap;

        /// <summary>
        /// Nobody holds a seat, nobody is still in the chat, and nothing is outstanding: the
        /// session is collectable.
        ///
        /// The chat clause matters: without it a conference whose members had all hung up voice
        /// but were still typing would be swept out from under them, because "holds a
        /// seat" would be the only sign of life this could see.
        ///
        /// A person-to-person call has no chat of its own in this store (the viewer keeps the
        /// instant-message conversation), so for it only a seat or an unanswered invitation
        /// keeps the session alive.
        /// </summary>
        public bool IsDead
        {
            get
            {
                // A removal given a lifetime outlasts the call, whatever kind of call it was.
                if (HasLongRemoval) return false;
                lock (_sync)
                    foreach (NonSpatialMember m in _members.Values)
                        if (m.State == MemberState.Invited || m.HoldsSeat || (m.InChat && Type != NonSpatialSessionType.P2P)) return false;
                return true;
            }
        }

        /// <summary>
        /// For a person-to-person call, the party who did not start it: the one invited. Null for the other
        /// session types, and for a call that has no second party yet.
        /// </summary>
        public NonSpatialMember Callee
        {
            get
            {
                if (Type != NonSpatialSessionType.P2P) return null;
                lock (_sync)
                    foreach (NonSpatialMember m in _members.Values)
                        if (m.AgentId != Creator) return m;
                return null;
            }
        }

        /// <summary>
        /// For a person-to-person call: the callee has taken the call at least once, so the call is under way.
        /// It stays true after the callee hangs up, until the whole call ends.
        /// </summary>
        public bool CalleeAnswered
        {
            get
            {
                NonSpatialMember callee = Callee;
                return callee != null && callee.State != MemberState.Invited && callee.State != MemberState.Declined;
            }
        }

        /// <summary>
        /// A member whose provision was admitted and answered by the voice service: it holds a seat and was
        /// tagged with the service's viewer session. A seat taken at start, before any provision, is not this.
        /// </summary>
        public static bool IsJoined(NonSpatialMember m)
            => m != null && m.HoldsSeat && !string.IsNullOrEmpty(m.ViewerSession);

        /// <summary>
        /// Everyone currently in the CHAT conversation. This -- not the seat list -- is the roster
        /// the viewer's participant panel shows and the audience a typed line reaches.
        /// </summary>
        public List<UUID> ChatMembers()
        {
            List<UUID> all = new List<UUID>();
            lock (_sync)
                foreach (NonSpatialMember m in _members.Values)
                    if (m.InChat) all.Add(m.AgentId);
            return all;
        }

        internal NonSpatialMember Upsert(UUID agent, MemberState state, UUID originRegion, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_members.TryGetValue(agent, out NonSpatialMember m))
                {
                    m.State = state;
                    m.OriginRegion = originRegion;
                    m.LastSeenUtc = nowUtc;
                    if (state != MemberState.Departed && state != MemberState.Declined)
                        m.Departure = DepartureReason.None;
                    return m;
                }
                m = new NonSpatialMember(agent, state, originRegion, nowUtc);
                _members[agent] = m;
                return m;
            }
        }
    }
}
