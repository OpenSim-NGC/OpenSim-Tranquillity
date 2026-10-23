/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The line a Janus session writes when its long poll ends is a Debug line: it ends for every participant who leaves
 * voice or logs out, and says nothing the leave path does not.
 */

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class LongPollExitLogTests
    {
        // Answers create and destroy, and ends the long poll at once with a 404.
        private sealed class EndingJanus : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
                var body = OSDParser.DeserializeJson(await request.Content.ReadAsStringAsync(ct)) as OSDMap;
                string txn = body["transaction"].AsString();
                string data = body["janus"].AsString() == "create" ? ",\"data\":{\"id\":4242}" : string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"" + data + "}", Encoding.UTF8, "application/json")
                };
            }
        }

        [Test]
        public async Task TheLongPollEnding_IsLoggedAtDebug_AndNotAboveIt()
        {
            using LogCapture log = new LogCapture();
            using var session = new JanusSession("http://janus.test/voice", "tok", "http://janus.test/admin", "tok", false, new EndingJanus());

            Assert.That(await session.CreateSession(), Is.True);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!log.Lines.Any(l => l.Text.Contains("Exiting long poll loop")) && clock.ElapsedMilliseconds < 8000)
                await Task.Delay(20);

            // At least one: a session another test left behind may end during the same moment.
            Assert.That(log.At(LogLevel.Debug, "Exiting long poll loop").Count, Is.GreaterThanOrEqualTo(1), "written, at Debug");
            Assert.That(log.AtInfoOrAbove("Exiting long poll loop"), Is.Empty, "and not at Information or above");
        }
    }
}
