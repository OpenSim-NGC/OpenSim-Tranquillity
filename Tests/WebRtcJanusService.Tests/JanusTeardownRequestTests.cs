/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tidying up a viewer session must not send Janus a request it is bound to refuse. Janus logs every refusal at
 * ERROR ("Couldn't find any handle|session"), so each hang-up left error lines in the mixer's log:
 *  - the session "destroy" carried the handle_id of the handle that had just been detached, so Janus looked
 *    the handle up, answered "No such handle" (459) and never destroyed the session;
 *  - once Janus had said the session does not exist (458), the detach and the destroy that followed were sent
 *    to that missing session as well.
 *
 * The fake Janus keeps the live sessions and handles and records every request together with Janus's answer.
 * It answers like the real one: a request naming a session or handle that is not live is refused.
 */

using System.Net;
using System.Text;
using System.Threading.Channels;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class JanusTeardownRequestTests
    {
        private sealed class RecordedRequest
        {
            public string Janus;
            public string Path;
            public string HandleInBody;
            public int RefusedWith;     // 0 = accepted
        }

        private sealed class FakeJanus : HttpMessageHandler
        {
            private readonly Channel<string> _never = Channel.CreateUnbounded<string>();
            private readonly object _lock = new object();
            private readonly HashSet<string> _sessions = new HashSet<string>();
            private readonly HashSet<string> _handles = new HashSet<string>();
            private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
            private const string SessionId = "4242";
            private const string HandleId = "77";

            public List<RecordedRequest> Requests { get { lock (_lock) return _requests.ToList(); } }
            public int LiveSessions { get { lock (_lock) return _sessions.Count; } }

            // Janus lost the session (a restart, a timeout): every handle in it is gone with it.
            public void DropSession()
            {
                lock (_lock)
                {
                    _sessions.Clear();
                    _handles.Clear();
                }
            }

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
                string janus = body["janus"].AsString();
                string path = request.RequestUri.AbsolutePath;
                string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);   // voice[/session[/handle]]
                string session = parts.Length > 1 ? parts[1] : null;
                string handle = parts.Length > 2 ? parts[2] : null;
                string handleInBody = body.ContainsKey("handle_id") ? body["handle_id"].AsLong().ToString() : null;
                handle ??= handleInBody;

                lock (_lock)
                {
                    int refused = 0;
                    string reason = null;
                    if (janus != "create" && !_sessions.Contains(session ?? ""))
                    {
                        refused = 458;
                        reason = "No such session " + session;
                    }
                    else if (handle is not null && !_handles.Contains(handle))
                    {
                        refused = 459;
                        reason = "No such handle " + handle;
                    }
                    _requests.Add(new RecordedRequest { Janus = janus, Path = path, HandleInBody = handleInBody, RefusedWith = refused });
                    if (refused != 0)
                    {
                        return Json("{\"janus\":\"error\",\"transaction\":\"" + txn + "\",\"error\":{\"code\":" + refused
                                    + ",\"reason\":\"" + reason + "\"}}");
                    }
                    switch (janus)
                    {
                        case "create":
                            _sessions.Add(SessionId);
                            return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":" + SessionId + "}}");
                        case "attach":
                            _handles.Add(HandleId);
                            return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":" + HandleId + "}}");
                        case "detach":
                            _handles.Remove(handle);
                            return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
                        case "destroy":
                            _sessions.Remove(session);
                            _handles.Clear();
                            return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
                        default:
                            return Json("{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
                    }
                }
            }

            private static HttpResponseMessage Json(string body) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private static async Task<(FakeJanus Fake, JanusViewerSession Viewer, JanusAudioBridge Bridge)> ConnectedViewer()
        {
            var fake = new FakeJanus();
            var session = new JanusSession("http://janus.example.org/voice", "tok", "http://janus.example.org/admin", "tok", false, fake);
            Assert.That(await session.CreateSession(), Is.True);
            var bridge = new JanusAudioBridge(session, "janus.plugin.slvoice");
            Assert.That(await bridge.Activate(null), Is.True, "the handle attached");
            var viewer = new JanusViewerSession(null) { Session = session, AudioBridge = bridge };
            return (fake, viewer, bridge);
        }

        [Test]
        public async Task ShutdownDetachesTheHandleOnceAndTheSessionDestroyIsNotAddressedToIt()
        {
            var (fake, viewer, _) = await ConnectedViewer();

            await viewer.Shutdown();

            var sent = fake.Requests;
            Assert.That(sent.Where(r => r.Janus == "detach").Count(), Is.EqualTo(1), "each handle is detached at most once");
            var destroy = sent.Single(r => r.Janus == "destroy");
            Assert.That(destroy.HandleInBody, Is.Null, "a session request does not name a handle");
            Assert.That(sent.Where(r => r.RefusedWith != 0), Is.Empty, "Janus refused nothing, so it logged no error");
            Assert.That(fake.LiveSessions, Is.EqualTo(0), "the session was destroyed now, not left to Janus's idle timeout");
        }

        [Test]
        public async Task OnceJanusSaysTheSessionIsGoneNothingMoreIsSentToIt()
        {
            var (fake, viewer, bridge) = await ConnectedViewer();
            viewer.Room = new JanusRoom(bridge, 1);
            fake.DropSession();
            int before = fake.Requests.Count;

            await viewer.Shutdown();

            var after = fake.Requests.Skip(before).ToList();
            Assert.That(after.Count, Is.EqualTo(1), "only the leave, which finds out the session is gone; got: "
                                                    + string.Join(", ", after.Select(r => r.Janus)));
            Assert.That(after.Any(r => r.Janus == "detach" || r.Janus == "destroy"), Is.False,
                        "no detach and no destroy for a session Janus no longer has");
        }
    }
}
