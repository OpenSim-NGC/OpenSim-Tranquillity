/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * ChatterBoxSessionAgentListUpdates for the
 * A2A session, so both parties' IM panels get the participant/moderation surface a group session
 * gets (the group module sends one after its invitation, GroupsMessagingModule.cs).
 *
 * DECIDED (the viewer trace): can_voice_chat MUST be true for BOTH parties -- the viewer treats
 * can_voice_chat:false for the peer on a P2P channel as a decline and HANGS UP the call
 * (P2PCallDeclined + endCall, llimview.cpp:4366-4382). The group pattern's 1-arg
 * GroupChatListAgentUpdateData ctor defaults canVoice to FALSE (IEventQueue.cs), so this class
 * never uses it: every update is built by the 5-arg ctor with cv:true, by construction. This class only
 * maintains the participant list; the caller's connected/End-Call state comes from mixer presence.
 *
 * The wire body is built by the existing EventQueueGetHandlers.ChatterBoxSessionAgentListUpdates
 * (EventQueueGetHandlers.cs): agent_updates.<uuid>.{info:{can_voice_chat,is_moderator,
 * mutes:{text}}, transition:"ENTER"|"LEAVE"}, empty `updates` map, session_id -- exactly the fields
 * LLIMMgr::processAgentListUpdates / LLIMSpeakerMgr::updateSpeakers read. No new body shape.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcVoice
{
    public static class A2AAgentListDelivery
    {
        public const string InstrumentTag = "[A2A AGENTLIST]";

        public const string DecisionSent = "sent";
        public const string DecisionNoPresence = "unreachable(no-presence)";
        public const string DecisionNoQueue = "unreachable(no-event-queue)";
        public const string DecisionSendFailed = "unreachable(send-failed)";

        public const string TransitionEnter = "ENTER";
        public const string TransitionLeave = "LEAVE";

        /// <summary>One entry, ENTER or LEAVE. can_voice_chat is TRUE by construction (see header).</summary>
        public static GroupChatListAgentUpdateData Update(UUID agent, bool enter)
            => new GroupChatListAgentUpdateData(agent, /*cv*/true, /*isMod*/false, /*mtd*/false, /*eOrL*/enter);

        /// <summary>
        /// The ENTER list one party receives when the voice session goes Active: the OTHER party plus, per
        /// the group pattern, the recipient's own entry (so the panel shows both participants).
        /// </summary>
        public static List<GroupChatListAgentUpdateData> EnterUpdates(UUID other, UUID self)
            => new List<GroupChatListAgentUpdateData> { Update(other, true), Update(self, true) };

        /// <summary>
        /// What the caller's viewer is sent when the callee declines: an entry for the callee that says it cannot voice
        /// chat. The viewer reads that on a P2P channel as the peer having declined, shows the call as declined
        /// and ends it (llimview.cpp:4366-4382). It is an ENTER, not a LEAVE, because the viewer skips a LEAVE
        /// before it reads can_voice_chat (llimview.cpp:4364-4366).
        /// </summary>
        public static List<GroupChatListAgentUpdateData> DeclinedUpdates(UUID callee)
            => new List<GroupChatListAgentUpdateData> { new GroupChatListAgentUpdateData(callee, /*cv*/false, /*isMod*/false, /*mtd*/false, /*eOrL*/true) };

        /// <summary>The LEAVE list the remaining party receives when an Active record is removed.</summary>
        public static List<GroupChatListAgentUpdateData> LeaveUpdates(UUID departed)
            => new List<GroupChatListAgentUpdateData> { Update(departed, false) };

        /// <summary>
        /// Deliver one AgentListUpdates to <paramref name="recipient"/>. Resolves the recipient's scene
        /// via the same root-preferred/child-fallback rule the invitation uses
        /// (A2AInviteDelivery.ResolveCalleeScene). Never throws into the caller's request.
        /// </summary>
        public static string Deliver(IEnumerable<Scene> scenes, UUID recipient, UUID sessionId,
            List<GroupChatListAgentUpdateData> updates, Func<Scene, IEventQueue> queueOf)
        {
            Scene scene = A2AInviteDelivery.ResolveCalleeScene(scenes, recipient, out _);
            if (scene == null)
                return DecisionNoPresence;
            IEventQueue queue;
            try
            {
                queue = queueOf != null ? queueOf(scene) : scene.RequestModuleInterface<IEventQueue>();
            }
            catch
            {
                queue = null;
            }
            if (queue == null)
                return DecisionNoQueue;
            try
            {
                queue.ChatterBoxSessionAgentListUpdates(sessionId, recipient, updates);
                return DecisionSent;
            }
            catch
            {
                return DecisionSendFailed;
            }
        }

        /// <summary>
        /// The Active transition (the callee's admitted provision, accept): both parties get
        /// the other's ENTER plus their own entry. Returns one instrument line per recipient.
        /// </summary>
        public static List<string> SendActivePair(IEnumerable<Scene> scenes, A2ASession session, Func<Scene, IEventQueue> queueOf)
        {
            var lines = new List<string>(2);
            if (session == null)
                return lines;
            string d1 = Deliver(scenes, session.Caller, session.SessionId, EnterUpdates(session.Callee, session.Caller), queueOf);
            lines.Add(Line(session.Caller, session.SessionId, TransitionEnter, session.Callee, d1));
            string d2 = Deliver(scenes, session.Callee, session.SessionId, EnterUpdates(session.Caller, session.Callee), queueOf);
            lines.Add(Line(session.Callee, session.SessionId, TransitionEnter, session.Caller, d2));
            return lines;
        }

        /// <summary>
        /// An Active record was removed (both-logout, client-closed): the remaining party gets the
        /// departed party's LEAVE, if still reachable on this instance. Returns the instrument line.
        /// Invited-state removals (decline / TTL) never come through here -- no session ever formed.
        /// </summary>
        public static string SendLeave(IEnumerable<Scene> scenes, A2ASession session, UUID departed, Func<Scene, IEventQueue> queueOf)
        {
            if (session == null)
                return null;
            UUID remaining = session.OtherParty(departed);
            if (remaining == UUID.Zero)
                return null;
            string d = Deliver(scenes, remaining, session.SessionId, LeaveUpdates(departed), queueOf);
            return Line(remaining, session.SessionId, TransitionLeave, departed, d);
        }

        /// <summary>Permanent instrument: one greppable line per recipient.</summary>
        public static string Line(UUID recipient, UUID sessionId, string transition, UUID about, string decision)
            => $"{InstrumentTag} agent={recipient} session-id={sessionId} transition={transition} about={about} decision={decision}";
    }
}
