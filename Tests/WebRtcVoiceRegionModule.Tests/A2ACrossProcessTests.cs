/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * An avatar-to-avatar call between two region-server processes.
 *
 * Each process is a module over its own scene, its own invitation registry and its own voice service
 * stand-in; they share nothing but the CarrierSecret and the messages one hands to the other (the
 * carrier test double records what a process sends and the test delivers it to the other, the way the
 * grid instant message does). Requests go in through the module's own capability handlers, as the
 * viewer's do.
 *
 * The carriers are decoded and forged here by hand, from the wire layout, so these tests pin that
 * layout and the fields the MAC covers independently of the code that builds them.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CrossProcessHarness;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2ACrossProcessTests
    {
        private const string Grid = "example-grid";
        private const string Secret = "0123456789abcdef0123456789abcdef-example";
        private const string OtherSecret = "fedcba9876543210fedcba9876543210-example";
        private const string Magic = "LGVA2A1";

        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");
        private static readonly UUID Session = A2ACalls.ComputeSessionId(Alice, Bob);

        /// <summary>The voice service of one process: answers a provision as the real one does, with the room the grid id,
        /// channel and type decide, and a viewer session of its own.</summary>
        private sealed class RoomService : IWebRtcVoiceService
        {
            public readonly List<OSDMap> Requests = new();
            public readonly string GridId;
            public RoomService(string gridId) { GridId = gridId; }

            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene)
            {
                Requests.Add(pRequest);
                if (pRequest.TryGetBool("logout", out bool lg) && lg)
                    return new OSDMap { ["response"] = OSD.FromString("closed") };
                string channel = pRequest["channel"].AsString();
                return new OSDMap
                {
                    ["viewer_session"] = OSD.FromString("vs-" + pUserID.ToString().Substring(0, 8) + "-" + Requests.Count),
                    ["room"] = OSD.FromInteger(JanusAudioBridge.CalcRoomNumber(GridId, pScene.ToString(), "multiagent", 0, channel)),
                    ["jsep"] = new OSDMap { ["type"] = OSD.FromString("answer"), ["sdp"] = OSD.FromString("v=0") },
                };
            }
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene)
                => ProvisionVoiceAccountRequest(pRequest, pUserID, pScene);
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }

        /// <summary>The calls a process holds in its own session store, as the region module reads them.</summary>
        private sealed class CallsView
        {
            private readonly A2ACalls _calls;
            private readonly NonSpatialVoiceSessionEngine _engine;

            public CallsView(WebRtcVoiceRegionModule module)
            {
                _calls = (A2ACalls)typeof(WebRtcVoiceRegionModule)
                    .GetProperty("A2A", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(module);
                _engine = (NonSpatialVoiceSessionEngine)GetPrivate(module, "m_nonSpatial");
            }

            public A2ASession TryGet(UUID id) => _calls.TryGet(id, UUID.Zero);

            public int Count => _engine.Store.All().Count(c => c.Type == NonSpatialSessionType.P2P && !c.IsDead);
        }

        private sealed class Node
        {
            public Process Process;
            public WebRtcVoiceRegionModule Module;
            public RoomService Voice;

            public Scene Scene => Process.Scene;
            public CallsView Registry => new CallsView(Module);
            public List<GridInstantMessage> Carriers => Process.Transfer.Sent.Where(IsA2A).ToList();
            public List<(UUID Session, UUID To, List<GroupChatListAgentUpdateData> Updates)> AgentLists => Process.Queue.AgentLists;
            public string NameOf(UUID agent) => Scene.GetScenePresence(agent).Name;
        }

        private static Node MakeNode(string region, uint x, string secret = Secret, bool a2aOn = true)
        {
            Process p = new Process(region, x, 1000);
            p.Scene.RegisterModuleInterface<IWebRtcVoiceService>(new RoomService(Grid));
            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_a2aVoiceEnabled", a2aOn);
            SetPrivate(mod, "m_nonSpatial", new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission() }, Grid));
            SetPrivate(mod, "m_carrierAuth", secret is null ? CarrierAuth.Off() : new CarrierAuth(secret));
            ((List<Scene>)GetPrivate(mod, "m_scenes")).Add(p.Scene);
            return new Node { Process = p, Module = mod, Voice = (RoomService)p.Scene.RequestModuleInterface<IWebRtcVoiceService>() };
        }

        /// <summary>Alice is root in the first process, Bob root in the second, and each is online in the grid.</summary>
        private static (Node One, Node Two) Pair(string secretOne = Secret, string secretTwo = Secret)
        {
            Node one = MakeNode("Region One", 1000, secretOne);
            Node two = MakeNode("Region Two", 1001, secretTwo);
            one.Process.AddRoot(Alice);
            two.Process.AddRoot(Bob);
            one.Process.Presence.SetOnline(Alice, Bob);
            two.Process.Presence.SetOnline(Alice, Bob);
            return (one, two);
        }

        private static bool IsA2A(GridInstantMessage m)
            => m.binaryBucket is { Length: > 0 } && Encoding.UTF8.GetString(m.binaryBucket).StartsWith(Magic, StringComparison.Ordinal);

        private static OSDMap PayloadOf(GridInstantMessage m)
            => (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(m.binaryBucket).Substring(Magic.Length));

        // ---- requests, through the module's handlers ---------------------------------------------------

        private static (int Status, OSDMap Body) Chat(Node n, UUID agent, string method, UUID session, UUID? other = null)
        {
            OSDMap body = new OSDMap
            {
                ["method"] = OSD.FromString(method),
                ["session-id"] = OSD.FromUUID(session),
                ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc"), ["voice_server_type"] = OSD.FromString("webrtc") },
            };
            if (other.HasValue) body["params"] = OSD.FromUUID(other.Value);
            TestOSHttpResponse resp = new TestOSHttpResponse();
            n.Module.ChatSessionRequest(new LlsdPost(body), resp, agent, n.Scene);
            return (resp.StatusCode, Parse(resp));
        }

        private static (int Status, OSDMap Body) Provision(Node n, UUID agent, OSDMap body)
        {
            TestOSHttpResponse resp = new TestOSHttpResponse();
            n.Module.ProvisionVoiceAccountRequest(new LlsdPost(body), resp, agent, n.Scene);
            return (resp.StatusCode, Parse(resp));
        }

        private static OSDMap Parse(TestOSHttpResponse resp)
        {
            if (resp.RawBuffer is not { Length: > 0 })
                return null;
            try { return OSDParser.DeserializeLLSDXml(resp.RawBuffer) as OSDMap; } catch { return null; }
        }

        private static OSDMap JoinBody(string channel, string token) => new OSDMap
        {
            ["channel_type"] = OSD.FromString("multiagent"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["channel"] = OSD.FromString(channel),
            ["credentials"] = OSD.FromString(token),
            ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
        };

        private static OSDMap LogoutBody(string viewerSession) => new OSDMap
        {
            ["logout"] = OSD.FromBoolean(true),
            ["viewer_session"] = OSD.FromString(viewerSession),
            ["voice_server_type"] = OSD.FromString("webrtc"),
        };

        // ---- the call, step by step --------------------------------------------------------------------

        /// <summary>Alice opens the IM window and presses Call in the first process.</summary>
        private static string PlaceCall(Node caller)
        {
            Assert.That(Chat(caller, Alice, "start p2p voice", Session, Bob).Status, Is.EqualTo((int)HttpStatusCode.OK));
            (int status, OSDMap body) = Chat(caller, Alice, "call", Session);
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            return ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString();
        }

        /// <summary>Hands every A2A carrier the sender recorded to the receiver, as the grid IM would.</summary>
        private static List<GridInstantMessage> Carry(Node from, Node to)
        {
            List<GridInstantMessage> carried = from.Carriers;
            from.Process.Transfer.Sent.Clear();
            from.Process.Transfer.SentRootOnly.Clear();
            foreach (GridInstantMessage m in carried)
                Invoke(to.Module, "OnIncomingA2ACarrier", m);
            return carried;
        }

        /// <summary>The ring is placed, carried and delivered: Bob's viewer has the invitation. Returns the call's token.</summary>
        private static string RingBob(Node one, Node two)
        {
            string token = PlaceCall(one);
            Carry(one, two);
            return token;
        }

        private static string BobAccepts(Node two, string token)
        {
            (int status, OSDMap body) = Provision(two, Bob, JoinBody(Session.ToString(), token));
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK), "the callee's accept is admitted in its own process");
            return body["viewer_session"].AsString();
        }

        private static string AliceJoins(Node one, string token)
        {
            (int status, OSDMap body) = Provision(one, Alice, JoinBody(Session.ToString(), token));
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            return body["viewer_session"].AsString();
        }

        // ---- the ring ----------------------------------------------------------------------------------

        [Test]
        public void ACalleeInAnotherProcess_IsRungThroughASignedCarrier()
        {
            var (one, two) = Pair();

            PlaceCall(one);

            Assert.That(one.Process.Queue.Enqueued, Is.Empty, "Bob is not here");
            GridInstantMessage ring = one.Carriers.SingleOrDefault();
            Assert.That(ring, Is.Not.Null, "one carrier, to the process where Bob is root");
            Assert.That(new UUID(ring.toAgentID), Is.EqualTo(Bob));
            Assert.That(PayloadOf(ring)["step"].AsString(), Is.EqualTo("ring"));
            Assert.That(PayloadOf(ring).ContainsKey(CarrierAuth.AuthKey), Is.True, "signed");
            Assert.That(one.Process.Transfer.SentRootOnly.Single(), Is.True,
                "root only, or a child presence of Bob in this process would be handed the carrier as an instant message");
        }

        [Test]
        public void ACalleeWhoIsOnlyAChildPresenceInTheCallersProcess_IsRungThroughTheCarrier()
        {
            Node one = MakeNode("Region One", 1000);
            one.Process.AddRoot(Alice);
            one.Process.AddChild(Bob);
            one.Process.Presence.SetOnline(Alice, Bob);

            PlaceCall(one);

            Assert.That(one.Process.Queue.Enqueued, Does.Not.Contain(Bob), "the answer to a ring on a child goes to another process");
            Assert.That(one.Carriers, Has.Count.EqualTo(1));
            Assert.That(one.Process.Transfer.SentRootOnly.Single(), Is.True);
        }

        [Test]
        public void ACalleeInAnotherProcess_IsNotRungWithoutACarrierSecret()
        {
            var (one, _) = Pair(secretOne: null);

            PlaceCall(one);

            Assert.That(one.Process.Transfer.Sent, Is.Empty);
            Assert.That(one.Process.Queue.Enqueued, Is.Empty);
            Assert.That(one.Registry.TryGet(Session).InviteSent, Is.False, "a later call can still ring");
        }

        [Test]
        public void AnOfflineCallee_IsNotRungThroughTheCarrier()
        {
            Node one = MakeNode("Region One", 1000);
            one.Process.AddRoot(Alice);
            one.Process.Presence.SetOnline(Alice);

            PlaceCall(one);

            Assert.That(one.Process.Transfer.Sent, Is.Empty);
        }

        [Test]
        public void TheCalleesProcess_AdoptsTheCall_AndDeliversTheInvitationBuiltFromTheSignedFields()
        {
            var (one, two) = Pair();

            string token = RingBob(one, two);

            var (name, body, to) = two.Process.Queue.Events.Single();
            Assert.That(name, Is.EqualTo("ChatterBoxInvitation"));
            Assert.That(to, Is.EqualTo(Bob));
            OSDMap b = (OSDMap)body;
            Assert.That(b["session_id"].AsUUID(), Is.EqualTo(Session));
            Assert.That(b["from_id"].AsUUID(), Is.EqualTo(Alice));
            Assert.That(b["from_name"].AsString(), Is.EqualTo(one.NameOf(Alice)));
            OSDMap voice = (OSDMap)b["voice"];
            Assert.That(voice["channel_uri"].AsString(), Is.EqualTo(Session.ToString()));
            Assert.That(voice["channel_credentials"].AsString(), Is.EqualTo(token), "the token the caller's process issued");
            Assert.That(voice["invitation_type"].AsInteger(), Is.EqualTo(A2AInvitation.InvitationTypeP2P));
            Assert.That(two.Registry.TryGet(Session), Is.Not.Null, "the accept that follows can be admitted here");
            Assert.That(two.Process.Transfer.Sent, Is.Empty, "a received ring is never carried on");
        }

        // ---- the callee's accept and the room ----------------------------------------------------------

        [Test]
        public void TheCalleesAccept_IsAdmittedInItsOwnProcess_WithTheSameTokenAndChannel()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);

            BobAccepts(two, token);

            OSDMap sent = two.Voice.Requests.Single();
            Assert.That(sent["channel"].AsString(), Is.EqualTo(Session.ToString()));
            Assert.That(sent["credentials"].AsString(), Is.EqualTo(token));
            Assert.That(two.Registry.TryGet(Session).State, Is.EqualTo(A2ASessionState.Active));
        }

        [Test]
        public void TheCalleesOwnCall_GetsTheSameCredentialAsTheCallers()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);

            (int status, OSDMap body) = Chat(two, Bob, "call", Session);

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(((OSDMap)body["voice_credentials"])["channel_credentials"].AsString(), Is.EqualTo(token));
            Assert.That(two.Process.Queue.Enqueued, Has.Count.EqualTo(1), "the callee's call rings nobody");
            Assert.That(two.Process.Transfer.Sent, Is.Empty);
        }

        [Test]
        public void ACalleeWithAWrongToken_IsRefusedInItsOwnProcess()
        {
            var (one, two) = Pair();
            RingBob(one, two);

            (int status, _) = Provision(two, Bob, JoinBody(Session.ToString(), "not-the-token"));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(two.Voice.Requests, Is.Empty);
        }

        [Test]
        public void BothPartiesAreProvisionedIntoOneRoom()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);

            AliceJoins(one, token);
            BobAccepts(two, token);

            Assert.That(one.Voice.Requests.Single()["channel"].AsString(), Is.EqualTo(two.Voice.Requests.Single()["channel"].AsString()));
            int roomOne = JanusAudioBridge.CalcRoomNumber(Grid, one.Scene.RegionInfo.RegionID.ToString(), "multiagent", 0, Session.ToString());
            int roomTwo = JanusAudioBridge.CalcRoomNumber(Grid, two.Scene.RegionInfo.RegionID.ToString(), "multiagent", 0, Session.ToString());
            Assert.That(roomOne, Is.EqualTo(roomTwo), "the room does not depend on the region that provisions");
        }

        // ---- what the caller's viewer is told ----------------------------------------------------------

        [Test]
        public void WhenTheCalleeJoins_TheCallerIsToldAcrossProcesses()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            BobAccepts(two, token);

            bool rootOnly = two.Process.Transfer.SentRootOnly.Single();
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1), "one join carrier, to the caller's process");
            Assert.That(rootOnly, Is.True);
            Assert.That(new UUID(carried[0].toAgentID), Is.EqualTo(Alice));
            Assert.That(PayloadOf(carried[0])["step"].AsString(), Is.EqualTo("join"));
            var (session, to, updates) = one.AgentLists.Single();
            Assert.That(session, Is.EqualTo(Session));
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(updates.Select(u => u.agentID), Is.EquivalentTo(new[] { Bob, Alice }));
            Assert.That(updates.All(u => u.enterOrLeave && u.canVoice), Is.True,
                "an entry that cannot voice chat is read by the viewer as a decline");
            Assert.That(one.Registry.TryGet(Session).State, Is.EqualTo(A2ASessionState.Active));
            // The callee's own panel is told in its own process, with nothing carried for it.
            Assert.That(two.AgentLists.Select(l => l.To), Is.EqualTo(new[] { Bob }));
        }

        [Test]
        public void WhenTheCalleeDeclines_TheCallerIsToldItCannotVoiceChat_AndBothRecordsAreGone()
        {
            var (one, two) = Pair();
            RingBob(one, two);

            Assert.That(Chat(two, Bob, "decline p2p voice", Session).Status, Is.EqualTo((int)HttpStatusCode.OK));
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1));
            Assert.That(PayloadOf(carried[0])["step"].AsString(), Is.EqualTo("decline"));
            var (session, to, updates) = one.AgentLists.Single();
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(session, Is.EqualTo(Session));
            GroupChatListAgentUpdateData declined = updates.Single();
            Assert.That(declined.agentID, Is.EqualTo(Bob));
            Assert.That(declined.canVoice, Is.False, "the viewer ends the call on a peer that cannot voice chat (llimview.cpp:4369)");
            Assert.That(declined.enterOrLeave, Is.True, "a LEAVE is skipped before can_voice_chat is read (llimview.cpp:4364)");
            Assert.That(one.Registry.TryGet(Session), Is.Null);
            Assert.That(two.Registry.TryGet(Session), Is.Null);
        }

        [Test]
        public void TheCallerCancelling_DoesNotTellTheCallerThatTheCalleeDeclined()
        {
            var (one, two) = Pair();
            PlaceCall(one);

            Chat(one, Alice, "decline p2p voice", Session);

            Assert.That(one.Process.Transfer.Sent.Where(IsA2A).Count(m => PayloadOf(m)["step"].AsString() == "decline"), Is.Zero);
            Assert.That(one.AgentLists, Is.Empty);
        }

        [Test]
        public void WhenTheCalleeHangsUp_TheCallerIsToldTheyLeft_AndTheCalleesRecordIsGone()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            string bobVs = BobAccepts(two, token);
            Carry(two, one);
            one.AgentLists.Clear();

            Provision(two, Bob, LogoutBody(bobVs));
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1));
            Assert.That(PayloadOf(carried[0])["step"].AsString(), Is.EqualTo("leave"));
            var (_, to, updates) = one.AgentLists.Single();
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(updates.Single().agentID, Is.EqualTo(Bob));
            Assert.That(updates.Single().enterOrLeave, Is.False);
            Assert.That(two.Registry.TryGet(Session), Is.Null);
            Assert.That(one.Registry.TryGet(Session), Is.Not.Null, "the caller is still in the call here until it hangs up");
        }

        [Test]
        public void WhenTheCallerHangsUp_TheCalleeIsToldTheyLeft_AndAfterBothLeaveNoRecordIsLeft()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);
            string aliceVs = AliceJoins(one, token);
            string bobVs = BobAccepts(two, token);
            Carry(two, one);

            Provision(one, Alice, LogoutBody(aliceVs));
            List<GridInstantMessage> carried = Carry(one, two);

            Assert.That(PayloadOf(carried.Single())["step"].AsString(), Is.EqualTo("leave"));
            var (_, to, updates) = two.AgentLists.Last();
            Assert.That(to, Is.EqualTo(Bob));
            Assert.That(updates.Single().agentID, Is.EqualTo(Alice));

            Provision(two, Bob, LogoutBody(bobVs));
            Carry(two, one);
            Assert.That(two.Registry.TryGet(Session), Is.Null);
            Assert.That(one.Registry.TryGet(Session), Is.Null, "the caller's record goes when the caller has left and the callee's leave arrives");
        }

        [Test]
        public void WhenTheCalleesClientCloses_TheCallerIsToldTheyLeft()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            BobAccepts(two, token);
            Carry(two, one);
            one.AgentLists.Clear();

            Invoke(two.Module, "OnVoiceClientClosed", two.Scene, Bob, two.Scene);
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(PayloadOf(carried.Single())["step"].AsString(), Is.EqualTo("leave"));
            Assert.That(one.AgentLists.Single().To, Is.EqualTo(Alice));
            Assert.That(two.Registry.TryGet(Session), Is.Null);
        }

        // ---- carriers that must be refused --------------------------------------------------------------

        /// <summary>A carrier as the wire layout has it, signed with <paramref name="secret"/>.</summary>
        private static GridInstantMessage Craft(string step, UUID target, UUID about, string name, UUID session, string token,
                                                string room, string secret, Func<long> clock = null)
        {
            OSDMap payload = new OSDMap
            {
                ["step"] = OSD.FromString(step),
                ["about"] = OSD.FromUUID(about),
                ["name"] = OSD.FromString(name),
                ["room"] = OSD.FromString(room),
                ["token"] = OSD.FromString(token),
            };
            if (secret is not null)
                payload[CarrierAuth.AuthKey] = new CarrierAuth(secret, clock).Seal(new CarrierFields
                {
                    Kind = "a2a-" + step, Target = target, SessionId = session, SessionType = "P2P",
                    RoomKey = room, Caller = about, Credential = token, Text = name,
                });
            return new GridInstantMessage
            {
                toAgentID = target.Guid, fromAgentID = about.Guid, fromAgentName = name, dialog = 17, fromGroup = false,
                imSessionID = session.Guid, message = string.Empty, binaryBucket = Encoding.UTF8.GetBytes(Magic + OSDParser.SerializeJsonString(payload)),
                RegionID = UUID.Random().Guid,
            };
        }

        private static string RoomOf(UUID session) => NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.P2P, session);

        private static GridInstantMessage GoodRing(string secret = Secret, Func<long> clock = null)
            => Craft("ring", Bob, Alice, "Test User", Session, "tok-" + new string('a', 60), RoomOf(Session), secret, clock);

        private static void Deliver(Node to, GridInstantMessage m) => Invoke(to.Module, "OnIncomingA2ACarrier", m);

        [Test]
        public void ARingWithTheRightSecret_IsAcceptedAndRungOnce()
        {
            var (_, two) = Pair();

            Deliver(two, GoodRing());

            Assert.That(two.Process.Queue.Enqueued, Is.EqualTo(new[] { Bob }));
            Assert.That(two.Registry.TryGet(Session), Is.Not.Null);
        }

        [Test]
        public void AForgedRing_SignedWithAnotherSecret_IsRefused_AndNothingIsAdopted()
        {
            var (_, two) = Pair();

            Deliver(two, GoodRing(OtherSecret));

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.TryGet(Session), Is.Null);
            CarrierAuth auth = (CarrierAuth)GetPrivate(two.Module, "m_carrierAuth");
            Assert.That(auth.RefusalCount(CarrierAuth.BadMac), Is.EqualTo(1));
        }

        [Test]
        public void AnUnsignedRing_IsRefused()
        {
            var (_, two) = Pair();

            Deliver(two, GoodRing(secret: null));

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.TryGet(Session), Is.Null);
        }

        [Test]
        public void AReplayedRing_IsRefused_AndRingsOnlyOnce()
        {
            var (_, two) = Pair();
            GridInstantMessage ring = GoodRing();

            Deliver(two, ring);
            Chat(two, Bob, "decline p2p voice", Session);   // the callee turned it down; a replay must not bring it back
            two.Process.Queue.Events.Clear();
            two.Process.Queue.Enqueued.Clear();
            Deliver(two, ring);

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.TryGet(Session), Is.Null);
            CarrierAuth auth = (CarrierAuth)GetPrivate(two.Module, "m_carrierAuth");
            Assert.That(auth.RefusalCount(CarrierAuth.Replay), Is.EqualTo(1));
        }

        [Test]
        public void AStaleRing_IsRefused()
        {
            var (_, two) = Pair();
            long longAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;

            Deliver(two, GoodRing(clock: () => longAgo));

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            CarrierAuth auth = (CarrierAuth)GetPrivate(two.Module, "m_carrierAuth");
            Assert.That(auth.RefusalCount(CarrierAuth.Stale), Is.EqualTo(1));
        }

        [Test]
        public void ARingRetargetedToAnotherAgent_IsRefused()
        {
            var (_, two) = Pair();
            two.Process.AddRoot(Carol);
            GridInstantMessage ring = GoodRing();
            ring.toAgentID = Carol.Guid;

            Deliver(two, ring);

            Assert.That(two.Process.Queue.Enqueued, Is.Empty, "neither Carol nor Bob is rung");
            Assert.That(two.Registry.Count, Is.Zero);
        }

        [Test]
        public void ARingWithATamperedToken_OrSession_OrStep_IsRefused()
        {
            var (_, two) = Pair();

            GridInstantMessage tamperedToken = GoodRing();
            OSDMap p = PayloadOf(tamperedToken);
            p["token"] = OSD.FromString("tok-" + new string('b', 60));
            tamperedToken.binaryBucket = Encoding.UTF8.GetBytes(Magic + OSDParser.SerializeJsonString(p));

            GridInstantMessage asDecline = GoodRing();
            OSDMap q = PayloadOf(asDecline);
            q["step"] = OSD.FromString("decline");
            asDecline.binaryBucket = Encoding.UTF8.GetBytes(Magic + OSDParser.SerializeJsonString(q));

            GridInstantMessage otherSession = GoodRing();
            otherSession.imSessionID = UUID.Random().Guid;

            Deliver(two, tamperedToken);
            Deliver(two, asDecline);
            Deliver(two, otherSession);

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.Count, Is.Zero);
            CarrierAuth auth = (CarrierAuth)GetPrivate(two.Module, "m_carrierAuth");
            Assert.That(auth.RefusalCount(CarrierAuth.BadMac), Is.EqualTo(3), "every one fails the MAC, so the step is part of what is signed");
        }

        [Test]
        public void ASignedRing_WhoseSessionIsNotThePairs_IsRefused()
        {
            var (_, two) = Pair();
            UUID wrong = UUID.Random();

            Deliver(two, Craft("ring", Bob, Alice, "Test User", wrong, "tok-" + new string('a', 60), RoomOf(wrong), Secret));

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.TryGet(wrong), Is.Null);
        }

        [Test]
        public void ASignedRing_WithAnotherRoomKey_IsRefused()
        {
            var (_, two) = Pair();

            Deliver(two, Craft("ring", Bob, Alice, "Test User", Session, "tok-" + new string('a', 60), "nsv1:group:" + UUID.Random(), Secret));

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.Count, Is.Zero);
        }

        [Test]
        public void ARing_ForACalleeWhoIsOnlyAChildPresenceHere_IsNotAdoptedOrRung()
        {
            Node two = MakeNode("Region Two", 1001);
            two.Process.AddChild(Bob);

            Deliver(two, GoodRing());

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.Count, Is.Zero);
        }

        [Test]
        public void ARing_AtAnInstanceWithAvatarToAvatarCallsOff_IsIgnored()
        {
            Node two = MakeNode("Region Two", 1001, a2aOn: false);
            two.Process.AddRoot(Bob);

            Deliver(two, GoodRing());

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.Count, Is.Zero);
        }

        [Test]
        public void ARing_AtAnInstanceWithNoCarrierSecret_IsRefused()
        {
            var (_, two) = Pair(secretTwo: null);

            Deliver(two, GoodRing());

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.Count, Is.Zero);
        }

        [Test]
        public void ARing_ForACallThatIsAlreadyAnswered_IsRefused_AndTheLiveCallIsKept()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);
            BobAccepts(two, token);
            two.Process.Queue.Enqueued.Clear();

            Deliver(two, Craft("ring", Bob, Alice, "Test User", Session, "tok-" + new string('c', 60), RoomOf(Session), Secret));

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
            Assert.That(two.Registry.TryGet(Session).Token, Is.EqualTo(token));
            Assert.That(two.Registry.TryGet(Session).State, Is.EqualTo(A2ASessionState.Active));
        }

        [Test]
        public void ARing_AgainstAnUnansweredCallThatHoldsAnotherToken_IsRefused()
        {
            // Two people calling each other at the same moment: each process already gave its own caller a token.
            var (one, two) = Pair();
            PlaceCall(one);
            Assert.That(Chat(two, Bob, "start p2p voice", Session, Alice).Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Chat(two, Bob, "call", Session).Status, Is.EqualTo((int)HttpStatusCode.OK));
            two.Process.Queue.Enqueued.Clear();

            Carry(one, two);

            Assert.That(two.Process.Queue.Enqueued, Is.Empty, "the token Bob's viewer holds is not replaced");
        }

        // ---- the follow-up carriers need the call and its token ----------------------------------------

        [TestCase("join")]
        [TestCase("decline")]
        [TestCase("leave")]
        public void AStepForACallThisProcessDoesNotHold_IsRefusedAndChangesNothing(string step)
        {
            var (one, _) = Pair();
            one.Process.Queue.AgentLists.Clear();

            Deliver(one, Craft(step, Alice, Bob, string.Empty, Session, "tok-" + new string('a', 60), RoomOf(Session), Secret));

            Assert.That(one.AgentLists, Is.Empty);
            Assert.That(one.Registry.Count, Is.Zero);
        }

        [TestCase("join")]
        [TestCase("decline")]
        [TestCase("leave")]
        public void AStepCarryingAnotherToken_IsRefusedAndChangesNothing(string step)
        {
            var (one, _) = Pair();
            PlaceCall(one);
            one.Process.Transfer.Sent.Clear();

            Deliver(one, Craft(step, Alice, Bob, string.Empty, Session, "tok-" + new string('z', 60), RoomOf(Session), Secret));

            Assert.That(one.AgentLists, Is.Empty);
            Assert.That(one.Registry.TryGet(Session).State, Is.EqualTo(A2ASessionState.Invited));
        }

        [Test]
        public void ADeclineCannotEndAnAnsweredCall()
        {
            var (one, two) = Pair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            BobAccepts(two, token);
            Carry(two, one);
            one.AgentLists.Clear();

            Deliver(one, Craft("decline", Alice, Bob, string.Empty, Session, token, RoomOf(Session), Secret));

            Assert.That(one.Registry.TryGet(Session), Is.Not.Null);
            Assert.That(one.AgentLists, Is.Empty);
        }

        // ---- nothing changes without the cross-process path --------------------------------------------

        [Test]
        public void InOneProcess_TheCallStillRingsLocally_AndNothingIsCarried()
        {
            Node one = MakeNode("Region One", 1000);
            one.Process.AddRoot(Alice);
            one.Process.AddRoot(Bob);

            PlaceCall(one);

            Assert.That(one.Process.Queue.Enqueued, Is.EqualTo(new[] { Bob }));
            Assert.That(one.Process.Transfer.Sent, Is.Empty);
        }

        [Test]
        public void InOneProcess_ADeclineAlsoTellsTheCaller()
        {
            Node one = MakeNode("Region One", 1000);
            one.Process.AddRoot(Alice);
            one.Process.AddRoot(Bob);
            PlaceCall(one);

            Chat(one, Bob, "decline p2p voice", Session);

            var (_, to, updates) = one.AgentLists.Single();
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(updates.Single().canVoice, Is.False);
            Assert.That(one.Process.Transfer.Sent, Is.Empty);
        }

        // ---- SessionStoreMode grid: the processes share one session service and need no CarrierSecret ----------

        private static Node MakeGridNode(string region, uint x, VoiceSessionService service, string secret = null)
        {
            Process p = new Process(region, x, 1000);
            p.Scene.RegisterModuleInterface<IWebRtcVoiceService>(new RoomService(Grid));
            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_a2aVoiceEnabled", true);
            SetPrivate(mod, "m_nonSpatial", VoiceSessionServiceTests.NewRegion(service));
            SetPrivate(mod, "m_carrierAuth", secret is null ? CarrierAuth.Off() : new CarrierAuth(secret));
            ((List<Scene>)GetPrivate(mod, "m_scenes")).Add(p.Scene);
            return new Node { Process = p, Module = mod, Voice = (RoomService)p.Scene.RequestModuleInterface<IWebRtcVoiceService>() };
        }

        /// <summary>Alice is root in the first process, Bob in the second; both use the one session service and no secret.</summary>
        private static (Node One, Node Two) GridPair(string secretOne = null, string secretTwo = null)
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            Node one = MakeGridNode("Region One", 1000, service, secretOne);
            Node two = MakeGridNode("Region Two", 1001, service, secretTwo);
            one.Process.AddRoot(Alice);
            two.Process.AddRoot(Bob);
            one.Process.Presence.SetOnline(Alice, Bob);
            two.Process.Presence.SetOnline(Alice, Bob);
            return (one, two);
        }

        private static A2ASession CallIn(Node n, UUID reader)
        {
            A2ACalls calls = (A2ACalls)typeof(WebRtcVoiceRegionModule)
                .GetProperty("A2A", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(n.Module);
            return calls.TryGet(Session, reader);
        }

        [Test]
        public void Grid_ACalleeInAnotherProcess_IsRungWithNoSecret_AndTheCarrierHoldsNoCredential()
        {
            var (one, two) = GridPair();

            string token = PlaceCall(one);

            GridInstantMessage ring = one.Carriers.Single();
            OSDMap payload = PayloadOf(ring);
            Assert.That(payload["step"].AsString(), Is.EqualTo("ring"));
            Assert.That(payload["token"].AsString(), Is.Empty, "the credential lives in the session service, not on the wire");
            Assert.That(payload.ContainsKey(CarrierAuth.AuthKey), Is.False, "no secret, nothing signed");
            Assert.That(one.Process.Transfer.SentRootOnly.Single(), Is.True);

            Carry(one, two);

            var (name, body, to) = two.Process.Queue.Events.Single();
            Assert.That(name, Is.EqualTo("ChatterBoxInvitation"));
            Assert.That(to, Is.EqualTo(Bob));
            Assert.That(((OSDMap)((OSDMap)body)["voice"])["channel_credentials"].AsString(), Is.EqualTo(token),
                "the viewer is given the service's credential");
            Assert.That(two.Process.Transfer.Sent, Is.Empty, "a received ring is never carried on");
        }

        [Test]
        public void Grid_TheCalleeJoinsWithTheServicesCredential_AndBothEndUpInOneRoom()
        {
            var (one, two) = GridPair();
            string token = RingBob(one, two);

            AliceJoins(one, token);
            BobAccepts(two, token);

            Assert.That(one.Voice.Requests.Single()["channel"].AsString(), Is.EqualTo(two.Voice.Requests.Single()["channel"].AsString()));
            Assert.That(CallIn(one, Alice).State, Is.EqualTo(A2ASessionState.Active), "the one record says so in both processes");
            Assert.That(CallIn(two, Bob).State, Is.EqualTo(A2ASessionState.Active));
        }

        [Test]
        public void Grid_WhenTheCalleeJoins_TheCallerIsToldAcrossProcesses_AndTheCarrierHoldsNoCredential()
        {
            var (one, two) = GridPair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            BobAccepts(two, token);

            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1));
            Assert.That(PayloadOf(carried[0])["step"].AsString(), Is.EqualTo("join"));
            Assert.That(PayloadOf(carried[0])["token"].AsString(), Is.Empty);
            var (session, to, updates) = one.AgentLists.Single();
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(session, Is.EqualTo(Session));
            Assert.That(updates.Select(u => u.agentID), Is.EquivalentTo(new[] { Bob, Alice }));
            Assert.That(updates.All(u => u.enterOrLeave && u.canVoice), Is.True);
        }

        [Test]
        public void Grid_WhenTheCalleeDeclines_TheCallerIsToldItCannotVoiceChat_AndTheServiceHoldsNoCall()
        {
            var (one, two) = GridPair();
            RingBob(one, two);

            Assert.That(Chat(two, Bob, "decline p2p voice", Session).Status, Is.EqualTo((int)HttpStatusCode.OK));
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1));
            Assert.That(PayloadOf(carried[0])["step"].AsString(), Is.EqualTo("decline"));
            var (session, to, updates) = one.AgentLists.Single();
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(session, Is.EqualTo(Session));
            Assert.That(updates.Single().agentID, Is.EqualTo(Bob));
            Assert.That(updates.Single().canVoice, Is.False, "the viewer ends the call on a peer that cannot voice chat");
            Assert.That(CallIn(one, Alice), Is.Null);
            Assert.That(CallIn(two, Bob), Is.Null);
        }

        [Test]
        public void Grid_WhenTheCalleeHangsUp_TheCallerIsToldTheyLeft_AndTheCallStaysForTheCaller()
        {
            var (one, two) = GridPair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            string bobVs = BobAccepts(two, token);
            Carry(two, one);
            one.AgentLists.Clear();

            Provision(two, Bob, LogoutBody(bobVs));
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1));
            Assert.That(PayloadOf(carried[0])["step"].AsString(), Is.EqualTo("leave"));
            var (_, to, updates) = one.AgentLists.Single();
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(updates.Single().agentID, Is.EqualTo(Bob));
            Assert.That(updates.Single().enterOrLeave, Is.False);
            Assert.That(CallIn(one, Alice), Is.Not.Null, "the caller is still in the call until it hangs up");
        }

        [Test]
        public void Grid_WhenBothLeaveInDifferentProcesses_TheServiceHoldsNoLiveCallAtOnce()
        {
            var (one, two) = GridPair();
            string token = RingBob(one, two);
            string aliceVs = AliceJoins(one, token);
            string bobVs = BobAccepts(two, token);
            Carry(two, one);

            Provision(one, Alice, LogoutBody(aliceVs));
            Carry(one, two);
            Assert.That(CallIn(two, Bob), Is.Not.Null, "Bob is still in it");
            Provision(two, Bob, LogoutBody(bobVs));
            Carry(two, one);

            Assert.That(CallIn(one, Alice), Is.Null, "no record waits for the idle sweep");
            Assert.That(CallIn(two, Bob), Is.Null);
        }

        [Test]
        public void Grid_WhenTheCalleesClientCloses_TheCallerIsToldTheyLeft()
        {
            var (one, two) = GridPair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            BobAccepts(two, token);
            Carry(two, one);
            one.AgentLists.Clear();

            Invoke(two.Module, "OnVoiceClientClosed", two.Scene, Bob, two.Scene);
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(PayloadOf(carried.Single())["step"].AsString(), Is.EqualTo("leave"));
            Assert.That(one.AgentLists.Single().To, Is.EqualTo(Alice));
        }

        [Test]
        public void Grid_TwoPeopleCallingEachOtherAtTheSameMoment_ShareOneCall()
        {
            var (one, two) = GridPair();
            string aliceToken = PlaceCall(one);
            Assert.That(Chat(two, Bob, "start p2p voice", Session, Alice).Status, Is.EqualTo((int)HttpStatusCode.OK));
            (int status, OSDMap body) = Chat(two, Bob, "call", Session);
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            string bobToken = ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString();

            Carry(one, two);

            Assert.That(bobToken, Is.EqualTo(aliceToken), "one record, one credential");
            AliceJoins(one, aliceToken);
            BobAccepts(two, bobToken);
            Assert.That(CallIn(one, Alice).State, Is.EqualTo(A2ASessionState.Active));
        }

        // A carrier is only repeated service state: each of these claims something the service does not say.

        [Test]
        public void Grid_AForgedRing_ForACallThatDoesNotExist_RingsNobody()
        {
            var (_, two) = GridPair();

            Deliver(two, Craft("ring", Bob, Alice, "Test User", Session, string.Empty, RoomOf(Session), secret: null));

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
        }

        [Test]
        public void Grid_AReplayedRing_IsRefused_AndRingsOnlyOnce()
        {
            var (one, two) = GridPair();
            PlaceCall(one);
            GridInstantMessage ring = one.Carriers.Single();

            Deliver(two, ring);
            Deliver(two, ring);

            Assert.That(two.Process.Queue.Enqueued, Is.EqualTo(new[] { Bob }));
        }

        [Test]
        public void Grid_AForgedDecline_ForACallTheCalleeHasNotDeclined_ChangesNothing()
        {
            var (one, two) = GridPair();
            RingBob(one, two);
            one.AgentLists.Clear();

            Deliver(one, Craft("decline", Alice, Bob, string.Empty, Session, string.Empty, RoomOf(Session), secret: null));

            Assert.That(one.AgentLists, Is.Empty, "the viewer would have ended the call");
            Assert.That(CallIn(one, Alice), Is.Not.Null);
        }

        [Test]
        public void Grid_AForgedLeave_ForAPartyWhoIsStillSeated_ChangesNothing()
        {
            var (one, two) = GridPair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            BobAccepts(two, token);
            Carry(two, one);
            one.AgentLists.Clear();

            Deliver(one, Craft("leave", Alice, Bob, string.Empty, Session, string.Empty, RoomOf(Session), secret: null));

            Assert.That(one.AgentLists, Is.Empty);
        }

        [Test]
        public void Grid_AForgedJoin_ForAPartyWhoHasNotJoined_ChangesNothing()
        {
            var (one, two) = GridPair();
            string token = RingBob(one, two);
            AliceJoins(one, token);
            one.AgentLists.Clear();

            Deliver(one, Craft("join", Alice, Bob, string.Empty, Session, string.Empty, RoomOf(Session), secret: null));

            Assert.That(one.AgentLists, Is.Empty);
        }

        [Test]
        public void Grid_ARegionThatHasASecret_StillRequiresTheSignature()
        {
            var (one, two) = GridPair(secretTwo: Secret);
            PlaceCall(one);

            Carry(one, two);   // the sender has no secret, so its ring is unsigned

            Assert.That(two.Process.Queue.Enqueued, Is.Empty);
        }

        [Test]
        public void Grid_ASignedRing_IsAccepted_WhenBothRegionsHaveTheSecret()
        {
            var (one, two) = GridPair(Secret, Secret);
            PlaceCall(one);
            Assert.That(PayloadOf(one.Carriers.Single()).ContainsKey(CarrierAuth.AuthKey), Is.True);

            Carry(one, two);

            Assert.That(two.Process.Queue.Enqueued, Is.EqualTo(new[] { Bob }));
        }
    }
}
