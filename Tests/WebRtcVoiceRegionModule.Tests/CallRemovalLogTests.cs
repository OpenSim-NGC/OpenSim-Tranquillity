/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What a removed member's viewer, retrying its provision, writes to the log: one line for the first refusal, nothing for
 * the retries, one line with the count when the removal is lifted. Counted with a test appender.
 */
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using static osWebRtcVoice.Tests.CallModerationModuleTests;
using static osWebRtcVoice.Tests.CallRemovalModuleTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class CallRemovalLogTests
    {
        private LogCapture _log;

        [SetUp]
        public void SetUp() => _log = new LogCapture();

        [TearDown]
        public void TearDown() => _log.Dispose();

        private System.Collections.Generic.List<LogCapture.Line> RefusalLines()
            => _log.AtInfoOrAbove("provision from").Where(l => l.Text.Contains(Member.ToString())).ToList();

        [Test]
        public void AViewerThatKeepsRetryingAfterARemoval_WritesOneLine_NotOnePerWindow()
        {
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");

            for (int i = 0; i < 300; i++)
                Assert.That(Provision(r, Member, out _), Is.EqualTo((int)HttpStatusCode.Forbidden));

            Assert.That(RefusalLines().Count, Is.EqualTo(1), "the first refusal; the 299 retries add nothing");
            Assert.That(RefusalLines()[0].Text, Does.Contain(Member.ToString()));
        }

        [Test]
        public void ReadmittingTheMember_EndsTheEpisode_WithOneLineThatCarriesTheNumberOfRefusals()
        {
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            for (int i = 0; i < 300; i++)
                Provision(r, Member, out _);

            r.Module.ReadmitToCall(GroupId, Officer, Member);

            var lines = RefusalLines();
            Assert.That(lines.Count, Is.EqualTo(2), "the first refusal and the end");
            Assert.That(lines[1].Text, Does.Contain("300 times"));
        }
    }
}
