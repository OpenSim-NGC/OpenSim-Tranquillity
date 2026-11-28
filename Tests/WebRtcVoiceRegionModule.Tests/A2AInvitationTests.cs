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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class A2AInvitationTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Xor = A2ARegistry.ComputeSessionId(Alice, Bob);

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

        [Fact(Timeout = 60000)]
        public void BuildBody_HasExactlyTheViewerVoiceBranchShape()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            s = reg.IssueToken(s.SessionId, Alice);

            OSDMap body = A2AInvitation.BuildBody(s, Alice, "Alice Caller");

            // top-level keys the viewer reads (llimview.cpp:5207-5210)
            Assert.Equal(Xor, body["session_id"].AsUUID());
            {
                var assertedValue = body["session_name"].AsString();
                TestAssert.WithMessage(() => Assert.Equal("Alice Caller", assertedValue), "labels the callee's incoming-call UI: the CALLER's name");
            }
            Assert.Equal(Alice, body["from_id"].AsUUID());
            Assert.Equal("Alice Caller", body["from_name"].AsString());
            // the branch selector
            Assert.True(body.ContainsKey("voice"));
            Assert.False(body.ContainsKey("instantmessage"), "its presence routes the viewer to the IM branch (llimview.cpp:5047)");
            Assert.False(body.ContainsKey("immediate"));
            // the voice map becomes voice_channel_info verbatim
            OSDMap voice = (OSDMap)body["voice"];
            {
                var assertedValue = voice["invitation_type"].AsInteger();
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "P2P_CHAT_SESSION (llimview.cpp:5204)");
            }
            {
                var assertedValue = voice["channel_uri"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(Xor.ToString(), assertedValue), "= the provision's `channel` (llvoicewebrtc.cpp:1497)");
            }
            {
                var assertedValue = voice["channel_credentials"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(s.Token, assertedValue), "= the provision's `credentials` (llvoicewebrtc.cpp:1498)");
            }
            // This routes the channel to the webrtc module (llvoiceclient.cpp:514-528; absent
            // defaults to Vivox, :126-132) and satisfies compareChannels' first test (llvoicewebrtc.cpp:1684).
            Assert.Equal("webrtc", voice["voice_server_type"].AsString());
            Assert.False(voice.ContainsKey("sip_uri"), "compareChannels tests sip_uri equality and the module's own maps lack it (getAudioSessionChannelInfo, llvoicewebrtc.cpp:1626-1636): absent==absent is what compares equal");
            Assert.Equal(4, voice.Count);
            Assert.Equal(5, body.Count);

            // and it survives the event serialisation the queue applies
            string xml = OSDParser.SerializeLLSDXmlString(body);
            {
                var assertedValue = xml;
                Assert.Contains("channel_credentials", assertedValue);
                Assert.Contains("invitation_type", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void BuildBody_RequiresAMintedToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            {
                var assertedValue = () => A2AInvitation.BuildBody(s, Alice, "x");
                Assert.Throws<InvalidOperationException>(() =>
                {
                    assertedValue();
                });
            }
        }

        // ---- trigger: on "call", never on "start p2p voice" ---------------------------------------------

        [Fact(Timeout = 60000)]
        public void StartP2PVoice_NeverProducesAnInvitation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(StartBody(), Alice, "Alice Caller", reg);
            Assert.Equal(HttpStatusCode.OK, o.Status);
            {
                var assertedValue = o.Invite;
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the viewer sends start p2p voice on every P2P IM window open (Firestorm llimview.cpp:634, SL llimview.cpp:485); it must not ring");
            }
        }

        [Fact(Timeout = 60000)]
        public void Call_ProducesTheInvitation_ForTheOtherParty_WithTheSameToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionRequestLogic.Decide(StartBody(), Alice, "Alice Caller", reg);

            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);

            Assert.Equal(HttpStatusCode.OK, o.Status);
            Assert.NotNull(o.Invite);
            Assert.Equal(Bob, o.Invite.Callee);
            Assert.Equal(Alice, o.Invite.Caller);
            Assert.Equal(Xor, o.Invite.SessionId);
            string token = ((OSDMap)o.Body["voice_credentials"])["channel_credentials"].AsString();
            {
                var assertedValue = ((OSDMap)o.Invite.Body["voice"])["channel_credentials"].AsString();
                TestAssert.WithMessage(() => Assert.Equal(token, assertedValue), "caller and callee join with the same session credentials");
            }
            Assert.Equal("Alice Caller", o.Invite.Body["session_name"].AsString());
            {
                var assertedValue = o.Instrument;
                Assert.Contains("invite=pending", assertedValue);
                Assert.DoesNotContain(token, assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void RepeatCall_ReSendsTheInvitation_WithTheSameCredentials()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The viewer retries "call" up to 3x on ERROR_NOT_AVAILABLE (llvoicechannel.cpp:571-579). The
            // callee viewer ignores a duplicate while the first invitation is pending
            // (mPendingInvitations, llimview.cpp:4257-4281), so re-sending is safe.
            var reg = new A2ARegistry();
            ChatSessionRequestLogic.Decide(StartBody(), Alice, "Alice Caller", reg);
            ChatSessionOutcome first = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);
            ChatSessionOutcome second = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);
            Assert.NotNull(second.Invite);
            Assert.Equal(((OSDMap)first.Invite.Body["voice"])["channel_credentials"].AsString(), ((OSDMap)second.Invite.Body["voice"])["channel_credentials"].AsString());
            Assert.Equal(1, reg.Count);
        }

        [Fact(Timeout = 60000)]
        public void Call_WithoutInvitation_ProducesNoInvite()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            ChatSessionOutcome o = ChatSessionRequestLogic.Decide(CallBody(), Alice, "Alice Caller", reg);
            Assert.Equal(HttpStatusCode.NotFound, o.Status);
            Assert.Null(o.Invite);
        }

        // ---- delivery on this instance ------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void Deliver_CalleeRootPresence_EnqueuesTheEventToTheCallee()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);
            var queue = new CapturingEventQueue();
            OSDMap body = SomeBody();

            string decision = A2AInviteDelivery.Deliver(new[] { scene }, Bob, body, _ => queue, out string region);

            Assert.Equal(A2AInviteDelivery.DecisionSent, decision);
            Assert.Equal(scene.RegionInfo.RegionName, region);
            Assert.Single(queue.Built);
            Assert.Equal("ChatterBoxInvitation", queue.Built[0].Name);
            Assert.Same(body, queue.Built[0].Body);
            Assert.Single(queue.Enqueued);
            {
                var assertedValue = queue.Enqueued[0];
                TestAssert.WithMessage(() => Assert.Equal(Bob, assertedValue), "delivered to the CALLEE, not the caller");
            }
        }

        [Fact(Timeout = 60000)]
        public void Deliver_CalleeNotOnAnyScene_IsUnreachable_NothingEnqueued()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);          // only the caller is here
            var queue = new CapturingEventQueue();

            string decision = A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => queue, out string region);

            Assert.Equal(A2AInviteDelivery.DecisionNoPresence, decision);
            Assert.Equal("-", region);
            Assert.Empty(queue.Enqueued);
            Assert.Equal(A2AInviteDelivery.DecisionNoPresence, A2AInviteDelivery.Deliver(Array.Empty<Scene>(), Bob, SomeBody(), _ => queue, out _));
            Assert.Equal(A2AInviteDelivery.DecisionNoPresence, A2AInviteDelivery.Deliver(null, Bob, SomeBody(), _ => queue, out _));
        }

        [Fact(Timeout = 60000)]
        public void Deliver_CalleePresentButNoEventQueue_IsUnreachable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);
            string decision = A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => null, out _);
            Assert.Equal(A2AInviteDelivery.DecisionNoQueue, decision);
        }

        [Fact(Timeout = 60000)]
        public void Deliver_EnqueueRefused_IsReported_NotThrown()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);
            var queue = new CapturingEventQueue { EnqueueResult = false };
            Assert.Equal(A2AInviteDelivery.DecisionEnqueueFailed, A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => queue, out _));
            var thrower = new CapturingEventQueue { Throw = true };
            Assert.Equal(A2AInviteDelivery.DecisionEnqueueFailed, A2AInviteDelivery.Deliver(new[] { scene }, Bob, SomeBody(), _ => thrower, out _));
        }

        [Fact(Timeout = 60000)]
        public void ResolveCalleeScene_PrefersRoot_FallsBackToChild()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene a = new SceneHelpers().SetupScene("a", UUID.Random(), 1000, 1000);
            Scene b = new SceneHelpers().SetupScene("b", UUID.Random(), 1001, 1000);
            ScenePresence child = SceneHelpers.AddScenePresence(a, Bob);
            child.IsChildAgent = true;
            SceneHelpers.AddScenePresence(b, Bob);                 // root here

            Assert.Same(b, A2AInviteDelivery.ResolveCalleeScene(new[] { a, b }, Bob, out bool isChild));
            Assert.False(isChild);
            {
                var assertedValue = A2AInviteDelivery.ResolveCalleeScene(new[] { a }, Bob, out isChild);
                TestAssert.WithMessage(() => Assert.Same(a, assertedValue), "a child presence is a fallback, as in GetActiveClient");
            }
            Assert.True(isChild);
        }

        // ---- instrument ----------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void InviteLine_NamesBothParties_AndNeverTheToken()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            s = reg.IssueToken(s.SessionId, Alice);
            string line = A2AInviteDelivery.Line(Bob, Alice, s.SessionId, "Elm", A2AInviteDelivery.DecisionSent);
            {
                var assertedValue = line;
                Assert.StartsWith("[A2A INVITE] agent=" + Bob, assertedValue);
                Assert.Contains("from=" + Alice, assertedValue);
                Assert.Contains("session-id=" + s.SessionId, assertedValue);
                Assert.Contains("region=Elm", assertedValue);
                Assert.Contains("decision=sent", assertedValue);
            }
            Assert.DoesNotContain(s.Token, line);
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
