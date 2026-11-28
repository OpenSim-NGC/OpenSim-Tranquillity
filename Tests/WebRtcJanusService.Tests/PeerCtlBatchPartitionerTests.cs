/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for PeerCtlBatchPartitioner - the per-room split that makes SLV_VIS_MAX_EXCL
 * unreachable by construction.
 *
 * The load-bearing cases are the two roles with no record. The resolver answers the room an agent's
 * last provision joined, or null when the agent has no record; an agent with no record is left out of
 * every column and every room, and counted. The pair of counters is what tells an operator which
 * state a deployment is in: fallback_listeners non-zero means listeners with no record,
 * fallback_sources non-zero means sources with no record.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void Partition_SplitsListenersByTheirOwnRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Listeners 1 and 2 in room 100; listener 3 in room 200. Sources co-located so nothing
            // is filtered - this test is about the split alone.
            var excl = Excl((1, new[] { 2 }), (2, new[] { 1 }), (3, new[] { 4 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200), (4, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.Equal(2, p.RoomCount);
            Assert.Equal(2, p.Rooms[100].Count);
            Assert.Single(p.Rooms[200]);
            Assert.False(p.Rooms[100].ContainsKey(Id(3)), "listener 3 belongs to room 200 only");
            Assert.Equivalent(new[] { Id(4) }, Column(p, 200, 3), strict: true);
            Assert.Equal(0, p.FallbackListeners);
            Assert.Equal(0, p.FallbackSources);
        }

        // ---- same-room source filtering ----

        [Fact(Timeout = 60000)]
        public void Partition_KeepsOnlySourcesInTheListenersRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Listener 1 (room 100) excludes sources 2 (room 100) and 3 (room 200). The cross-room
            // source is inert at the mixer, so it must not travel.
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            {
                var assertedValue = p.RoomCount;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "only listener 1 is a listener, so only its room is addressed");
            }
            Assert.Equivalent(new[] { Id(2) }, Column(p, 100, 1), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void Partition_ListenerWhoseColumnFiltersEmpty_KeepsItsKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Every source is in another room. The key must SURVIVE with an empty column: on a
            // Replace that empty array is the meaningful "clear this listener", and dropping the
            // key would silently skip the clear.
            var excl = Excl((1, new[] { 3, 4 }), (3, new[] { 4 }));
            var roomOf = Resolver((1, 100), (3, 200), (4, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.True(p.Rooms[100].ContainsKey(Id(1)));
            Assert.Empty(Column(p, 100, 1));
        }

        // ---- listener with no record ----

        [Fact(Timeout = 60000)]
        public void Partition_ListenerTheResolverCannotPlace_IsOmittedAndCounted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The resolver answers the agent's record, so a null means the agent has no record. An agent
            // with no record is NOT addressed at the estate number because that is the default - it is
            // left out, and counted.
            var excl = Excl((1, new[] { 2 }), (5, new[] { 6 }));
            var roomOf = Resolver((1, 100), (2, 100), (6, 100));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.False(p.Rooms.ContainsKey(EstateRoom), "nothing is addressed at the estate number");
            Assert.False(p.Rooms[100].ContainsKey(Id(5)), "and the unplaced listener is nowhere else either");
            {
                var assertedValue = p.FallbackListeners;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "it is counted, so the state is loud rather than silent");
            }
            {
                var assertedValue = p.FallbackSources;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "every source here has a record");
            }
        }

        // ---- source with no record ----

        [Fact(Timeout = 60000)]
        public void Partition_SourceTheResolverCannotPlace_IsFilteredOutOfEveryColumn_AndCounted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A source with no record is in no room, so it survives in no column. A source whose record is
            // the estate room is kept for listeners recorded there - that is the case below, where
            // listener 7 and source 9 both have the estate room.
            var excl = Excl((1, new[] { 9 }), (7, new[] { 9 }));
            var roomOf = Resolver((1, 100), (7, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            {
                var assertedValue = Column(p, EstateRoom, 7).Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the unplaced source is in nobody's column");
            }
            Assert.Empty(Column(p, 100, 1));
            {
                var assertedValue = p.FallbackSources;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "counted once");
            }
            {
                var assertedValue = p.FallbackListeners;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "listener 7's estate room is RECORDED, not a guess");
            }

            // The same shape with source 9 recorded in the estate room: kept for 7, filtered for 1.
            PeerCtlBatchPartition placed = PeerCtlBatchPartitioner.Partition(
                excl, Resolver((1, 100), (7, EstateRoom), (9, EstateRoom)), EstateRoom);
            Assert.Equivalent(new[] { Id(9) }, Column(placed, EstateRoom, 7), strict: true);
            Assert.Empty(Column(placed, 100, 1));
            Assert.Equal(0, placed.FallbackSources);
        }

        [Fact(Timeout = 60000)]
        public void Partition_CountsEachRoleSeparately_ForAnAgentThatIsBoth()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Agent 5 has no record and appears as a listener AND as a source. The counters are
            // per-role, so it reads once in each - not once overall, and not twice in one.
            var excl = Excl((5, new[] { 1 }), (1, new[] { 5 }));
            var roomOf = Resolver((1, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.Equal(1, p.FallbackListeners);
            Assert.Equal(1, p.FallbackSources);
        }

        [Fact(Timeout = 60000)]
        public void Partition_CountsDistinctSources_NotOccurrences()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The unrecorded source 9 is named in three columns. It is ONE agent missing a record;
            // counting occurrences would scale the counter with column fan-out and make "reads zero
            // on a fully upgraded deployment" the only interpretable reading it has.
            var excl = Excl((1, new[] { 9 }), (2, new[] { 9 }), (3, new[] { 9 }));
            var roomOf = Resolver((1, EstateRoom), (2, EstateRoom), (3, EstateRoom));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.Equal(1, p.FallbackSources);
        }

        // ---- empty map ----

        [Fact(Timeout = 60000)]
        public void Partition_EmptyMap_YieldsNoRoomsAndNoFallbacks()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(
                new Dictionary<UUID, IReadOnlyCollection<UUID>>(), Resolver(), EstateRoom);

            Assert.Equal(0, p.RoomCount);
            Assert.Empty(p.Rooms);
            Assert.Equal(0, p.FallbackListeners);
            Assert.Equal(0, p.FallbackSources);
        }

        // ---- single-room fast path ----

        [Fact(Timeout = 60000)]
        public void Partition_SingleRoom_ReturnsOneBucket_WithTheInputMapItself()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Everyone in room 100: no column can lose a source, so the input map IS the answer and
            // is handed back uncopied.
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 100));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.Equal(1, p.RoomCount);
            {
                var assertedValue = p.Rooms[100];
                TestAssert.WithMessage(() => Assert.Same(excl, assertedValue), "fast path must not copy");
            }
        }

        [Fact(Timeout = 60000)]
        public void Partition_NobodyCanBePlaced_SendsNothingAtAll_AndCountsEveryone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // With no record for anyone there is nothing to address: sending one estate-room batch carrying
            // everybody would send live policy to a room that may not exist. The counters carry the whole
            // population so the state is impossible to miss.
            var excl = Excl((1, new[] { 2, 3 }), (2, new[] { 1, 3 }));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, Resolver(), EstateRoom);

            {
                var assertedValue = p.RoomCount;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no room is addressed");
            }
            Assert.Equal(2, p.FallbackListeners);
            {
                var assertedValue = p.FallbackSources;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "sources 1, 2 and 3, counted once each");
            }
        }

        [Fact(Timeout = 60000)]
        public void Partition_NullResolver_SendsNothing_AndShouts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // An unwired sink must not throw on the send path. It sends NOTHING rather than sending
            // everything to a guessed room, and both counters shout. Until the service assigns the resolver there is
            // no honest address for anyone.
            var excl = Excl((1, new[] { 2 }));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, null, EstateRoom);

            Assert.Equal(0, p.RoomCount);
            Assert.Equal(1, p.FallbackListeners);
            Assert.Equal(1, p.FallbackSources);
        }

        // ---- guards and non-mutation ----

        [Fact(Timeout = 60000)]
        public void Partition_NullExcl_Throws()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = () => PeerCtlBatchPartitioner.Partition(null, Resolver(), EstateRoom);
                Assert.Throws<ArgumentNullException>(() =>
                {
                    assertedValue();
                });
            }
        }

        [Fact(Timeout = 60000)]
        public void Partition_NullColumn_ReadsAsAnEmptyColumn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [Id(1)] = null, [Id(2)] = new List<UUID> { Id(1) } };
            var roomOf = Resolver((1, 100), (2, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.Empty(Column(p, 100, 1));
        }

        [Fact(Timeout = 60000)]
        public void Partition_DoesNotMutateTheInput()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            Assert.Single(excl);
            {
                var assertedValue = excl[Id(1)];
                TestAssert.WithMessage(() => Assert.Equivalent(new[] { Id(2), Id(3) }, assertedValue, strict: true), "the caller's column is untouched");
            }
        }

        [Fact(Timeout = 60000)]
        public void Partition_ResultColumnsAreWhatTheSerializerAccepts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The contract with: each per-room map goes straight into BuildRequest, which stays
            // room-agnostic - the sink stamps the room key alongside.
            var excl = Excl((1, new[] { 2, 3 }));
            var roomOf = Resolver((1, 100), (2, 100), (3, 200));

            PeerCtlBatchPartition p = PeerCtlBatchPartitioner.Partition(excl, roomOf, EstateRoom);

            foreach (KeyValuePair<int, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>>> room in p.Rooms)
            {
                var body = PeerCtlBatchSerializer.BuildRequest(VisOp.Replace, room.Value);
                Assert.Equal("peer_ctl_batch", body["request"].AsString());
                Assert.False(body.ContainsKey("room"));
            }
        }
    }
}
