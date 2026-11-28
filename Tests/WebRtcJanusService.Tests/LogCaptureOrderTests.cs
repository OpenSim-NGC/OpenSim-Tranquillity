/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Captures whose lifetimes overlap, as they do when a timed-out test's teardown runs after the next test has set
 * up (an uncooperative timed-out test can tear down late). Whatever order the captures are
 * disposed in, logging keeps working and each line lands in the capture that is current when it is logged.
 */

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    public class LogCaptureOrderTests
    {
        [Fact(Timeout = 60000)]
        public void CapturesDisposedOutOfOrder_LeaveLoggingWorking()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var a = new LogCapture();
            var b = new LogCapture();
            a.Dispose();
            b.Dispose();

            ILogger logger = LoggerProvider.CreateLogger("osWebRtcVoice.Tests.LogCaptureOrder");
            {
                var assertedValue = () => logger.LogInformation("after both captures are gone");
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }

            using var c = new LogCapture();
            logger.LogInformation("seen by a new capture");
            Assert.Contains("seen by a new capture", c.Lines.Select(l => l.Text));
        }

        [Fact(Timeout = 60000)]
        public void ALateDisposeOfAnEarlierCapture_DoesNotStealTheCurrentOnesLines()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            ILogger logger = LoggerProvider.CreateLogger("osWebRtcVoice.Tests.LogCaptureOrder");
            var a = new LogCapture();
            using var b = new LogCapture();
            a.Dispose();

            logger.LogInformation("logged while the second capture is current");

            Assert.Contains("logged while the second capture is current", b.Lines.Select(l => l.Text));
            Assert.Empty(a.Lines);
        }
    }
}
