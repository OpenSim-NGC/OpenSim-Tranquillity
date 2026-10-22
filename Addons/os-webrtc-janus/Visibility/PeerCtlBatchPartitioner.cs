/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Pure partitioner for per-room peer_ctl_batch emission, with one policy for a missing room record.
 *
 * Takes the ROOM-LESS listener -> excluded-sources map the orchestrator produces, a room resolver
 * and the estate room, and splits it into one map per mixer room. Each result map is exactly what
 * PeerCtlBatchSerializer.BuildRequest already accepts, so the sink loops the result and stamps
 * one room per request. Sibling of that serializer: dependency-free beyond OpenMetaverse - no Scene,
 * no Janus, no OSD, no I/O - and directly unit-testable.
 *
 * POLICY FOR A MISSING RECORD. roomOf answers the room the agent's last provision joined, or null when
 * the agent has no record. An agent with no record is OMITTED from every column and every room, and counted.
 * Nothing guesses a room for it: the estate room is a real room in every region, so addressing the agent
 * there would write live exclusion and mute state into a room nobody asked for, and the room its parcel
 * would give it is not evidence that the mixer holds that room (an agent that is only in a group or
 * avatar-to-avatar call never joined it).
 *
 * When every agent has a record and one room holds every listener and every source, the input map is
 * returned as it is: the same instance, unfiltered.
 *
 * Same-room filtering: a listener's column keeps only the sources in the LISTENER's room.
 * A cross-room source is inert at the mixer (room membership already prevents it being heard), so
 * this is lossless, and it puts a per-room column under SLV_MAX_MIX - 1 = 109 BY CONSTRUCTION rather
 * than under a guard that has to be maintained.
 *
 * A listener whose column filters down to nothing KEEPS ITS KEY. An empty source array is a
 * meaningful "clear this listener" on a Replace (the serializer preserves it deliberately), so
 * dropping the key would silently skip the clear.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    /// <summary>The result of one partition: the per-room maps, plus how many DISTINCT agents had no
    /// room record in each role. The counters are per-call, not cumulative; the caller accumulates.</summary>
    public sealed class PeerCtlBatchPartition
    {
        private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> NoRooms
            = new Dictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>>();

        /// <summary>Nothing to send: no rooms, no fallbacks.</summary>
        public static readonly PeerCtlBatchPartition Empty = new PeerCtlBatchPartition(NoRooms, 0, 0);

        internal PeerCtlBatchPartition(
            IReadOnlyDictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> rooms,
            int fallbackListeners, int fallbackSources)
        {
            Rooms = rooms;
            FallbackListeners = fallbackListeners;
            FallbackSources = fallbackSources;
        }

        /// <summary>Room number -> the listener/sources map to send to THAT room. One entry per room
        /// that has at least one listener; empty only when the input was empty.</summary>
        public IReadOnlyDictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> Rooms { get; }

        /// <summary>Distinct listeners in this batch with no room record. They are left out of every room.</summary>
        public int FallbackListeners { get; }

        /// <summary>Distinct sources in this batch with no room record. They are left out of every column.</summary>
        public int FallbackSources { get; }

        /// <summary>How many rooms this send will address.</summary>
        public int RoomCount => Rooms.Count;
    }

    public static class PeerCtlBatchPartitioner
    {
        /// <summary>Split a room-less exclusion map into one map per room, filtering each listener's
        /// column to its own room and counting the missing records per role.</summary>
        /// <param name="excl">The orchestrator's listener -> excluded sources map. Never mutated.</param>
        /// <param name="roomOf">Recorded room per agent, or null for "no record". A NULL RESOLVER is
        /// accepted and means no agent has a record: every agent is left out and both counters read the
        /// full population, rather than throwing on the send path. The counters are what make that
        /// state loud.</param>
        /// <param name="estateRoom">NO LONGER AN ADDRESS. Nothing is bucketed here any more; the
        /// parameter is kept because the sink passes its region room and the callers' shape is unchanged.</param>
        public static PeerCtlBatchPartition Partition(
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            Func<UUID, int?> roomOf,
            int estateRoom)
        {
            if (excl == null)
                throw new ArgumentNullException(nameof(excl));
            if (excl.Count == 0)
                return PeerCtlBatchPartition.Empty;

            // Pass 1: resolve every agent named in the batch ONCE, in either role. The cache matters:
            // a 100x100 matrix would otherwise take 10,000 trips through the table's lock instead of
            // ~200, and it makes the distinct-agent counting below trivial.
            var resolved = new Dictionary<UUID, int>();
            var noRecord = new HashSet<UUID>();
            var seenSources = new HashSet<UUID>();
            int fallbackListeners = 0;
            int fallbackSources = 0;
            int soleRoom = 0;
            bool haveSoleRoom = false;
            bool manyRooms = false;

            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in excl)
            {
                int lr = Resolve(kv.Key, roomOf, resolved, noRecord);
                Track(lr, ref soleRoom, ref haveSoleRoom, ref manyRooms);
                if (noRecord.Contains(kv.Key))
                    fallbackListeners++;

                if (kv.Value == null)
                    continue;
                foreach (UUID s in kv.Value)
                {
                    if (!seenSources.Add(s))
                        continue;                          // distinct sources only - a source named
                    int sr = Resolve(s, roomOf, resolved, noRecord);              // in twenty columns
                    Track(sr, ref soleRoom, ref haveSoleRoom, ref manyRooms);     // is ONE agent
                    if (noRecord.Contains(s))
                        fallbackSources++;
                }
            }

            // Fast path: every agent has a record and one room holds every listener AND every source, so no
            // column can lose a source and the input map is already the answer. Hand it back as-is, with no copy.
            if (!manyRooms && noRecord.Count == 0 && haveSoleRoom)
            {
                var one = new Dictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>>(1)
                {
                    [soleRoom] = excl
                };
                return new PeerCtlBatchPartition(one, fallbackListeners, fallbackSources);
            }

            // Pass 2: bucket the listeners, keeping only same-room sources.
            var work = new Dictionary<int, Dictionary<UUID, IReadOnlyCollection<UUID>>>();
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in excl)
            {
                int lr = resolved[kv.Key];
                if (lr == Unplaced)
                    continue;   // omitted, and already counted in fallbackListeners
                if (!work.TryGetValue(lr, out Dictionary<UUID, IReadOnlyCollection<UUID>> bucket))
                {
                    bucket = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
                    work[lr] = bucket;
                }

                var kept = new List<UUID>();
                if (kv.Value != null)
                {
                    foreach (UUID s in kv.Value)
                    {
                        if (resolved[s] == lr)
                            kept.Add(s);
                    }
                }
                bucket[kv.Key] = kept;                  // kept may be empty: that is an explicit clear
            }

            var rooms = new Dictionary<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>>(work.Count);
            foreach (KeyValuePair<int, Dictionary<UUID, IReadOnlyCollection<UUID>>> kv in work)
                rooms[kv.Key] = kv.Value;

            return new PeerCtlBatchPartition(rooms, fallbackListeners, fallbackSources);
        }

        /// <summary>What an agent with no answer from the resolver is bucketed as. The resolver
        /// now answers with the agent's RECORD, the room its last provision joined, so a null means the
        /// sim has no evidence of a room it is in - and an agent it cannot place is one whose room it must not guess.</summary>
        internal const int Unplaced = int.MinValue;

        private static int Resolve(UUID agent, Func<UUID, int?> roomOf,
                                   Dictionary<UUID, int> resolved, HashSet<UUID> noRecord)
        {
            if (resolved.TryGetValue(agent, out int cached))
                return cached;

            int? record = roomOf?.Invoke(agent);
            int room = record ?? Unplaced;
            if (record == null)
                noRecord.Add(agent);
            resolved[agent] = room;
            return room;
        }

        private static void Track(int room, ref int soleRoom, ref bool haveSoleRoom, ref bool manyRooms)
        {
            if (room == Unplaced)
                return;   // an agent nobody can place is not a room, so it cannot be THE room either
            if (!haveSoleRoom)
            {
                soleRoom = room;
                haveSoleRoom = true;
            }
            else if (room != soleRoom)
            {
                manyRooms = true;
            }
        }
    }
}
