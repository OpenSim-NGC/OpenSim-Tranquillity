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

        [Fact(Timeout = 60000)]
        public async Task AnAgentOnlyInAGroupCall_CausesNoPeerPosForItsParcelsRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();   // the agent stands on a parcel but has joined no room

            await rig.Ticks(8);

            {
                var assertedValue = rig.PosRequests;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nothing is sent for an agent with no record");
            }
            {
                var assertedValue = rig.Sink.Replies.Counts.UnknownRoom;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "so the mixer refuses nothing");
            }
            Assert.Equal(0, rig.Pump.MessagesSent);
        }

        [Fact(Timeout = 60000)]
        public async Task AnAgentInTheRoom_IsSentAsBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Join(A, Room);

            await rig.Ticks(2);   // the first learns the mixer's version, the second sends
            {
                var assertedValue = rig.PosRequests;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the first request is the whole snapshot");
            }
            Assert.Equal(Room, rig.PosRequests[0]["room"].AsInteger());
            Assert.True(rig.PosRequests[0]["full"].AsBoolean());

            rig.World.Views = new List<PositionView> { new PositionView(A, false, 100000, 200, 300, 0) };
            await rig.Ticks(1);
            {
                var assertedValue = rig.PosRequests;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "and a move follows as a change");
            }
            Assert.False(rig.PosRequests[1]["full"].AsBoolean());

            Assert.Equal(2, rig.Sink.Replies.Counts.Applied);
            Assert.Equal(0, rig.Sink.Replies.Counts.UnknownRoom);
            Assert.Equal(0, rig.Refusals.Count);
            Assert.Equal(0, rig.Pump.MessagesHeldBack);
        }

        [Fact(Timeout = 60000)]
        public async Task AfterUnknownRoom_NothingMoreIsSent_UntilAJoin_AndThenAWholeSnapshot()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Table.Record(A, Room);   // a record from a join the mixer has since lost (its last listener left)

            await rig.Ticks(2);
            Assert.Single(rig.PosRequests);
            {
                var assertedValue = rig.Sink.Replies.Counts.UnknownRoom;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the first request is refused");
            }
            Assert.True(rig.Refusals.IsRefused(Room));

            await rig.Ticks(20);
            {
                var assertedValue = rig.PosRequests;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "nothing more is sent to the room");
            }
            {
                var assertedValue = rig.Sink.Replies.Counts.UnknownRoom;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "and the mixer is not asked again");
            }
            Assert.True(rig.Pump.MessagesHeldBack > 0, "what was left out is counted");

            rig.Join(A, Room);   // a viewer is provisioned into the room again
            await rig.Ticks(1);
            {
                var assertedValue = rig.PosRequests;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "sent to again");
            }
            Assert.True(rig.PosRequests[1]["full"].AsBoolean(), "as a whole snapshot");
            Assert.Equal(1, rig.Sink.Replies.Counts.Applied);
            Assert.Equal(1, rig.Sink.Replies.Counts.UnknownRoom);
        }

        [Fact(Timeout = 60000)]
        public async Task ARefusalOfAnotherRoom_DoesNotHoldBackThisOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Join(A, Room);
            rig.Refusals.Refused(OtherRoom, rig.Refusals.Stamp());

            await rig.Ticks(2);

            Assert.Single(rig.PosRequests);
            Assert.Equal(1, rig.Sink.Replies.Counts.Applied);
        }

        [Fact(Timeout = 60000)]
        public async Task ARefusalToARequestSentBeforeAJoin_DoesNotHoldTheRoomBack()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Table.Record(A, Room);
            rig.DuringRequest = () =>
            {
                rig.Join(A, Room);   // the join lands while the request is out ...
                rig.Held.Remove(Room);   // ... and the mixer's answer to the earlier request is still unknown_room
            };

            await rig.Ticks(2);
            Assert.Equal(1, rig.Sink.Replies.Counts.UnknownRoom);
            Assert.False(rig.Refusals.IsRefused(Room), "the answer describes the room before the join");

            rig.DuringRequest = null;
            rig.Held.Add(Room);
            await rig.Ticks(1);
            {
                var assertedValue = rig.PosRequests;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "so the next tick sends again");
            }
            Assert.Equal(1, rig.Sink.Replies.Counts.Applied);
        }
    }
}
