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
    [TestFixture]
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

        [Test]
        public async Task WithTheFeedOnArmingOffAndNobodyExcluded_PositionRequestsAreSent()
        {
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                Assert.That(sink.MixerProtocolKnown, Is.False, "nothing has been asked yet");

                await TicksAsync(pump, 3);

                Assert.That(sink.MixerVisProtocol, Is.EqualTo(3));
                Assert.That(sink.MixerProtocolKnown, Is.True);
                Assert.That(mixer.Of("peer_pos"), Is.Not.Empty, "position requests went out");
                Assert.That(mixer.Of("peer_ctl_batch"), Is.Empty, "no exclusion was ever sent");
                Assert.That(mixer.Of("peer_ctl_heartbeat").Count(), Is.EqualTo(1), "the version was asked for once");
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task AnOlderMixer_GetsNothingAfterTheCheck(int protocol)
        {
            var mixer = new ScriptedMixer { Protocol = protocol };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 20);

                Assert.That(mixer.Requests.Count, Is.EqualTo(1), "only the version check was sent");
                Assert.That(mixer.Requests[0]["request"].AsString(), Is.EqualTo("peer_ctl_heartbeat"));
                Assert.That(sink.MixerProtocolKnown, Is.True, "an older mixer is a known answer, not a reason to ask again");
                Assert.That(sink.MixerVisProtocol, Is.LessThan(3));
            }
        }

        [Test]
        public async Task TheCheckNamesNoRoom_AndCarriesNothingTheMixerWouldApply()
        {
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 1);

                OSDMap check = mixer.Requests[0];
                Assert.That(check["request"].AsString(), Is.EqualTo("peer_ctl_heartbeat"));
                Assert.That(check["rooms"], Is.InstanceOf<OSDMap>());
                Assert.That(((OSDMap)check["rooms"]).Count, Is.EqualTo(0), "no room is named, so no room's authority is touched");
                Assert.That(check.ContainsKey("state"), Is.False, "not a stopping heartbeat, which would mark records stale");
                Assert.That(check["room_epoch"].AsString(), Does.Match("^[0-9a-f]{16}$").And.Not.EqualTo("0000000000000000"),
                    "an epoch the mixer's parser accepts, so the reply is a heartbeat reply");
                Assert.That(check["interval_ms"].AsInteger(), Is.GreaterThan(0));
            }
        }

        [Test]
        public async Task AFailedRequest_MakesThePumpAskAgain_AndAMixerRestartedAsAnOlderOneStopsTheFeed()
        {
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 2);
                Assert.That(mixer.Of("peer_pos"), Is.Not.Empty);

                // The mixer goes away, and comes back older.
                mixer.Down = true;
                await TicksAsync(pump, 5);
                Assert.That(sink.MixerProtocolKnown, Is.False, "what was learned may be out of date");
                mixer.Down = false;
                mixer.Protocol = 0;
                mixer.Instance = "inst-2";
                int before = mixer.Requests.Count;

                await TicksAsync(pump, 10);

                Assert.That(mixer.Requests.Count - before, Is.EqualTo(1), "one check, and then nothing");
                Assert.That(mixer.Requests[before]["request"].AsString(), Is.EqualTo("peer_ctl_heartbeat"));
                Assert.That(sink.MixerVisProtocol, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task AMixerRestartedAsANewerOne_IsLearnedFromTheNextPeerCtlReply_AndTheFeedResumes()
        {
            // An older mixer is sent nothing, so the pump has no request of its own to notice a restart with. Any peer_ctl
            // request the region sends (an exclusion, or a heartbeat when arming is on) carries the new version.
            var mixer = new ScriptedMixer { Protocol = 2 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                await TicksAsync(pump, 5);
                Assert.That(mixer.Of("peer_pos"), Is.Empty);

                mixer.Protocol = 3;
                mixer.Instance = "inst-2";
                sink.RoomOf = _ => 7;
                await sink.SendAsync(VisOp.Replace, new Dictionary<UUID, IReadOnlyCollection<UUID>> { [A] = new[] { UUID.Random() } });
                Assert.That(sink.MixerVisProtocol, Is.EqualTo(3));

                await TicksAsync(pump, 3);

                Assert.That(mixer.Of("peer_pos"), Is.Not.Empty);
            }
        }

        [Test]
        public async Task AVersionLearnedFromAPeerCtlReply_IsNotAskedForAgain()
        {
            var mixer = new ScriptedMixer { Protocol = 3 };
            (VoicePositionPump pump, JanusPeerCtlBatchSink sink) = Build(mixer);
            using (sink)
            {
                // An exclusion goes out first, as it does when arming is on or someone is excluded.
                sink.RoomOf = _ => 7;
                var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [A] = new[] { UUID.Random() } };
                await sink.SendAsync(VisOp.Replace, excl);
                Assert.That(sink.MixerProtocolKnown, Is.True);
                mixer.Requests.Clear();

                await TicksAsync(pump, 3);

                Assert.That(mixer.Of("peer_ctl_heartbeat"), Is.Empty, "the version was already known");
                Assert.That(mixer.Of("peer_pos"), Is.Not.Empty);
            }
        }
    }
}
