/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The position feed sends only to rooms the mixer holds.
 *
 * The pump addresses a participant at the room its last provision joined (the AgentRoomTable, as VoiceVisibilityService
 * wires it) and at no other: a participant that is only in a group or avatar-to-avatar call has no record, so nothing is
 * sent for it. A room the mixer answers unknown_room for is held back until the region records a join into it, and the
 * next request for it is then a whole snapshot. These tests run the real pump, the real sink and the real record table
 * against a scripted mixer.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class PositionRoomHoldBackTests
    {
        private static readonly UUID A = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private const int Room = 226001999;
        private const int OtherRoom = 226002777;

        private sealed class World : IPositionWorld
        {
            public List<PositionView> Views = new List<PositionView> { new PositionView(A, false, 100, 200, 300, 0) };
            public IReadOnlyList<PositionView> SnapshotPositions() => Views;
        }

        private sealed class Rig : IDisposable
        {
            public long Now = 10_000;
            public readonly World World = new World();
            public readonly AgentRoomTable Table = new AgentRoomTable();
            public readonly RoomRefusals Refusals = new RoomRefusals();
            /// <summary>The rooms the mixer holds. A peer_pos for any other room is answered unknown_room.</summary>
            public readonly HashSet<int> Held = new HashSet<int>();
            public readonly List<OSDMap> PosRequests = new List<OSDMap>();
            /// <summary>Runs while a peer_pos request is in flight (a join arriving before the answer).</summary>
            public Action DuringRequest;
            public readonly JanusPeerCtlBatchSink Sink;
            public readonly VoicePositionPump Pump;

            public Rig()
            {
                Sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5), UUID.Random(),
                    "Test Region", 4, Send, nowMs: () => Now);
                Sink.Refusals = Refusals;
                Pump = new VoicePositionPump(World, Sink, new PositionSettings(), Table.Resolve, () => 100000, "Test Region",
                    () => Now, Refusals);
            }

            private Task<(AdminSendResult, string)> Send(OSDMap request)
            {
                string reply;
                if (request["request"].AsString() == "peer_pos")
                {
                    PosRequests.Add((OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(request)));
                    DuringRequest?.Invoke();
                    reply = Held.Contains(request["room"].AsInteger())
                        ? "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"op\":\"pos\",\"vis_protocol\":3}}"
                        : "{\"janus\":\"success\",\"response\":{\"slvoice\":\"error\",\"reason\":\"unknown_room\",\"vis_protocol\":3}}";
                }
                else
                {
                    reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"heartbeat\",\"vis_protocol\":3," +
                            "\"mixer_instance\":\"m1\",\"rooms\":{}}}";
                }
                return Task.FromResult((AdminSendResult.Ok, reply));
            }

            /// <summary>One feeder tick a second on: the first learns the mixer's version, the rest send.</summary>
            public async Task Ticks(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    Now += 1000;
                    await Pump.TickAsync();
                }
            }

            /// <summary>VoiceVisibilityService.OnListenerProvisioned for a successful provision.</summary>
            public void Join(UUID agent, int room)
            {
                Held.Add(room);
                Table.Record(agent, room);
                Refusals.Joined(room);
            }

            public void Dispose() => Sink.Dispose();
        }

        [Test]
        public async Task AnAgentOnlyInAGroupCall_CausesNoPeerPosForItsParcelsRoom()
        {
            using var rig = new Rig();   // the agent stands on a parcel but has joined no room

            await rig.Ticks(8);

            Assert.That(rig.PosRequests, Is.Empty, "nothing is sent for an agent with no record");
            Assert.That(rig.Sink.Replies.Counts.UnknownRoom, Is.Zero, "so the mixer refuses nothing");
            Assert.That(rig.Pump.MessagesSent, Is.Zero);
        }

        [Test]
        public async Task AnAgentInTheRoom_IsSentAsBefore()
        {
            using var rig = new Rig();
            rig.Join(A, Room);

            await rig.Ticks(2);   // the first learns the mixer's version, the second sends
            Assert.That(rig.PosRequests, Has.Count.EqualTo(1), "the first request is the whole snapshot");
            Assert.That(rig.PosRequests[0]["room"].AsInteger(), Is.EqualTo(Room));
            Assert.That(rig.PosRequests[0]["full"].AsBoolean(), Is.True);

            rig.World.Views = new List<PositionView> { new PositionView(A, false, 100000, 200, 300, 0) };
            await rig.Ticks(1);
            Assert.That(rig.PosRequests, Has.Count.EqualTo(2), "and a move follows as a change");
            Assert.That(rig.PosRequests[1]["full"].AsBoolean(), Is.False);

            Assert.That(rig.Sink.Replies.Counts.Applied, Is.EqualTo(2));
            Assert.That(rig.Sink.Replies.Counts.UnknownRoom, Is.Zero);
            Assert.That(rig.Refusals.Count, Is.Zero);
            Assert.That(rig.Pump.MessagesHeldBack, Is.Zero);
        }

        [Test]
        public async Task AfterUnknownRoom_NothingMoreIsSent_UntilAJoin_AndThenAWholeSnapshot()
        {
            using var rig = new Rig();
            rig.Table.Record(A, Room);   // a record from a join the mixer has since lost (its last listener left)

            await rig.Ticks(2);
            Assert.That(rig.PosRequests, Has.Count.EqualTo(1));
            Assert.That(rig.Sink.Replies.Counts.UnknownRoom, Is.EqualTo(1), "the first request is refused");
            Assert.That(rig.Refusals.IsRefused(Room), Is.True);

            await rig.Ticks(20);
            Assert.That(rig.PosRequests, Has.Count.EqualTo(1), "nothing more is sent to the room");
            Assert.That(rig.Sink.Replies.Counts.UnknownRoom, Is.EqualTo(1), "and the mixer is not asked again");
            Assert.That(rig.Pump.MessagesHeldBack, Is.GreaterThan(0), "what was left out is counted");

            rig.Join(A, Room);   // a viewer is provisioned into the room again
            await rig.Ticks(1);
            Assert.That(rig.PosRequests, Has.Count.EqualTo(2), "sent to again");
            Assert.That(rig.PosRequests[1]["full"].AsBoolean(), Is.True, "as a whole snapshot");
            Assert.That(rig.Sink.Replies.Counts.Applied, Is.EqualTo(1));
            Assert.That(rig.Sink.Replies.Counts.UnknownRoom, Is.EqualTo(1));
        }

        [Test]
        public async Task ARefusalOfAnotherRoom_DoesNotHoldBackThisOne()
        {
            using var rig = new Rig();
            rig.Join(A, Room);
            rig.Refusals.Refused(OtherRoom, rig.Refusals.Stamp());

            await rig.Ticks(2);

            Assert.That(rig.PosRequests, Has.Count.EqualTo(1));
            Assert.That(rig.Sink.Replies.Counts.Applied, Is.EqualTo(1));
        }

        [Test]
        public async Task ARefusalToARequestSentBeforeAJoin_DoesNotHoldTheRoomBack()
        {
            using var rig = new Rig();
            rig.Table.Record(A, Room);
            rig.DuringRequest = () =>
            {
                rig.Join(A, Room);   // the join lands while the request is out ...
                rig.Held.Remove(Room);   // ... and the mixer's answer to the earlier request is still unknown_room
            };

            await rig.Ticks(2);
            Assert.That(rig.Sink.Replies.Counts.UnknownRoom, Is.EqualTo(1));
            Assert.That(rig.Refusals.IsRefused(Room), Is.False, "the answer describes the room before the join");

            rig.DuringRequest = null;
            rig.Held.Add(Room);
            await rig.Ticks(1);
            Assert.That(rig.PosRequests, Has.Count.EqualTo(2), "so the next tick sends again");
            Assert.That(rig.Sink.Replies.Counts.Applied, Is.EqualTo(1));
        }
    }
}
