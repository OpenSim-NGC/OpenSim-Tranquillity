/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Avatar-to-avatar (P2P) voice invitation registry.
 *
 * In-memory, per region-server instance, thread-safe: sessionID -> { caller, callee, token, state, created }.
 * The session id is the viewer's XOR of the two agent ids (llimview.cpp:2530-2570); the
 * sim re-derives it and must find it equal. A record is created at ChatSessionRequest "start p2p voice"
 * (caller = the requesting agent, callee = `params`), the token is minted at the caller's "call" and is what
 * the viewer sends back as provision `credentials`. Unanswered invitations expire on a TTL.
 *
 * This class makes no authorization decision itself. A2AProvisionAdmission asks it, for a "multiagent"
 * provision: does the provision's `channel` name a live session, is the requesting agent one of its two parties,
 * and does `credentials` equal the token.
 *
 * The registry belongs to one region-server process: a callee in another process is not rung, finds no record
 * if it provisions, and is refused.
 */
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public enum A2ASessionState
    {
        /// <summary>Recorded at "start p2p voice"; the callee has not yet accepted (provisioned).</summary>
        Invited = 0,
        /// <summary>Both parties have provisioned (set by; unused in).</summary>
        Active = 1,
    }

    /// <summary>One invitation. Immutable identity fields; mutable token/state/seen under the registry lock.</summary>
    public sealed class A2ASession
    {
        public UUID SessionId { get; }
        public UUID Caller { get; }
        public UUID Callee { get; }
        public DateTime CreatedUtc { get; }

        /// <summary>The per-session secret the viewer echoes as provision <c>credentials</c>. Null until "call".</summary>
        public string Token { get; internal set; }
        public A2ASessionState State { get; internal set; }
        /// <summary>Last activity (record, call, provision, logout); TTL is measured from here.</summary>
        public DateTime LastSeenUtc { get; internal set; }

        /// <summary>Whether each party currently holds an admitted multiagent provision.</summary>
        public bool CallerProvisioned { get; internal set; }
        public bool CalleeProvisioned { get; internal set; }

        /// <summary>
        /// the invitation has been DELIVERED to the callee once for this record. Set by
        /// <see cref="A2ASessionRegistry.MarkInviteSent"/> after a confirmed enqueue, never on a failed
        /// delivery (so a caller retry can still reach a callee who was momentarily unreachable).
        /// Cleared only by the record being removed/recreated: one ring per invitation, ever. Closes
        /// the Invited-state window of an invitation feedback loop (each viewer answers a
        /// received ChatterBoxInvitation with its own bare "call", which would re-invite the other side,
        /// without bound).
        /// </summary>
        public bool InviteSent { get; internal set; }
        /// <summary>The voice-service viewer_session ids each party's admitted provision was answered with; null when none.</summary>
        public string CallerViewerSession { get; internal set; }
        public string CalleeViewerSession { get; internal set; }

        /// <summary>The value the viewer sends as <c>channel</c> on a multiagent provision: the session id as a string.</summary>
        public string ChannelUri => SessionId.ToString();

        public bool IsProvisioned(UUID agent) => agent == Caller ? CallerProvisioned : agent == Callee && CalleeProvisioned;

        internal A2ASession(UUID sessionId, UUID caller, UUID callee, DateTime nowUtc)
        {
            SessionId = sessionId;
            Caller = caller;
            Callee = callee;
            CreatedUtc = nowUtc;
            LastSeenUtc = nowUtc;
            State = A2ASessionState.Invited;
        }

        public bool IsParty(UUID agent) => agent == Caller || agent == Callee;

        public UUID OtherParty(UUID agent) => agent == Caller ? Callee : agent == Callee ? Caller : UUID.Zero;
    }

    public sealed class A2ASessionRegistry
    {
        /// <summary>Default lifetime of an unanswered invitation (unanswered invitation).</summary>
        public static readonly TimeSpan DefaultInviteTtl = TimeSpan.FromMinutes(2);

        /// <summary>Token size in bytes; rendered as lowercase hex (64 chars) so it survives LLSD string round-trips.</summary>
        public const int TokenBytes = 32;

        /// <summary>
        /// Idle backstop for an Active session (a leak guard): removed when no party has
        /// produced any registry activity (provision, logout, call, decline) for this long. A live call makes
        /// NO ChatSessionRequest traffic -- audio flows via the mixer -- so this must comfortably exceed any
        /// plausible call, or a party reconnecting mid-call would be refused-no-session. It is a leak guard
        /// for the crash/drop case, not a behaviour lever: normal teardown is both-logout (or a client close on
        /// this instance), which removes the record immediately. 8 hours: longer than a working day's call,
        /// bounded memory (records are ~200 bytes), and the only cost of a stale record before then is that a
        /// repeat "call" between the same pair does not re-ring while the record is Active.
        /// </summary>
        public static readonly TimeSpan DefaultActiveIdleTtl = TimeSpan.FromHours(8);

        private readonly object _lock = new object();
        private readonly Dictionary<UUID, A2ASession> _sessions = new Dictionary<UUID, A2ASession>();
        private readonly TimeSpan _inviteTtl;
        private readonly TimeSpan _activeIdleTtl;
        private readonly Func<DateTime> _clock;

        public A2ASessionRegistry() : this(DefaultInviteTtl, DefaultActiveIdleTtl, null) { }

        /// <param name="inviteTtl">Lifetime of an unanswered invitation.</param>
        /// <param name="clock">UTC clock; injectable so TTL is unit-testable without sleeping.</param>
        public A2ASessionRegistry(TimeSpan inviteTtl, Func<DateTime> clock) : this(inviteTtl, DefaultActiveIdleTtl, clock) { }

        /// <param name="activeIdleTtl">Idle backstop for Active sessions (see <see cref="DefaultActiveIdleTtl"/>).</param>
        public A2ASessionRegistry(TimeSpan inviteTtl, TimeSpan activeIdleTtl, Func<DateTime> clock)
        {
            _inviteTtl = inviteTtl > TimeSpan.Zero ? inviteTtl : DefaultInviteTtl;
            _activeIdleTtl = activeIdleTtl > TimeSpan.Zero ? activeIdleTtl : DefaultActiveIdleTtl;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public TimeSpan InviteTtl => _inviteTtl;
        public TimeSpan ActiveIdleTtl => _activeIdleTtl;

        // ---- lifecycle ----------------------------------------------------------------------

        /// <summary>
        /// A party's multiagent provision was ADMITTED and answered by the voice service with
        /// <paramref name="viewerSession"/>. The callee's admitted provision is the accept: Invited -> Active.
        /// Returns null when the voice session is unknown/expired or the agent is not a party.
        /// </summary>
        public A2ASession MarkProvisioned(UUID sessionId, UUID agent, string viewerSession)
        {
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                if (!_sessions.TryGetValue(sessionId, out A2ASession s) || !s.IsParty(agent))
                    return null;
                if (agent == s.Caller)
                {
                    s.CallerProvisioned = true;
                    s.CallerViewerSession = viewerSession;
                }
                else
                {
                    s.CalleeProvisioned = true;
                    s.CalleeViewerSession = viewerSession;
                    s.State = A2ASessionState.Active;          // the accept
                }
                s.LastSeenUtc = now;
                return s;
            }
        }

        /// <summary>
        /// should a client close mark the agent gone from its A2A records? Only a ROOT
        /// presence closing is the party leaving. A child agent closing (border crossing / draw distance)
        /// is not — the agent's voice lives in its root region — and no presence at all marks nothing.
        /// The same guard WebRtcVoiceServiceModule.Event_OnClientClosed applies.
        /// </summary>
        public static bool ShouldMarkGone(bool presenceFound, bool isChildAgent)
        {
            return presenceFound && !isChildAgent;
        }

        /// <summary>
        /// A party left (logout provision, or its client closed on this instance). When
        /// <paramref name="viewerSession"/> is given, only the record that party joined under that
        /// viewer session is affected; when null, every record the agent is a party of. An Active
        /// record whose parties are BOTH gone is removed (both-logout); an Invited record is left to its
        /// TTL. Returns the ids of the records removed.
        /// </summary>
        public List<UUID> MarkGone(UUID agent, string viewerSession)
        {
            List<UUID> ids = new List<UUID>();
            foreach (A2ASession s in MarkGoneSessions(agent, viewerSession))
                ids.Add(s.SessionId);
            return ids;
        }

        /// <summary>
        /// As <see cref="MarkGone"/>, but returns the removed SESSIONS: the caller needs the
        /// parties to tell the remaining one who left. Only Active records whose parties are both gone
        /// are ever removed here, so every returned session was a formed call.
        /// </summary>
        public List<A2ASession> MarkGoneSessions(UUID agent, string viewerSession)
        {
            DateTime now = _clock();
            List<A2ASession> removed = new List<A2ASession>();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                foreach (A2ASession s in new List<A2ASession>(_sessions.Values))
                {
                    if (!s.IsParty(agent))
                        continue;
                    bool isCaller = agent == s.Caller;
                    string vs = isCaller ? s.CallerViewerSession : s.CalleeViewerSession;
                    if (viewerSession != null && !string.Equals(vs, viewerSession, StringComparison.Ordinal))
                        continue;
                    if (isCaller) { s.CallerProvisioned = false; s.CallerViewerSession = null; }
                    else { s.CalleeProvisioned = false; s.CalleeViewerSession = null; }
                    s.LastSeenUtc = now;
                    if (s.State == A2ASessionState.Active && !s.CallerProvisioned && !s.CalleeProvisioned)
                    {
                        _sessions.Remove(s.SessionId);
                        removed.Add(s);
                    }
                }
            }
            return removed;
        }

        /// <summary>The voice session a party joined under <paramref name="viewerSession"/>, or null.</summary>
        public A2ASession FindByViewerSession(UUID agent, string viewerSession)
        {
            if (string.IsNullOrEmpty(viewerSession))
                return null;
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                foreach (A2ASession s in _sessions.Values)
                {
                    if (agent == s.Caller && string.Equals(s.CallerViewerSession, viewerSession, StringComparison.Ordinal))
                        return s;
                    if (agent == s.Callee && string.Equals(s.CalleeViewerSession, viewerSession, StringComparison.Ordinal))
                        return s;
                }
                return null;
            }
        }

        /// <summary>
        /// "decline p2p voice" from a named party removes the record. Returns true when removed; false when the
        /// session is unknown/expired (nothing to do) or the agent is not a party (ignored -- a stranger
        /// cannot cancel someone else's call).
        /// </summary>
        public bool Decline(UUID sessionId, UUID agent, out bool wasParty)
        {
            wasParty = false;
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                if (!_sessions.TryGetValue(sessionId, out A2ASession s))
                    return false;
                if (!s.IsParty(agent))
                    return false;
                wasParty = true;
                return _sessions.Remove(sessionId);
            }
        }

        /// <summary>
        /// record that the callee's invitation was enqueued. Called by the region module
        /// only when delivery reported "sent" -- issuing an Invitation from Decide does not set it,
        /// so a callee-unreachable delivery leaves the next caller "call" free to ring again.
        /// </summary>
        public void MarkInviteSent(UUID sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out A2ASession s))
                    s.InviteSent = true;
            }
        }

        /// <summary>The viewer's P2P session id: XOR of the two agent ids (symmetric, so both parties derive it).</summary>
        public static UUID ComputeSessionId(UUID a, UUID b)
            => new UUID(a.ulonga ^ b.ulonga, a.ulongb ^ b.ulongb);

        /// <summary>
        /// Record (or refresh) the invitation for caller -> callee. Idempotent for the same pair: a repeated
        /// "start p2p voice" refreshes LastSeen and keeps the token, so a caller that retries does not
        /// invalidate credentials already handed out. A record for the same session id from the OTHER
        /// direction (callee calling back) is the same session and is likewise refreshed, not replaced.
        /// </summary>
        public A2ASession Record(UUID caller, UUID callee, out bool created)
        {
            UUID sessionId = ComputeSessionId(caller, callee);
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                if (_sessions.TryGetValue(sessionId, out A2ASession existing) && existing.IsParty(caller) && existing.IsParty(callee))
                {
                    existing.LastSeenUtc = now;
                    created = false;
                    return existing;
                }
                A2ASession s = new A2ASession(sessionId, caller, callee, now);
                _sessions[sessionId] = s;
                created = true;
                return s;
            }
        }

        /// <summary>
        /// Mint (or return the existing) per-session token for a party's "call". Returns null when the
        /// session is unknown, expired, or the agent is not one of its parties.
        /// </summary>
        public A2ASession IssueToken(UUID sessionId, UUID agent)
        {
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                if (!_sessions.TryGetValue(sessionId, out A2ASession s) || !s.IsParty(agent))
                    return null;
                if (string.IsNullOrEmpty(s.Token))
                    s.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
                s.LastSeenUtc = now;
                return s;
            }
        }

        /// <summary>Lookup by the provision's <c>channel</c> string (the session id). Null when unknown or expired.</summary>
        public A2ASession TryGetByChannel(string channel)
        {
            if (!UUID.TryParse(channel, out UUID sessionId))
                return null;
            return TryGet(sessionId);
        }

        public A2ASession TryGet(UUID sessionId)
        {
            DateTime now = _clock();
            lock (_lock)
            {
                SweepExpiredLocked(now);
                return _sessions.TryGetValue(sessionId, out A2ASession s) ? s : null;
            }
        }

        /// <summary>Remove a voice session (decline, both-logout). False when nothing was recorded.</summary>
        public bool Remove(UUID sessionId)
        {
            lock (_lock)
                return _sessions.Remove(sessionId);
        }

        /// <summary>Number of live (unexpired) sessions; sweeps first. Diagnostics / tests.</summary>
        public int Count
        {
            get
            {
                DateTime now = _clock();
                lock (_lock)
                {
                    SweepExpiredLocked(now);
                    return _sessions.Count;
                }
            }
        }

        /// <summary>
        /// Drop every Invited session idle beyond the invite TTL, and every Active session idle beyond the
        /// active-idle backstop. Activity = record / call / admitted provision / logout / decline.
        /// </summary>
        private void SweepExpiredLocked(DateTime now)
        {
            List<UUID> dead = null;
            foreach (KeyValuePair<UUID, A2ASession> kv in _sessions)
            {
                TimeSpan idle = now - kv.Value.LastSeenUtc;
                bool expired = kv.Value.State == A2ASessionState.Invited ? idle > _inviteTtl : idle > _activeIdleTtl;
                if (expired)
                    (dead ??= new List<UUID>()).Add(kv.Key);
            }
            if (dead != null)
                foreach (UUID id in dead)
                    _sessions.Remove(id);
        }
    }
}
