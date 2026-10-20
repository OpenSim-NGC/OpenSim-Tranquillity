/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for arming, epochs, heartbeat and acting on the mixer's reply,
 * with [WebRtcVoice] VisibilityArmingEnabled ON. The knob-OFF path is proven byte-identical by
 * VisibilityKnobOffGoldenTests.
 *
 * Everything runs through the real VisibilityBatchSender, JanusPeerCtlBatchSink and VisAuthority. A scripted transport
 * stands in for the Janus admin call and answers each request the way a mixer (or an old one) would. A
 * controllable clock drives the heartbeat cadence and the unknown-room backoff.
 *
 * Covered: every listener armed including empty columns; a new listener armed; delta base and generations; a new epoch
 * on feeder restart; heartbeat gating, 1 s cadence, body shape, independence from a batch in flight, and the stopping
 * heartbeat; each row of the reply decision table.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisibilityArmingTests
    {
        private const int RoomAB = 1001;
        private const int RoomD = 1002;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        private static readonly UUID A = Id(1), B = Id(2), D = Id(4), E = Id(5);
        private static readonly UUID ParcelP = Id(201), ParcelQ = Id(202);

        // ---- world, feed, transport ------------------------------------------------------------

        private sealed class World : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public bool BanAOnP = true;
            public IReadOnlyList<AgentView> SnapshotAgents() => Agents.ToList();
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(ParcelQ);
            public ParcelView GetParcelByGlobalId(UUID id) => new ParcelView(id, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: x => BanAOnP && id == ParcelP && x == A, isRestrictedFromLand: _ => false);
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        private sealed class MatrixFeed : IVisibilityFeed
        {
            public VisibilityMatrix Current { get; private set; } = VisibilityMatrix.Empty;
#pragma warning disable CS0067
            public event Action<VisibilityBatch> BatchProduced;
#pragma warning restore CS0067
            public VisibilityBatch SnapshotFor(UUID listener) => DeltaComputer.SnapshotFor(Current, listener, -999);

            public VisibilityBatch Tick(IFeederWorld world)
            {
                VisibilityMatrix next = VisibilityMatrix.Build(world);
                VisibilityBatch batch = DeltaComputer.Diff(Current, next, -999);
                Current = next;
                return batch;
            }
        }

        private sealed class Transport
        {
            private readonly object _lock = new object();
            private readonly List<OSDMap> _sent = new();
            /// <summary>The inner "response" for a request; null means the default applied / heartbeat-ok reply.</summary>
            public Func<OSDMap, OSDMap> Response = _ => null;
            public Func<OSDMap, AdminSendResult> Result = _ => AdminSendResult.Ok;
            public TaskCompletionSource<bool> HoldBatches;

            public async Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                OSDMap copy = (OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(request));
                lock (_lock)
                    _sent.Add(copy);
                if (copy["request"].AsString() == "peer_ctl_batch" && HoldBatches != null)
                    await HoldBatches.Task.ConfigureAwait(false);
                OSDMap response = Response(copy) ?? DefaultResponse(copy);
                var top = new OSDMap { ["janus"] = OSD.FromString("success"), ["response"] = response };
                return (Result(copy), OSDParser.SerializeJsonString(top));
            }

            private static OSDMap DefaultResponse(OSDMap request)
                => request["request"].AsString() == "peer_ctl_heartbeat"
                    ? HeartbeatResponse("m1")
                    : new OSDMap { ["slvoice"] = OSD.FromString("applied"), ["room"] = request["room"] };

            public List<OSDMap> Batches() { lock (_lock) return _sent.Where(m => m["request"].AsString() == "peer_ctl_batch").ToList(); }
            public List<OSDMap> Heartbeats() { lock (_lock) return _sent.Where(m => m["request"].AsString() == "peer_ctl_heartbeat").ToList(); }
            public void Clear() { lock (_lock) _sent.Clear(); }
        }

        private static OSDMap Applied(OSDMap request, int visProtocol = 0, string instance = null,
            UUID[] stale = null, UUID[] unarmed = null)
        {
            var r = new OSDMap { ["slvoice"] = OSD.FromString("applied"), ["room"] = request["room"] };
            if (visProtocol > 0) r["vis_protocol"] = OSD.FromInteger(visProtocol);
            if (instance != null) r["mixer_instance"] = OSD.FromString(instance);
            if (stale != null) r["stale_listeners"] = Uuids(stale);
            if (unarmed != null) r["unarmed_listeners"] = Uuids(unarmed);
            return r;
        }

        private static OSDMap HeartbeatResponse(string instance, params (int room, string status, UUID[] unarmed, UUID[] stale)[] rooms)
        {
            var roomMap = new OSDMap();
            foreach (var (room, status, unarmed, stale) in rooms)
            {
                var entry = new OSDMap { ["status"] = OSD.FromString(status ?? "ok") };
                if (unarmed != null) entry["unarmed_listeners"] = Uuids(unarmed);
                if (stale != null) entry["stale_listeners"] = Uuids(stale);
                roomMap[room.ToString()] = entry;
            }
            return new OSDMap
            {
                ["slvoice"] = OSD.FromString("heartbeat"),
                ["vis_protocol"] = OSD.FromInteger(2),
                ["mixer_instance"] = OSD.FromString(instance),
                ["rooms"] = roomMap,
            };
        }

        private static OSDArray Uuids(IEnumerable<UUID> ids)
        {
            var a = new OSDArray();
            foreach (UUID id in ids) a.Add(OSD.FromString(id.ToString()));
            return a;
        }

        private sealed class Rig
        {
            public long Clock = 10_000;
            public readonly World World = new();
            public readonly Dictionary<UUID, int> Rooms = new() { [A] = RoomAB, [B] = RoomAB, [D] = RoomD, [E] = RoomD };
            public readonly Transport T = new();
            public readonly MatrixFeed Feed = new();
            public readonly VisAuthority Auth;
            public readonly JanusPeerCtlBatchSink Sink;
            public readonly VisibilityBatchSender Sender;

            public Rig(ulong? epoch = null)
            {
                World.Agents.Add(new AgentView(A, false, Vector3.Zero, ParcelQ, false));   // A on Q, banned from P
                World.Agents.Add(new AgentView(B, false, Vector3.Zero, ParcelP, false));   // B on P
                World.Agents.Add(new AgentView(D, false, Vector3.Zero, ParcelQ, false));   // D on Q: empty columns
                Sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), Id(77), "arming",
                    sendOne: T.SendAsync);
                // 0.8c2: as the service composes it - record, else the agent's parcel room (estate here), never a
                // default applied merely because there is no record.
                Sink.RoomOf = a => Rooms.TryGetValue(a, out int r) ? r : (int?)Sink.FallbackRoom;
                Auth = new VisAuthority(epoch ?? VisAuthority.NewEpoch(), "arming", () => Clock);
                Sink.Authority = Auth;
                Sender = new VisibilityBatchSender(Feed, Sink, true, TimeSpan.FromSeconds(5), "arming", () => Clock,
                    // 0.8c2: the rig's resolver answers with the room a record names, else the room this test's
                    // world resolves for that agent - the estate/fallback number here, which is what an
                    // estate-channel parcel resolves to, so these payloads are unchanged.
                    Auth, a => Rooms.TryGetValue(a, out int r) ? r : (int?)Sink.FallbackRoom);
            }

            public async Task Tick(long advanceMs = 250)
            {
                Clock += advanceMs;
                await Sender.PumpAsync(Feed.Tick(World));
            }

            public Task Heartbeat() => Sender.PumpHeartbeatAsync();

            public void Advertise(string instance = "m1")
                => T.Response = r => r["request"].AsString() == "peer_ctl_batch" ? Applied(r, 2, instance) : null;
        }

        private static OSDMap ForRoom(IEnumerable<OSDMap> batches, int room) => batches.Single(b => b["room"].AsInteger() == room);

        private static HashSet<string> Keys(OSD channel) => new HashSet<string>(((OSDMap)channel).Keys);

        /// <summary>Walk nested OSDMaps by key (OSD itself has no string indexer).</summary>
        private static OSD Path(OSD root, params string[] keys)
        {
            foreach (string key in keys)
                root = ((OSDMap)root)[key];
            return root;
        }

        // ---- arming --------------------------------------------------------------------------------

        [Test]
        public async Task FirstTick_ArmsEveryListener_IncludingAnEmptyColumn()
        {
            var rig = new Rig();
            await rig.Tick();

            List<OSDMap> sent = rig.T.Batches();
            Assert.That(sent.Select(b => b["room"].AsInteger()).OrderBy(x => x), Is.EqualTo(new[] { RoomAB, RoomD }));
            OSDMap ab = ForRoom(sent, RoomAB), d = ForRoom(sent, RoomD);
            Assert.That(ab["op"].AsString(), Is.EqualTo("replace"));
            Assert.That(Keys(ab["excl"]), Is.EquivalentTo(new[] { A.ToString(), B.ToString() }));
            Assert.That(Keys(ab["mute"]), Is.EquivalentTo(new[] { A.ToString(), B.ToString() }), "the mute column is armed too, empty");
            Assert.That(d["op"].AsString(), Is.EqualTo("replace"));
            Assert.That(((OSDArray)Path(d, "excl", D.ToString())).Count, Is.EqualTo(0), "D's empty exclusion column is armed");
            Assert.That(((OSDArray)Path(d, "mute", D.ToString())).Count, Is.EqualTo(0), "D's empty mute column is armed");
            foreach (OSDMap b in sent)
            {
                Assert.That(b["room_epoch"].AsString(), Is.EqualTo(rig.Auth.EpochString));
                Assert.That(b["policy_generation"].AsInteger(), Is.EqualTo(1));
                Assert.That(b.ContainsKey("base"), Is.False, "replace is absolute: no base");
            }
            Assert.That(rig.Auth.IsArmed(RoomAB, A) && rig.Auth.IsArmed(RoomAB, B) && rig.Auth.IsArmed(RoomD, D), Is.True);

            rig.T.Clear();
            await rig.Tick();
            Assert.That(rig.T.Batches(), Is.Empty, "a quiet tick after arming sends nothing");
        }

        [Test]
        public async Task ANewListenerWithEmptyColumns_IsArmedOnTheNextTick()
        {
            var rig = new Rig();
            await rig.Tick();
            rig.T.Clear();
            rig.World.Agents.Add(new AgentView(E, false, Vector3.Zero, ParcelQ, false));
            await rig.Tick();

            OSDMap only = rig.T.Batches().Single();
            Assert.That((only["room"].AsInteger(), only["op"].AsString()), Is.EqualTo((RoomD, "replace")));
            Assert.That(Keys(only["excl"]), Is.EquivalentTo(new[] { E.ToString() }));
            Assert.That(only["policy_generation"].AsInteger(), Is.EqualTo(2), "the room's second batch");
            Assert.That(rig.Auth.ListenerGeneration(RoomD, E), Is.EqualTo(2u));
        }

        [Test]
        public async Task ADelta_CarriesBaseGenerations_AndAdvancesThem()
        {
            var rig = new Rig();
            await rig.Tick();
            rig.T.Clear();
            rig.World.BanAOnP = false;
            await rig.Tick();

            OSDMap remove = rig.T.Batches().Single();
            Assert.That((remove["op"].AsString(), remove["room"].AsInteger()), Is.EqualTo(("remove", RoomAB)));
            Assert.That(remove["policy_generation"].AsInteger(), Is.EqualTo(2));
            Assert.That(Path(remove, "base", A.ToString()).AsInteger(), Is.EqualTo(1));
            Assert.That(Path(remove, "base", B.ToString()).AsInteger(), Is.EqualTo(1));
            Assert.That(rig.Auth.ListenerGeneration(RoomAB, A), Is.EqualTo(2u));
            Assert.That(rig.Auth.ListenerGeneration(RoomD, D), Is.EqualTo(1u), "D was not named: unchanged");
        }

        [Test]
        public async Task ProvisioningAnArmedListener_ArmsItAgain()
        {
            var rig = new Rig();
            await rig.Tick();
            rig.T.Clear();
            rig.Sender.OnListenerProvisioned(D);
            await rig.Tick();
            OSDMap only = rig.T.Batches().Single();
            Assert.That((only["op"].AsString(), only["room"].AsInteger()), Is.EqualTo(("replace", RoomD)));
            Assert.That(rig.Auth.ListenerGeneration(RoomD, D), Is.EqualTo(2u));
        }

        // ---- epochs ----------------------------------------------------------------------------------

        [Test]
        public void NewEpoch_IsSixteenHexDigits_AndStrictlyIncreasing()
        {
            ulong first = VisAuthority.NewEpoch(1_789_000_000_000, 65535);
            ulong second = VisAuthority.NewEpoch(1_789_000_000_000, 0);   // same millisecond, lower random bits
            Assert.That(second, Is.GreaterThan(first));
            Assert.That(Regex.IsMatch(VisAuthority.FormatEpoch(first), "^[0-9a-f]{16}$"), Is.True);
            Assert.That(VisAuthority.NewEpoch(), Is.GreaterThan(second));
        }

        [Test]
        public async Task ARestartedFeeder_SendsANewGreaterEpoch_AndGenerationsRestart()
        {
            var before = new Rig();
            await before.Tick();
            await before.Tick();
            string oldEpoch = before.T.Batches().First()["room_epoch"].AsString();

            var after = new Rig();   // the service builds a new authority on every StartLoop
            await after.Tick();
            OSDMap first = after.T.Batches().First();
            Assert.That(first["room_epoch"].AsString(), Is.Not.EqualTo(oldEpoch));
            Assert.That(Convert.ToUInt64(first["room_epoch"].AsString(), 16), Is.GreaterThan(Convert.ToUInt64(oldEpoch, 16)));
            Assert.That(first["policy_generation"].AsInteger(), Is.EqualTo(1));
        }

        // ---- heartbeat -------------------------------------------------------------------------------

        [Test]
        public async Task NoHeartbeat_UntilTheMixerAdvertisesVisProtocol2()
        {
            var rig = new Rig();
            await rig.Tick();   // an old mixer: applied replies without vis_protocol
            rig.Clock += 5000;
            await rig.Heartbeat();
            Assert.That(rig.T.Heartbeats(), Is.Empty);
            Assert.That(rig.Auth.HeartbeatCapable, Is.False);
        }

        [Test]
        public async Task Heartbeat_EverySecond_ListingEveryListenerWithItsGeneration()
        {
            var rig = new Rig();
            rig.Advertise();
            await rig.Tick();
            Assert.That(rig.Auth.HeartbeatCapable, Is.True);

            await rig.Heartbeat();
            Assert.That(rig.T.Heartbeats().Count, Is.EqualTo(1));
            rig.Clock += 1000;
            await rig.Heartbeat();
            Assert.That(rig.T.Heartbeats().Count, Is.EqualTo(2), "the caller's next beat sends");

            OSDMap hb = rig.T.Heartbeats()[1];
            Assert.That(hb["room_epoch"].AsString(), Is.EqualTo(rig.Auth.EpochString));
            Assert.That(hb["interval_ms"].AsInteger(), Is.EqualTo(1000));
            Assert.That(hb.ContainsKey("state"), Is.False);
            OSDMap rooms = (OSDMap)hb["rooms"];
            Assert.That(rooms.Keys, Is.EquivalentTo(new[] { RoomAB.ToString(), RoomD.ToString() }));
            Assert.That(Path(rooms, RoomAB.ToString(), "policy_generation").AsInteger(), Is.EqualTo(1));
            Assert.That(Path(rooms, RoomAB.ToString(), "listeners", A.ToString()).AsInteger(), Is.EqualTo(1));
            Assert.That(Path(rooms, RoomAB.ToString(), "listeners", B.ToString()).AsInteger(), Is.EqualTo(1));
            Assert.That(Path(rooms, RoomD.ToString(), "listeners", D.ToString()).AsInteger(), Is.EqualTo(1), "the empty-column listener is listed");
        }

        /// The CALLER owns the cadence. The sender does not throttle itself to HeartbeatIntervalMs measured
        /// from the previous send's start. VoiceVisibilityService's timer calls it once per interval, so a throttle could only subtract beats:
        /// a timer callback landing a hair under the interval was refused, and the next arrived a whole period
        /// later. A throttled sender measured 0.88 heartbeats/s against an intended 1.0, worst gap 1987 ms
        /// -- one skipped beat. Two owners of one cadence is the same fault the feeder must avoid.
        [Test]
        public async Task Heartbeat_DoesNotThrottleItself_TheCallerOwnsTheCadence()
        {
            var rig = new Rig();
            rig.Advertise();
            await rig.Tick();

            await rig.Heartbeat();
            Assert.That(rig.T.Heartbeats().Count, Is.EqualTo(1));

            // Four calls inside one interval: with the old gate this sent nothing, because each was "too soon".
            for (int i = 0; i < 4; i++)
            {
                rig.Clock += 100;
                await rig.Heartbeat();
            }
            Assert.That(rig.T.Heartbeats().Count, Is.EqualTo(5),
                "every call sends: how often to beat is the timer's business, not this method's");

            // And the clock not moving at all must not stop it either - that was the exact shape of the drift,
            // a callback arriving fractionally early being dropped entirely.
            await rig.Heartbeat();
            Assert.That(rig.T.Heartbeats().Count, Is.EqualTo(6), "a beat with no elapsed time still sends");
        }

        /// The as_of field is the highest generation the mixer APPLIED in the room when the heartbeat was built,
        /// not the highest allocated. A batch stamped but not yet answered, a transport failure, and a stale_epoch refusal
        /// all leave it where it was.
        [Test]
        public void Heartbeat_AsOf_IsTheHighestAppliedGeneration_NotTheHighestAllocated()
        {
            var auth = new VisAuthority(VisAuthority.NewEpoch(), "asof", () => 10_000);
            var named = new List<UUID> { A };
            var population = new[] { A };
            Func<UUID, int?> room = _ => RoomAB;   // 0.8c2: the resolver is nullable (unplaced = null)
            int AsOf() => Path(auth.BuildHeartbeat(population, room, false), "rooms", RoomAB.ToString(), "as_of").AsInteger();
            int Allocated() => Path(auth.BuildHeartbeat(population, room, false), "rooms", RoomAB.ToString(), "policy_generation").AsInteger();

            Assert.That(AsOf(), Is.EqualTo(0), "nothing applied yet");
            uint g1 = auth.NextGeneration(RoomAB);
            Assert.That((Allocated(), AsOf()), Is.EqualTo((1, 0)), "stamped, still in flight: allocated 1, applied 0");
            auth.OnBatchOutcome(RoomAB, VisOp.Replace, g1, named, true, default);
            Assert.That(AsOf(), Is.EqualTo(1), "applied: as_of 1");

            uint g2 = auth.NextGeneration(RoomAB);
            auth.OnBatchOutcome(RoomAB, VisOp.Replace, g2, named, false, default);
            Assert.That((Allocated(), AsOf()), Is.EqualTo((2, 1)), "a transport failure applied nothing");
            uint g3 = auth.NextGeneration(RoomAB);
            auth.OnBatchOutcome(RoomAB, VisOp.Replace, g3, named, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", StatusField = "stale_epoch" });
            Assert.That(AsOf(), Is.EqualTo(1), "a stale_epoch refusal applied nothing");
            uint g4 = auth.NextGeneration(RoomAB);
            auth.OnBatchOutcome(RoomAB, VisOp.Replace, g4, named, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "applied" });
            Assert.That((Allocated(), AsOf()), Is.EqualTo((4, 4)));
            auth.OnBatchOutcome(RoomAB, VisOp.Replace, g2, named, true, default);   // a late answer for an older batch
            Assert.That(AsOf(), Is.EqualTo(4), "as_of never goes backwards");
        }

        [Test]
        public async Task Heartbeat_IsNotStarvedByABatchInFlight()
        {
            var rig = new Rig();
            rig.Advertise();
            await rig.Tick();
            await rig.Heartbeat();

            rig.T.HoldBatches = new TaskCompletionSource<bool>();
            rig.World.BanAOnP = false;
            rig.Clock += 250;
            Task stuck = rig.Sender.PumpAsync(rig.Feed.Tick(rig.World));   // a batch now hangs in flight
            rig.Clock += 1000;
            await rig.Heartbeat();
            Assert.That(rig.T.Heartbeats().Count, Is.EqualTo(2), "the heartbeat has its own single-flight");
            rig.T.HoldBatches.SetResult(true);
            await stuck;
        }

        [Test]
        public async Task StoppingHeartbeat_SaysStopping_AtOnce()
        {
            var rig = new Rig();
            rig.Advertise();
            await rig.Tick();
            await rig.Heartbeat();
            await rig.Sender.PumpHeartbeatAsync(stopping: true);   // no interval wait
            OSDMap last = rig.T.Heartbeats().Last();
            Assert.That(last["state"].AsString(), Is.EqualTo("stopping"));
        }

        [Test]
        public async Task ADepartedListener_GetsNoEmptyReplace_AndIsOmittedFromTheHeartbeat()
        {
            var rig = new Rig();
            rig.Advertise();
            await rig.Tick();
            rig.T.Clear();
            rig.World.Agents.RemoveAll(a => a.Id == B);
            await rig.Tick();
            Assert.That(rig.T.Batches().Where(b => b["op"].AsString() == "replace"), Is.Empty,
                "an empty replace for B would ARM it");
            rig.Clock += 1000;
            await rig.Heartbeat();
            OSDMap listeners = (OSDMap)Path(rig.T.Heartbeats().Last(), "rooms", RoomAB.ToString(), "listeners");
            Assert.That(listeners.ContainsKey(B.ToString()), Is.False);
            Assert.That(listeners.ContainsKey(A.ToString()), Is.True);
        }

        // ---- the reply decision table -----------------------------------------------------------------

        [Test]
        public async Task Reply_FirstMixerInstance_IsRecorded_WithoutRearming()
        {
            var rig = new Rig();
            rig.Advertise("m1");
            await rig.Tick();
            rig.T.Clear();
            await rig.Tick();
            Assert.That(rig.Auth.MixerInstance, Is.EqualTo("m1"));
            Assert.That(rig.T.Batches(), Is.Empty);
        }

        [Test]
        public async Task Reply_MixerInstanceChanged_RearmsEveryListenerInEveryRoom()
        {
            var rig = new Rig();
            rig.Advertise("m1");
            await rig.Tick();
            await rig.Heartbeat();
            rig.T.Response = r => r["request"].AsString() == "peer_ctl_heartbeat" ? HeartbeatResponse("m2") : Applied(r, 2, "m2");
            rig.Clock += 1000;
            await rig.Heartbeat();   // the mixer restarted
            Assert.That(rig.Auth.MixerInstance, Is.EqualTo("m2"));
            Assert.That(rig.Auth.IsArmed(RoomAB, A), Is.False, "nothing is trusted after a restart");

            rig.T.Clear();
            await rig.Tick();
            List<OSDMap> sent = rig.T.Batches();
            Assert.That(sent.All(b => b["op"].AsString() == "replace"), Is.True);
            Assert.That(Keys(ForRoom(sent, RoomAB)["excl"]), Is.EquivalentTo(new[] { A.ToString(), B.ToString() }));
            Assert.That(Keys(ForRoom(sent, RoomD)["excl"]), Is.EquivalentTo(new[] { D.ToString() }));
            Assert.That(rig.Auth.IsArmed(RoomAB, A) && rig.Auth.IsArmed(RoomD, D), Is.True);
        }

        [Test]
        public async Task Reply_StaleListeners_RearmsExactlyThoseListeners()
        {
            var rig = new Rig();
            await rig.Tick();
            rig.T.Response = r => r["op"].AsString() == "remove" ? Applied(r, stale: new[] { A }) : null;
            rig.World.BanAOnP = false;
            await rig.Tick();   // the mixer says A's base did not match
            Assert.That(rig.Auth.IsArmed(RoomAB, A), Is.False);
            Assert.That(rig.Auth.IsArmed(RoomAB, B), Is.True);

            rig.T.Response = _ => null;
            rig.T.Clear();
            await rig.Tick();
            OSDMap only = rig.T.Batches().Single();
            Assert.That((only["op"].AsString(), only["room"].AsInteger()), Is.EqualTo(("replace", RoomAB)));
            Assert.That(Keys(only["excl"]), Is.EquivalentTo(new[] { A.ToString() }));
        }

        [Test]
        public async Task Reply_UnarmedListenersInAHeartbeat_RearmsExactlyThoseListeners()
        {
            var rig = new Rig();
            rig.Advertise();
            await rig.Tick();
            rig.T.Response = r => r["request"].AsString() == "peer_ctl_heartbeat"
                ? HeartbeatResponse("m1", (RoomAB, "ok", null, null), (RoomD, "ok", new[] { D }, null))
                : Applied(r, 2, "m1");
            await rig.Heartbeat();
            Assert.That(rig.Auth.IsArmed(RoomD, D), Is.False);

            rig.T.Clear();
            await rig.Tick();
            OSDMap only = rig.T.Batches().Single();
            Assert.That((only["op"].AsString(), only["room"].AsInteger()), Is.EqualTo(("replace", RoomD)));
            Assert.That(Keys(only["excl"]), Is.EquivalentTo(new[] { D.ToString() }));
        }

        /// The reply is LOST after the mixer applied the batch — the admin send times out,
        /// so the sim counts nothing applied and its as_of stays behind the generation the mixer now holds. Every
        /// heartbeat built from here on is outdated at the mixer, and would be ignored forever if nothing repaired it.
        /// The transport error makes the sender unsynced, so the next tick is a resync replace,
        /// and once that is answered as_of is at or above what the mixer applied.
        [Test]
        public async Task ALostReplyAfterTheMixerApplied_IsRepairedByTheNextTicksResyncReplace()
        {
            var rig = new Rig();
            rig.Advertise();
            int mixerApplied = 0;
            bool dropReply = true;
            // The mixer applies every batch it receives; the first room's reply never comes back. The flag is separate
            // from mixerApplied because the transport builds the response BEFORE it decides the result.
            rig.T.Result = r => r["room"].AsInteger() == RoomAB && dropReply
                ? AdminSendResult.TransportError : AdminSendResult.Ok;
            rig.T.Response = r =>
            {
                if (r["room"].AsInteger() == RoomAB)
                    mixerApplied = Math.Max(mixerApplied, r["policy_generation"].AsInteger());
                return Applied(r, 2, "m1");
            };
            await rig.Tick();

            int AsOf() => Path(rig.Auth.BuildHeartbeat(new[] { A, B }, a => RoomAB, false),
                "rooms", RoomAB.ToString(), "as_of").AsInteger();
            Assert.That(mixerApplied, Is.EqualTo(1), "the mixer applied generation 1");
            Assert.That(AsOf(), Is.EqualTo(0), "the sim never saw the reply, so it believes nothing was applied");
            Assert.That(AsOf(), Is.LessThan(mixerApplied), "every heartbeat built now is outdated at the mixer");
            Assert.That(rig.Auth.IsArmed(RoomAB, A), Is.False, "and A is not armed as far as the sim knows");

            rig.T.Clear();
            dropReply = false;
            int appliedBeforeResync = mixerApplied;
            await rig.Tick();   // the resync

            List<OSDMap> resync = rig.T.Batches();
            OSDMap ab = ForRoom(resync, RoomAB);
            Assert.That(ab["op"].AsString(), Is.EqualTo("replace"), "trigger 3: the next tick re-arms with a replace");
            Assert.That(Keys(ab["excl"]), Is.EquivalentTo(new[] { A.ToString(), B.ToString() }),
                "the whole population of that room, empty columns included");
            Assert.That(ab["policy_generation"].AsInteger(), Is.GreaterThan(appliedBeforeResync),
                "at a generation above the one the mixer already holds");
            Assert.That(AsOf(), Is.GreaterThanOrEqualTo(mixerApplied),
                "after the resync succeeds the sim's as_of is at or above what the mixer applied, so heartbeats are evaluated again");
            Assert.That(AsOf(), Is.EqualTo(ab["policy_generation"].AsInteger()));
            Assert.That(rig.Auth.IsArmed(RoomAB, A) && rig.Auth.IsArmed(RoomAB, B), Is.True);
        }

        [Test]
        public async Task Reply_UnknownRoom_BacksOff_RetriesAfterTheInterval_AndAProvisionRetriesAtOnce()
        {
            var rig = new Rig();
            rig.T.Response = r => r["room"].AsInteger() == RoomD
                ? new OSDMap { ["slvoice"] = OSD.FromString("error"), ["reason"] = OSD.FromString("unknown_room") }
                : null;
            await rig.Tick();   // t = 10250; D's room does not exist at the mixer
            Assert.That(rig.Auth.IsArmed(RoomD, D), Is.False);

            rig.T.Clear();
            await rig.Tick();   // 10500
            await rig.Tick();   // 10750
            await rig.Tick();   // 11000
            Assert.That(rig.T.Batches(), Is.Empty, "no retry storm inside the backoff");
            await rig.Tick();   // 11250: the backoff has passed
            Assert.That(rig.T.Batches().Single()["room"].AsInteger(), Is.EqualTo(RoomD));

            rig.T.Clear();
            rig.Sender.OnListenerProvisioned(D);   // the provision creates the room
            rig.T.Response = _ => null;
            await rig.Tick();   // 11500, still inside the new backoff
            Assert.That(rig.T.Batches().Single()["room"].AsInteger(), Is.EqualTo(RoomD));
            Assert.That(rig.Auth.IsArmed(RoomD, D), Is.True);
        }

        [Test]
        public async Task Reply_StaleEpoch_BacksOffLikeAnUnknownRoom()
        {
            var rig = new Rig();
            rig.T.Response = r => r["room"].AsInteger() == RoomAB ? new OSDMap
            {
                ["slvoice"] = OSD.FromString("applied"), ["status"] = OSD.FromString("stale_epoch"), ["room"] = r["room"],
            } : null;
            await rig.Tick();
            Assert.That(rig.Auth.IsArmed(RoomAB, A), Is.False);
            Assert.That(rig.Auth.IsArmed(RoomD, D), Is.True);
            rig.T.Clear();
            await rig.Tick();
            Assert.That(rig.T.Batches(), Is.Empty);
        }

        [Test]
        public async Task Reply_TransportError_ResnapshotsAndArmsEveryone()
        {
            var rig = new Rig();
            rig.T.Result = r => r["room"].AsInteger() == RoomD ? AdminSendResult.TransportError : AdminSendResult.Ok;
            await rig.Tick();
            rig.T.Result = _ => AdminSendResult.Ok;
            rig.T.Clear();
            await rig.Tick();
            List<OSDMap> sent = rig.T.Batches();
            Assert.That(Keys(ForRoom(sent, RoomAB)["excl"]), Is.EquivalentTo(new[] { A.ToString(), B.ToString() }));
            Assert.That(Keys(ForRoom(sent, RoomD)["excl"]), Is.EquivalentTo(new[] { D.ToString() }));
        }

        [Test]
        public async Task Reply_HeartbeatAnsweredAsAnUnknownRequest_StopsHeartbeats()
        {
            var rig = new Rig();
            rig.Advertise();
            await rig.Tick();
            rig.T.Response = r => r["request"].AsString() == "peer_ctl_heartbeat"
                ? new OSDMap { ["slvoice"] = OSD.FromString("error"), ["reason"] = OSD.FromString("unknown_request") }
                : Applied(r);
            await rig.Heartbeat();
            Assert.That(rig.Auth.HeartbeatCapable, Is.False);
            rig.Clock += 1000;
            await rig.Heartbeat();
            Assert.That(rig.T.Heartbeats().Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Reply_Malformed_IsNotArmed_AndRetriedAfterTheBackoff()
        {
            var rig = new Rig();
            rig.T.Response = r => r["room"].AsInteger() == RoomD ? new OSDMap { ["room"] = r["room"] } : null;   // no slvoice
            await rig.Tick();
            Assert.That(rig.Auth.IsArmed(RoomD, D), Is.False);
            rig.T.Response = _ => null;
            rig.T.Clear();
            await rig.Tick(1000);
            Assert.That(rig.T.Batches().Single()["room"].AsInteger(), Is.EqualTo(RoomD));
        }
        // ---- the heartbeat reports the feeder's age; the MIXER judges it ----

        [Test]
        public void BuildHeartbeat_WithNoFeedAgeSource_OmitsTheField()
        {
            var auth = new VisAuthority(0x0000018f3a2b4c5dUL, "R");
            OSDMap body = auth.BuildHeartbeat(new[] { A }, a => RoomAB, false);
            // A pre-V1b sim sends no feed_age_ms, and the mixer reads absent as live. Anything else here would
            // change behaviour for every sim that has not opted in.
            Assert.That(body.ContainsKey("feed_age_ms"), Is.False, "feed_age_ms must be absent when nothing supplies it");
        }

        [Test]
        public void BuildHeartbeat_ReportsTheFeedAge_AtBuildTime()
        {
            var auth = new VisAuthority(0x0000018f3a2b4c5dUL, "R");
            long age = 37;
            auth.FeedAgeMs = () => age;
            Assert.That(auth.BuildHeartbeat(new[] { A }, a => RoomAB, false)["feed_age_ms"].AsInteger(), Is.EqualTo(37));
            // The age is read when the BODY is built, not when the source was attached: a heartbeat must describe
            // the matrix it is actually carrying.
            age = 12345;
            Assert.That(auth.BuildHeartbeat(new[] { A }, a => RoomAB, false)["feed_age_ms"].AsInteger(), Is.EqualTo(12345));
        }

        [Test]
        public void BuildHeartbeat_OmitsAnUnreadableOrNegativeFeedAge()
        {
            var auth = new VisAuthority(0x0000018f3a2b4c5dUL, "R");
            auth.FeedAgeMs = () => -1;
            Assert.That(auth.BuildHeartbeat(new[] { A }, a => RoomAB, false).ContainsKey("feed_age_ms"), Is.False,
                "a negative age is malformed: omit it rather than let a bad clock silence the grid");
            auth.FeedAgeMs = () => throw new InvalidOperationException("feeder gone");
            Assert.That(auth.BuildHeartbeat(new[] { A }, a => RoomAB, false).ContainsKey("feed_age_ms"), Is.False,
                "a throwing source must not take the heartbeat down with it");
        }

        [Test]
        public void BuildHeartbeat_StillReportsTheAgeOnAStoppingBody()
        {
            var auth = new VisAuthority(0x0000018f3a2b4c5dUL, "R");
            auth.FeedAgeMs = () => 99;
            OSDMap body = auth.BuildHeartbeat(new[] { A }, a => RoomAB, true);
            Assert.That(body["state"].AsString(), Is.EqualTo("stopping"));
            Assert.That(body["feed_age_ms"].AsInteger(), Is.EqualTo(99),
                "the mixer honours a stopping heartbeat at any age, but the age is still the truth we owe it");
        }

    }
}
