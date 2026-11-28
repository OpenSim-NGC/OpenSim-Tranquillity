/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * How the position feed learns the mixer's vis_protocol when no peer_ctl request has been sent: with the feed on, arming
 * off and nobody excluded or muted, nothing else would ever ask. The pump asks once, with a peer_ctl_heartbeat that names
 * no room, which the mixer answers with its version and otherwise ignores (janus_slvoice_handle_heartbeat in the
 * mixer's janus_slvoice.c: the rooms object is walked room by room, and an empty one has no room to walk).
 *
 * These tests run the pump against the real sink with a scripted mixer, so what is counted is what would go on the wire.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class PositionProtocolCheckTests
    {
        private static readonly UUID A = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        private sealed class World : IPositionWorld
        {
            public IReadOnlyList<PositionView> SnapshotPositions()
                => new List<PositionView> { new PositionView(A, false, 100, 200, 300, 0) };
        }

        /// <summary>A mixer that answers what its version allows, and records every request it was sent.</summary>
        private sealed class ScriptedMixer
        {
            public int Protocol = 3;               // 0 = an older mixer: it advertises no version and rejects a request it does not know
            public string Instance = "inst-1";
            public bool Down;                      // transport failure
            public readonly List<OSDMap> Requests = new List<OSDMap>();

            public IEnumerable<OSDMap> Of(string request) => Requests.FindAll(r => r["request"].AsString() == request);

            public Task<(AdminSendResult, string)> Handle(OSDMap req)
            {
                if (Down)
                    return Task.FromResult((AdminSendResult.TransportError, string.Empty));
                Requests.Add(req);
                string request = req["request"].AsString();
                string version = Protocol > 0 ? $",\"vis_protocol\":{Protocol},\"mixer_instance\":\"{Instance}\"" : string.Empty;
                string inner;
                if (Protocol == 0)
                    inner = "{\"slvoice\":\"error\",\"reason\":\"unknown_request\"}";
                else if (request == "peer_ctl_heartbeat" && Protocol < 2)
                    inner = "{\"slvoice\":\"error\",\"reason\":\"unknown_request\"" + version + "}";
                else if (request == "peer_ctl_heartbeat")
                    inner = "{\"slvoice\":\"heartbeat\"" + version + ",\"rooms\":{}}";
                else if (request == "peer_pos" && Protocol < 3)
                    inner = "{\"slvoice\":\"error\",\"reason\":\"unknown_request\"" + version + "}";
                else
                    inner = "{\"slvoice\":\"applied\",\"room\":7" + version + "}";
                return Task.FromResult((AdminSendResult.Ok, "{\"janus\":\"success\",\"response\":" + inner + "}"));
            }
        }

        private long _now;

        private (VoicePositionPump Pump, JanusPeerCtlBatchSink Sink) Build(ScriptedMixer mixer)
        {
            var sink = new JanusPeerCtlBatchSink("http://example.org/admin", "secret", TimeSpan.FromSeconds(5),
                new UUID("00000001-0000-0000-0000-000000000000"), "Test Region", 4, mixer.Handle);
            var pump = new VoicePositionPump(new World(), sink, new PositionSettings(), _ => 7, () => 100000, "Test Region", () => _now);
            return (pump, sink);
        }

        private async Task TicksAsync(VoicePositionPump pump, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _now += 600;
                await pump.TickAsync();
            }
        }

        [Fact(Timeout = 60000)]
        public async Task WithTheFeedOnArmingOffAndNobodyExcluded_PositionRequestsAreSent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                Assert.False(sink.MixerProtocolKnown, "nothing has been asked yet");

                await TicksAsync(pump, 3);

                Assert.Equal(3, sink.MixerVisProtocol);
                Assert.True(sink.MixerProtocolKnown);
                {
                    var assertedValue = mixer.Of("peer_pos");
                    TestAssert.WithMessage(() => Assert.NotEmpty(assertedValue), "position requests went out");
                }
                {
                    var assertedValue = mixer.Of("peer_ctl_batch");
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no exclusion was ever sent");
                }
                {
                    var assertedValue = mixer.Of("peer_ctl_heartbeat").Count();
                    TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the version was asked for once");
                }
            }
        }

        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [Theory(Timeout = 60000)]
        public async Task AnOlderMixer_GetsNothingAfterTheCheck(int protocol)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var mixer = new ScriptedMixer { Protocol = protocol };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 20);

                {
                    var assertedValue = mixer.Requests.Count;
                    TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "only the version check was sent");
                }
                Assert.Equal("peer_ctl_heartbeat", mixer.Requests[0]["request"].AsString());
                Assert.True(sink.MixerProtocolKnown, "an older mixer is a known answer, not a reason to ask again");
                Assert.True(sink.MixerVisProtocol < 3);
            }
        }

        [Fact(Timeout = 60000)]
        public async Task TheCheckNamesNoRoom_AndCarriesNothingTheMixerWouldApply()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 1);

                OSDMap check = mixer.Requests[0];
                Assert.Equal("peer_ctl_heartbeat", check["request"].AsString());
                Assert.IsAssignableFrom<OSDMap>(check["rooms"]);
                {
                    var assertedValue = ((OSDMap)check["rooms"]).Count;
                    TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no room is named, so no room's authority is touched");
                }
                Assert.False(check.ContainsKey("state"), "not a stopping heartbeat, which would mark records stale");
                {
                    var assertedValue = check["room_epoch"].AsString();
                    TestAssert.WithMessage(() =>
                    {
                        Assert.Matches("^[0-9a-f]{16}$", assertedValue);
                        Assert.NotEqual("0000000000000000", assertedValue);
                    }, "an epoch the mixer's parser accepts, so the reply is a heartbeat reply");
                }
                Assert.True(check["interval_ms"].AsInteger() > 0);
            }
        }

        [Fact(Timeout = 60000)]
        public async Task AFailedRequest_MakesThePumpAskAgain_AndAMixerRestartedAsAnOlderOneStopsTheFeed()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 2);
                Assert.NotEmpty(mixer.Of("peer_pos"));

                // The mixer goes away, and comes back older.
                mixer.Down = true;
                await TicksAsync(pump, 5);
                Assert.False(sink.MixerProtocolKnown, "what was learned may be out of date");
                mixer.Down = false;
                mixer.Protocol = 0;
                mixer.Instance = "inst-2";
                int before = mixer.Requests.Count;

                await TicksAsync(pump, 10);

                {
                    var assertedValue = mixer.Requests.Count - before;
                    TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one check, and then nothing");
                }
                Assert.Equal("peer_ctl_heartbeat", mixer.Requests[before]["request"].AsString());
                Assert.Equal(0, sink.MixerVisProtocol);
            }
        }

        [Fact(Timeout = 60000)]
        public async Task AMixerRestartedAsANewerOne_IsLearnedFromTheNextPeerCtlReply_AndTheFeedResumes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // An older mixer is sent nothing, so the pump has no request of its own to notice a restart with. Any peer_ctl
            // request the region sends (an exclusion, or a heartbeat when arming is on) carries the new version.
            var mixer = new ScriptedMixer { Protocol = 2 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 5);
                Assert.Empty(mixer.Of("peer_pos"));

                mixer.Protocol = 3;
                mixer.Instance = "inst-2";
                sink.RoomOf = _ => 7;
                await sink.SendAsync(VisOp.Replace, new Dictionary<UUID, IReadOnlyCollection<UUID>> { [A] = new[] { UUID.Random() } });
                Assert.Equal(3, sink.MixerVisProtocol);

                await TicksAsync(pump, 3);

                Assert.NotEmpty(mixer.Of("peer_pos"));
            }
        }

        [Fact(Timeout = 60000)]
        public async Task AVersionLearnedFromAPeerCtlReply_IsNotAskedForAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                // An exclusion goes out first, as it does when arming is on or someone is excluded.
                sink.RoomOf = _ => 7;
                var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [A] = new[] { UUID.Random() } };
                await sink.SendAsync(VisOp.Replace, excl);
                Assert.True(sink.MixerProtocolKnown);
                mixer.Requests.Clear();

                await TicksAsync(pump, 3);

                {
                    var assertedValue = mixer.Of("peer_ctl_heartbeat");
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the version was already known");
                }
                Assert.NotEmpty(mixer.Of("peer_pos"));
            }
        }
    }
}
