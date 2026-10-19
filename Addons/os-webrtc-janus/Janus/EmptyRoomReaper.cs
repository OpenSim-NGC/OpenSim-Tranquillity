/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Removes rooms the service created once they have stood empty for a grace period.
 *
 * Janus's stock AudioBridge plugin never destroys a room that has emptied, so without this every estate,
 * parcel, call and conference room stays on the gateway until Janus restarts. The slvoice mixer removes
 * empty rooms itself (a 60 s grace sweep), so this is not used with it.
 *
 * Only rooms whose create this process saw answered "created" are tracked. A room the create found already
 * there (an operator's room, or another process's) is never tracked and so never removed here. The
 * grace period starts when a sweep first sees the room empty, so a room is never removed sooner than one
 * grace period after it was last seen with someone in it. The class holds no network code: the room list and
 * the destroy are passed to the sweep, which keeps it testable with a clock the test controls.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace osWebRtcVoice
{
    public sealed class EmptyRoomReaper
    {
        /// <summary>Same as the slvoice mixer's own empty-room grace.</summary>
        public const int DefaultGraceSeconds = 60;

        public const bool DefaultEnabled = true;

        private readonly TimeSpan _grace;
        private readonly Func<long> _nowMs;

        // Room number -> when a sweep first saw it empty (-1: not seen empty).
        private readonly ConcurrentDictionary<int, long> _tracked = new ConcurrentDictionary<int, long>();

        public EmptyRoomReaper(TimeSpan pGrace, Func<long> pNowMs = null)
        {
            _grace = pGrace;
            _nowMs = pNowMs ?? (() => Environment.TickCount64);
        }

        /// <summary>Rooms currently watched.</summary>
        public int TrackedCount => _tracked.Count;

        public bool IsTracked(int pRoom) => _tracked.ContainsKey(pRoom);

        /// <summary>The create for this room was answered "created": this process made it.</summary>
        public void NoteCreated(int pRoom) => _tracked[pRoom] = -1;

        /// <summary>One pass. <paramref name="pListCounts"/> returns the participant count of every room on the
        /// gateway, or null when the list could not be read (nothing is changed then).
        /// <paramref name="pDestroy"/> returns true when the room is gone afterwards. Returns the rooms removed.</summary>
        public async Task<int> SweepAsync(Func<Task<IReadOnlyDictionary<int, int>>> pListCounts, Func<int, Task<bool>> pDestroy)
        {
            if (_tracked.IsEmpty)
                return 0;
            IReadOnlyDictionary<int, int> counts = await pListCounts().ConfigureAwait(false);
            if (counts is null)
                return 0;
            long now = _nowMs();
            int removed = 0;
            foreach (KeyValuePair<int, long> kvp in _tracked)
            {
                int room = kvp.Key;
                if (!counts.TryGetValue(room, out int participants))
                {
                    _tracked.TryRemove(room, out _);   // already gone (the gateway restarted, or someone removed it)
                    continue;
                }
                if (participants > 0)
                {
                    _tracked[room] = -1;
                    continue;
                }
                if (kvp.Value < 0)
                {
                    _tracked[room] = now;
                    continue;
                }
                if (now - kvp.Value < (long)_grace.TotalMilliseconds)
                    continue;
                if (await pDestroy(room).ConfigureAwait(false))
                {
                    _tracked.TryRemove(room, out _);
                    removed++;
                }
            }
            return removed;
        }
    }
}
