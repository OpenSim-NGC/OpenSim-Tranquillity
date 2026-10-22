/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The stock AudioBridge plugin pushes a room event (another participant joined, finished setup, left) to every
 * participant's Janus handle with no transaction. No request is waiting for it, and that is normal, so it is logged at
 * Debug. A reply that names a transaction nobody waits for, or an event that reports an error, is still an Error.
 *
 * U1-U3 test the classification. U4 and U5 run a real long poll through a JanusSession and read the log it writes.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class UnclaimedEventLogTests
    {
        private const string RoomBroadcast =
            "{\"janus\":\"event\",\"session_id\":4242,\"sender\":7,\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\"," +
            "\"data\":{\"audiobridge\":\"event\",\"room\":1000,\"participants\":[{\"id\":5,\"display\":\"x\",\"setup\":true,\"muted\":false}]}}}";

        private const string LateReply =
            "{\"janus\":\"event\",\"transaction\":\"abc123\",\"session_id\":4242,\"sender\":7,\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\"," +
            "\"data\":{\"audiobridge\":\"joined\",\"room\":1000,\"id\":5}}}";

        private const string ErrorBroadcast =
            "{\"janus\":\"event\",\"session_id\":4242,\"sender\":7,\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\"," +
            "\"data\":{\"audiobridge\":\"event\",\"error_code\":486,\"error\":\"Room already exists\"}}}";

        private static JanusMessageResp Parse(string json) => JanusMessageResp.FromJson(json);

        [Test]
        public void U1_ARoomBroadcast_WithNoTransaction_IsDebug()
        {
            Assert.That(JanusSession.UnclaimedEventLevel(Parse(RoomBroadcast)), Is.EqualTo(LogLevel.Debug));
        }

        [Test]
        public void U2_AReplyNamingATransactionNobodyWaitsFor_StaysError()
        {
            Assert.That(JanusSession.UnclaimedEventLevel(Parse(LateReply)), Is.EqualTo(LogLevel.Error));
        }

        [Test]
        public void U3_AnEventReportingAnError_StaysError()
        {
            Assert.That(JanusSession.UnclaimedEventLevel(Parse(ErrorBroadcast)), Is.EqualTo(LogLevel.Error));
        }

        // ---- through a real long poll --------------------------------------------------------------------

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
            public List<(LogLevel Level, string Text)> Matching(string text)
            {
                lock (Lines)
                    return Lines.Where(l => l.Text.Contains(text)).ToList();
            }
        }

        private sealed class PollHandler : HttpMessageHandler
        {
            private readonly Channel<string> _gets = Channel.CreateUnbounded<string>();
            public void Queue(string body) => _gets.Writer.TryWrite(body);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Get)
                {
                    try { return Json(await _gets.Reader.ReadAsync(ct)); }
                    catch (OperationCanceledException) { throw new TaskCanceledException(); }
                }
                var body = OSDParser.DeserializeJson(await request.Content.ReadAsStringAsync(ct)) as OSDMap;
                string txn = body["transaction"].AsString();
                return body["janus"].AsString() == "create"
                    ? Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":4242}}")
                    : Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
            }

            private static HttpResponseMessage Json(string body) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private Capture _log;
        private ILoggerFactory _previous;

        [SetUp]
        public void SetUp()
        {
            _log = new Capture();
            _previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
        }

        [TearDown]
        public void TearDown()
        {
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
            _log.Dispose();
        }

        private async Task<List<(LogLevel Level, string Text)>> LoggedFor(string pEvent)
        {
            var handler = new PollHandler();
            var session = new JanusSession("http://janus.test/voice", "tok", "http://janus.test/admin", "tok", false, handler);
            try
            {
                Assert.That(await session.CreateSession(), Is.True);
                handler.Queue(pEvent);
                DateTime stop = DateTime.UtcNow.AddSeconds(10);
                List<(LogLevel Level, string Text)> seen;
                while ((seen = _log.Matching("no outstanding request")).Count == 0 && DateTime.UtcNow < stop)
                    await Task.Delay(20);
                return seen;
            }
            finally
            {
                await session.DestroySession();
                session.Dispose();
            }
        }

        [Test]
        public async Task U4_ARoomBroadcastThroughALongPoll_IsLoggedAtDebug_NotError()
        {
            List<(LogLevel Level, string Text)> seen = await LoggedFor(RoomBroadcast);

            Assert.That(seen, Is.Not.Empty, "the event reached the long poll's handler");
            Assert.That(seen.Select(l => l.Level), Is.All.EqualTo(LogLevel.Debug));
        }

        [Test]
        public async Task U5_ALateReplyThroughALongPoll_IsStillLoggedAtError()
        {
            List<(LogLevel Level, string Text)> seen = await LoggedFor(LateReply);

            Assert.That(seen, Is.Not.Empty);
            Assert.That(seen.Select(l => l.Level), Is.All.EqualTo(LogLevel.Error));
        }
    }
}
