/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The participant cap, RECONCILED with the mixer's rather than stacked on top of it.
 *
 * There is already a per-room ceiling and it is enforced where it has to be -- at the join, inside
 * the mixer: SLV_MAX_MIX (janus_slvoice.c:171-173, 110) refuses with JANUS_SLVOICE_ERROR_ROOM_FULL
 * (:112, 495), the connector keys on exactly that code (WebRtcJanusService.cs, :507) and maps it
 * to HTTP 409 Conflict (ProvisionResponseBuilder.cs), which the viewer reads as
 * ERROR_CHANNEL_FULL (llvoicewebrtc.cpp:3714-3716). That chain is the ground truth and this engine
 * does not duplicate it.
 *
 * What the engine adds is an EARLIER, SMALLER gate, and the reconciliation rule is:
 *
 *   1. The effective cap is min(requested, MixerRoomCap). A conference cap can only ever be at or
 *      below the mixer's, never above it, so the engine can never admit a seat the mixer will then
 *      refuse. If someone configures 500, they get 110, not a promise the mixer breaks.
 *   2. A refusal here produces the SAME observable as the mixer's: Capacity -> 409 -> the viewer's
 *      ERROR_CHANNEL_FULL. One failure mode reaches the user, not two that look different.
 *   3. The engine's gate is advisory-early, not authoritative: it refuses at accept, before a
 *      provision is attempted, so a 51st participant is told immediately instead of after a JSEP
 *      round trip. The mixer remains the last word, because only the mixer knows who is actually
 *      in the room.
 *
 * MixerRoomCap is a mirrored constant, deliberately: the module and the mixer are separate
 * artefacts with no shared header, and the codebase already mirrors the 495 literal the same way
 * with the same reasoning (WebRtcJanusService.cs). CapsAgree() is the assertion that keeps
 * the mirror honest and is unit-tested.
 */
using System;

namespace osWebRtcVoice.NonSpatial
{
    public static class NonSpatialCaps
    {
        /// <summary>
        /// Mirror of the mixer's SLV_MAX_MIX (janus_slvoice.c:172). The mixer is the enforcer; this
        /// copy exists so the engine can never hand out a seat the mixer will refuse.
        /// </summary>
        public const int MixerRoomCap = 110;

        /// <summary>The default conference ceiling.</summary>
        public const int DefaultConferenceCap = 50;

        /// <summary>
        /// P2P is structurally two. Kept explicit so a P2P session cannot be grown by configuration
        /// into something the A2A admission rules were never written for.
        /// </summary>
        public const int P2PCap = 2;

        /// <summary>The mixer's room-full error code, mirrored (janus_slvoice.c:112).</summary>
        public const int MixerRoomFullErrorCode = 495;

        /// <summary>The HTTP status a capacity refusal must answer with, matching the mixer's chain.</summary>
        public const int CapacityHttpStatus = 409;

        /// <summary>The HTTP status a join refused because the grid session service could not be reached answers with.</summary>
        public const int StoreUnavailableHttpStatus = 503;

        /// <summary>
        /// The HTTP status a refused join answers with, by the engine's decision word: 409 when the room is full, 503
        /// when the session service could not be reached (the viewer can try again), otherwise 403.
        /// </summary>
        public static int RefusalStatus(string decision)
            => decision == SessionOutcome.Capacity ? CapacityHttpStatus
             : decision == SessionOutcome.StoreUnavailable ? StoreUnavailableHttpStatus
             : 403;

        /// <summary>
        /// The cap actually in force. Never above <see cref="MixerRoomCap"/> (rule 1); never below 2,
        /// since a one-seat voice session is not a voice session.
        /// </summary>
        public static int Effective(NonSpatialSessionType type, int requested)
        {
            if (type == NonSpatialSessionType.P2P)
                return P2PCap;
            int want = requested > 0 ? requested : DefaultConferenceCap;
            if (want > MixerRoomCap) want = MixerRoomCap;
            if (want < 2) want = 2;
            return want;
        }

        /// <summary>
        /// The mirror invariant: every cap this engine can hand out is one the mixer will honour.
        /// Unit-tested so a future edit to either number cannot silently break rule 1.
        /// </summary>
        public static bool CapsAgree()
            => DefaultConferenceCap <= MixerRoomCap && P2PCap <= MixerRoomCap && MixerRoomCap > 0;
    }
}
