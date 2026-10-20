/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for PeerCtlBatchPartitioner - the per-room split that makes SLV_VIS_MAX_EXCL
 * unreachable by construction (design, OQ2/OQ4 and the missing-record resolution).
 *
 * The load-bearing cases are the two fallbacks. A missing room record resolves to the estate room
 * for BOTH roles, and the pair of counters is what tells an operator which state a deployment is
 * in: fallback_listeners non-zero means agents the table does not cover, fallback_sources non-zero
 * means a service that does not yet report the room it joined.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class PeerCtlBatchPartitionerTests
    {
        private const int EstateRoom = 9000;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        private static Dictionary<UUID, IReadOnlyCollection<UUID>> Excl(params (int listener, int[] sources)[] rows)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            foreach (var (listener, sources) in rows)
            {
                var list = new List<UUID>();
                foreach (int s in sources) list.Add(Id(s));
                d[Id(listener)] = list;
            }
            return d;
        }

        /// <summary>A resolver over a fixed agent-number -> room table; any agent not named has NO
        /// record, exactly as AgentRoomTable.Resolve returns null for one it never recorded.</summary>
        private static Func<UUID, int?> Resolver(params (int agent, int room)[] records)
        {
            var table = new Dictionary<UUID, int>();
            foreach (var (agent, room) in records) table[Id(agent)] = room;
            return a => table.TryGetValue(a, out int r) ? r : (int?)null;
        }

        private static IReadOnlyCollection<UUID> Column(PeerCtlBatchPartition p, int room, int listener)
            => p.Rooms[room][Id(listener)];

        // ---- partitioning across rooms ----

        [Test]
        public void Partition_SplitsListenersByTheirOwnRoom()
        {
            // Listeners 1 and 2 in room 100; listener 3 in room 200. Sources co-located so nothing
            // is filtered - this test is about the split alone.
            var excl = Excl((1, new[] { 2 }), (2, new[] { 1 }), (3, new[] { 4 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200), (4, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(2));
            Assert.That(p.Rooms[100].Count, Is.EqualTo(2));
            Assert.That(p.Rooms[200].Count, Is.EqualTo(1));
            Assert.That(p.Rooms[100].ContainsKey(Id(3)), Is.False, "listener 3 belongs to room 200 only");
            Assert.That(Column(p, 200, 3), Is.EquivalentTo(new[] { Id(4) }));
            Assert.That(p.FallbackListeners, Is.Zero);
            Assert.That(p.FallbackSources, Is.Zero);
        }

        // ---- same-room source filtering (OQ2a) ----

        [Test]
        public void Partition_KeepsOnlySourcesInTheListenersRoom()
        {
            // Listener 1 (room 100) excludes sources 2 (room 100) and 3 (room 200). The cross-room
            // source is inert at the mixer, so it must not travel.
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(1), "only listener 1 is a listener, so only its room is addressed");
            Assert.That(Column(p, 100, 1), Is.EquivalentTo(new[] { Id(2) }));
        }

        [Test]
        public void Partition_ListenerWhoseColumnFiltersEmpty_KeepsItsKey()
        {
            // Every source is in another room. The key must SURVIVE with an empty column: on a
            // Replace that empty array is the meaningful "clear this listener", and dropping the
            // key would silently skip the clear.
            var excl = Excl((1, new[] { 3, 4 }), (3, new[] { 4 }));
            var roomOf = Resolver((1, 100), (3, 200), (4, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.Rooms[100].ContainsKey(Id(1)), Is.True);
            Assert.That(Column(p, 100, 1).Count, Is.Zero);
        }

        // ---- fallback: listener with no record (OQ4a) ----

        [Test]
        public void Partition_ListenerTheResolverCannotPlace_IsOmittedAndCounted()
        {
            // 2, the contract this test used to pin the other way round: the resolver now answers
            // "the record, else the room this agent's parcel would provision it into", so a null means the sim cannot
            // place the agent at all. An agent it cannot place is NOT addressed at the estate number because that is
            // the default - it is left out, and counted.
            var excl = Excl((1, new[] { 2 }), (5, new[] { 6 }));
            var roomOf = Resolver((1, 100), (2, 100), (6, 100));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.Rooms.ContainsKey(EstateRoom), Is.False, "nothing is addressed at the estate number");
            Assert.That(p.Rooms[100].ContainsKey(Id(5)), Is.False, "and the unplaced listener is nowhere else either");
            Assert.That(p.FallbackListeners, Is.EqualTo(1), "it is counted, so the state is loud rather than silent");
            Assert.That(p.FallbackSources, Is.Zero, "every source here has a record");
        }

        // ---- fallback: source with no record (the resolution that supersedes OQ2's draft) ----

        [Test]
        public void Partition_SourceTheResolverCannotPlace_IsFilteredOutOfEveryColumn_AndCounted()
        {
            // 2: a source the sim cannot place is in no room, so it survives in no column. Before 0.8c2 it
            // was treated as an estate-room source and kept for estate-room listeners; that was the guess this change
            // removes. A source resolved to the estate room by its PARCEL still behaves exactly as it did - that is
            // the case below, where listener 7 and source 9 are both placed there.
            var excl = Excl((1, new[] { 9 }), (7, new[] { 9 }));
            var roomOf = Resolver((1, 100), (7, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(Column(p, EstateRoom, 7).Count, Is.Zero, "the unplaced source is in nobody's column");
            Assert.That(Column(p, 100, 1).Count, Is.Zero);
            Assert.That(p.FallbackSources, Is.EqualTo(1), "counted once");
            Assert.That(p.FallbackListeners, Is.Zero, "listener 7's estate room is RECORDED, not a guess");

            // The same shape with source 9 PLACED at the estate room by resolution: kept for 7, filtered for 1.
            PeerCtlBatchPartition placed = PeerCtlBatchPartitioner.Partition(
                excl, Resolver((1, 100), (7, EstateRoom), (9, EstateRoom)), EstateRoom);
            Assert.That(Column(placed, EstateRoom, 7), Is.EquivalentTo(new[] { Id(9) }));
            Assert.That(Column(placed, 100, 1).Count, Is.Zero);
            Assert.That(placed.FallbackSources, Is.Zero);
        }

        [Test]
        public void Partition_CountsEachRoleSeparately_ForAnAgentThatIsBoth()
        {
            // Agent 5 has no record and appears as a listener AND as a source. The counters are
            // per-role, so it reads once in each - not once overall, and not twice in one.
            var excl = Excl((5, new[] { 1 }), (1, new[] { 5 }));
            var roomOf = Resolver((1, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.FallbackListeners, Is.EqualTo(1));
            Assert.That(p.FallbackSources, Is.EqualTo(1));
        }

        [Test]
        public void Partition_CountsDistinctSources_NotOccurrences()
        {
            // The unrecorded source 9 is named in three columns. It is ONE agent missing a record;
            // counting occurrences would scale the counter with column fan-out and make "reads zero
            // on a fully upgraded deployment" the only interpretable reading it has.
            var excl = Excl((1, new[] { 9 }), (2, new[] { 9 }), (3, new[] { 9 }));
            var roomOf = Resolver((1, EstateRoom), (2, EstateRoom), (3, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.FallbackSources, Is.EqualTo(1));
        }

        // ---- empty map ----

        [Test]
        public void Partition_EmptyMap_YieldsNoRoomsAndNoFallbacks()
        {
            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(
                new Dictionary<UUID, IReadOnlyCollection<UUID>>(), Resolver(), EstateRoom);

            Assert.That(p.RoomCount, Is.Zero);
            Assert.That(p.Rooms.Count, Is.Zero);
            Assert.That(p.FallbackListeners, Is.Zero);
            Assert.That(p.FallbackSources, Is.Zero);
        }

        // ---- single-room fast path ----

        [Test]
        public void Partition_SingleRoom_ReturnsOneBucket_WithTheInputMapItself()
        {
            // Everyone in room 100: no column can lose a source, so the input map IS the answer and
            // is handed back uncopied.
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 100));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(p.RoomCount, Is.EqualTo(1));
            Assert.That(p.Rooms[100], Is.SameAs(excl), "fast path must not copy");
        }

        [Test]
        public void Partition_NobodyCanBePlaced_SendsNothingAtAll_AndCountsEveryone()
        {
            // 2: with no answer for anyone there is nothing to address. The old behaviour - one estate-room
            // batch carrying everybody - was the guess that sent live policy to a room that may never have existed
            // . The counters carry the whole population so the state is impossible to miss.
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1, 3 }));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, Resolver(), EstateRoom);

            Assert.That(p.RoomCount, Is.Zero, "no room is addressed");
            Assert.That(p.FallbackListeners, Is.EqualTo(2));
            Assert.That(p.FallbackSources, Is.EqualTo(3), "sources 1, 2 and 3, counted once each");
        }

        [Test]
        public void Partition_NullResolver_SendsNothing_AndShouts()
        {
            // An unwired sink must not throw on the send path. 2: it now sends NOTHING rather than sending
            // everything to a guessed room, and both counters shout. Until the service assigns the resolver there is
            // no honest address for anyone.
            var excl = Excl((1, new[] { 2 }));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, null, EstateRoom);

            Assert.That(p.RoomCount, Is.Zero);
            Assert.That(p.FallbackListeners, Is.EqualTo(1));
            Assert.That(p.FallbackSources, Is.EqualTo(1));
        }

        // ---- guards and non-mutation ----

        [Test]
        public void Partition_NullExcl_Throws()
        {
            Assert.That(() => PeerCtlBatchPartitioner.Partition(null, Resolver(), EstateRoom),
                Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void Partition_NullColumn_ReadsAsAnEmptyColumn()
        {
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [Id(1)] = null, [Id(2)] = new List<UUID> { Id(1) } };
            var roomOf = Resolver((1, 100), (2, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(Column(p, 100, 1).Count, Is.Zero);
        }

        [Test]
        public void Partition_DoesNotMutateTheInput()
        {
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.That(excl.Count, Is.EqualTo(1));
            Assert.That(excl[Id(1)], Is.EquivalentTo(new[] { Id(2), Id(3) }), "the caller's column is untouched");
        }

        [Test]
        public void Partition_ResultColumnsAreWhatTheSerializerAccepts()
        {
            // The contract with: each per-room map goes straight into BuildRequest, which stays
            // room-agnostic - the sink stamps the room key alongside.
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            foreach (KeyValuePair<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> room in p.Rooms)
            {
                var body = PeerCtlBatchSerializer.BuildRequest(VisOp.Replace, room.Value);
                Assert.That(body["request"].AsString(), Is.EqualTo("peer_ctl_batch"));
                Assert.That(body.ContainsKey("room"), Is.False);
            }
        }
    }
}
