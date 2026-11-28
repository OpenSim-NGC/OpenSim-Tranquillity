/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * After a mixer restart a viewer session's Janus session is gone, so the detach sent while the region server tidies
 * up that viewer session is answered with Janus error 458 "No such session". That is expected, so it is logged at
 * Debug. Any other failed detach is a real problem and stays at Error.
 *
 * The session talks to a fake HttpMessageHandler; the ambient logger factory is process-wide, so the class runs
 * on its own.
 */

using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    public class JanusPluginDetachTests : IAsyncLifetime
        {
        private sealed class Capture : ILoggerProvider, ILogger
        {
            private readonly List<(LogLevel Level, string Text)> _lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (_lines)
                    _lines.Add((logLevel, formatter(state, exception)));
            }
            public void Dispose() { }
            public List<(LogLevel Level, string Text)> Detach()
            {
                lock (_lines)
                    return _lines.Where(l => l.Text.Contains("Detach")).ToList();
            }
        }

        // create and attach succeed; the answer to "detach" is whatever the test sets; the long poll waits.
        private sealed class FakeJanus : HttpMessageHandler
        {
            private readonly Channel<string> _never = Channel.CreateUnbounded<string>();
            public string DetachAnswer;   // a Janus JSON body with "TXN" where the transaction goes; null = HTTP 503

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Get)
                {
                    try
                    {
                        await _never.Reader.ReadAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TaskCanceledException();
                    }
                    return Json("{}");
                }
                var body = OSDParser.DeserializeJson(await request.Content.ReadAsStringAsync(ct)) as OSDMap;
                string txn = body["transaction"].AsString();
                switch (body["janus"].AsString())
                {
                    case "create":
                        return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":4242}}");
                    case "attach":
                        return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":77}}");
                    case "detach":
                        return DetachAnswer is null
                            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                            : Json(DetachAnswer.Replace("TXN", txn));
                    default:
                        return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
                }
            }

            private static HttpResponseMessage Json(string body) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private Capture _log;
        private LogCapture _logging;
        private void SetUp()
        {
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
        }
        private void TearDown()
        {
            _logging.Dispose();
            _log.Dispose();
        }

        private async Task<bool> DetachAnswered(string detachAnswer)
        {
            var fake = new FakeJanus { DetachAnswer = detachAnswer };
            using var session = new JanusSession("http://janus.test/voice", "tok", "http://janus.test/admin", "tok", false, fake);
            Assert.True(await session.CreateSession());
            var plugin = new JanusPlugin(session, "janus.plugin.slvoice");
            Assert.True((await plugin.Activate(null)), "the handle attached");

            bool detached = await plugin.Detach();

            await session.DestroySession();
            return detached;
        }

        [Fact(Timeout = 60000)]
        public async Task ADetachForASessionJanusNoLongerHasIsLoggedAtDebug()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool detached = await DetachAnswered(
                "{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":458,\"reason\":\"No such session 4242\"}}");

            Assert.False(detached, "Janus did not confirm a detach");
            var lines = _log.Detach();
            {
                var assertedValue = lines.Where(l => l.Level >= LogLevel.Warning);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a vanished session is not an error");
            }
            Assert.True(lines.Any(l => l.Level == LogLevel.Debug && l.Text.Contains("session")), "the case is still recorded, at Debug");
        }

        [Fact(Timeout = 60000)]
        public async Task ADetachForAHandleJanusAlreadyDroppedIsDebugNotAnError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool detached = await DetachAnswered(
                "{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":459,\"reason\":\"No such handle 77\"}}");

            Assert.False(detached, "Janus did not confirm a detach");
            var lines = _log.Detach();
            {
                var assertedValue = lines.Where(l => l.Level >= LogLevel.Warning);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a handle that is already gone is a race, not an error");
            }
            Assert.True(lines.Any(l => l.Level == LogLevel.Debug && l.Text.Contains("handle")), "the case is still recorded, at Debug");
        }

        [InlineData("{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":403,\"reason\":\"Unauthorized request\"}}")]
        [InlineData("{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":490,\"reason\":\"Something else\"}}")]
        [InlineData(null)]
        [Theory(Timeout = 60000)]
        public async Task AnyOtherFailedDetachStaysAnError(string answer)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool detached = await DetachAnswered(answer);

            Assert.False(detached);
            Assert.Contains(_log.Detach(), l => l.Level == LogLevel.Error && l.Text.Contains("Detach: failed"));
        }

        [Fact(Timeout = 60000)]
        public async Task ASuccessfulDetachLogsNoWarningOrError()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            bool detached = await DetachAnswered("{\"janus\":\"success\",\"transaction\":\"TXN\"}");

            Assert.True(detached);
            Assert.DoesNotContain(_log.Detach(), l => l.Level >= LogLevel.Warning);
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
