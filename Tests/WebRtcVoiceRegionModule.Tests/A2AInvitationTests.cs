/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Tests for the voice ChatterBoxInvitation to the callee.
 *
 * Wire contract asserted here: the viewer's LLViewerChatterBoxInvitation (llimview.cpp) takes the VOICE
 * branch only when the body has a `voice` map and NO `instantmessage` block; it reads top-level session_id,
 * session_name, from_id, from_name and body.voice.invitation_type (2 = P2P), and hands the whole `voice`
 * map to startAdHocSession, which reads channel_uri and channel_credentials (llvoicewebrtc.cpp:1497-1498).
 */
using System;
using System.Collections.Generic;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class A2AInvitationTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Xor = A2ASessionRegistry.ComputeSessionId(Alice, Bob);

        private static OSDMap StartBody() => new OSDMap
        {
            ["method"] = OSD.FromString("start p2p voice"),
            ["session-id"] = OSD.FromUUID(Xor),
            ["params"] = OSD.FromUUID(Bob),
        };

        private static OSDMap CallBody() => new OSDMap
        {
            ["method"] = OSD.FromString("call"),
            ["session-id"] = OSD.FromUUID(Xor),
            ["alt_params"] = new OSDMap { ["preferred_voice_server_type"] = OSD.FromString("webrtc") },
        };

        // ---- body shape ---------------------------------------------------------------------------

        [Test]
        public void BuildBody_HasExactlyTheViewerVoiceBranchShape()
        {
            var reg = new A2ASessionRegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);

            OSDMap body = A2AInvitation.BuildBody(s, Alice, "Alice Caller");

            // top-level keys the viewer reads (llimview.cpp:5207-5210)
            Assert.That(body["session_id"].AsUUID(), Is.EqualTo(Xor));
            Assert.That(body["session_name"].AsString(), Is.EqualTo("Alice Caller"), "labels the callee's incoming-call UI: the CALLER's name");
            Assert.That(body["from_id"].AsUUID(), Is.EqualTo(Alice));
            Assert.That(body["from_name"].AsString(), Is.EqualTo("Alice Caller"));
            // the branch selector
            Assert.That(body.ContainsKey("voice"), Is.True);
            Assert.That(body.ContainsKey("instantmessage"), Is.False, "its presence routes the viewer to the IM branch (llimview.cpp:5047)");
            Assert.That(body.ContainsKey("immediate"), Is.False);
            // the voice map becomes voice_channel_info verbatim
            OSDMap voice = (OSDMap)body["voice"];
            Assert.That(voice["invitation_type"].AsInteger(), Is.EqualTo(2), "P2P_CHAT_SESSION (llimview.cpp:5204)");
            Assert.That(voice["channel_uri"].AsString(), Is.EqualTo(Xor.ToString()), "= the provision's `channel` (llvoicewebrtc.cpp:1497)");
            Assert.That(voice["channel_credentials"].AsString(), Is.EqualTo(s.Token), "= the provision's `credentials` (llvoicewebrtc.cpp:1498)");
            // This routes the channel to the webrtc module (llvoiceclient.cpp:514-528; absent
            // defaults to Vivox, :126-132) and satisfies compareChannels' first test (llvoicewebrtc.cpp:1684).
            Assert.That(voice["voice_server_type"].AsString(), Is.EqualTo("webrtc"));
            Assert.That(voice.ContainsKey("sip_uri"), Is.False,
                "compareChannels tests sip_uri equality and the module's own maps lack it (getAudioSessionChannelInfo, llvoicewebrtc.cpp:1626-1636): absent==absent is what compares equal");
            Assert.That(voice.Count, Is.EqualTo(4));
            Assert.That(body.Count, Is.EqualTo(5));

            // and it survives the event serialisation the queue applies
            string xml = OSDParser.SerializeLLSDXmlString(body);
            Assert.That(xml, Does.Contain("channel_credentials").And.Contain("invitation_type"));
        }

        [Test]
        public void BuildBody_RequiresAMintedToken()
        {
            var reg = new A2ASessionRegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            Assert.That(() => A2AInvitation.BuildBody(s, Alice, "x"), Throws.InvalidOperationException);
        }

        // ---- trigger: on "call", never on "start p2p voice" ---------------------------------------------

        [Test]
        public void StartP2PVoice_NeverProducesAnInvitation()
        {
            var reg = new A2ASessionRegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(), Alice, "Alice Caller", reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Invite, Is.Null, "the viewer sends start p2p voice on every P2P IM window open (Firestorm llimview.cpp:634, SL llimview.cpp:485); it must not ring");
        }

        [Test]
        public void Call_ProducesTheInvitation_ForTheOtherParty_WithTheSameToken()
        {
            var reg = new A2ASessionRegistry();
            ChatSessionRequestLogic.Decide(StartBody(), Alice, "Alice Caller", reg);

            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);

            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(o.Invite, Is.Not.Null);
            Assert.That(o.Invite.Callee, Is.EqualTo(Bob));
            Assert.That(o.Invite.Caller, Is.EqualTo(Alice));
            Assert.That(o.Invite.SessionId, Is.EqualTo(Xor));
            string token = ((OSDMap)o.Body["voice_credentials"])["channel_credentials"].AsString();
            Assert.That(((OSDMap)o.Invite.Body["voice"])["channel_credentials"].AsString(), Is.EqualTo(token),
                "caller and callee join with the same session credentials");
            Assert.That(o.Invite.Body["session_name"].AsString(), Is.EqualTo("Alice Caller"));
            Assert.That(o.Instrument, Does.Contain("invite=pending").And.Not.Contain(token));
        }

        [Test]
        public void RepeatCall_ReSendsTheInvitation_WithTheSameCredentials()
        {
            // The viewer retries "call" up to 3x on ERROR_NOT_AVAILABLE (llvoicechannel.cpp:571-579). The
            // callee viewer ignores a duplicate while the first invitation is pending
            // (mPendingInvitations, llimview.cpp:4257-4281), so re-sending is safe.
            var reg = new A2ASessionRegistry();
            ChatSessionRequestLogic.Decide(StartBody(), Alice, "Alice Caller", reg);
            ChatSessionOutcome first = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);
            ChatSessionOutcome second = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);
            Assert.That(second.Invite, Is.Not.Null);
            Assert.That(((OSDMap)second.Invite.Body["voice"])["channel_credentials"].AsString(),
                Is.EqualTo(((OSDMap)first.Invite.Body["voice"])["channel_credentials"].AsString()));
            Assert.That(reg.Count, Is.EqualTo(1));
        }

        [Test]
        public void Call_WithoutInvitation_ProducesNoInvite()
        {
            var reg = new A2ASessionRegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);
            Assert.That(o.Status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(o.Invite, Is.Null);
        }

        // ---- delivery on this instance ------------------------------------------------------------------

        [Test]
        public void Deliver_CalleeRootPresence_EnqueuesTheEventToTheCallee()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);
            var queue = new CapturingEventQueue();
            OSDMap body = SomeBody();

            string decision = A2AInviteDelivery.Deliver(new[] { scene }, Bob, body, _ => queue, out string region);

            Assert.That(decision, Is.EqualTo(A2AInviteDelivery.DecisionSent));
            Assert.That(region, Is.EqualTo(scene.RegionInfo.RegionName));
            Assert.That(queue.Built, Has.Count.EqualTo(1));
            Assert.That(queue.Built[0].Name, Is.EqualTo("ChatterBoxInvitation"));
            Assert.That(queue.Built[0].Body, Is.SameAs(body));
            Assert.That(queue.Enqueued, Has.Count.EqualTo(1));
            Assert.That(queue.Enqueued[0], Is.EqualTo(Bob), "delivered to the CALLEE, not the caller");
        }

        [Test]
        public void Deliver_CalleeNotOnAnyScene_IsUnreachable_NothingEnqueued()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);          // only the caller is here
            var queue = new CapturingEventQueue();

            string decision = A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => queue, out string region);

            Assert.That(decision, Is.EqualTo(A2AInviteDelivery.DecisionNoPresence));
            Assert.That(region, Is.EqualTo("-"));
            Assert.That(queue.Enqueued, Is.Empty);
            Assert.That(A2AInviteDelivery.Deliver(Array.Empty<Scene>(), Bob, SomeBody(), _ => queue, out _), Is.EqualTo(A2AInviteDelivery.DecisionNoPresence));
            Assert.That(A2AInviteDelivery.Deliver(null, Bob, SomeBody(), _ => queue, out _), Is.EqualTo(A2AInviteDelivery.DecisionNoPresence));
        }

        [Test]
        public void Deliver_CalleePresentButNoEventQueue_IsUnreachable()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);
            string decision = A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => null, out _);
            Assert.That(decision, Is.EqualTo(A2AInviteDelivery.DecisionNoQueue));
        }

        [Test]
        public void Deliver_EnqueueRefused_IsReported_NotThrown()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);
            var queue = new CapturingEventQueue { EnqueueResult = false };
            Assert.That(A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => queue, out _), Is.EqualTo(A2AInviteDelivery.DecisionEnqueueFailed));
            var thrower = new CapturingEventQueue { Throw = true };
            Assert.That(A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => thrower, out _), Is.EqualTo(A2AInviteDelivery.DecisionEnqueueFailed));
        }

        [Test]
        public void ResolveCalleeScene_PrefersRoot_FallsBackToChild()
        {
            Scene a = new SceneHelpers().SetupScene("a", UUID.Random(), 1000, 1000);
            Scene b = new SceneHelpers().SetupScene("b", UUID.Random(), 1001, 1000);
            ScenePresence child = SceneHelpers.AddScenePresence(a, Bob);
            child.IsChildAgent = true;
            SceneHelpers.AddScenePresence(b, Bob);                 // root here

            Assert.That(A2AInviteDelivery.ResolveCalleeScene(new[] { a, b }, Bob, out bool isChild), Is.SameAs(b));
            Assert.That(isChild, Is.False);
            Assert.That(A2AInviteDelivery.ResolveCalleeScene(new[] { a }, Bob, out isChild), Is.SameAs(a), "a child presence is a fallback, as in GetActiveClient");
            Assert.That(isChild, Is.True);
        }

        // ---- instrument ----------------------------------------------------------------------------

        [Test]
        public void InviteLine_NamesBothParties_AndNeverTheToken()
        {
            var reg = new A2ASessionRegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);
            string line = A2AInviteDelivery.Line(Bob, Alice, s.SessionId, "Elm", A2AInviteDelivery.DecisionSent);
            Assert.That(line, Does.StartWith("[A2A INVITE] agent=" + Bob).And.Contain("from=" + Alice).And.Contain("session-id=" + s.SessionId).And.Contain("region=Elm").And.Contain("decision=sent"));
            Assert.That(line, Does.Not.Contain(s.Token));
        }

        // ---- fixtures ---------------------------------------------------------------------------------

        private static OSDMap SomeBody() => new OSDMap { ["session_id"] = OSD.FromUUID(Xor), ["voice"] = new OSDMap() };

        /// <summary>Records BuildEvent/Enqueue; every other member is unreachable from the code under test.</summary>
        private sealed class CapturingEventQueue : IEventQueue
        {
            public readonly List<(string Name, OSD Body)> Built = new();
            public readonly List<UUID> Enqueued = new();
            public bool EnqueueResult = true;
            public bool Throw;

            public byte[] BuildEvent(string eventName, OSD eventBody)
            {
                if (Throw) throw new InvalidOperationException("boom");
                Built.Add((eventName, eventBody));
                return new byte[] { 1 };
            }
            public bool Enqueue(byte[] o, UUID avatarID) { Enqueued.Add(avatarID); return EnqueueResult; }
            public bool Enqueue(OSD o, UUID avatarID) => throw new NotSupportedException();
            public bool Enqueue(osUTF8 o, UUID avatarID) => throw new NotSupportedException();
            public void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void EstablishAgentCommunication(UUID avatarID, IPEndPoint endPoint, string capsPath, ulong regionHandle, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void TeleportFinishEvent(ulong regionHandle, byte simAccess, IPEndPoint regionExternalEndPoint, uint locationID, uint flags, string capsURL, UUID agentID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, IPEndPoint newRegionExternalEndPoint, string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void ChatterboxInvitation(UUID sessionID, string sessionName, UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog, uint timeStamp, bool offline, int parentEstateID, Vector3 position, uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket) => throw new NotSupportedException("the IM-only helper must not be used for voice");
            public void ChatterBoxSessionStartReply(UUID sessionID, string sessionName, int type, bool voiceEnabled, bool voiceModerated, UUID tmpSessionID, bool sucess, string error, UUID toAgent) => throw new NotSupportedException();
            public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates) => throw new NotSupportedException();
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
    }
}
