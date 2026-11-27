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
            public bool MixerProtocolKnown { get; set; } = true;
            public Task<bool> CheckMixerProtocolAsync() => Task.FromResult(true);
            public PositionReplyStats Replies { get; } = new PositionReplyStats();
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

        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [Theory(Timeout = 60000)]
        public async Task AMixerOlderThanProtocol3_IsSentNothing_AndNothingIsComputed(int protocol)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = protocol };
            VoicePositionPump pump = Pump(world, sink);

            for (int i = 0; i < 5; i++)
            {
                _now += 1000;
                await pump.TickAsync();
            }

            Assert.Empty(sink.Sent);
            {
                var assertedValue = world.Calls;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no scene walk while there is nobody to tell");
            }
            Assert.Equal(0, pump.MessagesSent);
        }

        [Fact(Timeout = 60000)]
        public async Task AProtocol3Mixer_ReceivesTheRoomsPositions()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            await Pump(world, sink).TickAsync();

            Assert.Single(sink.Sent);
            OSDMap body = sink.Sent[0];
            Assert.Equal("peer_pos", body["request"].AsString());
            Assert.Equal(7, body["room"].AsInteger());
            Assert.True(body["full"].AsBoolean());
            Assert.Equal("observe", body["mode"].AsString());
            var entry = (OSDArray)((OSDMap)body["pos"])[A.ToString()];
            Assert.Equal(new[] { 100, 200, 300, 800, 500 }, entry.Select(x => x.AsInteger()));
        }

        [Fact(Timeout = 60000)]
        public async Task LearningProtocol3Later_StartsTheFeedWithoutARestart()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 0 };
            VoicePositionPump pump = Pump(world, sink);

            await pump.TickAsync();
            Assert.Empty(sink.Sent);

            sink.MixerVisProtocol = 3;
            _now += 500;
            await pump.TickAsync();
            Assert.Single(sink.Sent);
        }

        [Fact(Timeout = 60000)]
        public async Task IfTheMixerIsReplacedByAnOlderOne_TheFeedStops()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);
            await pump.TickAsync();
            sink.Sent.Clear();

            sink.MixerVisProtocol = 2;
            _now += 5000;
            await pump.TickAsync();
            Assert.Empty(sink.Sent);
        }

        [Fact(Timeout = 60000)]
        public async Task EnforceModeIsAcceptedButSentAsObserve()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            await Pump(new FakeWorld(), sink, PositionFeedMode.Enforce).TickAsync();
            Assert.Equal("observe", sink.Sent.Single()["mode"].AsString());
        }

        [Fact(Timeout = 60000)]
        public async Task TicksCloserTogetherThanTheConfiguredInterval_DoNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);

            await pump.TickAsync();
            _now += 499;
            await pump.TickAsync();
            Assert.Equal(1, world.Calls);
            _now += 1;
            await pump.TickAsync();
            Assert.Equal(2, world.Calls);
        }

        [Fact(Timeout = 60000)]
        public async Task WhileASendIsStillInFlight_NoSecondScanStarts()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3, Hold = new TaskCompletionSource<bool>() };
            VoicePositionPump pump = Pump(world, sink);

            Task first = pump.TickAsync();
            _now += 1000;
            await pump.TickAsync();
            {
                var assertedValue = world.Calls;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the slow send is not queued behind");
            }

            sink.Hold.SetResult(true);
            await first;
            _now += 1000;
            await pump.TickAsync();
            Assert.Equal(2, world.Calls);
        }

        [Fact(Timeout = 60000)]
        public async Task AFailedSend_IsCounted_AndDoesNotThrow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var sink = new FakeSink { MixerVisProtocol = 3, Succeed = false };
            VoicePositionPump pump = Pump(new FakeWorld(), sink);
            await pump.TickAsync();
            Assert.Equal(1, pump.MessagesFailed);
            Assert.Equal(0, pump.MessagesSent);
        }

        [Fact(Timeout = 60000)]
        public async Task ASceneWalkThatThrows_DoesNotThrowOutOfTheTick()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld { Throw = true };
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);
            await pump.TickAsync();
            Assert.Empty(sink.Sent);

            world.Throw = false;
            _now += 500;
            await pump.TickAsync();
            {
                var assertedValue = sink.Sent;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the next tick recovers");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task OnlyChangesAreSentBetweenKeepAlives()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var world = new FakeWorld();
            var sink = new FakeSink { MixerVisProtocol = 3 };
            VoicePositionPump pump = Pump(world, sink);

            await pump.TickAsync();
            for (int i = 0; i < 3; i++)
            {
                _now += 500;
                await pump.TickAsync();
            }
            {
                var assertedValue = sink.Sent;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "standing still, under the keep-alive");
            }

            _now += 500;
            await pump.TickAsync();
            {
                var assertedValue = sink.Sent;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "keep-alive reached");
            }
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

        [Fact(Timeout = 60000)]
        public void TheSink_KnowsNothingOfTheMixerUntilItHasAnswered()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, new SinkRig().Sink.MixerVisProtocol);
        }

        [InlineData(2)]
        [InlineData(3)]
        [Theory(Timeout = 60000)]
        public async Task TheSink_LearnsTheMixersProtocolFromAPeerCtlBatchReply(int advertised)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new SinkRig { NextReply = Reply(advertised) };
            await rig.SendBatch();
            Assert.Equal(advertised, rig.Sink.MixerVisProtocol);
        }

        [Fact(Timeout = 60000)]
        public async Task AnAppliedReplyThatAdvertisesNoProtocol_MeansAnOlderMixer()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new SinkRig { NextReply = Reply(3) };
            await rig.SendBatch();
            rig.NextReply = Reply(null);
            await rig.SendBatch();
            Assert.Equal(0, rig.Sink.MixerVisProtocol);
        }

        [Fact(Timeout = 60000)]
        public async Task AFailedBatch_LeavesWhatWasLearnedAlone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new SinkRig { NextReply = Reply(3) };
            await rig.SendBatch();
            rig.TransportFails = true;
            Assert.Equal(PeerCtlSendResult.TransportError, await rig.SendBatch());
            Assert.Equal(3, rig.Sink.MixerVisProtocol);
        }

        [Fact(Timeout = 60000)]
        public async Task SendPositionsAsync_PostsTheBodyAndReportsSuccess()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new SinkRig();
            var body = new OSDMap { ["request"] = OSD.FromString("peer_pos") };
            Assert.True(await rig.Sink.SendPositionsAsync(body));
            Assert.Equal("peer_pos", rig.Requests.Single()["request"].AsString());
            Assert.False(rig.Requests.Single().ContainsKey("room_epoch"), "no epoch without arming");
        }

        [Fact(Timeout = 60000)]
        public async Task SendPositionsAsync_CarriesTheRoomEpochWhenTheRegionRunsWithArming()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new SinkRig();
            rig.Sink.Authority = new VisAuthority(0x1234, "Test Region");
            await rig.Sink.SendPositionsAsync(new OSDMap { ["request"] = OSD.FromString("peer_pos") });
            Assert.Equal(rig.Sink.Authority.EpochString, rig.Requests.Single()["room_epoch"].AsString());
        }

        [Fact(Timeout = 60000)]
        public async Task SendPositionsAsync_ReportsATransportFailureWithoutThrowing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var body = new OSDMap { ["request"] = OSD.FromString("peer_pos") };
            Assert.False((await new SinkRig
                {
                    TransportFails = true
                }.Sink.SendPositionsAsync(body)), "a failed POST");
            Assert.False((await new SinkRig
                {
                    Throws = true
                }.Sink.SendPositionsAsync(body)), "an exception from the transport");
        }

        // ---- the two together: an older mixer is sent nothing, a protocol 3 mixer is sent positions ----

        [Fact(Timeout = 60000)]
        public async Task EndToEnd_AnOlderMixerIsSentNoPeerPos_AndAProtocol3MixerIs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new SinkRig { NextReply = Reply(2) };
            var world = new FakeWorld();
            var pump = new VoicePositionPump(world, rig.Sink, new PositionSettings(), _ => 7, () => 100000, "Test Region", () => _now);

            Assert.Equal(PeerCtlSendResult.Ok, await rig.SendBatch());
            await pump.TickAsync();
            {
                var assertedValue = rig.Requests.Select(r => r["request"].AsString());
                TestAssert.WithMessage(() => Assert.Equal(new[] { "peer_ctl_batch" }, assertedValue), "protocol 2: no peer_pos");
            }

            rig.NextReply = Reply(3);
            await rig.SendBatch();
            _now += 500;
            await pump.TickAsync();
            Assert.Equal(new[] { "peer_ctl_batch", "peer_ctl_batch", "peer_pos" }, rig.Requests.Select(r => r["request"].AsString()));
        }
    }
}
