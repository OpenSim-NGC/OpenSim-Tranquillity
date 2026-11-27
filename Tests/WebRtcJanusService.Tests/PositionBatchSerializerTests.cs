/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The peer_pos request body: its exact bytes, its invariants, and its size at a full room. The mixer
 * refuses a request over 64 KB or with more than 128 entries, and refuses it whole.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class PositionBatchSerializerTests
    {
        private static readonly UUID A = new UUID("11111111-1111-1111-1111-111111111111");

        private static PositionMessage Message(int entries)
        {
            var m = new PositionMessage { Room = 4242, Seq = 7, Full = true };
            for (int i = 0; i < entries; i++)
                m.Entries.Add(new PositionEntry(UUID.Random(), 25612800 + i, 25612800, 2200, 800, 500));
            return m;
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_ProducesTheDocumentedBytes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var m = new PositionMessage { Room = 4242, Seq = 1760000000001, Full = true };
            m.Entries.Add(new PositionEntry(A, 25612800, 25612801, 2200, 800, 500));

            string json = OSDParser.SerializeJsonString(PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe), true);

            Assert.Equal("{\"request\":\"peer_pos\",\"room\":4242,\"seq\":1760000000001,\"full\":true,\"mode\":\"observe\"," +
                "\"pos\":{\"11111111-1111-1111-1111-111111111111\":[25612800,25612801,2200,800,500]}}", json);
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_PartialUpdateIsMarkedNotFull()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            PositionMessage m = Message(1);
            m.Full = false;
            OSDMap request = PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe);
            Assert.True(request.ContainsKey("full"));
            Assert.False(request["full"].AsBoolean());
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_RefusesAZeroUuid()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var m = new PositionMessage { Room = 1, Seq = 1, Full = true };
            m.Entries.Add(new PositionEntry(UUID.Zero, 0, 0, 0, 800, 500));
            Assert.Throws<ArgumentException>(() => PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe));
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_RefusesANegativeSpeedOrTolerance()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var m = new PositionMessage { Room = 1, Seq = 1, Full = true };
            m.Entries.Add(new PositionEntry(A, 0, 0, 0, -1, 500));
            Assert.Throws<ArgumentException>(() => PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe));
        }

        [Fact(Timeout = 60000)]
        public void BuildRequest_RefusesMoreEntriesThanOneRequestMayCarry()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            {
                var assertedValue = () => PositionBatchSerializer.BuildRequest(Message(PositionBatchSerializer.MaxEntries), PositionFeedMode.Observe);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            Assert.Throws<ArgumentException>(() => PositionBatchSerializer.BuildRequest(Message(PositionBatchSerializer.MaxEntries + 1), PositionFeedMode.Observe));
        }

        [Fact(Timeout = 60000)]
        public void ARoomOf110Participants_FitsInsideTheMixersLimits()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap request = PositionBatchSerializer.BuildRequest(Message(110), PositionFeedMode.Observe);
            string json = OSDParser.SerializeJsonString(request, true);
            int bytes = System.Text.Encoding.UTF8.GetByteCount(json);

            Assert.Equal(110, ((OSDMap)request["pos"]).Count);
            Assert.True(bytes > 1000, "the request really carries the entries");
            Assert.True(bytes < 65536, "the mixer's 64 KB request limit");
            Assert.True(PositionBatchSerializer.MaxEntries <= 128, "the mixer's entry limit");
            TestContext.Current.TestOutputHelper.WriteLine($"110 participants: {bytes} bytes");
        }

        [Fact(Timeout = 60000)]
        public void ARoomOf50Participants_IsOneRequestOfAboutFourKilobytes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap request = PositionBatchSerializer.BuildRequest(Message(50), PositionFeedMode.Observe);
            int bytes = System.Text.Encoding.UTF8.GetByteCount(OSDParser.SerializeJsonString(request, true));

            Assert.Equal(50, ((OSDMap)request["pos"]).Count);
            Assert.InRange(bytes, 3000, 5000);
            TestContext.Current.TestOutputHelper.WriteLine($"50 participants: {bytes} bytes");
        }

        [Fact(Timeout = 60000)]
        public void EnforceMode_IsSentAsObserveWhileNothingActsOnIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PositionFeedMode.Observe, PositionFeedModes.Active(PositionFeedMode.Enforce));
            Assert.Equal("observe", PositionFeedModes.WireName(PositionFeedModes.Active(PositionFeedMode.Enforce)));
        }

        [InlineData(null,PositionFeedMode.Observe,true)]
        [InlineData("",PositionFeedMode.Observe,true)]
        [InlineData("observe",PositionFeedMode.Observe,true)]
        [InlineData("OBSERVE",PositionFeedMode.Observe,true)]
        [InlineData("enforce",PositionFeedMode.Enforce,true)]
        [InlineData("Enforce ",PositionFeedMode.Enforce,true)]
        [InlineData("block",PositionFeedMode.Observe,false)]
        [Theory(Timeout = 60000)]
        public void ModeWord_IsParsed(string text, PositionFeedMode expected, bool recognised)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expected, PositionFeedModes.Parse(text, out bool ok));
            Assert.Equal(recognised, ok);
        }
    }
}
