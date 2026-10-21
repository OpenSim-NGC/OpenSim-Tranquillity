/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Builds the peer_pos admin request: where each participant of one room really is.
 *
 *   { "request": "peer_pos", "room": 123, "seq": 1760000000000, "full": true, "mode": "observe",
 *     "pos": { "<agent uuid>": [x_cm, y_cm, z_cm, speed_cm_s, tolerance_cm], ... } }
 *
 * The sink adds room_epoch when the region runs with arming. The request is separate from peer_ctl_batch
 * because positions change every tick while exclusions change on crossings, and a position failure must
 * not touch exclusion state.
 */

using System;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    public static class PositionBatchSerializer
    {
        /// The most entries one request carries. The mixer refuses a request with more than 128 entries or
        /// more than 64 KB, and refuses it whole, so the feed stays under both with room to spare.
        public const int MaxEntries = 120;

        public const string RequestName = "peer_pos";

        /// <exception cref="ArgumentException">More than <see cref="MaxEntries"/> entries, a zero UUID, or
        /// a negative speed or tolerance.</exception>
        public static OSDMap BuildRequest(PositionMessage message, PositionFeedMode mode)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (message.Entries.Count > MaxEntries)
                throw new ArgumentException($"A position request carries at most {MaxEntries} entries, got {message.Entries.Count}.", nameof(message));

            var pos = new OSDMap();
            foreach (PositionEntry e in message.Entries)
            {
                if (e.Id == UUID.Zero)
                    throw new ArgumentException("A position entry must name a participant.", nameof(message));
                if (e.SpeedCmS < 0 || e.ToleranceCm < 0)
                    throw new ArgumentException("A position entry's speed and tolerance cannot be negative.", nameof(message));
                pos[e.Id.ToString()] = new OSDArray
                {
                    OSD.FromInteger(e.XCm), OSD.FromInteger(e.YCm), OSD.FromInteger(e.ZCm),
                    OSD.FromInteger(e.SpeedCmS), OSD.FromInteger(e.ToleranceCm),
                };
            }

            var request = new OSDMap
            {
                ["request"] = OSD.FromString(RequestName),
                ["room"] = OSD.FromInteger(message.Room),
                ["seq"] = OSD.FromLong(message.Seq),
                ["full"] = OSD.FromBoolean(message.Full),
                ["mode"] = OSD.FromString(PositionFeedModes.WireName(mode)),
                ["pos"] = pos,
            };
            return request;
        }
    }
}
