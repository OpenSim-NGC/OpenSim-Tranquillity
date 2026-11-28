/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * An agent is addressed at the room it joined, and never at a guessed one.
 *
 * WHY NOT A DEFAULT. The estate-channel room is a real, live room in every region, so addressing an agent with no
 * AgentRoomTable record there would write that agent's live exclusion and mute state into the WRONG room on a parcel
 * with its own voice channel, and would create policy for a room no viewer had provisioned on an empty region. The
 * room its parcel would give it is a guess as well: an agent that is only in a group or avatar-to-avatar call never
 * joined its parcel's room, so the mixer does not hold it, and every batch to it would be answered unknown_room.
 *
 * THE RULE THIS FILE PINS. The resolver answers with the room the agent's last provision joined, and with nothing else:
 *   1. the agent's room RECORD, if it has one;
 *   2. else nothing: the agent is OMITTED from every batch, every column and every heartbeat entry, and counted.
 * The estate number and the parcel's number are never an address merely because they are the default or the likely one.
 * A room the mixer has answered unknown_room for is left out of every send until a join into it is recorded.
 *
 * The rig composes the resolver exactly as VoiceVisibilityService does (the AgentRoomTable, nothing else), with the
 * service's own RoomRefusals wiring; the record table is also proven by AgentRoomTableTests.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class RoomAddressingTests
    {
        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        private static readonly UUID A = Id(1), B = Id(2);
        private static readonly UUID ParcelOwn = Id(201), ParcelEstate = Id(202);

        private const int EstateRoom = 226001844;   // the region's estate-channel room
        private const int OwnChannelRoom = 226001999;   // the room of a parcel with its own channel
        private const int RecordedRoom = 226002777;   // what a record names, when one arrives

        // ---- rig -------------------------------------------------------------------------------

        private sealed class World : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public IReadOnlyList<AgentView> SnapshotAgents() => Agents.ToList();
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(ParcelEstate);
            public ParcelView GetParcelByGlobalId(UUID id)
                => new ParcelView(id, seeAVs: true, allowVoiceChat: true,
                    isBannedFromLand: _ => false, isRestrictedFromLand: _ => false, isVoiceModerated: _ => false);
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        private sealed class Feed : IVisibilityFeed
        {
            private readonly IFeederWorld _world;
            public Feed(IFeederWorld world) { _world = world; Current = VisibilityMatrix.Build(world); }
            public VisibilityMatrix Current { get; private set; }
            // Required by the interface; this fake never raises it.
            public event Action<VisibilityBatch> BatchProduced { add { } remove { } }
            public VisibilityBatch SnapshotFor(UUID listener) => DeltaComputer.SnapshotFor(Current, listener, -999);
            public VisibilityBatch Tick() { Current = VisibilityMatrix.Build(_world); return VisibilityBatch.EmptyDelta(-999); }
        }

        /// The sim as this change leaves it: a record table, the refused rooms, and a mixer that holds some rooms.
        private sealed class Rig : IDisposable
        {
            public long Clock = 10_000;
            public readonly World World = new();
            public readonly List<OSDMap> Sent = new();
            /// <summary>The service's record of the room each agent's last provision joined.</summary>
            public readonly AgentRoomTable Table = new();
            /// <summary>The service's record of the rooms the mixer refused as unknown.</summary>
            public readonly RoomRefusals Refusals = new();
            /// <summary>The rooms the mixer holds, or null when it holds every room asked for. A batch for any other
            /// room is answered unknown_room, as the mixer does.</summary>
            public HashSet<int> Held;
            /// <summary>Requests the mixer answered unknown_room.</summary>
            public int UnknownRoomReplies;
            public readonly VisAuthority Auth;
            public readonly JanusPeerCtlBatchSink Sink;
            public readonly VisibilityBatchSender Sender;
            public readonly Feed Feed;

            public Rig(bool arming = true)
            {
                Feed = new Feed(World);
                Sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), Id(77), "o92",
                    sendOne: Send);
                Auth = new VisAuthority(0x0000018f00000001UL, "o92", () => Clock);
                Sink.Authority = Auth;
                Sink.RoomOf = Resolve;
                Sink.Refusals = Refusals;
                Sender = new VisibilityBatchSender(Feed, Sink, arming, TimeSpan.FromSeconds(5), "o92", () => Clock,
                    Auth, Resolve, Refusals);
            }

            /// <summary>VoiceVisibilityService.ResolveRoom: the record, else null.</summary>
            public int? Resolve(UUID agent) => Table.Resolve(agent);

            /// <summary>VoiceVisibilityService.OnListenerProvisioned for a successful provision: the viewer joined
            /// the room, so the mixer holds it.</summary>
            public void Join(UUID agent, int room)
            {
                Held?.Add(room);
                Table.Record(agent, room);
                Refusals.Joined(room);
                Auth.RoomExists(room);
                Sender.OnListenerProvisioned(agent);
            }

            private Task<(AdminSendResult, string)> Send(OSDMap request)
            {
                Sent.Add((OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(request)));
                string reply;
                if (request["request"].AsString() == "peer_ctl_heartbeat")
                    reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"heartbeat\",\"vis_protocol\":2," +
                            "\"mixer_instance\":\"m1\",\"rooms\":{}}}";
                else if (Held != null && !Held.Contains(request["room"].AsInteger()))
                {
                    UnknownRoomReplies++;
                    reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"error\",\"reason\":\"unknown_room\"," +
                            "\"vis_protocol\":2,\"mixer_instance\":\"m1\"}}";
                }
                else
                    reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"vis_protocol\":2," +
                            "\"mixer_instance\":\"m1\",\"room\":" + request["room"].AsInteger() + "}}";
                return Task.FromResult((AdminSendResult.Ok, reply));
            }

            /// <summary>An avatar standing on a parcel. It has joined no room: that is Join.</summary>
            public void Place(UUID agent, UUID parcel)
                => World.Agents.Add(new AgentView(agent, false, Vector3.Zero, parcel, false));

            public async Task Tick(long advanceMs = 250)
            {
                Clock += advanceMs;
                await Sender.PumpAsync(Feed.Tick());
            }

            public List<OSDMap> Batches() => Sent.Where(m => m["request"].AsString() == "peer_ctl_batch").ToList();
            public List<int> Rooms() => Batches().Select(b => b["room"].AsInteger()).Distinct().OrderBy(x => x).ToList();
            public OSDMap Heartbeat(bool stopping = false)
                => Auth.BuildHeartbeat(Feed.Current.Population, Resolve, stopping);
            public void Dispose() => Sink.Dispose();
        }

        private static HashSet<string> ListenersOf(OSDMap heartbeat, int room)
        {
            var rooms = (OSDMap)heartbeat["rooms"];
            if (!rooms.ContainsKey(room.ToString()))
                return new HashSet<string>();
            return new HashSet<string>(((OSDMap)((OSDMap)rooms[room.ToString()])["listeners"]).Keys);
        }

        // ---- the recorded room, never the estate number ------------------------------------

        /// <summary>An avatar that joined a room of its parcel's own voice channel. That is where it is armed and
        /// where the heartbeat names it. The estate number is not addressed at all - and, the sharper half, no
        /// generation is ever ALLOCATED for it.</summary>
        [Fact(Timeout = 60000)]
        public async Task AnAvatarThatJoinedAnOwnChannelRoom_IsAddressedAtThatRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);
            rig.Join(A, OwnChannelRoom);

            await rig.Tick();

            {
                var assertedValue = rig.Rooms();
                TestAssert.WithMessage(() => Assert.Equal(new[] { OwnChannelRoom }, assertedValue), "armed at the joined room");
            }
            Assert.DoesNotContain(EstateRoom, rig.Rooms());
            Assert.True(rig.Auth.IsArmed(OwnChannelRoom, A));
            Assert.False(rig.Auth.IsArmed(EstateRoom, A));
            {
                var assertedValue = rig.Auth.CurrentGeneration(EstateRoom);
                TestAssert.WithMessage(() => Assert.Equal(0u, assertedValue), "no generation is ever allocated for the estate number in this region");
            }

            OSDMap hb = rig.Heartbeat();
            Assert.Equivalent(new[] { A.ToString() }, ListenersOf(hb, OwnChannelRoom), strict: true);
            Assert.False(((OSDMap)hb["rooms"]).ContainsKey(EstateRoom.ToString()), "the heartbeat claims authority over the joined room only");
        }

        // ---- an avatar that joined the estate room is addressed there ----------------------

        /// <summary>An avatar on an estate-channel parcel that joined the estate room is addressed at it, with arming
        /// on; with the knob off nothing is sent at all, as the knob-off goldens pin. The goldens cover the off case
        /// against a file captured before; this covers the on case, which no golden file pins.</summary>
        [InlineData(true)]
        [InlineData(false)]
        [Theory(Timeout = 60000)]
        public async Task AnAvatarThatJoinedTheEstateRoom_IsAddressedAtIt(bool arming)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig(arming);
            rig.Place(A, ParcelEstate);
            rig.Place(B, ParcelEstate);
            rig.Join(A, EstateRoom);
            rig.Join(B, EstateRoom);
            await rig.Tick();
            await rig.Tick();

            if (arming)
                {
                    var assertedValue = rig.Rooms();
                    TestAssert.WithMessage(() => Assert.Equal(new[] { EstateRoom }, assertedValue), "the room those bytes name is the estate-channel number");
                }
            else
                {
                    var assertedValue = rig.Sent;
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "with the knob off nothing is sent at all, as the goldens pin");
                }
        }

        // ---- a record arrives ------------------------------------------------------------------

        /// <summary>The agent has no record, so it is armed nowhere, whatever parcel it stands on. A record then
        /// arrives - the viewer provisioned it - and from the next pass it is armed at that room and the heartbeat names it
        /// there and nowhere else.</summary>
        [Fact(Timeout = 60000)]
        public async Task WhenARecordArrives_TheAgentIsArmedAtItAndNowhereElse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);
            await rig.Tick();
            {
                var assertedValue = rig.Rooms();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no record, so no room is addressed");
            }

            rig.Join(A, RecordedRoom);   // the viewer's provision lands
            await rig.Tick();

            Assert.True(rig.Auth.IsArmed(RecordedRoom, A), "armed at the recorded room");
            Assert.Equal(new[] { RecordedRoom }, rig.Rooms());

            OSDMap hb = rig.Heartbeat();
            Assert.Equivalent(new[] { A.ToString() }, ListenersOf(hb, RecordedRoom), strict: true);
            {
                var assertedValue = ListenersOf(hb, OwnChannelRoom);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "and named nowhere at its parcel's room");
            }
        }

        // ---- nowhere to put it ---------------------------------------------------------------

        /// <summary>No record - the sim has no evidence of a room this agent is in. It is left out of the batch and
        /// the heartbeat, counted on the sender, and nothing is allocated for any room on its behalf. It is never turned
        /// into an estate-room write.</summary>
        [Fact(Timeout = 60000)]
        public async Task AnAgentWithNoRecord_IsOmittedAndCounted_AndAllocatesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);

            await rig.Tick();

            {
                var assertedValue = rig.Batches();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nothing addressed, so nothing on the wire");
            }
            {
                var assertedValue = rig.Sender.Unplaced;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the sender counts what it could not place");
            }
            Assert.Equal(0u, rig.Auth.CurrentGeneration(EstateRoom));
            Assert.Equal(0u, rig.Auth.CurrentGeneration(OwnChannelRoom));
            Assert.False(rig.Auth.IsArmed(EstateRoom, A));
            {
                var assertedValue = ((OSDMap)rig.Heartbeat()["rooms"]).Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "and named in no room's entry");
            }

            // It is picked up the moment a provision for it is recorded - nothing latches.
            rig.Join(A, OwnChannelRoom);
            await rig.Tick();
            Assert.True(rig.Auth.IsArmed(OwnChannelRoom, A));
            Assert.Equal(0, rig.Sender.Unplaced);
        }

        // ---- what the operator reads ---------------------------------------------------------

        /// <summary>"show voice visibility" must report the rooms the last send RESOLVED and how many agents it
        /// could not place. A line that printed "fallback room N" first would read as an address, which is exactly
        /// what makes a wrong room look like a working fallback.</summary>
        [Fact(Timeout = 60000)]
        public async Task TheConsoleReader_NamesTheResolvedRooms_AndCountsTheUnplaced()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);
            rig.Join(A, OwnChannelRoom);
            rig.Place(B, ParcelOwn);   // no record: only in a call
            await rig.Tick();

            IReadOnlyList<string> lines = VoiceVisibilityCommands.Format("Example Region", rig.Sink, rig.Sender.Unplaced);
            string all = string.Join("\n", lines);

            {
                var assertedValue = lines[0];
                TestAssert.WithMessage(() => Assert.Contains("addressed 1 room(s): " + OwnChannelRoom, assertedValue), "the rooms it actually addressed, by number");
            }
            {
                var assertedValue = lines[0];
                TestAssert.WithMessage(() => Assert.Contains("estate room " + rig.Sink.FallbackRoom, assertedValue), "the estate number is reported as the region's own, not as anybody's address");
            }
            Assert.True(lines[0].IndexOf("addressed", StringComparison.Ordinal) < lines[0].IndexOf("estate room", StringComparison.Ordinal), "and it reads after the resolved rooms, not before them");
            {
                var assertedValue = lines[0];
                TestAssert.WithMessage(() => Assert.Contains("1 agent(s) unplaced and omitted", assertedValue), "B has no record, and the count of that is the headline, not a room number");
            }
            {
                var assertedValue = all;
                TestAssert.WithMessage(() => Assert.Contains("unplaced in the send", assertedValue), "the counters are named for what they count");
            }
            {
                var assertedValue = all;
                TestAssert.WithMessage(() => Assert.Contains("held back (unknown room)", assertedValue), "and the rooms held back after unknown_room are named");
            }
            {
                var assertedValue = all;
                TestAssert.WithMessage(() => Assert.DoesNotContain("fallback", assertedValue), "nothing here calls an omission a fallback any more");
            }
            Assert.Equal(new[] { OwnChannelRoom }, rig.Sink.LastSendRoomNumbers);
        }

        // ---- the mixer is not sent batches for a room it does not hold ------------------------------

        /// <summary>An avatar that is only in a group or avatar-to-avatar call stands on a parcel but never joined its
        /// room, so the mixer does not hold that room. Nothing is sent for it - not at the arming pass, not at later
        /// passes - and so the mixer never has to refuse it.</summary>
        [InlineData(1)]
        [InlineData(8)]
        [Theory(Timeout = 60000)]
        public async Task F1_AnAgentOnlyInAGroupCall_CausesNoBatchForItsParcelsRoom(int passes)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Held = new HashSet<int>();   // the mixer holds no room
            rig.Place(A, ParcelOwn);
            rig.Place(B, ParcelOwn);

            for (int i = 0; i < passes; i++)
                await rig.Tick(advanceMs: 2000);

            {
                var assertedValue = rig.Batches();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no peer_ctl_batch for any room");
            }
            {
                var assertedValue = rig.UnknownRoomReplies;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "so the mixer refused nothing");
            }
            {
                var assertedValue = rig.Sender.Unplaced;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "both agents are counted as left out");
            }
            Assert.Equal(0, rig.Refusals.Count);
            {
                var assertedValue = ((OSDMap)rig.Heartbeat()["rooms"]).Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "and named in no heartbeat entry");
            }
        }

        /// <summary>The same agents, joined to their room as the viewer does, are addressed at it as before.</summary>
        [Fact(Timeout = 60000)]
        public async Task F2_AnAgentInTheRoom_IsAddressedExactlyAsBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Held = new HashSet<int>();
            rig.Place(A, ParcelOwn);
            rig.Place(B, ParcelOwn);
            rig.Join(A, OwnChannelRoom);
            rig.Join(B, OwnChannelRoom);

            await rig.Tick();
            await rig.Tick();

            Assert.Equal(new[] { OwnChannelRoom }, rig.Rooms());
            Assert.True(rig.Auth.IsArmed(OwnChannelRoom, A) && rig.Auth.IsArmed(OwnChannelRoom, B));
            Assert.Equal(0, rig.UnknownRoomReplies);
            Assert.Equal(0, rig.Refusals.Count);
            Assert.Equal(0, rig.Sender.Unplaced);
            Assert.Equal(0, rig.Sink.RoomsHeldBack);
        }

        /// <summary>The mixer drops a room (its last listener left, or it restarted). The next request to it is answered
        /// unknown_room; after that nothing more is sent to the room, however long the sim waits and however often the
        /// authority would have retried, until the region records a join into it again.</summary>
        [Fact(Timeout = 60000)]
        public async Task F3_AfterUnknownRoom_NothingMoreIsSent_UntilAJoin()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Held = new HashSet<int>();
            rig.Place(A, ParcelOwn);
            rig.Join(A, OwnChannelRoom);
            await rig.Tick();
            Assert.True(rig.Auth.IsArmed(OwnChannelRoom, A), "armed while the mixer held the room");

            rig.Held.Remove(OwnChannelRoom);   // the mixer lost the room
            rig.Auth.RequestArm(A);
            await rig.Tick();
            {
                var assertedValue = rig.UnknownRoomReplies;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the first request is refused");
            }
            Assert.True(rig.Refusals.IsRefused(OwnChannelRoom));

            rig.Sent.Clear();
            for (int i = 0; i < 20; i++)
            {
                rig.Auth.RequestArm(A);
                await rig.Tick(advanceMs: 20_000);   // far past the authority's own retry delay
            }
            {
                var assertedValue = rig.Batches();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nothing more is sent to the room");
            }
            {
                var assertedValue = rig.UnknownRoomReplies;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "and the mixer is not asked again");
            }

            rig.Join(A, OwnChannelRoom);   // a viewer is provisioned into it again: the mixer holds it
            await rig.Tick();
            Assert.False(rig.Refusals.IsRefused(OwnChannelRoom));
            {
                var assertedValue = rig.Rooms();
                TestAssert.WithMessage(() => Assert.Equal(new[] { OwnChannelRoom }, assertedValue), "sent to again");
            }
            Assert.True(rig.Auth.IsArmed(OwnChannelRoom, A));
            Assert.Equal(1, rig.UnknownRoomReplies);
        }

        /// <summary>One room being refused does not hold back another: the back-off is per room.</summary>
        [Fact(Timeout = 60000)]
        public async Task F4_ARefusedRoom_DoesNotHoldBackAnotherRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig();
            rig.Held = new HashSet<int>();
            rig.Place(A, ParcelOwn);
            rig.Place(B, ParcelEstate);
            rig.Join(A, OwnChannelRoom);
            rig.Join(B, EstateRoom);
            rig.Held.Remove(OwnChannelRoom);   // only A's room is gone
            await rig.Tick();

            Assert.True(rig.Refusals.IsRefused(OwnChannelRoom));
            Assert.False(rig.Refusals.IsRefused(EstateRoom));
            Assert.True(rig.Auth.IsArmed(EstateRoom, B), "B is armed in the room the mixer holds");
        }

        /// <summary>The sink holds back a refused room whatever the sender does: a send that names the room goes out
        /// empty, and says so in the counters. This is the guard of the path that does not arm.</summary>
        [Fact(Timeout = 60000)]
        public async Task F5_TheSink_LeavesARefusedRoomOut_AndCountsIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var rig = new Rig(arming: false);
            rig.Held = new HashSet<int>();
            rig.Table.Record(A, OwnChannelRoom);
            rig.Table.Record(B, OwnChannelRoom);
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [A] = new[] { B } };

            await rig.Sink.SendAsync(VisOp.Replace, excl);
            {
                var assertedValue = rig.UnknownRoomReplies;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the first send is refused");
            }

            rig.Sent.Clear();
            await rig.Sink.SendAsync(VisOp.Replace, excl);
            await rig.Sink.SendAsync(VisOp.Replace, excl);
            {
                var assertedValue = rig.Sent;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "later sends name the room and go nowhere");
            }
            Assert.Equal(1, rig.Sink.LastSendHeldBackRooms);
            Assert.Equal(2, rig.Sink.RoomsHeldBack);
            Assert.Equal(0, rig.Sink.LastSendRooms);

            rig.Held.Add(OwnChannelRoom);
            rig.Refusals.Joined(OwnChannelRoom);
            await rig.Sink.SendAsync(VisOp.Replace, excl);
            {
                var assertedValue = rig.Batches();
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "after a join the room is sent to again");
            }
            Assert.Equal(0, rig.Sink.LastSendHeldBackRooms);
        }

        // ---- RoomRefusals ------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void R1_ARefusalFromBeforeAJoin_IsIgnored_AndOneFromAfterIsKept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = new RoomRefusals();
            long sentBeforeJoin = r.Stamp();
            r.Joined(OwnChannelRoom);                    // a viewer joins while the request is in flight
            r.Refused(OwnChannelRoom, sentBeforeJoin);   // the answer to the earlier request arrives late
            Assert.False(r.IsRefused(OwnChannelRoom), "that answer describes the room before the join");

            r.Refused(OwnChannelRoom, r.Stamp());        // a request sent after the join
            Assert.True(r.IsRefused(OwnChannelRoom));
            r.Joined(OwnChannelRoom);
            Assert.False(r.IsRefused(OwnChannelRoom), "a join clears it");
            Assert.Equal(0, r.Count);
        }
    }
}
