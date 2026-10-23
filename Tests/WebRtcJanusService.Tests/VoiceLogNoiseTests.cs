/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What the visibility code writes to the log in normal use, and what it writes when something keeps failing.
 *
 *   - A failing emit is one error line a minute with the count of the ones held back, not one for every tick.
 *   - An arming replace, a mixer that defers entries for listeners that have not joined yet, and a mixer report of
 *     stale listeners are Debug lines; their counts go into the heartbeat summary.
 *   - The heartbeat summary is not written while a region has no rooms and no listeners. One line says it went
 *     idle and one says it woke.
 *   - The first unknown_room for a room whose last listener left (the mixer removes an empty room after a grace
 *     period) is a Debug line, in the sender's log and in the sink's; a second one is a warning.
 *   - The heartbeat interval is a setting. The default is the interval the code always used.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class VoiceLogNoiseTests
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

        private static readonly UUID A = Id(1), B = Id(2), D = Id(4);
        private static readonly UUID ParcelP = Id(201), ParcelQ = Id(202);

        // ---- log capture --------------------------------------------------------------------------

        private sealed class Capture : ILoggerProvider, ILogger
        {
            public readonly List<(LogLevel Level, string Text)> Lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add((logLevel, formatter(state, exception)));
            }
            public void Dispose() { }

            public List<string> Where(LogLevel level, string contains)
            {
                lock (Lines)
                    return Lines.Where(l => l.Level == level && l.Text.Contains(contains)).Select(l => l.Text).ToList();
            }

            public List<string> Any(string contains)
            {
                lock (Lines)
                    return Lines.Where(l => l.Text.Contains(contains)).Select(l => l.Level + ": " + l.Text).ToList();
            }

            public List<string> AtOrAbove(LogLevel level, string contains)
            {
                lock (Lines)
                    return Lines.Where(l => l.Level >= level && l.Text.Contains(contains)).Select(l => l.Level + ": " + l.Text).ToList();
            }
        }

        private Capture _log;
        private ILoggerFactory _previous;

        [SetUp]
        public void SetUp()
        {
            _log = new Capture();
            _previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
        }

        [TearDown]
        public void TearDown()
        {
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
            _log.Dispose();
        }

        // ---- a region: scene-free world, a scripted mixer, the real sink, authority and sender -------

        private sealed class World : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public IReadOnlyList<AgentView> SnapshotAgents() => Agents.ToList();
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(ParcelQ);
            public ParcelView GetParcelByGlobalId(UUID id) => new ParcelView(id, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: x => id == ParcelP && x == A, isRestrictedFromLand: _ => false);
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
            public Func<OSDMap, OSDMap> Response = _ => null;
            public int Sends;

            public Task<(AdminSendResult, string)> SendAsync(OSDMap request)
            {
                Sends++;
                OSDMap response = Response(request) ?? Default(request);
                var top = new OSDMap { ["janus"] = OSD.FromString("success"), ["response"] = response };
                return Task.FromResult((AdminSendResult.Ok, OSDParser.SerializeJsonString(top)));
            }

            public static OSDMap Default(OSDMap request)
                => request["request"].AsString() == "peer_ctl_heartbeat"
                    ? HeartbeatReply()
                    : Applied(request);

            public static OSDMap Applied(OSDMap request)
                => new OSDMap
                {
                    ["slvoice"] = OSD.FromString("applied"),
                    ["room"] = request["room"],
                    ["vis_protocol"] = OSD.FromInteger(2),
                    ["mixer_instance"] = OSD.FromString("m1"),
                };

            public static OSDMap HeartbeatReply(params (int room, UUID[] unarmed)[] rooms)
            {
                var roomMap = new OSDMap();
                foreach ((int room, UUID[] unarmed) in rooms)
                {
                    var entry = new OSDMap { ["status"] = OSD.FromString("ok") };
                    if (unarmed != null)
                    {
                        var a = new OSDArray();
                        foreach (UUID u in unarmed) a.Add(OSD.FromString(u.ToString()));
                        entry["unarmed_listeners"] = a;
                    }
                    roomMap[room.ToString()] = entry;
                }
                return new OSDMap
                {
                    ["slvoice"] = OSD.FromString("heartbeat"),
                    ["vis_protocol"] = OSD.FromInteger(2),
                    ["mixer_instance"] = OSD.FromString("m1"),
                    ["rooms"] = roomMap,
                };
            }
        }

        private sealed class Region
        {
            public long Clock = 10_000;
            public readonly World World = new();
            public readonly Dictionary<UUID, int> Rooms = new() { [A] = RoomAB, [B] = RoomAB, [D] = RoomD };
            public readonly Transport T = new();
            public readonly MatrixFeed Feed = new();
            public readonly VisAuthority Auth;
            public readonly JanusPeerCtlBatchSink Sink;
            public readonly VisibilityBatchSender Sender;

            public Region(bool withAgents = true, int? heartbeatMs = null)
            {
                if (withAgents)
                    AddAgents();
                Sink = new JanusPeerCtlBatchSink("http://unused", "unused", TimeSpan.FromSeconds(5), Id(77), "noise",
                    sendOne: T.SendAsync);
                Sink.RoomOf = a => Rooms.TryGetValue(a, out int r) ? r : (int?)null;
                Auth = heartbeatMs.HasValue
                    ? new VisAuthority(VisAuthority.NewEpoch(), "noise", () => Clock, heartbeatMs.Value)
                    : new VisAuthority(VisAuthority.NewEpoch(), "noise", () => Clock);
                Sink.Authority = Auth;
                Sender = new VisibilityBatchSender(Feed, Sink, true, TimeSpan.FromSeconds(5), "noise", () => Clock,
                    Auth, a => Rooms.TryGetValue(a, out int r) ? r : (int?)null);
            }

            public void AddAgents()
            {
                World.Agents.Add(new AgentView(A, false, Vector3.Zero, ParcelQ, false));   // A on Q, banned from P
                World.Agents.Add(new AgentView(B, false, Vector3.Zero, ParcelP, false));   // B on P
                World.Agents.Add(new AgentView(D, false, Vector3.Zero, ParcelQ, false));   // D on Q
            }

            public async Task Tick(long advanceMs = 250)
            {
                Clock += advanceMs;
                await Sender.PumpAsync(Feed.Tick(World));
            }

            public Task Beat(long advanceMs = 1000)
            {
                Clock += advanceMs;
                return Sender.PumpHeartbeatAsync();
            }

            // The mixer has advertised vis_protocol 2, so heartbeats are allowed, without needing a batch.
            public void MixerAdvertises()
                => Auth.OnBatchOutcome(RoomAB, VisOp.Replace, 1, Array.Empty<UUID>(), true,
                    new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "applied", VisProtocol = 2, MixerInstance = "m1" });
        }

        // ---- R7: a failing emit ---------------------------------------------------------------------

        private sealed class ThrowingSink : IPeerCtlBatchSink
        {
            public Task<PeerCtlSendResult> SendAsync(VisOp op, IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
                IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null)
                => throw new InvalidOperationException("admin API is down");
            public PeerCtlSendStats LastSendStats => default;
        }

        [Test]
        public async Task AFailingEmit_IsOneErrorAMinute_WithTheCountOfTheOnesHeldBack()
        {
            long clock = 10_000;
            var world = new World();
            world.Agents.Add(new AgentView(A, false, Vector3.Zero, ParcelQ, false));
            world.Agents.Add(new AgentView(B, false, Vector3.Zero, ParcelP, false));
            var feed = new MatrixFeed();
            var sender = new VisibilityBatchSender(feed, new ThrowingSink(), true, TimeSpan.FromSeconds(5), "noise", () => clock);

            for (int i = 0; i < 20; i++)
            {
                clock += 250;
                await sender.PumpAsync(feed.Tick(world));
            }
            Assert.That(_log.Where(LogLevel.Error, "emit failed"), Has.Count.EqualTo(1),
                "twenty failing ticks in five seconds write one error line");

            clock += 61_000;
            await sender.PumpAsync(feed.Tick(world));
            List<string> errors = _log.Where(LogLevel.Error, "emit failed");
            Assert.That(errors, Has.Count.EqualTo(2), "the next minute writes one more");
            Assert.That(errors[1], Does.Contain("19 more"), "and says how many it stands for");
        }

        // ---- R8: arming replaces, deferred entries and stale reports ----------------------------------

        [Test]
        public async Task AnArmingReplace_IsADebugLine_AndItsCountIsInTheNextSummary()
        {
            var r = new Region();
            await r.Tick();

            Assert.That(_log.AtOrAbove(LogLevel.Information, "arming replace sent"), Is.Empty);
            Assert.That(_log.Where(LogLevel.Debug, "arming replace sent"), Has.Count.EqualTo(1));

            await r.Beat();
            await r.Beat(61_000);
            List<string> summary = _log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary");
            Assert.That(summary, Has.Count.EqualTo(1));
            Assert.That(summary[0], Does.Contain("1 arming replace(s) for 3 listener(s)"));
        }

        [Test]
        public async Task EntriesTheMixerDefers_AreADebugLine_AndTheirCountIsInTheNextSummary()
        {
            var r = new Region();
            r.T.Response = req => req["request"].AsString() == "peer_ctl_batch"
                ? new OSDMap
                {
                    ["slvoice"] = OSD.FromString("applied"),
                    ["room"] = req["room"],
                    ["vis_protocol"] = OSD.FromInteger(2),
                    ["mixer_instance"] = OSD.FromString("m1"),
                    ["deferred_listeners"] = OSD.FromInteger(2),
                }
                : null;
            await r.Tick();

            Assert.That(_log.AtOrAbove(LogLevel.Information, "mixer deferred"), Is.Empty);
            Assert.That(_log.Where(LogLevel.Debug, "mixer deferred"), Is.Not.Empty);

            await r.Beat();
            await r.Beat(61_000);
            List<string> summary = _log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary");
            Assert.That(summary, Has.Count.EqualTo(1));
            Assert.That(summary[0], Does.Contain("deferred entr(y/ies)").And.Match(@".* [1-9]\d* deferred entr\(y/ies\).*"));
        }

        [Test]
        public async Task AStaleReportFromTheMixer_IsADebugLine_AndItsCountIsInTheNextSummary()
        {
            var r = new Region();
            await r.Tick();
            r.T.Response = req => req["request"].AsString() == "peer_ctl_heartbeat"
                ? Transport.HeartbeatReply((RoomAB, new[] { A }))
                : null;
            await r.Beat();
            r.T.Response = _ => null;   // the mixer reported A once

            Assert.That(_log.AtOrAbove(LogLevel.Information, "stale/unarmed"), Is.Empty);
            Assert.That(_log.Where(LogLevel.Debug, "stale/unarmed"), Is.Not.Empty);

            await r.Beat(61_000);
            List<string> summary = _log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary");
            Assert.That(summary, Has.Count.EqualTo(1));
            Assert.That(summary[0], Does.Contain("mixer reported 1 stale or unarmed listener(s)"));
        }

        // ---- the heartbeat summary of an idle region ---------------------------------------------------

        [Test]
        public async Task AnIdleRegion_WritesNoSummary_JustOneLineWhenItGoesIdleAndOneWhenItWakes()
        {
            var r = new Region(withAgents: false);
            r.MixerAdvertises();

            for (int i = 0; i < 4; i++)
                await r.Beat(61_000);   // four minutes with no rooms and no listeners
            Assert.That(_log.Any("peer_ctl_heartbeat summary"), Is.Empty, "no summary while idle");
            Assert.That(_log.Where(LogLevel.Debug, "went idle"), Has.Count.EqualTo(1));

            r.AddAgents();
            await r.Tick();
            await r.Beat();
            Assert.That(_log.Where(LogLevel.Debug, "woke"), Has.Count.EqualTo(1));
            Assert.That(_log.Where(LogLevel.Debug, "went idle"), Has.Count.EqualTo(1), "still just the one");

            await r.Beat(61_000);
            Assert.That(_log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary"), Has.Count.EqualTo(1),
                "a region that has someone in it writes its summary as before");
        }

        // ---- the unknown_room pair on a first join into a room the mixer removed -----------------------

        private static OSDMap UnknownRoom(OSDMap request)
            => new OSDMap
            {
                ["slvoice"] = OSD.FromString("error"),
                ["reason"] = OSD.FromString("unknown_room"),
                ["room"] = request["room"],
                ["vis_protocol"] = OSD.FromInteger(2),
                ["mixer_instance"] = OSD.FromString("m1"),
            };

        [Test]
        public async Task TheFirstUnknownRoom_AfterTheRoomEmptied_IsDebugInTheSenderAndTheSink_AndTheSecondIsAWarning()
        {
            var r = new Region();
            await r.Tick();                       // the room answers: everyone is armed
            r.World.Agents.Clear();               // everyone leaves; the mixer removes the empty rooms after its grace period
            await r.Tick();
            await r.Tick();
            _log.Lines.Clear();

            r.T.Response = req => req["request"].AsString() == "peer_ctl_batch" ? UnknownRoom(req) : null;
            r.AddAgents();                        // the first avatar to come back is addressed before it provisions
            await r.Tick(1500);

            Assert.That(_log.AtOrAbove(LogLevel.Warning, "unknown_room"), Is.Empty,
                "a room that emptied and was removed is expected to be missing once");
            Assert.That(_log.AtOrAbove(LogLevel.Warning, "inner-reply anomaly"), Is.Empty);
            Assert.That(_log.Where(LogLevel.Debug, "unknown_room"), Is.Not.Empty);

            await r.Tick(2500);                   // still missing at the retry
            Assert.That(_log.AtOrAbove(LogLevel.Warning, "unknown_room"), Is.Not.Empty,
                "a room that is still missing after the retry is a warning");
        }

        [Test]
        public async Task UnknownRoom_ForARoomThatStillHasListeners_IsStillAWarning()
        {
            var r = new Region();
            await r.Tick();                       // armed
            _log.Lines.Clear();

            r.T.Response = req => req["request"].AsString() == "peer_ctl_batch" ? UnknownRoom(req) : null;
            r.Auth.RequestArm(A);                 // the mixer lost a room that has listeners in it
            await r.Tick(1500);

            Assert.That(_log.AtOrAbove(LogLevel.Warning, "unknown_room"), Is.Not.Empty);
        }

        // ---- the heartbeat interval is a setting --------------------------------------------------------

        [Test]
        public void TheHeartbeatInterval_DefaultsToOneSecond_SoNothingChanges()
        {
            Assert.That(VisAuthority.DefaultHeartbeatIntervalMs, Is.EqualTo(1000));
            Assert.That(new VisAuthority(1UL, "r").HeartbeatIntervalMs, Is.EqualTo(1000));
            IConfig cfg = new IniConfigSource().AddConfig("WebRtcVoice");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityHeartbeatMs(cfg), Is.EqualTo(1000));
        }

        [Test]
        public void TheHeartbeatInterval_CanBeLengthened_AndTheHeartbeatSaysSo()
        {
            IConfig cfg = new IniConfigSource().AddConfig("WebRtcVoice");
            cfg.Set("VisibilityHeartbeatMs", "2500");
            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityHeartbeatMs(cfg), Is.EqualTo(2500));

            var a = new VisAuthority(1UL, "r", () => 0, 2500);
            Assert.That(a.HeartbeatIntervalMs, Is.EqualTo(2500));
            OSDMap body = a.BuildHeartbeat(Array.Empty<UUID>(), _ => null, stopping: false);
            Assert.That(body["interval_ms"].AsInteger(), Is.EqualTo(2500), "the mixer is told the interval it should expect");
        }

        [TestCase("100")]
        [TestCase("4001")]
        [TestCase("0")]
        [TestCase("-5")]
        public void AnIntervalOutsideTheSafeRange_IsRefusedWithAWarning_AndTheDefaultIsUsed(string value)
        {
            IConfig cfg = new IniConfigSource().AddConfig("WebRtcVoice");
            cfg.Set("VisibilityHeartbeatMs", value);

            Assert.That(WebRtcVoiceRegionModule.ReadVisibilityHeartbeatMs(cfg, LoggerProvider.CreateLogger("test")), Is.EqualTo(1000));
            Assert.That(_log.Where(LogLevel.Warning, "VisibilityHeartbeatMs"), Has.Count.EqualTo(1));
        }
    }
}
