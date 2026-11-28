/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the connector join-capability endpoint.
 *
 * The secret-unset, short-secret, uniform-404, right-bearer, minting-disabled and after-Unregister flows route every request through a real BaseHttpServer's own handler lookup (TryGetSimpleStreamHandler,
 * reached by reflection: a STARTED server needs the region's WorkManager statics, which this test host lacks), with the
 * endpoint attached exactly as VoiceConnectorModule attaches it. A path with no handler answers as the server would,
 * with its HTML 404, so "indistinguishable" covers the case the var-path registration exists for: an unknown name
 * must reach this handler's empty 404, never the server's HTML one.
 *
 * The right-bearer flow verifies the capability with a C# transcription of the slvoice mixer's validator
 * (joincap.c: slv_joincap_parse, then slv_joincap_check), not with the minter: signature over "v1." + payload, then agent,
 * session, room, and the lifetime.
 *
 * A further test captures every log line the endpoint and the module's config load produce (both the injected logger and the
 * process LoggerProvider factory) across the first five flows and asserts no bearer, secret or capability appears.
 */

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class ConnectorJoinCapEndpointTests : IAsyncLifetime
        {
        private const string Secret = "connector-bearer-secret-0123456789abcdef";          // 40 chars
        private const string MintKey = "mixer-join-cap-hmac-key-fedcba9876543210";       // [JanusWebRtcVoice] JoinCapabilitySecret
        private const int Room = 226001844;
        private static readonly UUID Npc = new UUID("0c0c0c0c-0000-4000-8000-00000000c0c0");

        // ---- log capture ----
        private sealed class Capture : ILoggerProvider, ILogger
        {
            public readonly List<string> Lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
            }
            public void Dispose() { }
        }

        private Capture m_capture;
        private LogCapture m_logging;
        private BaseHttpServer m_server;
        private VoiceConnectorJoinCapEndpoint m_endpoint;
        private readonly List<string> m_minted = new();
        private void SetUp()
        {
            m_capture = new Capture();
            m_logging = new LogCapture(m_capture, LogLevel.Trace);
            JoinCapabilityAuthority.Clear();
        }
        private void TearDown()
        {
            m_server = null;
            m_endpoint = null;
            m_logging.Dispose();
            m_capture.Dispose();
            JoinCapabilityAuthority.Clear();
        }

        private BaseHttpServer StartServer()
        {
            m_server = new BaseHttpServer(9);   // never started: only its handler table is used
            return m_server;
        }

        private static readonly MethodInfo s_lookup = typeof(BaseHttpServer).GetMethod("TryGetSimpleStreamHandler",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly byte[] ServerHtml404 = Encoding.UTF8.GetBytes("<the server's own HTML 404>");

        private static VoiceConnectorRecord Record(string name = "Recorder", string secret = Secret, bool active = true)
        {
            var r = new VoiceConnectorRecord(name, true, name, "NPC", new Vector3(128, 128, 25),
                VoiceConnectorScope.Estate, false, "Operator", null, secret);
            if (active)
            {
                r.NpcId = Npc;
                r.ViewerSessionId = "5e551011-0000-4000-8000-000000005e55";
                r.Room = Room;
            }
            return r;
        }

        /// One request as the server would route it: its own lookup, then the endpoint's answer, or the HTML 404.
        private (int Status, byte[] Body, string ContentType) Post(string name, string bearer, string method = "POST")
        {
            string path = $"/voice/connector/{name}/join-cap";
            {
                var assertedValue = s_lookup;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "BaseHttpServer.TryGetSimpleStreamHandler exists");
            }
            object[] args = { path, null };
            if (!(bool)s_lookup.Invoke(m_server, args))
                return (404, ServerHtml404, "text/html");
            Assert.Equal(VoiceConnectorJoinCapEndpoint.PathRoot, ((ISimpleStreamHandler)args[1]).Path);
            VoiceConnectorJoinCapEndpoint.Response r = m_endpoint.Handle(method, path, bearer is null ? null : "Bearer " + bearer);
            return (r.Status, r.Body, r.ContentType);
        }

        private VoiceConnectorJoinCapEndpoint Endpoint(IHttpServer server)
            => m_endpoint = new VoiceConnectorJoinCapEndpoint(server, LoggerProvider.CreateLogger("test.joincap"));

        // ---- the mixer's validator, transcribed (src/joincap.c slv_joincap_parse + slv_joincap_check) ----
        private static string[] VerifyLikeTheMixer(string cap, string key)
        {
            string[] parts = cap.Split('.');
            {
                var assertedValue = parts.Length;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "v1.<payload>.<sig>");
            }
            Assert.Equal("v1", parts[0]);
            string signing = parts[0] + "." + parts[1];
            using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            byte[] want = mac.ComputeHash(Encoding.UTF8.GetBytes(signing));
            Assert.True(CryptographicOperations.FixedTimeEquals(want, B64UrlDecode(parts[2])), "signature verifies under the key");
            string[] f = Encoding.UTF8.GetString(B64UrlDecode(parts[1])).Split('|');
            {
                var assertedValue = f.Length;
                TestAssert.WithMessage(() => Assert.Equal(8, assertedValue), "agent|session|room|epoch|generation|iat|exp|nonce");
            }
            return f;
        }

        private static byte[] B64UrlDecode(string s)
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
        }

        // ---- flows (each test runs one; the log-capture test runs them all) ----

        private void FlowSecretUnset()
        {
            // The config default: no CapabilitySecret. Loaded through the real registry, attached as the module attaches.
            var cfg = new IniConfigSource();
            IConfig s = cfg.AddConfig("VoiceConnector.Recorder");
            s.Set("Enabled", "true"); s.Set("NpcFirstName", "Recorder"); s.Set("NpcLastName", "NPC"); s.Set("Position", "<128,128,25>");
            VoiceConnectorLoadResult load = VoiceConnectorRegistry.LoadFrom(cfg, "NPC");
            {
                var assertedValue = load.Registry.Snapshot().Single().CapabilitySecret;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "unset is the default");
            }
            Assert.Empty(load.Warnings);

            BaseHttpServer server = StartServer();
            int before = server.GetSimpleStreamHandlerKeys().Count;
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => load.Registry.Snapshot(), true, MintKey));
            Assert.False(ep.IsRegistered, "no handler registered");
            {
                var assertedValue = server.GetSimpleStreamHandlerKeys();
                Assert.Equal(before, assertedValue.Count());
                Assert.DoesNotContain("/voice", assertedValue);
            }
            // What a peer then sees is the server's own 404, exactly as if this endpoint did not exist.
            Assert.Equal(ServerHtml404, Post("Recorder", Secret).Body);
        }

        private void FlowShortSecret()
        {
            var cfg = new IniConfigSource();
            IConfig s = cfg.AddConfig("VoiceConnector.Recorder");
            s.Set("Enabled", "true"); s.Set("NpcFirstName", "Recorder"); s.Set("NpcLastName", "NPC"); s.Set("Position", "<128,128,25>");
            s.Set("CapabilitySecret", "short-secret-31-chars-abcdefghi");
            Assert.Equal(31, "short-secret-31-chars-abcdefghi".Length);
            // The module's own Initialise: it logs the load warnings.
            var module = new VoiceConnectorModule();
            cfg.AddConfig("WebRtcVoice").Set("ConnectorsEnabled", "true");
            cfg.AddConfig("JanusWebRtcVoice").Set("PluginName", "janus.plugin.slvoice");   // connectors need the slvoice mixer
            module.Initialise(cfg);
            lock (m_capture.Lines)
                Assert.True(m_capture.Lines.Any(l => l.StartsWith("Warning:") && l.Contains("CapabilitySecret is shorter than 32")), "a short secret logs a WARN");

            VoiceConnectorLoadResult load = VoiceConnectorRegistry.LoadFrom(cfg, "NPC");
            VoiceConnectorRecord r = load.Registry.Snapshot().Single();
            {
                var assertedValue = r.CapabilitySecret;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "a weak key is dropped, never enforced with");
            }
            r.NpcId = Npc; r.ViewerSessionId = "5e551011-0000-4000-8000-000000005e55"; r.Room = Room;   // even active

            BaseHttpServer server = StartServer();
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => load.Registry.Snapshot(), true, MintKey));
            Assert.False(ep.IsRegistered, "no handler for a short secret");
            Assert.DoesNotContain("/voice", server.GetSimpleStreamHandlerKeys());
        }

        private void FlowUniform404()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord active = Record("Recorder");
            VoiceConnectorRecord inactive = Record("Injector", active: false);
            VoiceConnectorRecord noSecret = Record("Plain", secret: null);
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { active, inactive, noSecret }, true, MintKey));
            Assert.True(ep.IsRegistered);

            var answers = new Dictionary<string, (int Status, byte[] Body, string ContentType)>
            {
                ["wrong bearer"] = Post("Recorder", "not-the-secret-not-the-secret-not-the-secret"),
                ["no bearer"] = Post("Recorder", null),
                ["unknown name"] = Post("Nobody", Secret),
                ["inactive record"] = Post("Injector", Secret),
                ["record without a secret"] = Post("Plain", Secret),
                ["GET with the right bearer"] = Post("Recorder", Secret, "GET"),
            };
            foreach (var (what, a) in answers)
            {
                {
                    var assertedValue = a.Status;
                    TestAssert.WithMessage(() => Assert.Equal(404, assertedValue), what);
                }
                {
                    var assertedValue = a.Body;
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), what + ": empty body");
                }
            }
            var distinct = answers.Values.Select(a => (a.Status, Convert.ToBase64String(a.Body), a.ContentType)).Distinct().ToList();
            {
                var assertedValue = distinct;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "every pre-authentication failure is the same answer");
            }
        }

        private void FlowRightBearer()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord r = Record();
            JoinCapabilityAuthority.Publish(Room, "0000018f00000001", 7);   // an armed room, as VisAuthority publishes it
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, MintKey));

            var a = Post("Recorder", Secret);
            {
                var assertedValue = a.Status;
                TestAssert.WithMessage(() => Assert.Equal(200, assertedValue), "right bearer");
            }
            var body = (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(a.Body));
            string cap = body["join_cap"].AsString();
            m_minted.Add(cap);
            Assert.Equal(Npc.ToString(), body["display"].AsString());
            Assert.Equal(Room, body["room"].AsInteger());
            Assert.Equal(r.ViewerSessionId, body["session_id"].AsString());

            string[] f = VerifyLikeTheMixer(cap, MintKey);
            {
                var assertedValue = f[0];
                TestAssert.WithMessage(() => Assert.Equal(Npc.ToString(), assertedValue), "agent = the NPC id");
            }
            {
                var assertedValue = f[1];
                TestAssert.WithMessage(() => Assert.Equal(r.ViewerSessionId, assertedValue), "session = the record's ViewerSessionId");
            }
            {
                var assertedValue = int.Parse(f[2]);
                TestAssert.WithMessage(() => Assert.Equal(Room, assertedValue), "room = the recorded room");
            }
            {
                var assertedValue = f[3];
                TestAssert.WithMessage(() => Assert.Equal("0000018f00000001", assertedValue), "epoch from JoinCapabilityAuthority, as an avatar's join");
            }
            {
                var assertedValue = f[4];
                TestAssert.WithMessage(() => Assert.Equal("7", assertedValue), "generation from JoinCapabilityAuthority, as an avatar's join");
            }
            Assert.Equal(JoinCapability.LifetimeSeconds, long.Parse(f[6]) - long.Parse(f[5]));
            Assert.Equal(long.Parse(f[6]), body["expires"].AsLong());
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            {
                var assertedValue = now;
                TestAssert.WithMessage(() => Assert.InRange(assertedValue, long.Parse(f[5]) - 120, long.Parse(f[6]) + 120), "fresh under the mixer's skew rule");
            }
        }

        private void FlowMintingDisabled()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord r = Record();
            var ep = Endpoint(server);
            var off = new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, false, MintKey);
            ep.Attach(off);
            var a = Post("Recorder", Secret);
            {
                var assertedValue = a.Status;
                TestAssert.WithMessage(() => Assert.Equal(503, assertedValue), "JoinCapabilityEnabled false");
            }
            Assert.Empty(a.Body);
            // No JoinCapabilitySecret: the module passes enabled=false then (WebRtcJanusService's own rule), and the
            // minter refuses an empty key besides.
            ep.Detach(off);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, string.Empty));
            {
                var assertedValue = Post("Recorder", Secret).Status;
                TestAssert.WithMessage(() => Assert.Equal(503, assertedValue), "no JoinCapabilitySecret");
            }
            {
                var assertedValue = Post("Recorder", "wrong-wrong-wrong-wrong-wrong-wrong-wrong").Status;
                TestAssert.WithMessage(() => Assert.Equal(404, assertedValue), "503 only after authentication");
            }
        }

        private void FlowAfterUnregister()
        {
            BaseHttpServer server = StartServer();
            VoiceConnectorRecord r = Record();
            r.NpcId = UUID.Zero; r.ViewerSessionId = null; r.Room = null;
            var ep = Endpoint(server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, MintKey));
            string session = null;
            VoiceConnectorRegistrar.Register(r, Room, _ => Npc,
                id => { var vs = new VoiceViewerSession(null, UUID.Random(), id); session = vs.ViewerSessionID; return vs; },
                (_, _) => { }, _ => { }, null);
            try
            {
                {
                    var assertedValue = Post("Recorder", Secret).Status;
                    TestAssert.WithMessage(() => Assert.Equal(200, assertedValue), "registered: served");
                }
            }
            finally
            {
                VoiceConnectorRegistrar.Unregister(r, _ => { }, null);
            }
            var after = Post("Recorder", Secret);
            {
                var assertedValue = after.Status;
                TestAssert.WithMessage(() => Assert.Equal(404, assertedValue), "after Unregister");
            }
            Assert.Empty(after.Body);
            {
                var assertedValue = after;
                TestAssert.WithMessage(() => Assert.Equal<(int, byte[], string)>(Post("Nobody", Secret), assertedValue, (x, y) => x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2) && x.Item3 == y.Item3), "and indistinguishable from an unknown name");
            }
            Assert.NotNull(session);
        }

        private void Reset()
        {
            m_server = null;
            m_endpoint = null;
            JoinCapabilityAuthority.Clear();
        }

        /// An unpinned record with a secret, in an instance with two regions, is named at startup with
        /// "add Region="; the request-time 404 stays; a pinned record and a single-region instance warn nothing.
        [Fact(Timeout = 60000)]
        public void L3_UnpinnedRecordWithSecret_InAMultiRegionInstance_WarnsAtStartup_And404Stays()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartServer();
            var ep = Endpoint(m_server);
            VoiceConnectorRecord unpinnedA = Record("Recorder");
            VoiceConnectorRecord pinned = new VoiceConnectorRecord("Pinned", true, "Pinned", "NPC", new Vector3(1, 1, 1),
                VoiceConnectorScope.Estate, false, "Operator", "Example Region", Secret);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { unpinnedA, pinned }, true, MintKey));
            List<string> Warnings() { lock (m_capture.Lines) return m_capture.Lines.Where(l => l.StartsWith("Warning:")).ToList(); }
            {
                var assertedValue = Warnings();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "one region: nothing to warn about");
            }

            // The second region's instance loads the same records; the unpinned one starts there too.
            VoiceConnectorRecord unpinnedB = Record("Recorder");
            unpinnedB.NpcId = new UUID("0c0c0c0c-0000-4000-8000-00000000c0c1");
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { unpinnedB, pinned }, true, MintKey));
            List<string> warned = Warnings();
            {
                var assertedValue = warned;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "one WARN, for the unpinned record only");
            }
            {
                var assertedValue = warned[0];
                TestAssert.WithMessage(() =>
                {
                    Assert.Contains("Recorder", assertedValue);
                    Assert.Contains("Add Region=", assertedValue);
                }, "names the record and says add Region=");
            }
            Assert.DoesNotContain(Secret, warned[0]);

            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { Record("Recorder") }, true, MintKey));
            {
                var assertedValue = Warnings();
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "once per record, not once per region");
            }
            {
                var assertedValue = Post("Recorder", Secret);
                TestAssert.WithMessage(() => Assert.Equal<(int, byte[], string)>((404, Array.Empty<byte>(), (string)null), assertedValue, (x, y) => x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2) && x.Item3 == y.Item3), "the request-time 404 stays");
            }
        }

        // ---- the room is resolved like an avatar's and ensured to exist ----

        private static LandData Land(int localId, bool estateChannel)
        {
            var land = new LandData { LocalID = localId };
            if (estateChannel)
                land.Flags |= (uint)ParcelFlags.UseEstateVoiceChan;
            return land;
        }

        /// A connector on a parcel that runs its OWN voice channel records that parcel's room; on an
        /// estate-channel parcel it records the estate room.
        [Fact(Timeout = 60000)]
        public void T1_TheRecordedRoomIsTheOneAnAvatarAtThatPositionWouldGet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID region = new UUID("c44606b1-43e1-45fb-8ae8-201545dc2f6a");
            int estateRoom = JanusAudioBridge.CalcRoomNumber(string.Empty, region.ToString(), "local",
                JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            int parcelRoom = JanusAudioBridge.CalcRoomNumber(string.Empty, region.ToString(), "local", 1, string.Empty);

            {
                var assertedValue = ConnectorRoomResolver.RoomFor(region, Land(1, estateChannel: false));
                TestAssert.WithMessage(() => Assert.Equal(parcelRoom, assertedValue), "an own-channel parcel: the parcel's own room, the one an avatar standing there is provisioned into");
            }
            {
                var assertedValue = ConnectorRoomResolver.RoomFor(region, Land(1, estateChannel: true));
                TestAssert.WithMessage(() => Assert.Equal(estateRoom, assertedValue), "an estate-channel parcel: the estate room, the same number an avatar's provision computes");
            }
            {
                var assertedValue = ConnectorRoomResolver.RoomFor(region, null);
                TestAssert.WithMessage(() => Assert.Equal(estateRoom, assertedValue), "no parcel at all: the estate channel, as the provisioning path also treats it");
            }
            Assert.False(ConnectorRoomResolver.IsEstateChannel(Land(7, estateChannel: false)));
            Assert.True(ConnectorRoomResolver.IsEstateChannel(Land(7, estateChannel: true)));
            {
                var assertedValue = parcelRoom;
                TestAssert.WithMessage(() => Assert.NotEqual(estateRoom, assertedValue), "the two rooms really are different numbers");
            }
        }

        /// A capability fetch ensures the room exists BEFORE minting, and a second fetch with the room already
        /// present ensures again idempotently without creating anything new.
        [Fact(Timeout = 60000)]
        public void T2_AFetchEnsuresTheRoomBeforeMinting_AndASecondFetchCreatesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartServer();
            VoiceConnectorRecord r = Record();
            var ensured = new List<int>();
            var created = new HashSet<int>();
            int? Ensure(VoiceConnectorRecord rec)
            {
                ensured.Add(Room);
                created.Add(Room);       // the real seam coalesces: a live room is reused, never re-created
                return Room;
            }
            var ep = Endpoint(m_server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, MintKey, Ensure));

            var a = Post("Recorder", Secret);
            Assert.Equal(200, a.Status);
            {
                var assertedValue = ensured;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the room was ensured on the fetch");
            }
            string[] f = VerifyLikeTheMixer(((OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(a.Body)))["join_cap"].AsString(), MintKey);
            {
                var assertedValue = int.Parse(f[2]);
                TestAssert.WithMessage(() => Assert.Equal(Room, assertedValue), "and the capability names that room");
            }

            var b = Post("Recorder", Secret);
            Assert.Equal(200, b.Status);
            {
                var assertedValue = ensured;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "every fetch ensures");
            }
            {
                var assertedValue = created;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "but only one room was ever needed");
            }
        }

        /// The endpoint half: the parcel's channel changes between two fetches, so the record MOVES and the
        /// capability names the new room.
        [Fact(Timeout = 60000)]
        public void T5_WhenTheParcelChannelChanges_TheRecordMovesAndTheCapabilityFollows()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartServer();
            VoiceConnectorRecord r = Record();
            int current = Room;
            int? Ensure(VoiceConnectorRecord rec)
            {
                if (rec.Room != current)
                    rec.Room = current;   // the module moves the record, as a re-provision would
                return current;
            }
            var ep = Endpoint(m_server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, MintKey, Ensure));

            string[] first = VerifyLikeTheMixer(((OSDMap)OSDParser.DeserializeJson(
                Encoding.UTF8.GetString(Post("Recorder", Secret).Body)))["join_cap"].AsString(), MintKey);
            Assert.Equal(Room, int.Parse(first[2]));

            current = 1966197062;   // the parcel now runs its own channel
            var body = (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(Post("Recorder", Secret).Body));
            {
                var assertedValue = body["room"].AsInteger();
                TestAssert.WithMessage(() => Assert.Equal(current, assertedValue), "the grant carries the new room");
            }
            string[] second = VerifyLikeTheMixer(body["join_cap"].AsString(), MintKey);
            {
                var assertedValue = int.Parse(second[2]);
                TestAssert.WithMessage(() => Assert.Equal(current, assertedValue), "and so does the capability");
            }
            {
                var assertedValue = r.Room;
                TestAssert.WithMessage(() => Assert.Equal(current, assertedValue), "the record moved with it");
            }
        }

        /// <summary>A connector with NO
        /// CapabilitySecret cannot authenticate, so it never reaches the capability path, so NOTHING ever ensures its
        /// room - the sim creates nothing on its behalf and the room simply stays unknown until a viewer provisions
        /// into it. Arming to it is then held by the backoff, which the room tests measure.
        ///
        /// There is deliberately no callback from the visibility authority back into the connector registry to create
        /// a room for exactly this connector. Once join capabilities are required, a
        /// connector with no secret cannot hold one, so it cannot join a declared room: a room created for it would
        /// be a room nothing can enter. The fix for such a connector is to give it a CapabilitySecret, which is what
        /// the connectors README says.</summary>
        [Fact(Timeout = 60000)]
        public void C3_AConnectorWithNoCapabilitySecret_EnsuresNothing_AndItsRoomStaysUnknown()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartServer();
            VoiceConnectorRecord noSecret = Record("Plain", secret: null);
            int ensures = 0;
            var ep = Endpoint(m_server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { noSecret }, true, MintKey,
                rec => { ensures++; return Room; }));

            Assert.False(ep.IsRegistered, "with no record holding a secret the endpoint is not even served: there is nothing to authenticate");
            foreach (string bearer in new[] { Secret, null, "" })
            {
                var a = Post("Plain", bearer);
                {
                    var assertedValue = a.Status;
                    TestAssert.WithMessage(() => Assert.Equal(404, assertedValue), "a record with no secret is not served, whatever is presented");
                }
            }
            {
                var assertedValue = ensures;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "and so no room was ever ensured, let alone created, for it");
            }

            // The other half of the rule: nothing was created, so the room stays unknown - and the sim's answer to
            // that is the backoff, not a room. (How cheap it stays over two minutes is ConnectorRoomTests.)
            long clock = 10_000;
            var auth = new VisAuthority(0x0000018f00000001UL, "c3", () => clock);
            auth.RequestArm(Npc);
            auth.OnBatchOutcome(Room, VisOp.Replace, auth.NextGeneration(Room), new List<UUID> { Npc }, true,
                new JanusPeerCtlBatchSink.SlvoiceReply { Present = true, Status = "error", Reason = "unknown_room" });
            Assert.False(auth.IsArmed(Room, Npc), "the mixer does not have the room");
            Assert.False(auth.CanArmNow(Npc), "so the connector waits out a backoff rather than hammering it");
        }

        // ---- a connector has a position, in the viewer's own SLData frame -------------------------

        /// <summary>P1: a standard 256 m region at grid (1000, 1000), origin 256000 m. (origin + Position) x 100 as integers:
        /// the global centimetres a viewer standing there would send as its sp (Firestorm llvoicewebrtc.cpp:1108,
        /// :1241-1244).</summary>
        [Fact(Timeout = 60000)]
        public void P1_GlobalCentimetres_A256Region()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (int x, int y, int z) = FeederWorldFromScene.GlobalCentimetres(256000, 256000, new Vector3(128, 128, 22));
            Assert.Equal((25612800, 25612800, 2200), (x, y, z));
        }

        /// <summary>P2: a var region (1024 x 1024, like Elm) at grid (1004, 1008): the origin is still its south-west
        /// corner, and Position runs past 256 - Elm's injector stands at (574, 719, 25).</summary>
        [Fact(Timeout = 60000)]
        public void P2_GlobalCentimetres_AVarRegion_PositionPast256()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            (int x, int y, int z) = FeederWorldFromScene.GlobalCentimetres(1004 * 256, 1008 * 256, new Vector3(574, 719, 25));
            Assert.Equal(((1004 * 256 + 574) * 100, (1008 * 256 + 719) * 100, 2500), (x, y, z));
        }

        /// <summary>P3: the grant carries the position, re-computed at EVERY fetch: when the record's Position changes (a
        /// config reload makes a new record under the same name), the next grant says so.</summary>
        [Fact(Timeout = 60000)]
        public void P3_TheGrantCarriesThePosition_AndItFollowsTheRecordsPosition()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartServer();
            VoiceConnectorRecord current = Record();
            var ep = Endpoint(m_server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { current }, true, MintKey,
                rec => Room, rec => FeederWorldFromScene.GlobalCentimetres(256000, 256000, rec.Position)));

            OSDMap first = (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(Post("Recorder", Secret).Body));
            Assert.True(first.ContainsKey("position"), "the grant carries a position");
            var p1 = (OSDMap)first["position"];
            {
                var assertedValue = (p1["x"].AsInteger(), p1["y"].AsInteger(), p1["z"].AsInteger());
                TestAssert.WithMessage(() => Assert.Equal((25612800, 25612800, 2500), assertedValue), "Record() stands at (128, 128, 25) in a region whose origin is 256000 m");
            }
            {
                var assertedValue = p1["x"].Type;
                TestAssert.WithMessage(() => Assert.Equal(OSDType.Integer, assertedValue), "integers, as the viewer sends");
            }

            VoiceConnectorRecord moved = new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC", new Vector3(140, 100, 30),
                VoiceConnectorScope.Estate, false, "Operator", null, Secret)
            {
                NpcId = current.NpcId, ViewerSessionId = current.ViewerSessionId, Room = current.Room,
            };
            current = moved;
            OSDMap second = (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(Post("Recorder", Secret).Body));
            var p2 = (OSDMap)second["position"];
            {
                var assertedValue = (p2["x"].AsInteger(), p2["y"].AsInteger(), p2["z"].AsInteger());
                TestAssert.WithMessage(() => Assert.Equal((25614000, 25610000, 3000), assertedValue), "the next fetch carries the new Position");
            }
        }

        /// After Unregister nothing more is ensured or minted for that record.
        [Fact(Timeout = 60000)]
        public void T7_AfterUnregister_NothingIsEnsuredOrMintedForThatRecord()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StartServer();
            VoiceConnectorRecord r = Record();
            int ensures = 0;
            var ep = Endpoint(m_server);
            ep.Attach(new VoiceConnectorJoinCapEndpoint.Source(() => new[] { r }, true, MintKey,
                rec => { ensures++; return Room; }));
            Assert.Equal(200, Post("Recorder", Secret).Status);
            Assert.Equal(1, ensures);

            VoiceConnectorRegistrar.Unregister(r, _ => { }, null);
            var after = Post("Recorder", Secret);
            {
                var assertedValue = after.Status;
                TestAssert.WithMessage(() => Assert.Equal(404, assertedValue), "an inactive record is not served");
            }
            Assert.Empty(after.Body);
            {
                var assertedValue = ensures;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "and nothing was ensured for it after Unregister");
            }
        }

        [Fact(Timeout = 60000)]
        public void SecretUnset_NoHandlerRegistered()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlowSecretUnset();
        }

        [Fact(Timeout = 60000)]
        public void SecretUnder32Chars_Warns_NoHandler()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlowShortSecret();
        }

        [Fact(Timeout = 60000)]
        public void WrongBearer_UnknownName_InactiveRecord_AllTheSame404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlowUniform404();
        }

        [Fact(Timeout = 60000)]
        public void RightBearer_200_CapabilityValidatesLikeTheMixer()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlowRightBearer();
        }

        [Fact(Timeout = 60000)]
        public void RightBearer_MintingDisabled_503()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlowMintingDisabled();
        }

        [Fact(Timeout = 60000)]
        public void AfterUnregister_404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlowAfterUnregister();
        }
        [Fact(Timeout = 60000)]
        public void LogsAcrossTheServedFlows_CarryNoBearerSecretOrCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlowSecretUnset(); Reset();
            FlowShortSecret(); Reset();
            FlowUniform404(); Reset();
            FlowRightBearer(); Reset();
            FlowMintingDisabled(); Reset();
            List<string> lines;
            lock (m_capture.Lines)
                lines = m_capture.Lines.ToList();
            Assert.True(lines.Any(l => l.Contains("join capability minted for room")), "the capture saw the mint line");
            Assert.NotEmpty(m_minted);
            var forbidden = new List<string> { Secret, MintKey, "short-secret-31-chars-abcdefghi",
                "not-the-secret-not-the-secret-not-the-secret", "wrong-wrong-wrong-wrong-wrong-wrong-wrong" };
            foreach (string cap in m_minted)
            {
                forbidden.Add(cap);
                forbidden.AddRange(cap.Split('.').Skip(1));   // the payload and the signature on their own
            }
            foreach (string f in forbidden)
                {
                    var assertedValue = lines.Where(l => l.Contains(f));
                    TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a log line carries a secret, bearer or capability");
                }
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
