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
using Xunit;
using static osWebRtcVoice.Tests.CallModerationModuleTests;
using static osWebRtcVoice.Tests.CallRemovalModuleTests;

namespace osWebRtcVoice.Tests
{
    public class CallRemovalLogTests : IAsyncLifetime
        {
        private LogCapture _log;
        private void SetUp() => _log = new LogCapture();
        private void TearDown() => _log.Dispose();

        private System.Collections.Generic.List<LogCapture.Line> RefusalLines()
            => _log.AtInfoOrAbove("provision from").Where(l => l.Text.Contains(Member.ToString())).ToList();

        [Fact(Timeout = 60000)]
        public void AViewerThatKeepsRetryingAfterARemoval_WritesOneLine_NotOnePerWindow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");

            for (int i = 0; i < 300; i++)
                Assert.Equal((int)HttpStatusCode.Forbidden, Provision(r, Member, out _));

            {
                var assertedValue = RefusalLines().Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the first refusal; the 299 retries add nothing");
            }
            Assert.Contains(Member.ToString(), RefusalLines()[0].Text);
        }

        [Fact(Timeout = 60000)]
        public void ReadmittingTheMember_EndsTheEpisode_WithOneLineThatCarriesTheNumberOfRefusals()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using Region r = InACall();
            r.Module.RemoveFromCall(GroupId, Officer, Member, "x");
            for (int i = 0; i < 300; i++)
                Provision(r, Member, out _);

            r.Module.ReadmitToCall(GroupId, Officer, Member);

            var lines = RefusalLines();
            {
                var assertedValue = lines.Count;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the first refusal and the end");
            }
            Assert.Contains("300 times", lines[1].Text);
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
