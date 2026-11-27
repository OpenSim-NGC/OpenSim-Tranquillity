/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * With [JanusWebRtcVoice] MessageDetails on, the session logs each request it sends and
 * (here) the URL of each long poll. Neither line may carry the Janus API secret or the admin
 * secret. The long-poll GET still has to send the API secret in its query string, because the
 * Janus HTTP transport reads a GET's secret from there and nowhere else; only the log changes.
 *
 * The session talks to a fake HttpMessageHandler, so no Janus is needed. The tests swap the
 * ambient logger factory, which is process-wide state, so the class runs on its own.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    public class JanusSecretLoggingTests : IAsyncLifetime
        {
        private const string ApiSecret = "api-secret-0123456789abcdef0123456789abcdef";
        private const string AdminSecret = "admin-secret-0123456789abcdef0123456789abcd";

        private sealed class Capture : ILoggerProvider, ILogger
        {
            private readonly List<string> _lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                string text = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
                lock (_lines)
                    _lines.Add(text);
            }
            public void Dispose() { }
            public List<string> Lines()
            {
                lock (_lines)
                    return new List<string>(_lines);
            }
        }

        // Answers create/destroy with success, other POSTs with an ack, and holds each long-poll GET
        // until the session is torn down. Records the URL of every request.
        private sealed class FakeJanus : HttpMessageHandler
        {
            public readonly ConcurrentQueue<string> GetUrls = new();
            private readonly Channel<string> _never = Channel.CreateUnbounded<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Get)
                {
                    GetUrls.Enqueue(request.RequestUri.ToString());
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
                string janus = body["janus"].AsString();
                string txn = body["transaction"].AsString();
                return janus switch
                {
                    "create" => Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":4242}}"),
                    "destroy" => Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}"),
                    _ => Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}"),
                };
            }

            private static HttpResponseMessage Json(string body) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private Capture _log;
        private LogCapture _logging;
        private readonly List<JanusSession> _sessions = new();
        private void SetUp()
        {
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
        }
        private async Task TearDown()
        {
            foreach (JanusSession s in _sessions)
            {
                if (s.IsConnected)
                    await s.DestroySession();
                s.Dispose();
            }
            _sessions.Clear();
            _logging.Dispose();
            _log.Dispose();
        }

        private async Task<(JanusSession Session, FakeJanus Fake)> ConnectedSession(bool messageDetails)
        {
            var fake = new FakeJanus();
            var s = new JanusSession("http://janus.test/voice", ApiSecret, "http://janus.test/admin", AdminSecret,
                                     messageDetails, fake);
            _sessions.Add(s);
            Assert.True((await s.CreateSession()), "fake create succeeds and starts the long poll");
            return (s, fake);
        }

        private static async Task<string> WaitForLine(Capture log, string contains)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                string hit = log.Lines().FirstOrDefault(l => l.Contains(contains));
                if (hit is not null)
                    return hit;
                await Task.Delay(20);
            }
            return null;
        }

        private static void AssertNoSecrets(string text, string what)
        {
            {
                var assertedValue = text;
                TestAssert.WithMessage(() => Assert.DoesNotContain(ApiSecret, assertedValue), what + " must not carry the API secret");
            }
            {
                var assertedValue = text;
                TestAssert.WithMessage(() => Assert.DoesNotContain(AdminSecret, assertedValue), what + " must not carry the admin secret");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheBaseRequestLogFormHidesBothSecrets()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var req = new JanusMessageReq("keepalive");
            req.AddAPIToken(ApiSecret);
            req.RawBody["admin_secret"] = AdminSecret;

            string forLog = req.ToJsonForLog();

            AssertNoSecrets(forLog, "the log form of a request");
            {
                var assertedValue = forLog;
                TestAssert.WithMessage(() => Assert.Contains("keepalive", assertedValue), "the rest of the request is still logged");
            }
            {
                var assertedValue = forLog;
                TestAssert.WithMessage(() => Assert.Contains(req.TransactionId, assertedValue), "the transaction id stays: it is what an operator correlates on");
            }
            {
                var assertedValue = req.ToJson();
                TestAssert.WithMessage(() => Assert.Contains(ApiSecret, assertedValue), "the wire form still carries the API secret");
            }
            {
                var assertedValue = req.ToJson();
                TestAssert.WithMessage(() => Assert.Contains(AdminSecret, assertedValue), "the wire form still carries the admin secret");
            }
        }

        [Fact(Timeout = 60000)]
        public void ThePluginMessageLogFormHidesBothSecretsAndTheJoinCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            const string capability = "join-capability-token-value";
            var req = new PluginMsgReq(new OSDMap());
            req.AddStringToBody("join_cap", capability);
            req.AddAPIToken(ApiSecret);
            req.RawBody["admin_secret"] = AdminSecret;

            string forLog = req.ToJsonForLog();

            AssertNoSecrets(forLog, "the log form of a plugin message");
            {
                var assertedValue = forLog;
                TestAssert.WithMessage(() => Assert.DoesNotContain(capability, assertedValue), "the join capability stays hidden");
            }
            {
                var assertedValue = req.ToJson();
                TestAssert.WithMessage(() => Assert.Contains(ApiSecret, assertedValue), "the wire form still carries the API secret");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task ALoggedRequestCarriesNeitherSecret()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (s, _) = await ConnectedSession(messageDetails: true);

            await s.SendToJanus(new JanusMessageReq("message"), s.SessionUri);

            string line = await WaitForLine(_log, "SendToJanus. URI=");
            {
                var assertedValue = line;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the request line is logged when MessageDetails is on");
            }
            AssertNoSecrets(line, "the logged request");
            AssertNoSecrets(string.Join("\n", _log.Lines()), "any log line");
        }

        [Fact(Timeout = 60000)]
        public async Task ALoggedUriHasItsQueryRedacted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (s, _) = await ConnectedSession(messageDetails: true);

            await s.SendToJanus(new JanusMessageReq("message"), s.SessionUri + "?apisecret=" + ApiSecret + "&x=1");

            string line = await WaitForLine(_log, "SendToJanus. URI=" + s.SessionUri);
            {
                var assertedValue = line;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the request to the session URI is logged");
            }
            AssertNoSecrets(line, "the logged URI");
        }

        [Fact(Timeout = 60000)]
        public async Task TheLongPollUrlIsLoggedWithoutTheSecretButStillSentWithIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (_, fake) = await ConnectedSession(messageDetails: true);

            string line = await WaitForLine(_log, "GetFromJanus: URI=");
            {
                var assertedValue = line;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the long-poll URL is logged when MessageDetails is on");
            }
            AssertNoSecrets(line, "the logged long-poll URL");
            AssertNoSecrets(string.Join("\n", _log.Lines()), "any log line");

            var sw = Stopwatch.StartNew();
            while (fake.GetUrls.IsEmpty && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(fake.GetUrls.TryPeek(out string sent), "the fake saw the long poll");
            {
                var assertedValue = sent;
                TestAssert.WithMessage(() => Assert.Contains("?apisecret=" + ApiSecret, assertedValue), "Janus needs the secret in the GET query");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task WithMessageDetailsOffNeitherSecretIsLogged()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (s, _) = await ConnectedSession(messageDetails: false);

            await s.SendToJanus(new JanusMessageReq("message"), s.SessionUri);

            AssertNoSecrets(string.Join("\n", _log.Lines()), "any log line");
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await TearDown();
        }
    }
}
