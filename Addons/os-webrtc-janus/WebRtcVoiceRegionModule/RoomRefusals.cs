/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The rooms the mixer has answered "unknown_room" for, and so gets nothing more from this region until a viewer is
 * provisioned into them again.
 *
 * A mixer removes a room once its last listener has left and does not hold a room nobody has joined. Sending
 * visibility batches (peer_ctl_batch) or positions (peer_pos) to such a room only draws the same refusal, once for every
 * tick. The sink records the refusal here; every sender then leaves that room out until the region records a
 * join into it (VoiceVisibilityService.OnListenerProvisioned), which proves the room exists again.
 *
 * A refusal can arrive after the join that makes it out of date: a request is sent, a viewer joins, then the mixer's
 * answer for the earlier request is read. Each request therefore takes a Stamp() before it is sent, and Refused() ignores
 * a refusal when a join into that room has been recorded since.
 *
 * Thread-safe: read on the feeder tick and the position tick, written on the send path and on CAP handler threads.
 */

using System.Collections.Generic;

namespace osWebRtcVoice
{
    public sealed class RoomRefusals
    {
        private readonly object _lock = new object();
        private readonly HashSet<int> _refused = new HashSet<int>();
        private readonly Dictionary<int, long> _joinedAt = new Dictionary<int, long>();
        private long _clock;

        /// <summary>Read before a request is sent and passed to <see cref="Refused"/> with the mixer's answer.</summary>
        public long Stamp()
        {
            lock (_lock)
                return _clock;
        }

        /// <summary>The mixer answered unknown_room for this room to a request stamped <paramref name="sentAt"/>. The room is
        /// held back until <see cref="Joined"/>, unless a join into it has been recorded since the request went out.</summary>
        public void Refused(int room, long sentAt)
        {
            lock (_lock)
            {
                if (_joinedAt.TryGetValue(room, out long joined) && joined > sentAt)
                    return;
                _refused.Add(room);
            }
        }

        /// <summary>A viewer was provisioned into this room, so the mixer holds it: send to it again.</summary>
        public void Joined(int room)
        {
            lock (_lock)
            {
                _joinedAt[room] = ++_clock;
                _refused.Remove(room);
            }
        }

        /// <summary>True while the room is held back.</summary>
        public bool IsRefused(int room)
        {
            lock (_lock)
                return _refused.Contains(room);
        }

        /// <summary>Number of rooms held back (diagnostics / tests).</summary>
        public int Count
        {
            get { lock (_lock) return _refused.Count; }
        }
    }
}
