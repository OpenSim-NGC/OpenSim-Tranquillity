/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The mixer's answer to a peer_pos request. A request that reaches the mixer counts as sent as far as the
 * transport can tell, but the mixer may refuse it. The region counts applied and refused (by reason), writes at
 * most one refusal line a minute, and reports the counts in the position feed's one-minute summary and in
 * "show voice visibility".
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
    public class PositionReplyTests
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

            public List<string> Where(LogLevel level, string contains)
            {
                lock (Lines)
                    return Lines.Where(l => l.Level == level && l.Text.Contains(contains)).Select(l => l.Text).ToList();
            }

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

        // ---- the mapping --------------------------------------------------------------------------

        [TestCase("applied", null, PositionReplyKind.Applied)]
        [TestCase("error", "stale_seq", PositionReplyKind.StaleSeq)]
        [TestCase("error", "unknown_room", PositionReplyKind.UnknownRoom)]
        [TestCase("error", "too_big", PositionReplyKind.TooBig)]
        [TestCase("error", "malformed", PositionReplyKind.Other)]
        [TestCase("error", "stale_epoch", PositionReplyKind.Other)]
        [TestCase("error", null, PositionReplyKind.Other)]
        [TestCase("empty", null, PositionReplyKind.Other)]
        [TestCase(null, null, PositionReplyKind.Other)]
        public void AnInnerReply_IsClassifiedByItsStatusAndReason(string status, string reason, PositionReplyKind expected)
            => Assert.That(PositionReply.Classify(true, status, reason), Is.EqualTo(expected));

        [Test]
        public void NoInnerReply_IsNotClassified()
            => Assert.That(PositionReply.Classify(false, null, null), Is.EqualTo(PositionReplyKind.None));

        // ---- the sink counts what the mixer answered ----------------------------------------------

        private static string Inner(string status, string reason = null)
            => "{\"janus\":\"success\",\"response\":{\"slvoice\":\"" + status + "\"" +
               (reason != null ? ",\"reason\":\"" + reason + "\"" : ",\"op\":\"pos\",\"room\":7,\"entries\":1,\"skipped\":0,\"dropped\":0") +
               ",\"vis_protocol\":3}}";

        private JanusPeerCtlBatchSink Sink(Func<OSDMap, (AdminSendResult, string)> reply)
            => new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5), UUID.Random(), "Test Region", 4,
                req => Task.FromResult(reply(req)), nowMs: () => _now);

        private static OSDMap PosBody() => new OSDMap { ["request"] = OSD.FromString("peer_pos"), ["room"] = OSD.FromInteger(7) };

        [Test]
        public async Task AnAppliedReply_IsCountedApplied_AndLogsNothing()
        {
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, Inner("applied")));

            Assert.That(await sink.SendPositionsAsync(PosBody()), Is.True);

            Assert.That(sink.Replies.Counts.Applied, Is.EqualTo(1));
            Assert.That(sink.Replies.Counts.Refused, Is.EqualTo(0));
            Assert.That(_log.AtOrAbove(LogLevel.Information, "peer_pos"), Is.Empty);
        }

        [TestCase("stale_seq")]
        [TestCase("unknown_room")]
        [TestCase("too_big")]
        [TestCase("malformed")]
        [TestCase("stale_epoch")]
        public async Task ARefusal_IsCountedByReason_AndTheRequestIsStillSentAsFarAsTheTransportGoes(string reason)
        {
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, Inner("error", reason)));

            Assert.That(await sink.SendPositionsAsync(PosBody()), Is.True, "the transport carried it");

            PositionReplyCounts c = sink.Replies.Counts;
            Assert.That(c.Applied, Is.EqualTo(0));
            Assert.That(c.Refused, Is.EqualTo(1));
            Assert.That((c.StaleSeq, c.UnknownRoom, c.TooBig, c.Other), Is.EqualTo(reason switch
            {
                "stale_seq" => (1L, 0L, 0L, 0L),
                "unknown_room" => (0L, 1L, 0L, 0L),
                "too_big" => (0L, 0L, 1L, 0L),
                _ => (0L, 0L, 0L, 1L),
            }));
        }

        [Test]
        public async Task ATransportFailure_AndAnEmptyBody_AreNotCounted()
        {
            int n = 0;
            using JanusPeerCtlBatchSink sink = Sink(_ => n++ == 0 ? (AdminSendResult.TransportError, string.Empty) : (AdminSendResult.Ok, string.Empty));

            Assert.That(await sink.SendPositionsAsync(PosBody()), Is.False);
            Assert.That(await sink.SendPositionsAsync(PosBody()), Is.True);

            Assert.That(sink.Replies.Counts.Total, Is.EqualTo(0));
        }

        [Test]
        public async Task TwentyRefusalsInTenSeconds_GiveOneWarning_ThenOneMoreWithTheCount()
        {
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, Inner("error", "stale_seq")));

            for (int i = 0; i < 20; i++)
            {
                _now += 500;
                await sink.SendPositionsAsync(PosBody());
            }

            List<string> lines = _log.AtOrAbove(LogLevel.Warning, "refused a peer_pos");
            Assert.That(lines, Has.Count.EqualTo(1), string.Join(" | ", lines));
            Assert.That(lines[0], Does.Contain("stale_seq").And.Contain("room 7"));
            Assert.That(sink.Replies.Counts.StaleSeq, Is.EqualTo(20), "every one is still counted");

            _now += 60_000;
            await sink.SendPositionsAsync(PosBody());

            lines = _log.AtOrAbove(LogLevel.Warning, "refused a peer_pos");
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines[1], Does.Contain("19 more"));
        }

        // ---- the pump's one-minute summary ---------------------------------------------------------

        private sealed class World : IPositionWorld
        {
            public IReadOnlyList<PositionView> SnapshotPositions() => new[] { new PositionView(A, false, 100, 200, 300, 0) };
        }

        private sealed class Sink3 : IPeerPosSink
        {
            public int MixerVisProtocol => 3;
            public PositionReplyStats Replies { get; } = new PositionReplyStats();
            public bool MixerProtocolKnown => true;
            public Task<bool> CheckMixerProtocolAsync() => Task.FromResult(true);
            public Task<bool> SendPositionsAsync(OSDMap body) => Task.FromResult(true);
        }

        [Test]
        public async Task TheSummary_IsOneDebugLineAMinute_WithTheRepliesOfThatMinute()
        {
            var sink = new Sink3();
            var pump = new VoicePositionPump(new World(), sink, new PositionSettings(), _ => 7, () => 100000, "Test Region", () => _now);

            await pump.TickAsync();   // the first full snapshot goes out
            sink.Replies.Record(PositionReplyKind.Applied);
            sink.Replies.Record(PositionReplyKind.Applied);
            sink.Replies.Record(PositionReplyKind.StaleSeq);
            for (int i = 0; i < 120; i++)
            {
                _now += 500;
                await pump.TickAsync();
            }

            List<string> lines = _log.Where(LogLevel.Debug, "position summary");
            Assert.That(lines, Has.Count.EqualTo(1), string.Join(" | ", lines));
            Assert.That(lines[0], Does.Contain("2 applied").And.Contain("1 refused").And.Contain("stale_seq 1"));
            Assert.That(_log.AtOrAbove(LogLevel.Information, "position summary"), Is.Empty);

            // a minute with nothing in it writes nothing: the keep-alive snapshots still go out, but the mixer said nothing new
            // and the counts did not move, so only the sent figure is left to report
            for (int i = 0; i < 120; i++)
            {
                _now += 500;
                await pump.TickAsync();
            }
            lines = _log.Where(LogLevel.Debug, "position summary");
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines[1], Does.Contain("0 applied").And.Contain("0 refused"));
        }

        [Test]
        public async Task AnIdleFeed_WritesNoSummary()
        {
            var sink = new Sink3();
            var pump = new VoicePositionPump(new EmptyWorld(), sink, new PositionSettings(), _ => 7, () => 100000, "Test Region", () => _now);

            for (int i = 0; i < 240; i++)
            {
                _now += 500;
                await pump.TickAsync();
            }

            Assert.That(_log.Where(LogLevel.Debug, "position summary"), Is.Empty);
        }

        private sealed class EmptyWorld : IPositionWorld
        {
            public IReadOnlyList<PositionView> SnapshotPositions() => Array.Empty<PositionView>();
        }

        // ---- the console ---------------------------------------------------------------------------

        [Test]
        public async Task ShowVoiceVisibility_ReportsTheReplyCounts_WhenThePositionFeedIsOn()
        {
            int n = 0;
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, n++ < 2 ? Inner("applied") : Inner("error", "too_big")));
            for (int i = 0; i < 3; i++)
                await sink.SendPositionsAsync(PosBody());

            IReadOnlyList<string> off = VoiceVisibilityCommands.Format("Test Region", sink, 0);
            IReadOnlyList<string> on = VoiceVisibilityCommands.Format("Test Region", sink, 0, positionFeed: true);

            Assert.That(off.Any(l => l.Contains("position")), Is.False, "nothing about positions while the feed is off");
            string line = on.Single(l => l.Contains("position replies"));
            Assert.That(line, Does.Contain("2 applied").And.Contain("1 refused").And.Contain("too_big 1"));
        }
    }
}
