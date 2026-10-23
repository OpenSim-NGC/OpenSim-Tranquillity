/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A connector's room is resolved the way an avatar's is,
 * the sim makes sure it exists, and an unknown_room retry backs off.
 *
 * The backoff test lives here because it measures the defect directly: a listener whose re-arm
 * request never clears would be re-armed EVERY tick, so a room that stays unknown would be hammered at tick rate. The other
 * cases live in ConnectorRoomResolverTests / the endpoint tests; this file is the authority-level behaviour.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class ConnectorRoomTests
    {
        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            return new UUID(b, 0);
        }

        private static readonly UUID L = Id(1);
        private static readonly UUID L2 = Id(2);   // a second listener in the same room
        private const int Room = 226001844;
        private const string Epoch = "0000018f00000001";

        /// A one-agent world, so the matrix population is exactly the connector NPC.
        private sealed class OneAgentWorld : IFeederWorld
        {
            private readonly UUID _id;
            public OneAgentWorld(UUID id) { _id = id; }
            public IReadOnlyList<AgentView> SnapshotAgents()
                => new List<AgentView> { new AgentView(_id, false, Vector3.Zero, Id(200), false) };
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(Id(200));
            public ParcelView GetParcelByGlobalId(UUID id)
                => new ParcelView(Id(200), true, true, _ => false, _ => false, _ => false);
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        /// A world whose population is exactly the ids given, all on one parcel.
        private sealed class ManyAgentWorld : IFeederWorld
        {
            private readonly UUID[] _ids;
            public ManyAgentWorld(params UUID[] ids) { _ids = ids; }
            public IReadOnlyList<AgentView> SnapshotAgents()
            {
                var list = new List<AgentView>();
                foreach (UUID id in _ids)
                    list.Add(new AgentView(id, false, Vector3.Zero, Id(200), false));
                return list;
            }
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(Id(200));
            public ParcelView GetParcelByGlobalId(UUID id)
                => new ParcelView(Id(200), true, true, _ => false, _ => false, _ => false);
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

        /// A room the mixer never knows must not be retried at tick rate. The REAL sender and authority, 120
        /// simulated seconds at a tick every 250 ms, every batch answered unknown_room: at most 10 attempts.
        [Test]
        public async Task T4_ARoomThatStaysUnknown_DrawsAtMostTenAttemptsInTwoMinutes()
        {
            long clock = 10_000;
            int attempts = 0;
            Task<(AdminSendResult, string)> Send(OSDMap request)
            {
                if (request["request"].AsString() == "peer_ctl_batch")
                    attempts++;
                string reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"error\",\"reason\":\"unknown_room\"," +
                    "\"status\":\"unknown_room\",\"vis_protocol\":2,\"mixer_instance\":\"m1\"}}";
                return Task.FromResult((AdminSendResult.Ok, reply));
            }
            var sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), Id(77), "backoff",
                sendOne: Send);
            var auth = new VisAuthority(0x0000018f00000001UL, "backoff", () => clock);
            sink.Authority = auth;
            sink.RoomOf = _ => Room;
            var feed = new Feed(new OneAgentWorld(L));
            var sender = new VisibilityBatchSender(feed, sink, true, TimeSpan.FromSeconds(5), "backoff", () => clock,
                auth, _ => Room);
            auth.RequestArm(L);   // the connector's registration, or any provision: one standing re-arm request

            for (int tick = 0; tick < 480; tick++)   // 480 ticks x 250 ms = 120 s
            {
                clock += 250;
                await sender.PumpAsync(feed.Tick());
            }

            TestContext.Out.WriteLine($"attempts in 120 s: {attempts}");
            Assert.That(attempts, Is.LessThanOrEqualTo(10),
                "a room that stays unknown for 120 s draws at most 10 attempts (1 s doubling; capped at 300 s)");
            Assert.That(attempts, Is.GreaterThanOrEqualTo(5), "and it does keep trying");
        }

        /// The backoff resets once the room answers: the next failure starts at 1 s again, not at the cap.
        [Test]
        public void T4b_TheBackoffResetsOnSuccess()
        {
            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "backoff", () => clock);
            var named = new List<UUID> { L };
            for (int i = 0; i < 5; i++)   // climb the backoff
            {
                auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), named, true,
                    new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
                clock += 60_000;
            }
            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), named, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "applied" });
            Assert.That(auth.IsArmed(Room, L), Is.True, "the room answered: armed");

            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), named, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
            Assert.That(auth.CanArmNow(L), Is.False, "backing off again");
            clock += 1_100;
            Assert.That(auth.CanArmNow(L), Is.True, "from 1 s again, not from the cap it had climbed to");
        }

        /// Pinned: a standing re-arm request must not bypass the backoff. Otherwise the mixer logs
        /// unknown_room several times a second for one connector: the NPC never leaves the population, so its request
        /// never expires, and the arming pass would short-circuit on it every tick.
        [Test]
        public void T4c_AStandingRearmRequest_DoesNotBypassTheBackoff()
        {
            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "backoff", () => clock);
            auth.RequestArm(L);
            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), new List<UUID> { L }, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });

            Assert.That(auth.IsRearmRequested(L), Is.True, "the request is still outstanding");
            Assert.That(auth.CanArmNow(L), Is.False, "the authority's own backoff was always right");
            // The sender's arming condition is what bypassed it. This is that condition, as VisibilityBatchSender
            // evaluates it: with the fix nothing arms while the backoff holds, standing request or not.
            bool snapshot = false;
            bool wouldArm = (snapshot || auth.IsRearmRequested(L) || !auth.IsArmed(Room, L)) && auth.CanArmNow(L);
            Assert.That(wouldArm, Is.False,
                "and the sender does not arm through it: a re-arm request is not a licence to retry every tick");
        }

        // ---- proof that the room exists releases the backoff -------------

        /// <summary>The backoff makes a room that stays unknown cheap; releasing it on proof makes a room that STARTS EXISTING fast. A
        /// listener that has climbed its backoff (to 128 s here; the cap is 300 s) must not sit out the rest of that delay once the sim has proof
        /// the room is there. Two kinds of proof, both run here:
        ///   (i)  an ensure succeeded at a connector's capability fetch  - VoiceConnectorModule.cs:227;
        ///   (ii) a viewer was provisioned into the room                 - VoiceVisibilityService.cs:207.
        /// Both call VisAuthority.RoomExists, and every listener the room's absence was holding arms on the VERY NEXT
        /// tick, not minutes later. TWO listeners are in the room deliberately: RequestArm has always cleared the retry
        /// for the agent it names, so a one-listener provision case would pass without the release at all. What is tested
        /// here is that the room existing releases the OTHERS - the listener nobody provisioned.</summary>
        [TestCase(false, TestName = "C1_EnsureSucceeded_ArmsOnTheVeryNextTick")]
        [TestCase(true, TestName = "C1_ViewerProvisioned_ArmsOnTheVeryNextTick")]
        public async Task C1_ProofThatTheRoomExists_ReleasesTheBackoff_AndArmsOnTheVeryNextTick(bool viaProvision)
        {
            long clock = 10_000;
            int attempts = 0;
            bool unknown = true;
            Task<(AdminSendResult, string)> Send(OSDMap request)
            {
                if (request["request"].AsString() == "peer_ctl_batch")
                    attempts++;
                string reply = unknown
                    ? "{\"janus\":\"success\",\"response\":{\"slvoice\":\"error\",\"reason\":\"unknown_room\"," +
                      "\"status\":\"unknown_room\",\"vis_protocol\":2,\"mixer_instance\":\"m1\"}}"
                    : "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"vis_protocol\":2," +
                      "\"mixer_instance\":\"m1\",\"room\":" + request["room"].AsInteger() + "}}";
                return Task.FromResult((AdminSendResult.Ok, reply));
            }
            using var sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), Id(77), "reset",
                sendOne: Send);
            var auth = new VisAuthority(0x0000018f00000001UL, "reset", () => clock);
            sink.Authority = auth;
            sink.RoomOf = _ => Room;
            var feed = new Feed(new ManyAgentWorld(L, L2));
            var sender = new VisibilityBatchSender(feed, sink, true, TimeSpan.FromSeconds(5), "reset", () => clock,
                auth, _ => Room);

            for (int tick = 0; tick < 480; tick++)   // 120 s of a room the mixer does not have: climb to the cap
            {
                clock += 250;
                await sender.PumpAsync(feed.Tick());
            }
            clock += 31_000;                         // let the capped delay expire, so the next pump really tries
            await sender.PumpAsync(feed.Tick());
            int attemptsAtCap = attempts;
            Assert.That(auth.CanArmNow(L), Is.False, "that attempt failed too, so it is waiting again");
            clock += 25_000;
            Assert.That(auth.CanArmNow(L), Is.False,
                "and still waiting 25 s later: the delay has climbed far past the 1 s it started at");

            // The room now exists, and the sim learns it the way the two live paths learn it.
            unknown = false;
            auth.RoomExists(Room);
            if (viaProvision)
                sender.OnListenerProvisioned(L);   // what VoiceVisibilityService does after RoomExists

            Assert.That(auth.CanArmNow(L), Is.True, "the proof released the backoff there and then (ruling C)");
            Assert.That(auth.CanArmNow(L2), Is.True,
                "including for the listener nobody named: it is the ROOM that was proved to exist, not one agent's "
                + "place in it");

            clock += 250;                          // ONE tick later, not minutes
            await sender.PumpAsync(feed.Tick());

            Assert.That(attempts, Is.EqualTo(attemptsAtCap + 1), "it armed on the very next tick");
            Assert.That(auth.IsArmed(Room, L) && auth.IsArmed(Room, L2), Is.True, "and the room took them both");
        }

        /// <summary>The heartbeat half of connector room resolution: the connector's parcel changes its voice channel, so the
        /// resolver answers with a new room. The next heartbeat must name the NPC at the NEW room and stop naming it
        /// at the old one. A heartbeat that kept listing it at the old room would be claiming authority over a room
        /// the sim no longer places it in - the mixer reconciles against that list, so the claim is not cosmetic.</summary>
        [Test]
        public void C2_WhenTheConnectorsParcelChannelChanges_TheHeartbeatFollowsIt()
        {
            long clock = 10_000;
            const int NewRoom = 1966197062;
            int current = Room;
            var auth = new VisAuthority(0x0000018f00000001UL, "move", () => clock);
            var population = new List<UUID> { L };
            Func<UUID, int?> resolve = _ => current;

            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), population, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "applied" });
            Assert.That(auth.IsArmed(Room, L), Is.True);
            Assert.That(Listeners(auth.BuildHeartbeat(population, resolve, false), Room),
                Is.EquivalentTo(new[] { L.ToString() }), "named at the room it was armed in");

            current = NewRoom;   // the parcel now runs its own channel
            auth.PruneTo(population, resolve);
            OSDMap hb = auth.BuildHeartbeat(population, resolve, false);

            Assert.That(Listeners(hb, NewRoom), Is.EquivalentTo(new[] { L.ToString() }), "named at the new room");
            Assert.That(((OSDMap)hb["rooms"]).ContainsKey(Room.ToString()), Is.False,
                "and the old room is not in the heartbeat at all");
            Assert.That(auth.IsArmed(Room, L), Is.False, "the claim on the old room is dropped, not left standing");
        }

        // ---- a 300 s cap, and proof still releases it at once --------------------------

        /// <summary>The real sender and authority against a room the mixer answers unknown_room for, at a 250 ms tick.
        /// Returns the number of batch attempts; <paramref name="proveAtMs"/> (if given) is the simulated time at which
        /// the room starts existing and RoomExists fires, after which the test checks the very next tick.</summary>
        private static async Task<(int Attempts, bool ArmedNextTick, int AttemptsAtProof)> RunUnknownRoom(long spanMs,
            long? proveAtMs = null)
        {
            long clock = 10_000, start = clock;
            int attempts = 0;
            bool unknown = true;
            Task<(AdminSendResult, string)> Send(OSDMap request)
            {
                if (request["request"].AsString() == "peer_ctl_batch")
                    attempts++;
                string reply = unknown
                    ? "{\"janus\":\"success\",\"response\":{\"slvoice\":\"error\",\"reason\":\"unknown_room\"," +
                      "\"status\":\"unknown_room\",\"vis_protocol\":2,\"mixer_instance\":\"m1\"}}"
                    : "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"vis_protocol\":2," +
                      "\"mixer_instance\":\"m1\",\"room\":" + request["room"].AsInteger() + "}}";
                return Task.FromResult((AdminSendResult.Ok, reply));
            }
            using var sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), Id(77), "h7",
                sendOne: Send);
            var auth = new VisAuthority(0x0000018f00000001UL, "h7", () => clock);
            sink.Authority = auth;
            sink.RoomOf = _ => Room;
            var feed = new Feed(new OneAgentWorld(L));
            var sender = new VisibilityBatchSender(feed, sink, true, TimeSpan.FromSeconds(5), "h7", () => clock,
                auth, _ => Room);
            auth.RequestArm(L);

            while (clock - start < spanMs)
            {
                if (proveAtMs.HasValue && clock - start >= proveAtMs.Value)
                {
                    unknown = false;
                    auth.RoomExists(Room);
                    int before = attempts;
                    clock += 250;
                    await sender.PumpAsync(feed.Tick());
                    return (attempts, attempts == before + 1 && auth.IsArmed(Room, L), before);
                }
                clock += 250;
                await sender.PumpAsync(feed.Tick());
            }
            return (attempts, false, attempts);
        }

        /// <summary>A room the mixer never has, for a simulated HOUR, draws at most 20 attempts: 1 s doubling to a
        /// 300 s cap is attempts at 0, 1, 3, 7 ... 511 s, then one every 300 s. A shorter 30 s cap would draw one
        /// attempt per room every 30 s, flooding the log overnight.</summary>
        [Test]
        public async Task H7_ARoomUnknownForAnHour_DrawsAtMostTwentyAttempts()
        {
            var r = await RunUnknownRoom(3_600_000);
            TestContext.Out.WriteLine($"attempts in one simulated hour: {r.Attempts}");
            Assert.That(r.Attempts, Is.LessThanOrEqualTo(20), "at most 20 in an hour (300 s cap)");
            Assert.That(r.Attempts, Is.GreaterThanOrEqualTo(15), "and it does keep trying");
        }

        /// <summary>H7, second half: however far the backoff has climbed, a RoomExists arms on the very next tick - so a
        /// longer cap costs a proven room no latency.</summary>
        [TestCase(10_000L, TestName = "H7_RoomExistsAfter10s_ArmsOnTheNextTick")]
        [TestCase(700_000L, TestName = "H7_RoomExistsAfter700s_ArmsOnTheNextTick")]
        [TestCase(3_500_000L, TestName = "H7_RoomExistsAfterAnHour_ArmsOnTheNextTick")]
        public async Task H7_RoomExistsAtAnyPoint_ArmsOnTheNextTick(long proveAtMs)
        {
            var r = await RunUnknownRoom(3_600_000, proveAtMs);
            Assert.That(r.ArmedNextTick, Is.True, $"armed on the tick after RoomExists at {proveAtMs / 1000} s " +
                $"({r.AttemptsAtProof} attempts before it)");
        }

        private static HashSet<string> Listeners(OSDMap heartbeat, int room)
        {
            var rooms = (OSDMap)heartbeat["rooms"];
            return rooms.ContainsKey(room.ToString())
                ? new HashSet<string>(((OSDMap)((OSDMap)rooms[room.ToString()])["listeners"]).Keys)
                : new HashSet<string>();
        }
    }
}
