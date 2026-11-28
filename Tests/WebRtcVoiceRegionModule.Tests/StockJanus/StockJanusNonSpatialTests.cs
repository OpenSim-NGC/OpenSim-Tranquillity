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
    public class StockJanusNonSpatialTests(RawJanus janus) : IAsyncLifetime, IClassFixture<RawJanus>
        {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly UUID Group = new UUID("9a0a0000-0000-4000-8000-0000000000aa");
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        private readonly RawJanus _janus = janus;
        private void RequireGateway() => StockJanusEnvironment.IgnoreUnlessConfigured();

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
        [Fact(Timeout = 240000)]
        public async Task A2A_CallRoomCreateJoinLeave()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create((voice, _) => voice.Set("A2AVoiceEnabled", "true"));
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);

            CapAnswer start = await region.ChatSessionAsync(Alice, ChatBody("start p2p voice", session, OSD.FromUUID(Bob)));
            {
                var assertedValue = start.Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "start p2p voice");
            }
            CapAnswer call = await region.ChatSessionAsync(Alice, ChatBody("call", session));
            {
                var assertedValue = call.Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "caller's call: " + call.Body);
            }
            Assert.True(region.Queue.Sent.Exists(s => s.To == Bob), "the callee was sent an invitation");
            CapAnswer bobCall = await region.ChatSessionAsync(Bob, ChatBody("call", session));
            {
                var assertedValue = bobCall.Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "callee's call: " + bobCall.Body);
            }

            (string channel, string aliceToken) = CredentialsOf(call);
            (_, string bobToken) = CredentialsOf(bobCall);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, aliceToken));
            Assert.True(a.Answer.Provisioned, "caller joined: " + a.Answer.Body);
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, bobToken));
            Assert.True(b.Answer.Provisioned, "callee joined: " + b.Answer.Body);

            {
                var assertedValue = b.Answer.Room;
                TestAssert.WithMessage(() => Assert.Equal(a.Answer.Room, assertedValue), "both parties are in one room");
            }
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle));
            await region.LogoutAsync(a);
            Assert.True((await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle)), "caller left");
            await region.LogoutAsync(b);
            Assert.True((await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle)), "callee left");
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
        [Fact(Timeout = 240000)]
        public async Task A2A_CrossProcess_CallerAndCalleeJoinOneRoom()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using StockRegion one = CrossProcessRegion();
            using StockRegion two = CrossProcessRegion();
            one.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            two.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            SetOnline(one, Alice, Bob);
            SetOnline(two, Alice, Bob);
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);

            Assert.Equal((int)HttpStatusCode.OK, (await one.ChatSessionAsync(Alice, ChatBody("start p2p voice", session, OSD.FromUUID(Bob)))).Status);
            CapAnswer call = await one.ChatSessionAsync(Alice, ChatBody("call", session));
            {
                var assertedValue = call.Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "caller's call: " + call.Body);
            }
            (string channel, string token) = CredentialsOf(call);
            Assert.False(one.Queue.Sent.Exists(s => s.To == Bob), "Bob is not in the caller's process");

            List<OpenSim.Framework.GridInstantMessage> rings = TakeCarriers(one);
            {
                var assertedValue = rings;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "one ring, signed, for the process where Bob is");
            }
            two.Scene.EventManager.TriggerIncomingInstantMessage(rings[0]);
            var invitation = two.Queue.Sent.Find(s => s.To == Bob && s.Name == "ChatterBoxInvitation");
            {
                var assertedValue = invitation.Body;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "the callee's process delivered the invitation");
            }
            Assert.Equal(token, ((OSDMap)((OSDMap)invitation.Body)["voice"])["channel_credentials"].AsString());

            using Joined a = await one.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, token));
            Assert.True(a.Answer.Provisioned, "caller joined: " + a.Answer.Body);
            using Joined b = await two.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, token));
            Assert.True(b.Answer.Provisioned, "the callee's accept was admitted in its own process: " + b.Answer.Body);

            {
                var assertedValue = b.Answer.Room;
                TestAssert.WithMessage(() => Assert.Equal(a.Answer.Room, assertedValue), "both parties, joined through two services, are in one room");
            }
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle));

            // The callee joining reaches the caller's process as a signed join carrier.
            List<OpenSim.Framework.GridInstantMessage> joins = TakeCarriers(two);
            Assert.Single(joins);
            one.Scene.EventManager.TriggerIncomingInstantMessage(joins[0]);

            await two.LogoutAsync(b);
            Assert.True((await _janus.WaitForParticipantsAsync(a.Answer.Room, 1, Settle)), "the callee left the room");
            await one.LogoutAsync(a);
            Assert.True((await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle)), "the caller left the room");
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task A2A_AStrangerWithTheRightChannelIsRefused_AndNoRoomIsCreated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            Assert.Equal((int)HttpStatusCode.Forbidden, refused.Status);
            {
                var assertedValue = await _janus.RoomsAsync();
                TestAssert.WithMessage(() => Assert.Equivalent(before, assertedValue, strict: true), "the refusal never reached the gateway");
            }
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task A2A_Audio_BothPartiesHearEachOther()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            List<Measurement> ma = await a.Peer.MeasureWindowsAsync(6, 0.5, 440, 880);
            List<Measurement> mb = await b.Peer.MeasureWindowsAsync(6, 0.5, 440, 880);
            TestContext.Current.TestOutputHelper.WriteLine("A2A caller heard: " + Measurement.Describe(ma, 440, 880));
            TestContext.Current.TestOutputHelper.WriteLine("A2A callee heard: " + Measurement.Describe(mb, 440, 880));

            Assert.True(Measurement.MedianShare(ma, 880) > 0.8, "the caller hears the callee");
            Assert.True(Measurement.MedianShare(mb, 440) > 0.8, "the callee hears the caller");
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
        [Fact(Timeout = 240000)]
        public async Task Group_CallRoomCreateJoinLeave()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.True(a.Answer.Provisioned, "first member joined: " + a.Answer.Body);
            Assert.True(b.Answer.Provisioned, "second member joined: " + b.Answer.Body);
            {
                var assertedValue = b.Answer.Room;
                TestAssert.WithMessage(() => Assert.Equal(a.Answer.Room, assertedValue), "one group room");
            }
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle));
            {
                var assertedValue = engine.Store.Get(Group).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the engine holds both seats");
            }

            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle));
            {
                var assertedValue = engine.Store.Get(Group).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the seats are released");
            }

            static CapAnswer Credentials(UUID agent, GroupVoicePolicy p, NonSpatialVoiceSessionEngine e, UUID r)
            {
                var call = new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(Group) };
                Assert.True(GroupVoiceChatSession.TryHandleCall(call, agent, p, e, r, out ChatSessionOutcome o));
                return new CapAnswer { Status = (int)o.Status, Body = o.Body };
            }
        }

        // ---- ad-hoc conference ------------------------------------------------------------------

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task Adhoc_ConferenceRoomCreateJoinLeave()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.True(AdhocVoiceChatSession.TryHandleStartConference(start, Alice, engine, regionId, true, out ChatSessionOutcome started));
            UUID conference = started.Reply.SessionId;

            var callBody = new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(conference) };
            Assert.True(AdhocVoiceChatSession.TryHandleCall(callBody, Alice, engine, regionId, true, out ChatSessionOutcome aliceCall));
            Assert.True(AdhocVoiceChatSession.TryHandleCall(callBody, Bob, engine, regionId, true, out ChatSessionOutcome bobCall));
            (string channel, string aliceToken) = CredentialsOf(new CapAnswer { Body = aliceCall.Body });
            (_, string bobToken) = CredentialsOf(new CapAnswer { Body = bobCall.Body });

            using Joined a = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, channel, aliceToken));
            using Joined b = await region.JoinAsync(Bob, o => StockRegion.MultiagentBody(o, channel, bobToken));
            Assert.True(a.Answer.Provisioned, "conference creator joined: " + a.Answer.Body);
            Assert.True(b.Answer.Provisioned, "invitee joined: " + b.Answer.Body);
            {
                var assertedValue = b.Answer.Room;
                TestAssert.WithMessage(() => Assert.Equal(a.Answer.Room, assertedValue), "one conference room");
            }
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 2, Settle));

            await region.LogoutAsync(a);
            await region.LogoutAsync(b);
            Assert.True(await _janus.WaitForParticipantsAsync(a.Answer.Room, 0, Settle));
        }

        // ---- every switch off ------------------------------------------------------------------

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task EverySwitchOff_NonSpatialRequestsAreAnsweredAsBeforeTheFeatures()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create();   // no switch set: the shipped defaults
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            List<int> before = await _janus.RoomsAsync();
            UUID session = A2ACalls.ComputeSessionId(Alice, Bob);

            {
                var assertedValue = (await region.ChatSessionAsync(Alice, ChatBody("call", session))).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.BadRequest, assertedValue), "call: unhandled method");
            }
            {
                var assertedValue = (await region.ChatSessionAsync(Alice, ChatBody("start conference", UUID.Random()))).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "start conference: acknowledged, nothing started");
            }
            {
                var assertedValue = (await region.ChatSessionAsync(Alice, ChatBody("decline invitation", session))).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "decline invitation");
            }
            {
                var assertedValue = region.Queue.StartReplies;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "no session reply was sent to a viewer");
            }

            // No chat session was started, so nothing reached the gateway so far.
            {
                var assertedValue = await _janus.RoomsAsync();
                TestAssert.WithMessage(() => Assert.Equivalent(before, assertedValue, strict: true), "no room was created on the gateway");
            }

            // A multiagent provision is served as upstream/develop serves it (see EverySwitchOff_TheAnswersAreTheShippedDefaults).
            using Joined multiagent = await region.JoinAsync(Alice, o => StockRegion.MultiagentBody(o, session.ToString(), "credentials"), waitConnected: false);
            Assert.Equal((int)HttpStatusCode.OK, multiagent.Answer.Status);
            Assert.True(multiagent.Answer.Provisioned, "served, as develop serves it: " + multiagent.Answer.Body);
            await region.LogoutAsync(multiagent);

            // A spatial provision is unaffected by the switches being off.
            using Joined spatial = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o), waitConnected: false);
            Assert.True(spatial.Answer.Provisioned, "spatial voice works with every optional switch off: " + spatial.Answer.Body);
            await region.LogoutAsync(spatial);
        }

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task EverySwitchOff_TheAnswersAreTheShippedDefaults()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);

            List<ProbeRow> rows = await DefaultsProbe.RunAsync(region, Alice, _janus);

            foreach (ProbeRow row in rows) TestContext.Current.TestOutputHelper.WriteLine(row.ToString());
            ProbeRow Row(string name) => rows.Find(r => r.Name == name);
            // With no call feature on, these answer as upstream/develop does (with its
            // two start-up fixes): a multiagent provision is served, a provision with no channel_type gets an empty 200.
            foreach (string served in new[] { "provision multiagent, unknown session", "provision multiagent, group-style channel" })
            {
                {
                    var assertedValue = Row(served).Status;
                    TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), served);
                }
                Assert.True(Row(served).Provisioned, served + ": joined a room");
            }
            Assert.Equal((int)HttpStatusCode.OK, Row("provision with no channel_type").Status);
            Assert.False(Row("provision with no channel_type").Provisioned);
            {
                var assertedValue = Row("provision with no channel_type").NewRooms;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "empty answer, no room");
            }
            // A channel_type the module issues no room for is refused before it reaches the voice service. Upstream
            // develop passes it on, the service throws and the request answers 500.
            Assert.Equal((int)HttpStatusCode.Forbidden, Row("provision channel_type=group").Status);
            {
                var assertedValue = Row("provision channel_type=group").NewRooms;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no room");
            }
            Assert.Equal("closed", Row("logout, unknown viewer session").Response);
            Assert.Equal((int)HttpStatusCode.BadRequest, Row("chat session: call").Status);
            Assert.Equal((int)HttpStatusCode.OK, Row("chat session: start conference").Status);
            Assert.Equal((int)HttpStatusCode.OK, Row("chat session: decline invitation").Status);
            Assert.Equal((int)HttpStatusCode.OK, Row("chat session: fetch history").Status);
            Assert.Equal((int)HttpStatusCode.BadRequest, Row("chat session: unknown method").Status);
        }

        // ---- recovery --------------------------------------------------------------------------

        [NeedsPlugin(JanusPluginKind.Any)]
        [Fact(Timeout = 240000)]
        public async Task GatewayRestartMidSession_TheServiceHealsAndNewProvisionsWork()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            StockJanusEnvironment.IgnoreUnlessRestartable();
            using var region = StockRegion.Create();
            region.AddAvatar(Alice, StockRegion.InEstateChannelParcel);
            region.AddAvatar(Bob, StockRegion.InEstateChannelParcel);
            using Joined a = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            Assert.True(a.Answer.Provisioned);
            var service = region.GetPrivate<IWebRtcVoiceService>(region.Service, "m_spatialVoiceService") as WebRtcJanusService;
            Assert.NotNull(service);
            Assert.True(((await service.ServiceListRoomsAsync())?.isSuccess), "the service session works before the restart");
            string sessionBefore = service.ServiceSessionId;

            await DockerControl.RestartGatewayAsync(_janus);

            AudioBridgeResp rooms = await service.ServiceListRoomsAsync();
            Assert.True((rooms?.isSuccess), "the service session reconnects by itself after a gateway restart");
            {
                var assertedValue = service.ServiceSessionId;
                TestAssert.WithMessage(() => Assert.NotEqual(sessionBefore, assertedValue), "on a new Janus session");
            }

            // The restart dropped Alice's media and her Janus session; the region is told by her viewer, which re-provisions.
            CapAnswer closed = await region.LogoutAsync(a);
            {
                var assertedValue = closed.Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "logout of the dead session does not fail the request");
            }
            using Joined again = await region.JoinAsync(Alice, o => StockRegion.LocalBody(o));
            Assert.True(again.Answer.Provisioned, "a fresh provision after the restart: " + again.Answer.Body);
            using Joined bob = await region.JoinAsync(Bob, o => StockRegion.LocalBody(o));
            Assert.True(bob.Answer.Provisioned, "a second agent after the restart: " + bob.Answer.Body);
            Assert.Equal(again.Answer.Room, bob.Answer.Room);
            Assert.True(await _janus.WaitForParticipantsAsync(again.Answer.Room, 2, Settle));
            await region.LogoutAsync(again);
            await region.LogoutAsync(bob);
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
