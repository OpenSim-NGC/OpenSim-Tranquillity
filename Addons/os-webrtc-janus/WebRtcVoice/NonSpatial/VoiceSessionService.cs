/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The service side of the grid call session store. It owns one engine over one in-memory store, so
 * everything the engine decides (the seat cap, the ring latch, ad-hoc start de-duplication, the
 * session and per-agent caps, the request rate) is decided once for every region that asks.
 *
 * Group membership and power checks stay with the region, which has the groups service; the service
 * keeps the checks that need only the session itself (who is a party, who was invited).
 *
 * Seats belong to regions that can disappear, so a seat has a lease: a region renews the seats it
 * still holds (RenewSeats) and a seat nobody renews for the lease is released by Sweep with the
 * PresenceLost reason. A seat is renewed by name, not by its agent, so a seat a region has let go of is not
 * kept by the other seats the same agent holds. A region that starts up asks for the seats naming it to be released
 * (DepartRegion), since a fresh process holds no viewers.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class VoiceSessionService : IVoiceSessionService
    {
        public static readonly TimeSpan DefaultSeatLease = TimeSpan.FromSeconds(180);

        /// <summary>Most seats one RenewSeats call is read for.</summary>
        public const int MaxRenewSeats = 10000;

        private readonly NonSpatialVoiceSessionEngine _engine;
        private readonly INonSpatialSessionStore _store;
        private readonly Func<DateTime> _clock;
        private readonly TimeSpan _seatLease;
        // When each seat was last renewed, and when any seat of each agent was: the second answers whether an agent
        // is still here, which keeps a call with a renewed member from being collected as idle.
        private readonly Dictionary<SeatKey, DateTime> _renewed = new Dictionary<SeatKey, DateTime>();
        private readonly Dictionary<UUID, DateTime> _renewedAgent = new Dictionary<UUID, DateTime>();

        public event Action<UUID, string, string> LimitRefused
        {
            add { _engine.LimitRefused += value; }
            remove { _engine.LimitRefused -= value; }
        }

        public VoiceSessionService(string gridId, NonSpatialLimits limits = null, Func<DateTime> clock = null,
                                   TimeSpan? inviteTtl = null, TimeSpan? idleTtl = null, TimeSpan? seatLease = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
            _seatLease = seatLease ?? DefaultSeatLease;
            _store = new InMemoryNonSpatialSessionStore();
            _engine = new NonSpatialVoiceSessionEngine(
                _store,
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), new RegionCheckedGroupAdmission() },
                gridId, _clock, inviteTtl, idleTtl, limits);
            // A call whose members a region is still renewing is not idle, however quiet it has been.
            _engine.IsAgentPresent = IsRenewed;
        }

        public NonSpatialLimits Limits => _engine.Limits;
        public int SessionCount => _store.All().Count;

        // ---- operations ------------------------------------------------------------------------

        public SessionReply Start(int type, UUID creator, UUID otherOrOwner, UUID tempSessionId, UUID originRegion, int requestedCap)
        {
            if (!IsStartableType(type) || creator == UUID.Zero)
                return SessionReply.Fail("refused-bad-request");
            return Reply(_engine.Start((NonSpatialSessionType)type, creator, otherOrOwner, tempSessionId, originRegion, requestedCap), creator);
        }

        public SessionReply StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion, IReadOnlyList<UUID> invitees, int requestedCap)
            => Reply(_engine.StartAdhoc(creator, tempSessionId, originRegion, invitees, requestedCap), creator);

        public SessionReply CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyList<UUID> asked)
            => Reply(_engine.CheckInviteCount(sessionId, inviter, asked), inviter);

        public SessionReply Invite(UUID sessionId, UUID inviter, UUID invitee)
            => Reply(_engine.Invite(sessionId, inviter, invitee), inviter);

        public SessionReply Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession)
            => Reply(_engine.Accept(sessionId, agent, originRegion, viewerSession), agent);

        public SessionReply MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession)
            => Reply(_engine.MarkPresent(sessionId, agent, originRegion, viewerSession), agent);

        public SessionReply Decline(UUID sessionId, UUID agent)
            => Reply(_engine.Decline(sessionId, agent), agent);

        public SessionReply Depart(UUID sessionId, UUID agent, int reason)
            => IsReason(reason)
                ? Reply(_engine.Depart(sessionId, agent, (DepartureReason)reason), agent)
                : SessionReply.Fail("refused-bad-request");

        public SessionReply LeaveChat(UUID sessionId, UUID agent)
            => Reply(_engine.LeaveChat(sessionId, agent), agent);

        public SessionReply DepartByViewerSession(UUID agent, string viewerSession, int reason)
            => IsReason(reason)
                ? Many(_engine.DepartByViewerSession(agent, viewerSession, (DepartureReason)reason), agent)
                : SessionReply.Fail("refused-bad-request");

        public SessionReply DepartAll(UUID agent, int reason)
            => IsReason(reason)
                ? Many(_engine.DepartAll(agent, (DepartureReason)reason), agent)
                : SessionReply.Fail("refused-bad-request");

        public SessionReply DepartRegion(UUID region)
        {
            if (region == UUID.Zero) return new SessionReply { Ok = true, Decision = "region-departed", Count = 0 };
            int released = 0;
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                List<UUID> seated = _store.Mutate(s.SessionId, sess =>
                {
                    List<UUID> ids = new List<UUID>();
                    foreach (NonSpatialMember m in sess.Members)
                        if (m.HoldsSeat && m.OriginRegion == region) ids.Add(m.AgentId);
                    return ids;
                }, null);
                if (seated == null) continue;
                foreach (UUID agent in seated)
                    if (_engine.Depart(s.SessionId, agent, DepartureReason.PresenceLost).Ok) released++;
            }
            return new SessionReply { Ok = true, Decision = "region-departed", Count = released };
        }

        public SessionReply RenewSeats(IReadOnlyList<SeatKey> seats)
        {
            DateTime now = _clock();
            int n = 0;
            if (seats != null)
                lock (_renewed)
                    foreach (SeatKey seat in seats)
                    {
                        if (n >= MaxRenewSeats) break;
                        if (seat.Agent == UUID.Zero || seat.Session == UUID.Zero) continue;
                        _renewed[seat] = now;
                        _renewedAgent[seat.Agent] = now;
                        n++;
                    }
            return new SessionReply { Ok = true, Decision = "renewed", Count = n };
        }

        public SessionReply MarkInvited(UUID sessionId, IReadOnlyList<UUID> agents)
        {
            _engine.MarkInvited(sessionId, agents);
            return new SessionReply { Ok = true, Decision = "marked" };
        }

        public SessionReply VerifyRing(UUID sessionId, UUID target, UUID claimedCaller, int type)
            => type == (int)NonSpatialSessionType.Group || type == (int)NonSpatialSessionType.Adhoc
               || type == (int)NonSpatialSessionType.P2P
                ? Reply(_engine.VerifyRing(sessionId, target, claimedCaller, (NonSpatialSessionType)type), target)
                : SessionReply.Fail("refused-bad-request");

        public SessionReply CarrierKey(UUID sessionId, UUID agent)
        {
            string key = _engine.CarrierKey(sessionId, agent);
            return new SessionReply { Ok = key != null, Decision = key != null ? "carrier-key" : SessionOutcome.NoSuchSession, Text = key };
        }

        public SessionReply IssueToken(UUID sessionId, UUID agent)
        {
            string token = _engine.IssueToken(sessionId, agent);
            return new SessionReply { Ok = token != null, Decision = token != null ? "token" : SessionOutcome.NoSuchSession, Text = token };
        }

        public SessionReply TakeConferenceRequest(UUID agent)
        {
            bool ok = _engine.TryTakeConferenceRequest(agent);
            return new SessionReply { Ok = true, Decision = ok ? "taken" : SessionOutcome.RateLimited, Flag = ok };
        }

        public SessionReply Get(UUID sessionId, UUID agent = default)
        {
            _engine.Get(sessionId);   // a call whose time ran out ends before it is read
            SessionData d = Snapshot(sessionId, agent);
            return d == null ? SessionReply.Fail(SessionOutcome.NoSuchSession) : new SessionReply { Ok = true, Decision = "found", Session = d };
        }

        public SessionReply GetByRoomKey(string roomKey, UUID agent = default)
        {
            NonSpatialVoiceSession s = _store.GetByRoomKey(roomKey);
            return s == null ? SessionReply.Fail(SessionOutcome.NoSuchSession) : Get(s.SessionId, agent);
        }

        // ---- upkeep ----------------------------------------------------------------------------

        /// <summary>What one sweep did.</summary>
        public readonly struct SweepResult
        {
            public int LeasesExpired { get; }
            public int InvitationsExpired { get; }
            public int SessionsRemoved { get; }
            public int SeatsReleased { get; }

            public SweepResult(int leases, int invitations, int sessions, int seats)
            {
                LeasesExpired = leases;
                InvitationsExpired = invitations;
                SessionsRemoved = sessions;
                SeatsReleased = seats;
            }
        }

        /// <summary>
        /// Release seats nobody has renewed, then lapse invitations and collect idle and dead sessions.
        /// Run on a timer by the Robust connector.
        /// </summary>
        public SweepResult Sweep()
        {
            int leases = ExpireSeatLeases();
            NonSpatialVoiceSessionEngine.SweepSummary s = _engine.SweepWithSummary();
            _engine.PruneTransientState();
            PruneRenewals();
            return new SweepResult(leases, s.InvitationsExpired, s.Removed.Count, s.SeatsReleased);
        }

        private int ExpireSeatLeases()
        {
            DateTime now = _clock();
            int released = 0;
            foreach (NonSpatialVoiceSession s in _store.All())
            {
                List<KeyValuePair<UUID, DateTime>> seated = _store.Mutate(s.SessionId, sess =>
                {
                    List<KeyValuePair<UUID, DateTime>> list = new List<KeyValuePair<UUID, DateTime>>();
                    foreach (NonSpatialMember m in sess.Members)
                        if (m.HoldsSeat) list.Add(new KeyValuePair<UUID, DateTime>(m.AgentId, m.LastSeenUtc));
                    return list;
                }, null);
                if (seated == null) continue;
                foreach (KeyValuePair<UUID, DateTime> seat in seated)
                {
                    DateTime last = seat.Value;
                    lock (_renewed)
                        if (_renewed.TryGetValue(new SeatKey(s.SessionId, seat.Key), out DateTime renewed) && renewed > last)
                            last = renewed;
                    if (now - last >= _seatLease && _engine.Depart(s.SessionId, seat.Key, DepartureReason.PresenceLost).Ok)
                        released++;
                }
            }
            return released;
        }

        private bool IsRenewed(UUID agent)
        {
            lock (_renewed)
                return _renewedAgent.TryGetValue(agent, out DateTime at) && _clock() - at < _seatLease;
        }

        private void PruneRenewals()
        {
            DateTime now = _clock();
            lock (_renewed)
            {
                List<SeatKey> stale = new List<SeatKey>();
                foreach (KeyValuePair<SeatKey, DateTime> kv in _renewed)
                    if (now - kv.Value > _seatLease + _seatLease)
                        stale.Add(kv.Key);
                foreach (SeatKey k in stale) _renewed.Remove(k);
                List<UUID> staleAgents = new List<UUID>();
                foreach (KeyValuePair<UUID, DateTime> kv in _renewedAgent)
                    if (now - kv.Value > _seatLease + _seatLease)
                        staleAgents.Add(kv.Key);
                foreach (UUID k in staleAgents) _renewedAgent.Remove(k);
            }
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static bool IsStartableType(int type)
            => type == (int)NonSpatialSessionType.P2P || type == (int)NonSpatialSessionType.Group;

        private static bool IsReason(int reason) => Enum.IsDefined(typeof(DepartureReason), reason);

        /// <summary>
        /// A copy of the session for <paramref name="reader"/>. The call's credential is left out unless the
        /// reader is a member of the session or was invited to it: anyone who can reach the service can ask
        /// for a session, and the credential is what admits a viewer to the call's audio.
        /// </summary>
        private SessionData Snapshot(UUID sessionId, UUID reader)
            => _store.Mutate<SessionData>(sessionId, s =>
            {
                SessionData d = SessionWire.ToData(s);
                if (reader == UUID.Zero || (s.Find(reader) == null && !s.WasInvited(reader)))
                    d.Token = null;
                return d;
            }, null);

        private SessionReply Reply(SessionOutcome o, UUID reader)
            => new SessionReply
            {
                Ok = o.Ok,
                Decision = o.Decision,
                Created = o.Created,
                StartedRinging = o.StartedRinging,
                Session = o.Session == null ? null : Snapshot(o.Session.SessionId, reader),
            };

        private SessionReply Many(IReadOnlyList<NonSpatialVoiceSession> touched, UUID reader)
        {
            SessionReply r = new SessionReply { Ok = true, Decision = "departed", Sessions = new List<SessionData>(), Count = touched.Count };
            foreach (NonSpatialVoiceSession s in touched)
            {
                SessionData d = Snapshot(s.SessionId, reader);
                if (d != null) r.Sessions.Add(d);
            }
            return r;
        }

        /// <summary>
        /// Group admission as far as the service can judge it. Whether the agent is in the group and may
        /// join its calls needs the groups service, which only a region has, so the region decides that
        /// before it asks. What is checked here is the shape: a group call has an owning group, and is
        /// never invited into.
        /// </summary>
        private sealed class RegionCheckedGroupAdmission : INonSpatialAdmission
        {
            public NonSpatialSessionType Type => NonSpatialSessionType.Group;

            public AdmissionVerdict CanStart(UUID creator, UUID owner, IReadOnlyList<UUID> invitees)
                => owner != UUID.Zero && creator != UUID.Zero
                    ? AdmissionVerdict.Allow("group-admitted")
                    : AdmissionVerdict.Deny(AdmissionVerdict.WrongType);

            public AdmissionVerdict CanInvite(NonSpatialVoiceSession session, UUID inviter, UUID invitee)
                => AdmissionVerdict.Deny(AdmissionVerdict.WrongType);

            public AdmissionVerdict CanJoin(NonSpatialVoiceSession session, UUID agent)
                => session != null && agent != UUID.Zero
                    ? AdmissionVerdict.Allow("group-member")
                    : AdmissionVerdict.Deny(AdmissionVerdict.NotAMember);
        }
    }
}
