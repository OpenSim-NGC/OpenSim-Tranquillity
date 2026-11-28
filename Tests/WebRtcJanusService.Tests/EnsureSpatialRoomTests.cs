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
    public class EnsureSpatialRoomTests : IAsyncLifetime
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
        private LogCapture _logging;
        private void SetUp()
        {
            _gw = new FakeJanusGateway { ModelRooms = true };
            _log = new Capture();
            _logging = new LogCapture(_log, LogLevel.Trace);
        }
        private void TearDown()
        {
            _logging.Dispose();
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

        [Fact(Timeout = 60000)]
        public void ALiveService_ReturnsTheRoomTheProvisionPathComputes_AndSendsOneCreate()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();

            int? room = svc.EnsureSpatialRoom(region, 17);

            Assert.Equal(RoomOf(region, 17), room);
            {
                var assertedValue = _gw.RoomCreates;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one create request");
            }
            Assert.True(_gw.Rooms.ContainsKey(room.Value), "the mixer holds the room");
            Assert.Empty(_log.AtOrAbove(LogLevel.Warning));
        }

        [Fact(Timeout = 60000)]
        public void TheEstateRoom_IsTheOneAViewerProvisionJoins()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();

            int? room = svc.EnsureSpatialRoom(region, JanusAudioBridge.REGION_ROOM_ID);

            Assert.Equal(RoomOf(region, JanusAudioBridge.REGION_ROOM_ID), room);
        }

        [Fact(Timeout = 60000)]
        public void ASecondCall_ForALiveRoom_ReturnsTheSameNumber_AndCreatesNothingNew()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();

            int? first = svc.EnsureSpatialRoom(region, 5);
            int? second = svc.EnsureSpatialRoom(region, 5);

            Assert.Equal(first, second);
            {
                var assertedValue = _gw.RoomsCreated;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the mixer created the room once; the second ask reused it");
            }
            {
                var assertedValue = _log.AtOrAbove(LogLevel.Warning);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "an existing room is success, not a failure");
            }
        }

        [InlineData(true)]
        [InlineData(false)]
        [Theory(Timeout = 60000)]
        public void TheCreate_DeclaresVisAuthority_OnlyWhenTheSimArms(bool pArmed)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap createBody = null;
            _gw.PluginAnswer = body =>
            {
                if (body["request"].AsString() == "create")
                    createBody = body;
                return null;   // leave the answer to the room model
            };
            WebRtcJanusService svc = NewService(pArmed);

            svc.EnsureSpatialRoom(UUID.Random(), 3);

            {
                var assertedValue = createBody;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "a create was sent");
            }
            Assert.Equal(pArmed, createBody.ContainsKey("vis_authority"));
            if (pArmed)
                Assert.True(createBody["vis_authority"].AsBoolean());
        }

        [Fact(Timeout = 60000)]
        public void AServiceSessionTheServerNoLongerKnows_IsReplaced_AndTheRoomIsStillReturned()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();
            {
                var assertedValue = svc.EnsureSpatialRoom(region, 1);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "precondition: the service session exists");
            }
            foreach (string sid in _gw.CreatedSessions)
                _gw.DeadSessions[sid] = true;

            int? room = svc.EnsureSpatialRoom(region, 2);

            Assert.Equal(RoomOf(region, 2), room);
            Assert.True(_gw.CreateCount > 1, "a new Janus session was made for the retry");
        }

        [Fact(Timeout = 60000)]
        public void AGatewayThatCannotBeReached_GivesNull_AndNoException()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var svc = new WebRtcJanusService(JanusConfig(), () => new ThrowingHandler(() => new HttpRequestException("no route")));

            int? room = null;
            {
                var assertedValue = () => room = svc.EnsureSpatialRoom(UUID.Random(), 1);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }

            Assert.Null(room);
        }

        [Fact(Timeout = 60000)]
        public void AnUnexpectedException_GivesNull_AndOneWarning_NotAThrow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var svc = new WebRtcJanusService(JanusConfig(), () => new ThrowingHandler(() => new InvalidOperationException("boom")));

            int? room = null;
            {
                var assertedValue = () => room = svc.EnsureSpatialRoom(UUID.Random(), 1);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }

            Assert.Null(room);
            List<(LogLevel Level, string Text)> warned = _log.AtOrAbove(LogLevel.Warning).Where(l => l.Level == LogLevel.Warning).ToList();
            {
                var assertedValue = warned;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), string.Join(" | ", warned.Select(w => w.Text)));
            }
        }

        [Fact(Timeout = 60000)]
        public void TheServiceModule_WithNoSpatialService_AnswersNull()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var module = new WebRtcVoiceServiceModule();

            Assert.Null(module.EnsureSpatialRoom(UUID.Random(), 1));
        }

        [Fact(Timeout = 60000)]
        public void TheGridServiceClient_OwnsNoRooms_AndAnswersNull()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var connector = new WebRtcVoiceServiceConnector(JanusConfig());

            Assert.Null(connector.EnsureSpatialRoom(UUID.Random(), 1));
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
