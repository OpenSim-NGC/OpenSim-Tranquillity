/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * IWebRtcVoiceService.EnsureSpatialRoom makes the spatial room for a parcel exist before any avatar has joined it.
 *
 * Covered, against FakeJanusGateway with ModelRooms on (it answers create and join as the mixer does):
 *   - the room number is the one the provision path computes for the same region and parcel, and one create is sent;
 *   - a second call for a live room returns the same number and creates nothing new;
 *   - the create carries vis_authority when the sim arms and omits it when it does not;
 *   - a service session the server no longer knows is replaced and the room is still returned;
 *   - a gateway that cannot be reached, or one that throws something unexpected, gives null and one warning, not an exception;
 *   - the service module with no spatial service, and the grid-service client, answer null.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
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
    public class EnsureSpatialRoomTests
    {
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
            public List<(LogLevel Level, string Text)> AtOrAbove(LogLevel level)
            {
                lock (Lines)
                    return Lines.Where(l => l.Level >= level).ToList();
            }
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            private readonly Func<Exception> _make;
            public ThrowingHandler(Func<Exception> make) { _make = make; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw _make();
        }

        private FakeJanusGateway _gw;
        private Capture _log;
        private ILoggerFactory _previous;

        [SetUp]
        public void SetUp()
        {
            _gw = new FakeJanusGateway { ModelRooms = true };
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

        private static IConfigSource JanusConfig(bool pArmed = false)
        {
            var config = new IniConfigSource();
            IConfig voice = config.AddConfig("WebRtcVoice");
            voice.Set("Enabled", "true");
            if (pArmed)
            {
                voice.Set("VisibilityFeederEnabled", "true");
                voice.Set("VisibilityEmitEnabled", "true");
                voice.Set("VisibilityArmingEnabled", "true");
            }
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", new string('a', 40));
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", new string('b', 40));
            janus.Set("PluginName", "janus.plugin.slvoice");
            janus.Set("RequestTimeoutMs", "2000");
            return config;
        }

        private WebRtcJanusService NewService(bool pArmed = false)
            => new WebRtcJanusService(JanusConfig(pArmed), () => new FakeJanusHandler(_gw));

        private static int RoomOf(UUID region, int parcel)
            => JanusAudioBridge.CalcRoomNumber(string.Empty, region.ToString(), "local", parcel, string.Empty);

        [Test]
        public void ALiveService_ReturnsTheRoomTheProvisionPathComputes_AndSendsOneCreate()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();

            int? room = svc.EnsureSpatialRoom(region, 17);

            Assert.That(room, Is.EqualTo(RoomOf(region, 17)));
            Assert.That(_gw.RoomCreates, Is.EqualTo(1), "one create request");
            Assert.That(_gw.Rooms.ContainsKey(room.Value), Is.True, "the mixer holds the room");
            Assert.That(_log.AtOrAbove(LogLevel.Warning), Is.Empty);
        }

        [Test]
        public void TheEstateRoom_IsTheOneAViewerProvisionJoins()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();

            int? room = svc.EnsureSpatialRoom(region, JanusAudioBridge.REGION_ROOM_ID);

            Assert.That(room, Is.EqualTo(RoomOf(region, JanusAudioBridge.REGION_ROOM_ID)));
        }

        [Test]
        public void ASecondCall_ForALiveRoom_ReturnsTheSameNumber_AndCreatesNothingNew()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();

            int? first = svc.EnsureSpatialRoom(region, 5);
            int? second = svc.EnsureSpatialRoom(region, 5);

            Assert.That(second, Is.EqualTo(first));
            Assert.That(_gw.RoomsCreated, Is.EqualTo(1), "the mixer created the room once; the second ask reused it");
            Assert.That(_log.AtOrAbove(LogLevel.Warning), Is.Empty, "an existing room is success, not a failure");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TheCreate_DeclaresVisAuthority_OnlyWhenTheSimArms(bool pArmed)
        {
            OSDMap createBody = null;
            _gw.PluginAnswer = body =>
            {
                if (body["request"].AsString() == "create")
                    createBody = body;
                return null;   // leave the answer to the room model
            };
            WebRtcJanusService svc = NewService(pArmed);

            svc.EnsureSpatialRoom(UUID.Random(), 3);

            Assert.That(createBody, Is.Not.Null, "a create was sent");
            Assert.That(createBody.ContainsKey("vis_authority"), Is.EqualTo(pArmed));
            if (pArmed)
                Assert.That(createBody["vis_authority"].AsBoolean(), Is.True);
        }

        [Test]
        public void AServiceSessionTheServerNoLongerKnows_IsReplaced_AndTheRoomIsStillReturned()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();
            Assert.That(svc.EnsureSpatialRoom(region, 1), Is.Not.Null, "precondition: the service session exists");
            foreach (string sid in _gw.CreatedSessions)
                _gw.DeadSessions[sid] = true;

            int? room = svc.EnsureSpatialRoom(region, 2);

            Assert.That(room, Is.EqualTo(RoomOf(region, 2)));
            Assert.That(_gw.CreateCount, Is.GreaterThan(1), "a new Janus session was made for the retry");
        }

        [Test]
        public void AGatewayThatCannotBeReached_GivesNull_AndNoException()
        {
            var svc = new WebRtcJanusService(JanusConfig(), () => new ThrowingHandler(() => new HttpRequestException("no route")));

            int? room = null;
            Assert.DoesNotThrow(() => room = svc.EnsureSpatialRoom(UUID.Random(), 1));

            Assert.That(room, Is.Null);
        }

        [Test]
        public void AnUnexpectedException_GivesNull_AndOneWarning_NotAThrow()
        {
            var svc = new WebRtcJanusService(JanusConfig(), () => new ThrowingHandler(() => new InvalidOperationException("boom")));

            int? room = null;
            Assert.DoesNotThrow(() => room = svc.EnsureSpatialRoom(UUID.Random(), 1));

            Assert.That(room, Is.Null);
            List<(LogLevel Level, string Text)> warned = _log.AtOrAbove(LogLevel.Warning).Where(l => l.Level == LogLevel.Warning).ToList();
            Assert.That(warned, Has.Count.EqualTo(1), string.Join(" | ", warned.Select(w => w.Text)));
        }

        [Test]
        public void TheServiceModule_WithNoSpatialService_AnswersNull()
        {
            var module = new WebRtcVoiceServiceModule();

            Assert.That(module.EnsureSpatialRoom(UUID.Random(), 1), Is.Null);
        }

        [Test]
        public void TheGridServiceClient_OwnsNoRooms_AndAnswersNull()
        {
            var connector = new WebRtcVoiceServiceConnector(JanusConfig());

            Assert.That(connector.EnsureSpatialRoom(UUID.Random(), 1), Is.Null);
        }
    }
}
