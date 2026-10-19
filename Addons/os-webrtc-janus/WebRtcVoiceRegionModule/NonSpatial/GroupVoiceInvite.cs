/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The group voice invitation body, and who gets rung.
 *
 * THE BODY. The viewer's voice branch fires on a ChatterBoxInvitation whose body has a `voice` key
 * (llimview.cpp:5196). It then calls LLIMMgr::inviteToSession with body["session_id"], and THAT is
 * what chooses the prompt: `gAgent.isInGroup(session_id, true)` -> "VoiceInviteGroup"
 * (llimview.cpp:4156-4161). So session_id MUST be the GROUP id, which for a group session is also
 * the engine's SessionId -- the two agree by construction (DeriveSessionId). Get it wrong and the
 * member sees an ad-hoc prompt, or none.
 *
 * `invitation_type` must NOT be P2P: the handler reads
 * `input["body"]["voice"]["invitation_type"] == P2P_CHAT_SESSION` to pick IM_SESSION_P2P_INVITE over
 * IM_SESSION_INVITE (llimview.cpp:5204). A group invite carries GROUP_CHAT_SESSION = 0
 * (llimview.cpp:119-125), which lands on IM_SESSION_INVITE and therefore on the group prompt.
 *
 * The `voice` map becomes the callee's voice_channel_info VERBATIM, so it carries channel_uri and
 * channel_credentials -- the same pair the "call" reply hands out -- and the same voice_server_type
 * the A2A path documents at length (A2AInvitation.VoiceServerType). On accept the viewer posts
 * "accept invitation" and then calls startCall with this map, skipping method:"call" entirely
 * (llimview.cpp:3382-3385 -> chatterBoxInvitationCoro -> startCall).
 *
 * WHO GETS RUNG. This list is built within one simulator process, so the target list is "every agent with a presence
 * in THIS process who is a member of the group holding both powers", minus the initiator and minus
 * anyone already seated. That is deliberately not the group roster: a roster query would list
 * offline and other-host members we cannot deliver to anyway. Members on other simulator processes are
 * reached separately, through IMessageTransferModule.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice.NonSpatial
{
    public static class GroupVoiceInvite
    {
        /// <summary>EMultiAgentChatSessionType::GROUP_CHAT_SESSION (llimview.cpp:121).</summary>
        public const int InvitationTypeGroup = 0;

        public const string InstrumentTag = "[GROUP INVITE]";

        /// <summary>
        /// The ChatterBoxInvitation body for a group voice ring. <paramref name="groupName"/> labels the
        /// callee's incoming-call UI; the caller's name rides in from_name.
        /// </summary>
        public static OSDMap BuildBody(NonSpatialVoiceSession session, string token, UUID caller, string callerName, string groupName)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (session.Type != NonSpatialSessionType.Group)
                throw new ArgumentException("group invitation for a non-group session", nameof(session));
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("invitation requires a minted token");

            OSDMap voice = new OSDMap
            {
                ["invitation_type"] = OSD.FromInteger(InvitationTypeGroup),
                ["voice_server_type"] = OSD.FromString(A2AInvitation.VoiceServerType),
                ["channel_uri"] = OSD.FromString(session.RoomKey),
                ["channel_credentials"] = OSD.FromString(token),
            };
            return new OSDMap
            {
                // the GROUP id -- what makes the viewer choose VoiceInviteGroup
                ["session_id"] = OSD.FromUUID(session.Owner),
                ["session_name"] = OSD.FromString(string.IsNullOrEmpty(groupName) ? "Group Voice" : groupName),
                ["from_id"] = OSD.FromUUID(caller),
                ["from_name"] = OSD.FromString(callerName ?? string.Empty),
                ["voice"] = voice,
            };
        }

        /// <summary>
        /// Everyone this process should ring for <paramref name="session"/>: present in some scene here,
        /// a member of the group with both powers, not the initiator, not already seated, and not already
        /// rung for this ring (<see cref="NonSpatialVoiceSession.WasInvited"/>).
        ///
        /// Pure apart from the scene walk, so the whole selection is unit-testable through
        /// <paramref name="presentAgents"/>.
        /// </summary>
        public static List<UUID> Targets(IEnumerable<UUID> presentAgents, NonSpatialVoiceSession session,
                                         UUID initiator, GroupVoicePolicy policy)
        {
            List<UUID> targets = new List<UUID>();
            if (presentAgents == null || session == null || policy == null || !policy.IsUsable)
                return targets;
            HashSet<UUID> seen = new HashSet<UUID>();
            foreach (UUID a in presentAgents)
            {
                if (a == UUID.Zero || a == initiator || !seen.Add(a))
                    continue;
                NonSpatialMember m = session.Find(a);
                if (m != null && m.HoldsSeat)
                    continue;                         // already in the call; ringing it would be noise
                if (session.WasInvited(a))
                    continue;                         // one ring per member per ring cycle
                if (!policy.IsMember(a, session.Owner) || !policy.HasRequiredPowers(a, session.Owner))
                    continue;
                targets.Add(a);
            }
            return targets;
        }

        /// <summary>Every agent with a presence in these scenes, root or child, deduplicated.</summary>
        public static List<UUID> PresentAgents(IEnumerable<Scene> scenes)
        {
            List<UUID> agents = new List<UUID>();
            HashSet<UUID> seen = new HashSet<UUID>();
            if (scenes == null)
                return agents;
            foreach (Scene s in scenes)
            {
                if (s == null) continue;
                s.ForEachScenePresence(sp =>
                {
                    if (sp != null && !sp.IsDeleted && seen.Add(sp.UUID))
                        agents.Add(sp.UUID);
                });
            }
            return agents;
        }

        /// <summary>
        /// Of the candidate remote targets, keep only those the PRESENCE
        /// SERVICE reports online in some region. <paramref name="onlineRegionOf"/> returns the region
        /// a member is currently in, or UUID.Zero when the presence service does not know them --
        /// which is what "offline" looks like from here.
        ///
        /// Why this matters beyond politeness: an undeliverable ring becomes an OnUndeliveredMessage,
        /// and a ring that arrives at the next login is worse than useless -- the call it announces
        /// ended hours ago. Not sending is the correct behaviour, not merely the tidy one.
        ///
        /// Pure, so the whole rule is unit-testable without a grid.
        /// </summary>
        public static List<UUID> OnlineOnly(IEnumerable<UUID> candidates, Func<UUID, UUID> onlineRegionOf)
        {
            List<UUID> online = new List<UUID>();
            if (candidates == null || onlineRegionOf == null)
                return online;
            foreach (UUID a in candidates)
            {
                UUID region;
                try { region = onlineRegionOf(a); }
                catch { continue; }            // a presence lookup that throws is treated as offline
                if (region != UUID.Zero)
                    online.Add(a);
            }
            return online;
        }

        public const string DecisionOffline = "skipped-offline";

        public static string Line(UUID target, UUID group, string region, string decision)
            => $"{InstrumentTag} target={target} group={group} region=\"{region}\" decision={decision}";
    }
}
