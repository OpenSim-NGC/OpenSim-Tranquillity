/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The grid-wide call session service: the non-spatial session engine's own operations (start, invite,
 * accept, depart and the rest) run in one place, so the seat cap, the ring-once latch and the start
 * races are decided once for the whole grid. Every operation is atomic inside the service and
 * answers with a SessionReply (the outcome and a copy of the session it touched).
 *
 * Two implementations: VoiceSessionService (the engine, in the process that hosts it) and
 * VoiceSessionServicesConnector (a region's HTTP client for it).
 */
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>One seat: the agent's place in one session.</summary>
    public readonly record struct SeatKey(UUID Session, UUID Agent);

    public interface IVoiceSessionService
    {
        SessionReply Start(int type, UUID creator, UUID otherOrOwner, UUID tempSessionId, UUID originRegion, int requestedCap);
        SessionReply StartAdhoc(UUID creator, UUID tempSessionId, UUID originRegion, IReadOnlyList<UUID> invitees, int requestedCap);
        SessionReply CheckInviteCount(UUID sessionId, UUID inviter, IReadOnlyList<UUID> asked);
        SessionReply Invite(UUID sessionId, UUID inviter, UUID invitee);
        SessionReply Accept(UUID sessionId, UUID agent, UUID originRegion, string viewerSession);
        SessionReply MarkPresent(UUID sessionId, UUID agent, UUID originRegion, string viewerSession);
        SessionReply Decline(UUID sessionId, UUID agent);
        SessionReply Depart(UUID sessionId, UUID agent, int reason);
        SessionReply LeaveChat(UUID sessionId, UUID agent);
        SessionReply DepartByViewerSession(UUID agent, string viewerSession, int reason);
        SessionReply DepartAll(UUID agent, int reason);

        /// <summary>Release every seat that names this region as where it was taken. Count = seats released.</summary>
        SessionReply DepartRegion(UUID region);

        /// <summary>
        /// The region still holds these seats; their leases restart. A seat that is not named keeps the lease it had,
        /// whoever else is named. Count = seats recorded.
        /// </summary>
        SessionReply RenewSeats(IReadOnlyList<SeatKey> seats);

        SessionReply MarkInvited(UUID sessionId, IReadOnlyList<UUID> agents);

        /// <summary>
        /// A region received a ring naming this session for <paramref name="target"/> from <paramref name="claimedCaller"/>.
        /// Answers yes (with the session as the target may see it, credential included) only for a session of that type
        /// whose ring was sent, whose caller holds a seat, and in which the target was marked invited and has not
        /// already used the ring. A yes uses the ring up.
        /// </summary>
        SessionReply VerifyRing(UUID sessionId, UUID target, UUID claimedCaller, int type);

        /// <summary>
        /// The key a conference's text lines are signed with, for an agent who holds a seat in it. Text = the key.
        /// Refused for anyone else, so a forger who is not in the conference never has it.
        /// </summary>
        SessionReply CarrierKey(UUID sessionId, UUID agent);

        SessionReply IssueToken(UUID sessionId, UUID agent);
        SessionReply TakeConferenceRequest(UUID agent);
        /// <summary>
        /// The session as the named agent may see it. The call's credential is in the copy only when that
        /// agent is a member of the session or was invited to it; with no agent (or a stranger) it is left out.
        /// </summary>
        SessionReply Get(UUID sessionId, UUID agent = default);
        SessionReply GetByRoomKey(string roomKey, UUID agent = default);
    }
}
