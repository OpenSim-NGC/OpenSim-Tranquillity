/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The region's side of the grid call session store: the same operations as the in-process engine
 * (INonSpatialSessions), answered by the grid session service.
 *
 * Admission stays here, because the group checks need the groups service, which only a region has.
 * The region decides whether to ask; the service decides the cap, the ring latch, idempotency and
 * state. When the service does not answer, a request is REFUSED (refused-store-unavailable). There is
 * no fallback to a region-local store: a region that started its own record of a call would ring the
 * group a second time and count the seat cap on its own, which is what the grid store exists to stop.
 *
 * Sessions returned are copies. Reading them is what the region does; changes go through the operations.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class GridNonSpatialSessions : INonSpatialSessions
    {
        private readonly IVoiceSessionService _service;
        private readonly Dictionary<NonSpatialSessionType, INonSpatialAdmission> _admission = new Dictionary<NonSpatialSessionType, INonSpatialAdmission>();
        private readonly RefusalLogGate _unavailableGate = new RefusalLogGate();
        private readonly Func<DateTime> _clock;
        private readonly DepartOutbox _outbox;

        // The seats this region took and has not let go of: the seat, and the viewer session it was tagged with. These
        // are the only seats the region renews. An entry goes when the region departs the seat (or declines the
        // invitation), whether or not the service received that, so a departure that was lost cannot leave a seat
        // that keeps being renewed.
        private readonly object _heldLock = new object();
        private readonly Dictionary<SeatKey, string> _held = new Dictionary<SeatKey, string>();

        /// <summary>Raised at most once a minute when the service did not answer. Carries the operation name.</summary>
        public event Action<string> Unavailable;

        public GridNonSpatialSessions(IVoiceSessionService service, IEnumerable<INonSpatialAdmission> admissions, Func<DateTime> clock = null,
                                      DepartOutbox outbox = null)
        {
            _outbox = outbox;
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _clock = clock ?? (() => DateTime.UtcNow);
            if (admissions != null)
                foreach (INonSpatialAdmission a in admissions)
                    if (a != null) _admission[a.Type] = a;
        }

        public NonSpatialVoiceSession Get(UUID sessionId, UUID agent = default)
        {
            SessionReply r = Reached(_service.Get(sessionId, agent), "get");
            return r != null && r.Ok ? SessionWire.FromData(r.Session) : null;
        }

        public NonSpatialVoiceSession Get(UUID sessionId, UUID agent, out bool unavailable)
        {
            SessionReply r = Reached(_service.Get(sessionId, agent), "get");
            unavailable = r == null;
            return r != null && r.Ok ? SessionWire.FromData(r.Session) : null;
        }

        public NonSpatialVoiceSession GetByRoomKey(string roomKey, UUID agent = default)
        {
            SessionReply r = Reached(_service.GetByRoomKey(roomKey, agent), "getbyroomkey");
            return r != null && r.Ok ? SessionWire.FromData(r.Session) : null;
        }

        public NonSpatialVoiceSession GetByRoomKey(string roomKey, UUID agent, out bool unavailable)
        {
            SessionReply r = Reached(_service.GetByRoomKey(roomKey, agent), "getbyroomkey");
            unavailable = r == null;
            return r != null && r.Ok ? SessionWire.FromData(r.Session) : null;
        }

        public SessionOutcome Start(NonSpatialSessionType type, UUID creator, UUID otherOrOwner,
                                    UUID tempSessionId, UUID originRegion, int requestedCap = 0)
        {
            if (type == NonSpatialSessionType.Adhoc)
                throw new ArgumentException("use StartAdhoc: an ad-hoc id is server-owned and keyed on the viewer's temp id", nameof(type));
            List<UUID> invitees = type == NonSpatialSessionType.P2P ? new List<UUID> { otherOrOwner } : new List<UUID>();
            AdmissionVerdict v = Admission(type).CanStart(creator, type == NonSpatialSessionType.Group ? otherOrOwner : UUID.Zero, invitees);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);
            return SeatTaken(Outcome(_service.Start((int)type, creator, otherOrOwner, tempSessionId, originRegion, requestedCap), "start"), creator);
        }

        public SessionOutcome StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion,
                                         IReadOnlyList<UUID> invitees = null, int requestedCap = 0)
        {
            AdmissionVerdict v = Admission(NonSpatialSessionType.Adhoc).CanStart(creator, UUID.Zero, invitees ?? new List<UUID>());
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision);
            return SeatTaken(Outcome(_service.StartAdhoc(creator, tempSessionId, originRegion, invitees ?? new List<UUID>(), requestedCap), "startadhoc"), creator);
        }

        public SessionOutcome CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyCollection<UUID> asked)
            => Outcome(_service.CheckInviteCount(sessionId, inviter, new List<UUID>(asked ?? new List<UUID>())), "checkinvitecount");

        public SessionOutcome Invite(UUID sessionId, UUID inviter, UUID invitee)
        {
            SessionReply found = Reached(_service.Get(sessionId), "invite");
            if (found == null) return SessionOutcome.Fail(SessionOutcome.StoreUnavailable);
            if (!found.Ok) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            NonSpatialVoiceSession snapshot = SessionWire.FromData(found.Session);
            AdmissionVerdict v = Admission(snapshot.Type).CanInvite(snapshot, inviter, invitee);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, snapshot);
            return Outcome(_service.Invite(sessionId, inviter, invitee), "invite");
        }

        public SessionOutcome Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
        {
            SessionReply found = Reached(_service.Get(sessionId), "accept");
            if (found == null) return SessionOutcome.Fail(SessionOutcome.StoreUnavailable);
            if (!found.Ok) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            NonSpatialVoiceSession snapshot = SessionWire.FromData(found.Session);
            AdmissionVerdict v = Admission(snapshot.Type).CanJoin(snapshot, agent);
            if (!v.Admitted) return SessionOutcome.Fail(v.Decision, snapshot);
            return SeatTaken(Outcome(_service.Accept(sessionId, agent, originRegion, viewerSession), "accept"), agent);
        }

        public SessionOutcome MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession = null)
            => SeatTaken(Outcome(_service.MarkPresent(sessionId, agent, originRegion, viewerSession), "markpresent"), agent);

        public SessionOutcome Decline(UUID sessionId, UUID agent)
        {
            Forget(sessionId, agent);
            return Outcome(_service.Decline(sessionId, agent), "decline");
        }

        // A departure the service did not receive is kept in the outbox, when the region has one, and sent when the
        // service answers again. The caller is told the service was unavailable all the same.
        public SessionOutcome Depart(UUID sessionId, UUID agent, DepartureReason reason)
        {
            Forget(sessionId, agent);
            SessionReply r = Reached(_service.Depart(sessionId, agent, (int)reason), "depart");
            if (r == null) _outbox?.EnqueueDepart(sessionId, agent, (int)reason);
            return SessionWire.ToOutcome(r);
        }

        public SessionOutcome LeaveChat(UUID sessionId, UUID agent)
        {
            SessionReply r = Reached(_service.LeaveChat(sessionId, agent), "leavechat");
            if (r == null) _outbox?.EnqueueLeaveChat(sessionId, agent);
            return SessionWire.ToOutcome(r);
        }

        public IReadOnlyList<NonSpatialVoiceSession> DepartByViewerSession(UUID agent, string viewerSession, DepartureReason reason)
        {
            if (string.IsNullOrEmpty(viewerSession)) return new List<NonSpatialVoiceSession>();
            ForgetByViewerSession(agent, viewerSession);
            SessionReply r = Reached(_service.DepartByViewerSession(agent, viewerSession, (int)reason), "departbyviewer");
            if (r == null) _outbox?.EnqueueDepartByViewerSession(agent, viewerSession, (int)reason);
            return SessionWire.ToSessions(r);
        }

        public IReadOnlyList<NonSpatialVoiceSession> DepartAll(UUID agent, DepartureReason reason)
        {
            ForgetAll(agent);
            SessionReply r = Reached(_service.DepartAll(agent, (int)reason), "departall");
            if (r == null) _outbox?.EnqueueDepartAll(agent, (int)reason);
            return SessionWire.ToSessions(r);
        }

        /// <summary>
        /// There is nothing to adopt from a ring: every region sees the one record in the service. A ring for a
        /// session the service holds is accepted as already held; a ring for one it does not is refused. State is
        /// never created or changed from what a ring carries.
        /// </summary>
        public SessionOutcome AdoptRemoteRing(UUID groupId, string carriedRoomKey, string token, int cap,
                                              UUID creator, IEnumerable<UUID> alreadyRung,
                                              NonSpatialSessionType type = NonSpatialSessionType.Group)
        {
            SessionReply found = Reached(_service.Get(groupId), "adoptring");
            if (found == null) return SessionOutcome.Fail(SessionOutcome.StoreUnavailable);
            if (!found.Ok || found.Session == null) return SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            NonSpatialVoiceSession s = SessionWire.FromData(found.Session);
            if (s == null || s.Type != type) return SessionOutcome.Fail("refused-session-type-mismatch");
            if (!string.Equals(s.RoomKey, carriedRoomKey, StringComparison.Ordinal))
                return SessionOutcome.Fail(string.IsNullOrEmpty(carriedRoomKey) ? "refused-room-key-missing" : "refused-room-key-mismatch");
            return new SessionOutcome(true, "ring-session-held", s);
        }

        public bool StoreVerifiesRings => true;

        public bool StoreIssuesCarrierKeys => true;

        // A conference's key does not change while it lasts, and a text line asks for it once per target, so the keys this
        // region has been given are kept for a while, and so is a refusal (briefly), so lines naming sessions the sender is
        // not in cost the service one question and not one per line. An unreachable service is not kept.
        private static readonly TimeSpan KeyKept = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan NoKeyKept = TimeSpan.FromSeconds(5);
        private const int MaxKeptKeys = 1024;
        private readonly object _keyLock = new object();
        private readonly Dictionary<SeatKey, (string Key, DateTime Until)> _keys = new Dictionary<SeatKey, (string, DateTime)>();

        public string CarrierKey(UUID sessionId, UUID agent)
        {
            SeatKey asked = new SeatKey(sessionId, agent);
            DateTime now = _clock();
            lock (_keyLock)
                if (_keys.TryGetValue(asked, out (string Key, DateTime Until) kept) && kept.Until > now)
                    return kept.Key;

            SessionReply r = Reached(_service.CarrierKey(sessionId, agent), "carrierkey");
            if (r == null)
                return null;
            string key = r.Ok && !string.IsNullOrEmpty(r.Text) ? r.Text : null;
            lock (_keyLock)
            {
                if (_keys.Count >= MaxKeptKeys)
                {
                    List<SeatKey> gone = new List<SeatKey>();
                    foreach (KeyValuePair<SeatKey, (string Key, DateTime Until)> kv in _keys)
                        if (kv.Value.Until <= now) gone.Add(kv.Key);
                    foreach (SeatKey g in gone) _keys.Remove(g);
                    if (_keys.Count >= MaxKeptKeys) _keys.Clear();
                }
                _keys[asked] = (key, now + (key == null ? NoKeyKept : KeyKept));
            }
            return key;
        }

        public SessionOutcome VerifyRing(UUID sessionId, UUID target, UUID claimedCaller, NonSpatialSessionType type)
            => Outcome(_service.VerifyRing(sessionId, target, claimedCaller, (int)type), "verifyring");

        public SessionOutcome Moderate(UUID sessionId, UUID by, ModerationOp op, UUID target, bool on,
                                       string reason = null, TimeSpan? lifetime = null)
        {
            // Whole minutes, rounded up, so a removal never ends sooner than asked.
            int minutes = lifetime is TimeSpan l && l > TimeSpan.Zero ? (int)Math.Min(int.MaxValue, Math.Ceiling(l.TotalMinutes)) : 0;
            return Outcome(_service.Moderate(sessionId, by, (int)op, target, on, reason, minutes), "moderate");
        }

        public bool IsRemoved(NonSpatialVoiceSession session, UUID agent) => session != null && session.IsRemoved(agent, _clock());

        public IReadOnlyList<NonSpatialVoiceSession> Listing()
        {
            List<NonSpatialVoiceSession> calls = new List<NonSpatialVoiceSession>();
            HashSet<UUID> seen = new HashSet<UUID>();
            foreach (SeatKey seat in HeldSeats())
                if (seen.Add(seat.Session) && Get(seat.Session) is NonSpatialVoiceSession s)
                    calls.Add(s);
            return calls;
        }

        public string IssueToken(UUID sessionId, UUID agent) => IssueToken(sessionId, agent, out _);

        public string IssueToken(UUID sessionId, UUID agent, out bool unavailable)
        {
            SessionReply r = Reached(_service.IssueToken(sessionId, agent), "issuetoken");
            unavailable = r == null;
            return r != null && r.Ok ? r.Text : null;
        }

        public void MarkInvited(UUID sessionId, IEnumerable<UUID> agents)
        {
            if (agents == null) return;
            Reached(_service.MarkInvited(sessionId, new List<UUID>(agents)), "markinvited");
        }

        /// <summary>False when the agent's bucket is empty, and also when the service did not answer.</summary>
        public bool TryTakeConferenceRequest(UUID agent)
            => TryTakeConferenceRequest(agent, out _);

        public bool TryTakeConferenceRequest(UUID agent, out bool unavailable)
        {
            SessionReply r = Reached(_service.TakeConferenceRequest(agent), "takeconferencerequest");
            unavailable = r == null;
            return r != null && r.Flag;
        }

        // ---- lifecycle of the region itself -------------------------------------------------------

        /// <summary>
        /// A region process has started: release the seats that name it, which belong to viewers the restart
        /// disconnected. Returns the seats released, or -1 when the service did not answer.
        /// </summary>
        public int DepartRegion(UUID region)
        {
            SessionReply r = Reached(_service.DepartRegion(region), "departregion");
            return r != null && r.Ok ? r.Count : -1;
        }

        /// <summary>The seats this region took and still holds.</summary>
        public IReadOnlyList<SeatKey> HeldSeats()
        {
            lock (_heldLock)
                return new List<SeatKey>(_held.Keys);
        }

        /// <summary>
        /// Tell the service these seats are still held, so their leases restart. The service is asked in batches no
        /// larger than it reads. False when it did not answer.
        /// </summary>
        public bool RenewSeats(IReadOnlyList<SeatKey> seats) => RenewSeats(seats, out _);

        /// <summary>
        /// As <see cref="RenewSeats(IReadOnlyList{SeatKey})"/>, and says which of the seats the service reports as
        /// belonging to a member removed from the call. Those seats are no longer held here.
        /// </summary>
        public bool RenewSeats(IReadOnlyList<SeatKey> seats, out IReadOnlyList<SeatKey> removed)
        {
            List<SeatKey> gone = new List<SeatKey>();
            removed = gone;
            if (seats == null) return true;
            bool all = true;
            for (int from = 0; from < seats.Count; from += VoiceSessionService.MaxRenewSeats)
            {
                List<SeatKey> batch = new List<SeatKey>();
                for (int i = from; i < seats.Count && i < from + VoiceSessionService.MaxRenewSeats; i++)
                    batch.Add(seats[i]);
                SessionReply r = Reached(_service.RenewSeats(batch), "renewseats");
                all &= r != null && r.Ok;
                if (r?.RemovedSeats != null)
                    foreach (string pair in r.RemovedSeats)
                    {
                        string[] ids = (pair ?? string.Empty).Split(':');
                        if (ids.Length == 2 && UUID.TryParse(ids[0], out UUID session) && UUID.TryParse(ids[1], out UUID agent)
                            && batch.Contains(new SeatKey(session, agent)))
                        {
                            gone.Add(new SeatKey(session, agent));
                            Forget(session, agent);
                        }
                    }
            }
            return all;
        }

        // ---- helpers -----------------------------------------------------------------------------

        private INonSpatialAdmission Admission(NonSpatialSessionType type)
            => _admission.TryGetValue(type, out INonSpatialAdmission a)
                ? a
                : throw new InvalidOperationException("no admission policy registered for " + type);

        // The agent holds a seat again, so a departure queued for it earlier is out of date (see DepartOutbox.Supersede),
        // and the seat is one this region holds and renews.
        private SessionOutcome SeatTaken(SessionOutcome outcome, UUID agent)
        {
            if (outcome.Ok && outcome.Session != null)
            {
                _outbox?.Supersede(agent, outcome.Session.SessionId);
                NonSpatialMember member = outcome.Session.Find(agent);
                if (member != null && member.HoldsSeat)
                    lock (_heldLock)
                        _held[new SeatKey(outcome.Session.SessionId, agent)] = member.ViewerSession;
            }
            return outcome;
        }

        private void Forget(UUID sessionId, UUID agent)
        {
            lock (_heldLock)
                _held.Remove(new SeatKey(sessionId, agent));
        }

        private void ForgetByViewerSession(UUID agent, string viewerSession)
        {
            lock (_heldLock)
            {
                List<SeatKey> gone = new List<SeatKey>();
                foreach (KeyValuePair<SeatKey, string> held in _held)
                    if (held.Key.Agent == agent && string.Equals(held.Value, viewerSession, StringComparison.Ordinal))
                        gone.Add(held.Key);
                foreach (SeatKey k in gone) _held.Remove(k);
            }
        }

        private void ForgetAll(UUID agent)
        {
            lock (_heldLock)
            {
                List<SeatKey> gone = new List<SeatKey>();
                foreach (SeatKey k in _held.Keys)
                    if (k.Agent == agent)
                        gone.Add(k);
                foreach (SeatKey k in gone) _held.Remove(k);
            }
        }

        private SessionOutcome Outcome(SessionReply reply, string operation)
            => SessionWire.ToOutcome(Reached(reply, operation));

        private SessionReply Reached(SessionReply reply, string operation)
        {
            if (reply == null && _unavailableGate.ShouldLog(UUID.Zero, _clock()))
                Unavailable?.Invoke(operation);
            return reply;
        }
    }
}
