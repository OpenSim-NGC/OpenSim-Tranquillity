/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * When the callee turns a call down, the caller's viewer is told, so the call ends on its screen instead of
 * ringing out. The viewer reads an agent-list entry for the peer that says it cannot voice chat as the peer
 * having declined, shows the call as declined and ends it (llimview.cpp:4366-4382), and it skips a LEAVE entry
 * before it reads that flag (llimview.cpp:4364-4366), so the entry is an ENTER.
 *
 * These tests run the module's own chat session handler with both people root in one region server process.
 */
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    public class A2ADeclineTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");
        private static readonly UUID Session = A2ACalls.ComputeSessionId(Alice, Bob);

        private static (WebRtcVoiceRegionModule Module, Process Process) Region()
        {
            Process p = new Process("Region One", 1000, 1000);
            WebRtcVoiceRegionModule module = new WebRtcVoiceRegionModule();
            SetPrivate(module, "m_Enabled", true);
            SetPrivate(module, "m_a2aVoiceEnabled", true);
            SetPrivate(module, "m_nonSpatial", new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission() }, "example-grid"));
            ((List<Scene>)GetPrivate(module, "m_scenes")).Add(p.Scene);
            p.AddRoot(Alice);
            p.AddRoot(Bob);
            p.AddRoot(Carol);
            return (module, p);
        }

        private static int Chat(WebRtcVoiceRegionModule module, Process p, UUID agent, string method, UUID? other = null)
        {
            OSDMap body = new OSDMap
            {
                ["method"] = OSD.FromString(method),
                ["session-id"] = OSD.FromUUID(Session),
                ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
            };
            if (other.HasValue) body["params"] = OSD.FromUUID(other.Value);
            TestOSHttpResponse resp = new TestOSHttpResponse();
            module.ChatSessionRequest(new LlsdPost(body), resp, agent, p.Scene);
            return resp.StatusCode;
        }

        /// <summary>Alice starts a call to Bob and presses Call; Bob's invitation is delivered.</summary>
        private static void PlaceCall(WebRtcVoiceRegionModule module, Process p)
        {
            Assert.That(Chat(module, p, Alice, "start p2p voice", Bob), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(Chat(module, p, Alice, "call"), Is.EqualTo((int)HttpStatusCode.OK));
            Assert.That(p.Queue.Enqueued, Is.EqualTo(new[] { Bob }), "Bob's viewer is rung");
            p.Queue.AgentLists.Clear();
        }

        [Test]
        public void WhenTheCalleeDeclines_TheCallerIsToldItCannotVoiceChat()
        {
            var (module, p) = Region();
            PlaceCall(module, p);

            Assert.That(Chat(module, p, Bob, "decline p2p voice"), Is.EqualTo((int)HttpStatusCode.OK));

            var (session, to, updates) = p.Queue.AgentLists.Single();
            Assert.That(to, Is.EqualTo(Alice));
            Assert.That(session, Is.EqualTo(Session));
            GroupChatListAgentUpdateData declined = updates.Single();
            Assert.That(declined.agentID, Is.EqualTo(Bob));
            Assert.That(declined.canVoice, Is.False, "the viewer ends the call on a peer that cannot voice chat (llimview.cpp:4369)");
            Assert.That(declined.enterOrLeave, Is.True, "a LEAVE is skipped before can_voice_chat is read (llimview.cpp:4364)");
        }

        [Test]
        public void WhenTheCallerCancels_NobodyIsToldTheCalleeDeclined()
        {
            var (module, p) = Region();
            PlaceCall(module, p);

            Chat(module, p, Alice, "decline p2p voice");

            Assert.That(p.Queue.AgentLists, Is.Empty);
        }

        [Test]
        public void ADeclineFromSomeoneNotInTheCall_TellsNobody()
        {
            var (module, p) = Region();
            PlaceCall(module, p);

            Chat(module, p, Carol, "decline p2p voice");

            Assert.That(p.Queue.AgentLists, Is.Empty);
        }

        [Test]
        public void ADeclineForACallThatIsGone_TellsNobody()
        {
            var (module, p) = Region();
            PlaceCall(module, p);
            Chat(module, p, Bob, "decline p2p voice");
            p.Queue.AgentLists.Clear();

            Chat(module, p, Bob, "decline p2p voice");

            Assert.That(p.Queue.AgentLists, Is.Empty, "the second decline finds nothing to end");
        }
    }
}
