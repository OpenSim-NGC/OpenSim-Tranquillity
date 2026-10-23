/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The handler and the region connector over real HTTP, and the way the service is protected. A listener
 * on an ephemeral loopback port stands in for Robust's HTTP server: it hands each POST to the real
 * VoiceSessionPostHandler.Handle (the method that checks the IServiceAuth before anything else) and writes
 * back what it returns.
 */
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceSessionWireTests
    {
        private static readonly UUID GroupId = NonSpatialIdentityTests.Group;
        private const string User = "region-server";
        private const string Password = "0123456789abcdef0123456789abcdef-example";

        private sealed class TestServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly VoiceSessionPostHandler _handler;
            private readonly Task _loop;
            public string BaseUri { get; }
            public int Requests;

            /// <summary>The body of every request the server received, in order.</summary>
            public readonly List<string> Bodies = new List<string>();

            /// <summary>While this is unset a request is read but not answered, as a service that has hung does not answer.</summary>
            public readonly System.Threading.ManualResetEventSlim Answering = new System.Threading.ManualResetEventSlim(true);

            public TestServer(VoiceSessionPostHandler handler)
            {
                _handler = handler;
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                BaseUri = $"http://127.0.0.1:{port}";
                _listener.Prefixes.Add(BaseUri + "/");
                _listener.Start();
                _loop = Task.Run(Serve);
            }

            private async Task Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    try
                    {
                        System.Threading.Interlocked.Increment(ref Requests);
                        byte[] body;
                        using (MemoryStream ms = new MemoryStream())
                        {
                            ctx.Request.InputStream.CopyTo(ms);
                            body = ms.ToArray();
                        }
                        lock (Bodies)
                            Bodies.Add(Encoding.UTF8.GetString(body));
                        Answering.Wait();
                        TestOSHttpRequest request = new TestOSHttpRequest { Headers = new NameValueCollection(ctx.Request.Headers) };
                        TestOSHttpResponse response = new TestOSHttpResponse();
                        byte[] reply = _handler.Handle(ctx.Request.Url.AbsolutePath, new MemoryStream(body), request, response);
                        ctx.Response.StatusCode = response.StatusCode != 0 ? response.StatusCode : 200;
                        ctx.Response.ContentLength64 = reply.Length;
                        ctx.Response.OutputStream.Write(reply, 0, reply.Length);
                    }
                    catch { /* a broken exchange is the client's to notice */ }
                    finally { ctx.Response.Close(); }
                }
            }

            public void Dispose()
            {
                Answering.Set();
                _listener.Close();
                try { _loop.Wait(2000); } catch { }
            }
        }

        private static IniConfigSource Config(string password = Password, string user = User)
        {
            IniConfigSource config = new IniConfigSource();
            foreach (string section in new[] { "WebRtcVoice", VoiceSessionServerConnector.SectionName })
            {
                IConfig c = config.AddConfig(section);
                c.Set("AuthType", "BasicHttpAuthentication");
                c.Set("HttpAuthUsername", user);
                c.Set("HttpAuthPassword", password);
            }
            return config;
        }

        private static VoiceSessionPostHandler NewHandler(VoiceSessionService service)
            => new VoiceSessionPostHandler(service, ServiceAuth.Create(Config(), VoiceSessionServerConnector.SectionName));

        private static VoiceSessionServicesConnector NewClient(TestServer server, IniConfigSource config = null, int timeoutMs = 3000)
            => new VoiceSessionServicesConnector(config ?? Config(), "WebRtcVoice", server.BaseUri, timeoutMs);

        // ---- a region over HTTP ----------------------------------------------------------------------

        [Test]
        public void ARegionRunsACallThroughTheHandler()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));

            SessionOutcome started = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            SessionOutcome joined = region.Accept(GroupId, Bob, RegionB, "viewer-one");
            string token = region.IssueToken(GroupId, Bob);
            NonSpatialVoiceSession s = region.Get(GroupId, Bob);

            Assert.That(started.Ok, Is.True, started.Decision);
            Assert.That(started.Created, Is.True);
            Assert.That(started.StartedRinging, Is.True);
            Assert.That(joined.Ok, Is.True, joined.Decision);
            Assert.That(joined.StartedRinging, Is.False);
            Assert.That(token, Is.Not.Null);
            Assert.That(s.SeatsHeld, Is.EqualTo(2));
            Assert.That(s.Find(Bob).ViewerSession, Is.EqualTo("viewer-one"));
            Assert.That(s.Find(Bob).OriginRegion, Is.EqualTo(RegionB));
            Assert.That(s.Token, Is.EqualTo(token));
            Assert.That(service.SessionCount, Is.EqualTo(1));
        }

        [Test]
        public void TheCredentialCrossesTheWireOnlyToAMember()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));
            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            string token = region.IssueToken(GroupId, Alice);
            string key = region.Get(GroupId).RoomKey;

            Assert.That(region.Get(GroupId, Alice).Token, Is.EqualTo(token));
            Assert.That(region.GetByRoomKey(key, Alice).Token, Is.EqualTo(token));
            Assert.That(region.Get(GroupId).Token, Is.Null.Or.Empty, "no agent named");
            Assert.That(region.Get(GroupId, Carol).Token, Is.Null.Or.Empty, "not a member");
            Assert.That(region.GetByRoomKey(key, Carol).Token, Is.Null.Or.Empty, "not a member, by room key");
        }

        [Test]
        public void AReadNamingAnUnreadableAgentIsRefused()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            VoiceSessionServiceTests.NewRegion(service).Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.That(handler.Dispatch("get", new Dictionary<string, object> { ["session"] = GroupId.ToString(), ["agent"] = "not-an-id" }), Is.Null);
            Assert.That(handler.Dispatch("get", new Dictionary<string, object> { ["session"] = GroupId.ToString() }), Is.Not.Null, "naming none is fine");
        }

        [Test]
        public void EveryOperationRoundTrips()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));

            SessionOutcome conf = region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol });
            UUID id = conf.Session.SessionId;
            Assert.That(conf.Ok, Is.True, conf.Decision);
            Assert.That(region.CheckInviteCount(id, Alice, new List<UUID> { UUID.Random() }).Ok, Is.True);
            UUID dave = UUID.Random();
            Assert.That(region.Invite(id, Alice, dave).Decision, Is.EqualTo("invited"));
            Assert.That(region.Accept(id, Bob, RegionA).Ok, Is.True);
            Assert.That(region.MarkPresent(id, Bob, RegionA, "v").Ok, Is.True);
            Assert.That(region.Decline(id, Carol).Decision, Is.EqualTo("declined"));
            Assert.That(region.LeaveChat(id, Bob).Decision, Is.EqualTo("chat-left"));
            region.MarkInvited(id, new List<UUID> { dave });
            Assert.That(region.Get(id).WasInvited(dave), Is.True);
            Assert.That(region.VerifyRing(id, dave, Alice, NonSpatialSessionType.Adhoc).Decision, Is.EqualTo("ring-verified"));
            Assert.That(region.VerifyRing(id, dave, Alice, NonSpatialSessionType.Adhoc).Decision, Is.EqualTo("refused-ring-used"), "over the wire too");
            Assert.That(region.CarrierKey(id, Alice), Has.Length.GreaterThanOrEqualTo(CarrierAuth.MinSecretLength), "a seated member is given the key");
            Assert.That(region.CarrierKey(id, UUID.Random()), Is.Null, "a stranger is not");
            Assert.That(region.Moderate(id, Alice, ModerationOp.TextMute, Bob, true).Decision, Is.EqualTo("moderation-changed"));
            Assert.That(region.Get(id).IsTextMuted(Bob), Is.True, "the record crossed the wire");
            Assert.That(region.TryTakeConferenceRequest(Alice), Is.True);
            Assert.That(region.DepartByViewerSession(Bob, "v", DepartureReason.VoiceTeardown), Has.Count.EqualTo(1));
            Assert.That(region.Depart(id, Alice, DepartureReason.VoiceTeardown).Ok, Is.True);
            Assert.That(region.DepartAll(dave, DepartureReason.PresenceLost), Has.Count.EqualTo(1), "the invited member leaves everything");
            Assert.That(region.DepartRegion(RegionA), Is.GreaterThanOrEqualTo(0));
            Assert.That(region.RenewSeats(new List<SeatKey> { new SeatKey(id, Alice) }), Is.True);
            Assert.That(region.GetByRoomKey(region.Get(id).RoomKey)?.SessionId, Is.EqualTo(id));
        }

        [Test]
        public void ARemovalCrossesTheWire_WithItsReasonAndLifetime_AndTheRenewalSaysWhichSeatsWereRemoved()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));
            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            region.Accept(GroupId, Bob, RegionA, "v");

            SessionOutcome removed = region.Moderate(GroupId, Alice, ModerationOp.Remove, Bob, true, "too loud", TimeSpan.FromMinutes(30));

            Assert.That(removed.Decision, Is.EqualTo("moderation-changed"));
            CallRemoval r = region.Get(GroupId).RemovalOf(Bob);
            Assert.That(r.Reason, Is.EqualTo("too loud"));
            Assert.That(r.By, Is.EqualTo(Alice));
            Assert.That(r.ExpiresUtc - r.AtUtc, Is.EqualTo(TimeSpan.FromMinutes(30)));
            Assert.That(region.Get(GroupId).Find(Bob).HoldsSeat, Is.False);
            Assert.That(region.RenewSeats(new List<SeatKey> { new SeatKey(GroupId, Alice), new SeatKey(GroupId, Bob) },
                                          out IReadOnlyList<SeatKey> told), Is.True);
            Assert.That(told, Is.EqualTo(new[] { new SeatKey(GroupId, Bob) }), "only the removed member's seat is named");
            Assert.That(region.Moderate(GroupId, Alice, ModerationOp.Remove, Bob, false).Decision, Is.EqualTo("moderation-changed"));
        }

        [Test]
        public void AnOldRegionsModerateRequestIsStillRead_AndAReasonIsBoundedAndCleaned()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            VoiceSessionServiceTests.NewRegion(service).Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Dictionary<string, object> old = new()
            {
                ["session"] = GroupId.ToString(), ["by"] = Alice.ToString(), ["target"] = Bob.ToString(), ["op"] = "0", ["on"] = "1",
            };
            Assert.That(handler.Dispatch("moderate", old)?.Ok, Is.True, "no reason and no minutes: an old region's request");

            Dictionary<string, object> removal = new(old) { ["op"] = "3", ["reason"] = "ab\u0007\ncd" + new string('x', 500),["minutes"] = "99999999" };
            SessionReply r = handler.Dispatch("moderate", removal);

            Assert.That(r.Ok, Is.True);
            CallRemoval stored = SessionWire.FromData(r.Session).RemovalOf(Bob);
            Assert.That(stored.Reason.Length, Is.LessThanOrEqualTo(200));
            Assert.That(stored.Reason, Does.StartWith("abcdxxx"), "control characters are dropped");
            Assert.That(stored.ExpiresUtc - stored.AtUtc, Is.EqualTo(TimeSpan.FromMinutes(VoiceSessionService.MaxRemovalMinutes)), "bounded");

            old["minutes"] = "not a number";
            old["op"] = "3";
            Assert.That(handler.Dispatch("moderate", old)?.Ok, Is.True, "an unreadable lifetime is taken as none");
        }

        // ---- protection ---------------------------------------------------------------------------------

        [Test]
        public void ARegionWithTheWrongPasswordIsRefusedAndNothingChanges()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server, Config(password: "not-the-service-password-not-the-service")));

            SessionOutcome o = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.That(o.Ok, Is.False);
            Assert.That(o.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void ARequestWithNoCredentialIsRefusedBeforeItIsRead()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpResponse response = new TestOSHttpResponse();
            string body = "METHOD=start&type=2&creator=" + Alice + "&other=" + GroupId + "&temp=" + GroupId + "&origin=" + RegionA + "&cap=0";

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          new TestOSHttpRequest(), response);

            Assert.That(response.StatusCode, Is.EqualTo((int)HttpStatusCode.Unauthorized));
            Assert.That(reply, Is.Empty);
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void AnEmptyConfigurationIsNotAcceptableAuthentication()
        {
            Assert.That(VoiceSessionServerConnector.CheckAuthConfig(new IniConfigSource()), Is.Not.Null);
        }

        [TestCase("None", User, Password)]
        [TestCase("BasicHttpAuthentication", "", Password)]
        [TestCase("BasicHttpAuthentication", User, "")]
        [TestCase("BasicHttpAuthentication", User, "short-password")]
        [TestCase("BasicHttpAuthentication", User, "0123456789abcdef0123456789abcde")]   // 31 characters
        public void AnEnabledServiceRefusesToLoadWithoutAcceptableAuthentication(string authType, string user, string password)
        {
            IniConfigSource config = new IniConfigSource();
            IConfig c = config.AddConfig(VoiceSessionServerConnector.SectionName);
            c.Set("Enabled", "true");
            c.Set("AuthType", authType);
            c.Set("HttpAuthUsername", user);
            c.Set("HttpAuthPassword", password);

            Assert.That(VoiceSessionServerConnector.CheckAuthConfig(config), Is.Not.Null);
            Assert.Throws<Exception>(() => new VoiceSessionServerConnector(config, null, VoiceSessionServerConnector.SectionName));
        }

        [Test]
        public void AnAcceptablePasswordPassesTheCheck_AndThe32CharacterBoundaryIsInclusive()
        {
            IniConfigSource config = Config("0123456789abcdef0123456789abcdef");   // exactly 32
            Assert.That(VoiceSessionServerConnector.CheckAuthConfig(config), Is.Null);
        }

        [Test]
        public void ADisabledServiceServesNothing()
        {
            IniConfigSource none = new IniConfigSource();
            Assert.DoesNotThrow(() => new VoiceSessionServerConnector(none, null, VoiceSessionServerConnector.SectionName));
            IniConfigSource off = new IniConfigSource();
            off.AddConfig(VoiceSessionServerConnector.SectionName).Set("Enabled", "false");
            Assert.DoesNotThrow(() => new VoiceSessionServerConnector(off, null, VoiceSessionServerConnector.SectionName));
        }

        // ---- malformed requests change nothing -----------------------------------------------------------

        [TestCase("")]
        [TestCase("not a form")]
        [TestCase("type=2&creator=x")]
        [TestCase("METHOD=nosuchmethod")]
        [TestCase("METHOD=start")]
        [TestCase("METHOD=start&type=two&creator=11111111-1111-1111-1111-111111111111&other=x&temp=x&origin=x&cap=0")]
        [TestCase("METHOD=start&type=2&creator=not-a-uuid&other=99999999-9999-9999-9999-999999999999&temp=99999999-9999-9999-9999-999999999999&origin=99999999-9999-9999-9999-999999999999&cap=0")]
        [TestCase("METHOD=accept&session=99999999-9999-9999-9999-999999999999")]
        [TestCase("METHOD=startadhoc&creator=11111111-1111-1111-1111-111111111111&temp=22222222-2222-2222-2222-222222222222&origin=33333333-3333-3333-3333-333333333333&cap=0&invitees=11111111-1111-1111-1111-111111111111,garbage")]
        [TestCase("METHOD=renewseats&seats=garbage")]
        [TestCase("METHOD=depart&session=99999999-9999-9999-9999-999999999999&agent=11111111-1111-1111-1111-111111111111&reason=banana")]
        [TestCase("METHOD=getbyroomkey&roomkey=")]
        public void AMalformedRequestIsAFailureAndChangesNothing(string body)
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpRequest request = new TestOSHttpRequest();
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          request, new TestOSHttpResponse());

            string text = Encoding.UTF8.GetString(reply);
            Assert.That(text, Does.Contain("Failure"), text);
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void ARequestOverTheSizeLimitIsAFailure()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpRequest request = new TestOSHttpRequest();
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));
            string body = "METHOD=renewseats&seats=" + new string('a', VoiceSessionPostHandler.MaxBodyChars + 1);

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          request, new TestOSHttpResponse());

            Assert.That(Encoding.UTF8.GetString(reply), Does.Contain("Failure"));
        }

        // ---- the service is not reachable ------------------------------------------------------------------

        [Test]
        public void WithNothingListening_ARegionIsRefusedCleanly()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            TestServer first = new TestServer(NewHandler(service));
            string uri = first.BaseUri;
            first.Dispose();   // Robust is down: the port is closed
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", uri, 1000);
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(client);

            SessionOutcome refused = default;
            Assert.DoesNotThrow(() => refused = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA));
            Assert.That(refused.Ok, Is.False);
            Assert.That(refused.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            Assert.That(region.Get(GroupId), Is.Null);
            Assert.That(service.SessionCount, Is.EqualTo(0), "no session anywhere");
        }

        // ---- the wire contract ---------------------------------------------------------------------------

        [Test]
        public void EveryRequestHasTheDocumentedBody()
        {
            // The names and order of the fields each operation sends. A later move to another web stack, or a
            // service written by someone else, is checked against these bytes.
            UUID s = new UUID("a0000000-0000-4000-8000-000000000001");
            UUID a = new UUID("a0000000-0000-4000-8000-000000000002");
            UUID b = new UUID("a0000000-0000-4000-8000-000000000003");
            UUID r = new UUID("a0000000-0000-4000-8000-000000000004");
            UUID t = new UUID("a0000000-0000-4000-8000-000000000005");
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            VoiceSessionServicesConnector c = NewClient(server);

            c.Start(2, a, b, t, r, 50);
            c.StartAdhoc(a, t, r, new List<UUID> { b, s }, 0);
            c.CheckInviteCount(s, a, new List<UUID> { b });
            c.Invite(s, a, b);
            c.Accept(s, a, r, "vs-1");
            c.MarkPresent(s, a, r, "vs-1");
            c.Decline(s, a);
            c.Depart(s, a, 3);
            c.LeaveChat(s, a);
            c.DepartByViewerSession(a, "vs-1", 4);
            c.DepartAll(a, 2);
            c.DepartRegion(r);
            c.RenewSeats(new List<SeatKey> { new SeatKey(s, a), new SeatKey(s, b) });
            c.MarkInvited(s, new List<UUID> { a, b });
            c.VerifyRing(s, b, a, 3);
            c.CarrierKey(s, a);
            c.Moderate(s, a, 0, b, true);
            c.Moderate(s, a, 2, UUID.Zero, false);
            c.Moderate(s, a, 3, b, true, "too loud", 30);
            c.Moderate(s, a, 3, b, false);
            c.IssueToken(s, a);
            c.TakeConferenceRequest(a);
            c.Get(s, a);
            c.GetByRoomKey("room-key", a);

            string[] expected =
            {
                $"METHOD=start&type=2&creator={a}&other={b}&temp={t}&origin={r}&cap=50",
                $"METHOD=startadhoc&creator={a}&temp={t}&origin={r}&invitees={b}%2c{s}&cap=0",
                $"METHOD=checkinvitecount&session={s}&inviter={a}&asked={b}",
                $"METHOD=invite&session={s}&inviter={a}&invitee={b}",
                $"METHOD=accept&session={s}&agent={a}&origin={r}&viewer=vs-1",
                $"METHOD=markpresent&session={s}&agent={a}&origin={r}&viewer=vs-1",
                $"METHOD=decline&session={s}&agent={a}",
                $"METHOD=depart&session={s}&agent={a}&reason=3",
                $"METHOD=leavechat&session={s}&agent={a}",
                $"METHOD=departbyviewer&agent={a}&viewer=vs-1&reason=4",
                $"METHOD=departall&agent={a}&reason=2",
                $"METHOD=departregion&region={r}",
                $"METHOD=renewseats&seats={s}%3a{a}%2c{s}%3a{b}",
                $"METHOD=markinvited&session={s}&agents={a}%2c{b}",
                $"METHOD=verifyring&session={s}&target={b}&caller={a}&type=3",
                $"METHOD=carrierkey&session={s}&agent={a}",
                $"METHOD=moderate&session={s}&by={a}&target={b}&op=0&on=1",
                $"METHOD=moderate&session={s}&by={a}&target={UUID.Zero}&op=2&on=0",
                $"METHOD=moderate&session={s}&by={a}&target={b}&op=3&on=1&reason=too+loud&minutes=30",
                $"METHOD=moderate&session={s}&by={a}&target={b}&op=3&on=0",
                $"METHOD=issuetoken&session={s}&agent={a}",
                $"METHOD=takeconferencerequest&agent={a}",
                $"METHOD=get&session={s}&agent={a}",
                $"METHOD=getbyroomkey&roomkey=room-key&agent={a}",
            };
            string[] actual;
            lock (server.Bodies)
                actual = server.Bodies.ToArray();
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void TheReplyEnvelopeHasTheDocumentedShape()
        {
            SessionReply reply = new SessionReply { Ok = true, Decision = "found", Text = "t", Flag = true, Count = 2 };

            Assert.That(reply.ToJson(), Is.EqualTo(
                "{\"Ok\":true,\"Decision\":\"found\",\"Created\":false,\"StartedRinging\":false,\"Session\":null,\"Sessions\":null,\"Text\":\"t\",\"Flag\":true,\"Count\":2}"));
            SessionReply back = SessionReply.FromJson(reply.ToJson());
            Assert.That(new object[] { back.Ok, back.Decision, back.Text, back.Flag, back.Count }, Is.EqualTo(new object[] { true, "found", "t", true, 2 }));
            Assert.That(SessionReply.FromJson("not json"), Is.Null);
            Assert.That(SessionReply.FromJson(""), Is.Null);
        }

        [Test]
        public void ARoundTripThroughTheHandlerOnLoopback_IsReportedAsNumbers()
        {
            // Not a tight assertion: the figure that matters is the one measured between two hosts. This puts a number
            // on the handler, the authentication and the serialisation alone.
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));
            region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            UUID id = region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            region.Get(id);   // the first request pays for the connection and the JIT

            const int n = 50;
            List<double> ms = new List<double>();
            for (int i = 0; i < n; i++)
            {
                System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                Assert.That(region.Get(id), Is.Not.Null);
                ms.Add(w.Elapsed.TotalMilliseconds);
            }
            ms.Sort();
            TestContext.Progress.WriteLine($"[loopback] get x{n}: median {ms[n / 2]:F2} ms, p95 {ms[(int)(n * 0.95)]:F2} ms, max {ms[n - 1]:F2} ms");
            Assert.That(ms[n / 2], Is.LessThan(250), "a loopback request is nowhere near a quarter of a second");
        }

        [Test]
        public void AServiceThatDoesNotAnswerInTime_IsRefusedCleanly_AndTheClientDoesNotWaitForever()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            server.Answering.Reset();
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", server.BaseUri, 1000);
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(client);

            System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
            SessionOutcome refused = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            waited.Stop();

            Assert.That(refused.Ok, Is.False);
            Assert.That(refused.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            Assert.That(waited.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)), "it gave up at its own timeout, not the server's");
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void ACallToAServiceThatHasHung_Answers503()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", server.BaseUri, 1000);
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(client);
            A2ACalls calls = new A2ACalls(() => region);
            Assert.That(calls.Record(Alice, Bob, RegionA, out _), Is.Not.Null);
            server.Answering.Reset();

            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(new OpenMetaverse.StructuredData.OSDMap
            {
                ["method"] = OpenMetaverse.StructuredData.OSD.FromString("call"),
                ["session-id"] = OpenMetaverse.StructuredData.OSD.FromUUID(A2ACalls.ComputeSessionId(Alice, Bob)),
            }, Alice, calls);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "a timeout is not the store saying the call is gone");
        }

        [Test]
        public void AServiceWithNoSuchSession_AnswersTheStartUpCheck()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", server.BaseUri, 1000);

            SessionReply reply = client.Get(UUID.Zero);

            Assert.That(reply, Is.Not.Null, "an answer, even a refusal, means the service is there");
            Assert.That(reply.Ok, Is.False);
        }

        [Test]
        public void NothingAtTheAddress_DoesNotAnswerTheStartUpCheck()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            TestServer first = new TestServer(NewHandler(service));
            string uri = first.BaseUri;
            first.Dispose();

            Assert.That(new VoiceSessionServicesConnector(Config(), "WebRtcVoice", uri, 1000).Get(UUID.Zero), Is.Null);
        }

        [Test]
        public void AWrongPassword_DoesNotAnswerTheStartUpCheck()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));

            Assert.That(NewClient(server, Config(password: "not-the-service-password-not-the-service")).Get(UUID.Zero), Is.Null);
        }
    }
}
