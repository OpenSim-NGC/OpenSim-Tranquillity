/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>
    /// The grid call session service behind a circuit breaker (see SessionCircuitBreaker). While the breaker is
    /// open a call returns null, "no answer", at once and the service is not touched. A null from the service
    /// counts as a failure; any other reply, refusals included, counts as an answer.
    /// </summary>
    public sealed class GuardedVoiceSessionService : IVoiceSessionService
    {
        private readonly IVoiceSessionService _inner;
        private readonly SessionCircuitBreaker _breaker;

        public GuardedVoiceSessionService(IVoiceSessionService inner, SessionCircuitBreaker breaker)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        }

        private SessionReply Run(Func<SessionReply> call)
        {
            if (!_breaker.TryEnter())
                return null;
            SessionReply reply;
            try
            {
                reply = call();
            }
            catch
            {
                _breaker.RecordFailure();
                return null;
            }
            if (reply == null) _breaker.RecordFailure();
            else _breaker.RecordSuccess();
            return reply;
        }

        public SessionReply Start(int type, UUID creator, UUID otherOrOwner, UUID tempSessionId, UUID originRegion, int requestedCap)
            => Run(() => _inner.Start(type, creator, otherOrOwner, tempSessionId, originRegion, requestedCap));

        public SessionReply StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion, IReadOnlyList<UUID> invitees, int requestedCap)
            => Run(() => _inner.StartAdhoc(creator, tempSessionId, originRegion, invitees, requestedCap));

        public SessionReply CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyList<UUID> asked)
            => Run(() => _inner.CheckInviteCount(sessionId, inviter, asked));

        public SessionReply Invite(UUID sessionId, UUID inviter, UUID invitee)
            => Run(() => _inner.Invite(sessionId, inviter, invitee));

        public SessionReply Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession)
            => Run(() => _inner.Accept(sessionId, agent, originRegion, viewerSession));

        public SessionReply MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession)
            => Run(() => _inner.MarkPresent(sessionId, agent, originRegion, viewerSession));

        public SessionReply Decline(UUID sessionId, UUID agent)
            => Run(() => _inner.Decline(sessionId, agent));

        public SessionReply Depart(UUID sessionId, UUID agent, int reason)
            => Run(() => _inner.Depart(sessionId, agent, reason));

        public SessionReply LeaveChat(UUID sessionId, UUID agent)
            => Run(() => _inner.LeaveChat(sessionId, agent));

        public SessionReply DepartByViewerSession(UUID agent, string viewerSession, int reason)
            => Run(() => _inner.DepartByViewerSession(agent, viewerSession, reason));

        public SessionReply DepartAll(UUID agent, int reason)
            => Run(() => _inner.DepartAll(agent, reason));

        public SessionReply DepartRegion(UUID region)
            => Run(() => _inner.DepartRegion(region));

        public SessionReply RenewSeats(IReadOnlyList<SeatKey> seats)
            => Run(() => _inner.RenewSeats(seats));

        public SessionReply MarkInvited(UUID sessionId, IReadOnlyList<UUID> agents)
            => Run(() => _inner.MarkInvited(sessionId, agents));

        public SessionReply VerifyRing(UUID sessionId, UUID target, UUID claimedCaller, int type)
            => Run(() => _inner.VerifyRing(sessionId, target, claimedCaller, type));

        public SessionReply CarrierKey(UUID sessionId, UUID agent)
            => Run(() => _inner.CarrierKey(sessionId, agent));

        public SessionReply IssueToken(UUID sessionId, UUID agent)
            => Run(() => _inner.IssueToken(sessionId, agent));

        public SessionReply TakeConferenceRequest(UUID agent)
            => Run(() => _inner.TakeConferenceRequest(agent));

        public SessionReply Get(UUID sessionId, UUID agent = default)
            => Run(() => _inner.Get(sessionId, agent));

        public SessionReply GetByRoomKey(string roomKey, UUID agent = default)
            => Run(() => _inner.GetByRoomKey(roomKey, agent));
    }
}
