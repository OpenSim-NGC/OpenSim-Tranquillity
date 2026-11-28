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
using Xunit;
using OpenMetaverse;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void ARegionRunsACallThroughTheHandler()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));

            SessionOutcome started = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            SessionOutcome joined = region.Accept(GroupId, Bob, RegionB, "viewer-one");
            string token = region.IssueToken(GroupId, Bob);
            NonSpatialVoiceSession s = region.Get(GroupId, Bob);

            Assert.True(started.Ok, started.Decision);
            Assert.True(started.Created);
            Assert.True(started.StartedRinging);
            Assert.True(joined.Ok, joined.Decision);
            Assert.False(joined.StartedRinging);
            Assert.NotNull(token);
            Assert.Equal(2, s.SeatsHeld);
            Assert.Equal("viewer-one", s.Find(Bob).ViewerSession);
            Assert.Equal(RegionB, s.Find(Bob).OriginRegion);
            Assert.Equal(token, s.Token);
            Assert.Equal(1, service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void TheCredentialCrossesTheWireOnlyToAMember()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));
            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            string token = region.IssueToken(GroupId, Alice);
            string key = region.Get(GroupId).RoomKey;

            Assert.Equal(token, region.Get(GroupId, Alice).Token);
            Assert.Equal(token, region.GetByRoomKey(key, Alice).Token);
            Assert.True(string.IsNullOrEmpty(region.Get(GroupId).Token), "no agent named");
            Assert.True(string.IsNullOrEmpty(region.Get(GroupId, Carol).Token), "not a member");
            Assert.True(string.IsNullOrEmpty(region.GetByRoomKey(key, Carol).Token), "not a member, by room key");
        }

        [Fact(Timeout = 60000)]
        public void AReadNamingAnUnreadableAgentIsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            VoiceSessionServiceTests.NewRegion(service).Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.Null(handler.Dispatch("get", new Dictionary<string, object> { ["session"] = GroupId.ToString(), ["agent"] = "not-an-id" }));
            {
                var assertedValue = handler.Dispatch("get", new Dictionary<string, object> { ["session"] = GroupId.ToString() });
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "naming none is fine");
            }
        }

        [Fact(Timeout = 60000)]
        public void EveryOperationRoundTrips()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));

            SessionOutcome conf = region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol });
            UUID id = conf.Session.SessionId;
            Assert.True(conf.Ok, conf.Decision);
            Assert.True(region.CheckInviteCount(id, Alice, new List<UUID> { UUID.Random() }).Ok);
            UUID dave = UUID.Random();
            Assert.Equal("invited", region.Invite(id, Alice, dave).Decision);
            Assert.True(region.Accept(id, Bob, RegionA).Ok);
            Assert.True(region.MarkPresent(id, Bob, RegionA, "v").Ok);
            Assert.Equal("declined", region.Decline(id, Carol).Decision);
            Assert.Equal("chat-left", region.LeaveChat(id, Bob).Decision);
            region.MarkInvited(id, new List<UUID> { dave });
            Assert.True(region.Get(id).WasInvited(dave));
            Assert.Equal("ring-verified", region.VerifyRing(id, dave, Alice, NonSpatialSessionType.Adhoc).Decision);
            {
                var assertedValue = region.VerifyRing(id, dave, Alice, NonSpatialSessionType.Adhoc).Decision;
                TestAssert.WithMessage(() => Assert.Equal("refused-ring-used", assertedValue), "over the wire too");
            }
            Assert.True(region.CarrierKey(id, Alice).Length >= CarrierAuth.MinSecretLength, "a seated member is given the key");
            {
                var assertedValue = region.CarrierKey(id, UUID.Random());
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a stranger is not");
            }
            Assert.Equal("moderation-changed", region.Moderate(id, Alice, ModerationOp.TextMute, Bob, true).Decision);
            Assert.True(region.Get(id).IsTextMuted(Bob), "the record crossed the wire");
            Assert.True(region.TryTakeConferenceRequest(Alice));
            Assert.Single(region.DepartByViewerSession(Bob, "v", DepartureReason.VoiceTeardown));
            Assert.True(region.Depart(id, Alice, DepartureReason.VoiceTeardown).Ok);
            {
                var assertedValue = region.DepartAll(dave, DepartureReason.PresenceLost);
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the invited member leaves everything");
            }
            Assert.True(region.DepartRegion(RegionA) >= 0);
            Assert.True(region.RenewSeats(new List<SeatKey> { new SeatKey(id, Alice) }));
            Assert.Equal(id, region.GetByRoomKey(region.Get(id).RoomKey)?.SessionId);
        }

        [Fact(Timeout = 60000)]
        public void ARemovalCrossesTheWire_WithItsReasonAndLifetime_AndTheRenewalSaysWhichSeatsWereRemoved()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));
            region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            region.Accept(GroupId, Bob, RegionA, "v");

            SessionOutcome removed = region.Moderate(GroupId, Alice, ModerationOp.Remove, Bob, true, "too loud", TimeSpan.FromMinutes(30));

            Assert.Equal("moderation-changed", removed.Decision);
            CallRemoval r = region.Get(GroupId).RemovalOf(Bob);
            Assert.Equal("too loud", r.Reason);
            Assert.Equal(Alice, r.By);
            Assert.Equal(TimeSpan.FromMinutes(30), r.ExpiresUtc - r.AtUtc);
            Assert.False(region.Get(GroupId).Find(Bob).HoldsSeat);
            Assert.True(region.RenewSeats(new List<SeatKey> { new SeatKey(GroupId, Alice), new SeatKey(GroupId, Bob) },
                                          out IReadOnlyList<SeatKey> told));
            {
                var assertedValue = told;
                TestAssert.WithMessage(() => Assert.Equal(new[] { new SeatKey(GroupId, Bob) }, assertedValue), "only the removed member's seat is named");
            }
            Assert.Equal("moderation-changed", region.Moderate(GroupId, Alice, ModerationOp.Remove, Bob, false).Decision);
        }

        [Fact(Timeout = 60000)]
        public void AnOldRegionsModerateRequestIsStillRead_AndAReasonIsBoundedAndCleaned()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            VoiceSessionServiceTests.NewRegion(service).Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            Dictionary<string, object> old = new()
            {
                ["session"] = GroupId.ToString(), ["by"] = Alice.ToString(), ["target"] = Bob.ToString(), ["op"] = "0", ["on"] = "1",
            };
            Assert.True((handler.Dispatch("moderate", old)?.Ok), "no reason and no minutes: an old region's request");

            Dictionary<string, object> removal = new(old) { ["op"] = "3", ["reason"] = "ab\u0007\ncd" + new string('x', 500),["minutes"] = "99999999" };
            SessionReply r = handler.Dispatch("moderate", removal);

            Assert.True(r.Ok);
            CallRemoval stored = SessionWire.FromData(r.Session).RemovalOf(Bob);
            Assert.True(stored.Reason.Length <= 200);
            {
                var assertedValue = stored.Reason;
                TestAssert.WithMessage(() => Assert.StartsWith("abcdxxx", assertedValue), "control characters are dropped");
            }
            {
                var assertedValue = stored.ExpiresUtc - stored.AtUtc;
                TestAssert.WithMessage(() => Assert.Equal(TimeSpan.FromMinutes(VoiceSessionService.MaxRemovalMinutes), assertedValue), "bounded");
            }

            old["minutes"] = "not a number";
            old["op"] = "3";
            Assert.True((handler.Dispatch("moderate", old)?.Ok), "an unreadable lifetime is taken as none");
        }

        // ---- protection ---------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ARegionWithTheWrongPasswordIsRefusedAndNothingChanges()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server, Config(password: "not-the-service-password-not-the-service")));

            SessionOutcome o = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.False(o.Ok);
            Assert.Equal(SessionOutcome.StoreUnavailable, o.Decision);
            Assert.Equal(0, service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void ARequestWithNoCredentialIsRefusedBeforeItIsRead()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpResponse response = new TestOSHttpResponse();
            string body = "METHOD=start&type=2&creator=" + Alice + "&other=" + GroupId + "&temp=" + GroupId + "&origin=" + RegionA + "&cap=0";

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          new TestOSHttpRequest(), response);

            Assert.Equal((int)HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(reply);
            Assert.Equal(0, service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void AnEmptyConfigurationIsNotAcceptableAuthentication()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(VoiceSessionServerConnector.CheckAuthConfig(new IniConfigSource()));
        }

        [InlineData("None",User,Password)]
        [InlineData("BasicHttpAuthentication","",Password)]
        [InlineData("BasicHttpAuthentication",User,"")]
        [InlineData("BasicHttpAuthentication",User,"short-password")]
        [InlineData("BasicHttpAuthentication",User,"0123456789abcdef0123456789abcde")]   // 31 characters
        [Theory(Timeout = 60000)]
        public void AnEnabledServiceRefusesToLoadWithoutAcceptableAuthentication(string authType, string user, string password)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IniConfigSource config = new IniConfigSource();
            IConfig c = config.AddConfig(VoiceSessionServerConnector.SectionName);
            c.Set("Enabled", "true");
            c.Set("AuthType", authType);
            c.Set("HttpAuthUsername", user);
            c.Set("HttpAuthPassword", password);

            Assert.NotNull(VoiceSessionServerConnector.CheckAuthConfig(config));
            Assert.Throws<Exception>(() => new VoiceSessionServerConnector(config, null, VoiceSessionServerConnector.SectionName));
        }

        [Fact(Timeout = 60000)]
        public void AnAcceptablePasswordPassesTheCheck_AndThe32CharacterBoundaryIsInclusive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IniConfigSource config = Config("0123456789abcdef0123456789abcdef");   // exactly 32
            Assert.Null(VoiceSessionServerConnector.CheckAuthConfig(config));
        }

        [Fact(Timeout = 60000)]
        public void ADisabledServiceServesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            IniConfigSource none = new IniConfigSource();
            {
                var assertedValue = () => new VoiceSessionServerConnector(none, null, VoiceSessionServerConnector.SectionName);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            IniConfigSource off = new IniConfigSource();
            off.AddConfig(VoiceSessionServerConnector.SectionName).Set("Enabled", "false");
            {
                var assertedValue = () => new VoiceSessionServerConnector(off, null, VoiceSessionServerConnector.SectionName);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
        }

        // ---- malformed requests change nothing -----------------------------------------------------------

        [InlineData("")]
        [InlineData("not a form")]
        [InlineData("type=2&creator=x")]
        [InlineData("METHOD=nosuchmethod")]
        [InlineData("METHOD=start")]
        [InlineData("METHOD=start&type=two&creator=11111111-1111-1111-1111-111111111111&other=x&temp=x&origin=x&cap=0")]
        [InlineData("METHOD=start&type=2&creator=not-a-uuid&other=99999999-9999-9999-9999-999999999999&temp=99999999-9999-9999-9999-999999999999&origin=99999999-9999-9999-9999-999999999999&cap=0")]
        [InlineData("METHOD=accept&session=99999999-9999-9999-9999-999999999999")]
        [InlineData("METHOD=startadhoc&creator=11111111-1111-1111-1111-111111111111&temp=22222222-2222-2222-2222-222222222222&origin=33333333-3333-3333-3333-333333333333&cap=0&invitees=11111111-1111-1111-1111-111111111111,garbage")]
        [InlineData("METHOD=renewseats&seats=garbage")]
        [InlineData("METHOD=depart&session=99999999-9999-9999-9999-999999999999&agent=11111111-1111-1111-1111-111111111111&reason=banana")]
        [InlineData("METHOD=getbyroomkey&roomkey=")]
        [Theory(Timeout = 60000)]
        public void AMalformedRequestIsAFailureAndChangesNothing(string body)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpRequest request = new TestOSHttpRequest();
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          request, new TestOSHttpResponse());

            string text = Encoding.UTF8.GetString(reply);
            {
                var assertedValue = text;
                TestAssert.WithMessage(() => Assert.Contains("Failure", assertedValue), text);
            }
            Assert.Equal(0, service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void ARequestOverTheSizeLimitIsAFailure()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpRequest request = new TestOSHttpRequest();
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));
            string body = "METHOD=renewseats&seats=" + new string('a', VoiceSessionPostHandler.MaxBodyChars + 1);

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          request, new TestOSHttpResponse());

            Assert.Contains("Failure", Encoding.UTF8.GetString(reply));
        }

        // ---- the service is not reachable ------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void WithNothingListening_ARegionIsRefusedCleanly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            TestServer first = new TestServer(NewHandler(service));
            string uri = first.BaseUri;
            first.Dispose();   // Robust is down: the port is closed
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", uri, 1000);
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(client);

            SessionOutcome refused = default;
            {
                var assertedValue = () => refused = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            Assert.False(refused.Ok);
            Assert.Equal(SessionOutcome.StoreUnavailable, refused.Decision);
            Assert.Null(region.Get(GroupId));
            {
                var assertedValue = service.SessionCount;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no session anywhere");
            }
        }

        // ---- the wire contract ---------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void EveryRequestHasTheDocumentedBody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.Equal(expected, actual);
        }

        [Fact(Timeout = 60000)]
        public void TheReplyEnvelopeHasTheDocumentedShape()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            SessionReply reply = new SessionReply { Ok = true, Decision = "found", Text = "t", Flag = true, Count = 2 };

            Assert.Equal("{\"Ok\":true,\"Decision\":\"found\",\"Created\":false,\"StartedRinging\":false,\"Session\":null,\"Sessions\":null,\"Text\":\"t\",\"Flag\":true,\"Count\":2}", reply.ToJson());
            SessionReply back = SessionReply.FromJson(reply.ToJson());
            Assert.Equal(new object[] { true, "found", "t", true, 2 }, new object[] { back.Ok, back.Decision, back.Text, back.Flag, back.Count });
            Assert.Null(SessionReply.FromJson("not json"));
            Assert.Null(SessionReply.FromJson(""));
        }

        [Fact(Timeout = 60000)]
        public void ARoundTripThroughTheHandlerOnLoopback_IsReportedAsNumbers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
                Assert.NotNull(region.Get(id));
                ms.Add(w.Elapsed.TotalMilliseconds);
            }
            ms.Sort();
            TestContext.Current.TestOutputHelper.WriteLine($"[loopback] get x{n}: median {ms[n / 2]:F2} ms, p95 {ms[(int)(n * 0.95)]:F2} ms, max {ms[n - 1]:F2} ms");
            Assert.True((ms[n / 2]) < 250, "a loopback request is nowhere near a quarter of a second");
        }

        [Fact(Timeout = 60000)]
        public void AServiceThatDoesNotAnswerInTime_IsRefusedCleanly_AndTheClientDoesNotWaitForever()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            server.Answering.Reset();
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", server.BaseUri, 1000);
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(client);

            System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
            SessionOutcome refused = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            waited.Stop();

            Assert.False(refused.Ok);
            Assert.Equal(SessionOutcome.StoreUnavailable, refused.Decision);
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(8), "it gave up at its own timeout, not the server's");
            Assert.Equal(0, service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void ACallToAServiceThatHasHung_Answers503()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", server.BaseUri, 1000);
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(client);
            A2ACalls calls = new A2ACalls(() => region);
            Assert.NotNull(calls.Record(Alice, Bob, RegionA, out _));
            server.Answering.Reset();

            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(new OpenMetaverse.StructuredData.OSDMap
            {
                ["method"] = OpenMetaverse.StructuredData.OSD.FromString("call"),
                ["session-id"] = OpenMetaverse.StructuredData.OSD.FromUUID(A2ACalls.ComputeSessionId(Alice, Bob)),
            }, Alice, calls);

            {
                var assertedValue = o.Status;
                TestAssert.WithMessage(() => Assert.Equal(HttpStatusCode.ServiceUnavailable, assertedValue), "a timeout is not the store saying the call is gone");
            }
        }

        [Fact(Timeout = 60000)]
        public void AServiceWithNoSuchSession_AnswersTheStartUpCheck()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", server.BaseUri, 1000);

            SessionReply reply = client.Get(UUID.Zero);

            {
                var assertedValue = reply;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "an answer, even a refusal, means the service is there");
            }
            Assert.False(reply.Ok);
        }

        [Fact(Timeout = 60000)]
        public void NothingAtTheAddress_DoesNotAnswerTheStartUpCheck()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            TestServer first = new TestServer(NewHandler(service));
            string uri = first.BaseUri;
            first.Dispose();

            Assert.Null(new VoiceSessionServicesConnector(Config(), "WebRtcVoice", uri, 1000).Get(UUID.Zero));
        }

        [Fact(Timeout = 60000)]
        public void AWrongPassword_DoesNotAnswerTheStartUpCheck()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));

            Assert.Null(NewClient(server, Config(password: "not-the-service-password-not-the-service")).Get(UUID.Zero));
        }
    }
}
