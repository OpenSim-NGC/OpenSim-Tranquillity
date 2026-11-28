/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration tests: the region module and the voice service, unchanged, against a real Janus gateway
 * running the stock AudioBridge plugin (PluginName = janus.plugin.audiobridge), with real WebRTC peers (aiortc).
 *
 * They run only when WEBRTC_JANUS_TEST_URL names a gateway (see StockJanusSupport.cs for the variables) and are
 * skipped otherwise. The gateway is read through its own API, not through the service under test, so a room the
 * service believes it joined has to exist on the gateway too.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    // The assembly-wide 10 s backstop is too short for a session with a real gateway and a real peer.
    public class StockJanusSpatialTests(RawJanus janus) : IAsyncLifetime, IClassFixture<RawJanus>
        {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");

        private readonly RawJanus _janus = janus;
        private void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task EstateRoom_ProvisionJoinsARoomOnTheGateway()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));

            Assert.Equal((int)HttpStatusCode.OK, a.Answer.Status);
            Assert.True(a.Answer.Provisioned, "the answer carries viewer_session: " + a.Answer.Body);
            Assert.True(a.Answer.Room > 0);
            Assert.True((await _janus.RoomExistsAsync(a.Answer.Room)), "the room exists on the gateway");
            Assert.True((await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle)), "and holds the participant");
            await region.LogoutAsync(a);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task ParcelRoom_IsADifferentRoomFromTheEstateRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InWestParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);

            // The viewer names its parcel; the region resolves the real one from the avatar's position.
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o, region.WestParcelLocalId));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));

            Assert.True(a.Answer.Provisioned, "parcel provision: " + a.Answer.Body);
            Assert.True(b.Answer.Provisioned, "estate provision: " + b.Answer.Body);
            {
                var assertedValue = a.Answer.Room;
                TestAssert.WithMessage(() => Assert.NotEqual(b.Answer.Room, assertedValue), "parcel and estate channel are separate rooms");
            }
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle));
            Assert.True(await _janus.WaitForParticipantsAsync(b.Answer.Room, 1, Settle));
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task SecondProvisionIntoTheSameRoom_SharesIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));

            Assert.True(b.Answer.Provisioned, "second provision: " + b.Answer.Body);
            {
                var assertedValue = b.Answer.Room;
                TestAssert.WithMessage(() => Assert.Equal(a.Answer.Room, assertedValue), "same room");
            }
            {
                var assertedValue = b.Answer.ViewerSession;
                TestAssert.WithMessage(() => Assert.NotEqual(a.Answer.ViewerSession, assertedValue), "each agent has its own voice session");
            }
            Assert.True((await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle)), "two participants on the gateway");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task Leave_RemovesTheParticipantAndKeepsTheOther()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));
            int room = a.Answer.Room;
            Assert.True(await _janus.WaitForParticipantsAsync(room, 2, Settle));

            CapAnswer left = await region.LogoutAsync(a);

            Assert.Equal((int)HttpStatusCode.OK, left.Status);
            Assert.Equal("closed", left.Body["response"].AsString());
            Assert.True((await _janus.WaitForParticipantsAsync(room, 1, Settle)), "one participant left in the room");
            await region.LogoutAsync(b);
            Assert.True((await _janus.WaitForParticipantsAsync(room, 0, Settle)), "and none after the second leaves");
        }

        // The slvoice mixer clears an emptied room on its own after its grace period. The stock AudioBridge plugin never
        // does, so the service removes the rooms it created once they have stood empty for EmptyRoomGraceSeconds (set
        // short here so the test does not wait a minute and a half). Either way the room is gone afterwards.
        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task EmptiedRoom_AfterTheGracePeriod_IsGoneFromTheGateway()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create((voice, janus) => janus.Set("EmptyRoomGraceSeconds", "5"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            int room = a.Answer.Room;
            await region.LogoutAsync(a);
            Assert.True((await _janus.WaitForParticipantsAsync(room, 0, Settle)), "emptied");

            DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(StockJanusEnvironment.GraceSeconds, 60));
            while (await _janus.RoomExistsAsync(room) && DateTime.UtcNow < until)
                await Task.Delay(2000, TestContext.Current.CancellationToken);

            Assert.False((await _janus.RoomExistsAsync(room)), $"room {room} is still on the gateway after its last participant left and the grace passed");
        }

        // A room the service did not create is never removed by it, however long it stands empty.
        [NeedsPlugin(JanusPluginKind.Stock)]
        [Fact(Timeout = 240000)]
        public async Task EmptiedRoom_ThatTheServiceDidNotCreate_IsLeftInPlace()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StockJanusEnvironment.IgnoreUnlessAudioBridge();
            using var region = StockRegion.Create((voice, janus) => janus.Set("EmptyRoomGraceSeconds", "3"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            int room = JanusAudioBridge.CalcRoomNumber(string.Empty, region.Scene.RegionInfo.RegionID.ToString(), "local",
                JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            JsonObject made = await _janus.PluginRequestAsync(new JsonObject
            {
                ["request"] = "create", ["room"] = room, ["permanent"] = false, ["description"] = "operator room"
            });
            {
                var assertedValue = made["audiobridge"]?.GetValue<string>();
                TestAssert.WithMessage(() => Assert.Equal("created", assertedValue), "precondition: the operator made the room");
            }
            try
            {
                using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
                {
                    var assertedValue = a.Answer.Room;
                    TestAssert.WithMessage(() => Assert.Equal(room, assertedValue), "precondition: the service joined that room");
                }
                await region.LogoutAsync(a);
                Assert.True((await _janus.WaitForParticipantsAsync(room, 0, Settle)), "emptied");

                await Task.Delay(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);   // well past the grace and several sweeps

                Assert.True((await _janus.RoomExistsAsync(room)), "the operator's room is still there");
            }
            finally
            {
                await _janus.PluginRequestAsync(new JsonObject { ["request"] = "destroy", ["room"] = room, ["permanent"] = false });
            }
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task Audio_TwoPeersHearEachOther_AndNotThemselves()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            const double ToneA = 440, ToneB = 880, Other = 1320;

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o), ToneA);
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o), ToneB);
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle));
            await Task.Delay(1500, TestContext.Current.CancellationToken);   // let the first frames flow before measuring

            Task<List<Measurement>> heardByA = a.Peer.MeasureWindowsAsync(6, 0.5, ToneA, ToneB, Other);
            Task<List<Measurement>> heardByB = b.Peer.MeasureWindowsAsync(6, 0.5, ToneA, ToneB, Other);
            List<Measurement> ma = await heardByA, mb = await heardByB;
            TestContext.Current.TestOutputHelper.WriteLine("A (talks 440 Hz) heard: " + Measurement.Describe(ma, ToneA, ToneB, Other));
            TestContext.Current.TestOutputHelper.WriteLine("B (talks 880 Hz) heard: " + Measurement.Describe(mb, ToneA, ToneB, Other));

            Assert.True(Measurement.MedianRms(ma) > 0.01, "A receives audible audio");
            Assert.True(Measurement.MedianShare(ma, ToneB) > 0.8, "A hears B's tone");
            Assert.True(Measurement.MedianShare(ma, ToneA) < 0.02, "A does not hear itself");
            Assert.True(Measurement.MedianRms(mb) > 0.01, "B receives audible audio");
            Assert.True(Measurement.MedianShare(mb, ToneA) > 0.8, "B hears A's tone");
            Assert.True(Measurement.MedianShare(mb, ToneB) < 0.02, "B does not hear itself");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task Audio_AParcelRoomDoesNotCarryTheEstateRoomsVoice()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InWestParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            const double ToneB = 880;

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o, region.WestParcelLocalId));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o), ToneB);
            await Task.Delay(1500, TestContext.Current.CancellationToken);

            Measurement ma = await a.Peer.MeasureAsync(2, ToneB);
            TestContext.Current.TestOutputHelper.WriteLine("A (other room) heard: " + ma);

            Assert.True(ma.Share(ToneB) < 0.01, "a listener in the parcel room does not hear the estate room");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        public ValueTask InitializeAsync()
        {
            RequireGateway();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
