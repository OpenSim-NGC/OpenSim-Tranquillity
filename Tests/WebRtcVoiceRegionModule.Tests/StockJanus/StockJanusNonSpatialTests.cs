/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Opt-in integration tests (see StockJanusRegionTests.cs): avatar-to-avatar, group and ad-hoc conference voice,
 * each with its switch on, then every switch off, then a gateway restart in the middle of a session.
 */

using System;
using System.Collections.Generic;
using System.Net;
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
    public class StockJanusNonSpatialTests
    {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly UUID Group = new UUID("9a0a0000-0000-4000-8000-0000000000aa");
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        private readonly RawJanus _janus = new();

        [OneTimeSetUp]
        public void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

        private static (string Channel, string Token) CredentialsOf(CapAnswer a)
        {
            var creds = (OSDMap)a.Body["voice_credentials"];
            return (creds["channel_uri"].AsString(), creds["channel_credentials"].AsString());
        }

        private static OSDMap ChatBody(string method, UUID sessionId, OSD @params = null)
        {
            var m = new OSDMap
            {
                ["method"] = OSD.FromString(method),
                ["session-id"] = OSD.FromUUID(sessionId),
                ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
            };
            if (@params is not null) m["params"] = @params;
            return m;
        }

        // ---- avatar-to-avatar -------------------------------------------------------------------

        [Test]
        public async Task A2A_CallRoomCreateJoinLeave()
        {
            using var region = StockRegion.Create((voice, _) => voice.Set("A2AVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            UUID session = A2ASessionRegistry.ComputeSessionId(Alice, Bob);

            CapAnswer start = await region.ChatSessionAsync(Alice, ChatBody("start p2p voice", session, OSD.FromUUID(Bob)));
            Assert.That(start.Status, Is.EqualTo((int)HttpStatusCode.OK), "start p2p voice");
            CapAnswer call = await region.ChatSessionAsync(Alice, ChatBody("call", session));
            Assert.That(call.Status, Is.EqualTo((int)HttpStatusCode.OK), "caller's call: " + call.Body);
            Assert.That(region.Queue.Sent.Exists(s => s.To == Bob), Is.True, "the callee was sent an invitation");
            CapAnswer bobCall = await region.ChatSessionAsync(Bob, ChatBody("call", session));
            Assert.That(bobCall.Status, Is.EqualTo((int)HttpStatusCode.OK), "callee's call: " + bobCall.Body);

            (string channel, string aliceToken) = CredentialsOf(call);
            (_, string bobToken) = CredentialsOf(bobCall);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, aliceToken));
            Assert.That(a.Answer.Provisioned, Is.True, "caller joined: " + a.Answer.Body);
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, bobToken));
            Assert.That(b.Answer.Provisioned, Is.True, "callee joined: " + b.Answer.Body);

            Assert.That(b.Answer.Room, Is.EqualTo(a.Answer.Room), "both parties are in one room");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
            await region.LogoutAsync(a);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle), Is.True, "caller left");
            await region.LogoutAsync(b);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle), Is.True, "callee left");
        }

        [Test]
        public async Task A2A_AStrangerWithTheRightChannelIsRefused_AndNoRoomIsCreated()
        {
            using var region = StockRegion.Create((voice, _) => voice.Set("A2AVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            UUID mallory = UUID.Random();
            region.AddAvatar(mallory, StockRegion.InEstateChannelParcel);
            UUID session = A2ASessionRegistry.ComputeSessionId(Alice, Bob);
            await region.ChatSessionAsync(Alice, ChatBody("start p2p voice", session, OSD.FromUUID(Bob)));
            CapAnswer call = await region.ChatSessionAsync(Alice, ChatBody("call", session));
            (string channel, string token) = CredentialsOf(call);
            List<int> before = await _janus.RoomsAsync();

            using var peer = await AiortcPeer.StartAsync();
            CapAnswer refused = await region.ProvisionAsync(mallory, StockRegion.MultiagentBody(peer.Offer, channel, token));

            Assert.That(refused.Status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(await _janus.RoomsAsync(), Is.EquivalentTo(before), "the refusal never reached the gateway");
        }

        [Test]
        public async Task A2A_Audio_BothPartiesHearEachOther()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using var region = StockRegion.Create((voice, _) => voice.Set("A2AVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            UUID session = A2ASessionRegistry.ComputeSessionId(Alice, Bob);
            await region.ChatSessionAsync(Alice, ChatBody("start p2p voice", session, OSD.FromUUID(Bob)));
            CapAnswer call = await region.ChatSessionAsync(Alice, ChatBody("call", session));
            CapAnswer bobCall = await region.ChatSessionAsync(Bob, ChatBody("call", session));
            (string channel, string aliceToken) = CredentialsOf(call);
            (_, string bobToken) = CredentialsOf(bobCall);

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, aliceToken), 440);
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, bobToken), 880);
            await Task.Delay(1500);
            List<Measurement> ma = await a.Peer.MeasureWindowsAsync(6, 0.5, 440, 880);
            List<Measurement> mb = await b.Peer.MeasureWindowsAsync(6, 0.5, 440, 880);
            TestContext.Out.WriteLine("A2A caller heard: " + Measurement.Describe(ma, 440, 880));
            TestContext.Out.WriteLine("A2A callee heard: " + Measurement.Describe(mb, 440, 880));

            Assert.That(Measurement.MedianShare(ma, 880), Is.GreaterThan(0.8), "the caller hears the callee");
            Assert.That(Measurement.MedianShare(mb, 440), Is.GreaterThan(0.8), "the callee hears the caller");
            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
        }

        // ---- every switch off ------------------------------------------------------------------

        [Test]
        public async Task EverySwitchOff_NonSpatialRequestsAreAnsweredAsBeforeTheFeatures()
        {
            using var region = StockRegion.Create();   // no switch set: the shipped defaults
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            List<int> before = await _janus.RoomsAsync();
            UUID session = A2ASessionRegistry.ComputeSessionId(Alice, Bob);

            Assert.That((await region.ChatSessionAsync(Alice, ChatBody("call", session))).Status,
                Is.EqualTo((int)HttpStatusCode.BadRequest), "call: unhandled method");
            Assert.That((await region.ChatSessionAsync(Alice, ChatBody("start conference", UUID.Random()))).Status,
                Is.EqualTo((int)HttpStatusCode.OK), "start conference: acknowledged, nothing started");
            Assert.That((await region.ChatSessionAsync(Alice, ChatBody("decline invitation", session))).Status,
                Is.EqualTo((int)HttpStatusCode.OK), "decline invitation");
            Assert.That(region.Queue.StartReplies, Is.Empty, "no session reply was sent to a viewer");

            // No chat session was started, so nothing reached the gateway so far.
            Assert.That(await _janus.RoomsAsync(), Is.EquivalentTo(before), "no room was created on the gateway");

            // A multiagent provision is served as upstream/develop serves it (see EverySwitchOff_TheAnswersAreTheShippedDefaults).
            using Joined multiagent = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, session.ToString(), "credentials"), waitConnected: false);
            Assert.That(multiagent.Answer.Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(multiagent.Answer.Provisioned, Is.True, "served, as develop serves it: " + multiagent.Answer.Body);
            await region.LogoutAsync(multiagent);

            // A spatial provision is unaffected by the switches being off.
            using Joined spatial = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o), waitConnected: false);
            Assert.That(spatial.Answer.Provisioned, Is.True, "spatial voice works with every optional switch off: " + spatial.Answer.Body);
            await region.LogoutAsync(spatial);
        }

        [Test]
        public async Task EverySwitchOff_TheAnswersAreTheShippedDefaults()
        {
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);

            List<ProbeRow> rows = await DefaultsProbe.RunAsync(region, Alice, _janus);

            foreach (ProbeRow row in rows) TestContext.Out.WriteLine(row);
            ProbeRow Row(string name) => rows.Find(r => r.Name == name);
            // With avatar-to-avatar calls off, these answer as upstream/develop does (with its
            // two start-up fixes): a multiagent provision is served, a provision with no channel_type gets an empty 200.
            foreach (string served in new[] { "provision multiagent, unknown session", "provision multiagent, group-style channel" })
            {
                Assert.That(Row(served).Status, Is.EqualTo((int)HttpStatusCode.OK), served);
                Assert.That(Row(served).Provisioned, Is.True, served + ": joined a room");
            }
            Assert.That(Row("provision with no channel_type").Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Row("provision with no channel_type").Provisioned, Is.False);
            Assert.That(Row("provision with no channel_type").NewRooms, Is.EqualTo(0), "empty answer, no room");
            // A channel_type the module issues no room for is refused before it reaches the voice service. Upstream
            // develop passes it on, the service throws and the request answers 500.
            Assert.That(Row("provision channel_type=group").Status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(Row("provision channel_type=group").NewRooms, Is.EqualTo(0), "no room");
            Assert.That(Row("logout, unknown viewer session").Response, Is.EqualTo("closed"));
            Assert.That(Row("chat session: call").Status, Is.EqualTo((int)HttpStatusCode.BadRequest));
            Assert.That(Row("chat session: start conference").Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Row("chat session: decline invitation").Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Row("chat session: fetch history").Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Row("chat session: unknown method").Status, Is.EqualTo((int)HttpStatusCode.BadRequest));
        }

        // ---- recovery --------------------------------------------------------------------------

        [Test]
        public async Task GatewayRestartMidSession_TheServiceHealsAndNewProvisionsWork()
        {
            StockJanusEnvironment.IgnoreUnlessRestartable();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            Assert.That(a.Answer.Provisioned, Is.True);
            var service = region.GetPrivate<IWebRtcVoiceService>(region.Service, "m_spatialVoiceService") as WebRtcJanusService;
            Assert.That(service, Is.Not.Null);
            Assert.That((await service.ServiceListRoomsAsync())?.isSuccess, Is.True, "the service session works before the restart");
            string sessionBefore = service.ServiceSessionId;

            await DockerControl.RestartGatewayAsync(_janus);

            AudioBridgeResp rooms = await service.ServiceListRoomsAsync();
            Assert.That(rooms?.isSuccess, Is.True, "the service session reconnects by itself after a gateway restart");
            Assert.That(service.ServiceSessionId, Is.Not.EqualTo(sessionBefore), "on a new Janus session");

            // The restart dropped Alice's media and her Janus session; the region is told by her viewer, which re-provisions.
            CapAnswer closed = await region.LogoutAsync(a);
            Assert.That(closed.Status, Is.EqualTo((int)HttpStatusCode.OK), "logout of the dead session does not fail the request");
            using Joined again = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            Assert.That(again.Answer.Provisioned, Is.True, "a fresh provision after the restart: " + again.Answer.Body);
            using Joined bob = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));
            Assert.That(bob.Answer.Provisioned, Is.True, "a second agent after the restart: " + bob.Answer.Body);
            Assert.That(bob.Answer.Room, Is.EqualTo(again.Answer.Room));
            Assert.That(await _janus.WaitForParticipantsAsync(again.Answer.Room, 2, Settle), Is.True);
            await region.LogoutAsync(again);
            await region.LogoutAsync(bob);
        }
    }
}
