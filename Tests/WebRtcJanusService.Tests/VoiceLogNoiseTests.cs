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
    public class VoiceLogNoiseTests : IAsyncLifetime
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
        private LogCapture _logging;
        private void SetUp()
        {
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
        }
        private void TearDown()
        {
            _logging.Dispose();
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
            // Required by the interface; this fake never raises it.
            public event Action<VisibilityBatch> BatchProduced { add { } remove { } }
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

        [Fact(Timeout = 60000)]
        public async Task AFailingEmit_IsOneErrorAMinute_WithTheCountOfTheOnesHeldBack()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            {
                var assertedValue = _log.Where(LogLevel.Error, "emit failed");
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "twenty failing ticks in five seconds write one error line");
            }

            clock += 61_000;
            await sender.PumpAsync(feed.Tick(world));
            List<string> errors = _log.Where(LogLevel.Error, "emit failed");
            {
                var assertedValue = errors;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "the next minute writes one more");
            }
            {
                var assertedValue = errors[1];
                TestAssert.WithMessage(() => Assert.Contains("19 more", assertedValue), "and says how many it stands for");
            }
        }

        // ---- R8: arming replaces, deferred entries and stale reports ----------------------------------

        [Fact(Timeout = 60000)]
        public async Task AnArmingReplace_IsADebugLine_AndItsCountIsInTheNextSummary()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = new Region();
            await r.Tick();

            Assert.Empty(_log.AtOrAbove(LogLevel.Information, "arming replace sent"));
            Assert.Single(_log.Where(LogLevel.Debug, "arming replace sent"));

            await r.Beat();
            await r.Beat(61_000);
            List<string> summary = _log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary");
            Assert.Single(summary);
            Assert.Contains("1 arming replace(s) for 3 listener(s)", summary[0]);
        }

        [Fact(Timeout = 60000)]
        public async Task EntriesTheMixerDefers_AreADebugLine_AndTheirCountIsInTheNextSummary()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            Assert.Empty(_log.AtOrAbove(LogLevel.Information, "mixer deferred"));
            Assert.NotEmpty(_log.Where(LogLevel.Debug, "mixer deferred"));

            await r.Beat();
            await r.Beat(61_000);
            List<string> summary = _log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary");
            Assert.Single(summary);
            {
                var assertedValue = summary[0];
                Assert.Contains("deferred entr(y/ies)", assertedValue);
                Assert.Matches(@".* [1-9]\d* deferred entr\(y/ies\).*", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public async Task AStaleReportFromTheMixer_IsADebugLine_AndItsCountIsInTheNextSummary()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = new Region();
            await r.Tick();
            r.T.Response = req => req["request"].AsString() == "peer_ctl_heartbeat"
                ? Transport.HeartbeatReply((RoomAB, new[] { A }))
                : null;
            await r.Beat();
            r.T.Response = _ => null;   // the mixer reported A once

            Assert.Empty(_log.AtOrAbove(LogLevel.Information, "stale/unarmed"));
            Assert.NotEmpty(_log.Where(LogLevel.Debug, "stale/unarmed"));

            await r.Beat(61_000);
            List<string> summary = _log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary");
            Assert.Single(summary);
            Assert.Contains("mixer reported 1 stale or unarmed listener(s)", summary[0]);
        }

        // ---- the heartbeat summary of an idle region ---------------------------------------------------

        [Fact(Timeout = 60000)]
        public async Task AnIdleRegion_WritesNoSummary_JustOneLineWhenItGoesIdleAndOneWhenItWakes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = new Region(withAgents: false);
            r.MixerAdvertises();

            for (int i = 0; i < 4; i++)
                await r.Beat(61_000);   // four minutes with no rooms and no listeners
            {
                var assertedValue = _log.Any("peer_ctl_heartbeat summary");
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no summary while idle");
            }
            Assert.Single(_log.Where(LogLevel.Debug, "went idle"));

            r.AddAgents();
            await r.Tick();
            await r.Beat();
            Assert.Single(_log.Where(LogLevel.Debug, "woke"));
            {
                var assertedValue = _log.Where(LogLevel.Debug, "went idle");
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "still just the one");
            }

            await r.Beat(61_000);
            {
                var assertedValue = _log.Where(LogLevel.Debug, "peer_ctl_heartbeat summary");
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "a region that has someone in it writes its summary as before");
            }
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

        [Fact(Timeout = 60000)]
        public async Task TheFirstUnknownRoom_AfterTheRoomEmptied_IsDebugInTheSenderAndTheSink_AndTheSecondIsAWarning()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = new Region();
            await r.Tick();                       // the room answers: everyone is armed
            r.World.Agents.Clear();               // everyone leaves; the mixer removes the empty rooms after its grace period
            await r.Tick();
            await r.Tick();
            _log.Lines.Clear();

            r.T.Response = req => req["request"].AsString() == "peer_ctl_batch" ? UnknownRoom(req) : null;
            r.AddAgents();                        // the first avatar to come back is addressed before it provisions
            await r.Tick(1500);

            {
                var assertedValue = _log.AtOrAbove(LogLevel.Warning, "unknown_room");
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a room that emptied and was removed is expected to be missing once");
            }
            Assert.Empty(_log.AtOrAbove(LogLevel.Warning, "inner-reply anomaly"));
            Assert.NotEmpty(_log.Where(LogLevel.Debug, "unknown_room"));

            await r.Tick(2500);                   // still missing at the retry
            {
                var assertedValue = _log.AtOrAbove(LogLevel.Warning, "unknown_room");
                TestAssert.WithMessage(() => Assert.NotEmpty(assertedValue), "a room that is still missing after the retry is a warning");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task UnknownRoom_ForARoomThatStillHasListeners_IsStillAWarning()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var r = new Region();
            await r.Tick();                       // armed
            _log.Lines.Clear();

            r.T.Response = req => req["request"].AsString() == "peer_ctl_batch" ? UnknownRoom(req) : null;
            r.Auth.RequestArm(A);                 // the mixer lost a room that has listeners in it
            await r.Tick(1500);

            Assert.NotEmpty(_log.AtOrAbove(LogLevel.Warning, "unknown_room"));
        }

        // ---- the heartbeat interval is a setting --------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheHeartbeatInterval_DefaultsToOneSecond_SoNothingChanges()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1000, VisAuthority.DefaultHeartbeatIntervalMs);
            Assert.Equal(1000, new VisAuthority(1UL, "r").HeartbeatIntervalMs);
            IConfig cfg = new IniConfigSource().AddConfig("WebRtcVoice");
            Assert.Equal(1000, WebRtcVoiceRegionModule.ReadVisibilityHeartbeatMs(cfg));
        }

        [Fact(Timeout = 60000)]
        public void TheHeartbeatInterval_CanBeLengthened_AndTheHeartbeatSaysSo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = new IniConfigSource().AddConfig("WebRtcVoice");
            cfg.Set("VisibilityHeartbeatMs", "2500");
            Assert.Equal(2500, WebRtcVoiceRegionModule.ReadVisibilityHeartbeatMs(cfg));

            var a = new VisAuthority(1UL, "r", () => 0, 2500);
            Assert.Equal(2500, a.HeartbeatIntervalMs);
            OSDMap body = a.BuildHeartbeat(Array.Empty<UUID>(), _ => null, stopping: false);
            {
                var assertedValue = body["interval_ms"].AsInteger();
                TestAssert.WithMessage(() => Assert.Equal(2500, assertedValue), "the mixer is told the interval it should expect");
            }
        }

        [InlineData("100")]
        [InlineData("4001")]
        [InlineData("0")]
        [InlineData("-5")]
        [Theory(Timeout = 60000)]
        public void AnIntervalOutsideTheSafeRange_IsRefusedWithAWarning_AndTheDefaultIsUsed(string value)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IConfig cfg = new IniConfigSource().AddConfig("WebRtcVoice");
            cfg.Set("VisibilityHeartbeatMs", value);

            Assert.Equal(1000, WebRtcVoiceRegionModule.ReadVisibilityHeartbeatMs(cfg, LoggerProvider.CreateLogger("test")));
            Assert.Single(_log.Where(LogLevel.Warning, "VisibilityHeartbeatMs"));
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            TearDown();
            return ValueTask.CompletedTask;
        }
    }
}
