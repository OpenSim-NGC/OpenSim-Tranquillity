/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The receiving side of the cross-process ring carrier, kept apart from the region module so it can be
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

    public static class CarrierIntake
    {
        public const string DecisionCarrierRefused = "carrier-refused";
        public const string DecisionFeatureOff = "feature-off";
        public const string DecisionCarrierOff = "carrier-off-no-secret";
        public const string DecisionRoomKeyMismatch = "refused-room-key-mismatch";

        public static RingIntake AcceptRing(GridInstantMessage msg, CarrierAuth auth, NonSpatialVoiceSessionEngine engine,
                                            bool groupUsable, int groupCap)
        {
            if (!GroupVoiceRingTransport.TryParse(msg, out UUID target, out UUID groupId, out OSDMap body, out OSDMap authBlock))
                return default;

            GroupVoiceRingTransport.ReadBody(body, out NonSpatialSessionType type, out string roomKey,
                                             out string credential, out UUID caller);
            if (type != NonSpatialSessionType.Group || !groupUsable)
                return new RingIntake { Claimed = true, Decision = DecisionFeatureOff, Target = target, GroupId = groupId };

            auth ??= CarrierAuth.Off();
            string reason = auth.Verify(GroupVoiceRingTransport.Fields(target, groupId, body), authBlock);
            if (reason != null)
                return new RingIntake
                {
                    Claimed = true, Decision = DecisionCarrierRefused, Reason = reason,
                    RefusalCount = auth.RecordRefusal(reason), Target = target, GroupId = groupId,
                };

            SessionOutcome adopted = engine.AdoptRemoteRing(groupId, roomKey, credential, groupCap,
                                                            caller, new[] { target }, type);
            return new RingIntake
            {
                Claimed = true, Ok = adopted.Ok, Decision = adopted.Decision, Target = target, GroupId = groupId,
                Authenticated = true, Type = type, OriginRegion = new UUID(msg.RegionID),
                Body = body, Adopted = adopted,
            };
        }
    }
}
