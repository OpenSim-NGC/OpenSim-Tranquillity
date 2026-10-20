/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The receiving side of the two cross-process carriers, kept apart from the region module so it can be
 * tested without a scene: parse the grid IM, authenticate it (CarrierAuth), and only then let it touch
 * the session engine.
 *
 * Nothing is adopted, and no session is read for a decision, before the MAC has been verified. A refused
 * carrier changes no state beyond the refusal counter.
 */
using System;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;   // GridInstantMessage

namespace osWebRtcVoice.NonSpatial
{
    /// <summary>The outcome of offering an incoming grid IM to the ring receiver.</summary>
    public readonly struct RingIntake
    {
        /// <summary>False when the message is not a ring at all: other subscribers still see it untouched.</summary>
        public bool Claimed { get; init; }

        /// <summary>True when the ring was authentic and its session adopted; the invitation may be delivered.</summary>
        public bool Ok { get; init; }

        public string Decision { get; init; }

        /// <summary>Why the carrier was refused (a CarrierAuth reason), or null.</summary>
        public string Reason { get; init; }

        /// <summary>How many carriers have now been refused for <see cref="Reason"/>.</summary>
        public long RefusalCount { get; init; }

        public UUID Target { get; init; }
        public UUID GroupId { get; init; }
        public OSDMap Body { get; init; }
        public SessionOutcome Adopted { get; init; }

        /// <summary>True once the carrier passed authentication, whatever the session engine then decided.</summary>
        public bool Authenticated { get; init; }

        public NonSpatialSessionType Type { get; init; }

        /// <summary>The region the carrier says it came from.</summary>
        public UUID OriginRegion { get; init; }
    }

    /// <summary>The outcome of offering an incoming grid IM to the conference-text receiver.</summary>
    public readonly struct TextIntake
    {
        public bool Claimed { get; init; }
        public bool Ok { get; init; }
        public string Decision { get; init; }
        public string Reason { get; init; }
        public long RefusalCount { get; init; }
        public UUID Target { get; init; }
        public UUID SessionId { get; init; }
        public UUID Sender { get; init; }
        public string Message { get; init; }
        public string FromName { get; init; }
        public UUID OriginRegion { get; init; }
        public bool Authenticated { get; init; }
    }

    public static class CarrierIntake
    {
        public const string DecisionCarrierRefused = "carrier-refused";
        public const string DecisionFeatureOff = "feature-off";
        public const string DecisionCarrierOff = "carrier-off-no-secret";
        public const string DecisionRoomKeyMismatch = "refused-room-key-mismatch";
        public const string DecisionNoCarrierKey = "refused-no-carrier-key";

        public static RingIntake AcceptRing(GridInstantMessage msg, CarrierAuth auth, INonSpatialSessions engine,
                                            bool groupUsable, int groupCap, bool adhocEnabled)
        {
            if (!GroupVoiceRingTransport.TryParse(msg, out UUID target, out UUID groupId, out OSDMap body, out OSDMap authBlock))
                return default;

            GroupVoiceRingTransport.ReadBody(body, out NonSpatialSessionType type, out string roomKey,
                                             out string credential, out UUID caller);
            if (type == NonSpatialSessionType.Adhoc ? !adhocEnabled : !groupUsable)
                return new RingIntake { Claimed = true, Decision = DecisionFeatureOff, Target = target, GroupId = groupId };

            auth ??= CarrierAuth.Off();
            // A store that judges rings itself does not need the carrier's MAC, so a grid that keeps its sessions in
            // the grid service rings across regions without a shared secret. A region that does have a secret still
            // requires the MAC, which also covers the names the invitation shows, since the store holds no names.
            bool storeVerifies = engine.StoreVerifiesRings;
            string reason = storeVerifies && !auth.Enabled
                ? null
                : auth.Verify(GroupVoiceRingTransport.Fields(target, groupId, body), authBlock);
            if (reason != null)
                return new RingIntake
                {
                    Claimed = true, Decision = DecisionCarrierRefused, Reason = reason,
                    RefusalCount = auth.RecordRefusal(reason), Target = target, GroupId = groupId,
                };

            if (storeVerifies)
            {
                // Nothing is adopted from the wire: the store says whether this ring is genuine, and the room and
                // credential the viewer is shown are the store's, not the carrier's.
                SessionOutcome verified = engine.VerifyRing(groupId, target, caller, type);
                if (!verified.Ok)
                    return new RingIntake { Claimed = true, Decision = verified.Decision, Target = target, GroupId = groupId };
                if (string.IsNullOrEmpty(verified.Session?.Token))
                    return new RingIntake { Claimed = true, Decision = "refused-no-credential", Target = target, GroupId = groupId };
                OSDMap voice = body.TryGetOSDMap("voice", out OSDMap v) ? v : new OSDMap();
                voice["channel_uri"] = OSD.FromString(verified.Session.RoomKey);
                voice["channel_credentials"] = OSD.FromString(verified.Session.Token);
                body["voice"] = voice;
                return new RingIntake
                {
                    Claimed = true, Ok = true, Decision = verified.Decision, Target = target, GroupId = groupId,
                    Body = body, Adopted = verified,
                };
            }

            SessionOutcome adopted = engine.AdoptRemoteRing(groupId, roomKey, credential,
                                                            type == NonSpatialSessionType.Adhoc
                                                                ? NonSpatialCaps.DefaultConferenceCap : groupCap,
                                                            caller, new[] { target }, type);
            return new RingIntake
            {
                Claimed = true, Ok = adopted.Ok, Decision = adopted.Decision, Target = target, GroupId = groupId,
                Authenticated = true, Type = type, OriginRegion = new UUID(msg.RegionID),
                Body = body, Adopted = adopted,
            };
        }

        public static TextIntake AcceptText(GridInstantMessage msg, CarrierAuth auth, INonSpatialSessions engine)
        {
            if (!AdhocTextTransport.TryParse(msg, out UUID target, out UUID sessionId, out string message,
                                             out string fromName, out _, out string roomKey, out OSDMap authBlock))
                return default;

            UUID sender = new UUID(msg.fromAgentID);
            auth ??= CarrierAuth.Off();
            // A store that keys conferences gives a seated member the key the line was signed with, so the line needs
            // no shared secret; someone who is not in the conference is given none, and nothing they send checks out.
            string sessionKey = null;
            if (engine.StoreIssuesCarrierKeys)
            {
                sessionKey = engine.CarrierKey(sessionId, target);
                if (string.IsNullOrEmpty(sessionKey))
                    return new TextIntake { Claimed = true, Decision = DecisionNoCarrierKey, Target = target, SessionId = sessionId };
            }
            string reason = auth.Verify(AdhocTextTransport.Fields(target, sessionId, sender, roomKey, message), authBlock, sessionKey);
            if (reason != null)
                return new TextIntake
                {
                    Claimed = true, Decision = DecisionCarrierRefused, Reason = reason,
                    RefusalCount = auth.RecordRefusal(reason), Target = target, SessionId = sessionId,
                };

            // Authentic. The sender's instance decided the sender is a member; this instance holds its own
            // copy of the session and checks again, and the room key the sender signed must be the one this
            // instance derives for the session.
            NonSpatialVoiceSession session = engine.Get(sessionId);
            if (session is null || session.Type != NonSpatialSessionType.Adhoc)
                return new TextIntake { Claimed = true, Authenticated = true, OriginRegion = new UUID(msg.RegionID), Decision = SessionOutcome.NoSuchSession, Target = target, SessionId = sessionId };
            if (string.IsNullOrEmpty(roomKey) || !string.Equals(roomKey, session.RoomKey, StringComparison.Ordinal))
                return new TextIntake { Claimed = true, Authenticated = true, OriginRegion = new UUID(msg.RegionID), Decision = DecisionRoomKeyMismatch, Target = target, SessionId = sessionId };
            // The sender must be a participant of this process's copy of the record. It may still be marked as
            // invited here, because it answered in the sender's own process, which this one has not heard from.
            if (!AdhocTextSession.IsParticipant(session, sender))
                return new TextIntake
                {
                    Claimed = true, Authenticated = true, OriginRegion = new UUID(msg.RegionID), Decision = AdhocTextSession.DecisionNotMember, Target = target,
                    SessionId = sessionId, Sender = sender,
                };
            // The addressee must be in the conversation according to THIS process's record: it answered the
            // invitation here and has not declined or left. The sending process could only guess.
            if (!AdhocTextSession.IsInChat(session, target))
                return new TextIntake
                {
                    Claimed = true, Authenticated = true, OriginRegion = new UUID(msg.RegionID), Decision = AdhocTextSession.DecisionRecipientNotInChat, Target = target,
                    SessionId = sessionId, Sender = sender,
                };

            return new TextIntake
            {
                Claimed = true, Authenticated = true, Ok = true, Decision = AdhocTextTransport.DecisionSent, Target = target,
                SessionId = sessionId, Sender = sender, Message = message, FromName = fromName,
                OriginRegion = new UUID(msg.RegionID),
            };
        }
    }
}
