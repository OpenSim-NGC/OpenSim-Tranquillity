/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Ad-hoc conference invitations -- the ring, the "invite" that adds someone to a call already
 * running, and the "decline invitation" that unwinds a pending one.
 *
 * HOW AN ADHOC RING DIFFERS FROM A GROUP RING, which is less than it looks:
 *   - the TARGET LIST is the invited members of the voice session itself (the `params` array from
 *     "start conference", plus anyone added later by "invite"), NOT a group roster. An ad-hoc
 *     conference has no roster; being invited is the whole of the membership.
 *   - `invitation_type` is CONFERENCE_SESSION = 1, not GROUP_CHAT_SESSION = 0 (llimview.cpp:119-125).
 *     That is what makes LLIMMgr::inviteToSession fall past the group branch -- `gAgent.isInGroup`
 *     is false for a conference id -- and land on "VoiceInviteAdHoc" (:4163-4168).
 *   - `session_id` is the AUTHORITATIVE conference id, the one the start reply re-keyed the viewer
 *     to. Sending the viewer's temp id would address a voice session the invitee has never heard of.
 * Everything else is shared: the same `voice` map, the same A2AInviteDelivery locally, the same
 * GroupVoiceRingTransport carrier cross-instance, the same online-only filter, and the initiator is
 * never rung.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.NonSpatial
{
    public static class AdhocVoiceInvite
    {
        /// <summary>EMultiAgentChatSessionType::CONFERENCE_SESSION (llimview.cpp:122).</summary>
        public const int InvitationTypeConference = 1;

        public const string InstrumentTag = "[ADHOC INVITE]";

        /// <summary>
        /// The ChatterBoxInvitation body for an ad-hoc conference ring. Same shape as the group one;
        /// the two differences that matter are the invitation type and that session_id is the
        /// conference's authoritative id.
        /// </summary>
        public static OSDMap BuildBody(NonSpatialVoiceSession session, string token, UUID caller, string callerName)
        {
            if (session is null) throw new ArgumentNullException(nameof(session));
            if (session.Type != NonSpatialSessionType.Adhoc)
                throw new ArgumentException("ad-hoc invitation for a non-ad-hoc session", nameof(session));
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("invitation requires a minted token");

            OSDMap voice = new OSDMap
            {
                ["invitation_type"] = OSD.FromInteger(InvitationTypeConference),
                ["voice_server_type"] = OSD.FromString(A2AInvitation.VoiceServerType),
                ["channel_uri"] = OSD.FromString(session.RoomKey),
                ["channel_credentials"] = OSD.FromString(token),
            };
            return new OSDMap
            {
                // the AUTHORITATIVE conference id, not the viewer's temp id
                ["session_id"] = OSD.FromUUID(session.SessionId),
                ["session_name"] = OSD.FromString(string.IsNullOrEmpty(callerName) ? "Conference" : callerName),
                ["from_id"] = OSD.FromUUID(caller),
                ["from_name"] = OSD.FromString(callerName ?? string.Empty),
                ["voice"] = voice,
            };
        }

        /// <summary>
        /// Who to ring for this conference: members the voice session already lists as INVITED, minus the
        /// initiator, minus anyone seated, minus anyone rung this cycle.
        ///
        /// Deliberately NOT "everyone present who might want in": an ad-hoc conference is invitation
        /// only, which is also what AdhocAdmission.CanJoin enforces at the accept. The two agree by
        /// construction because both read the same membership.
        ///
        /// <paramref name="only"/> narrows it to a specific set -- what "invite" needs, so adding one
        /// person to a running call rings that person and nobody else.
        /// </summary>
        public static List<UUID> Targets(NonSpatialVoiceSession session, UUID initiator, IEnumerable<UUID> only = null)
        {
            List<UUID> targets = new List<UUID>();
            if (session is null)
                return targets;
            HashSet<UUID> narrow = only is null ? null : new HashSet<UUID>(only);
            foreach (NonSpatialMember m in session.Members)
            {
                if (m.AgentId == UUID.Zero || m.AgentId == initiator) continue;
                if (narrow is not null && !narrow.Contains(m.AgentId)) continue;
                if (m.State != MemberState.Invited) continue;    // seated, declined or departed: not a ring target
                if (session.WasInvited(m.AgentId)) continue;     // one ring per member per cycle
                targets.Add(m.AgentId);
            }
            return targets;
        }

        public static string Line(UUID target, UUID session, string decision, string detail = null)
            => $"{InstrumentTag} target={target} session={session} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
