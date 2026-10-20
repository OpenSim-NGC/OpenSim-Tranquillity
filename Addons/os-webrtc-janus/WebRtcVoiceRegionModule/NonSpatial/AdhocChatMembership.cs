/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The viewer's chat-session LEAVE, and what "SessionAdd / SessionDrop" actually
 * turn out to be.
 *
 * THE FINDING THAT COLLAPSES TWO ITEMS INTO ONE. listed SessionAdd/SessionDrop as unbuilt, and
 * lists IM_SESSION_LEAVE separately. They are not separate. MEASURED by reflecting the real
 * enum out of the deployed OpenMetaverse.dll rather than trusting either name:
 *     SessionAdd           = 13     IM_SESSION_INVITE            (llinstantmessage.h:98)
 *     SessionOfflineAdd    = 14     IM_SESSION_P2P_INVITE        (:100)
 *     SessionGroupStart    = 15     IM_SESSION_GROUP_START       (:103)
 *     SessionCardlessStart = 16     IM_SESSION_CONFERENCE_START  (:106)
 *     SessionSend          = 17     IM_SESSION_SEND              (:109)
 *     SessionDrop          = 18     IM_SESSION_LEAVE             (:112)
 * So libomv's SessionDrop IS the viewer's IM_SESSION_LEAVE -- ONE dialog value, 18, under two
 * names. One handler serves both items, and building them separately would have produced two
 * handlers racing over the same message.
 *
 * SESSIONADD IS OUT OF SCOPE, WITH EVIDENCE RATHER THAN BY ASSERTION. Dialog 13 is
 * IM_SESSION_INVITE, and this viewer never SENDS it: every occurrence in the tree is a viewer-side
 * session TYPE, passed to gIMMgr->addSession or compared against session->mType
 * (exogroupmutelist.cpp:174, fsfloaterim.cpp:155/:1281/:2101/:2436, fsfloaterimcontainer.cpp:282,
 * llchiclet.cpp:706, llfloaterimsession.cpp:462, fsfloatervoicecontrols.cpp:241). Nothing packs it
 * into an outbound IM. A handler for it would be dead code, so there is none. The server-side way
 * into a conference stays the cap method "invite" -- which is what the viewer does send.
 *
 * WHAT A CHAT LEAVE MUST NOT DO: touch the voice seat. 's three lifecycles are independent,
 * and this is lifecycle (1) alone. Before the engine could only express a leave as
 * Depart(.., ChatLeave), which sets the member Departed and releases their seat -- so closing the
 * chat window would have hung up the call. NonSpatialVoiceSessionEngine.LeaveChat exists precisely
 * so that cannot happen, and the tests walk every combination of the three axes.
 *
 * WHY THE VIEWER SENDS THIS AT ALL: LLIMModel::sendLeaveSession packs IM_SESSION_LEAVE through
 * pack_instant_message + sendReliableMessage (llimview.cpp:2161-2180), reached from
 * LLIMMgr::leaveSession and from the group-mute path. It is UDP IM traffic, not the cap -- which is
 * why this is a client-IM handler and not another ChatSessionRequest method (: "there is no
 * leave method on ChatSessionRequest").
 */
using System;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public static class AdhocChatMembership
    {
        /// <summary>
        /// InstantMessageDialog.SessionDrop == the viewer's IM_SESSION_LEAVE. Mirrored rather than
        /// referenced, like the other dialog constants here, and named for BOTH so a reader
        /// searching for either finds it.
        /// </summary>
        public const byte DialogSessionLeave = 18;

        /// <summary>Dialog 13. Present only to document that it is not ours; see the header.</summary>
        public const byte DialogSessionAddUnused = 13;

        public const string InstrumentTag = "[ADHOC CHAT]";

        public const string DecisionLeft = "adhoc-chat-left";
        public const string DecisionIdempotent = "adhoc-chat-leave-idempotent";
        public const string DecisionNotMember = "adhoc-chat-leave-not-member";
        public const string DecisionDisabled = "adhoc-chat-leave-disabled";

        /// <summary>
        /// Handle one UDP chat-session leave. Returns FALSE -- untouched -- when the voice session is not
        /// an ad-hoc conference this instance holds, so a GROUP leave still belongs entirely to
        /// GroupsMessagingModule and a P2P one to the A2A path.
        ///
        /// Returns TRUE with <paramref name="stillSeated"/> telling the caller whether the member
        /// kept a voice seat, because that is the fact the instrument line has to record: it is the
        /// visible proof that the two lifecycles did not get conflated.
        /// </summary>
        public static bool TryHandleLeave(INonSpatialSessions engine, bool enabled,
                                          UUID sessionId, UUID agent,
                                          out string decision, out bool stillSeated)
        {
            decision = null;
            stillSeated = false;
            if (engine is null || sessionId == UUID.Zero)
                return false;

            NonSpatialVoiceSession session = engine.Get(sessionId);
            if (session is null || session.Type != NonSpatialSessionType.Adhoc)
                return false;

            if (!enabled)
            {
                decision = DecisionDisabled;
                return true;
            }

            SessionOutcome outcome = engine.LeaveChat(sessionId, agent);
            if (!outcome.Ok)
            {
                // The agent was never in this conference at all. Not an error worth a warning: a
                // viewer closing windows on logout can address a voice session it only ever declined.
                decision = DecisionNotMember;
                return true;
            }

            NonSpatialMember m = engine.Get(sessionId)?.Find(agent);
            stillSeated = m is not null && m.HoldsSeat;
            decision = outcome.Decision == "chat-left" ? DecisionLeft : DecisionIdempotent;
            return true;
        }

        public static string Line(UUID agent, UUID session, string decision, bool stillSeated, string detail = null)
            => $"{InstrumentTag} agent={agent} session={session} decision={decision} "
               + $"voice_seat={(stillSeated ? "kept" : "none")}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
