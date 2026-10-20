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
    [TestFixture]
    [NonParallelizable]
    public class JoinCapabilityProvisionTests
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
        private ILoggerFactory _previous;
        private ConcurrentQueue<OSDMap> _bodies;

        [SetUp]
        public void SetUp()
        {
            _bodies = new ConcurrentQueue<OSDMap>();   // NUnit shares one fixture instance across the tests
            _gw = new FakeJanusGateway { ModelRooms = true };
            _gw.PluginAnswer = body => { _bodies.Enqueue(body); return null; };
            JoinCapabilityAuthority.Clear();
            _log = new Capture();
            _previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(_log).SetMinimumLevel(LogLevel.Trace));
        }

        [TearDown]
        public void TearDown()
        {
            JoinCapabilityAuthority.Clear();
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
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
            Assert.That(parts, Has.Length.EqualTo(3));
            Assert.That(parts[0], Is.EqualTo("v1"));
            using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            string expected = Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.That(parts[2], Is.EqualTo(expected), "the signature is the HMAC of the secret over the payload");
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
        }

        private void AssertNoCapabilityInTheLog(params string[] values)
        {
            foreach (var line in _log.Snapshot())
                foreach (string v in values.Where(v => !string.IsNullOrEmpty(v)))
                    Assert.That(line.Text, Does.Not.Contain(v), "a capability or secret was logged: " + line.Level);
        }

        [Test]
        public async Task SwitchOff_TheCreateAndJoinBodiesCarryNoCapability()
        {
            var svc = NewService(joinCapability: false, secret: Secret);

            var result = await Provision(svc, UUID.Random());

            Assert.That(result.Reply.ContainsKey("jsep"), Is.True, "the join succeeded");
            OSDMap join = BodyOf("join");
            Assert.That(join.ContainsKey("join_cap"), Is.False);
            Assert.That(join.ContainsKey("session_id"), Is.False);
            Assert.That(BodyOf("create").ContainsKey("sim_created"), Is.False);
        }

        [Test]
        public async Task SwitchOnWithNoSecret_NothingIsMintedAndTheJoinStillGoes()
        {
            var svc = NewService(joinCapability: true, secret: null);

            var result = await Provision(svc, UUID.Random());

            Assert.That(result.Reply.ContainsKey("jsep"), Is.True);
            Assert.That(BodyOf("join").ContainsKey("join_cap"), Is.False);
            Assert.That(BodyOf("create").ContainsKey("sim_created"), Is.False);
            Assert.That(_log.Snapshot().Any(l => l.Level == LogLevel.Error && l.Text.Contains("JoinCapabilitySecret is empty")), Is.True);
        }

        [Test]
        public async Task SwitchOnWithAShortSecret_NothingIsMintedAndOneErrorIsLogged()
        {
            string shortSecret = new string('k', 31);
            var svc = NewService(joinCapability: true, secret: shortSecret);

            var result = await Provision(svc, UUID.Random());

            Assert.That(result.Reply.ContainsKey("jsep"), Is.True);
            Assert.That(BodyOf("join").ContainsKey("join_cap"), Is.False);
            Assert.That(_log.Snapshot().Count(l => l.Level == LogLevel.Error && l.Text.Contains("shorter than 32 characters")), Is.EqualTo(1));
            AssertNoCapabilityInTheLog(shortSecret);
        }

        [Test]
        public async Task SwitchOn_TheJoinCarriesACapabilityBoundToThisJoin_AndTheCreateIsMarked()
        {
            var svc = NewService(joinCapability: true, secret: Secret);

            var result = await Provision(svc, UUID.Random());

            Assert.That(result.Reply.ContainsKey("jsep"), Is.True);
            Assert.That(BodyOf("create")["sim_created"].AsBoolean(), Is.True);
            OSDMap join = BodyOf("join");
            string capability = join["join_cap"].AsString();
            Assert.That(join["session_id"].AsString(), Is.EqualTo(result.ViewerSession));
            string[] payload = Verify(capability, Secret);
            Assert.That(payload[0], Is.EqualTo(result.Agent.ToString()), "bound to the agent");
            Assert.That(payload[1], Is.EqualTo(result.ViewerSession), "bound to the viewer session");
            Assert.That(payload[2], Is.EqualTo(join["room"].AsInteger().ToString()), "bound to the room");
            Assert.That(payload[3], Is.EqualTo(JoinCapability.NoEpoch), "no authority has armed the room");
            Assert.That(payload[4], Is.EqualTo("0"));
            AssertNoCapabilityInTheLog(capability, capability.Split('.')[1], Secret.Substring(8));
        }

        [Test]
        public async Task SwitchOn_ARoomTheRegionArmedCarriesItsEpochAndGeneration()
        {
            var svc = NewService(joinCapability: true, secret: Secret);
            UUID region = UUID.Random();
            int room = JanusAudioBridge.CalcRoomNumber(string.Empty, region.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            var authority = new VisAuthority(0x18f00000001UL);
            authority.NextGeneration(room);
            authority.NextGeneration(room);

            await Provision(svc, region);

            string[] payload = Verify(BodyOf("join")["join_cap"].AsString(), Secret);
            Assert.That(payload[3], Is.EqualTo(authority.EpochString));
            Assert.That(payload[4], Is.EqualTo("2"));
        }

        [Test]
        public async Task SwitchOn_AMixerRefusal496NamesTheReasonAndNeverTheCapability()
        {
            _gw.PluginAnswer = body =>
            {
                _bodies.Enqueue(body);
                return body["request"].AsString() == "join"
                    ? "{\"audiobridge\":\"event\",\"error_code\":496,\"error\":\"join capability refused\",\"reason\":\"cap_expired\"}"
                    : null;
            };
            var svc = NewService(joinCapability: true, secret: Secret);

            var result = await Provision(svc, UUID.Random());

            Assert.That(result.Reply.ContainsKey("jsep"), Is.False, "the join was refused");
            string capability = BodyOf("join")["join_cap"].AsString();
            Assert.That(_log.Snapshot().Any(l => l.Level == LogLevel.Error && l.Text.Contains("cap_expired") && l.Text.Contains("496")), Is.True,
                string.Join(" | ", _log.Snapshot().Select(l => l.Text)));
            AssertNoCapabilityInTheLog(capability, capability.Split('.')[1]);
        }
    }
}
