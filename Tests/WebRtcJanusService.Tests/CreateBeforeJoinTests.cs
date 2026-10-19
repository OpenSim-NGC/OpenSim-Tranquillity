/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The provision path creates the room before it joins.
 *
 * The service used to remember which rooms it had created and skip the create for those. The mixer
 * removes an emptied room after a grace period without telling the sim, so the memory went stale. The
 * first join, carrying the viewer's JSEP offer, was then refused with 485 after Janus core had already
 * built the handle's ICE agent from that offer, and the same JSEP-bearing join re-sent on the SAME handle
 * failed in Janus core with 490 "Error setting ICE locally". The viewer's own retry succeeded several
 * seconds later, and three ERROR lines were logged.
 *
 * Now, before every JSEP join, SelectRoom asks the mixer to create the room, "already exists" (486)
 * counting as success; no remembered state decides anything. The same-handle retry is removed: a 485 that
 * still happens fails the provision back to the viewer with ONE WARN and no ERROR. The 486 "Reusing!"
 * line is DEBUG, as it is now the normal case. Two provisions into one missing room create one room and
 * both join.
 *
 * Everything here runs the REAL provision path - WebRtcJanusService.ProvisionVoiceAccountRequest -> SelectRoom ->
 * CreateRoom -> JanusRoom.JoinRoom - over HTTP against FakeJanusGateway with ModelRooms on, which answers create and
 * join as the live mixer does, including Janus core's per-handle ICE rule. Log lines are the real ones, captured by
 * swapping LoggerProvider's factory (DeferredLogger rebinds on the swap). Each test uses its own region, so its own
 * room number: the create gates are process-wide.
 */

using System;
using System.Collections.Generic;
using System.Linq;
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
    public class CreateBeforeJoinTests
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
            public List<(LogLevel Level, string Text)> At(LogLevel level)
            {
                lock (Lines)
                    return Lines.Where(l => l.Level == level).ToList();
            }
        }

        private FakeJanusGateway _gw;
        private Capture _log;
        private ILoggerFactory _previous;
        private readonly List<WebRtcJanusService> _services = new();

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
            _services.Clear();   // the fake gateway is in-memory; nothing outlives the test process
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = _previous;
            ours.Dispose();
            _log.Dispose();
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
            janus.Set("RequestTimeoutMs", "2000");
            return config;
        }

        private WebRtcJanusService NewService()
        {
            var svc = new WebRtcJanusService(JanusConfig(), () => new FakeJanusHandler(_gw));
            _services.Add(svc);
            return svc;
        }

        private static int EstateRoomOf(UUID region)
            => JanusAudioBridge.CalcRoomNumber(string.Empty, region.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);

        /// One viewer provision with a JSEP offer, exactly the shape the region module hands the service.
        private static Task<OSDMap> Provision(WebRtcJanusService svc, UUID region)
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
            return Task.Run(() => svc.ProvisionVoiceAccountRequest(vs, request, agent, region));
        }

        private static bool Joined(OSDMap reply) => reply is not null && reply.ContainsKey("jsep") && reply.ContainsKey("viewer_session");

        /// A room this process already created once, which the mixer's empty-room grace has since destroyed - the
        /// state that used to leave a stale hint behind.
        private async Task<int> RoomCreatedThenGraceDestroyed(WebRtcJanusService svc, UUID region)
        {
            OSDMap first = await Provision(svc, region);
            Assert.That(Joined(first), Is.True, "precondition: an earlier provision created and joined the room");
            int room = EstateRoomOf(region);
            _gw.GraceDestroy(room);
            return room;
        }

        private string Where() => string.Join(" | ", _log.Lines.Where(l => l.Level >= LogLevel.Warning)
            .Select(l => l.Level + ": " + l.Text.Substring(0, Math.Min(140, l.Text.Length))));

        // ---- V1 --------------------------------------------------------------------------------------------------

        /// <summary>V1: the room this process once created has been destroyed by the mixer's grace. The provision must
        /// send ONE create and ONE join, and be joined. TODAY the stale hint skips the create, the JSEP join draws 485,
        /// re-creates and re-sends the same join on the same handle, and Janus core refuses it 490 - not joined, two
        /// joins, three ERROR lines, and the viewer's own retry has to do the rest.</summary>
        [Test]
        public async Task V1_HintSaysExists_MixerHasNoRoom_OneCreateOneJoin_Joined()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();
            await RoomCreatedThenGraceDestroyed(svc, region);
            int creates = _gw.RoomCreates, joins = _gw.RoomJoins;
            lock (_log.Lines) _log.Lines.Clear();

            OSDMap reply = await Provision(svc, region);

            TestContext.Out.WriteLine($"joined={Joined(reply)} creates={_gw.RoomCreates - creates} joins={_gw.RoomJoins - joins} " +
                $"errors={_log.At(LogLevel.Error).Count} :: {Where()}");
            Assert.That(Joined(reply), Is.True, "joined in ONE provision");
            Assert.That(_gw.RoomCreates - creates, Is.EqualTo(1), "exactly one create");
            Assert.That(_gw.RoomJoins - joins, Is.EqualTo(1), "exactly one join");
            Assert.That(_log.At(LogLevel.Error), Is.Empty, "no ERROR");
        }

        // ---- V2 --------------------------------------------------------------------------------------------------

        /// <summary>V2: the room exists. The create answers 486 - success - then one join, joined, and nothing is logged
        /// at WARN or ERROR: the 486 is now the normal case on almost every provision, so its "Reusing!" line is DEBUG.</summary>
        [Test]
        public async Task V2_RoomExists_Create486_OneJoin_Joined_NoWarnOrError()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();
            int room = EstateRoomOf(region);
            _gw.Rooms[room] = true;   // someone - another sim, a connector's ensure - already has it
            lock (_log.Lines) _log.Lines.Clear();

            OSDMap reply = await Provision(svc, region);

            TestContext.Out.WriteLine($"joined={Joined(reply)} creates={_gw.RoomCreates} joins={_gw.RoomJoins} :: {Where()}");
            Assert.That(Joined(reply), Is.True);
            Assert.That(_gw.RoomCreates, Is.EqualTo(1), "the create was sent and answered 486");
            Assert.That(_gw.RoomsCreated, Is.Zero, "and created nothing, because the room was there");
            Assert.That(_gw.RoomJoins, Is.EqualTo(1));
            Assert.That(_log.At(LogLevel.Warning).Concat(_log.At(LogLevel.Error)), Is.Empty,
                "no WARN or ERROR on the normal path");
        }

        // ---- V3 --------------------------------------------------------------------------------------------------

        /// <summary>V3: a 485 on the join anyway - the room vanished between the create and the join. Exactly ONE join
        /// attempt (no same-handle retry: Janus core would refuse it 490), the provision fails back to the viewer as
        /// before, ONE WARN, no ERROR. There is no hint left to forget: the next provision creates unconditionally.</summary>
        [Test]
        public async Task V3_A485OnTheJoinAnyway_OneJoin_ProvisionFails_OneWarn_NoError()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();
            int room = EstateRoomOf(region);
            _gw.DestroyAfterCreate = room;   // the grace sweep lands between the create's answer and the join
            lock (_log.Lines) _log.Lines.Clear();

            OSDMap reply = await Provision(svc, region);

            TestContext.Out.WriteLine($"joined={Joined(reply)} joins={_gw.RoomJoins} warns={_log.At(LogLevel.Warning).Count} " +
                $"errors={_log.At(LogLevel.Error).Count} :: {Where()}");
            Assert.That(Joined(reply), Is.False, "the provision fails back to the viewer");
            Assert.That(reply?["response"].AsString(), Is.EqualTo("failed"));
            Assert.That(_gw.RoomJoins, Is.EqualTo(1), "exactly ONE join attempt - never the same JSEP join twice on a handle");
            Assert.That(_log.At(LogLevel.Warning).Count, Is.EqualTo(1), "one WARN");
            Assert.That(_log.At(LogLevel.Error), Is.Empty, "no ERROR");

            _gw.DestroyAfterCreate = 0;
            OSDMap retry = await Provision(svc, region);   // the viewer's own retry
            Assert.That(Joined(retry), Is.True, "and the next provision creates the room and joins");
        }

        // ---- V4 --------------------------------------------------------------------------------------------------

        /// <summary>V4: two provisions at once into one room that is missing (created by this process once, since
        /// destroyed by the grace - which used to leave a stale hint). One room is created, both join: the loser's create
        /// answers 486, which is success.</summary>
        [Test]
        public async Task V4_TwoConcurrentProvisionsIntoOneMissingRoom_OneCreated_BothJoined()
        {
            WebRtcJanusService svc = NewService();
            UUID region = UUID.Random();
            int room = await RoomCreatedThenGraceDestroyed(svc, region);
            int created = _gw.RoomsCreated, joins = _gw.RoomJoins;

            OSDMap[] replies = await Task.WhenAll(Provision(svc, region), Provision(svc, region));

            TestContext.Out.WriteLine($"joined={Joined(replies[0])}/{Joined(replies[1])} created={_gw.RoomsCreated - created} " +
                $"joins={_gw.RoomJoins - joins} :: {Where()}");
            Assert.That(Joined(replies[0]) && Joined(replies[1]), Is.True, "both joined");
            Assert.That(_gw.RoomsCreated - created, Is.EqualTo(1), "one room created");
            Assert.That(_gw.RoomJoins - joins, Is.EqualTo(2), "two joins");
            Assert.That(_gw.Rooms.ContainsKey(room), Is.True);
        }
    }
}
