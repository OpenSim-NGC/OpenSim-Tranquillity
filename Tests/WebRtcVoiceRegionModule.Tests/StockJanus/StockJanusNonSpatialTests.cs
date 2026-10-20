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
using osWebRtcVoice.NonSpatial;
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

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task A2A_CallRoomCreateJoinLeave()
        {
            using var region = StockRegion.Create((voice, _) => voice.Set("A2AVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);

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

        // Two region modules, each over its own scene and its own stock-Janus service, stand in for two region-server
        // processes; they share the carrier secret and the gateway, and nothing else. Each one's grid message transfer
        // is a recorder, and the test hands what one sends to the other's scene as the grid would.
        private static readonly string CarrierSecret = "0123456789abcdef0123456789abcdef-example";

        private static StockRegion CrossProcessRegion()
        {
            StockRegion r = StockRegion.Create((voice, _) =>
            {
                voice.Set("A2AVoiceEnabled", "true");
                voice.Set("CarrierSecret", CarrierSecret);
            });
            r.Scene.RegisterModuleInterface<OpenSim.Region.Framework.Interfaces.IMessageTransferModule>(new CrossProcessHarness.RecordingTransfer());
            return r;
        }

        private static void SetOnline(StockRegion r, params UUID[] agents)
        {
            var presence = new CrossProcessHarness.FakePresence();
            presence.SetOnline(agents);
            for (Type t = r.Scene.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField("m_PresenceService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f != null) { f.SetValue(r.Scene, presence); return; }
            }
            throw new MissingFieldException("Scene", "m_PresenceService");
        }

        private static List<OpenSim.Framework.GridInstantMessage> TakeCarriers(StockRegion r)
        {
            var transfer = (CrossProcessHarness.RecordingTransfer)r.Scene.RequestModuleInterface<OpenSim.Region.Framework.Interfaces.IMessageTransferModule>();
            var taken = new List<OpenSim.Framework.GridInstantMessage>(transfer.Sent);
            transfer.Sent.Clear();
            return taken;
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task A2A_CrossProcess_CallerAndCalleeJoinOneRoom()
        {
            using StockRegion one = CrossProcessRegion();
            using StockRegion two = CrossProcessRegion();
            one.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            two.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            SetOnline(one, Alice, Bob);
            SetOnline(two, Alice, Bob);
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);

            Assert.That((await one.ChatSessionAsync(Alice, ChatBody("start p2p voice", session, OSD.FromUUID(Bob)))).Status, Is.EqualTo((int)HttpStatusCode.OK));
            CapAnswer call = await one.ChatSessionAsync(Alice, ChatBody("call", session));
            Assert.That(call.Status, Is.EqualTo((int)HttpStatusCode.OK), "caller's call: " + call.Body);
            (string channel, string token) = CredentialsOf(call);
            Assert.That(one.Queue.Sent.Exists(s => s.To == Bob), Is.False, "Bob is not in the caller's process");

            List<OpenSim.Framework.GridInstantMessage> rings = TakeCarriers(one);
            Assert.That(rings, Has.Count.EqualTo(1), "one ring, signed, for the process where Bob is");
            two.Scene.EventManager.TriggerIncomingInstantMessage(rings[0]);
            var invitation = two.Queue.Sent.Find(s => s.To == Bob && s.Name == "ChatterBoxInvitation");
            Assert.That(invitation.Body, Is.Not.Null, "the callee's process delivered the invitation");
            Assert.That(((OSDMap)((OSDMap)invitation.Body)["voice"])["channel_credentials"].AsString(), Is.EqualTo(token));

            using Joined a = await one.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, token));
            Assert.That(a.Answer.Provisioned, Is.True, "caller joined: " + a.Answer.Body);
            using Joined b = await two.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, token));
            Assert.That(b.Answer.Provisioned, Is.True, "the callee's accept was admitted in its own process: " + b.Answer.Body);

            Assert.That(b.Answer.Room, Is.EqualTo(a.Answer.Room), "both parties, joined through two services, are in one room");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);

            // The callee joining reaches the caller's process as a signed join carrier.
            List<OpenSim.Framework.GridInstantMessage> joins = TakeCarriers(two);
            Assert.That(joins, Has.Count.EqualTo(1));
            one.Scene.EventManager.TriggerIncomingInstantMessage(joins[0]);

            await two.LogoutAsync(b);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle), Is.True, "the callee left the room");
            await one.LogoutAsync(a);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle), Is.True, "the caller left the room");
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task A2A_AStrangerWithTheRightChannelIsRefused_AndNoRoomIsCreated()
        {
            using var region = StockRegion.Create((voice, _) => voice.Set("A2AVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            UUID mallory = UUID.Random();
            region.AddAvatar(mallory, StockRegion.InEstateChannelParcel);
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);
            await region.ChatSessionAsync(Alice, ChatBody("start p2p voice", session, OSD.FromUUID(Bob)));
            CapAnswer call = await region.ChatSessionAsync(Alice, ChatBody("call", session));
            (string channel, string token) = CredentialsOf(call);
            List<int> before = await _janus.RoomsAsync();

            using var peer = await AiortcPeer.StartAsync();
            CapAnswer refused = await region.ProvisionAsync(mallory, StockRegion.MultiagentBody(peer.Offer, channel, token));

            Assert.That(refused.Status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(await _janus.RoomsAsync(), Is.EquivalentTo(before), "the refusal never reached the gateway");
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task A2A_Audio_BothPartiesHearEachOther()
        {
            StockJanusEnvironment.IgnoreUnlessPeerAvailable();
            using var region = StockRegion.Create((voice, _) => voice.Set("A2AVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);
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

        // ---- group ------------------------------------------------------------------------------

        private static (GroupVoicePolicy, NonSpatialVoiceSessionEngine) GroupFixture(StockRegion region)
        {
            // Membership answers come from the test, as in the module's own group tests: a scene has no groups service here.
            var policy = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = NonSpatialCaps.DefaultConferenceCap,
                IsMember = (a, g) => g == Group && (a == Alice || a == Bob),
                Powers = (a, g) => g == Group ? GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice : 0UL,
            };
            var engine = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), policy.ToAdmissionOrNull() },
                "example-grid");
            region.SetPrivate(region.Region, "m_groupVoice", policy);
            region.SetPrivate(region.Region, "m_nonSpatial", engine);
            return (policy, engine);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task Group_CallRoomCreateJoinLeave()
        {
            using var region = StockRegion.Create((voice, _) => voice.Set("GroupVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            (GroupVoicePolicy policy, NonSpatialVoiceSessionEngine engine) = GroupFixture(region);
            UUID regionId = region.Scene.RegionInfo.RegionID;

            CapAnswer aliceCall = Credentials(Alice, policy, engine, regionId);
            CapAnswer bobCall = Credentials(Bob, policy, engine, regionId);
            (string channel, string aliceToken) = CredentialsOf(aliceCall);
            (_, string bobToken) = CredentialsOf(bobCall);

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, aliceToken));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, bobToken));
            Assert.That(a.Answer.Provisioned, Is.True, "first member joined: " + a.Answer.Body);
            Assert.That(b.Answer.Provisioned, Is.True, "second member joined: " + b.Answer.Body);
            Assert.That(b.Answer.Room, Is.EqualTo(a.Answer.Room), "one group room");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);
            Assert.That(engine.Store.Get(Group).SeatsHeld, Is.EqualTo(2), "the engine holds both seats");

            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle), Is.True);
            Assert.That(engine.Store.Get(Group).SeatsHeld, Is.EqualTo(0), "the seats are released");

            static CapAnswer Credentials(UUID agent, GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, UUID r)
            {
                var call = new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(Group) };
                Assert.That(GroupVoiceChatSession.TryHandleCall(call, agent, p, e, r, out ChatSessionOutcome o), Is.True);
                return new CapAnswer { Status = (int)o.Status, Body = o.Body };
            }
        }

        // ---- ad-hoc conference ------------------------------------------------------------------

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task Adhoc_ConferenceRoomCreateJoinLeave()
        {
            using var region = StockRegion.Create((voice, _) => voice.Set("AdhocVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            var engine = region.GetPrivate<NonSpatialVoiceSessionEngine>(region.Region, "m_nonSpatial");
            UUID regionId = region.Scene.RegionInfo.RegionID;

            var start = new OSDMap
            {
                ["method"] = OSD.FromString("start conference"),
                ["session-id"] = OSD.FromUUID(UUID.Random()),
                ["params"] = new OSDArray { OSD.FromUUID(Bob) },
                ["alt_params"] = new OSDMap { ["voice_server_type"] = OSD.FromString("webrtc") },
            };
            Assert.That(AdhocVoiceChatSession.TryHandleStartConference(start, Alice, engine, regionId, true, out ChatSessionOutcome started), Is.True);
            UUID conference = started.Reply.SessionId;

            var callBody = new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(conference) };
            Assert.That(AdhocVoiceChatSession.TryHandleCall(callBody, Alice, engine, regionId, true, out ChatSessionOutcome aliceCall), Is.True);
            Assert.That(AdhocVoiceChatSession.TryHandleCall(callBody, Bob, engine, regionId, true, out ChatSessionOutcome bobCall), Is.True);
            (string channel, string aliceToken) = CredentialsOf(new CapAnswer { Body = aliceCall.Body });
            (_, string bobToken) = CredentialsOf(new CapAnswer { Body = bobCall.Body });

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, aliceToken));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, bobToken));
            Assert.That(a.Answer.Provisioned, Is.True, "conference creator joined: " + a.Answer.Body);
            Assert.That(b.Answer.Provisioned, Is.True, "invitee joined: " + b.Answer.Body);
            Assert.That(b.Answer.Room, Is.EqualTo(a.Answer.Room), "one conference room");
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle), Is.True);

            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
            Assert.That(await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle), Is.True);
        }

        // ---- every switch off ------------------------------------------------------------------

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task EverySwitchOff_NonSpatialRequestsAreAnsweredAsBeforeTheFeatures()
        {
            using var region = StockRegion.Create();   // no switch set: the shipped defaults
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            List<int> before = await _janus.RoomsAsync();
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);

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

        [NeedsPlugin(JanusPluginKind.Any)]
        [Test]
        public async Task EverySwitchOff_TheAnswersAreTheShippedDefaults()
        {
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);

            List<ProbeRow> rows = await DefaultsProbe.RunAsync(region, Alice, _janus);

            foreach (ProbeRow row in rows) TestContext.Out.WriteLine(row);
            ProbeRow Row(string name) => rows.Find(r => r.Name == name);
            // With no call feature on, these answer as upstream/develop does (with its
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

        [NeedsPlugin(JanusPluginKind.Any)]
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
