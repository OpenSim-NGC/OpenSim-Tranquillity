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
    [TestFixture]
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

        [Test]
        public void OpString_MapsEachOp()
        {
            Assert.That(PeerCtlBatchSerializer.OpString(VisOp.Add), Is.EqualTo("add"));
            Assert.That(PeerCtlBatchSerializer.OpString(VisOp.Remove), Is.EqualTo("remove"));
            Assert.That(PeerCtlBatchSerializer.OpString(VisOp.Replace), Is.EqualTo("replace"));
        }

        [Test]
        public void BuildRequest_ProducesRoomLessBody_WithRequestOpAndExcl()
        {
            OSDMap body = PeerCtlBatchSerializer.BuildRequest(VisOp.Add, Excl((1, new[] { 2, 3 })));

            Assert.That(body["request"].AsString(), Is.EqualTo("peer_ctl_batch"));
            Assert.That(body["op"].AsString(), Is.EqualTo("add"));
            // Room-less: the sink stamps "room", the serializer must NOT.
            Assert.That(body.ContainsKey("room"), Is.False, "serializer must stay room-agnostic");

            var excl = (OSDMap)body["excl"];
            var sources = (OSDArray)excl[Id(1).ToString()];
            Assert.That(sources.Count, Is.EqualTo(2));
            Assert.That(sources[0].AsString(), Is.EqualTo(Id(2).ToString()));
            Assert.That(sources[1].AsString(), Is.EqualTo(Id(3).ToString()));
        }

        [Test]
        public void BuildRequest_EmptySourceList_IsPreservedAsExplicitClear()
        {
            // An empty source array is a MEANINGFUL "clear this listener" (Replace clear-tracking),
            // not an error — it must survive serialization as an empty array.
            OSDMap body = PeerCtlBatchSerializer.BuildRequest(VisOp.Replace, Excl((1, new int[0])));
            var excl = (OSDMap)body["excl"];
            Assert.That(excl.ContainsKey(Id(1).ToString()), Is.True);
            Assert.That(((OSDArray)excl[Id(1).ToString()]).Count, Is.EqualTo(0));
        }

        [Test]
        public void BuildRequest_ZeroListenerUUID_ThrowsInvariant()
        {
            var bad = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [UUID.Zero] = new List<UUID> { Id(2) } };
            Assert.That(() => PeerCtlBatchSerializer.BuildRequest(VisOp.Add, bad),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void BuildRequest_ZeroSourceUUID_ThrowsInvariant()
        {
            var bad = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [Id(1)] = new List<UUID> { UUID.Zero } };
            Assert.That(() => PeerCtlBatchSerializer.BuildRequest(VisOp.Add, bad),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void BuildRequest_NullExcl_Throws()
        {
            Assert.That(() => PeerCtlBatchSerializer.BuildRequest(VisOp.Add, null),
                Throws.TypeOf<ArgumentNullException>());
        }

        // ---- EnsureDisjoint: an (L,S) in BOTH add and remove is a feeder bug — must throw ----

        [Test]
        public void EnsureDisjoint_OverlappingPair_ThrowsNamingTheOverlap()
        {
            var added = Excl((1, new[] { 2, 3 }));
            var removed = Excl((1, new[] { 3 }));   // (listener 1, source 3) in both
            Assert.That(() => PeerCtlBatchSerializer.EnsureDisjoint(added, removed),
                Throws.TypeOf<InvalidOperationException>()
                      .With.Message.Contains(Id(3).ToString()));
        }

        [Test]
        public void EnsureDisjoint_SameListenerDifferentSources_DoesNotThrow()
        {
            // Add source 2, remove source 4 for the same listener — legitimate, disjoint per pair.
            var added = Excl((1, new[] { 2 }));
            var removed = Excl((1, new[] { 4 }));
            Assert.That(() => PeerCtlBatchSerializer.EnsureDisjoint(added, removed), Throws.Nothing);
        }

        [Test]
        public void EnsureDisjoint_NullSides_DoNotThrow()
        {
            Assert.That(() => PeerCtlBatchSerializer.EnsureDisjoint(null, Excl((1, new[] { 2 }))), Throws.Nothing);
            Assert.That(() => PeerCtlBatchSerializer.EnsureDisjoint(Excl((1, new[] { 2 })), null), Throws.Nothing);
        }
    }
}
