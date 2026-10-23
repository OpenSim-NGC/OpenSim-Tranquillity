/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Golden-file proof: with [WebRtcVoice] VisibilityArmingEnabled OFF (the default), the sim's
 * peer_ctl_batch payloads and its room-create body are byte-for-byte what they were before the
 * arming feature existed.
 *
 * The golden file (Golden/visibility-knob-off.golden.txt) was CAPTURED FROM THE CODE BEFORE THE
 * ARMING FEATURE CHANGED ANYTHING. It was written by this same test with
 * VIS_GOLDEN_UPDATE=1, and is compared exactly on every run since. One scenario runs through the real
 * VisibilityBatchSender and the real JanusPeerCtlBatchSink, with a recording transport in place of the
 * admin HTTP call:
 *   tick 1  snapshot; listener D has an empty column (today: skipped);
 *   join    OnListenerProvisioned for A (non-empty, re-sent) and D (empty, today: skipped);
 *   tick 2  C joins moderated: a multi-room add, including a listener recorded in the estate room;
 *   tick 3  the ban is lifted: a remove;
 *   tick 4  C leaves, and the transport fails: nothing applied, unsynced;
 *   tick 5  the re-snapshot, with clear-tracking empty replaces;
 * plus the AudioBridgeCreateRoomReq body a spatial room create sends.
 * Each tick's bodies are sorted ordinally, because a multi-room send issues its rooms concurrently.
 * Every body is otherwise the exact serialized JSON.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VisibilityKnobOffGoldenTests
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

        private static readonly UUID A = Id(1), B = Id(2), C = Id(3), D = Id(4);
        private static readonly UUID ParcelP = Id(201), ParcelQ = Id(202);
        private static readonly UUID GoldenRegion = Id(77);

        private sealed class World : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public bool BanAOnP = true;
            public bool ModerateC = false;
            // An id listed here is admitted only while it holds a viewer session in GoldenRegion,
            // FeederWorldFromScene's IsAgentInRegion gate. Empty in the golden scenario itself.
            public readonly HashSet<UUID> Gated = new();
            public IReadOnlyList<AgentView> SnapshotAgents()
                => Agents.Where(a => !Gated.Contains(a.Id) || VoiceViewerSession.IsAgentInRegion(GoldenRegion, a.Id)).ToList();
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(ParcelQ);
            public ParcelView GetParcelByGlobalId(UUID id) => id == ParcelP
                ? new ParcelView(ParcelP, seeAVs: true, allowVoiceChat: true,
                    isBannedFromLand: x => BanAOnP && x == A, isRestrictedFromLand: _ => false,
                    isVoiceModerated: x => ModerateC && x == C)
                : new ParcelView(ParcelQ, seeAVs: true, allowVoiceChat: true,
                    isBannedFromLand: _ => false, isRestrictedFromLand: _ => false,
                    isVoiceModerated: x => ModerateC && x == C);
            public EstateView Estate => new EstateView(true, false, _ => false);
        }

        /// Stands in for VoiceStateFeeder without its single-tick-thread assert (awaits resume elsewhere).
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
            public readonly List<string> Bodies = new();
            public AdminSendResult Result = AdminSendResult.Ok;

            public Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                lock (_lock)
                    Bodies.Add(OSDParser.SerializeJsonString(request, true));
                string reply = "{\"janus\":\"success\",\"response\":{\"slvoice\":\"applied\",\"room\":" +
                    request["room"].AsInteger() + ",\"entries\":1}}";
                return Task.FromResult((Result, reply));
            }

            public List<string> Take()
            {
                lock (_lock)
                {
                    var taken = Bodies.OrderBy(s => s, StringComparer.Ordinal).ToList();
                    Bodies.Clear();
                    return taken;
                }
            }
        }

        private static string GoldenPath([CallerFilePath] string here = "")
            => Path.Combine(Path.GetDirectoryName(here), "Golden", "visibility-knob-off.golden.txt");

        /// The scenario, run against whatever the default (knob-off) construction is in this build.
        internal static async Task<string> RunScenarioAsync()
        {
            var world = new World();
            world.Agents.Add(new AgentView(A, false, Vector3.Zero, ParcelQ, false));   // A on Q, banned from P
            world.Agents.Add(new AgentView(B, false, Vector3.Zero, ParcelP, false));   // B on P
            world.Agents.Add(new AgentView(D, false, Vector3.Zero, ParcelQ, false));   // D on Q: an empty column

            var rooms = new Dictionary<UUID, int> { [A] = RoomAB, [B] = RoomAB, [D] = RoomD };
            var transport = new Transport();
            var sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), GoldenRegion, "golden",
                sendOne: transport.SendAsync);
            // The sink's resolver answers from the record only, as the service's does. C's record is the estate room,
            // as if it had been provisioned into it, which is the room the golden file recorded for it.
            rooms[C] = sink.FallbackRoom;
            sink.RoomOf = a => rooms.TryGetValue(a, out int r) ? r : (int?)null;
            var feed = new MatrixFeed();
            var sender = new VisibilityBatchSender(feed, sink, true, TimeSpan.FromSeconds(5), "golden");

            var sb = new StringBuilder();
            void Record(string step)
            {
                sb.Append("== ").Append(step).Append('\n');
                foreach (string body in transport.Take())
                    sb.Append(body).Append('\n');
            }

            await sender.PumpAsync(feed.Tick(world));
            Record("tick 1: snapshot, D's column empty");

            sender.OnListenerProvisioned(A);
            sender.OnListenerProvisioned(D);
            await sender.PumpAsync(feed.Tick(world));
            Record("join: A and D provisioned (quiet tick)");

            world.ModerateC = true;
            world.Agents.Add(new AgentView(C, false, Vector3.Zero, ParcelP, false));
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 2: C joins moderated (multi-room add, C recorded in the estate room)");

            world.BanAOnP = false;
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 3: ban lifted (remove)");

            world.Agents.RemoveAll(a => a.Id == C);
            transport.Result = AdminSendResult.TransportError;
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 4: C leaves, transport fails");

            transport.Result = AdminSendResult.Ok;
            await sender.PumpAsync(feed.Tick(world));
            Record("tick 5: re-snapshot with clear-tracking");

            var create = new AudioBridgeCreateRoomReq(RoomAB, true, "golden");
            create.ToJson();
            sb.Append("== room create body (spatial)\n")
              .Append(OSDParser.SerializeJsonString(create.RawBody["body"], true)).Append('\n');

            return sb.ToString();
        }

        [Test]
        public async Task KnobOff_PayloadsAndRoomCreate_AreByteIdenticalToTheGoldenFile()
        {
            string actual = await RunScenarioAsync();
            string path = GoldenPath();
            if (Environment.GetEnvironmentVariable("VIS_GOLDEN_UPDATE") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, actual, new UTF8Encoding(false));
                Assert.Pass("golden written: " + path);
            }
            Assert.That(File.Exists(path), Is.True, "golden file missing: " + path);
            string golden = File.ReadAllText(path).Replace("\r\n", "\n");   // a CRLF checkout changes line ends only
            Assert.That(actual, Is.EqualTo(golden));
        }
    }
}
