/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The refusal episode log on its own, with a test clock: one line when an episode begins, one with the count when it
 * ends (admitted, logged out, let back in, or no retry for a minute), and nothing in between.
 */

using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace osWebRtcVoice.Tests
{
    public class RefusalEpisodeLogTests : IAsyncLifetime
        {
        private static readonly UUID Agent = new UUID("aaaaaaaa-1111-1111-1111-0000000d0072");
        private static readonly UUID Other = new UUID("aaaaaaaa-2222-2222-2222-0000000d0072");
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000d0072");

        private LogCapture _log;
        private long _now;
        private void SetUp()
        {
            _log = new LogCapture();
            _now = 1_000_000;
        }
        private void TearDown() => _log.Dispose();

        private RefusalEpisodeLog New(int windowSeconds = 5)
            => new RefusalEpisodeLog(LoggerProvider(), "[TEST]", TimeSpan.FromSeconds(windowSeconds), null, () => _now);

        private static ILogger LoggerProvider() => OpenSim.Framework.LoggerProvider.CreateLogger("osWebRtcVoice.Tests.RefusalEpisodeLog");

        // A viewer retrying for a while: the cache records a fresh refusal each window and answers the rest from memory.
        private static void Retry(RefusalEpisodeLog log, UUID agent, int replays, ref long now, int windowSeconds = 5)
        {
            int suppressed = log.Cache.RecordRefusal(agent, Region, "no");
            log.Refused(agent, Region, " in \"R\"", string.Empty, suppressed);
            for (int i = 0; i < replays; i++)
                Assert.True(log.Cache.TryGetRefusal(agent, Region, out _));
            now += windowSeconds * 1000 + 1;
        }

        [Fact(Timeout = 60000)]
        public void TheFirstRefusal_IsLogged_AndNothingElseUntilTheEpisodeEnds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalEpisodeLog log = New();

            for (int window = 0; window < 12; window++)   // a minute of a viewer retrying at its usual rate
                Retry(log, Agent, 9, ref _now);

            Assert.Single(_log.AtInfoOrAbove("[TEST]"));
            {
                var assertedValue = _log.AtInfoOrAbove("[TEST]")[0].Text;
                Assert.Contains("refused", assertedValue);
                Assert.Contains(Agent.ToString(), assertedValue);
            }
            Assert.Equal(1, log.Open);
        }

        [Fact(Timeout = 60000)]
        public void WhenTheViewerIsAdmitted_OneLineSaysHowOftenItWasRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalEpisodeLog log = New();
            for (int window = 0; window < 3; window++)
                Retry(log, Agent, 9, ref _now);   // 3 x (1 fresh + 9 replays)
            _now -= 4000;                         // the last window is still open: its replays are in the cache

            log.Ended(Agent, Region, "it was admitted");

            var lines = _log.AtInfoOrAbove("[TEST]");
            Assert.Equal(2, lines.Count);
            {
                var assertedValue = lines[1].Text;
                Assert.Contains("30 times", assertedValue);
                Assert.Contains("it was admitted", assertedValue);
            }
            Assert.Equal(0, log.Open);
            Assert.False(log.Cache.TryGetRefusal(Agent, Region, out _), "the cached refusal went with the episode");
        }

        [Fact(Timeout = 60000)]
        public void ARefusalOnItsOwn_WritesTheFirstLineOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalEpisodeLog log = New();
            Retry(log, Agent, 0, ref _now);

            log.Ended(Agent, Region);

            Assert.Single(_log.AtInfoOrAbove("[TEST]"));
        }

        [Fact(Timeout = 60000)]
        public void AfterAQuietMinute_TheEpisodeIsClosed_WithItsCount_AndTheNextRefusalStartsAnotherOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalEpisodeLog log = New();
            for (int window = 0; window < 3; window++)
                Retry(log, Agent, 9, ref _now);
            _now -= 4000;

            _now += 58_000;   // 59 s since the last refusal
            {
                var assertedValue = log.Sweep();
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "not yet quiet for a minute");
            }
            _now += 2_000;    // 61 s
            Assert.Equal(1, log.Sweep());

            var lines = _log.AtInfoOrAbove("[TEST]");
            Assert.Equal(2, lines.Count);
            {
                var assertedValue = lines[1].Text;
                Assert.Contains("30 times", assertedValue);
                Assert.Contains("no retry for 60 s", assertedValue);
            }
            {
                var assertedValue = log.Sweep();
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "closed once");
            }

            Retry(log, Agent, 0, ref _now);
            {
                var assertedValue = _log.AtInfoOrAbove("[TEST]").Count;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "a refusal after the end is a new episode");
            }
        }

        [Fact(Timeout = 60000)]
        public void EachViewerHasItsOwnEpisode()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalEpisodeLog log = New();

            Retry(log, Agent, 5, ref _now);
            Retry(log, Other, 5, ref _now);
            Retry(log, Agent, 5, ref _now);

            {
                var assertedValue = _log.AtInfoOrAbove("[TEST]").Count;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "one first line for each");
            }
            Assert.Equal(2, log.Open);
        }

        [Fact(Timeout = 60000)]
        public void WithTheCacheOff_NothingIsLogged()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RefusalEpisodeLog log = New(0);

            Retry(log, Agent, 0, ref _now, 0);
            log.Ended(Agent, Region);

            Assert.Empty(_log.AtInfoOrAbove("[TEST]"));
            Assert.Equal(0, log.Open);
        }

        [Fact(Timeout = 60000)]
        public void AStartedLog_ClosesQuietEpisodesOnItsOwn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using RefusalEpisodeLog log = new RefusalEpisodeLog(LoggerProvider(), "[TEST]", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            int suppressed = log.Cache.RecordRefusal(Agent, Region, "no");
            log.Refused(Agent, Region, " in \"R\"", string.Empty, suppressed);
            log.Cache.TryGetRefusal(Agent, Region, out _);
            log.Cache.TryGetRefusal(Agent, Region, out _);

            log.Start(TimeSpan.FromMilliseconds(50));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (log.Open > 0 && clock.ElapsedMilliseconds < 8000)
                Thread.Sleep(20);
            {
                var assertedValue = log.Open;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the timer closed the episode");
            }
            Assert.Contains("3 times", _log.AtInfoOrAbove("[TEST]").Last().Text);
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
