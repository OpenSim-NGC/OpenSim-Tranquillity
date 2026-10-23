/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A group or conference join refused because the grid session service cannot be reached is answered 503, as an
 * avatar-to-avatar start or call is, on every path by which it can reach a viewer: the chat session request
 * ("call", "accept invitation", "start conference") and the voice provision for a group or conference room.
 * These tests go through the module's own handlers with HTTP-level requests, with a session service that stops
 * answering. A refusal that is not about the service (a missing group power, a wrong token) keeps its 403.
 */
using System.Collections.Generic;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CrossProcessHarness;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class StoreUnavailableJoinTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Group = new UUID("99999999-9999-9999-9999-999999999999");
        private static readonly UUID Region = new UUID("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly UUID TempId = new UUID("77777777-7777-7777-7777-777777777777");

        /// <summary>A voice service that admits everything it is asked; a refused provision never reaches it.</summary>
        private sealed class StubVoiceService : IWebRtcVoiceService
        {
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene)
                => new OSDMap { ["viewer_session"] = OSD.FromString("vs-1"), ["room"] = OSD.FromInteger(1) };
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene)
                => ProvisionVoiceAccountRequest(pRequest, pUserID, pScene);
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID) => null;
        }

        private sealed class Rig
        {
            public WebRtcVoiceRegionModule Module;
            public Process Process;
            public GridStoreResilienceTests.FlakyService Service;
            public GridNonSpatialSessions Store;
            public GroupVoicePolicy Policy;
        }

        private static Rig NewRig(ulong powers = GroupVoicePolicy.PowerJoinSession | GroupVoicePolicy.PowerVoice)
        {
            Rig r = new Rig();
            r.Policy = new GroupVoicePolicy
            {
                Enabled = true,
                RequireVoicePower = true,
                Cap = NonSpatialCaps.DefaultConferenceCap,
                IsMember = (a, g) => (a == Alice || a == Bob) && g == Group,
                Powers = (a, g) => (a == Alice || a == Bob) && g == Group ? powers : 0UL,
            };
            NonSpatialIdentityTests.FakeClock clock = new NonSpatialIdentityTests.FakeClock();
            r.Service = new GridStoreResilienceTests.FlakyService(GridStoreResilienceTests.NewReal(clock));
            r.Store = new GridNonSpatialSessions(r.Service,
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(), r.Policy.ToAdmissionOrNull() });
            r.Process = new Process("Region One", 1000, 1000);
            r.Process.Scene.RegisterModuleInterface<IWebRtcVoiceService>(new StubVoiceService());
            r.Module = new WebRtcVoiceRegionModule();
            SetPrivate(r.Module, "m_Enabled", true);
            SetPrivate(r.Module, "m_groupVoice", r.Policy);
            SetPrivate(r.Module, "m_adhocVoiceEnabled", true);
            SetPrivate(r.Module, "m_nonSpatial", r.Store);
            ((List<Scene>)GetPrivate(r.Module, "m_scenes")).Add(r.Process.Scene);
            r.Process.AddRoot(Alice);
            r.Process.AddRoot(Bob);
            return r;
        }

        private static int Chat(Rig r, UUID agent, string method, UUID sessionId, UUID? other = null)
        {
            OSDMap body = new OSDMap
            {
                ["method"] = OSD.FromString(method),
                ["session-id"] = OSD.FromUUID(sessionId),
            };
            if (other.HasValue) body["params"] = OSD.FromUUID(other.Value);
            TestOSHttpResponse resp = new TestOSHttpResponse();
            r.Module.ChatSessionRequest(new LlsdPost(body), resp, agent, r.Process.Scene);
            return resp.StatusCode;
        }

        private static int Provision(Rig r, UUID agent, string room, string token)
        {
            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString("multiagent"),
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["channel"] = OSD.FromString(room),
                ["credentials"] = OSD.FromString(token),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };
            TestOSHttpResponse resp = new TestOSHttpResponse();
            r.Module.ProvisionVoiceAccountRequest(new LlsdPost(body), resp, agent, r.Process.Scene);
            return resp.StatusCode;
        }

        /// <summary>Alice opens the group call while the service answers; returns the room and token she was given.</summary>
        private static (string Room, string Token) OpenGroup(Rig r)
        {
            OSDMap call = new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(Group) };
            Assert.That(GroupVoiceChatSession.TryHandleCall(call, Alice, r.Policy, r.Store, Region, out ChatSessionOutcome o), Is.True);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            return (creds["channel_uri"].AsString(), creds["channel_credentials"].AsString());
        }

        /// <summary>Alice starts a conference with Bob invited while the service answers; returns its id, room and token.</summary>
        private static (UUID Id, string Room, string Token) OpenConference(Rig r)
        {
            SessionOutcome start = r.Store.StartAdhoc(Alice, TempId, Region, new List<UUID> { Bob });
            Assert.That(start.Ok, Is.True);
            UUID id = start.Session.SessionId;
            OSDMap call = new OSDMap { ["method"] = OSD.FromString("call"), ["session-id"] = OSD.FromUUID(id) };
            Assert.That(AdhocVoiceChatSession.TryHandleCall(call, Alice, r.Store, Region, true, out ChatSessionOutcome o), Is.True);
            OSDMap creds = (OSDMap)o.Body["voice_credentials"];
            return (id, creds["channel_uri"].AsString(), creds["channel_credentials"].AsString());
        }

        // ---- the chat session request ------------------------------------------------------------

        [Test]
        public void AGroupCall_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            r.Service.Up = false;
            Assert.That(Chat(r, Alice, "call", Group), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AGroupCall_WhenTheServiceDropsBetweenStartAndSeat_Answers503()
        {
            Rig r = NewRig();
            OpenGroup(r);
            r.Service.DownOnly = "accept";
            Assert.That(Chat(r, Bob, "call", Group), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AGroupAcceptInvitation_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            OpenGroup(r);
            r.Service.Up = false;
            Assert.That(Chat(r, Bob, "accept invitation", Group), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AConferenceStart_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            r.Service.Up = false;
            Assert.That(Chat(r, Alice, "start conference", TempId, Bob), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AConferenceCall_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            (UUID id, _, _) = OpenConference(r);
            r.Service.Up = false;
            Assert.That(Chat(r, Alice, "call", id), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AConferenceCall_WhenTheServiceDropsBeforeTheSeat_Answers503()
        {
            Rig r = NewRig();
            (UUID id, _, _) = OpenConference(r);
            r.Service.DownOnly = "accept";
            Assert.That(Chat(r, Bob, "call", id), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AConferenceAcceptInvitation_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            (UUID id, _, _) = OpenConference(r);
            r.Service.Up = false;
            Assert.That(Chat(r, Bob, "accept invitation", id), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AConferenceInvite_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            (UUID id, _, _) = OpenConference(r);
            r.Service.Up = false;
            Assert.That(Chat(r, Alice, "invite", id, Bob), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        // ---- the voice provision -----------------------------------------------------------------

        [Test]
        public void AGroupProvision_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            (string room, string token) = OpenGroup(r);
            r.Service.Up = false;
            Assert.That(Provision(r, Alice, room, token), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AGroupProvision_WhenTheServiceDropsBeforeTheSeat_Answers503()
        {
            Rig r = NewRig();
            (string room, string token) = OpenGroup(r);
            r.Service.DownOnly = "accept";
            Assert.That(Provision(r, Alice, room, token), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AConferenceProvision_WhileTheServiceIsUnreachable_Answers503()
        {
            Rig r = NewRig();
            (_, string room, string token) = OpenConference(r);
            r.Service.Up = false;
            Assert.That(Provision(r, Alice, room, token), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public void AConferenceProvision_WhenTheServiceDropsBeforeTheSeat_Answers503()
        {
            Rig r = NewRig();
            (_, string room, string token) = OpenConference(r);
            r.Service.DownOnly = "accept";
            Assert.That(Provision(r, Alice, room, token), Is.EqualTo((int)HttpStatusCode.ServiceUnavailable));
        }

        // ---- every other refusal keeps its code --------------------------------------------------

        [Test]
        public void AGroupCallWithoutThePowers_StillAnswers403_WithTheServiceUp()
        {
            Rig r = NewRig(powers: 0UL);
            Assert.That(Chat(r, Alice, "call", Group), Is.EqualTo((int)HttpStatusCode.Forbidden));
        }

        [Test]
        public void AGroupProvisionWithAWrongToken_StillAnswers403_WithTheServiceUp()
        {
            Rig r = NewRig();
            (string room, _) = OpenGroup(r);
            Assert.That(Provision(r, Alice, room, "not-the-token"), Is.EqualTo((int)HttpStatusCode.Forbidden));
        }

        [Test]
        public void AConferenceProvisionForSomeoneNotInvited_StillAnswers403_WithTheServiceUp()
        {
            Rig r = NewRig();
            (_, string room, string token) = OpenConference(r);
            UUID stranger = new UUID("33333333-3333-3333-3333-333333333333");
            r.Process.AddRoot(stranger);
            Assert.That(Provision(r, stranger, room, token), Is.EqualTo((int)HttpStatusCode.Forbidden));
        }
    }
}
