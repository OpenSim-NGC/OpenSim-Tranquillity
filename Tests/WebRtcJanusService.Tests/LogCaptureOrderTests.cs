/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Captures whose lifetimes overlap, as they do when a timed-out test's teardown runs after the next test has set
 * up (NUnit's [Timeout] does not stop a test on .NET, so its teardown runs late). Whatever order the captures are
 * disposed in, logging keeps working and each line lands in the capture that is current when it is logged.
 */

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // captures register with the process-wide logger
    public class LogCaptureOrderTests
    {
        [Test]
        public void CapturesDisposedOutOfOrder_LeaveLoggingWorking()
        {
            var a = new LogCapture();
            var b = new LogCapture();
            a.Dispose();
            b.Dispose();

            ILogger logger = LoggerProvider.CreateLogger("osWebRtcVoice.Tests.LogCaptureOrder");
            Assert.DoesNotThrow(() => logger.LogInformation("after both captures are gone"));

            using var c = new LogCapture();
            logger.LogInformation("seen by a new capture");
            Assert.That(c.Lines.Select(l => l.Text), Does.Contain("seen by a new capture"));
        }

        [Test]
        public void ALateDisposeOfAnEarlierCapture_DoesNotStealTheCurrentOnesLines()
        {
            ILogger logger = LoggerProvider.CreateLogger("osWebRtcVoice.Tests.LogCaptureOrder");
            var a = new LogCapture();
            using var b = new LogCapture();
            a.Dispose();

            logger.LogInformation("logged while the second capture is current");

            Assert.That(b.Lines.Select(l => l.Text), Does.Contain("logged while the second capture is current"));
            Assert.That(a.Lines, Is.Empty);
        }
    }
}
