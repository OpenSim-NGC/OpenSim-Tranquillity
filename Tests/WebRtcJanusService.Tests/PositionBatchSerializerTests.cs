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
    [TestFixture]
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

        [Test]
        public void BuildRequest_ProducesTheDocumentedBytes()
        {
            var m = new PositionMessage { Room = 4242, Seq = 1760000000001, Full = true };
            m.Entries.Add(new PositionEntry(A, 25612800, 25612801, 2200, 800, 500));

            string json = OSDParser.SerializeJsonString(PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe), true);

            Assert.That(json, Is.EqualTo(
                "{\"request\":\"peer_pos\",\"room\":4242,\"seq\":1760000000001,\"full\":true,\"mode\":\"observe\"," +
                "\"pos\":{\"11111111-1111-1111-1111-111111111111\":[25612800,25612801,2200,800,500]}}"));
        }

        [Test]
        public void BuildRequest_PartialUpdateIsMarkedNotFull()
        {
            PositionMessage m = Message(1);
            m.Full = false;
            OSDMap request = PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe);
            Assert.That(request.ContainsKey("full"), Is.True);
            Assert.That(request["full"].AsBoolean(), Is.False);
        }

        [Test]
        public void BuildRequest_RefusesAZeroUuid()
        {
            var m = new PositionMessage { Room = 1, Seq = 1, Full = true };
            m.Entries.Add(new PositionEntry(UUID.Zero, 0, 0, 0, 800, 500));
            Assert.Throws<ArgumentException>(() => PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe));
        }

        [Test]
        public void BuildRequest_RefusesANegativeSpeedOrTolerance()
        {
            var m = new PositionMessage { Room = 1, Seq = 1, Full = true };
            m.Entries.Add(new PositionEntry(A, 0, 0, 0, -1, 500));
            Assert.Throws<ArgumentException>(() => PositionBatchSerializer.BuildRequest(m, PositionFeedMode.Observe));
        }

        [Test]
        public void BuildRequest_RefusesMoreEntriesThanOneRequestMayCarry()
        {
            Assert.DoesNotThrow(() => PositionBatchSerializer.BuildRequest(Message(PositionBatchSerializer.MaxEntries), PositionFeedMode.Observe));
            Assert.Throws<ArgumentException>(() => PositionBatchSerializer.BuildRequest(Message(PositionBatchSerializer.MaxEntries + 1), PositionFeedMode.Observe));
        }

        [Test]
        public void ARoomOf110Participants_FitsInsideTheMixersLimits()
        {
            OSDMap request = PositionBatchSerializer.BuildRequest(Message(110), PositionFeedMode.Observe);
            string json = OSDParser.SerializeJsonString(request, true);
            int bytes = System.Text.Encoding.UTF8.GetByteCount(json);

            Assert.That(((OSDMap)request["pos"]).Count, Is.EqualTo(110));
            Assert.That(bytes, Is.GreaterThan(1000), "the request really carries the entries");
            Assert.That(bytes, Is.LessThan(65536), "the mixer's 64 KB request limit");
            Assert.That(PositionBatchSerializer.MaxEntries, Is.LessThanOrEqualTo(128), "the mixer's entry limit");
            TestContext.Out.WriteLine($"110 participants: {bytes} bytes");
        }

        [Test]
        public void EnforceMode_IsSentAsObserveWhileNothingActsOnIt()
        {
            Assert.That(PositionFeedModes.Active(PositionFeedMode.Enforce), Is.EqualTo(PositionFeedMode.Observe));
            Assert.That(PositionFeedModes.WireName(PositionFeedModes.Active(PositionFeedMode.Enforce)), Is.EqualTo("observe"));
        }

        [TestCase(null, PositionFeedMode.Observe, true)]
        [TestCase("", PositionFeedMode.Observe, true)]
        [TestCase("observe", PositionFeedMode.Observe, true)]
        [TestCase("OBSERVE", PositionFeedMode.Observe, true)]
        [TestCase("enforce", PositionFeedMode.Enforce, true)]
        [TestCase("Enforce ", PositionFeedMode.Enforce, true)]
        [TestCase("block", PositionFeedMode.Observe, false)]
        public void ModeWord_IsParsed(string text, PositionFeedMode expected, bool recognised)
        {
            Assert.That(PositionFeedModes.Parse(text, out bool ok), Is.EqualTo(expected));
            Assert.That(ok, Is.EqualTo(recognised));
        }
    }
}
