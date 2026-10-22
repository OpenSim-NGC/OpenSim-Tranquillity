/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * JanusSession ack'd requests are bounded by a timeout, pending
 * requests are completed with an error on DestroySession and on long-poll exit, and every
 * _OutstandingRequests access is locked.
 *
 * No live Janus: a fake HttpMessageHandler answers "create"/"destroy" with success, every other
 * POST with "ack", and serves the long-poll GET from a queue the test controls.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusSessionTimeoutTests
    {
        private sealed class FakeJanus : HttpMessageHandler
        {
            private readonly Channel<(HttpStatusCode Code, string Body)> _gets =
                Channel.CreateUnbounded<(HttpStatusCode, string)>();
            public readonly ConcurrentQueue<string> AckedTransactions = new ConcurrentQueue<string>();
            private int _ackCount;
            public int AckCount => Volatile.Read(ref _ackCount);

            // The next long-poll GET returns this.
            public void QueueGet(HttpStatusCode code, string body) => _gets.Writer.TryWrite((code, body));
            public void QueueEvent(string transaction) =>
                QueueGet(HttpStatusCode.OK, "{\"janus\":\"event\",\"transaction\":\"" + transaction + "\"}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Get)
                {
                    (HttpStatusCode Code, string Body) get;
                    try
                    {
                        get = await _gets.Reader.ReadAsync(ct);   // blocks until queued or the Janus session cancels
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TaskCanceledException();
                    }
                    return Json(get.Code, get.Body);
                }

                var body = OSDParser.DeserializeJson(await request.Content.ReadAsStringAsync(ct)) as OSDMap;
                string janus = body["janus"].AsString();
                string txn = body["transaction"].AsString();
                switch (janus)
                {
                    case "create":
                        return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":4242}}");
                    case "destroy":
                        return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
                    default:
                        AckedTransactions.Enqueue(txn);
                        Interlocked.Increment(ref _ackCount);
                        return Json(HttpStatusCode.OK, "{\"janus\":\"ack\",\"transaction\":\"" + txn + "\"}");
                }
            }

            private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
                new HttpResponseMessage(code) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };
        }

        private readonly List<JanusSession> _sessions = new List<JanusSession>();

        [TearDown]
        public async Task TearDown()
        {
            foreach (JanusSession s in _sessions)
            {
                if (s.IsConnected)
                    await s.DestroySession();
                s.Dispose();
            }
            _sessions.Clear();
        }

        private async Task<(JanusSession Session, FakeJanus Fake)> NewConnectedSession(TimeSpan requestTimeout)
        {
            var fake = new FakeJanus();
            var s = new JanusSession("http://janus.test/voice", "tok", "http://janus.test/admin", "tok", false, fake)
            {
                JanusRequestTimeout = requestTimeout
            };
            _sessions.Add(s);
            Assert.That(await s.CreateSession(), Is.True, "fake create succeeds and starts the long poll");
            return (s, fake);
        }

        // Bound every await so a red run fails instead of hanging the suite.
        private static async Task<T> Within<T>(Task<T> task, int ms, string what)
        {
            Task done = await Task.WhenAny(task, Task.Delay(ms));
            Assert.That(done, Is.SameAs(task), what + " did not complete within " + ms + " ms");
            return await task;
        }

        private static async Task WaitForAcks(FakeJanus fake, int n)
        {
            var sw = Stopwatch.StartNew();
            while (fake.AckCount < n && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(10);
            Assert.That(fake.AckCount, Is.EqualTo(n), "requests acked by the fake");
        }

        private static void AssertError(JanusMessageResp resp, string reason)
        {
            Assert.That(resp, Is.Not.Null, "a response, not null");
            Assert.That(resp.ReturnCode, Is.EqualTo("error"));
            Assert.That(new ErrorResp(resp).errorReason, Is.EqualTo(reason));
        }

        // (a) ack, no event: bounded by the timeout, error resp, entry removed.
        [Test]
        public async Task AckWithNoEvent_ReturnsTimeoutError_InAboutTheTimeout()
        {
            var (s, _) = await NewConnectedSession(TimeSpan.FromMilliseconds(300));

            var sw = Stopwatch.StartNew();
            JanusMessageResp resp = await Within(s.SendToJanus(new JanusMessageReq("message"), s.SessionUri), 5000, "ack'd request");
            sw.Stop();

            AssertError(resp, JanusSession.RequestTimeoutReason);
            Assert.That(s.OutstandingRequestCount, Is.EqualTo(0), "timed-out entry removed");
            Assert.That(sw.ElapsedMilliseconds, Is.InRange(250, 2000), "returns in about the timeout, not longer");
        }

        // (b) DestroySession completes every pending wait with an error.
        [Test]
        public async Task DestroySession_CompletesBothPendingRequests_WithSessionDestroyedError()
        {
            var (s, fake) = await NewConnectedSession(TimeSpan.FromSeconds(30));
            Task<JanusMessageResp> t1 = s.SendToJanus(new JanusMessageReq("message"), s.SessionUri);
            Task<JanusMessageResp> t2 = s.SendToJanus(new JanusMessageReq("message"), s.SessionUri);
            await WaitForAcks(fake, 2);

            await s.DestroySession();

            AssertError(await Within(t1, 3000, "first pending request"), JanusSession.SessionDestroyedReason);
            AssertError(await Within(t2, 3000, "second pending request"), JanusSession.SessionDestroyedReason);
            Assert.That(s.OutstandingRequestCount, Is.EqualTo(0), "dictionary empty after destroy");
        }

        // (c) Long-poll exit (GETERROR arm, here a 404) completes pending waits with an error.
        [Test]
        public async Task LongPollGetError_CompletesPendingRequest_WithLongPollExitedError()
        {
            var (s, fake) = await NewConnectedSession(TimeSpan.FromSeconds(30));
            Task<JanusMessageResp> t = s.SendToJanus(new JanusMessageReq("message"), s.SessionUri);
            await WaitForAcks(fake, 1);

            fake.QueueGet(HttpStatusCode.NotFound, string.Empty);

            AssertError(await Within(t, 3000, "pending request after long-poll exit"), JanusSession.LongPollExitedReason);
            Assert.That(s.OutstandingRequestCount, Is.EqualTo(0), "dictionary empty after long-poll exit");
        }

        // (d) 20 concurrent ack'd requests, events delivered from another task.
        [Test]
        public async Task TwentyParallelAckedRequests_AllCompleteWithTheirEvents_DictionaryEmpty()
        {
            var (s, fake) = await NewConnectedSession(TimeSpan.FromSeconds(30));

            Task<JanusMessageResp>[] sends = Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => s.SendToJanus(new JanusMessageReq("message"), s.SessionUri)))
                .ToArray();
            await WaitForAcks(fake, 20);

            _ = Task.Run(() =>
            {
                while (fake.AckedTransactions.TryDequeue(out string txn))
                    fake.QueueEvent(txn);
            });

            JanusMessageResp[] all = await Within(Task.WhenAll(sends), 5000, "20 parallel requests");
            Assert.That(all.All(r => r is not null && r.ReturnCode == "event"), Is.True, "every request got its event");
            Assert.That(all.Select(r => r.TransactionId).Distinct().Count(), Is.EqualTo(20), "each got its own event");
            Assert.That(s.OutstandingRequestCount, Is.EqualTo(0), "dictionary empty");
        }
    }
}
