/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for PeerCtlBatchSerializer — the SINGLE producer of the peer_ctl_batch wire body.
 * Because the mixer's Ok does not mean applied, format correctness cannot be verified from
 * responses; it is guarded by invariants HERE, so these tests are the guard's safety net.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class PeerCtlBatchSerializerTests
    {
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

        [Fact(Timeout = 60000)]
        public void OpString_MapsEachOp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("add", PeerCtlBatchSerializer.OpString(VisOp.Add));
            Assert.Equal("remove", PeerCtlBatchSerializer.OpString(VisOp.Remove));
            Assert.Equal("replace", PeerCtlBatchSerializer.OpString(VisOp.Replace));
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_ProducesRoomLessBody_WithRequestOpAndExcl()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap body = PeerCtlBatchSerializer.BuildRequest(VisOp.Add, Excl((1, new[] { 2, 3 })));

            Assert.Equal("peer_ctl_batch", body["request"].AsString());
            Assert.Equal("add", body["op"].AsString());
            // Room-less: the sink stamps "room", the serializer must NOT.
            Assert.False(body.ContainsKey("room"), "serializer must stay room-agnostic");

            var excl = (OSDMap)body["excl"];
            var sources = (OSDArray)excl[Id(1).ToString()];
            Assert.Equal(2, sources.Count);
            Assert.Equal(Id(2).ToString(), sources[0].AsString());
            Assert.Equal(Id(3).ToString(), sources[1].AsString());
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_EmptySourceList_IsPreservedAsExplicitClear()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // An empty source array is a MEANINGFUL "clear this listener" (Replace clear-tracking),
            // not an error — it must survive serialization as an empty array.
            OSDMap body = PeerCtlBatchSerializer.BuildRequest(VisOp.Replace, Excl((1, new int[0])));
            var excl = (OSDMap)body["excl"];
            Assert.True(excl.ContainsKey(Id(1).ToString()));
            Assert.Empty(((OSDArray)excl[Id(1).ToString()]));
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_ZeroListenerUUID_ThrowsInvariant()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var bad = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [UUID.Zero] = new List<UUID> { Id(2) } };
            {
                var assertedValue = () => PeerCtlBatchSerializer.BuildRequest(VisOp.Add, bad);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    assertedValue();
                });
            }
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_ZeroSourceUUID_ThrowsInvariant()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var bad = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [Id(1)] = new List<UUID> { UUID.Zero } };
            {
                var assertedValue = () => PeerCtlBatchSerializer.BuildRequest(VisOp.Add, bad);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    assertedValue();
                });
            }
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_NullExcl_Throws()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = () => PeerCtlBatchSerializer.BuildRequest(VisOp.Add, null);
                Assert.Throws<ArgumentNullException>(() =>
                {
                    assertedValue();
                });
            }
        }

        // ---- EnsureDisjoint: an (L,S) in BOTH add and remove is a feeder bug — must throw ----

        [Fact(Timeout = 60000)]
        public void EnsureDisjoint_OverlappingPair_ThrowsNamingTheOverlap()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var added = Excl((1, new[] { 2, 3 }));
            var removed = Excl((1, new[] { 3 }));   // (listener 1, source 3) in both
            {
                var assertedValue = () => PeerCtlBatchSerializer.EnsureDisjoint(added, removed);
                var thrownException = Assert.Throws<InvalidOperationException>(() =>
                {
                    assertedValue();
                });
                Assert.Contains(Id(3).ToString(), thrownException.Message);
            }
        }

        [Fact(Timeout = 60000)]
        public void EnsureDisjoint_SameListenerDifferentSources_DoesNotThrow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Add source 2, remove source 4 for the same listener — legitimate, disjoint per pair.
            var added = Excl((1, new[] { 2 }));
            var removed = Excl((1, new[] { 4 }));
            {
                var assertedValue = () => PeerCtlBatchSerializer.EnsureDisjoint(added, removed);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
        }

        [Fact(Timeout = 60000)]
        public void EnsureDisjoint_NullSides_DoNotThrow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = () => PeerCtlBatchSerializer.EnsureDisjoint(null, Excl((1, new[] { 2 })));
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            {
                var assertedValue = () => PeerCtlBatchSerializer.EnsureDisjoint(Excl((1, new[] { 2 })), null);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
        }
    }
}
