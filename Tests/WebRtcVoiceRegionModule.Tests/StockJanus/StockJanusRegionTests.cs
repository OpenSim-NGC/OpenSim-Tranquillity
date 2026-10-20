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
#pragma warning disable CS0618
    [TestFixture, NUnit.Framework.Timeout(240000)]
#pragma warning restore CS0618
    public class StockJanusSpatialTests
    {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");

        private readonly RawJanus _janus = new();

        [OneTimeSetUp]
        public void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task EstateRoom_ProvisionJoinsARoomOnTheGateway()
        {
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));

            Assert.That(a.Answer.Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(a.Answer.Provisioned, Is.True, "the answer carries viewer_session: " + a.Answer.Body);
            Assert.That(a.Answer.Room, Is.GreaterThan(0));
            Assert.That(await _janus.RoomExistsAsync(a.Answer.Room), Is.True, "the room exists on the gateway");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle), Is.True, "and holds the participant");
            await region.LogoutAsync(a);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task ParcelRoom_IsADifferentRoomFromTheEstateRoom()
        {
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InWestParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);

            // The viewer names its parcel; the region resolves the real one from the avatar's position.
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o, region.WestParcelLocalId));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));

            Assert.That(a.Answer.Provisioned, Is.True, "parcel provision: " + a.Answer.Body);
            Assert.That(b.Answer.Provisioned, Is.True, "estate provision: " + b.Answer.Body);
            Assert.That(a.Answer.Room, Is.Not.EqualTo(b.Answer.Room), "parcel and estate channel are separate rooms");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle), Is.True);
            Assert.That(await _janus.WaitForParticipantsAsync(b.Answer.Room, 1, Settle), Is.True);
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task SecondProvisionIntoTheSameRoom_SharesIt()
        {
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));

            Assert.That(b.Answer.Provisioned, Is.True, "second provision: " + b.Answer.Body);
            Assert.That(b.Answer.Room, Is.EqualTo(a.Answer.Room), "same room");
            Assert.That(b.Answer.ViewerSession, Is.Not.EqualTo(a.Answer.ViewerSession), "each agent has its own voice session");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True, "two participants on the gateway");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task Leave_RemovesTheParticipantAndKeepsTheOther()
        {
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));
            int room = a.Answer.Room;
            Assert.That(await _janus.WaitForParticipantsAsync(room, 2, Settle), Is.True);

            CapAnswer left = await region.LogoutAsync(a);

            Assert.That(left.Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(left.Body["response"].AsString(), Is.EqualTo("closed"));
            Assert.That(await _janus.WaitForParticipantsAsync(room, 1, Settle), Is.True, "one participant left in the room");
            await region.LogoutAsync(b);
            Assert.That(await _janus.WaitForParticipantsAsync(room, 0, Settle), Is.True, "and none after the second leaves");
        }

        // The slvoice mixer clears an emptied room on its own after its grace period. The stock AudioBridge plugin never
        // does, so the service removes the rooms it created once they have stood empty for EmptyRoomGraceSeconds (set
        // short here so the test does not wait a minute and a half). Either way the room is gone afterwards.
        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task EmptiedRoom_AfterTheGracePeriod_IsGoneFromTheGateway()
        {
            using var region = StockRegion.Create((voice, janus) => janus.Set("EmptyRoomGraceSeconds", "5"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            int room = a.Answer.Room;
            await region.LogoutAsync(a);
            Assert.That(await _janus.WaitForParticipantsAsync(room, 0, Settle), Is.True, "emptied");

            DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(StockJanusEnvironment.GraceSeconds, 60));
            while (await _janus.RoomExistsAsync(room) && DateTime.UtcNow < until)
                await Task.Delay(2000);

            Assert.That(await _janus.RoomExistsAsync(room), Is.False,
                $"room {room} is still on the gateway after its last participant left and the grace passed");
        }

        // A room the service did not create is never removed by it, however long it stands empty.
        [NeedsPlugin(JanusPluginKind.Stock)]
        [Test]
        public async Task EmptiedRoom_ThatTheServiceDidNotCreate_IsLeftInPlace()
        {
            StockJanusEnvironment.IgnoreUnlessAudioBridge();
            using var region = StockRegion.Create((voice, janus) => janus.Set("EmptyRoomGraceSeconds", "3"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            int room = JanusAudioBridge.CalcRoomNumber(string.Empty, region.Scene.RegionInfo.RegionID.ToString(), "local",
                JanusAudioBridge.REGION_ROOM_ID, string.Empty);
            JsonObject made = await _janus.PluginRequestAsync(new JsonObject
            {
                ["request"] = "create", ["room"] = room, ["permanent"] = false, ["description"] = "operator room"
            });
            Assert.That(made["audiobridge"]?.GetValue<string>(), Is.EqualTo("created"), "precondition: the operator made the room");
            try
            {
                using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
                Assert.That(a.Answer.Room, Is.EqualTo(room), "precondition: the service joined that room");
                await region.LogoutAsync(a);
                Assert.That(await _janus.WaitForParticipantsAsync(room, 0, Settle), Is.True, "emptied");

                await Task.Delay(TimeSpan.FromSeconds(40));   // well past the grace and several sweeps

                Assert.That(await _janus.RoomExistsAsync(room), Is.True, "the operator's room is still there");
            }
            finally
            {
                await _janus.PluginRequestAsync(new JsonObject { ["request"] = "destroy", ["room"] = room, ["permanent"] = false });
            }
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task Audio_TwoPeersHearEachOther_AndNotThemselves()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            const double ToneA = 440, ToneB = 880, Other = 1320;

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o), ToneA);
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o), ToneB);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
            await Task.Delay(1500);   // let the first frames flow before measuring

            Task<List<Measurement>> heardByA = a.Peer.MeasureWindowsAsync(6, 0.5, ToneA, ToneB, Other);
            Task<List<Measurement>> heardByB = b.Peer.MeasureWindowsAsync(6, 0.5, ToneA, ToneB, Other);
            List<Measurement> ma = await heardByA, mb = await heardByB;
            TestContext.Out.WriteLine("A (talks 440 Hz) heard: " + Measurement.Describe(ma, ToneA, ToneB, Other));
            TestContext.Out.WriteLine("B (talks 880 Hz) heard: " + Measurement.Describe(mb, ToneA, ToneB, Other));

            Assert.That(Measurement.MedianRms(ma), Is.GreaterThan(0.01), "A receives audible audio");
            Assert.That(Measurement.MedianShare(ma, ToneB), Is.GreaterThan(0.8), "A hears B's tone");
            Assert.That(Measurement.MedianShare(ma, ToneA), Is.LessThan(0.02), "A does not hear itself");
            Assert.That(Measurement.MedianRms(mb), Is.GreaterThan(0.01), "B receives audible audio");
            Assert.That(Measurement.MedianShare(mb, ToneA), Is.GreaterThan(0.8), "B hears A's tone");
            Assert.That(Measurement.MedianShare(mb, ToneB), Is.LessThan(0.02), "B does not hear itself");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task Audio_AParcelRoomDoesNotCarryTheEstateRoomsVoice()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InWestParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            const double ToneB = 880;

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o, region.WestParcelLocalId));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o), ToneB);
            await Task.Delay(1500);

            Measurement ma = await a.Peer.MeasureAsync(2, ToneB);
            TestContext.Out.WriteLine("A (other room) heard: " + ma);

            Assert.That(ma.Share(ToneB), Is.LessThan(0.01), "a listener in the parcel room does not hear the estate room");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }
    }
}
