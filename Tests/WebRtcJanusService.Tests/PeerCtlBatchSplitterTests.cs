/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for PeerCtlBatchSplitter: a room's request is cut between listeners so that each part is within the
 * 65,536 bytes of compact JSON the slvoice mixer accepts (SLV_VISBATCH_MAX_BYTES), and a request that fits is not cut.
 * The size the splitter works from is checked against the size of the request the serializer really builds.
 *
 * The classes are independent of each other and share no state, so they run in parallel.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class PeerCtlBatchSplitterTests
    {
        private const int Limit = PeerCtlBatchSplitter.MixerMaxRequestBytes;
        private const int Room = 1234567;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        /// <summary>A room of <paramref name="listeners"/> avatars where each listener's column names the next
        /// <paramref name="perColumn"/> others.</summary>
        internal static Dictionary<UUID, IReadOnlyCollection<UUID>> Column(int listeners, int perColumn, int firstId = 1)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            for (int i = 0; i < listeners; i++)
            {
                var sources = new List<UUID>(perColumn);
                for (int j = 1; j <= perColumn; j++)
                    sources.Add(Id(firstId + (i + j) % listeners));
                d[Id(firstId + i)] = sources;
            }
            return d;
        }

        // The request the sink builds for one part, with the arming fields it adds at realistic values.
        private static OSDMap Build(VisOp op, PeerCtlBatchPart part, bool armed)
        {
            OSDMap request = PeerCtlBatchSerializer.BuildRequest(op, part.Excl, part.Mute);
            request["room"] = new OSDInteger(Room);
            if (armed)
            {
                request["room_epoch"] = OSD.FromString("0000018f00000001");
                request["policy_generation"] = OSD.FromInteger(12345);
                if (op != VisOp.Replace)
                {
                    var named = new OSDMap();
                    foreach (UUID l in part.Excl.Keys.Concat(part.Mute?.Keys ?? Enumerable.Empty<UUID>()).Distinct())
                        named[l.ToString()] = OSD.FromInteger(12345);
                    request["base"] = named;
                }
            }
            return request;
        }

        private static int Size(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
                                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute)
            => PeerCtlBatchSplitter.CompactBytes(Build(op, new PeerCtlBatchPart(excl, mute), false));

        // ---- the size reference -------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void CompactBytes_CountsTheJsonTheMixerMeasures()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var map = new OSDMap
            {
                ["request"] = OSD.FromString("peer_ctl_batch"),
                ["n"] = OSD.FromInteger(-12),
                ["a"] = new OSDArray { OSD.FromString("x\"y"), OSD.FromBoolean(true) },
                ["e"] = new OSDMap()
            };
            // {"request":"peer_ctl_batch","n":-12,"a":["x\"y",true],"e":{}}
            Assert.Equal(61, PeerCtlBatchSplitter.CompactBytes(map));
        }

        // ---- a request that fits is not cut ---------------------------------------------------------

        [InlineData(VisOp.Replace)]
        [InlineData(VisOp.Add)]
        [InlineData(VisOp.Remove)]
        [Theory(Timeout = 60000)]
        public void Split_ARequestThatFits_IsOnePartHoldingTheMapsItWasGiven(VisOp op)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var excl = Column(8, 5);
            var mute = Column(8, 2);

            IReadOnlyList<PeerCtlBatchPart> parts = PeerCtlBatchSplitter.Split(op, excl, mute, Room, armed: true);

            Assert.Single(parts);
            Assert.Same(excl, parts[0].Excl);
            Assert.Same(mute, parts[0].Mute);
        }

        [Fact(Timeout = 60000)]
        public void Split_NothingToSend_IsOneEmptyPart()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PeerCtlBatchPart> parts = PeerCtlBatchSplitter.Split(VisOp.Replace,
                new Dictionary<UUID, IReadOnlyCollection<UUID>>(), null, Room, armed: false);

            Assert.Single(parts);
            Assert.Empty(parts[0].Excl);
        }

        // ---- the boundary ------------------------------------------------------------------------------

        // Without arming the model is exact, so a request of exactly the limit is one part and a limit one byte smaller cuts it.
        [InlineData(VisOp.Replace,false)]
        [InlineData(VisOp.Replace,true)]
        [InlineData(VisOp.Add,false)]
        [InlineData(VisOp.Add,true)]
        [InlineData(VisOp.Remove,true)]
        [Theory(Timeout = 60000)]
        public void Split_ARequestOfExactlyTheLimit_IsNotCut_AndOneByteLessIs(VisOp op, bool withMute)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var excl = Column(40, 30);
            var mute = withMute ? Column(40, 7, firstId: 1) : null;
            int exact = Size(op, excl, mute);

            IReadOnlyList<PeerCtlBatchPart> whole = PeerCtlBatchSplitter.Split(op, excl, mute, Room, armed: false, limit: exact);
            IReadOnlyList<PeerCtlBatchPart> cut = PeerCtlBatchSplitter.Split(op, excl, mute, Room, armed: false, limit: exact - 1);

            {
                var assertedValue = whole.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "a request of exactly the limit is accepted by the mixer");
            }
            Assert.True(cut.Count > 1);
            foreach (PeerCtlBatchPart p in cut)
                Assert.True(PeerCtlBatchSplitter.CompactBytes(Build(op, p, false)) <= exact - 1);
        }

        // At the real limit: the most listeners with a full column of 109 that fit are one part, and one more listener is two.
        [Fact(Timeout = 60000)]
        public void Split_AtTheMixersLimit_TheLastListenerThatFitsIsTheBoundary()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            int fits = 0;
            while (Size(VisOp.Replace, Fill(fits + 1), null) <= Limit)
                fits++;
            {
                var assertedValue = fits;
                TestAssert.WithMessage(() => Assert.InRange(assertedValue, 10, 20), "about 4.3 KB for each listener");
            }

            // 109 sources need 110 distinct avatars in the room
            Assert.Single(PeerCtlBatchSplitter.Split(VisOp.Replace, Fill(fits), null, Room, false));
            IReadOnlyList<PeerCtlBatchPart> over = PeerCtlBatchSplitter.Split(VisOp.Replace, Fill(fits + 1), null, Room, false);
            Assert.Equal(2, over.Count);
            Assert.Equal(fits, over[0].Excl.Count);
            Assert.Single(over[1].Excl);
        }

        // `listeners` listeners, each excluding the 109 others of a room of 110.
        private static Dictionary<UUID, IReadOnlyCollection<UUID>> Fill(int listeners)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            for (int i = 0; i < listeners; i++)
            {
                var others = new List<UUID>(109);
                for (int j = 0; j < 110; j++)
                    if (j != i)
                        others.Add(Id(1000 + j));
                d[Id(1000 + i)] = others;
            }
            return d;
        }

        // ---- a room of 110 where everyone excludes or mutes everyone --------------------------------------

        private static Dictionary<UUID, IReadOnlyCollection<UUID>> Everyone(int room = 110)
        {
            var d = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            for (int i = 0; i < room; i++)
            {
                var others = new List<UUID>(room - 1);
                for (int j = 0; j < room; j++)
                    if (j != i)
                        others.Add(Id(1 + j));
                d[Id(1 + i)] = others;
            }
            return d;
        }

        [InlineData(VisOp.Replace,false)]
        [InlineData(VisOp.Replace,true)]
        [InlineData(VisOp.Add,true)]
        [InlineData(VisOp.Remove,false)]
        [Theory(Timeout = 60000)]
        public void Split_ARoomOf110WhereEveryoneExcludesEveryone_IsPartsThatFitAndCoverEveryListenerOnce(VisOp op, bool armed)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var excl = Everyone();
            Assert.True(Size(op, excl, null) > Limit * 7, "the request this guards against is about 472 KB");

            IReadOnlyList<PeerCtlBatchPart> parts = PeerCtlBatchSplitter.Split(op, excl, null, Room, armed);

            AssertParts(op, parts, excl, null, armed);
        }

        [InlineData(VisOp.Replace,false)]
        [InlineData(VisOp.Replace,true)]
        [InlineData(VisOp.Add,true)]
        [Theory(Timeout = 60000)]
        public void Split_ARoomOf110WhereEveryoneMutesEveryone_IsPartsThatFitAndCoverEveryListenerOnce(VisOp op, bool armed)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var mute = Everyone();

            IReadOnlyList<PeerCtlBatchPart> parts = PeerCtlBatchSplitter.Split(op, new Dictionary<UUID, IReadOnlyCollection<UUID>>(), mute, Room, armed);

            Assert.True(parts.Count > 1);
            AssertParts(op, parts, new Dictionary<UUID, IReadOnlyCollection<UUID>>(), mute, armed);
        }

        [Fact(Timeout = 60000)]
        public void Split_AListenerKeepsItsExclAndItsMuteInTheSamePart()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // 110 listeners; 55 of the sources in each column are excluded, 54 are muted (disjoint, as the matrix builds them)
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var mute = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            foreach (var kv in Everyone())
            {
                excl[kv.Key] = kv.Value.Take(55).ToList();
                mute[kv.Key] = kv.Value.Skip(55).ToList();
            }

            IReadOnlyList<PeerCtlBatchPart> parts = PeerCtlBatchSplitter.Split(VisOp.Replace, excl, mute, Room, armed: true);

            Assert.True(parts.Count > 1);
            foreach (PeerCtlBatchPart p in parts)
                {
                    var assertedValue = p.Mute.Keys;
                    TestAssert.WithMessage(() => Assert.Equivalent(p.Excl.Keys, assertedValue, strict: true), "one part holds both columns of each listener it names");
                }
            AssertParts(VisOp.Replace, parts, excl, mute, armed: true);
        }

        [Fact(Timeout = 60000)]
        public void Split_AListenerWithOnlyAMuteColumnIsKept_AndAnAddPartWithoutMutesHasNoMuteKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var excl = Everyone(60);                  // 60 listeners with excl columns, the early parts
            var mute = new Dictionary<UUID, IReadOnlyCollection<UUID>>
            {
                [Id(9000)] = new List<UUID> { Id(1) } // one listener that has only a mute column, the last part
            };

            IReadOnlyList<PeerCtlBatchPart> parts = PeerCtlBatchSplitter.Split(VisOp.Add, excl, mute, Room, armed: false);

            Assert.True(parts.Count > 1);
            {
                var assertedValue = parts[0].Mute;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no listener in the first part has a mute column");
            }
            Assert.Equivalent(new[] { Id(9000) }, parts[parts.Count - 1].Mute.Keys, strict: true);
            Assert.False(PeerCtlBatchSerializer.BuildRequest(VisOp.Add, parts[0].Excl, parts[0].Mute).ContainsKey("mute"));
            AssertParts(VisOp.Add, parts, excl, mute, armed: false);
        }

        [Fact(Timeout = 60000)]
        public void Split_AnEmptyColumnIsKeptWithItsKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // a replace that clears 200 listeners is 200 keys with empty arrays, which is a clear, not nothing
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            for (int i = 0; i < 2000; i++)
                excl[Id(i + 1)] = Array.Empty<UUID>();

            IReadOnlyList<PeerCtlBatchPart> parts = PeerCtlBatchSplitter.Split(VisOp.Replace, excl, null, Room, armed: true);

            Assert.True(parts.Count > 1);
            AssertParts(VisOp.Replace, parts, excl, null, armed: true);
            Assert.Equal(2000, parts.Sum(p => p.Excl.Count));
        }

        // Every listener in exactly one part with the same columns, in the order given, every part within the limit.
        private static void AssertParts(VisOp op, IReadOnlyList<PeerCtlBatchPart> parts,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute, bool armed)
        {
            var seenExcl = new List<UUID>();
            var seenMute = new List<UUID>();
            foreach (PeerCtlBatchPart p in parts)
            {
                Assert.True(PeerCtlBatchSplitter.CompactBytes(Build(op, p, armed)) <= Limit);
                foreach (var kv in p.Excl)
                {
                    seenExcl.Add(kv.Key);
                    Assert.Same(excl[kv.Key], kv.Value);
                }
                if (p.Mute != null)
                    foreach (var kv in p.Mute)
                    {
                        seenMute.Add(kv.Key);
                        Assert.Same(mute[kv.Key], kv.Value);
                    }
            }
            {
                var assertedValue = seenExcl;
                TestAssert.WithMessage(() => Assert.Equal(excl.Keys.ToList(), assertedValue), "every excl listener once, in order");
            }
            {
                var assertedValue = seenMute;
                TestAssert.WithMessage(() => Assert.Equal((mute?.Keys ?? Enumerable.Empty<UUID>()).ToList(), assertedValue), "every mute listener once, in order");
            }
            if (armed)
                return;   // the splitter allows the arming fields at their longest; the reference here uses real ones
            // the parts are as few as they can be: joining any two neighbours would be over the limit
            for (int i = 0; i + 1 < parts.Count; i++)
            {
                var e = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
                foreach (var kv in parts[i].Excl) e[kv.Key] = kv.Value;
                Dictionary<UUID, IReadOnlyCollection<UUID>> m = null;
                if (parts[i].Mute != null) m = new Dictionary<UUID, IReadOnlyCollection<UUID>>(parts[i].Mute.ToDictionary(k => k.Key, k => k.Value));
                var next = parts[i + 1];
                foreach (var kv in next.Excl) { e[kv.Key] = kv.Value; break; }
                if (next.Mute != null) foreach (var kv in next.Mute) { (m ??= new Dictionary<UUID, IReadOnlyCollection<UUID>>())[kv.Key] = kv.Value; break; }
                var joined = new PeerCtlBatchPart(e, m);
                Assert.True(PeerCtlBatchSplitter.CompactBytes(Build(op, joined, armed)) > Limit, "part " + i + " could have taken the next listener");
            }
        }
    }
}
