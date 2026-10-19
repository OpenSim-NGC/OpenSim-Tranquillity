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
    [TestFixture]
    [NonParallelizable]
    public class JanusPluginDetachTests
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

        private async Task<bool> DetachAnswered(string detachAnswer)
        {
            var fake = new FakeJanus { DetachAnswer = detachAnswer };
            using var session = new JanusSession("http://janus.test/voice", "tok", "http://janus.test/admin", "tok", false, fake);
            Assert.That(await session.CreateSession(), Is.True);
            var plugin = new JanusPlugin(session, "janus.plugin.slvoice");
            Assert.That(await plugin.Activate(null), Is.True, "the handle attached");

            bool detached = await plugin.Detach();

            await session.DestroySession();
            return detached;
        }

        [Test]
        public async Task ADetachForASessionJanusNoLongerHasIsLoggedAtDebug()
        {
            bool detached = await DetachAnswered(
                "{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":458,\"reason\":\"No such session 4242\"}}");

            Assert.That(detached, Is.False, "Janus did not confirm a detach");
            var lines = _log.Detach();
            Assert.That(lines.Where(l => l.Level >= LogLevel.Warning), Is.Empty, "a vanished session is not an error");
            Assert.That(lines.Any(l => l.Level == LogLevel.Debug && l.Text.Contains("session")), Is.True,
                "the case is still recorded, at Debug");
        }

        [TestCase("{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":459,\"reason\":\"No such handle 77\"}}")]
        [TestCase("{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":403,\"reason\":\"Unauthorized request\"}}")]
        [TestCase("{\"janus\":\"error\",\"transaction\":\"TXN\",\"error\":{\"code\":490,\"reason\":\"Something else\"}}")]
        [TestCase(null)]
        public async Task AnyOtherFailedDetachStaysAnError(string answer)
        {
            bool detached = await DetachAnswered(answer);

            Assert.That(detached, Is.False);
            Assert.That(_log.Detach().Any(l => l.Level == LogLevel.Error && l.Text.Contains("Detach: failed")), Is.True);
        }

        [Test]
        public async Task ASuccessfulDetachLogsNoWarningOrError()
        {
            bool detached = await DetachAnswered("{\"janus\":\"success\",\"transaction\":\"TXN\"}");

            Assert.That(detached, Is.True);
            Assert.That(_log.Detach().Where(l => l.Level >= LogLevel.Warning), Is.Empty);
        }
    }
}
