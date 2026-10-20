/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The "call" handler for a GROUP session.
 *
 * This is the EXISTING call shape, reused, not new machinery. The viewer's group voice channel does
 * exactly what the P2P one does:
 *     LLVoiceChannelGroup::activate -> requestChannelInfo (llvoicechannel.cpp:492-502)
 *       -> voiceCallCapCoro posts {method:"call", session-id, alt_params} (:631)
 *       -> setChannelInfo(result["voice_credentials"]) (:687)
 * and for a group `session-id` IS the group id, because computeSessionID slams it
 * (llimview.cpp:2535-2538). So the only difference from A2A is which authority answers: the group
 * service rather than the pair registry.
 *
 * WHY THIS IS A WRAPPER AND NOT AN EDIT TO ChatSessionRequestLogic. The A2A path has to stay
 * provably unchanged. So the module tries this handler FIRST, and it
 * declines -- returning false, touching nothing -- unless every one of these holds:
 *     group voice is enabled, the method is "call", and the `session-id` names a group this agent is
 *     a member of.
 * On a decline the module calls ChatSessionRequestLogic.Decide exactly as before, byte for byte.
 * A non-member's "call" therefore still reaches the A2A arm and still 404s there, which is the
 * answer an unknown session always got, and what the viewer already knows how to survive.
 *
 * WHAT IT HANDS BACK is the same map the A2A arm builds (ChatSessionRequestLogic.cs), with
 * one deliberate difference: channel_uri is NonSpatialRoomKey.Derive(grid, Group, groupId), NOT the
 * session id. The viewer treats it as opaque, and the mixer hashes it as the multiagent
 * channel, so the group's room is distinct from every A2A room and from every parcel room without
 * any change to JanusAudioBridge.
 */
using System;
using System.Net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    public static class GroupVoiceChatSession
    {
        public const string MethodCall = "call";

        /// <summary>
        /// The group/adhoc accept path posts THIS, not "call": Accept on the popup
        /// runs chatterBoxInvitationCoro, which POSTs {method:"accept invitation", session-id} and
        /// calls startCall(voice_channel_info) only when it succeeds (llimview.cpp:3382-3385 and the
        /// coroutine tail). We answered 400 for it -- no case in ChatSessionRequestLogic.Decide -- so
        /// the accept failed, the invitation was cleared and no call started. Without this handler
        /// the popup would be decorative.
        /// </summary>
        public const string MethodAcceptInvitation = "accept invitation";

        public const string DecisionCallAdmitted = "group-call-admitted";
        public const string DecisionAcceptAdmitted = "group-accept-admitted";
        public const string DecisionNotMember = "group-refused-not-a-member";
        public const string DecisionNoPower = "group-refused-no-power";
        public const string DecisionFull = "group-refused-capacity";

        /// <summary>The agent was removed from this call and is not let back in until the removal ends.</summary>
        public const string DecisionRemoved = "group-refused-removed";
        public const string InstrumentTag = "[GROUP VOICE]";

        /// <summary>
        /// Answer "accept invitation" for a group session. Returns false -- untouched -- for anything
        /// else, so the A2A path keeps its behaviour, including its 400 for an accept it does not know.
        ///
        /// The reply is 200 with no body: the coroutine feeds the body to LLIMSpeakerMgr::setSpeakers,
        /// which tolerates an empty map, and the participant surface is
        /// ChatterBoxSessionAgentListUpdates rather than this reply. What matters is the STATUS --
        /// any failure makes the coroutine clear the invitation and never call startCall.
        /// </summary>
        public static bool TryHandleAcceptInvitation(OSDMap reqmap, UUID agentID, GroupVoicePolicy policy,
                                                     INonSpatialSessions engine,
                                                     out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (policy == null || !policy.IsUsable || engine == null || reqmap == null)
                return false;
            if (!reqmap.TryGetString("method", out string method)
                || !string.Equals(method, MethodAcceptInvitation, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!reqmap.TryGetUUID("session-id", out UUID groupID) || groupID == UUID.Zero)
                return false;
            // Ours only if this names a live GROUP session the agent belongs to; an accept for an
            // ad-hoc or an unknown session falls through untouched.
            NonSpatialVoiceSession s = engine.Get(groupID, agentID, out bool unavailable);
            if (s == null && unavailable && policy.IsMember(agentID, groupID))
            {
                // The service could not say whether this session exists, so the viewer is told to try again.
                outcome = Refuse((HttpStatusCode)NonSpatialCaps.StoreUnavailableHttpStatus, agentID, groupID,
                                 "group-refused-" + SessionOutcome.StoreUnavailable, SessionOutcome.StoreUnavailable);
                return true;
            }
            if (s == null || s.Type != NonSpatialSessionType.Group || !policy.IsMember(agentID, s.Owner))
                return false;

            if (!policy.HasRequiredPowers(agentID, s.Owner))
            {
                outcome = Refuse(HttpStatusCode.Forbidden, agentID, s.Owner, DecisionNoPower,
                                 "accept invitation without GP_SESSION_JOIN/GP_SESSION_VOICE");
                return true;
            }

            // Accepting the popup IS the join: the viewer goes straight to startCall with the
            // invitation channel info and never posts "call", so this is the only place a
            // popup-accepted member takes its seat.
            SessionOutcome seat = engine.Accept(s.SessionId, agentID, UUID.Zero);
            if (!seat.Ok)
            {
                HttpStatusCode code = (HttpStatusCode)NonSpatialCaps.RefusalStatus(seat.Decision);
                string word = seat.Decision == SessionOutcome.Capacity ? DecisionFull
                    : seat.Decision == SessionOutcome.Removed ? DecisionRemoved : "group-refused-" + seat.Decision;
                outcome = Refuse(code, agentID, s.Owner, word, seat.Decision);
                return true;
            }

            outcome = new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                StartedRinging = seat.StartedRinging,
                Instrument = Line(agentID, s.Owner, DecisionAcceptAdmitted,
                                  "room=" + s.RoomKey + " seats=" + (engine.Get(s.SessionId)?.SeatsHeld) + "/" + s.Cap),
            };
            return true;
        }

        /// <summary>
        /// Try to answer a ChatSessionRequest as a group voice call. Returns false -- having done
        /// NOTHING -- when this is not one, so the caller falls through to the A2A path unchanged.
        /// </summary>
        public static bool TryHandleCall(OSDMap reqmap, UUID agentID, GroupVoicePolicy policy,
                                         INonSpatialSessions engine, UUID originRegion,
                                         out ChatSessionOutcome outcome)
        {
            outcome = null;
            if (policy == null || !policy.IsUsable || engine == null || reqmap == null)
                return false;
            if (!reqmap.TryGetString("method", out string method) || !string.Equals(method, MethodCall, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!reqmap.TryGetUUID("session-id", out UUID groupID) || groupID == UUID.Zero)
                return false;
            // The discriminator: a group session-id is a group this agent belongs to. Anything else
            // is not ours and must fall through untouched.
            if (!policy.IsMember(agentID, groupID))
                return false;

            outcome = Call(agentID, groupID, policy, engine, originRegion);
            return true;
        }

        private static ChatSessionOutcome Call(UUID agentID, UUID groupID, GroupVoicePolicy policy,
                                               INonSpatialSessions engine, UUID originRegion)
        {
            // The power gate. A member without it is a 403, which the viewer shows as VoiceNotAllowed
            // (llvoicechannel.cpp:658-663) -- the correct, survivable answer for "you may not".
            if (!policy.HasRequiredPowers(agentID, groupID))
                return Refuse(HttpStatusCode.Forbidden, agentID, groupID, DecisionNoPower, "missing GP_SESSION_JOIN/GP_SESSION_VOICE");

            // Start is idempotent by derivation (the session id IS the group id), so the first caller
            // opens the room and every later one finds it.
            SessionOutcome start = engine.Start(NonSpatialSessionType.Group, agentID, groupID, groupID, originRegion, policy.Cap);
            if (!start.Ok)
                return Refuse((HttpStatusCode)NonSpatialCaps.RefusalStatus(start.Decision), agentID, groupID, "group-refused-" + start.Decision, start.Decision);

            // Take the seat HERE, at "call", not at provision: with no invitation step this is the
            // first moment the grid knows this agent intends to be in the room, and refusing here
            // means the viewer never builds a PeerConnection it is going to be denied.
            SessionOutcome seat = engine.Accept(start.Session.SessionId, agentID, originRegion);
            if (!seat.Ok)
            {
                // Capacity is the one refusal with a required status: 409 Conflict, which the viewer
                // maps to ERROR_CHANNEL_FULL, the same thing the mixer's own 495 produces.
                HttpStatusCode code = (HttpStatusCode)NonSpatialCaps.RefusalStatus(seat.Decision);
                string word = seat.Decision == SessionOutcome.Capacity ? DecisionFull
                    : seat.Decision == SessionOutcome.Removed ? DecisionRemoved : "group-refused-" + seat.Decision;
                return Refuse(code, agentID, groupID, word, seat.Decision);
            }

            string token = engine.IssueToken(start.Session.SessionId, agentID);
            OSDMap creds = new OSDMap
            {
                ["voice_server_type"] = OSD.FromString(A2AInvitation.VoiceServerType),
                ["channel_uri"] = OSD.FromString(start.Session.RoomKey),
                ["channel_credentials"] = OSD.FromString(token),
            };
            return new ChatSessionOutcome
            {
                Status = HttpStatusCode.OK,
                // Either half can be the first seat: Start seats the opener, Accept seats everyone
                // else. Only one of them can ever claim the ring, so OR is safe.
                StartedRinging = start.StartedRinging || seat.StartedRinging,
                Body = new OSDMap { ["voice_credentials"] = creds },
                Instrument = Line(agentID, groupID, DecisionCallAdmitted,
                                  $"room={start.Session.RoomKey} seats={engine.Get(start.Session.SessionId)?.SeatsHeld}/{start.Session.Cap}"),
            };
        }

        private static ChatSessionOutcome Refuse(HttpStatusCode status, UUID agentID, UUID groupID, string decision, string detail)
            => new ChatSessionOutcome
            {
                Status = status,
                Instrument = Line(agentID, groupID, decision, detail),
            };

        /// <summary>One greppable line per decision, the house instrument convention. No token is ever logged.</summary>
        public static string Line(UUID agentID, UUID groupID, string decision, string detail)
            => $"{InstrumentTag} agent={agentID} group={groupID} decision={decision}" +
               (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
