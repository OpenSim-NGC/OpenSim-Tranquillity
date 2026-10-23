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
    [TestFixture]
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
#pragma warning disable CS0067
            public event Action<VisibilityBatch> BatchProduced;
#pragma warning restore CS0067
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
        [Test]
        public async Task AnAvatarThatJoinedAnOwnChannelRoom_IsAddressedAtThatRoom()
        {
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);
            rig.Join(A, OwnChannelRoom);

            await rig.Tick();

            Assert.That(rig.Rooms(), Is.EqualTo(new[] { OwnChannelRoom }), "armed at the joined room");
            Assert.That(rig.Rooms(), Does.Not.Contain(EstateRoom));
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A), Is.True);
            Assert.That(rig.Auth.IsArmed(EstateRoom, A), Is.False);
            Assert.That(rig.Auth.CurrentGeneration(EstateRoom), Is.Zero,
                "no generation is ever allocated for the estate number in this region");

            OSDMap hb = rig.Heartbeat();
            Assert.That(ListenersOf(hb, OwnChannelRoom), Is.EquivalentTo(new[] { A.ToString() }));
            Assert.That(((OSDMap)hb["rooms"]).ContainsKey(EstateRoom.ToString()), Is.False,
                "the heartbeat claims authority over the joined room only");
        }

        // ---- an avatar that joined the estate room is addressed there ----------------------

        /// <summary>An avatar on an estate-channel parcel that joined the estate room is addressed at it, with arming
        /// on; with the knob off nothing is sent at all, as the knob-off goldens pin. The goldens cover the off case
        /// against a file captured before; this covers the on case, which no golden file pins.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public async Task AnAvatarThatJoinedTheEstateRoom_IsAddressedAtIt(bool arming)
        {
            using var rig = new Rig(arming);
            rig.Place(A, ParcelEstate);
            rig.Place(B, ParcelEstate);
            rig.Join(A, EstateRoom);
            rig.Join(B, EstateRoom);
            await rig.Tick();
            await rig.Tick();

            if (arming)
                Assert.That(rig.Rooms(), Is.EqualTo(new[] { EstateRoom }), "the room those bytes name is the estate-channel number");
            else
                Assert.That(rig.Sent, Is.Empty, "with the knob off nothing is sent at all, as the goldens pin");
        }

        // ---- a record arrives ------------------------------------------------------------------

        /// <summary>The agent has no record, so it is armed nowhere, whatever parcel it stands on. A record then
        /// arrives - the viewer provisioned it - and from the next pass it is armed at that room and the heartbeat names it
        /// there and nowhere else.</summary>
        [Test]
        public async Task WhenARecordArrives_TheAgentIsArmedAtItAndNowhereElse()
        {
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);
            await rig.Tick();
            Assert.That(rig.Rooms(), Is.Empty, "no record, so no room is addressed");

            rig.Join(A, RecordedRoom);   // the viewer's provision lands
            await rig.Tick();

            Assert.That(rig.Auth.IsArmed(RecordedRoom, A), Is.True, "armed at the recorded room");
            Assert.That(rig.Rooms(), Is.EqualTo(new[] { RecordedRoom }));

            OSDMap hb = rig.Heartbeat();
            Assert.That(ListenersOf(hb, RecordedRoom), Is.EquivalentTo(new[] { A.ToString() }));
            Assert.That(ListenersOf(hb, OwnChannelRoom), Is.Empty, "and named nowhere at its parcel's room");
        }

        // ---- nowhere to put it ---------------------------------------------------------------

        /// <summary>No record - the sim has no evidence of a room this agent is in. It is left out of the batch and
        /// the heartbeat, counted on the sender, and nothing is allocated for any room on its behalf. It is never turned
        /// into an estate-room write.</summary>
        [Test]
        public async Task AnAgentWithNoRecord_IsOmittedAndCounted_AndAllocatesNothing()
        {
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);

            await rig.Tick();

            Assert.That(rig.Batches(), Is.Empty, "nothing addressed, so nothing on the wire");
            Assert.That(rig.Sender.Unplaced, Is.EqualTo(1), "the sender counts what it could not place");
            Assert.That(rig.Auth.CurrentGeneration(EstateRoom), Is.Zero);
            Assert.That(rig.Auth.CurrentGeneration(OwnChannelRoom), Is.Zero);
            Assert.That(rig.Auth.IsArmed(EstateRoom, A), Is.False);
            Assert.That(((OSDMap)rig.Heartbeat()["rooms"]).Count, Is.Zero, "and named in no room's entry");

            // It is picked up the moment a provision for it is recorded - nothing latches.
            rig.Join(A, OwnChannelRoom);
            await rig.Tick();
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A), Is.True);
            Assert.That(rig.Sender.Unplaced, Is.Zero);
        }

        // ---- what the operator reads ---------------------------------------------------------

        /// <summary>"show voice visibility" must report the rooms the last send RESOLVED and how many agents it
        /// could not place. A line that printed "fallback room N" first would read as an address, which is exactly
        /// what makes a wrong room look like a working fallback.</summary>
        [Test]
        public async Task TheConsoleReader_NamesTheResolvedRooms_AndCountsTheUnplaced()
        {
            using var rig = new Rig();
            rig.Place(A, ParcelOwn);
            rig.Join(A, OwnChannelRoom);
            rig.Place(B, ParcelOwn);   // no record: only in a call
            await rig.Tick();

            IReadOnlyList<string> lines = VoiceVisibilityCommands.Format("Example Region", rig.Sink, rig.Sender.Unplaced);
            string all = string.Join("\n", lines);

            Assert.That(lines[0], Does.Contain("addressed 1 room(s): " + OwnChannelRoom),
                "the rooms it actually addressed, by number");
            Assert.That(lines[0], Does.Contain("estate room " + rig.Sink.FallbackRoom),
                "the estate number is reported as the region's own, not as anybody's address");
            Assert.That(lines[0].IndexOf("addressed", StringComparison.Ordinal),
                Is.LessThan(lines[0].IndexOf("estate room", StringComparison.Ordinal)),
                "and it reads after the resolved rooms, not before them");
            Assert.That(lines[0], Does.Contain("1 agent(s) unplaced and omitted"),
                "B has no record, and the count of that is the headline, not a room number");
            Assert.That(all, Does.Contain("unplaced in the send"), "the counters are named for what they count");
            Assert.That(all, Does.Contain("held back (unknown room)"), "and the rooms held back after unknown_room are named");
            Assert.That(all, Does.Not.Contain("fallback"), "nothing here calls an omission a fallback any more");
            Assert.That(rig.Sink.LastSendRoomNumbers, Is.EqualTo(new[] { OwnChannelRoom }));
        }

        // ---- the mixer is not sent batches for a room it does not hold ------------------------------

        /// <summary>An avatar that is only in a group or avatar-to-avatar call stands on a parcel but never joined its
        /// room, so the mixer does not hold that room. Nothing is sent for it - not at the arming pass, not at later
        /// passes - and so the mixer never has to refuse it.</summary>
        [TestCase(1)]
        [TestCase(8)]
        public async Task F1_AnAgentOnlyInAGroupCall_CausesNoBatchForItsParcelsRoom(int passes)
        {
            using var rig = new Rig();
            rig.Held = new HashSet<int>();   // the mixer holds no room
            rig.Place(A, ParcelOwn);
            rig.Place(B, ParcelOwn);

            for (int i = 0; i < passes; i++)
                await rig.Tick(advanceMs: 2000);

            Assert.That(rig.Batches(), Is.Empty, "no peer_ctl_batch for any room");
            Assert.That(rig.UnknownRoomReplies, Is.Zero, "so the mixer refused nothing");
            Assert.That(rig.Sender.Unplaced, Is.EqualTo(2), "both agents are counted as left out");
            Assert.That(rig.Refusals.Count, Is.Zero);
            Assert.That(((OSDMap)rig.Heartbeat()["rooms"]).Count, Is.Zero, "and named in no heartbeat entry");
        }

        /// <summary>The same agents, joined to their room as the viewer does, are addressed at it as before.</summary>
        [Test]
        public async Task F2_AnAgentInTheRoom_IsAddressedExactlyAsBefore()
        {
            using var rig = new Rig();
            rig.Held = new HashSet<int>();
            rig.Place(A, ParcelOwn);
            rig.Place(B, ParcelOwn);
            rig.Join(A, OwnChannelRoom);
            rig.Join(B, OwnChannelRoom);

            await rig.Tick();
            await rig.Tick();

            Assert.That(rig.Rooms(), Is.EqualTo(new[] { OwnChannelRoom }));
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A) && rig.Auth.IsArmed(OwnChannelRoom, B), Is.True);
            Assert.That(rig.UnknownRoomReplies, Is.Zero);
            Assert.That(rig.Refusals.Count, Is.Zero);
            Assert.That(rig.Sender.Unplaced, Is.Zero);
            Assert.That(rig.Sink.RoomsHeldBack, Is.Zero);
        }

        /// <summary>The mixer drops a room (its last listener left, or it restarted). The next request to it is answered
        /// unknown_room; after that nothing more is sent to the room, however long the sim waits and however often the
        /// authority would have retried, until the region records a join into it again.</summary>
        [Test]
        public async Task F3_AfterUnknownRoom_NothingMoreIsSent_UntilAJoin()
        {
            using var rig = new Rig();
            rig.Held = new HashSet<int>();
            rig.Place(A, ParcelOwn);
            rig.Join(A, OwnChannelRoom);
            await rig.Tick();
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A), Is.True, "armed while the mixer held the room");

            rig.Held.Remove(OwnChannelRoom);   // the mixer lost the room
            rig.Auth.RequestArm(A);
            await rig.Tick();
            Assert.That(rig.UnknownRoomReplies, Is.EqualTo(1), "the first request is refused");
            Assert.That(rig.Refusals.IsRefused(OwnChannelRoom), Is.True);

            rig.Sent.Clear();
            for (int i = 0; i < 20; i++)
            {
                rig.Auth.RequestArm(A);
                await rig.Tick(advanceMs: 20_000);   // far past the authority's own retry delay
            }
            Assert.That(rig.Batches(), Is.Empty, "nothing more is sent to the room");
            Assert.That(rig.UnknownRoomReplies, Is.EqualTo(1), "and the mixer is not asked again");

            rig.Join(A, OwnChannelRoom);   // a viewer is provisioned into it again: the mixer holds it
            await rig.Tick();
            Assert.That(rig.Refusals.IsRefused(OwnChannelRoom), Is.False);
            Assert.That(rig.Rooms(), Is.EqualTo(new[] { OwnChannelRoom }), "sent to again");
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A), Is.True);
            Assert.That(rig.UnknownRoomReplies, Is.EqualTo(1));
        }

        /// <summary>One room being refused does not hold back another: the back-off is per room.</summary>
        [Test]
        public async Task F4_ARefusedRoom_DoesNotHoldBackAnotherRoom()
        {
            using var rig = new Rig();
            rig.Held = new HashSet<int>();
            rig.Place(A, ParcelOwn);
            rig.Place(B, ParcelEstate);
            rig.Join(A, OwnChannelRoom);
            rig.Join(B, EstateRoom);
            rig.Held.Remove(OwnChannelRoom);   // only A's room is gone
            await rig.Tick();

            Assert.That(rig.Refusals.IsRefused(OwnChannelRoom), Is.True);
            Assert.That(rig.Refusals.IsRefused(EstateRoom), Is.False);
            Assert.That(rig.Auth.IsArmed(EstateRoom, B), Is.True, "B is armed in the room the mixer holds");
        }

        /// <summary>The sink holds back a refused room whatever the sender does: a send that names the room goes out
        /// empty, and says so in the counters. This is the guard of the path that does not arm.</summary>
        [Test]
        public async Task F5_TheSink_LeavesARefusedRoomOut_AndCountsIt()
        {
            using var rig = new Rig(arming: false);
            rig.Held = new HashSet<int>();
            rig.Table.Record(A, OwnChannelRoom);
            rig.Table.Record(B, OwnChannelRoom);
            var excl = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [A] = new[] { B } };

            await rig.Sink.SendAsync(VisOp.Replace, excl);
            Assert.That(rig.UnknownRoomReplies, Is.EqualTo(1), "the first send is refused");

            rig.Sent.Clear();
            await rig.Sink.SendAsync(VisOp.Replace, excl);
            await rig.Sink.SendAsync(VisOp.Replace, excl);
            Assert.That(rig.Sent, Is.Empty, "later sends name the room and go nowhere");
            Assert.That(rig.Sink.LastSendHeldBackRooms, Is.EqualTo(1));
            Assert.That(rig.Sink.RoomsHeldBack, Is.EqualTo(2));
            Assert.That(rig.Sink.LastSendRooms, Is.Zero);

            rig.Held.Add(OwnChannelRoom);
            rig.Refusals.Joined(OwnChannelRoom);
            await rig.Sink.SendAsync(VisOp.Replace, excl);
            Assert.That(rig.Batches(), Has.Count.EqualTo(1), "after a join the room is sent to again");
            Assert.That(rig.Sink.LastSendHeldBackRooms, Is.Zero);
        }

        // ---- RoomRefusals ------------------------------------------------------------------------

        [Test]
        public void R1_ARefusalFromBeforeAJoin_IsIgnored_AndOneFromAfterIsKept()
        {
            var r = new RoomRefusals();
            long sentBeforeJoin = r.Stamp();
            r.Joined(OwnChannelRoom);                    // a viewer joins while the request is in flight
            r.Refused(OwnChannelRoom, sentBeforeJoin);   // the answer to the earlier request arrives late
            Assert.That(r.IsRefused(OwnChannelRoom), Is.False, "that answer describes the room before the join");

            r.Refused(OwnChannelRoom, r.Stamp());        // a request sent after the join
            Assert.That(r.IsRefused(OwnChannelRoom), Is.True);
            r.Joined(OwnChannelRoom);
            Assert.That(r.IsRefused(OwnChannelRoom), Is.False, "a join clears it");
            Assert.That(r.Count, Is.Zero);
        }
    }
}
