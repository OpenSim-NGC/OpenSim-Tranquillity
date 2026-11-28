/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Rooms the service created are removed once they have stood empty for the grace period, on the stock
 * AudioBridge plugin, which never removes an emptied room itself.
 *
 * The R tests are the sweep rules, run against a clock the test controls. The C tests are the settings. The S tests run the real
 * service against the fake gateway with its room lifecycle model switched on: a room made by a viewer's join is
 * removed after the last leave; a room the service did not create, an occupied room, and a plugin that removes
 * empty rooms itself are left alone.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice.Tests
{
    public class EmptyRoomReaperTests : IAsyncLifetime
        {
        // ---- the sweep rules ---------------------------------------------------------------------------

        private sealed class Rig
        {
            public long Now = 1_000;
            public readonly Dictionary<int, int> Counts = new();
            public bool ListFails;
            public bool DestroyFails;
            public readonly List<int> Destroyed = new();
            public EmptyRoomReaper Reaper;

            public Rig(int graceSeconds = 60)
            {
                Reaper = new EmptyRoomReaper(TimeSpan.FromSeconds(graceSeconds), () => Now);
            }

            public Task<int> Sweep() => Reaper.SweepAsync(
                () => Task.FromResult<IReadOnlyDictionary<int, int>>(ListFails ? null : new Dictionary<int, int>(Counts)),
                room =>
                {
                    if (DestroyFails)
                        return Task.FromResult(false);
                    Destroyed.Add(room);
                    Counts.Remove(room);
                    return Task.FromResult(true);
                });
        }

        [Fact(Timeout = 60000)]
        public async Task R1_ARoomEmptyForTheGracePeriod_IsRemoved_NotBefore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new Rig(60);
            rig.Counts[7] = 0;
            rig.Reaper.NoteCreated(7);

            await rig.Sweep();                       // first sight of the empty room: the grace starts here
            rig.Now += 59_000;
            await rig.Sweep();
            {
                var assertedValue = rig.Destroyed;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "59 s after it was first seen empty");
            }

            rig.Now += 1_000;
            int removed = await rig.Sweep();

            Assert.Equal(new[] { 7 }, rig.Destroyed);
            Assert.Equal(1, removed);
            Assert.False(rig.Reaper.IsTracked(7), "and it is forgotten");
        }

        [Fact(Timeout = 60000)]
        public async Task R2_ARoomWithSomeoneInIt_IsNeverRemoved_AndItsGraceRestartsWhenItEmpties()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new Rig(60);
            rig.Counts[7] = 0;
            rig.Reaper.NoteCreated(7);
            await rig.Sweep();
            rig.Now += 50_000;
            rig.Counts[7] = 1;                       // someone joins
            await rig.Sweep();
            rig.Now += 600_000;                      // and stays a long time
            await rig.Sweep();
            {
                var assertedValue = rig.Destroyed;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "occupied");
            }

            rig.Counts[7] = 0;                       // they leave
            await rig.Sweep();
            rig.Now += 59_000;
            await rig.Sweep();
            {
                var assertedValue = rig.Destroyed;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the grace counts from when it emptied, not from the old sighting");
            }
            rig.Now += 1_000;
            await rig.Sweep();
            Assert.Equal(new[] { 7 }, rig.Destroyed);
        }

        [Fact(Timeout = 60000)]
        public async Task R3_ARoomTheServiceDidNotCreate_IsNeverRemoved()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new Rig(0);
            rig.Counts[1234] = 0;                    // an operator's room: empty on the gateway, never reported as created
            rig.Counts[8] = 0;
            rig.Reaper.NoteCreated(8);

            await rig.Sweep();
            rig.Now += 10_000;
            await rig.Sweep();

            Assert.Equal(new[] { 8 }, rig.Destroyed);
            Assert.True(rig.Counts.ContainsKey(1234), "the room nobody reported creating is still there");
        }

        [Fact(Timeout = 60000)]
        public async Task R4_AListThatCannotBeRead_ChangesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new Rig(0);
            rig.Counts[7] = 0;
            rig.Reaper.NoteCreated(7);
            await rig.Sweep();                       // the room is seen empty
            rig.ListFails = true;
            rig.Now += 10_000;

            int removed = await rig.Sweep();

            Assert.Equal(0, removed);
            Assert.Empty(rig.Destroyed);
            Assert.True(rig.Reaper.IsTracked(7), "still watched");
        }

        [Fact(Timeout = 60000)]
        public async Task R5_AFailedDestroy_IsTriedAgainOnTheNextSweep()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new Rig(0);
            rig.Counts[7] = 0;
            rig.Reaper.NoteCreated(7);
            await rig.Sweep();
            rig.Now += 1_000;
            rig.DestroyFails = true;
            Assert.Equal(0, await rig.Sweep());
            Assert.True(rig.Reaper.IsTracked(7));

            rig.DestroyFails = false;
            Assert.Equal(1, await rig.Sweep());
            Assert.Equal(new[] { 7 }, rig.Destroyed);
        }

        [Fact(Timeout = 60000)]
        public async Task R6_ARoomTheGatewayNoLongerHas_IsForgotten()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var rig = new Rig(60);
            rig.Reaper.NoteCreated(7);               // the gateway restarted: it lists nothing

            await rig.Sweep();

            Assert.False(rig.Reaper.IsTracked(7));
            Assert.Empty(rig.Destroyed);
        }

        // ---- the settings ------------------------------------------------------------------------------

        private static IConfig Janus(params (string Key, string Value)[] pairs)
        {
            var src = new IniConfigSource();
            IConfig cfg = src.AddConfig("JanusWebRtcVoice");
            foreach ((string k, string v) in pairs)
                cfg.Set(k, v);
            return cfg;
        }

        [Fact(Timeout = 60000)]
        public void C1_TheDefault_OnTheStockPlugin_IsOn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(EmptyRoomReaper.DefaultEnabled);
            Assert.Equal(60, EmptyRoomReaper.DefaultGraceSeconds);
            Assert.NotNull(WebRtcJanusService.CreateRoomReaper(Janus(), WebRtcJanusService.DefaultPluginName, NullLogger.Instance));
        }

        [Fact(Timeout = 60000)]
        public void C2_TheKeyTurnsItOff()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Null(WebRtcJanusService.CreateRoomReaper(Janus(("RemoveEmptyRooms", "false")),
                WebRtcJanusService.DefaultPluginName, NullLogger.Instance));
        }

        [Fact(Timeout = 60000)]
        public void C3_TheSlvoiceMixerRemovesItsOwnRooms_SoNoReaperIsMade()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Null(WebRtcJanusService.CreateRoomReaper(Janus(), "janus.plugin.slvoice", NullLogger.Instance));
            {
                var assertedValue = WebRtcJanusService.CreateRoomReaper(Janus(("RemoveEmptyRooms", "true")), "janus.plugin.slvoice", NullLogger.Instance);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "also with the key on");
            }
        }

        [Fact(Timeout = 60000)]
        public async Task C4_TheGraceSecondsKeyIsRead()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            long now = 0;
            EmptyRoomReaper reaper = WebRtcJanusService.CreateRoomReaper(Janus(("EmptyRoomGraceSeconds", "5")),
                WebRtcJanusService.DefaultPluginName, NullLogger.Instance, () => now);
            var destroyed = new List<int>();
            Func<Task<IReadOnlyDictionary<int, int>>> list = () => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int> { [3] = 0 });
            Func<int, Task<bool>> destroy = r => { destroyed.Add(r); return Task.FromResult(true); };
            reaper.NoteCreated(3);
            await reaper.SweepAsync(list, destroy);
            now = 4_999;
            await reaper.SweepAsync(list, destroy);
            Assert.Empty(destroyed);
            now = 5_000;
            await reaper.SweepAsync(list, destroy);
            Assert.Equal(new[] { 3 }, destroyed);
        }

        [Fact(Timeout = 60000)]
        public async Task C5_ANegativeGraceFallsBackToTheDefault()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            long now = 0;
            EmptyRoomReaper reaper = WebRtcJanusService.CreateRoomReaper(Janus(("EmptyRoomGraceSeconds", "-5")),
                WebRtcJanusService.DefaultPluginName, NullLogger.Instance, () => now);
            var destroyed = new List<int>();
            Func<Task<IReadOnlyDictionary<int, int>>> list = () => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int> { [3] = 0 });
            Func<int, Task<bool>> destroy = r => { destroyed.Add(r); return Task.FromResult(true); };
            reaper.NoteCreated(3);
            await reaper.SweepAsync(list, destroy);
            now = 59_999;
            await reaper.SweepAsync(list, destroy);
            Assert.Empty(destroyed);
            now = 60_000;
            await reaper.SweepAsync(list, destroy);
            Assert.Equal(new[] { 3 }, destroyed);
        }

        // ---- the service against the fake gateway --------------------------------------------------------

        private FakeJanusGateway _gw;
        private void SetUp()
        {
            _gw = new FakeJanusGateway { ModelRooms = true, ModelRoomLifecycle = true };
        }

        private WebRtcJanusService NewService(string pluginName, params (string Key, string Value)[] extra)
        {
            var config = new IniConfigSource();
            config.AddConfig("WebRtcVoice").Set("Enabled", "true");
            IConfig janus = config.AddConfig("JanusWebRtcVoice");
            janus.Set("JanusGatewayURI", "http://janus.test/voice");
            janus.Set("APIToken", "tok");
            janus.Set("JanusGatewayAdminURI", "http://janus.test/admin");
            janus.Set("AdminAPIToken", "tok");
            janus.Set("PluginName", pluginName);
            janus.Set("RequestTimeoutMs", "2000");
            janus.Set("EmptyRoomGraceSeconds", "0");   // the test drives the sweeps, so the grace is the sweep before
            foreach ((string k, string v) in extra)
                janus.Set(k, v);
            return new WebRtcJanusService(config, () => new FakeJanusHandler(_gw));
        }

        private static int EstateRoomOf(UUID region)
            => JanusAudioBridge.CalcRoomNumber(string.Empty, region.ToString(), "local", JanusAudioBridge.REGION_ROOM_ID, string.Empty);

        private sealed class Caller
        {
            public JanusViewerSession Session;
            public UUID Agent;
            public UUID Region;
        }

        private static async Task<Caller> Join(WebRtcJanusService svc, UUID region)
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
            Assert.True(reply.ContainsKey("jsep"), "precondition: joined");
            return new Caller { Session = vs, Agent = agent, Region = region };
        }

        private static async Task Leave(WebRtcJanusService svc, Caller c)
        {
            await Task.Run(() => svc.ProvisionVoiceAccountRequest(c.Session, new OSDMap { ["logout"] = true }, c.Agent, c.Region));
        }

        // The timer may be sweeping at the same moment (one pass at a time), so keep asking until the answer holds.
        private static async Task SweepUntil(WebRtcJanusService svc, Func<bool> done)
        {
            DateTime stop = DateTime.UtcNow.AddSeconds(15);
            while (!done() && DateTime.UtcNow < stop)
            {
                await svc.ReapEmptyRoomsAsync();
                if (!done())
                    await Task.Delay(50);
            }
        }

        private static async Task SweepAFewTimes(WebRtcJanusService svc)
        {
            for (int i = 0; i < 4; i++)
            {
                await svc.ReapEmptyRoomsAsync();
                await Task.Delay(30);
            }
        }

        [Fact(Timeout = 60000)]
        public async Task S1_AfterTheLastLeave_TheRoomTheServiceMadeIsRemoved()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService("janus.plugin.audiobridge");
            UUID region = UUID.Random();
            int room = EstateRoomOf(region);
            Caller a = await Join(svc, region);
            Caller b = await Join(svc, region);
            Assert.True(_gw.Rooms.ContainsKey(room));
            Assert.True(svc.RoomReaper.IsTracked(room), "the create was answered 'created'");

            await Leave(svc, a);
            await SweepAFewTimes(svc);
            Assert.True(_gw.Rooms.ContainsKey(room), "one person still in it");

            await Leave(svc, b);
            await SweepUntil(svc, () => !_gw.Rooms.ContainsKey(room));

            Assert.False(_gw.Rooms.ContainsKey(room), "removed once empty");
            Assert.Contains(room, _gw.Destroyed);
        }

        [Fact(Timeout = 60000)]
        public async Task S2_ARoomTheServiceDidNotCreate_IsLeftInPlace_EvenWhenEmpty()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService("janus.plugin.audiobridge");
            UUID region = UUID.Random();
            int room = EstateRoomOf(region);
            _gw.Rooms[room] = true;                  // an operator's room: the create is answered 486
            Caller a = await Join(svc, region);
            Assert.False(svc.RoomReaper.IsTracked(room));

            await Leave(svc, a);
            await SweepAFewTimes(svc);

            Assert.True(_gw.Rooms.ContainsKey(room));
            Assert.Empty(_gw.Destroyed);
        }

        [Fact(Timeout = 60000)]
        public async Task S3_AnOccupiedRoom_IsNotRemoved()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService("janus.plugin.audiobridge");
            UUID region = UUID.Random();
            int room = EstateRoomOf(region);
            await Join(svc, region);

            await SweepAFewTimes(svc);

            Assert.True(_gw.Rooms.ContainsKey(room));
            Assert.Empty(_gw.Destroyed);
        }

        [Fact(Timeout = 60000)]
        public async Task S4_OnTheSlvoiceMixer_NothingIsRemoved_TheMixerDoesItsOwn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService("janus.plugin.slvoice");
            UUID region = UUID.Random();
            int room = EstateRoomOf(region);
            Caller a = await Join(svc, region);
            await Leave(svc, a);

            await SweepAFewTimes(svc);

            Assert.Null(svc.RoomReaper);
            Assert.True(_gw.Rooms.ContainsKey(room), "left for the mixer's grace sweep");
            Assert.Empty(_gw.Destroyed);
        }

        [Fact(Timeout = 60000)]
        public async Task S5_WithTheKeyOff_NothingIsRemoved()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            WebRtcJanusService svc = NewService("janus.plugin.audiobridge", ("RemoveEmptyRooms", "false"));
            UUID region = UUID.Random();
            int room = EstateRoomOf(region);
            Caller a = await Join(svc, region);
            await Leave(svc, a);

            await SweepAFewTimes(svc);

            Assert.Null(svc.RoomReaper);
            Assert.True(_gw.Rooms.ContainsKey(room));
            Assert.Empty(_gw.Destroyed);
        }

        public ValueTask InitializeAsync()
        {
            SetUp();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
