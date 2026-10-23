/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The warnings a refused request or carrier writes, counted with a test appender. A viewer that keeps retrying, or a
 * stranger that keeps sending, is one line a minute for each kind of refusal, not one line a request.
 */
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.AdhocTextCrossProcessTests;
using static osWebRtcVoice.Tests.CallModerationModuleTests;
using static osWebRtcVoice.Tests.CallRemovalModuleTests;
using static osWebRtcVoice.Tests.CrossProcessHarness;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class RefusalWarningLogTests
    {
        private const int Requests = 30;

        private LogCapture _log;

        [SetUp]
        public void SetUp() => _log = new LogCapture();

        [TearDown]
        public void TearDown() => _log.Dispose();

        private List<LogCapture.Line> Warnings(string contains) => _log.At(LogLevel.Warning, contains);

        private static OSDMap Body(string channelType, string channel = null, string credentials = null)
        {
            OSDMap body = new OSDMap
            {
                ["channel_type"] = OSD.FromString(channelType),
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString("v=0") },
            };
            if (channel is not null) body["channel"] = OSD.FromString(channel);
            if (credentials is not null) body["credentials"] = OSD.FromString(credentials);
            return body;
        }

        private static int Provision(Region r, UUID agent, OSDMap body)
        {
            TestOSHttpResponse response = new TestOSHttpResponse();
            r.Module.ProvisionVoiceAccountRequest(new ProvisionPost(body), response, agent, r.Scene);
            return response.StatusCode;
        }

        [Test]
        public void ARefusedChannelType_WarnsOncePerAgent_NotOncePerRequest()
        {
            using Region r = Region.Create();

            for (int i = 0; i < Requests; i++)
                Assert.That(Provision(r, Member, Body("bogus")), Is.EqualTo((int)HttpStatusCode.Forbidden));

            Assert.That(Warnings("refusing provision with channel_type").Count, Is.EqualTo(1));

            UUID another = UUID.Random();
            SceneHelpers.AddScenePresence(r.Scene, another);
            Provision(r, another, Body("bogus"));
            Assert.That(Warnings("refusing provision with channel_type").Count, Is.EqualTo(2), "another agent has its own line");
        }

        [Test]
        public void ARefusedChildAgent_WarnsOncePerAgent_NotOncePerRequest()
        {
            using Region r = Region.Create();
            UUID child = UUID.Random();
            SceneHelpers.AddChildScenePresence(r.Scene, child);

            for (int i = 0; i < Requests; i++)
                Assert.That(r.Call(child), Is.EqualTo((int)HttpStatusCode.Forbidden));

            Assert.That(Warnings("refusing child agent").Count, Is.EqualTo(1));
        }

        [Test]
        public void ARefusedGroupProvision_WarnsOncePerAgent_NotOncePerRequest()
        {
            using Region r = Region.Create();
            r.Call(Officer);
            string room = r.Sessions.Get(GroupId).RoomKey;
            UUID stranger = UUID.Random();
            SceneHelpers.AddScenePresence(r.Scene, stranger);

            for (int i = 0; i < Requests; i++)
                Assert.That(Provision(r, stranger, Body("multiagent", room, "not-the-token")), Is.EqualTo((int)HttpStatusCode.Forbidden));

            Assert.That(Warnings("refusing group provision").Count, Is.EqualTo(1));
        }

        [Test]
        public void AStrangersLinesToAConference_WarnOncePerSender_NotOncePerLine()
        {
            using Region r = Region.Create(v => v.Set("AdhocVoiceEnabled", "true"));
            UUID home = r.Scene.RegionInfo.RegionID;
            UUID conference = r.Sessions.StartAdhoc(Officer, UUID.Random(), home, new[] { Member }).Session.SessionId;
            UUID stranger = UUID.Random();

            for (int i = 0; i < Requests; i++)
                Invoke(r.Module, "OnViewerConferenceText", null, new GridInstantMessage
                {
                    dialog = AdhocTextSession.DialogSessionSend,
                    fromAgentID = stranger.Guid,
                    fromAgentName = "Test Stranger",
                    imSessionID = conference.Guid,
                    message = "let me in " + i,
                });

            Assert.That(Warnings(stranger.ToString()).Count, Is.EqualTo(1));
        }

        [Test]
        public void ASignedConferenceLineFromANonMember_WarnsOnce_NotOncePerCarrier()
        {
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            string roomKey = two.Engine.Store.Get(session).RoomKey;

            for (int i = 0; i < Requests; i++)
                Invoke(two.Module, "OnIncomingConferenceText", AdhocTextTransport.Build(Bob, Stranger, "Test Stranger", session, RegionA,
                    "let me in " + i, null, roomKey, (CarrierAuth)GetPrivate(one.Module, "m_carrierAuth")));

            Assert.That(Warnings(Stranger.ToString()).Count, Is.EqualTo(1));
        }

        [Test]
        public void ARingWhoseRoomKeyIsWrong_WarnsOnce_NotOncePerRing()
        {
            // The far process derives its room keys from another grid id, so every genuine ring it receives is refused.
            INonSpatialSessionStore store1 = new InMemoryNonSpatialSessionStore();
            Node one = MakeNode("Region One", 1000, store1);
            Node two = MakeNode("Region Two", 1001, new InMemoryNonSpatialSessionStore(), grid: "another-grid");
            one.Process.AddRoot(Alice);
            two.Process.AddRoot(Bob);
            one.Process.Presence.SetOnline(Bob);
            two.Process.Presence.SetOnline(Alice);
            SessionOutcome start = one.Engine.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });

            // Each ring is signed afresh, as a sender that keeps ringing would; a repeat of one carrier is a replay and is refused earlier.
            OSDMap invitation = AdhocVoiceInvite.BuildBody(start.Session, one.Engine.IssueToken(start.Session.SessionId, Alice), Alice, "Test User");
            CarrierAuth auth = (CarrierAuth)GetPrivate(one.Module, "m_carrierAuth");
            for (int i = 0; i < Requests; i++)
                Invoke(two.Module, "OnIncomingGroupRing",
                    GroupVoiceRingTransport.Build(Bob, Alice, "Test User", start.Session.SessionId, RegionA, invitation, auth));

            Assert.That(Warnings("check GatekeeperURI").Count, Is.EqualTo(1));
        }

        [Test]
        public void ARefusedAvatarToAvatarCarrier_WarnsOncePerDecision_NotOncePerCarrier()
        {
            using Region r = Region.Create();
            A2ACarrier carrier = new A2ACarrier { Step = A2ACarrierTransport.StepRing, Target = Member, About = Officer, SessionId = UUID.Random() };

            for (int i = 0; i < Requests; i++)
                Invoke(r.Module, "LogA2ACarrier", carrier, A2ACarrierTransport.DecisionBadToken, null);
            Assert.That(Warnings(A2ACarrierTransport.DecisionBadToken).Count, Is.EqualTo(1));

            Invoke(r.Module, "LogA2ACarrier", carrier, A2ACarrierTransport.DecisionBadRoom, null);
            Assert.That(Warnings(A2ACarrierTransport.DecisionBadRoom).Count, Is.EqualTo(1), "another reason has its own line");

            for (int i = 0; i < Requests; i++)
                Invoke(r.Module, "LogA2ACarrier", carrier, A2ACarrierTransport.DecisionSent, null);
            Assert.That(Warnings(A2ACarrierTransport.DecisionSent), Is.Empty, "a carrier that was sent is a Debug line, as before");
        }
    }
}
