/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The position feed's driver and its sink: when positions are computed and sent, how the sim learns the
 * mixer's protocol version (from the replies to the peer_ctl requests it already sends), and that a mixer
 * older than protocol 3 is sent nothing.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoicePositionPumpTests
    {
        private static readonly UUID A = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly UUID Listener = new UUID("00000001-0000-0000-0000-000000000000");

        private sealed class FakeWorld : IPositionWorld
        {
            public int Calls;
            public bool Throw;
            public List<PositionView> Views = new List<PositionView> { new PositionView(A, false, 100, 200, 300, 0) };

            public IReadOnlyList<PositionView> SnapshotPositions()
            {
                Calls++;
                if (Throw)
                    throw new InvalidOperationException("scene walk failed");
                return Views;
            }
        }

        private sealed class FakeSink : IPeerPosSink
        {
            public int MixerVisProtocol { get; set; }
            public readonly List<OSDMap> Sent = new List<OSDMap>();
            public bool Succeed = true;
            public TaskCompletionSource<bool> Hold;

            public Task<bool> SendPositionsAsync(OSDMap body)
            {
                Sent.Add(body);
                return Hold != null ? Hold.Task : Task.FromResult(Succeed);
            }
        }

        private long _now;

        private VoicePositionPump Pump(FakeWorld world, IPeerPosSink sink, PositionFeedMode mode = PositionFeedMode.Observe)
            => new VoicePositionPump(world, sink, new PositionSettings { Mode = mode }, _ => 7, () => 100000, "Test Region", () => _now);

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task AMixerOlderThanProtocol3_IsSentNothing_AndNothingIsComputed(int protocol)
        {
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = protocol };
            VoicePositionPump pump = Pump(world, sink);

            for (int i = 0; i < 5; i++)
            {
                _now += 1000;
                await pump.TickAsync();
            }

            Assert.That(sink.Sent, Is.Empty);
            Assert.That(world.Calls, Is.EqualTo(0), "no scene walk while there is nobody to tell");
            Assert.That(pump.MessagesSent, Is.EqualTo(0));
        }

        [Test]
        public async Task AProtocol3Mixer_ReceivesTheRoomsPositions()
        {
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            await Pump(world, sink).TickAsync();

            Assert.That(sink.Sent, Has.Count.EqualTo(1));
            OSDMap body = sink.Sent[0];
            Assert.That(body["request"].AsString(), Is.EqualTo("peer_pos"));
            Assert.That(body["room"].AsInteger(), Is.EqualTo(7));
            Assert.That(body["full"].AsBoolean(), Is.True);
            Assert.That(body["mode"].AsString(), Is.EqualTo("observe"));
            var entry = (OSDArray)((OSDMap)body["pos"])[A.ToString()];
            Assert.That(entry.Select(x => x.AsInteger()), Is.EqualTo(new[] { 100, 200, 300, 800, 500 }));
        }

        [Test]
        public async Task LearningProtocol3Later_StartsTheFeedWithoutARestart()
        {
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 0 };
            VoicePositionPump pump = Pump(world, sink);

            await pump.TickAsync();
            Assert.That(sink.Sent, Is.Empty);

            sink.MixerVisProtocol = 3;
            _now += 500;
            await pump.TickAsync();
            Assert.That(sink.Sent, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task IfTheMixerIsReplacedByAnOlderOne_TheFeedStops()
        {
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);
            await pump.TickAsync();
            sink.Sent.Clear();

            sink.MixerVisProtocol = 2;
            _now += 5000;
            await pump.TickAsync();
            Assert.That(sink.Sent, Is.Empty);
        }

        [Test]
        public async Task EnforceModeIsAcceptedButSentAsObserve()
        {
            var sink = new FakeSink { MixerVisProtocol = 3 };
            await Pump(new FakeWorld(), sink, PositionFeedMode.Enforce).TickAsync();
            Assert.That(sink.Sent.Single()["mode"].AsString(), Is.EqualTo("observe"));
        }

        [Test]
        public async Task TicksCloserTogetherThanTheConfiguredInterval_DoNothing()
        {
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);

            await pump.TickAsync();
            _now += 499;
            await pump.TickAsync();
            Assert.That(world.Calls, Is.EqualTo(1));
            _now += 1;
            await pump.TickAsync();
            Assert.That(world.Calls, Is.EqualTo(2));
        }

        [Test]
        public async Task WhileASendIsStillInFlight_NoSecondScanStarts()
        {
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3, Hold = new TaskCompletionSource<bool>() };
            VoicePositionPump pump = Pump(world, sink);

            Task first = pump.TickAsync();
            _now += 1000;
            await pump.TickAsync();
            Assert.That(world.Calls, Is.EqualTo(1), "the slow send is not queued behind");

            sink.Hold.SetResult(true);
            await first;
            _now += 1000;
            await pump.TickAsync();
            Assert.That(world.Calls, Is.EqualTo(2));
        }

        [Test]
        public async Task AFailedSend_IsCounted_AndDoesNotThrow()
        {
            var sink = new FakeSink { MixerVisProtocol = 3, Succeed = false };
            VoicePositionPump pump = Pump(new FakeWorld(), sink);
            await pump.TickAsync();
            Assert.That(pump.MessagesFailed, Is.EqualTo(1));
            Assert.That(pump.MessagesSent, Is.EqualTo(0));
        }

        [Test]
        public async Task ASceneWalkThatThrows_DoesNotThrowOutOfTheTick()
        {
            var world = new FakeWorld { Throw = true };
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);
            await pump.TickAsync();
            Assert.That(sink.Sent, Is.Empty);

            world.Throw = false;
            _now += 500;
            await pump.TickAsync();
            Assert.That(sink.Sent, Has.Count.EqualTo(1), "the next tick recovers");
        }

        [Test]
        public async Task OnlyChangesAreSentBetweenKeepAlives()
        {
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);

            await pump.TickAsync();
            _now += 500;
            await pump.TickAsync();
            Assert.That(sink.Sent, Has.Count.EqualTo(1), "standing still, under the keep-alive");

            _now += 500;
            await pump.TickAsync();
            Assert.That(sink.Sent, Has.Count.EqualTo(2), "keep-alive reached");
        }

        // ---- the sink: how the sim learns the mixer's version ---------------------------------

        private static string Reply(int? visProtocol)
            => "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"op\":\"replace\",\"room\":1,\"entries\":1,\"skipped\":0" +
               (visProtocol.HasValue ? ",\"vis_protocol\":" + visProtocol.Value : string.Empty) + "}}";

        private sealed class SinkRig
        {
            public readonly List<OSDMap> Requests = new List<OSDMap>();
            public string NextReply = Reply(2);
            public bool TransportFails;
            public bool Throws;
            public readonly JanusPeerCtlBatchSink Sink;

            public SinkRig()
            {
                Sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                    new UUID("00000000-0000-0000-0000-0000000003e7"), "Test Region", 4, req =>
                    {
                        Requests.Add(req);
                        if (Throws)
                            throw new InvalidOperationException("connection refused");
                        return Task.FromResult(TransportFails ? (AdminSendResult.TransportError, string.Empty) : (AdminSendResult.Ok, NextReply));
                    });
                Sink.RoomOf = _ => Sink.FallbackRoom;
            }

            public Task<PeerCtlSendResult> SendBatch()
                => Sink.SendAsync(VisOp.Replace, new Dictionary<UUID, IReadOnlyCollection<UUID>> { [Listener] = new[] { A } });
        }

        [Test]
        public void TheSink_KnowsNothingOfTheMixerUntilItHasAnswered()
        {
            Assert.That(new SinkRig().Sink.MixerVisProtocol, Is.EqualTo(0));
        }

        [TestCase(2)]
        [TestCase(3)]
        public async Task TheSink_LearnsTheMixersProtocolFromAPeerCtlBatchReply(int advertised)
        {
            var rig = new SinkRig { NextReply = Reply(advertised) };
            await rig.SendBatch();
            Assert.That(rig.Sink.MixerVisProtocol, Is.EqualTo(advertised));
        }

        [Test]
        public async Task AnAppliedReplyThatAdvertisesNoProtocol_MeansAnOlderMixer()
        {
            var rig = new SinkRig { NextReply = Reply(3) };
            await rig.SendBatch();
            rig.NextReply = Reply(null);
            await rig.SendBatch();
            Assert.That(rig.Sink.MixerVisProtocol, Is.EqualTo(0));
        }

        [Test]
        public async Task AFailedBatch_LeavesWhatWasLearnedAlone()
        {
            var rig = new SinkRig { NextReply = Reply(3) };
            await rig.SendBatch();
            rig.TransportFails = true;
            Assert.That(await rig.SendBatch(), Is.EqualTo(PeerCtlSendResult.TransportError));
            Assert.That(rig.Sink.MixerVisProtocol, Is.EqualTo(3));
        }

        [Test]
        public async Task SendPositionsAsync_PostsTheBodyAndReportsSuccess()
        {
            var rig = new SinkRig();
            var body = new OSDMap { ["request"] = OSD.FromString("peer_pos") };
            Assert.That(await rig.Sink.SendPositionsAsync(body), Is.True);
            Assert.That(rig.Requests.Single()["request"].AsString(), Is.EqualTo("peer_pos"));
            Assert.That(rig.Requests.Single().ContainsKey("room_epoch"), Is.False, "no epoch without arming");
        }

        [Test]
        public async Task SendPositionsAsync_CarriesTheRoomEpochWhenTheRegionRunsWithArming()
        {
            var rig = new SinkRig();
            rig.Sink.Authority = new VisAuthority(0x1234, "Test Region");
            await rig.Sink.SendPositionsAsync(new OSDMap { ["request"] = OSD.FromString("peer_pos") });
            Assert.That(rig.Requests.Single()["room_epoch"].AsString(), Is.EqualTo(rig.Sink.Authority.EpochString));
        }

        [Test]
        public async Task SendPositionsAsync_ReportsATransportFailureWithoutThrowing()
        {
            var body = new OSDMap { ["request"] = OSD.FromString("peer_pos") };
            Assert.That(await new SinkRig { TransportFails = true }.Sink.SendPositionsAsync(body), Is.False, "a failed POST");
            Assert.That(await new SinkRig { Throws = true }.Sink.SendPositionsAsync(body), Is.False, "an exception from the transport");
        }

        // ---- the two together: an older mixer is sent nothing, a protocol 3 mixer is sent positions ----

        [Test]
        public async Task EndToEnd_AnOlderMixerIsSentNoPeerPos_AndAProtocol3MixerIs()
        {
            var rig = new SinkRig { NextReply = Reply(2) };
            var world = new FakeWorld();
            var pump = new VoicePositionPump(world, rig.Sink, new PositionSettings(), _ => 7, () => 100000, "Test Region", () => _now);

            Assert.That(await rig.SendBatch(), Is.EqualTo(PeerCtlSendResult.Ok));
            await pump.TickAsync();
            Assert.That(rig.Requests.Select(r => r["request"].AsString()), Is.EqualTo(new[] { "peer_ctl_batch" }), "protocol 2: no peer_pos");

            rig.NextReply = Reply(3);
            await rig.SendBatch();
            _now += 500;
            await pump.TickAsync();
            Assert.That(rig.Requests.Select(r => r["request"].AsString()), Is.EqualTo(new[] { "peer_ctl_batch", "peer_ctl_batch", "peer_pos" }));
        }
    }
}
