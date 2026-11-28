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
using Xunit;
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
            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Alice, StartBody(CallId, Bob)).Status);
            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));
            Assert.Equal((int)HttpStatusCode.OK, status);
            return ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString();
        }

        /// <summary>Alice and Bob have both joined: Alice's provision is vs-1, Bob's is vs-2.</summary>
        private static string FormCall(World w)
        {
            string token = StartAndCall(w);
            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status);
            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status);
            return token;
        }

        private static int Forbidden => (int)HttpStatusCode.Forbidden;

        // ---- start p2p voice ------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Start_AnswersOk_AndSendsTheStartReplyToTheCaller()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);

            (int status, OSDMap body) = Chat(w, Alice, StartBody(CallId, Bob));

            Assert.Equal((int)HttpStatusCode.OK, status);
            {
                var assertedValue = body;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the answer to the POST has no body; the viewer waits for the event");
            }
            Assert.Single(w.Queue.StartReplies);
            var reply = w.Queue.StartReplies[0];
            {
                var assertedValue = reply.Session;
                TestAssert.WithMessage(() => Assert.Equal(CallId, assertedValue), "the session id is the XOR of the two agents");
            }
            {
                var assertedValue = reply.Temp;
                TestAssert.WithMessage(() => Assert.Equal(CallId, assertedValue), "the viewer's own id, so the re-key is a no-op");
            }
            Assert.Equal(Alice, reply.To);
            Assert.Equal(2, reply.Type);
            Assert.False(reply.VoiceEnabled);
            Assert.True(reply.Moderated);
            Assert.True(reply.Success);
            Assert.Empty(reply.Error);
        }

        [Fact(Timeout = 60000)]
        public void Start_NeverRingsTheCallee()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);

            Chat(w, Alice, StartBody(CallId, Bob));
            Chat(w, Alice, StartBody(CallId, Bob));

            {
                var assertedValue = w.Queue.Invitations;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the viewer sends this on every IM window it opens");
            }
        }

        [Fact(Timeout = 60000)]
        public void Start_RepeatedFromEitherSide_AnswersEachTime_AndKeepsOneCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            {
                var assertedValue = Chat(w, Bob, StartBody(CallId, Alice)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "the callee opens the IM window");
            }
            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Alice, StartBody(CallId, Bob)).Status);

            (int status, OSDMap body) = Chat(w, Bob, MethodBody("call", CallId));
            Assert.Equal((int)HttpStatusCode.OK, status);
            {
                var assertedValue = ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "a repeated start keeps the credentials already handed out");
            }
        }

        [Fact(Timeout = 60000)]
        public void Start_WithoutParams_Is400()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            OSDMap body = StartBody(CallId, Bob);
            body.Remove("params");

            Assert.Equal((int)HttpStatusCode.BadRequest, Chat(w, Alice, body).Status);
            Assert.Empty(w.Queue.StartReplies);
        }

        [Fact(Timeout = 60000)]
        public void Start_WithTheCallerAsParams_Is400()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);

            Assert.Equal((int)HttpStatusCode.BadRequest, Chat(w, Alice, StartBody(CallId, Alice)).Status);
            Assert.Empty(w.Queue.StartReplies);
        }

        // ---- call: the credentials and the invitation --------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Call_ByTheCaller_ReturnsTheCredentialsInTheBody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            Chat(w, Alice, StartBody(CallId, Bob));

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.Equal((int)HttpStatusCode.OK, status);
            OSDMap creds = (OSDMap)body["voice_credentials"];
            Assert.Equal("webrtc", creds["voice_server_type"].AsString());
            {
                var assertedValue = creds["channel_uri"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(CallId.ToString(), assertedValue), "the channel is the session id as text");
            }
            {
                var assertedValue = creds["channel_credentials"].AsString();
                TestAssert.WithMessage(() => Assert.Matches("^[0-9a-f]{64}$", assertedValue), "a 32-byte token in lowercase hex");
            }
            Assert.False(creds.ContainsKey("sip_uri"));
        }

        [Fact(Timeout = 60000)]
        public void Call_RingsTheCalleeOnce_WithTheSameCredentials()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);

            string token = StartAndCall(w);

            Assert.Single(w.Queue.Invitations);
            (UUID to, OSDMap invite) = w.Queue.Invitations[0];
            {
                var assertedValue = to;
                TestAssert.WithMessage(() => Assert.Equal(Bob, assertedValue), "delivered to the callee, not the caller");
            }
            Assert.Equal(CallId, invite["session_id"].AsUUID());
            Assert.Equal(Alice, invite["from_id"].AsUUID());
            Assert.False(invite.ContainsKey("instantmessage"), "that key would send the viewer down its IM branch");
            OSDMap voice = (OSDMap)invite["voice"];
            Assert.Equal(2, voice["invitation_type"].AsInteger());
            Assert.Equal("webrtc", voice["voice_server_type"].AsString());
            Assert.Equal(CallId.ToString(), voice["channel_uri"].AsString());
            {
                var assertedValue = voice["channel_credentials"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "the callee is handed the caller's token");
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_RepeatedByTheCaller_ReturnsTheSameCredentials_AndDoesNotRingAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            for (int i = 0; i < 3; i++)
            {
                (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));
                Assert.Equal((int)HttpStatusCode.OK, status);
                Assert.Equal(token, ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString());
            }

            {
                var assertedValue = w.Queue.Invitations;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "one ring per invitation");
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_ByTheCallee_ReturnsTheSameCredentials_AndRingsNobody()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);
            w.Queue.Invitations.Clear();

            (int status, OSDMap body) = Chat(w, Bob, MethodBody("call", CallId));

            Assert.Equal((int)HttpStatusCode.OK, status);
            Assert.Equal(token, ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString());
            {
                var assertedValue = w.Queue.Invitations;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "a callee's call must not ring the caller back");
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_WhileTheCallIsActive_ReturnsCredentials_AndDoesNotRing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.Invitations.Clear();

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.Equal((int)HttpStatusCode.OK, status);
            Assert.Equal(token, ((OSDMap)body["voice_credentials"])["channel_credentials"].AsString());
            Assert.Empty(w.Queue.Invitations);
        }

        [Fact(Timeout = 60000)]
        public void Call_WhenTheCalleeCouldNotBeReached_RingsOnTheNextCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice);   // Bob is not on this region server
            Chat(w, Alice, StartBody(CallId, Bob));
            {
                var assertedValue = Chat(w, Alice, MethodBody("call", CallId)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "the caller still gets credentials");
            }
            Assert.Empty(w.Queue.Invitations);

            SceneHelpers.AddScenePresence(w.Scene, Bob);
            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Alice, MethodBody("call", CallId)).Status);

            {
                var assertedValue = w.Queue.Invitations;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "a failed delivery does not use up the ring");
            }
            Assert.Equal(Bob, w.Queue.Invitations[0].To);
        }

        [Fact(Timeout = 60000)]
        public void Call_ForASessionNobodyStarted_Is404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.Equal((int)HttpStatusCode.NotFound, status);
            Assert.Null(body);
            Assert.Empty(w.Queue.Invitations);
        }

        [Fact(Timeout = 60000)]
        public void Call_ByAStrangerOnALiveSession_Is403_AndLearnsNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob, Carol);
            Chat(w, Alice, StartBody(CallId, Bob));

            (int status, OSDMap body) = Chat(w, Carol, MethodBody("call", CallId));

            Assert.Equal(Forbidden, status);
            {
                var assertedValue = body;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "no credentials");
            }
            Assert.Empty(w.Queue.Invitations);
        }

        [Fact(Timeout = 60000)]
        public void Call_ForTheSessionIdOfAnotherPair_Is404()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob, Carol);
            Chat(w, Alice, StartBody(CallId, Bob));
            UUID other = new UUID(Alice.ulonga ^ Carol.ulonga, Alice.ulongb ^ Carol.ulongb);

            Assert.Equal((int)HttpStatusCode.NotFound, Chat(w, Alice, MethodBody("call", other)).Status);
        }

        // ---- the multiagent provision: who may join --------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Provision_TheCallerWithTheToken_IsAdmitted_AndReachesTheVoiceService()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            (int status, OSDMap body) = Provision(w, Alice, MultiagentBody(CallId.ToString(), token));

            Assert.Equal((int)HttpStatusCode.OK, status);
            Assert.Equal(1, w.Voice.Calls);
            {
                var assertedValue = w.Voice.Requests[0]["channel"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(CallId.ToString(), assertedValue), "the body goes on unchanged");
            }
            Assert.Equal("vs-1", body["viewer_session"].AsString());
        }

        [Fact(Timeout = 60000)]
        public void Provision_TheCalleeWithTheToken_IsAdmitted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status);
            Assert.Equal(1, w.Voice.Calls);
        }

        [Fact(Timeout = 60000)]
        public void Provision_AStrangerWithTheRightToken_Is403_AndNeverReachesTheVoiceService()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob, Carol);
            string token = StartAndCall(w);

            Assert.Equal(Forbidden, Provision(w, Carol, MultiagentBody(CallId.ToString(), token)).Status);
            Assert.Equal(0, w.Voice.Calls);
        }

        [InlineData("not-the-token")]
        [InlineData("")]
        [Theory(Timeout = 60000)]
        public void Provision_AWrongOrEmptyToken_Is403(string credentials)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            StartAndCall(w);

            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody(CallId.ToString(), credentials)).Status);
            Assert.Equal(0, w.Voice.Calls);
        }

        [Fact(Timeout = 60000)]
        public void Provision_ATokenInTheWrongCase_Is403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody(CallId.ToString(), token.ToUpperInvariant())).Status);
            Assert.Equal(0, w.Voice.Calls);
        }

        [Fact(Timeout = 60000)]
        public void Provision_BeforeAnyCall_NoTokenExists_Is403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            Chat(w, Alice, StartBody(CallId, Bob));

            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody(CallId.ToString(), "")).Status);
            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody(CallId.ToString(), "anything")).Status);
            Assert.Equal(0, w.Voice.Calls);
        }

        [Fact(Timeout = 60000)]
        public void Provision_ForAnUnknownOrMalformedChannel_Is403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody(UUID.Random().ToString(), token)).Status);
            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody("not-a-uuid", token)).Status);
            OSDMap noChannel = MultiagentBody(CallId.ToString(), token);
            noChannel.Remove("channel");
            noChannel["channel_id"] = OSD.FromString(CallId.ToString());
            {
                var assertedValue = Provision(w, Alice, noChannel).Status;
                TestAssert.WithMessage(() => Assert.Equal(Forbidden, assertedValue), "channel_id is not the channel");
            }
            Assert.Equal(0, w.Voice.Calls);
        }

        [Fact(Timeout = 60000)]
        public void Provision_WithNoChannelType_Is403()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);
            OSDMap body = MultiagentBody(CallId.ToString(), token);
            body.Remove("channel_type");

            Assert.Equal(Forbidden, Provision(w, Alice, body).Status);
            Assert.Equal(0, w.Voice.Calls);
        }

        [Fact(Timeout = 60000)]
        public void Provision_ARepeatByAParty_IsAdmittedEachTime()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            for (int i = 0; i < 3; i++)
                Assert.Equal((int)HttpStatusCode.OK, Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status);

            Assert.Equal(3, w.Voice.Calls);
        }

        // ---- the agent list --------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheCallersProvision_AloneSendsNoAgentList_TheCalleesAcceptSendsBothParties()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Provision(w, Alice, MultiagentBody(CallId.ToString(), token));
            {
                var assertedValue = w.Queue.AgentLists;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the call is not active until the callee joins");
            }

            Provision(w, Bob, MultiagentBody(CallId.ToString(), token));

            Assert.Equal(2, w.Queue.AgentLists.Count());
            foreach (UUID party in new[] { Alice, Bob })
            {
                var sent = w.Queue.AgentLists.Find(l => l.To == party);
                {
                    var assertedValue = sent.Session;
                    TestAssert.WithMessage(() => Assert.Equal(CallId, assertedValue), party.ToString());
                }
                UUID other = party == Alice ? Bob : Alice;
                Assert.Equal(2, sent.Updates.Count());
                {
                    var assertedValue = sent.Updates[0].agentID;
                    TestAssert.WithMessage(() => Assert.Equal(other, assertedValue), "the other party first");
                }
                {
                    var assertedValue = sent.Updates[1].agentID;
                    TestAssert.WithMessage(() => Assert.Equal(party, assertedValue), "then the recipient's own entry");
                }
                foreach (GroupChatListAgentUpdateData u in sent.Updates)
                {
                    Assert.True(u.canVoice, "false would hang the call up");
                    Assert.True(u.enterOrLeave);
                }
            }
        }

        [Fact(Timeout = 60000)]
        public void ACalleeReconnecting_DoesNotSendTheAgentListAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            Provision(w, Bob, MultiagentBody(CallId.ToString(), token));

            Assert.Empty(w.Queue.AgentLists);
        }

        // ---- hang-up ---------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Logout_IsRoutedToTheVoiceService_AndTheOtherPartyKeepsTheCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            int before = w.Voice.Calls;

            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Alice, LogoutBody("vs-1")).Status);

            {
                var assertedValue = w.Voice.Calls;
                TestAssert.WithMessage(() => Assert.Equal(before + 1, assertedValue), "the teardown reaches the voice service");
            }
            {
                var assertedValue = w.Queue.AgentLists;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue.Count()), "no leave is sent while the other party is still in");
            }
            {
                var assertedValue = Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "the call is still there for the remaining party");
            }
            {
                var assertedValue = Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "and the party who hung up can join again with the same credentials");
            }
        }

        [Fact(Timeout = 60000)]
        public void Logout_OfBothParties_EndsTheCall_AndTellsTheRemainingPartyWhoLeft()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            Provision(w, Alice, LogoutBody("vs-1"));
            Assert.Empty(w.Queue.AgentLists);
            Provision(w, Bob, LogoutBody("vs-2"));

            Assert.Single(w.Queue.AgentLists);
            var leave = w.Queue.AgentLists[0];
            {
                var assertedValue = leave.To;
                TestAssert.WithMessage(() => Assert.Equal(Alice, assertedValue), "the other party hears about the one who left last");
            }
            Assert.Equal(CallId, leave.Session);
            Assert.Single(leave.Updates);
            Assert.Equal(Bob, leave.Updates[0].agentID);
            Assert.False(leave.Updates[0].enterOrLeave);
            Assert.True(leave.Updates[0].canVoice);

            {
                var assertedValue = Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status;
                TestAssert.WithMessage(() => Assert.Equal(Forbidden, assertedValue), "the old credentials are dead");
            }
            {
                var assertedValue = Chat(w, Alice, MethodBody("call", CallId)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.NotFound, assertedValue), "and so is the call");
            }
        }

        [Fact(Timeout = 60000)]
        public void AfterBothHangUp_ACallGetsNewCredentials_AndRingsAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string first = FormCall(w);
            Provision(w, Alice, LogoutBody("vs-1"));
            Provision(w, Bob, LogoutBody("vs-2"));
            w.Queue.Invitations.Clear();

            string second = StartAndCall(w);

            Assert.NotEqual(first, second);
            Assert.Single(w.Queue.Invitations);
            Assert.Equal(Bob, w.Queue.Invitations[0].To);
        }

        [Fact(Timeout = 60000)]
        public void ALogoutUnderAnotherViewerSession_TouchesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            Provision(w, Alice, LogoutBody("vs-nobody"));
            Provision(w, Bob, LogoutBody("vs-nobody"));

            Assert.Empty(w.Queue.AgentLists);
            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status);
        }

        [Fact(Timeout = 60000)]
        public void Logout_BeforeTheCalleeJoined_LeavesTheInvitationForTheCalleeToAnswer()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);
            Provision(w, Alice, MultiagentBody(CallId.ToString(), token));

            Provision(w, Alice, LogoutBody("vs-1"));

            {
                var assertedValue = Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "the caller giving up does not withdraw an invitation the callee has");
            }
        }

        // ---- decline ---------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Decline_ByTheCallee_EndsTheInvitation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Bob, MethodBody("decline p2p voice", CallId)).Status);

            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status);
            Assert.Equal((int)HttpStatusCode.NotFound, Chat(w, Alice, MethodBody("call", CallId)).Status);
        }

        [Fact(Timeout = 60000)]
        public void Decline_ByTheCaller_AlsoEndsIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Alice, MethodBody("decline p2p voice", CallId)).Status);

            Assert.Equal(Forbidden, Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status);
        }

        [Fact(Timeout = 60000)]
        public void Decline_ByAStranger_IsAnsweredOkButChangesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob, Carol);
            string token = StartAndCall(w);

            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Carol, MethodBody("decline p2p voice", CallId)).Status);

            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status);
        }

        [Fact(Timeout = 60000)]
        public void Decline_ForASessionNobodyStarted_IsAnsweredOk()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);

            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Alice, MethodBody("decline p2p voice", CallId)).Status);
        }

        // ---- the client closes -------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ARootClientClosing_IsThatPartyGone_AndTheLastToGoEndsTheCall()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Queue.AgentLists.Clear();

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Bob, w.Scene);
            {
                var assertedValue = w.Queue.AgentLists;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "Alice is still in the call");
            }
            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Alice, w.Scene);

            Assert.Single(w.Queue.AgentLists);
            Assert.Equal(Bob, w.Queue.AgentLists[0].To);
            Assert.Equal(Alice, w.Queue.AgentLists[0].Updates[0].agentID);
            Assert.False(w.Queue.AgentLists[0].Updates[0].enterOrLeave);
            Assert.Equal(Forbidden, Provision(w, Alice, MultiagentBody(CallId.ToString(), token)).Status);
        }

        [Fact(Timeout = 60000)]
        public void AChildAgentClosing_IsNotThePartyLeaving()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);
            w.Scene.GetScenePresence(Bob).IsChildAgent = true;
            w.Queue.AgentLists.Clear();

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Bob, w.Scene);
            Provision(w, Alice, LogoutBody("vs-1"));

            {
                var assertedValue = w.Queue.AgentLists;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "Bob is still counted as in the call, so Alice leaving does not end it");
            }
            w.Scene.GetScenePresence(Bob).IsChildAgent = false;
            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status);
        }

        [Fact(Timeout = 60000)]
        public void AClientClosingBeforeTheCalleeJoined_LeavesTheInvitation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = StartAndCall(w);

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Alice, w.Scene);

            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status);
        }

        [Fact(Timeout = 60000)]
        public void AClientThatClosedAndComesBack_JoinsAgainWithTheSameCredentials()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            string token = FormCall(w);

            InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Bob, w.Scene);

            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status);
            Provision(w, Alice, LogoutBody("vs-1"));
            {
                var assertedValue = Provision(w, Bob, MultiagentBody(CallId.ToString(), token)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.OK, assertedValue), "Bob is back in, so Alice leaving did not end the call");
            }
        }

        // ---- a child agent --------------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AChildAgentsChatSessionRequest_IsRefused403_BeforeAnythingIsRecorded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(Alice, Bob);
            w.Scene.GetScenePresence(Alice).IsChildAgent = true;

            Assert.Equal(Forbidden, Chat(w, Alice, StartBody(CallId, Bob)).Status);
            w.Scene.GetScenePresence(Alice).IsChildAgent = false;
            {
                var assertedValue = Chat(w, Alice, MethodBody("call", CallId)).Status;
                TestAssert.WithMessage(() => Assert.Equal((int)HttpStatusCode.NotFound, assertedValue), "nothing was recorded");
            }
        }

        // ---- A2AVoiceEnabled off -------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void FeatureOff_StartP2PVoice_IsAnsweredByTheStockHandler()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(false, Alice, Bob);

            (int status, OSDMap body) = Chat(w, Alice, StartBody(CallId, Bob));

            Assert.Equal((int)HttpStatusCode.OK, status);
            Assert.Null(body);
            Assert.Single(w.Queue.StartReplies);
            Assert.Equal(CallId, w.Queue.StartReplies[0].Session);
            Assert.Equal(CallId, w.Queue.StartReplies[0].Temp);
            Assert.Equal(Alice, w.Queue.StartReplies[0].To);
        }

        [Fact(Timeout = 60000)]
        public void FeatureOff_Call_Is400_AndNothingIsIssued()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(false, Alice, Bob);
            Chat(w, Alice, StartBody(CallId, Bob));

            (int status, OSDMap body) = Chat(w, Alice, MethodBody("call", CallId));

            Assert.Equal((int)HttpStatusCode.BadRequest, status);
            Assert.Null(body);
            Assert.Empty(w.Queue.Invitations);
        }

        [InlineData("decline p2p voice")]
        [InlineData("decline invitation")]
        [InlineData("start conference")]
        [InlineData("fetch history")]
        [Theory(Timeout = 60000)]
        public void FeatureOff_TheStubbedMethods_AreAnsweredOk(string method)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(false, Alice, Bob);

            Assert.Equal((int)HttpStatusCode.OK, Chat(w, Alice, MethodBody(method, CallId)).Status);
        }

        [Fact(Timeout = 60000)]
        public void FeatureOff_AMultiagentProvision_GoesToTheVoiceServiceUnexamined()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(false, Alice, Bob);

            (int status, OSDMap body) = Provision(w, Alice, MultiagentBody(CallId.ToString(), "anything"));

            Assert.Equal((int)HttpStatusCode.OK, status);
            Assert.Equal(1, w.Voice.Calls);
            Assert.Equal("vs-1", body["viewer_session"].AsString());
        }

        [Fact(Timeout = 60000)]
        public void FeatureOff_ALogout_GoesToTheVoiceService()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(false, Alice, Bob);

            Assert.Equal((int)HttpStatusCode.OK, Provision(w, Alice, LogoutBody("vs-9")).Status);
            Assert.Equal(1, w.Voice.Calls);
        }

        [Fact(Timeout = 60000)]
        public void FeatureOff_AClientClosing_IsHarmless()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            World w = NewWorld(false, Alice, Bob);

            {
                var assertedValue = () => InvokePrivate(w.Module, "OnVoiceClientClosed", w.Scene, Alice, w.Scene);
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
        }
    }
}
