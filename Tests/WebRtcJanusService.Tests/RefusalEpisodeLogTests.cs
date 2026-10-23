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
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class RefusalEpisodeLogTests
    {
        private static readonly UUID Agent = new UUID("aaaaaaaa-1111-1111-1111-0000000d0072");
        private static readonly UUID Other = new UUID("aaaaaaaa-2222-2222-2222-0000000d0072");
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000d0072");

        private LogCapture _log;
        private long _now;

        [SetUp]
        public void SetUp()
        {
            _log = new LogCapture();
            _now = 1_000_000;
        }

        [TearDown]
        public void TearDown() => _log.Dispose();

        private RefusalEpisodeLog New(int windowSeconds = 5)
            => new RefusalEpisodeLog(LoggerProvider(), "[TEST]", TimeSpan.FromSeconds(windowSeconds), null, () => _now);

        private static ILogger LoggerProvider() => OpenSim.Framework.LoggerProvider.CreateLogger("osWebRtcVoice.Tests.RefusalEpisodeLog");

        // A viewer retrying for a while: the cache records a fresh refusal each window and answers the rest from memory.
        private static void Retry(RefusalEpisodeLog log, UUID agent, int replays, ref long now, int windowSeconds = 5)
        {
            int suppressed = log.Cache.RecordRefusal(agent, Region, "no");
            log.Refused(agent, Region, " in \"R\"", string.Empty, suppressed);
            for (int i = 0; i < replays; i++)
                Assert.That(log.Cache.TryGetRefusal(agent, Region, out _), Is.True);
            now += windowSeconds * 1000 + 1;
        }

        [Test]
        public void TheFirstRefusal_IsLogged_AndNothingElseUntilTheEpisodeEnds()
        {
            RefusalEpisodeLog log = New();

            for (int window = 0; window < 12; window++)   // a minute of a viewer retrying at its usual rate
                Retry(log, Agent, 9, ref _now);

            Assert.That(_log.AtInfoOrAbove("[TEST]").Count, Is.EqualTo(1));
            Assert.That(_log.AtInfoOrAbove("[TEST]")[0].Text, Does.Contain("refused").And.Contain(Agent.ToString()));
            Assert.That(log.Open, Is.EqualTo(1));
        }

        [Test]
        public void WhenTheViewerIsAdmitted_OneLineSaysHowOftenItWasRefused()
        {
            RefusalEpisodeLog log = New();
            for (int window = 0; window < 3; window++)
                Retry(log, Agent, 9, ref _now);   // 3 x (1 fresh + 9 replays)
            _now -= 4000;                         // the last window is still open: its replays are in the cache

            log.Ended(Agent, Region, "it was admitted");

            var lines = _log.AtInfoOrAbove("[TEST]");
            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[1].Text, Does.Contain("30 times").And.Contain("it was admitted"));
            Assert.That(log.Open, Is.EqualTo(0));
            Assert.That(log.Cache.TryGetRefusal(Agent, Region, out _), Is.False, "the cached refusal went with the episode");
        }

        [Test]
        public void ARefusalOnItsOwn_WritesTheFirstLineOnly()
        {
            RefusalEpisodeLog log = New();
            Retry(log, Agent, 0, ref _now);

            log.Ended(Agent, Region);

            Assert.That(_log.AtInfoOrAbove("[TEST]").Count, Is.EqualTo(1));
        }

        [Test]
        public void AfterAQuietMinute_TheEpisodeIsClosed_WithItsCount_AndTheNextRefusalStartsAnotherOne()
        {
            RefusalEpisodeLog log = New();
            for (int window = 0; window < 3; window++)
                Retry(log, Agent, 9, ref _now);
            _now -= 4000;

            _now += 58_000;   // 59 s since the last refusal
            Assert.That(log.Sweep(), Is.EqualTo(0), "not yet quiet for a minute");
            _now += 2_000;    // 61 s
            Assert.That(log.Sweep(), Is.EqualTo(1));

            var lines = _log.AtInfoOrAbove("[TEST]");
            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[1].Text, Does.Contain("30 times").And.Contain("no retry for 60 s"));
            Assert.That(log.Sweep(), Is.EqualTo(0), "closed once");

            Retry(log, Agent, 0, ref _now);
            Assert.That(_log.AtInfoOrAbove("[TEST]").Count, Is.EqualTo(3), "a refusal after the end is a new episode");
        }

        [Test]
        public void EachViewerHasItsOwnEpisode()
        {
            RefusalEpisodeLog log = New();

            Retry(log, Agent, 5, ref _now);
            Retry(log, Other, 5, ref _now);
            Retry(log, Agent, 5, ref _now);

            Assert.That(_log.AtInfoOrAbove("[TEST]").Count, Is.EqualTo(2), "one first line for each");
            Assert.That(log.Open, Is.EqualTo(2));
        }

        [Test]
        public void WithTheCacheOff_NothingIsLogged()
        {
            RefusalEpisodeLog log = New(0);

            Retry(log, Agent, 0, ref _now, 0);
            log.Ended(Agent, Region);

            Assert.That(_log.AtInfoOrAbove("[TEST]"), Is.Empty);
            Assert.That(log.Open, Is.EqualTo(0));
        }

        [Test]
        public void AStartedLog_ClosesQuietEpisodesOnItsOwn()
        {
            using RefusalEpisodeLog log = new RefusalEpisodeLog(LoggerProvider(), "[TEST]", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            int suppressed = log.Cache.RecordRefusal(Agent, Region, "no");
            log.Refused(Agent, Region, " in \"R\"", string.Empty, suppressed);
            log.Cache.TryGetRefusal(Agent, Region, out _);
            log.Cache.TryGetRefusal(Agent, Region, out _);

            log.Start(TimeSpan.FromMilliseconds(50));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (log.Open > 0 && clock.ElapsedMilliseconds < 8000)
                Thread.Sleep(20);
            Assert.That(log.Open, Is.EqualTo(0), "the timer closed the episode");
            Assert.That(_log.AtInfoOrAbove("[TEST]").Last().Text, Does.Contain("3 times"));
        }
    }
}
