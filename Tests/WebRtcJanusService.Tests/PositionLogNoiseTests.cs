/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What the position feed writes to the log when something keeps failing.
 *
 *   - Each of the pump's three failure sites (reading positions, the tick itself, sending) writes one warning a
 *     minute with the count of the ones held back, not one for every tick.
 *   - The sink's own "peer_pos send failed" line is a Debug line: the pump's once-a-minute line is the one an
 *     operator reads.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class PositionLogNoiseTests
    {
        private static readonly UUID A = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

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

            public List<string> AtOrAbove(LogLevel level, string contains)
            {
                lock (Lines)
                    return Lines.Where(l => l.Level >= level && l.Text.Contains(contains)).Select(l => l.Level + ": " + l.Text).ToList();
            }
        }

        private Capture _log;
        private ILoggerFactory _previous;
        private long _now;

        [SetUp]
        public void SetUp()
        {
            _log = new Capture();
            _previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
            _now = 10_000;
        }

        [TearDown]
        public void TearDown()
        {
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
            _log.Dispose();
        }

        /// <summary>One avatar that moves a metre a tick, so every tick has something to send.</summary>
        private sealed class World : IPositionWorld
        {
            public bool Throw;
            public int Step;
            public IReadOnlyList<PositionView> SnapshotPositions()
            {
                if (Throw)
                    throw new InvalidOperationException("scene walk failed");
                Step++;
                return new[] { new PositionView(A, false, 100 + Step * 500, 200, 300, 0) };
            }
        }

        private sealed class Sink : IPeerPosSink
        {
            public bool ThrowOnProtocol;
            public bool ThrowOnSend;
            public int MixerVisProtocol => ThrowOnProtocol ? throw new InvalidOperationException("protocol read failed") : 3;
            public PositionReplyStats Replies { get; } = new PositionReplyStats();
            public bool MixerProtocolKnown => true;
            public Task<bool> CheckMixerProtocolAsync() => Task.FromResult(true);

            public Task<bool> SendPositionsAsync(OSDMap body)
                => ThrowOnSend ? throw new InvalidOperationException("send failed") : Task.FromResult(true);
        }

        private VoicePositionPump Pump(World world, Sink sink)
            => new VoicePositionPump(world, sink, new PositionSettings(), _ => 7, () => 100000, "Test Region", () => _now);

        private async Task TickManyAsync(VoicePositionPump pump, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                _now += 500;
                await pump.TickAsync();
            }
        }

        [TestCase("read")]
        [TestCase("tick")]
        [TestCase("send")]
        public async Task TwentyFailingTicksInTenSeconds_GiveOneWarning_ThenOneMoreWithTheCount(string site)
        {
            var world = new World { Throw = site == "read" };
            var sink = new Sink { ThrowOnProtocol = site == "tick", ThrowOnSend = site == "send" };
            VoicePositionPump pump = Pump(world, sink);

            await TickManyAsync(pump, 20);

            Assert.That(_log.AtOrAbove(LogLevel.Warning, "[VOICE POSITIONS]"), Has.Count.EqualTo(1), site);

            _now += 60_000;
            await TickManyAsync(pump, 1);

            List<string> lines = _log.AtOrAbove(LogLevel.Warning, "[VOICE POSITIONS]");
            Assert.That(lines, Has.Count.EqualTo(2), string.Join(" | ", lines));
            Assert.That(lines[1], Does.Contain("19 more"));
        }

        [Test]
        public async Task TheSinksPeerPosSendFailure_IsNotAWarning()
        {
            var sink = new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5),
                UUID.Random(), "Test Region", 4, _ => throw new InvalidOperationException("admin down"));
            var body = new OSDMap { ["request"] = OSD.FromString("peer_pos"), ["room"] = OSD.FromInteger(7) };

            for (int i = 0; i < 5; i++)
                Assert.That(await sink.SendPositionsAsync(body), Is.False);

            Assert.That(_log.AtOrAbove(LogLevel.Information, "peer_pos send failed"), Is.Empty);
        }
    }
}
