/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The END-TO-END handler test. Without it a
 * NullReferenceException could escape the handler on a group call.
 *
 * Everything else in GroupVoiceTests exercises the DECISION functions. This drives the HANDLER --
 * WebRtcVoiceRegionModule.ProvisionVoiceAccountRequest, the real method the cap invokes -- with a
 * real Scene, a real request and response, and a stub voice service that returns the success map a
 * real provision returns. That is the seam where the bug lived: admission was correct, the service
 * was correct, and the bookkeeping AFTER them dereferenced a null A2ASession because a group
 * admission was filed as ProvisionKind.Multiagent.
 *
 * With ProvisionKind.Group filed as Multiagent again, this test throws the NullReferenceException.
 *
 * The group policy is injected rather than resolved through IGroupsModule: a SceneHelpers scene has
 * no groups module, and this is a test of the handler, not of the groups plumbing.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class GroupProvisionHandlerTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Group = new UUID("99999999-9999-9999-9999-999999999999");
        private const string Grid = "example-grid";

        /// <summary>The provision body a viewer sends, as LLSD XML on the request stream.</summary>
        private sealed class ProvisionRequest : IOSHttpRequest
        {
            public ProvisionRequest(OSDMap body)
            {
                InputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(body)));
            }
            public string HttpMethod => "POST";
            public Uri Url => new Uri("http://sim.test/provision");
            public string RawUrl => "/provision";
            public string UriPath => "/provision";
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
            public System.Net.IPEndPoint RemoteIPEndPoint => new(System.Net.IPAddress.Loopback, 1);
            public System.Net.IPEndPoint LocalIPEndPoint => new(System.Net.IPAddress.Loopback, 2);
            public string UserAgent => "test";
            public double ArrivalTS => 0;
        }

        /// <summary>What a real provision answers with: the success map carrying viewer_session and room.</summary>
        private sealed class StubVoiceService : IWebRtcVoiceService
        {
            public int Calls;
            public OSDMap LastRequest;
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene)
            {
                Calls++;
                LastRequest = pRequest;
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

        /// <summary>m_Enabled is static on the module; the group fields are per-instance.</summary>
        private static void SetPrivate(object target, string field, object value)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? t.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(t.Name, field);
            f.SetValue(f.IsStatic ? null : target, value);
        }

        private static (WebRtcVoiceRegionModule, Scene, StubVoiceService, NonSpatialVoiceSessionEngine, GroupVoicePolicy)
            Harness(bool aliceIsMember = true, ulong powers = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice)
        {
            Scene scene = new SceneHelpers().SetupScene();
            StubVoiceService svc = new StubVoiceService();
            scene.RegisterModuleInterface<IWebRtcVoiceService>(svc);

            GroupVoicePolicy policy = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = NonSpatialCaps.DefaultConferenceCap,
                IsMember = (a, g) => aliceIsMember && a == Alice && g == Group,
                Powers = (a, g) => a == Alice && g == Group ? powers : 0UL,
            };
            NonSpatialVoiceSessionEngine engine = new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), policy.ToAdmissionOrNull() },
                Grid);

            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_groupVoice", policy);
            SetPrivate(mod, "m_nonSpatial", engine);
            return (mod, scene, svc, engine, policy);
        }

        private static (int Status, OSDMap Body) Provision(WebRtcVoiceRegionModule mod, Scene scene, UUID agent, OSDMap body)
        {
            ProvisionRequest req = new ProvisionRequest(body);
            TestOSHttpResponse resp = new TestOSHttpResponse();
            mod.ProvisionVoiceAccountRequest(req, resp, agent, scene);
            OSDMap parsed = null;
            if (resp.RawBuffer is not null && resp.RawBuffer.Length > 0)
            {
                try { parsed = OSDParser.DeserializeLLSDXml(resp.RawBuffer) as OSDMap; } catch { }
            }
            return (resp.StatusCode, parsed);
        }

        private static OSDMap GroupProvisionBody(string room, string token)
            => new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["channel"] = OSD.FromString(room),
                ["credentials"] = OSD.FromString(token),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };

        /// <summary>Open the group room through the real call handler, returning (room, token).</summary>
        private static (string, string) OpenRoom(NonSpatialVoiceSessionEngine engine, GroupVoicePolicy policy, UUID region)
        {
            OSDMap call = new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(Group) };
            Assert.That(GroupVoiceChatSession.TryHandleCall(call, Alice, policy, engine, region, out ChatSessionOutcome o), Is.True);
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            return (creds["channel_uri"].AsString(), creds["channel_credentials"].AsString());
        }

        // ---- the regression itself ---------------------------------------------------------------

        [Test]
        public void AGroupProvisionRunsTheWholeHandlerWithoutThrowing()
        {
            // This is the shape that threw for a group provision.
            (WebRtcVoiceRegionModule mod, Scene scene, StubVoiceService svc, NonSpatialVoiceSessionEngine engine, GroupVoicePolicy policy) = Harness();
            (string room, string token) = OpenRoom(engine, policy, scene.RegionInfo.RegionID);

            (int status, OSDMap body) = Provision(mod, scene, Alice, GroupProvisionBody(room, token));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK), "the handler must complete, not throw");
            Assert.That(svc.Calls, Is.EqualTo(1), "and the body must have reached the voice service unchanged");
            Assert.That(svc.LastRequest["channel"].AsString(), Is.EqualTo(room));
            Assert.That(body, Is.Not.Null);
            Assert.That(body["viewer_session"].AsString(), Is.EqualTo("vs-1"));
        }

        [Test]
        public void TheGroupSeatIsTaggedWithTheViewerSession_SoTeardownCanFindIt()
        {
            (WebRtcVoiceRegionModule mod, Scene scene, _, NonSpatialVoiceSessionEngine engine, GroupVoicePolicy policy) = Harness();
            (string room, string token) = OpenRoom(engine, policy, scene.RegionInfo.RegionID);

            Provision(mod, scene, Alice, GroupProvisionBody(room, token));

            NonSpatialMember m = engine.Store.Get(Group).Find(Alice);
            Assert.That(m.ViewerSession, Is.EqualTo("vs-1"));
            Assert.That(m.State, Is.EqualTo(MemberState.Present), "an admitted provision means the agent is in the room");
        }

        [Test]
        public void ALogoutReleasesTheGroupSeat_ThroughTheRealHandler()
        {
            (WebRtcVoiceRegionModule mod, Scene scene, _, NonSpatialVoiceSessionEngine engine, GroupVoicePolicy policy) = Harness();
            (string room, string token) = OpenRoom(engine, policy, scene.RegionInfo.RegionID);
            Provision(mod, scene, Alice, GroupProvisionBody(room, token));
            Assert.That(engine.Store.Get(Group).SeatsHeld, Is.EqualTo(1));

            // The teardown body the viewer actually sends: logout + viewer_session, NO channel_type.
            OSDMap logout = new OSDMap
            {
                ["logout"] = OSD.FromBoolean(true),
                ["viewer_session"] = OSD.FromString("vs-1"),
                ["voice_server_type"] = OSD.FromString("webrtc"),
            };
            Provision(mod, scene, Alice, logout);

            Assert.That(engine.Store.Get(Group).SeatsHeld, Is.EqualTo(0), "the hang-up frees the seat");
            Assert.That(engine.Store.Get(Group).Find(Alice).Departure, Is.EqualTo(DepartureReason.VoiceTeardown));
        }

        [Test]
        public void RepeatedProvisionsThroughTheHandler_HoldOneSeat()
        {
            (WebRtcVoiceRegionModule mod, Scene scene, StubVoiceService svc, NonSpatialVoiceSessionEngine engine, GroupVoicePolicy policy) = Harness();
            (string room, string token) = OpenRoom(engine, policy, scene.RegionInfo.RegionID);

            for (int i = 0; i < 20; i++)
                Assert.That(Provision(mod, scene, Alice, GroupProvisionBody(room, token)).Status,
                            Is.EqualTo((int)HttpStatusCode.OK), "retry " + i);

            Assert.That(engine.Store.Get(Group).SeatsHeld, Is.EqualTo(1));
            Assert.That(svc.Calls, Is.EqualTo(20));
        }

        [Test]
        public void ANonMemberIsRefused403ByTheHandler_AndNeverReachesTheVoiceService()
        {
            (WebRtcVoiceRegionModule mod, Scene scene, StubVoiceService svc, NonSpatialVoiceSessionEngine engine, GroupVoicePolicy policy) = Harness();
            (string room, string token) = OpenRoom(engine, policy, scene.RegionInfo.RegionID);
            UUID stranger = UUID.Random();

            (int status, _) = Provision(mod, scene, stranger, GroupProvisionBody(room, token));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.EqualTo(0), "a refusal must never reach the mixer");
        }

        [Test]
        public void ABadTokenIsRefused403ByTheHandler()
        {
            (WebRtcVoiceRegionModule mod, Scene scene, StubVoiceService svc, NonSpatialVoiceSessionEngine engine, GroupVoicePolicy policy) = Harness();
            (string room, _) = OpenRoom(engine, policy, scene.RegionInfo.RegionID);

            (int status, _) = Provision(mod, scene, Alice, GroupProvisionBody(room, new string('a', 64)));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.EqualTo(0));
        }

        [Test]
        public void AnUnknownRoomKeyIsRefused_AndTheA2AArmNeverSeesIt()
        {
            (WebRtcVoiceRegionModule mod, Scene scene, StubVoiceService svc, _, _) = Harness();
            string stranger = NonSpatialRoomKey.Derive(Grid, NonSpatialSessionType.Group, UUID.Random());

            (int status, _) = Provision(mod, scene, Alice, GroupProvisionBody(stranger, "x"));

            Assert.That(status, Is.EqualTo((int)HttpStatusCode.Forbidden));
            Assert.That(svc.Calls, Is.EqualTo(0));
        }
    }
}
