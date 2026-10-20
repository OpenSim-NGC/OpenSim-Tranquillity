/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Message fan-out for an AD-HOC conference, and who is allowed to send.
 *
 * WHAT THIS REPLACES. Without it, a typed conference line reaches nothing: the viewer sends
 * ImprovedInstantMessage with dialog SessionSend and the conference's session id, and no subscriber
 * claimed it. InstantMessageModule drops it in its default arm (InstantMessageModule.cs --
 * SessionSend is not in the five it forwards), and GroupsMessagingModule takes SessionSend only
 * for a GROUP id (its handler looks the id up as a group). So the line was dropped and logged.
 * That is the gap: "there is no server-side ad-hoc conference TEXT session at all".
 *
 * GROUP TEXT IS NOT TOUCHED. This arm claims a SessionSend only when the engine holds its session
 * id as an ADHOC session. A group id is not in that store, so TryPlan returns false and
 * GroupsMessagingModule handles it exactly as it does today, byte for byte.
 *
 * THE MEMBERSHIP RULE IS CHAT MEMBERSHIP -- NonSpatialMember.InChat -- not a roster, not
 * a power, and NOT THE VOICE SEAT. An ad-hoc conference has no roster; "being invited
 * is the whole of the membership" (AdhocVoiceInvite.cs), and joining is what puts an agent in
 * the conversation.
 *   - the SENDER must be InChat. Invited-but-not-accepted is NOT enough: someone who was rung and
 *     never answered is not in the conversation, and letting them type into it would make an
 *     invitation a write capability.
 *   - the RECIPIENTS are every OTHER chat member. Invited, Declined and anyone who left the chat
 *     get nothing.
 *
 * THE SEAT LIST IS NOT USED HERE. HoldsSeat is only a proxy for chat membership, and that proxy is wrong in BOTH directions once
 * the lifecycles are honoured: it would silence a member who hung up voice but still has
 * the window open, and keep one who closed the window while still holding a seat. Voice, ringing
 * and the cap still read the SEAT list; text and the roster read the CHAT list. They are allowed
 * to differ, and representing that difference is the point of the separate chat axis.
 *
 * NO ECHO TO THE SENDER, AND THIS IS A DELIBERATE DIVERGENCE FROM THE GROUP PATTERN.
 * GroupsMessagingModule sends to self first of all (GroupsMessagingModule.cs) because a
 * the local echo of a group voice session is how the sender's own panel fills. A conference here does not want
 * that: the viewer already renders what it just sent into the ad-hoc session it owns, so echoing
 * would double every line. The prompt fixes this as a requirement; it is recorded here as a
 * KNOWN DIFFERENCE from the group path rather than an oversight, because the next person to read
 * both will otherwise "fix" it back.
 *
 * OFFLINE MEMBERS GET NOTHING STORED, and three independent things say so:
 *     OfflineMessageModule.cs        allowlist = MessageFromObject, MessageFromAgent,
 *                                            GroupNotice, GroupInvitation, InventoryOffered,
 *                                            TaskInventoryOffered -> anything else returns early
 *     OfflineIMRegionModule.cs       allowlist = the same minus TaskInventoryOffered
 *                                            -> anything else returns early
 * SessionSend (17) is in neither list, so an undelivered conference line is discarded rather than
 * stored, on whichever offline module a grid runs. We also set offline = 0 on the carrier
 * (AdhocTextTransport.Build) so the intent is stated at the source and does not depend on reading
 * two other modules to discover. This is the same finding as for rings; it is re-checked
 * here rather than assumed, because the two modules' lists are not identical.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;   // GridInstantMessage

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>
    /// Who one conference line goes to. Built by <see cref="AdhocTextSession.TryPlan"/> and then
    /// split into a local and a cross-instance half by the module, which is the only thing that
    /// knows which agents have a client here.
    /// </summary>
    public sealed class AdhocTextPlan
    {
        public NonSpatialVoiceSession Session { get; }
        public UUID Sender { get; }
        public string Message { get; }

        /// <summary>Every OTHER member this process records as in the conversation. Never contains <see cref="Sender"/>.</summary>
        public IReadOnlyList<UUID> Recipients { get; }

        /// <summary>
        /// Members this process does not record as in the conversation but who may be, because they answered the
        /// invitation in another server process that this one has not heard from: an invitee still marked as
        /// invited, and the member who started the conversation when this process adopted it from a ring. A
        /// line is only ever CARRIED to these, never delivered here, and the process that holds their record
        /// decides. Never contains <see cref="Sender"/>.
        /// </summary>
        public IReadOnlyList<UUID> Elsewhere { get; }

        public AdhocTextPlan(NonSpatialVoiceSession session, UUID sender, string message, IReadOnlyList<UUID> recipients,
                             IReadOnlyList<UUID> elsewhere = null)
        {
            Session = session;
            Sender = sender;
            Message = message ?? string.Empty;
            Recipients = recipients ?? new List<UUID>();
            Elsewhere = elsewhere ?? new List<UUID>();
        }
    }

    public static class AdhocTextSession
    {
        /// <summary>InstantMessageDialog.SessionSend. Mirrored, not referenced -- same reason as the ring.</summary>
        public const byte DialogSessionSend = 17;

        /// <summary>Matches AdhocVoiceInvite's fallback so the panel title cannot differ between ring and text.</summary>
        public const string DefaultSessionName = "Conference";

        public const string InstrumentTag = "[ADHOC TEXT]";

        public const string DecisionDelivered = "adhoc-text-delivered";
        public const string DecisionNotMember = "adhoc-text-refused-not-member";

        /// <summary>A carried line reached a process whose record does not have the addressee in the conversation.</summary>
        public const string DecisionRecipientNotInChat = "adhoc-text-recipient-not-in-chat";
        public const string DecisionNoRecipients = "adhoc-text-no-recipients";
        public const string DecisionEmpty = "adhoc-text-refused-empty";
        public const string DecisionDisabled = "adhoc-text-refused-disabled";

        /// <summary>A moderator muted the sender's text in this conference.</summary>
        public const string DecisionMuted = "adhoc-text-refused-muted";

        /// <summary>
        /// Decide what to do with one SessionSend. Returns FALSE -- untouched, nothing logged, no
        /// refusal -- when this is not an ad-hoc conference message, so every other claimant
        /// (GroupsMessagingModule above all) behaves exactly as before.
        ///
        /// Returns TRUE with a null <paramref name="plan"/> for a message that IS ours and is
        /// refused; <paramref name="decision"/> then carries the greppable word.
        /// </summary>
        public static bool TryPlan(INonSpatialSessions engine, bool enabled, UUID sessionId,
                                   UUID sender, string message, out AdhocTextPlan plan, out string decision)
        {
            plan = null;
            decision = null;
            if (engine is null || sessionId == UUID.Zero)
                return false;

            NonSpatialVoiceSession session = engine.Get(sessionId);
            // Not a voice session we hold, or not a conference: not ours. A GROUP id lands here and is
            // handed straight back, which is what keeps group text working untouched.
            if (session is null || session.Type != NonSpatialSessionType.Adhoc)
                return false;

            // It IS a conference message from here down, so every exit is a decision we log.
            if (!enabled)
            {
                decision = DecisionDisabled;
                return true;
            }

            NonSpatialMember from = session.Find(sender);
            if (from is null || !from.InChat)
            {
                // A non-member's SessionSend for this voice session is refused and logged. This
                // covers the stranger, the invitee who never accepted, the decliner and the member
                // who already left -- none of them is in the conversation.
                //
                // The test is InChat, not HoldsSeat. The voice seat is only a proxy for
                // membership, and a wrong one in both directions -- it would silence someone who hung up voice but was
                // still typing, and kept someone who closed the chat window in the conversation.
                decision = DecisionNotMember;
                return true;
            }

            if (session.IsTextMuted(sender))
            {
                // A moderator muted this member's text in the conference. The line goes to nobody.
                decision = DecisionMuted;
                return true;
            }

            if (string.IsNullOrEmpty(message))
            {
                // Nothing to fan out. Refused rather than delivered so an empty line cannot be used
                // as a cheap presence ping against every member of a conference.
                decision = DecisionEmpty;
                return true;
            }

            List<UUID> recipients = Recipients(session, sender);
            List<UUID> elsewhere = Elsewhere(session, sender);
            decision = recipients.Count == 0 && elsewhere.Count == 0 ? DecisionNoRecipients : DecisionDelivered;
            plan = new AdhocTextPlan(session, sender, message, recipients, elsewhere);
            return true;
        }

        /// <summary>
        /// Members this process cannot rule in or out of the conversation: those still recorded as invited (they may
        /// have answered in another server process, which tells this one nothing), and the member who started the
        /// conversation when this process holds no record of them (a process that adopted the conversation from a
        /// ring learns only the invitees). Members who declined or left are never listed. The sender is excluded.
        /// </summary>
        public static List<UUID> Elsewhere(NonSpatialVoiceSession session, UUID sender)
        {
            List<UUID> to = new List<UUID>();
            if (session is null)
                return to;
            foreach (NonSpatialMember m in session.Members)
                if (m.AgentId != sender && !m.InChat && m.State == MemberState.Invited)
                    to.Add(m.AgentId);
            UUID creator = session.Creator;
            if (creator != UUID.Zero && creator != sender && session.Find(creator) is null)
                to.Add(creator);
            return to;
        }

        /// <summary>
        /// May this agent speak into the conversation, as far as THIS process's record can tell? A member who has
        /// not declined or left, or the member who started it. A line carried from another process was already
        /// checked there against the sender being in the conversation; this is the second check, against this
        /// process's own copy of the record.
        /// </summary>
        public static bool IsParticipant(NonSpatialVoiceSession session, UUID agent)
        {
            if (session is null || agent == UUID.Zero)
                return false;
            NonSpatialMember m = session.Find(agent);
            if (m is null)
                return agent == session.Creator;
            return m.State != MemberState.Declined && m.State != MemberState.Departed;
        }

        /// <summary>Is this agent in the conversation according to THIS process's record? The only members a line is delivered to.</summary>
        public static bool IsInChat(NonSpatialVoiceSession session, UUID agent)
        {
            NonSpatialMember m = session?.Find(agent);
            return m is not null && m.InChat && m.State != MemberState.Declined && m.State != MemberState.Departed;
        }

        /// <summary>
        /// Every CHAT MEMBER except the sender. Order is the store's; nothing downstream depends on
        /// it.
        ///
        /// This is not read from the seat list. Text is the chat lifecycle, so its audience is who is
        /// in the CONVERSATION -- which includes a member who hung up voice and still has the
        /// window open, and excludes one who closed the window while a voice seat is still held.
        /// Invited-but-never-accepted is excluded either way: joining is what sets InChat.
        /// </summary>
        public static List<UUID> Recipients(NonSpatialVoiceSession session, UUID sender)
        {
            List<UUID> to = new List<UUID>();
            if (session is null)
                return to;
            foreach (NonSpatialMember m in session.Members)
                if (m.InChat && m.AgentId != sender)
                    to.Add(m.AgentId);
            return to;
        }

        /// <summary>
        /// The message a VIEWER receives, local path or cross-instance path alike. Modelled on what
        /// GroupsMessagingModule hands to client.SendInstantMessage (:552-569): dialog SessionSend,
        /// imSessionID = the voice session the viewer is keyed on, and the voice session name in binaryBucket,
        /// which is where the group path puts its group name (:325-329).
        ///
        /// fromGroup is FALSE: this is not a group session, and the viewer keys the panel off
        /// imSessionID, which the start reply already re-keyed to our authoritative id.
        /// </summary>
        public static GridInstantMessage BuildForViewer(UUID target, UUID fromAgent, string fromName,
                                                        UUID sessionId, UUID originRegion, string message,
                                                        string sessionName = null, uint timestamp = 0)
        {
            string name = string.IsNullOrEmpty(sessionName) ? DefaultSessionName : sessionName;
            return new GridInstantMessage
            {
                toAgentID = target.Guid,
                fromAgentID = fromAgent.Guid,
                fromAgentName = fromName ?? string.Empty,
                dialog = DialogSessionSend,
                fromGroup = false,
                imSessionID = sessionId.Guid,
                message = message ?? string.Empty,
                offline = 0,
                RegionID = originRegion.Guid,
                timestamp = timestamp != 0 ? timestamp : (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                binaryBucket = Utils.StringToBytes(name),
            };
        }

        public static string Line(UUID sender, UUID session, string decision, string detail = null)
            => $"{InstrumentTag} from={sender} session={session} decision={decision}"
               + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
    }
}
