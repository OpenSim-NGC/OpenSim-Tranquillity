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
    public class PositionReplyTests : IAsyncLifetime
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
        private LogCapture _logging;
        private long _now;
        private void SetUp()
        {
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
            _now = 10_000;
        }
        private void TearDown()
        {
            _logging.Dispose();
            _log.Dispose();
        }

        // ---- the mapping --------------------------------------------------------------------------

        [InlineData("applied",null,PositionReplyKind.Applied)]
        [InlineData("error","stale_seq",PositionReplyKind.StaleSeq)]
        [InlineData("error","unknown_room",PositionReplyKind.UnknownRoom)]
        [InlineData("error","too_big",PositionReplyKind.TooBig)]
        [InlineData("error","malformed",PositionReplyKind.Other)]
        [InlineData("error","stale_epoch",PositionReplyKind.Other)]
        [InlineData("error",null,PositionReplyKind.Other)]
        [InlineData("empty",null,PositionReplyKind.Other)]
        [InlineData(null,null,PositionReplyKind.Other)]
        [Theory(Timeout = 60000)]
        public void AnInnerReply_IsClassifiedByItsStatusAndReason(string status, string reason, PositionReplyKind expected)

            {
    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    Assert.Equal(expected, PositionReply.Classify(true, status, reason));
}
        [Fact(Timeout = 60000)]
        public void NoInnerReply_IsNotClassified()

            {
    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    Assert.Equal(PositionReplyKind.None, PositionReply.Classify(false, null, null));
}
        // ---- the sink counts what the mixer answered ----------------------------------------------

        private static string Inner(string status, string reason = null)
            => "{\"janus\":\"success\",\"response\":{\"slvoice\":\"" + status + "\"" +
               (reason != null ? ",\"reason\":\"" + reason + "\"" : ",\"op\":\"pos\",\"room\":7,\"entries\":1,\"skipped\":0,\"dropped\":0") +
               ",\"vis_protocol\":3}}";

        private JanusPeerCtlBatchSink Sink(Func<OSDMap, (AdminSendResult, string)> reply)
            => new JanusPeerCtlBatchSink("http://localhost/voiceAdmin", "secret", TimeSpan.FromSeconds(5), UUID.Random(), "Test Region", 4,
                req => Task.FromResult(reply(req)), nowMs: () => _now);

        private static OSDMap PosBody() => new OSDMap { ["request"] = OSD.FromString("peer_pos"), ["room"] = OSD.FromInteger(7) };

        [Fact(Timeout = 60000)]
        public async Task AnAppliedReply_IsCountedApplied_AndLogsNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, Inner("applied")));

            Assert.True(await sink.SendPositionsAsync(PosBody()));

            Assert.Equal(1, sink.Replies.Counts.Applied);
            Assert.Equal(0, sink.Replies.Counts.Refused);
            Assert.Empty(_log.AtOrAbove(LogLevel.Information, "peer_pos"));
        }

        [InlineData("stale_seq")]
        [InlineData("unknown_room")]
        [InlineData("too_big")]
        [InlineData("malformed")]
        [InlineData("stale_epoch")]
        [Theory(Timeout = 60000)]
        public async Task ARefusal_IsCountedByReason_AndTheRequestIsStillSentAsFarAsTheTransportGoes(string reason)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, Inner("error", reason)));

            Assert.True((await sink.SendPositionsAsync(PosBody())), "the transport carried it");

            PositionReplyCounts c = sink.Replies.Counts;
            Assert.Equal(0, c.Applied);
            Assert.Equal(1, c.Refused);
            Assert.Equal(reason switch
            {
                "stale_seq" => (1L, 0L, 0L, 0L),
                "unknown_room" => (0L, 1L, 0L, 0L),
                "too_big" => (0L, 0L, 1L, 0L),
                _ => (0L, 0L, 0L, 1L),
            }, (c.StaleSeq, c.UnknownRoom, c.TooBig, c.Other));
        }

        [Fact(Timeout = 60000)]
        public async Task ATransportFailure_AndAnEmptyBody_AreNotCounted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            int n = 0;
            using JanusPeerCtlBatchSink sink = Sink(_ => n++ == 0 ? (AdminSendResult.TransportError, string.Empty) : (AdminSendResult.Ok, string.Empty));

            Assert.False(await sink.SendPositionsAsync(PosBody()));
            Assert.True(await sink.SendPositionsAsync(PosBody()));

            Assert.Equal(0, sink.Replies.Counts.Total);
        }

        [Fact(Timeout = 60000)]
        public async Task TwentyRefusalsInTenSeconds_GiveOneWarning_ThenOneMoreWithTheCount()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, Inner("error", "stale_seq")));

            for (int i = 0; i < 20; i++)
            {
                _now += 500;
                await sink.SendPositionsAsync(PosBody());
            }

            List<string> lines = _log.AtOrAbove(LogLevel.Warning, "refused a peer_pos");
            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), string.Join(" | ", lines));
            }
            {
                var assertedValue = lines[0];
                Assert.Contains("stale_seq", assertedValue);
                Assert.Contains("room 7", assertedValue);
            }
            {
                var assertedValue = sink.Replies.Counts.StaleSeq;
                TestAssert.WithMessage(() => Assert.Equal(20, assertedValue), "every one is still counted");
            }

            _now += 60_000;
            await sink.SendPositionsAsync(PosBody());

            lines = _log.AtOrAbove(LogLevel.Warning, "refused a peer_pos");
            Assert.Equal(2, lines.Count());
            Assert.Contains("19 more", lines[1]);
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

        [Fact(Timeout = 60000)]
        public async Task TheSummary_IsOneDebugLineAMinute_WithTheRepliesOfThatMinute()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), string.Join(" | ", lines));
            }
            {
                var assertedValue = lines[0];
                Assert.Contains("2 applied", assertedValue);
                Assert.Contains("1 refused", assertedValue);
                Assert.Contains("stale_seq 1", assertedValue);
            }
            Assert.Empty(_log.AtOrAbove(LogLevel.Information, "position summary"));

            // a minute with nothing in it writes nothing: the keep-alive snapshots still go out, but the mixer said nothing new
            // and the counts did not move, so only the sent figure is left to report
            for (int i = 0; i < 120; i++)
            {
                _now += 500;
                await pump.TickAsync();
            }
            lines = _log.Where(LogLevel.Debug, "position summary");
            Assert.Equal(2, lines.Count());
            {
                var assertedValue = lines[1];
                Assert.Contains("0 applied", assertedValue);
                Assert.Contains("0 refused", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public async Task AnIdleFeed_WritesNoSummary()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var sink = new Sink3();
            var pump = new VoicePositionPump(new EmptyWorld(), sink, new PositionSettings(), _ => 7, () => 100000, "Test Region", () => _now);

            for (int i = 0; i < 240; i++)
            {
                _now += 500;
                await pump.TickAsync();
            }

            Assert.Empty(_log.Where(LogLevel.Debug, "position summary"));
        }

        private sealed class EmptyWorld : IPositionWorld
        {
            public IReadOnlyList<PositionView> SnapshotPositions() => Array.Empty<PositionView>();
        }

        // ---- the console ---------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public async Task ShowVoiceVisibility_ReportsTheReplyCounts_WhenThePositionFeedIsOn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            int n = 0;
            using JanusPeerCtlBatchSink sink = Sink(_ => (AdminSendResult.Ok, n++ < 2 ? Inner("applied") : Inner("error", "too_big")));
            for (int i = 0; i < 3; i++)
                await sink.SendPositionsAsync(PosBody());

            IReadOnlyList<string> off = VoiceVisibilityCommands.Format("Test Region", sink, 0);
            IReadOnlyList<string> on = VoiceVisibilityCommands.Format("Test Region", sink, 0, positionFeed: true);

            Assert.False(off.Any(l => l.Contains("position")), "nothing about positions while the feed is off");
            string line = on.Single(l => l.Contains("position replies"));
            {
                var assertedValue = line;
                Assert.Contains("2 applied", assertedValue);
                Assert.Contains("1 refused", assertedValue);
                Assert.Contains("too_big 1", assertedValue);
            }
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
