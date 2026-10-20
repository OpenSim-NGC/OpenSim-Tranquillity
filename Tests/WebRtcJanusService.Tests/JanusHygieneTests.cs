/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for service-session reconnect, LeaveRoom's answer, the signalling fixes and the
 * bounded room-create locks.
 *
 * No live Janus: FakeJanusGateway answers the HTTP API every JanusSession in a test creates (each through
 * its own FakeJanusHandler, because disposing the HttpClient of a Janus session disposes its handler). It numbers
 * sessions, serves the long poll of each Janus session from a queue the test controls, can declare a Janus session unknown
 * to the server (458), records trickles, and answers the plugin "list" and "leave" requests.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public sealed class FakeJanusGateway
    {
        private long _nextId = 1000;
        private readonly ConcurrentDictionary<string, Channel<(HttpStatusCode Code, string Body)>> _polls = new();

        public readonly ConcurrentQueue<string> CreatedSessions = new ConcurrentQueue<string>();
        public readonly ConcurrentDictionary<string, bool> DeadSessions = new ConcurrentDictionary<string, bool>();
        public readonly ConcurrentQueue<OSDMap> Trickles = new ConcurrentQueue<OSDMap>();

        // plugindata.data of the event that completes a "leave".
        public string LeaveReplyData = "{\"audiobridge\":\"left\",\"room\":900,\"id\":5}";

        public int CreateCount => CreatedSessions.Count;

        // ---- An opt-in model of the mixer's rooms and of Janus core's per-handle ICE rule ----------
        // Off by default, so every existing test sees exactly the replies it always did. On, the plugin's "create"
        // and "join" behave as the live mixer does:
        //   create - "created", or the 486 already-exists event (which the sim treats as success);
        //   join   - Janus CORE builds the handle's ICE agent from the join's JSEP offer BEFORE the plugin looks at
        //            the room, so a missing room answers the plugin's 485 event while the agent stays; a second
        //            JSEP-bearing request on that SAME handle is refused by core with a top-level 490 "Error setting
        //            ICE locally", which never reaches the plugin; a fresh handle joins.
        public bool ModelRooms;

        /// A test's own answer to a plugin request: given the request body, the plugindata.data JSON to answer with, or
        /// null to leave the request to the model above. The answer comes back in the HTTP response, as list and destroy do.
        public Func<OSDMap, string> PluginAnswer;
        public readonly ConcurrentDictionary<int, bool> Rooms = new();
        public readonly ConcurrentDictionary<string, bool> IceAgents = new();
        private int _roomCreates, _roomJoins, _roomCreated;
        public int RoomCreates => Volatile.Read(ref _roomCreates);   // create requests the plugin saw
        public int RoomsCreated => Volatile.Read(ref _roomCreated);  // of those, how many actually created a room
        public int RoomJoins => Volatile.Read(ref _roomJoins);       // JSEP-bearing join requests sent
        public void GraceDestroy(int room) => Rooms.TryRemove(room, out _);
        /// A room number whose room the "grace sweep" removes right after the plugin answers its create - the race
        /// between a create (or its 486) and the join that follows it.
        public int DestroyAfterCreate;

        public void QueueGet(string pSessionId, HttpStatusCode pCode, string pBody) => Poll(pSessionId).Writer.TryWrite((pCode, pBody));

        private Channel<(HttpStatusCode Code, string Body)> Poll(string pSessionId) =>
            _polls.GetOrAdd(pSessionId, _ => Channel.CreateUnbounded<(HttpStatusCode, string)>());

        public async Task<HttpResponseMessage> Handle(HttpRequestMessage request, CancellationToken ct)
        {
            string[] segs = request.RequestUri.AbsolutePath.Trim('/').Split('/');   // voice[/session[/handle]]
            string sid = segs.Length > 1 ? segs[1] : null;

            if (request.Method == HttpMethod.Get)
            {
                if (sid is null)
                    return Json(HttpStatusCode.OK, "{\"janus\":\"server_info\"}");
                try
                {
                    (HttpStatusCode code, string body) = await Poll(sid).Reader.ReadAsync(ct);
                    return Json(code, body);
                }
                catch (OperationCanceledException)
                {
                    throw new TaskCanceledException();
                }
            }

            var msg = OSDParser.DeserializeJson(await request.Content.ReadAsStringAsync(ct)) as OSDMap;
            string janus = msg["janus"].AsString();
            string txn = msg["transaction"].AsString();

            if (sid is not null && DeadSessions.ContainsKey(sid))
                return Json(HttpStatusCode.OK, "{\"janus\":\"error\",\"transaction\":\"" + txn + "\",\"error\":{\"code\":458,\"reason\":\"No such session\"}}");

            switch (janus)
            {
                case "create":
                    string id = Interlocked.Increment(ref _nextId).ToString();
                    CreatedSessions.Enqueue(id);
                    return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":" + id + "}}");
                case "attach":
                    return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\",\"data\":{\"id\":" + Interlocked.Increment(ref _nextId) + "}}");
                case "trickle":
                    Trickles.Enqueue(msg);
                    return Json(HttpStatusCode.OK, "{\"janus\":\"ack\",\"transaction\":\"" + txn + "\"}");
                case "message":
                    string req = (msg["body"] as OSDMap)?["request"].AsString();
                    if (PluginAnswer is not null && msg["body"] is OSDMap asked && PluginAnswer(asked) is string answer)
                        return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn +
                            "\",\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":" + answer + "}}");
                    if (ModelRooms && (req == "create" || req == "join"))
                        return ModelRoomRequest(req, msg, sid, segs.Length > 2 ? segs[2] : null, txn);
                    if (ModelRoomLifecycle && (req == "list" || req == "destroy"))
                        return ModelRoomLifecycleRequest(req, msg, txn);
                    if (req == "list")
                        return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn +
                            "\",\"plugindata\":{\"plugin\":\"janus.plugin.slvoice\",\"data\":{\"audiobridge\":\"success\",\"list\":[]}}}");
                    if (req == "leave" && ModelRoomLifecycle)
                        Participants.AddOrUpdate((msg["body"] as OSDMap)["room"].AsInteger(), 0, (_, n) => Math.Max(0, n - 1));
                    if (req == "leave" && sid is not null)
                        QueueGet(sid, HttpStatusCode.OK, "{\"janus\":\"event\",\"transaction\":\"" + txn +
                            "\",\"plugindata\":{\"plugin\":\"janus.plugin.slvoice\",\"data\":" + LeaveReplyData + "}}");
                    return Json(HttpStatusCode.OK, "{\"janus\":\"ack\",\"transaction\":\"" + txn + "\"}");
                default:   // destroy, detach, keepalive
                    return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"}");
            }
        }

        // ---- An opt-in model of the stock plugin's room lifecycle (needs ModelRooms for create and join) ----
        // On, "list" answers every room with its participant count, "leave" lowers the count, and "destroy" removes
        // a room (error 485 for one that is not there). Nothing removes an emptied room by itself, as on the stock plugin.
        public bool ModelRoomLifecycle;
        public readonly ConcurrentDictionary<int, int> Participants = new();
        public readonly ConcurrentQueue<int> Destroyed = new();

        private HttpResponseMessage ModelRoomLifecycleRequest(string req, OSDMap msg, string txn)
        {
            const string Plugin = "\"plugindata\":{\"plugin\":\"janus.plugin.audiobridge\",\"data\":";
            if (req == "list")
            {
                var items = new List<string>();
                foreach (int room in Rooms.Keys)
                    items.Add("{\"room\":" + room + ",\"description\":\"r\",\"num_participants\":" + Participants.GetValueOrDefault(room) + "}");
                return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"," + Plugin +
                    "{\"audiobridge\":\"success\",\"list\":[" + string.Join(",", items) + "]}}}");
            }
            int target = ((OSDMap)msg["body"])["room"].AsInteger();
            if (Rooms.TryRemove(target, out _))
            {
                Participants.TryRemove(target, out _);
                Destroyed.Enqueue(target);
                return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"," + Plugin +
                    "{\"audiobridge\":\"destroyed\",\"room\":" + target + "}}}");
            }
            return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"," + Plugin +
                "{\"audiobridge\":\"event\",\"error_code\":485,\"error\":\"No such room (" + target + ")\"}}}");
        }

        private HttpResponseMessage ModelRoomRequest(string req, OSDMap msg, string sid, string handle, string txn)
        {
            var body = (OSDMap)msg["body"];
            int room = body["room"].AsInteger();
            const string Plugin = "\"plugindata\":{\"plugin\":\"janus.plugin.slvoice\",\"data\":";
            if (req == "create")
            {
                Interlocked.Increment(ref _roomCreates);
                bool created = Rooms.TryAdd(room, true);
                if (created)
                    Interlocked.Increment(ref _roomCreated);
                if (room == DestroyAfterCreate)
                    Rooms.TryRemove(room, out _);
                string data = created
                    ? "{\"audiobridge\":\"created\",\"room\":" + room + "}"
                    : "{\"audiobridge\":\"event\",\"error_code\":486,\"error\":\"Room " + room + " already exists\"}";
                return Json(HttpStatusCode.OK, "{\"janus\":\"success\",\"transaction\":\"" + txn + "\"," + Plugin + data + "}}");
            }
            // join
            if (msg.ContainsKey("jsep"))
            {
                Interlocked.Increment(ref _roomJoins);
                if (handle is not null && !IceAgents.TryAdd(handle, true))
                    return Json(HttpStatusCode.OK, "{\"janus\":\"error\",\"transaction\":\"" + txn +
                        "\",\"error\":{\"code\":490,\"reason\":\"Error setting ICE locally\"}}");
            }
            if (Rooms.ContainsKey(room))
                Participants.AddOrUpdate(room, 1, (_, n) => n + 1);
            string ev = Rooms.ContainsKey(room)
                ? "{\"janus\":\"event\",\"transaction\":\"" + txn + "\"," + Plugin +
                  "{\"audiobridge\":\"joined\",\"room\":" + room + ",\"id\":" + Interlocked.Increment(ref _nextId) +
                  "}},\"jsep\":{\"type\":\"answer\",\"sdp\":\"v=0 fake-answer\"}}"
                : "{\"janus\":\"event\",\"transaction\":\"" + txn + "\"," + Plugin +
                  "{\"audiobridge\":\"event\",\"error_code\":485,\"error\":\"No such room (" + room + ")\"}}}";
            if (sid is not null)
                QueueGet(sid, HttpStatusCode.OK, ev);
            return Json(HttpStatusCode.OK, "{\"janus\":\"ack\",\"transaction\":\"" + txn + "\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new HttpResponseMessage(code) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };
    }

    public sealed class FakeJanusHandler : HttpMessageHandler
    {
        private readonly FakeJanusGateway _gateway;
        public FakeJanusHandler(FakeJanusGateway pGateway) { _gateway = pGateway; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _gateway.Handle(request, ct);
    }

    [TestFixture]
    public class JanusHygieneTests
    {
        private static readonly UUID Region = new UUID("bbbbbbbb-0000-0000-0000-0000000a0060");
        private static readonly UUID Alice = new UUID("aaaaaaaa-1111-1111-1111-0000000a0060");

        private FakeJanusGateway _gw;
        private readonly List<JanusSession> _sessions = new List<JanusSession>();

        [SetUp]
        public void SetUp()
        {
            _gw = new FakeJanusGateway();
        }

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

        private static IConfigSource JanusConfig()
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", "tok");
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", "tok");
            janus.Set("PluginName", "janus.plugin.slvoice");
            janus.Set("RequestTimeoutMs", "500");
            return config;
        }

        private static IConfigSource NoJanusConfig()
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            return config;
        }

        private WebRtcJanusService NewService() => new WebRtcJanusService(JanusConfig(), () => new FakeJanusHandler(_gw));

        private async Task<JanusSession> NewSession()
        {
            var s = new JanusSession("http://janus.test/voice", "tok", "http://janus.test/admin", "tok", false, new FakeJanusHandler(_gw))
            {
                JanusRequestTimeout = TimeSpan.FromMilliseconds(500)
            };
            _sessions.Add(s);
            Assert.That(await s.CreateSession(), Is.True, "fake create succeeds");
            return s;
        }

        private static async Task<T> Within<T>(Task<T> task, int ms, string what)
        {
            Task done = await Task.WhenAny(task, Task.Delay(ms));
            Assert.That(done, Is.SameAs(task), what + " did not complete within " + ms + " ms");
            return await task;
        }

        private static async Task WaitUntil(Func<bool> condition, int ms, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < ms)
                await Task.Delay(10);
            Assert.That(condition(), Is.True, what);
        }

        // ---- service session reconnect ----------------------------------------------------------

        [Test]
        public async Task ServiceSession_WhoseLongPollExited_IsReplacedOnTheNextConsoleCall()
        {
            WebRtcJanusService svc = NewService();
            string first = svc.ServiceSessionId;
            Assert.That(first, Is.Not.Null.And.Not.Empty, "the service session is created at construction");

            _gw.QueueGet(first, HttpStatusCode.NotFound, string.Empty);   // the mixer restarted: the long poll gets 404
            await WaitUntil(() => !svc.ServiceSessionIsConnected, 3000, "the lost long poll marks the service session disconnected");

            AudioBridgeResp resp = await Within(svc.ServiceListRoomsAsync(), 5000, "console list rooms");

            Assert.That(resp, Is.Not.Null);
            Assert.That(resp.isSuccess, Is.True, "the console command succeeds on the fresh session");
            Assert.That(svc.ServiceSessionId, Is.Not.EqualTo(first), "a fresh Janus session replaced the dead one");
            Assert.That(svc.ServiceSessionIsConnected, Is.True);
            Assert.That(_gw.CreateCount, Is.EqualTo(2));
        }

        [Test]
        public async Task ServiceSession_UnknownToTheServer_IsReplacedAfterTheFailedRequest()
        {
            WebRtcJanusService svc = NewService();
            string first = svc.ServiceSessionId;
            _gw.DeadSessions[first] = true;   // restarted mixer, but no disconnect event reached us

            AudioBridgeResp resp = await Within(svc.ServiceListRoomsAsync(), 5000, "console list rooms");

            Assert.That(resp?.isSuccess, Is.True, "retried once on a reconnected session");
            Assert.That(svc.ServiceSessionId, Is.Not.EqualTo(first));
            Assert.That(_gw.CreateCount, Is.EqualTo(2));
        }

        [Test]
        public async Task HealthyServiceSession_IsReused()
        {
            WebRtcJanusService svc = NewService();
            string first = svc.ServiceSessionId;

            Assert.That((await Within(svc.ServiceListRoomsAsync(), 5000, "first list"))?.isSuccess, Is.True);
            Assert.That((await Within(svc.ServiceListRoomsAsync(), 5000, "second list"))?.isSuccess, Is.True);

            Assert.That(svc.ServiceSessionId, Is.EqualTo(first));
            Assert.That(_gw.CreateCount, Is.EqualTo(1), "no reconnect for a live session");
        }

        // ---- LeaveRoom's answer ------------------------------------------------------------------

        [TestCase("{\"audiobridge\":\"left\",\"room\":900,\"id\":5}", true)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":487,\"error\":\"Not in a room\"}", true)]
        [TestCase("{\"audiobridge\":\"event\",\"error_code\":485,\"error\":\"No such room\"}", false)]
        public async Task LeaveRoom_ReportsTheMixersAnswer(string pReplyData, bool pExpected)
        {
            _gw.LeaveReplyData = pReplyData;
            JanusSession s = await NewSession();
            var bridge = new JanusAudioBridge(s, "janus.plugin.slvoice");
            Assert.That(await bridge.Activate(new IniConfigSource()), Is.True, "fake attach succeeds");
            var room = new JanusRoom(bridge, 900);

            bool left = await Within(room.LeaveRoom(new JanusViewerSession(null) { ParticipantId = 5 }), 3000, "leave");

            Assert.That(left, Is.EqualTo(pExpected));
        }

        // ---- signalling --------------------------------------------------------------------------

        [Test]
        public void Signalling_OnAViewerSessionWithNoJanusSession_AnswersAnErrorMap_NotAnNre()
        {
            var svc = new WebRtcJanusService(NoJanusConfig());
            var vs = (JanusViewerSession)svc.CreateViewerSession(new OSDMap(), Alice, Region);
            var req = new OSDMap { ["candidate"] = new OSDMap { ["completed"] = OSD.FromBoolean(true) } };

            OSDMap resp = null;
            Assert.DoesNotThrow(() => resp = svc.VoiceSignalingRequest(vs, req, Alice, Region));

            Assert.That(resp, Is.Not.Null);
            Assert.That(resp["response"].AsString(), Is.EqualTo("error"));
        }

        [Test]
        public async Task Signalling_SingularCandidate_IsTrickled_NotDropped()
        {
            var svc = new WebRtcJanusService(NoJanusConfig());
            var vs = new JanusViewerSession(svc) { Session = await NewSession(), AgentId = Alice, RegionId = Region };
            var req = new OSDMap
            {
                ["candidate"] = new OSDMap
                {
                    ["candidate"] = OSD.FromString("candidate:1 1 udp 2122260223 192.0.2.1 50000 typ host"),
                    ["sdpMid"] = OSD.FromString("0"),
                    ["sdpMLineIndex"] = OSD.FromInteger(0),
                }
            };

            OSDMap resp = svc.VoiceSignalingRequest(vs, req, Alice, Region);

            Assert.That(_gw.Trickles.Count, Is.EqualTo(1), "one trickle request for the singular candidate (was an empty else)");
            Assert.That(resp.ContainsKey("response"), Is.False, "the Janus reply, not the error map");
            Assert.That(_gw.Trickles.TryPeek(out OSDMap sent) && sent.TryGetOSDArray("candidates", out OSDArray one)
                && one.Count == 1 && ((OSDMap)one[0])["candidate"].AsString().StartsWith("candidate:1 "), Is.True,
                "the singular candidate is forwarded as a one-element candidates array");
        }

        // TrickleCandidates used to send the end-of-candidates marker, dropping every trickled candidate.
        [Test]
        public async Task Signalling_Candidates_AreForwardedToJanus()
        {
            var svc = new WebRtcJanusService(NoJanusConfig());
            var vs = new JanusViewerSession(svc) { Session = await NewSession(), AgentId = Alice, RegionId = Region };
            var req = new OSDMap
            {
                ["candidates"] = new OSDArray
                {
                    new OSDMap
                    {
                        ["candidate"] = OSD.FromString("candidate:1 1 udp 2122260223 192.0.2.1 50000 typ host"),
                        ["sdpMid"] = OSD.FromString("0"),
                        ["sdpMLineIndex"] = OSD.FromInteger(0),
                    },
                    new OSDMap
                    {
                        ["candidate"] = OSD.FromString("candidate:2 1 udp 1686052607 203.0.113.9 61000 typ srflx raddr 192.0.2.1 rport 50000"),
                        ["sdpMid"] = OSD.FromString("0"),
                        ["sdpMLineIndex"] = OSD.FromInteger(0),
                    },
                }
            };

            OSDMap resp = svc.VoiceSignalingRequest(vs, req, Alice, Region);

            Assert.That(resp.ContainsKey("response"), Is.False, "the Janus reply, not the error map");
            Assert.That(_gw.Trickles.Count, Is.EqualTo(1), "one trickle request reached Janus");
            Assert.That(_gw.Trickles.TryPeek(out OSDMap sent), Is.True);
            Assert.That(sent.ContainsKey("candidate"), Is.False, "not the end-of-candidates marker");
            Assert.That(sent.ContainsKey("viewer_session"), Is.False, "no member outside the Janus trickle API");
            Assert.That(sent.TryGetOSDArray("candidates", out OSDArray forwarded), Is.True, "a candidates array");
            Assert.That(forwarded.Count, Is.EqualTo(2));
            Assert.That(((OSDMap)forwarded[0])["candidate"].AsString(), Does.Contain("typ host"));
            Assert.That(((OSDMap)forwarded[1])["candidate"].AsString(), Does.Contain("typ srflx"));
            Assert.That(((OSDMap)forwarded[1])["sdpMid"].AsString(), Is.EqualTo("0"));
            Assert.That(((OSDMap)forwarded[1])["sdpMLineIndex"].AsInteger(), Is.EqualTo(0));
        }

        [Test]
        public async Task Signalling_Completed_SendsTheEndOfCandidatesMarker()
        {
            var svc = new WebRtcJanusService(NoJanusConfig());
            var vs = new JanusViewerSession(svc) { Session = await NewSession(), AgentId = Alice, RegionId = Region };
            var req = new OSDMap { ["candidate"] = new OSDMap { ["completed"] = OSD.FromBoolean(true) } };

            svc.VoiceSignalingRequest(vs, req, Alice, Region);

            Assert.That(_gw.Trickles.Count, Is.EqualTo(1), "one trickle request reached Janus");
            Assert.That(_gw.Trickles.TryPeek(out OSDMap sent), Is.True);
            Assert.That(sent.ContainsKey("candidates"), Is.False, "the marker carries no candidates");
            Assert.That(sent.TryGetOSDMap("candidate", out OSDMap marker) && marker["completed"].AsBoolean(), Is.True,
                "{\"candidate\":{\"completed\":true}}");
        }

        // ---- bounded room-create locks -----------------------------------------------------------

        [Test]
        public async Task RoomCreateLocks_IdleEntriesAreEvicted()
        {
            const int room = 777_000_060;
            await JanusAudioBridge.SelectRoomCoalesced(room, () => Task.FromResult(new JanusRoom(null, room)));
            int before = JanusAudioBridge.RoomCreateLockCount;
            Assert.That(before, Is.GreaterThanOrEqualTo(1), "the select left a gate behind");

            int keptNow = JanusAudioBridge.SweepRoomCreateLocks(Environment.TickCount64, JanusAudioBridge.RoomCreateLockIdle);
            Assert.That(JanusAudioBridge.RoomCreateLockCount, Is.EqualTo(before - keptNow), "a gate used just now is kept");

            long later = Environment.TickCount64 + (long)TimeSpan.FromMinutes(11).TotalMilliseconds;
            int evicted = JanusAudioBridge.SweepRoomCreateLocks(later, JanusAudioBridge.RoomCreateLockIdle);

            Assert.That(evicted, Is.GreaterThanOrEqualTo(1));
            Assert.That(JanusAudioBridge.RoomCreateLockCount, Is.LessThan(before), "the idle room's gate is gone");
        }
    }
}
