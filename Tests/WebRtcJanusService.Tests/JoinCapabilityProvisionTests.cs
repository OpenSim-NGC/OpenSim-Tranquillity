/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The join capability on the real provision path: WebRtcJanusService.ProvisionVoiceAccountRequest -> SelectRoom ->
 * CreateRoom -> JanusRoom.JoinRoom over HTTP against FakeJanusGateway. The gateway's PluginAnswer hook records every
 * plugin request body, so the tests read the create and join bodies the mixer would receive.
 *
 *   - Off (the default, or on with no usable secret): the create and join bodies carry no capability keys.
 *   - On with a 32-character secret: the join body carries join_cap and session_id, the capability verifies against the
 *     secret and names this agent, this viewer session and this room, and the create body is marked sim_created.
 *   - No capability value appears in any log line, even when the mixer refuses the join (error 496, with a reason).
 *
 * The ambient logger factory is process-wide, so the class runs on its own.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace osWebRtcVoice.Tests
{
    public class JoinCapabilityProvisionTests : IAsyncLifetime
        {
        private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef";

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
            public List<(LogLevel Level, string Text)> Snapshot()
            {
                lock (Lines)
                    return Lines.ToList();
            }
        }

        private FakeJanusGateway _gw;
        private Capture _log;
        private LogCapture _logging;
        private ConcurrentQueue<OSDMap> _bodies;
        private void SetUp()
        {
            _bodies = new ConcurrentQueue<OSDMap>();
            _gw = new FakeJanusGateway { ModelRooms = true };
            _gw.PluginAnswer = body => { _bodies.Enqueue(body); return null; };
            JoinCapabilityAuthority.Clear();
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
        }
        private void TearDown()
        {
            JoinCapabilityAuthority.Clear();
            _logging.Dispose();
            _log.Dispose();
        }

        private WebRtcJanusService NewService(bool joinCapability, string secret)
        {
            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            if (joinCapability)
                voice.Set("JoinCapabilityEnabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", Secret);
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", Secret);
            janus.Set("PluginName", "janus.plugin.slvoice");
            janus.Set("RequestTimeoutMs", "2000");
            if (secret is not null)
                janus.Set("JoinCapabilitySecret", secret);
            return new WebRtcJanusService(config, () => new FakeJanusHandler(_gw));
        }

        private static async Task<(OSDMap Reply, UUID Agent, string ViewerSession)> Provision(WebRtcJanusService svc, UUID region)
        {
            UUID agent = UUID.Random();
            var vs = (JanusViewerSession)svc.CreateViewerSession(new OSDMap(), agent, region);
            var request = new OSDMap
            {
                ["jsep"] = new OSDMap { ["type"] = "offer", ["sdp"] = "v=0 fake-offer" },
                ["channel_type"] = "local",
                ["voice_server_type"] = "webrtc",
                ["parcel_local_id"] = JanusAudioBridge.REGION_ROOM_ID,
            };
            OSDMap reply = await Task.Run(() => svc.ProvisionVoiceAccountRequest(vs, request, agent, region));
            return (reply, agent, vs.ViewerSessionID);
        }

        // The first body of that kind. The create is sent before every join, and the gateway sees each one.
        private OSDMap BodyOf(string request) => _bodies.First(b => b["request"].AsString() == request);

        // Checks the HMAC of a minted capability against the secret and returns its payload fields.
        private static string[] Verify(string capability, string secret)
        {
            string[] parts = capability.Split('.');
            Assert.Equal(3, parts.Length);
            Assert.Equal("v1", parts[0]);
            using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            string expected = Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            {
                var assertedValue = parts[2];
                TestAssert.WithMessage(() => Assert.Equal(expected, assertedValue), "the signature is the HMAC of the secret over the payload");
            }
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
        }

        private void AssertNoCapabilityInTheLog(params string[] values)
        {
            foreach (var line in _log.Snapshot())
                foreach (string v in values.Where(v => !string.IsNullOrEmpty(v)))
                    {
                        var assertedValue = line.Text;
                        TestAssert.WithMessage(() => Assert.DoesNotContain(v, assertedValue), "a capability or secret was logged: " + line.Level);
                    }
        }

        [Fact(Timeout = 60000)]
        public async Task SwitchOff_TheCreateAndJoinBodiesCarryNoCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var svc = NewService(joinCapability: false, secret: Secret);

            var result = await Provision(svc, UUID.Random());

            Assert.True(result.Reply.ContainsKey("jsep"), "the join succeeded");
            OSDMap join = BodyOf("join");
            Assert.False(join.ContainsKey("join_cap"));
            Assert.False(join.ContainsKey("session_id"));
            Assert.False(BodyOf("create").ContainsKey("sim_created"));
        }

        [Fact(Timeout = 60000)]
        public async Task SwitchOnWithNoSecret_NothingIsMintedAndTheJoinStillGoes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var svc = NewService(joinCapability: true, secret: null);

            var result = await Provision(svc, UUID.Random());

            Assert.True(result.Reply.ContainsKey("jsep"));
            Assert.False(BodyOf("join").ContainsKey("join_cap"));
            Assert.False(BodyOf("create").ContainsKey("sim_created"));
            Assert.Contains(_log.Snapshot(), l => l.Level == LogLevel.Error && l.Text.Contains("JoinCapabilitySecret is empty"));
        }

        [Fact(Timeout = 60000)]
        public async Task SwitchOnWithAShortSecret_NothingIsMintedAndOneErrorIsLogged()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            string shortSecret = new string('k', 31);
            var svc = NewService(joinCapability: true, secret: shortSecret);

            var result = await Provision(svc, UUID.Random());

            Assert.True(result.Reply.ContainsKey("jsep"));
            Assert.False(BodyOf("join").ContainsKey("join_cap"));
            Assert.Equal(1, _log.Snapshot().Count(l => l.Level == LogLevel.Error && l.Text.Contains("shorter than 32 characters")));
            AssertNoCapabilityInTheLog(shortSecret);
        }

        [Fact(Timeout = 60000)]
        public async Task SwitchOn_TheJoinCarriesACapabilityBoundToThisJoin_AndTheCreateIsMarked()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var svc = NewService(joinCapability: true, secret: Secret);

            var result = await Provision(svc, UUID.Random());

            Assert.True(result.Reply.ContainsKey("jsep"));
            Assert.True(BodyOf("create")["sim_created"].AsBoolean());
            OSDMap join = BodyOf("join");
            string capability = join["join_cap"].AsString();
            Assert.Equal(result.ViewerSession, join["session_id"].AsString());
            string[] payload = Verify(capability, Secret);
            {
                var assertedValue = payload[0];
                TestAssert.WithMessage(() => Assert.Equal(result.Agent.ToString(), assertedValue), "bound to the agent");
            }
            {
                var assertedValue = payload[1];
                TestAssert.WithMessage(() => Assert.Equal(result.ViewerSession, assertedValue), "bound to the viewer session");
            }
            {
                var assertedValue = payload[2];
                TestAssert.WithMessage(() => Assert.Equal(join["room"].AsInteger().ToString(), assertedValue), "bound to the room");
            }
            {
                var assertedValue = payload[3];
                TestAssert.WithMessage(() => Assert.Equal(JoinCapability.NoEpoch, assertedValue), "no authority has armed the room");
            }
            Assert.Equal("0", payload[4]);
            AssertNoCapabilityInTheLog(capability, capability.Split('.')[1], Secret.Substring(8));
        }

        [Fact(Timeout = 60000)]
        public async Task SwitchOn_ARoomTheRegionArmedCarriesItsEpochAndGeneration()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var svc = NewService(joinCapability: true, secret: Secret);
            UUID region = UUID.Random();
            int room = JanusAudioBridge.CalcRoomNumber(string.Empty, region.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            var authority = new VisAuthority(0x18f00000001UL);
            authority.NextGeneration(room);
            authority.NextGeneration(room);

            await Provision(svc, region);

            string[] payload = Verify(BodyOf("join")["join_cap"].AsString(), Secret);
            Assert.Equal(authority.EpochString, payload[3]);
            Assert.Equal("2", payload[4]);
        }

        [Fact(Timeout = 60000)]
        public async Task SwitchOn_AMixerRefusal496NamesTheReasonAndNeverTheCapability()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            _gw.PluginAnswer = body =>
            {
                _bodies.Enqueue(body);
                return body["request"].AsString() == "join"
                    ? "{\"audiobridge\":\"event\",\"error_code\":496,\"error\":\"join capability refused\",\"reason\":\"cap_expired\"}"
                    : null;
            };
            var svc = NewService(joinCapability: true, secret: Secret);

            var result = await Provision(svc, UUID.Random());

            Assert.False(result.Reply.ContainsKey("jsep"), "the join was refused");
            string capability = BodyOf("join")["join_cap"].AsString();
            Assert.True(_log.Snapshot().Any(l => l.Level == LogLevel.Error && l.Text.Contains("cap_expired") && l.Text.Contains("496")), string.Join(" | ", _log.Snapshot().Select(l => l.Text)));
            AssertNoCapabilityInTheLog(capability, capability.Split('.')[1]);
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
