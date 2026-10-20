/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Avatar-to-avatar calls, driven through the two capability handlers a viewer uses: ChatSessionRequest
 * ("start p2p voice", "call", "decline p2p voice") and ProvisionVoiceAccountRequest (the multiagent
 * provision and the logout). Every assertion is about what the viewers and the voice service are told, so
 * the tests hold whatever the module uses to remember a call between those requests.
 *
 * They fix the behaviour a call has today: the invitation to the callee, the credentials, who may join and
 * the 403 refusals, the token, the agent-list updates, the hang-up and the close of a client, and the
 * handlers' answers while A2AVoiceEnabled is off.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2ACharacterisationTests
    {
        private static readonly UUID Alice = new UUID("a11ce000-0000-4000-8000-000000000001");
        private static readonly UUID Bob = new UUID("b0b00000-0000-4000-8000-000000000002");
        private static readonly UUID Carol = new UUID("ca401000-0000-4000-8000-000000000003");
        private static readonly UUID CallId = new UUID(Alice.ulonga ^ Bob.ulonga, Alice.ulongb ^ Bob.ulongb);
        private const string Grid = "example-grid";

        // ---- fixtures ------------------------------------------------------------------------------------

        private sealed class Post : IOSHttpRequest
        {
            public Post(OSDMap body)
            {
                InputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(body)));
            }
            public string HttpMethod => "POST";
            public Uri Url => new Uri("http://sim.example.org/voice");
            public string RawUrl => "/voice";
            public string UriPath => "/voice";
            public Stream InputStream { get; set; }
            public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
            public bool HasEntityBody => true;
            public long ContentLength => InputStream.Length;
            public long ContentLength64 => InputStream.Length;
            public string ContentType => "application/llsd+xml";
            public string[] AcceptTypes => Array.Empty<string>();
            public System.Text.Encoding ContentEncoding => System.Text.Encoding.UTF8;
            public bool IsSecured => false;
            public bool KeepAlive => false;
            public System.Collections.Specialized.NameValueCollection QueryString => throw new NotImplementedException();
            public System.Collections.Hashtable Query => throw new NotImplementedException();
            public HashSet<string> QueryFlags => throw new NotImplementedException();
            public Dictionary<string, string> QueryAsDictionary => throw new NotImplementedException();
            public IPEndPoint RemoteIPEndPoint => new(IPAddress.Parse("192.0.2.1"), 1);
            public IPEndPoint LocalIPEndPoint => new(IPAddress.Parse("192.0.2.2"), 2);
            public string UserAgent => "test";
            public double ArrivalTS => 0;
        }

        /// <summary>The voice service: answers every request with the success map a real provision returns.</summary>
        private sealed class StubVoiceService : IWebRtcVoiceService
        {
            public int Calls;
            public readonly List<OSDMap> Requests = new();
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene)
            {
                Calls++;
                Requests.Add(pRequest);
                return new OSDMap
                {
                    ["viewer_session"] = OSD.FromString("vs-" + Calls),
                    ["room"] = OSD.FromInteger(68971284),
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

        /// <summary>Records what the handlers send to a viewer's event queue; every other member is unreachable.</summary>
        private sealed class RecordingQueue : IEventQueue
        {
            public readonly List<(UUID To, OSDMap Body)> Invitations = new();
            public readonly List<(UUID Session, string Name, int Type, bool VoiceEnabled, bool Moderated, UUID Temp, bool Success, string Error, UUID To)> StartReplies = new();
            public readonly List<(UUID Session, UUID To, List<GroupChatListAgentUpdateData> Updates)> AgentLists = new();
            private readonly List<(string Name, OSD Body)> m_built = new();

            public byte[] BuildEvent(string eventName, OSD eventBody)
            {
                m_built.Add((eventName, eventBody));
                return new byte[] { (byte)(m_built.Count - 1) };
            }
            public bool Enqueue(byte[] o, UUID avatarID)
            {
                (string name, OSD body) = m_built[o[0]];
                if (name == "ChatterBoxInvitation")
                    Invitations.Add((avatarID, (OSDMap)body));
                return true;
            }
            public void ChatterBoxSessionStartReply(UUID sessionID, string sessionName, int type, bool voiceEnabled, bool voiceModerated, UUID tmpSessionID, bool sucess, string error, UUID toAgent)
                => StartReplies.Add((sessionID, sessionName, type, voiceEnabled, voiceModerated, tmpSessionID, sucess, error, toAgent));
            public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates)
                => AgentLists.Add((sessionID, toAgent, updates));

            public bool Enqueue(OSD o, UUID avatarID) => throw new NotSupportedException();
            public bool Enqueue(osUTF8 o, UUID avatarID) => throw new NotSupportedException();
            public void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void EstablishAgentCommunication(UUID avatarID, IPEndPoint endPoint, string capsPath, ulong regionHandle, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void TeleportFinishEvent(ulong regionHandle, byte simAccess, IPEndPoint regionExternalEndPoint, uint locationID, uint flags, string capsURL, UUID agentID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, IPEndPoint newRegionExternalEndPoint, string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void ChatterboxInvitation(UUID sessionID, string sessionName, UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog, uint timeStamp, bool offline, int parentEstateID, Vector3 position, uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket) => throw new NotSupportedException();
            public void ChatterBoxForceClose(UUID toAgent, UUID sessionID, string reason) => throw new NotSupportedException();
            public void GroupMembershipData(UUID receiverAgent, GroupMembershipData[] data) => throw new NotSupportedException();
            public void ScriptRunningEvent(UUID objectID, UUID itemID, bool running, UUID avatarID) => throw new NotSupportedException();
            public void partPhysicsProperties(uint localID, byte physhapetype, float density, float friction, float bounce, float gravmod, UUID avatarID) => throw new NotSupportedException();
            public void WindlightRefreshEvent(int interpolate, UUID avatarID) => throw new NotSupportedException();
            public void SendEnvironmentUpdate(UUID experience_id, UUID agent_id, EnvironmentUpdate update) => throw new NotSupportedException();
            public void SendBulkUpdateInventoryItem(InventoryItemBase item, UUID avatarID, UUID? transationID = null) => throw new NotSupportedException();
            public osUTF8 StartEvent(string eventName) => throw new NotSupportedException();
            public osUTF8 StartEvent(string eventName, int cap) => throw new NotSupportedException();
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<byte[]> message) => throw new NotSupportedException();
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<string> message) => throw new NotSupportedException();
            public byte[] EndEventToBytes(osUTF8 sb) => throw new NotSupportedException();
        }

        private sealed class World
        {
            public WebRtcVoiceRegionModule Module;
            public Scene Scene;
            public StubVoiceService Voice;
            public RecordingQueue Queue;
        }

        private static void SetPrivate(object target, string field, object value)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? t.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(t.Name, field);
            f.SetValue(f.IsStatic ? null : target, value);
        }

        private static object InvokePrivate(object target, string method, params object[] args)
        {
            MethodInfo m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException(target.GetType().Name, method);
            return m.Invoke(target, args);
        }

        /// <summary>One region with the listed agents present as root agents and the call feature on or off.</summary>
        private static World NewWorld(bool a2aOn = true, params UUID[] present)
        {
            Scene scene = new SceneHelpers().SetupScene();
            StubVoiceService voice = new StubVoiceService();
            RecordingQueue queue = new RecordingQueue();
            scene.RegisterModuleInterface<IWebRtcVoiceService>(voice);
            scene.RegisterModuleInterface<IEventQueue>(queue);
            foreach (UUID agent in present)
                SceneHelpers.AddScenePresence(scene, agent);

            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_a2aVoiceEnabled", a2aOn);
            SetPrivate(mod, "m_nonSpatial", new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission() },
                Grid));
            InvokePrivate(mod, "RegisterSceneForVoiceSessions", scene);
            return new World { Module = mod, Scene = scene, Voice = voice, Queue = queue };
        }

        private static World NewWorld(params UUID[] present) => NewWorld(true, present);

        private static OSDMap StartBody(UUID viewerSession, UUID other) => new OSDMap
        {
            ["method"] = OSD.FromString("start p2p voice"),
            ["session-id"] = OSD.FromUUID(viewerSession),
            ["params"] = OSD.FromUUID(other),
            ["alt_params"] = new OSDMap { ["voice_server_type"] = OSD.FromString("webrtc") },
        };

        private static OSDMap MethodBody(string method, UUID session) => new OSDMap
        {
            ["method"] = OSD.FromString(method),
            ["session-id"] = OSD.FromUUID(session),
            ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
        };

        private static (int Status, OSDMap Body) Chat(World w, UUID agent, OSDMap body)
        {
            TestOSHttpResponse resp = new TestOSHttpResponse();
            w.Module.ChatSessionRequest(new Post(body), resp, agent, w.Scene);
            return (resp.StatusCode, Parse(resp));
        }

        private static (int Status, OSDMap Body) Provision(World w, UUID agent, OSDMap body)
        {
            TestOSHttpResponse resp = new TestOSHttpResponse();
            w.Module.ProvisionVoiceAccountRequest(new Post(body), resp, agent, w.Scene);
            return (resp.StatusCode, Parse(resp));
        }

        private static OSDMap Parse(TestOSHttpResponse resp)
        {
            if (resp.RawBuffer is not { Length: > 0 })
                return null;
            try { return OSDParser.DeserializeLLSDXml(resp.RawBuffer) as OSDMap; }
            catch (Exception) { return null; }
        }

        private static OSDMap MultiagentBody(string channel, string credentials) => new OSDMap
        {
            ["channel_type"] = OSD.FromString("multiagent"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["channel"] = OSD.FromString(channel),
            ["credentials"] = OSD.FromString(credentials),
            ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
        };

        private static OSDMap LogoutBody(string viewerSession) => new OSDMap
        {
            ["logout"] = OSD.FromBoolean(true),
            ["viewer_session"] = OSD.FromString(viewerSession),
            ["voice_server_type"] = OSD.FromString("webrtc"),
        };

        /// <summary>Alice starts the call to Bob and presses call; returns the credentials she is given.</summary>
        private static string StartAndCall(World w)
        {
            Assert.That(Chat(w, Alice, StartBody(CallId, Bob)).Status, Is.EqualTo((int)HttpStatusCode.OK));
            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            return ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString();
        }

        /// <summary>Alice and Bob have both joined: Alice's provision is vs-1, Bob's is vs-2.</summary>
        private static string FormCall(World w)
        {
            string token = StartAndCall(w);
            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
            return token;
        }

        private static int Forbidden => (int)HttpStatusCode.Forbidden;

        // ---- start p2p voice ------------------------------------------------------------------------------

        [Test]
        public void Start_AnswersOk_AndSendsTheStartReplyToTheCaller()
        {
            World w = NewWorld(Alice, Bob);

            (int status, OSDMap body) = Chat(w, Alice, StartBody(CallId, Bob));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(body, Is.Null, "the answer to the POST has no body; the viewer waits for the event");
            Assert.That(w.Queue.StartReplies, Has.Count.EqualTo(1));
            var reply = w.Queue.StartReplies[0];
            Assert.That(reply.Session, Is.EqualTo(CallId), "the session id is the XOR of the two agents");
            Assert.That(reply.Temp, Is.EqualTo(CallId), "the viewer's own id, so the re-key is a no-op");
            Assert.That(reply.To, Is.EqualTo(Alice));
            Assert.That(reply.Type, Is.EqualTo(2));
            Assert.That(reply.VoiceEnabled, Is.False);
            Assert.That(reply.Moderated, Is.True);
            Assert.That(reply.Success, Is.True);
            Assert.That(reply.Error, Is.Empty);
        }

        [Test]
        public void Start_NeverRingsTheCallee()
        {
            World w = NewWorld(Alice, Bob);

            Chat(w, Alice, StartBody(CallId, Bob));
            Chat(w, Alice, StartBody(CallId, Bob));

            Assert.That(w.Queue.Invitations, Is.Empty, "the viewer sends this on every IM window it opens");
        }

        [Test]
        public void Start_RepeatedFromEitherSide_AnswersEachTime_AndKeepsOneCall()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.That(Chat(w, Bob, StartBody(CallId, Alice)).Status, Is.EqualTo((int)HttpStatusCode.OK), "the callee opens the IM window");
            Assert.That(Chat(w, Alice, StartBody(CallId, Bob)).Status, Is.EqualTo((int)HttpStatusCode.OK));

            (int status, OSDMap body) = Chat(w, Bob, MethodBody("call", CallId));
            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(((OSDMap)body["voice_credentials"])["channel_credentials"].AsString(), Is.EqualTo(token),
                "a repeated start keeps the credentials already handed out");
        }

        [Test]
        public void Start_WithoutParams_Is400()
        {
            World w = NewWorld(Alice, Bob);
            OSDMap body = StartBody(CallId, Bob);
            body.Remove("params");

            Assert.That(Chat(w, Alice, body).Status, Is.EqualTo((int)HttpStatusCode.BadRequest));
            Assert.That(w.Queue.StartReplies, Is.Empty);
        }

        [Test]
        public void Start_WithTheCallerAsParams_Is400()
        {
            World w = NewWorld(Alice, Bob);

            Assert.That(Chat(w, Alice, StartBody(CallId, Alice)).Status, Is.EqualTo((int)HttpStatusCode.BadRequest));
            Assert.That(w.Queue.StartReplies, Is.Empty);
        }

        // ---- call: the credentials and the invitation --------------------------------------------------------

        [Test]
        public void Call_ByTheCaller_ReturnsTheCredentialsInTheBody()
        {
            World w = NewWorld(Alice, Bob);
            Chat(w, Alice, StartBody(CallId, Bob));

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            OSDMap creds = (OSDMap)body["voice_credentials"];
            Assert.That(creds["voice_server_type"].AsString(), Is.EqualTo("webrtc"));
            Assert.That(creds["channel_uri"].AsString(), Is.EqualTo(CallId.ToString()), "the channel is the session id as text");
            Assert.That(creds["channel_credentials"].AsString(), Does.Match("^[0-9a-f]{64}$"), "a 32-byte token in lowercase hex");
            Assert.That(creds.ContainsKey("sip_uri"), Is.False);
        }

        [Test]
        public void Call_RingsTheCalleeOnce_WithTheSameCredentials()
        {
            World w = NewWorld(Alice, Bob);

            string token = StartAndCall(w);

            Assert.That(w.Queue.Invitations, Has.Count.EqualTo(1));
            (UUID to, OSDMap invite) = w.Queue.Invitations[0];
            Assert.That(to, Is.EqualTo(Bob), "delivered to the callee, not the caller");
            Assert.That(invite["session_id"].AsUUID(), Is.EqualTo(CallId));
            Assert.That(invite["from_id"].AsUUID(), Is.EqualTo(Alice));
            Assert.That(invite.ContainsKey("instantmessage"), Is.False, "that key would send the viewer down its IM branch");
            OSDMap voice = (OSDMap)invite["voice"];
            Assert.That(voice["invitation_type"].AsInteger(), Is.EqualTo(2));
            Assert.That(voice["voice_server_type"].AsString(), Is.EqualTo("webrtc"));
            Assert.That(voice["channel_uri"].AsString(), Is.EqualTo(CallId.ToString()));
            Assert.That(voice["channel_credentials"].AsString(), Is.EqualTo(token), "the callee is handed the caller's token");
        }

        [Test]
        public void Call_RepeatedByTheCaller_ReturnsTheSameCredentials_AndDoesNotRingAgain()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            for (int i = 0; i < 3; i++)
            {
                (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));
                Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
                Assert.That(((OSDMap)body["voice_credentials"])["channel_credentials"].AsString(), Is.EqualTo(token));
            }

            Assert.That(w.Queue.Invitations, Has.Count.EqualTo(1), "one ring per invitation");
        }

        [Test]
        public void Call_ByTheCallee_ReturnsTheSameCredentials_AndRingsNobody()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);
            w.Queue.Invitations.Clear();

            (int status, OSDMap body) = Chat(w, Bob, MethodBody("call", CallId));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(((OSDMap)body["voice_credentials"])["channel_credentials"].AsString(), Is.EqualTo(token));
            Assert.That(w.Queue.Invitations, Is.Empty, "a callee's call must not ring the caller back");
        }

        [Test]
        public void Call_WhileTheCallIsActive_ReturnsCredentials_AndDoesNotRing()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.Invitations.Clear();

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(((OSDMap)body["voice_credentials"])["channel_credentials"].AsString(), Is.EqualTo(token));
            Assert.That(w.Queue.Invitations, Is.Empty);
        }

        [Test]
        public void Call_WhenTheCalleeCouldNotBeReached_RingsOnTheNextCall()
        {
            World w = NewWorld(Alice);   // Bob is not on this region server
            Chat(w, Alice, StartBody(CallId, Bob));
            Assert.That(Chat(w, Alice, MethodBody("call", CallId)).Status, Is.EqualTo((int)HttpStatusCode.OK), "the caller still gets credentials");
            Assert.That(w.Queue.Invitations, Is.Empty);

            SceneHelpers.AddScenePresence(w.Scene, Bob);
            Assert.That(Chat(w, Alice, MethodBody("call", CallId)).Status, Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(w.Queue.Invitations, Has.Count.EqualTo(1), "a failed delivery does not use up the ring");
            Assert.That(w.Queue.Invitations[0].To, Is.EqualTo(Bob));
        }

        [Test]
        public void Call_ForASessionNobodyStarted_Is404()
        {
            World w = NewWorld(Alice, Bob);

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
            Assert.That(body, Is.Null);
            Assert.That(w.Queue.Invitations, Is.Empty);
        }

        [Test]
        public void Call_ByAStrangerOnALiveSession_Is403_AndLearnsNothing()
        {
            World w = NewWorld(Alice, Bob, Carol);
            Chat(w, Alice, StartBody(CallId, Bob));

            (int status, OSDMap body) = Chat(w, Carol, MethodBody("call", CallId));

            Assert.That(status, Is.EqualTo(Forbidden));
            Assert.That(body, Is.Null, "no credentials");
            Assert.That(w.Queue.Invitations, Is.Empty);
        }

        [Test]
        public void Call_ForTheSessionIdOfAnotherPair_Is404()
        {
            World w = NewWorld(Alice, Bob, Carol);
            Chat(w, Alice, StartBody(CallId, Bob));
            UUID other = new UUID(Alice.ulonga ^ Carol.ulonga, Alice.ulongb ^ Carol.ulongb);

            Assert.That(Chat(w, Alice, MethodBody("call", other)).Status, Is.EqualTo((int)HttpStatusCode.NotFound));
        }

        // ---- the multiagent provision: who may join --------------------------------------------------------

        [Test]
        public void Provision_TheCallerWithTheToken_IsAdmitted_AndReachesTheVoiceService()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            (int status, OSDMap body) = Provision(w, Alice, MultiagentBody(CallId.ToString(), token));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(w.Voice.Calls, Is.EqualTo(1));
            Assert.That(w.Voice.Requests[0]["channel"].AsString(), Is.EqualTo(CallId.ToString()), "the body goes on unchanged");
            Assert.That(body["viewer_session"].AsString(), Is.EqualTo("vs-1"));
        }

        [Test]
        public void Provision_TheCalleeWithTheToken_IsAdmitted()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(w.Voice.Calls, Is.EqualTo(1));
        }

        [Test]
        public void Provision_AStrangerWithTheRightToken_Is403_AndNeverReachesTheVoiceService()
        {
            World w = NewWorld(Alice, Bob, Carol);
            string token = StartAndCall(w);

            Assert.That(Provision(w, Carol, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo(Forbidden));
            Assert.That(w.Voice.Calls, Is.Zero);
        }

        [TestCase("not-the-token")]
        [TestCase("")]
        public void Provision_AWrongOrEmptyToken_Is403(string credentials)
        {
            World w = NewWorld(Alice, Bob);
            StartAndCall(w);

            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), credentials)).Status, Is.EqualTo(Forbidden));
            Assert.That(w.Voice.Calls, Is.Zero);
        }

        [Test]
        public void Provision_ATokenInTheWrongCase_Is403()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token.ToUpperInvariant())).Status, Is.EqualTo(Forbidden));
            Assert.That(w.Voice.Calls, Is.Zero);
        }

        [Test]
        public void Provision_BeforeAnyCall_NoTokenExists_Is403()
        {
            World w = NewWorld(Alice, Bob);
            Chat(w, Alice, StartBody(CallId, Bob));

            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), "")).Status, Is.EqualTo(Forbidden));
            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), "anything")).Status, Is.EqualTo(Forbidden));
            Assert.That(w.Voice.Calls, Is.Zero);
        }

        [Test]
        public void Provision_ForAnUnknownOrMalformedChannel_Is403()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.That(Provision(w, Alice, MultiagentBody(UUID.Random().ToString(), token)).Status, Is.EqualTo(Forbidden));
            Assert.That(Provision(w, Alice, MultiagentBody("not-a-uuid", token)).Status, Is.EqualTo(Forbidden));
            OSDMap noChannel = MultiagentBody(CallId.ToString(), token);
            noChannel.Remove("channel");
            noChannel["channel_id"] = OSD.FromString(CallId.ToString());
            Assert.That(Provision(w, Alice, noChannel).Status, Is.EqualTo(Forbidden), "channel_id is not the channel");
            Assert.That(w.Voice.Calls, Is.Zero);
        }

        [Test]
        public void Provision_WithNoChannelType_Is403()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);
            OSDMap body = MultiagentBody(CallId.ToString(), token);
            body.Remove("channel_type");

            Assert.That(Provision(w, Alice, body).Status, Is.EqualTo(Forbidden));
            Assert.That(w.Voice.Calls, Is.Zero);
        }

        [Test]
        public void Provision_ARepeatByAParty_IsAdmittedEachTime()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            for (int i = 0; i < 3; i++)
                Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(w.Voice.Calls, Is.EqualTo(3));
        }

        // ---- the agent list --------------------------------------------------------------------------------

        [Test]
        public void TheCallersProvision_AloneSendsNoAgentList_TheCalleesAcceptSendsBothParties()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Provision(w, Alice, MultiagentBody(CallId.ToString(), token));
            Assert.That(w.Queue.AgentLists, Is.Empty, "the call is not active until the callee joins");

            Provision(w, Bob, MultiagentBody(CallId.ToString(), token));

            Assert.That(w.Queue.AgentLists, Has.Count.EqualTo(2));
            foreach (UUID party in new[] { Alice, Bob })
            {
                var sent = w.Queue.AgentLists.Find(l => l.To == party);
                Assert.That(sent.Session, Is.EqualTo(CallId), party.ToString());
                UUID other = party == Alice ? Bob : Alice;
                Assert.That(sent.Updates, Has.Count.EqualTo(2));
                Assert.That(sent.Updates[0].agentID, Is.EqualTo(other), "the other party first");
                Assert.That(sent.Updates[1].agentID, Is.EqualTo(party), "then the recipient's own entry");
                foreach (GroupChatListAgentUpdateData u in sent.Updates)
                {
                    Assert.That(u.canVoice, Is.True, "false would hang the call up");
                    Assert.That(u.enterOrLeave, Is.True);
                }
            }
        }

        [Test]
        public void ACalleeReconnecting_DoesNotSendTheAgentListAgain()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            Provision(w, Bob, MultiagentBody(CallId.ToString(), token));

            Assert.That(w.Queue.AgentLists, Is.Empty);
        }

        // ---- hang-up ---------------------------------------------------------------------------------------

        [Test]
        public void Logout_IsRoutedToTheVoiceService_AndTheOtherPartyKeepsTheCall()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            int before = w.Voice.Calls;

            Assert.That(Provision(w, Alice, LogoutBody("vs-1")).Status, Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(w.Voice.Calls, Is.EqualTo(before + 1), "the teardown reaches the voice service");
            Assert.That(w.Queue.AgentLists, Has.Count.EqualTo(2), "no leave is sent while the other party is still in");
            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK),
                "the call is still there for the remaining party");
            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK),
                "and the party who hung up can join again with the same credentials");
        }

        [Test]
        public void Logout_OfBothParties_EndsTheCall_AndTellsTheRemainingPartyWhoLeft()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            Provision(w, Alice, LogoutBody("vs-1"));
            Assert.That(w.Queue.AgentLists, Is.Empty);
            Provision(w, Bob, LogoutBody("vs-2"));

            Assert.That(w.Queue.AgentLists, Has.Count.EqualTo(1));
            var leave = w.Queue.AgentLists[0];
            Assert.That(leave.To, Is.EqualTo(Alice), "the other party hears about the one who left last");
            Assert.That(leave.Session, Is.EqualTo(CallId));
            Assert.That(leave.Updates, Has.Count.EqualTo(1));
            Assert.That(leave.Updates[0].agentID, Is.EqualTo(Bob));
            Assert.That(leave.Updates[0].enterOrLeave, Is.False);
            Assert.That(leave.Updates[0].canVoice, Is.True);

            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo(Forbidden), "the old credentials are dead");
            Assert.That(Chat(w, Alice, MethodBody("call", CallId)).Status, Is.EqualTo((int)HttpStatusCode.NotFound), "and so is the call");
        }

        [Test]
        public void AfterBothHangUp_ACallGetsNewCredentials_AndRingsAgain()
        {
            World w = NewWorld(Alice, Bob);
            string first = FormCall(w);
            Provision(w, Alice, LogoutBody("vs-1"));
            Provision(w, Bob, LogoutBody("vs-2"));
            w.Queue.Invitations.Clear();

            string second = StartAndCall(w);

            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(w.Queue.Invitations, Has.Count.EqualTo(1));
            Assert.That(w.Queue.Invitations[0].To, Is.EqualTo(Bob));
        }

        [Test]
        public void ALogoutUnderAnotherViewerSession_TouchesNothing()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            Provision(w, Alice, LogoutBody("vs-nobody"));
            Provision(w, Bob, LogoutBody("vs-nobody"));

            Assert.That(w.Queue.AgentLists, Is.Empty);
            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void Logout_BeforeTheCalleeJoined_LeavesTheInvitationForTheCalleeToAnswer()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);
            Provision(w, Alice, MultiagentBody(CallId.ToString(), token));

            Provision(w, Alice, LogoutBody("vs-1"));

            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK),
                "the caller giving up does not withdraw an invitation the callee has");
        }

        // ---- decline ---------------------------------------------------------------------------------------

        [Test]
        public void Decline_ByTheCallee_EndsTheInvitation()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.That(Chat(w, Bob, MethodBody("decline p2p voice", CallId)).Status, Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo(Forbidden));
            Assert.That(Chat(w, Alice, MethodBody("call", CallId)).Status, Is.EqualTo((int)HttpStatusCode.NotFound));
        }

        [Test]
        public void Decline_ByTheCaller_AlsoEndsIt()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.That(Chat(w, Alice, MethodBody("decline p2p voice", CallId)).Status, Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo(Forbidden));
        }

        [Test]
        public void Decline_ByAStranger_IsAnsweredOkButChangesNothing()
        {
            World w = NewWorld(Alice, Bob, Carol);
            string token = StartAndCall(w);

            Assert.That(Chat(w, Carol, MethodBody("decline p2p voice", CallId)).Status, Is.EqualTo((int)HttpStatusCode.OK));

            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void Decline_ForASessionNobodyStarted_IsAnsweredOk()
        {
            World w = NewWorld(Alice, Bob);

            Assert.That(Chat(w, Alice, MethodBody("decline p2p voice", CallId)).Status, Is.EqualTo((int)HttpStatusCode.OK));
        }

        // ---- the client closes -------------------------------------------------------------------------------

        [Test]
        public void ARootClientClosing_IsThatPartyGone_AndTheLastToGoEndsTheCall()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Bob, w.Scene);
            Assert.That(w.Queue.AgentLists, Is.Empty, "Alice is still in the call");
            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Alice, w.Scene);

            Assert.That(w.Queue.AgentLists, Has.Count.EqualTo(1));
            Assert.That(w.Queue.AgentLists[0].To, Is.EqualTo(Bob));
            Assert.That(w.Queue.AgentLists[0].Updates[0].agentID, Is.EqualTo(Alice));
            Assert.That(w.Queue.AgentLists[0].Updates[0].enterOrLeave, Is.False);
            Assert.That(Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo(Forbidden));
        }

        [Test]
        public void AChildAgentClosing_IsNotThePartyLeaving()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Scene.GetScenePresence(Bob).IsChildAgent = true;
            w.Queue.AgentLists.Clear();

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Bob, w.Scene);
            Provision(w, Alice, LogoutBody("vs-1"));

            Assert.That(w.Queue.AgentLists, Is.Empty, "Bob is still counted as in the call, so Alice leaving does not end it");
            w.Scene.GetScenePresence(Bob).IsChildAgent = false;
            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void AClientClosingBeforeTheCalleeJoined_LeavesTheInvitation()
        {
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Alice, w.Scene);

            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void AClientThatClosedAndComesBack_JoinsAgainWithTheSameCredentials()
        {
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Bob, w.Scene);

            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK));
            Provision(w, Alice, LogoutBody("vs-1"));
            Assert.That(Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status, Is.EqualTo((int)HttpStatusCode.OK),
                "Bob is back in, so Alice leaving did not end the call");
        }

        // ---- a child agent --------------------------------------------------------------------------------------

        [Test]
        public void AChildAgentsChatSessionRequest_IsRefused403_BeforeAnythingIsRecorded()
        {
            World w = NewWorld(Alice, Bob);
            w.Scene.GetScenePresence(Alice).IsChildAgent = true;

            Assert.That(Chat(w, Alice, StartBody(CallId, Bob)).Status, Is.EqualTo(Forbidden));
            w.Scene.GetScenePresence(Alice).IsChildAgent = false;
            Assert.That(Chat(w, Alice, MethodBody("call", CallId)).Status, Is.EqualTo((int)HttpStatusCode.NotFound), "nothing was recorded");
        }

        // ---- A2AVoiceEnabled off -------------------------------------------------------------------------------

        [Test]
        public void FeatureOff_StartP2PVoice_IsAnsweredByTheStockHandler()
        {
            World w = NewWorld(false, Alice, Bob);

            (int status, OSDMap body) = Chat(w, Alice, StartBody(CallId, Bob));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(body, Is.Null);
            Assert.That(w.Queue.StartReplies, Has.Count.EqualTo(1));
            Assert.That(w.Queue.StartReplies[0].Session, Is.EqualTo(CallId));
            Assert.That(w.Queue.StartReplies[0].Temp, Is.EqualTo(CallId));
            Assert.That(w.Queue.StartReplies[0].To, Is.EqualTo(Alice));
        }

        [Test]
        public void FeatureOff_Call_Is400_AndNothingIsIssued()
        {
            World w = NewWorld(false, Alice, Bob);
            Chat(w, Alice, StartBody(CallId, Bob));

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.BadRequest));
            Assert.That(body, Is.Null);
            Assert.That(w.Queue.Invitations, Is.Empty);
        }

        [TestCase("decline p2p voice")]
        [TestCase("decline invitation")]
        [TestCase("start conference")]
        [TestCase("fetch history")]
        public void FeatureOff_TheStubbedMethods_AreAnsweredOk(string method)
        {
            World w = NewWorld(false, Alice, Bob);

            Assert.That(Chat(w, Alice, MethodBody(method, CallId)).Status, Is.EqualTo((int)HttpStatusCode.OK));
        }

        [Test]
        public void FeatureOff_AMultiagentProvision_GoesToTheVoiceServiceUnexamined()
        {
            World w = NewWorld(false, Alice, Bob);

            (int status, OSDMap body) = Provision(w, Alice, MultiagentBody(CallId.ToString(), "anything"));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(w.Voice.Calls, Is.EqualTo(1));
            Assert.That(body["viewer_session"].AsString(), Is.EqualTo("vs-1"));
        }

        [Test]
        public void FeatureOff_ALogout_GoesToTheVoiceService()
        {
            World w = NewWorld(false, Alice, Bob);

            Assert.That(Provision(w, Alice, LogoutBody("vs-9")).Status, Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(w.Voice.Calls, Is.EqualTo(1));
        }

        [Test]
        public void FeatureOff_AClientClosing_IsHarmless()
        {
            World w = NewWorld(false, Alice, Bob);

            Assert.DoesNotThrow(() => InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Alice, w.Scene));
        }
    }
}
