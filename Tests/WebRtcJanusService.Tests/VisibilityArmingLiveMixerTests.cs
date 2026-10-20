/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Live evidence for the visibility arming knob-on path: the real VisibilityBatchSender, JanusPeerCtlBatchSink with
 * its real JanusAdminClient, and VisAuthority, talking to a real mixer. VisibilityArmingTests proves the same logic
 * against a scripted transport; this proves it on the wire against a mixer that advertises vis_protocol 2.
 *
 * EXPLICIT and opt-in: it never runs in a normal test run, and does nothing unless LVM_LIVE_ADMIN_URL is set. It
 * RESTARTS the container named in LVM_LIVE_CONTAINER, so point it only at a scratch mixer, never at a grid's.
 *
 *   LVM_LIVE_ADMIN_URL     the scratch mixer's Admin API, e.g. http://localhost:34225/voiceAdmin
 *   LVM_LIVE_ADMIN_SECRET  its admin_secret (a throwaway value)
 *   LVM_LIVE_ROOM          a room on it created with vis_authority
 *   LVM_LIVE_CONTAINER     the container to `docker restart`
 *   LVM_LIVE_CREATE_ROOM   a shell command that re-creates LVM_LIVE_ROOM after the restart
 *
 * Every log line the sim code writes is printed to the test output, so the run is its own record.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture, Explicit("live scratch mixer; see the file header")]
    public class VisibilityArmingLiveMixerTests
    {
        private static readonly UUID ParcelQ = new UUID("00000000-0000-0000-0000-0000000000ca");

        private sealed class World : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public IReadOnlyList<AgentView> SnapshotAgents() => Agents.ToList();
            public ParcelView GetParcelAt(Vector3 p) => GetParcelByGlobalId(ParcelQ);
            public ParcelView GetParcelByGlobalId(UUID id) => new ParcelView(id, seeAVs: true, allowVoiceChat: true,
                isBannedFromLand: _ => false, isRestrictedFromLand: _ => false);
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

        /// <summary>Collects every formatted log line from the sim code (LoggerProvider resolves its factory per call).</summary>
        private sealed class LineCollector : ILoggerProvider
        {
            public readonly List<string> Lines = new();

            public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

            public void Dispose() { }

            private sealed class Logger : ILogger
            {
                private readonly LineCollector _owner;
                private readonly string _category;

                public Logger(LineCollector owner, string category) { _owner = owner; _category = category; }

                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    if (!IsEnabled(logLevel))
                        return;
                    string line = $"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel,-11} {formatter(state, exception)}";
                    lock (_owner.Lines)
                        _owner.Lines.Add(line);
                    TestContext.Out.WriteLine(line);
                }
            }
        }

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            Assume.That(string.IsNullOrEmpty(v), Is.False, $"{name} is not set: this live test needs a scratch mixer");
            return v;
        }

        private static int Run(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            TestContext.Out.WriteLine($"$ {file} {args} -> exit {p.ExitCode}: {output.Trim()}");
            return p.ExitCode;
        }

        private static async Task WaitForAdmin(string url, string secret)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            DateTime until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    string body = "{\"janus\":\"ping\",\"transaction\":\"live-evidence\",\"admin_secret\":\"" + secret + "\"}";
                    using HttpResponseMessage r = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                    if (r.IsSuccessStatusCode && (await r.Content.ReadAsStringAsync()).Contains("pong"))
                        return;
                }
                catch (Exception) { }
                await Task.Delay(500);
            }
            Assert.Fail("the scratch mixer's Admin API did not answer within 90 s of the restart");
        }

        private static bool Logged(LineCollector c, string fragment)
        {
            lock (c.Lines)
                return c.Lines.Any(l => l.Contains(fragment));
        }

        [Test]
        public async Task RealSimPath_ArmsHeartbeatsAndDetectsAMixerRestart()
        {
            string url = Env("LVM_LIVE_ADMIN_URL");
            string secret = Env("LVM_LIVE_ADMIN_SECRET");
            int room = int.Parse(Env("LVM_LIVE_ROOM"));
            string container = Env("LVM_LIVE_CONTAINER");
            string createRoom = Env("LVM_LIVE_CREATE_ROOM");

            var collector = new LineCollector();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(collector).SetMinimumLevel(LogLevel.Information));
            try
            {
                UUID a = new UUID("0a000000-0000-4000-8000-00000000000a"), b = new UUID("0b000000-0000-4000-8000-00000000000b");
                var world = new World();
                world.Agents.Add(new AgentView(a, false, Vector3.Zero, ParcelQ, false));
                world.Agents.Add(new AgentView(b, false, Vector3.Zero, ParcelQ, false));
                var feed = new MatrixFeed();
                var sink = new JanusPeerCtlBatchSink(url, secret, TimeSpan.FromSeconds(5), UUID.Random(), "live-evidence");
                sink.RoomOf = _ => room;
                var auth = new VisAuthority(VisAuthority.NewEpoch(), "live-evidence");
                sink.Authority = auth;
                long clock = 1_000_000;
                var sender = new VisibilityBatchSender(feed, sink, true, TimeSpan.FromSeconds(5), "live-evidence", () => clock,
                    auth, _ => room);

                // Arming: the first tick sends a replace for both listeners, empty columns included.
                await sender.PumpAsync(feed.Tick(world));
                Assert.That(auth.IsArmed(room, a) && auth.IsArmed(room, b), Is.True, "both listeners armed by the mixer's applied reply");
                Assert.That(auth.HeartbeatCapable, Is.True, "the mixer's reply advertised vis_protocol 2");
                string firstInstance = auth.MixerInstance;
                Assert.That(firstInstance, Is.Not.Null.And.Length.EqualTo(16), "the mixer's reply carried a mixer_instance");
                Assert.That(Logged(collector, "advertises vis_protocol 2"), Is.True);
                Assert.That(Logged(collector, "arming replace sent for 2 listener(s)"), Is.True);

                // Heartbeat: the sender sends one now and acknowledges it.
                await sender.PumpHeartbeatAsync();
                Assert.That(Logged(collector, "first peer_ctl_heartbeat acknowledged"), Is.True, "the mixer acknowledged a heartbeat");

                // Mixer restart: the next heartbeat's reply carries a new mixer_instance (and unknown_room).
                Assert.That(Run("docker", $"restart {container}"), Is.EqualTo(0));
                await WaitForAdmin(url, secret);
                clock += VisAuthority.HeartbeatIntervalMs + 1;
                await sender.PumpHeartbeatAsync();
                Assert.That(auth.MixerInstance, Is.Not.EqualTo(firstInstance), "the sim saw the new mixer_instance");
                Assert.That(Logged(collector, "mixer_instance changed"), Is.True, "and logged it as a restart");
                Assert.That(auth.IsArmed(room, a), Is.False, "the restart disarmed everything at the sim");

                // Re-arm: once the room exists again, the next tick arms every listener (the snapshot after TakeRearmAll).
                Assert.That(Run("sh", $"-c \"{createRoom.Replace("\"", "\\\"")}\""), Is.EqualTo(0));
                clock += VisAuthority.UnknownRoomRetryMs + 1;
                await sender.PumpAsync(feed.Tick(world));
                Assert.That(auth.IsArmed(room, a) && auth.IsArmed(room, b), Is.True, "re-armed after the restart");
                clock += VisAuthority.HeartbeatIntervalMs + 1;
                await sender.PumpHeartbeatAsync();
                Assert.That(auth.IsArmed(room, a), Is.True, "the heartbeat after re-arming leaves them armed");
            }
            finally
            {
                LoggerProvider.LoggerFactory = previous;
            }
        }
    }
}
