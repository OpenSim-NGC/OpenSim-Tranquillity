/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * 2: an agent is addressed at the room it is IN, and never at a guessed one.
 *
 * THE DEFECT. Until this change the sim had one rule for an agent with no AgentRoomTable record: address it at the
 * region's estate-channel room. That number is a real, live room in every region, so the "fallback" was not inert -
 * on a parcel with its own voice channel it wrote that agent's live exclusion and mute state into the WRONG room, and
 * on an empty region it created policy for a room no viewer had provisioned. It also looked healthy: the send
 * succeeded, the counters called it a fallback, and the operator saw a number.
 *
 * THE RULE THIS FILE PINS (B). The resolver answers in this order, and only this order:
 *   1. the agent's room RECORD, if it has one;
 *   2. else the room the parcel logic (FeederWorldFromScene.ParcelLocalIdFor) says an avatar at that agent's current position would be
 *      provisioned into - its parcel's room, which on an estate-channel parcel IS the estate number;
 *   3. else nothing: the agent is OMITTED from every batch, every column and every heartbeat entry, and counted.
 * The estate number is never an address merely because it is the default. Where it is the RESOLVED number - an
 * estate-channel parcel, which is the common case - every byte is exactly what it was before this change, which is
 * why the knob-off goldens are untouched (O2 below proves the knob-ON half the goldens do not cover).
 *
 * The rig composes the resolver exactly as VoiceVisibilityService does (record, else parcel, else null); the sim-side
 * halves of that composition are proven where they live - the record table by AgentRoomTableTests, the parcel number
 * by FeederWorldFromSceneTests.
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
        private const int OwnChannelRoom = 226001999;   // what a parcel with its own channel resolves to
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

        /// The sim as this change leaves it: a record table, a parcel resolution, and the composition of the two.
        private sealed class Rig : IDisposable
        {
            public long Clock = 10_000;
            public readonly World World = new();
            public readonly List<OSDMap> Sent = new();
            /// <summary>The AgentRoomTable's answer: an agent a viewer has been provisioned for.</summary>
            public readonly Dictionary<UUID, int> Records = new();
            /// <summary>What the agent's own parcel resolves to - the number FeederWorldFromScene gives it.</summary>
            public readonly Dictionary<UUID, int> ParcelRooms = new();
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
                Sender = new VisibilityBatchSender(Feed, Sink, arming, TimeSpan.FromSeconds(5), "o92", () => Clock,
                    Auth, Resolve);
            }

            /// <summary>VoiceVisibilityService.ResolveRoom, as it now reads: record, else the parcel's room, else null.</summary>
            public int? Resolve(UUID agent)
            {
                if (Records.TryGetValue(agent, out int recorded))
                    return recorded;
                if (ParcelRooms.TryGetValue(agent, out int parcel))
                    return parcel;
                return null;
            }

            private Task<(AdminSendResult, string)> Send(OSDMap request)
            {
                Sent.Add((OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(request)));
                string reply = request["request"].AsString() == "peer_ctl_heartbeat"
                    ? "{\"janus\":\"success\",\"response\":{\"slvoice\":\"heartbeat\",\"vis_protocol\":2," +
                      "\"mixer_instance\":\"m1\",\"rooms\":{}}}"
                    : "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"vis_protocol\":2," +
                      "\"mixer_instance\":\"m1\",\"room\":" + request["room"].AsInteger() + "}}";
                return Task.FromResult((AdminSendResult.Ok, reply));
            }

            public void Place(UUID agent, UUID parcel, int parcelRoom)
            {
                World.Agents.Add(new AgentView(agent, false, Vector3.Zero, parcel, false));
                ParcelRooms[agent] = parcelRoom;
            }

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

        // ---- O1: the parcel's room, never the estate number ------------------------------------

        /// <summary>O1: an avatar with no record, standing on a parcel with its OWN voice channel. Its parcel resolves
        /// to a room of its own, so that is where it is armed and where the heartbeat names it. The estate number is
        /// not addressed at all - and, the sharper half, no generation is ever ALLOCATED for it: before 0.8c2 the
        /// arming pass called NextGeneration(estate) and burned a generation on a room it then wrote to.</summary>
        [Test]
        public async Task O1_UnrecordedAvatarOnAnOwnChannelParcel_IsAddressedAtItsParcelsRoom()
        {
            using var rig = new Rig();
            rig.Place(A, ParcelOwn, OwnChannelRoom);

            await rig.Tick();

            Assert.That(rig.Rooms(), Is.EqualTo(new[] { OwnChannelRoom }), "armed at the parcel's room");
            Assert.That(rig.Rooms(), Does.Not.Contain(EstateRoom));
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A), Is.True);
            Assert.That(rig.Auth.IsArmed(EstateRoom, A), Is.False);
            Assert.That(rig.Auth.CurrentGeneration(EstateRoom), Is.Zero,
                "no generation is ever allocated for the estate number in this region");

            OSDMap hb = rig.Heartbeat();
            Assert.That(ListenersOf(hb, OwnChannelRoom), Is.EquivalentTo(new[] { A.ToString() }));
            Assert.That(((OSDMap)hb["rooms"]).ContainsKey(EstateRoom.ToString()), Is.False,
                "the heartbeat claims authority over the parcel's room only");
        }

        // ---- O2: on an estate-channel parcel, nothing changed at all ---------------------------

        /// <summary>O2: the resolved number for an estate-channel parcel IS the estate number, so an unrecorded avatar
        /// standing there produces exactly the bytes it produced before 0.8c2 - byte-for-byte those of an avatar
        /// RECORDED at that same room. Run with arming on and off. The knob-off goldens cover the off case against a
        /// file captured before; this covers the on case, which no golden file pins.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public async Task O2_UnrecordedAvatarOnAnEstateChannelParcel_ProducesIdenticalBytes(bool arming)
        {
            string resolved = await TranscriptAsync(arming, recorded: false);
            string byRecord = await TranscriptAsync(arming, recorded: true);

            Assert.That(resolved, Is.EqualTo(byRecord),
                "an agent RESOLVED to the estate room and an agent RECORDED there are indistinguishable on the wire");
            if (arming)
                Assert.That(resolved, Does.Contain(EstateRoom.ToString()),
                    "and with arming on, the room those bytes name is the estate-channel number");
            else
                Assert.That(resolved, Is.Empty, "with the knob off nothing is sent at all, as the goldens pin");
        }

        private static async Task<string> TranscriptAsync(bool arming, bool recorded)
        {
            using var rig = new Rig(arming);
            rig.Place(A, ParcelEstate, EstateRoom);
            rig.Place(B, ParcelEstate, EstateRoom);
            if (recorded)
            {
                rig.Records[A] = EstateRoom;
                rig.Records[B] = EstateRoom;
            }
            await rig.Tick();
            await rig.Tick();
            return string.Join("\n", rig.Sent
                .Select(OSDParser.SerializeJsonString)
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        // ---- O3: a record arrives and disagrees with the parcel --------------------------------

        /// <summary>O3: the agent was armed at its parcel's room while it had no record. A record then arrives naming a
        /// DIFFERENT room - the viewer provisioned it elsewhere, which is the authority. From the next pass the agent
        /// is armed at the recorded room and the heartbeat stops naming it at the resolved one: the record wins, and
        /// the sim does not keep claiming a room it no longer places the agent in.</summary>
        [Test]
        public async Task O3_WhenARecordArrivesAndDiffers_TheAgentMovesToIt_AndIsOmittedAtTheResolvedRoom()
        {
            using var rig = new Rig();
            rig.Place(A, ParcelOwn, OwnChannelRoom);
            await rig.Tick();
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A), Is.True, "armed where its parcel put it");

            rig.Records[A] = RecordedRoom;   // the viewer's provision lands
            rig.Auth.RequestArm(A);
            await rig.Tick();

            Assert.That(rig.Auth.IsArmed(RecordedRoom, A), Is.True, "armed at the recorded room");
            Assert.That(rig.Rooms(), Does.Contain(RecordedRoom));

            OSDMap hb = rig.Heartbeat();
            Assert.That(ListenersOf(hb, RecordedRoom), Is.EquivalentTo(new[] { A.ToString() }));
            Assert.That(ListenersOf(hb, OwnChannelRoom), Is.Empty,
                "and named nowhere at the room it was resolved to before the record existed");
        }

        // ---- O4: nowhere to put it ---------------------------------------------------------------

        /// <summary>O4: no record and no parcel - the sim genuinely cannot place this agent. It is left out of the
        /// batch and the heartbeat, counted on the sink's counters and the sender's own, and nothing is allocated for
        /// any room on its behalf. Before 0.8c2 this case silently became an estate-room write.</summary>
        [Test]
        public async Task O4_AnAgentTheSimCannotPlace_IsOmittedAndCounted_AndAllocatesNothing()
        {
            using var rig = new Rig();
            rig.World.Agents.Add(new AgentView(A, false, Vector3.Zero, ParcelOwn, false));   // placed nowhere

            await rig.Tick();

            Assert.That(rig.Batches(), Is.Empty, "nothing addressed, so nothing on the wire");
            Assert.That(rig.Sender.Unplaced, Is.EqualTo(1), "the sender counts what it could not place");
            Assert.That(rig.Auth.CurrentGeneration(EstateRoom), Is.Zero);
            Assert.That(rig.Auth.CurrentGeneration(OwnChannelRoom), Is.Zero);
            Assert.That(rig.Auth.IsArmed(EstateRoom, A), Is.False);
            Assert.That(((OSDMap)rig.Heartbeat()["rooms"]).Count, Is.Zero, "and named in no room's entry");

            // It is picked up the moment either source answers - nothing latches.
            rig.ParcelRooms[A] = OwnChannelRoom;
            await rig.Tick();
            Assert.That(rig.Auth.IsArmed(OwnChannelRoom, A), Is.True);
            Assert.That(rig.Sender.Unplaced, Is.Zero);
        }

        // ---- O5: what the operator reads ---------------------------------------------------------

        /// <summary>O5: "show voice visibility" must report the rooms the last send RESOLVED and how many agents it
        /// could not place. The old line printed "fallback room N" first, which read as an address and, during the 0.8
        /// soak, is exactly what made a wrong room look like a working fallback.</summary>
        [Test]
        public async Task O5_TheConsoleReader_NamesTheResolvedRooms_AndCountsTheUnplaced()
        {
            using var rig = new Rig();
            rig.Place(A, ParcelOwn, OwnChannelRoom);
            rig.World.Agents.Add(new AgentView(B, false, Vector3.Zero, ParcelOwn, false));   // unplaceable
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
                "B could not be placed anywhere, and the count of that is the headline, not a room number");
            Assert.That(all, Does.Contain("unplaced in the send"), "the counters are named for what they now mean");
            Assert.That(all, Does.Not.Contain("fallback"), "nothing here calls an omission a fallback any more");
            Assert.That(rig.Sink.LastSendRoomNumbers, Is.EqualTo(new[] { OwnChannelRoom }));
        }
    }
}
