/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * ChatterBoxSessionAgentListUpdates for A2A sessions.
 *
 * The one hard invariant from the viewer trace: can_voice_chat MUST be true -- the viewer
 * treats can_voice_chat:false for the peer on a P2P channel as a decline and hangs up the call
 * (P2PCallDeclined + endCall, llimview.cpp:4366-4382). The group pattern's 1-arg ctor defaults
 * canVoice to FALSE (IEventQueue.cs); these tests assert the false case is impossible by
 * construction in every update this class can produce.
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
    public class A2AAgentListTests
    {
        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Xor = A2ARegistry.ComputeSessionId(Alice, Bob);

        /// <summary>Records ChatterBoxSessionAgentListUpdates calls; everything else unsupported.</summary>
        private sealed class CapturingAgentListQueue : IEventQueue
        {
            public readonly List<(UUID SessionId, UUID ToAgent, List<GroupChatListAgentUpdateData> Updates)> Sent = new();
            public bool Throw;

            public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates)
            {
                if (Throw) throw new InvalidOperationException("boom");
                Sent.Add((sessionID, toAgent, updates));
            }

            public byte[] BuildEvent(string eventName, OSD eventBody) => throw new NotSupportedException();
            public bool Enqueue(byte[] o, UUID avatarID) => throw new NotSupportedException();
            public bool Enqueue(OSD o, UUID avatarID) => throw new NotSupportedException();
            public bool Enqueue(osUTF8 o, UUID avatarID) => throw new NotSupportedException();
            public void EnableSimulator(ulong handle, System.Net.IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void EstablishAgentCommunication(UUID avatarID, System.Net.IPEndPoint endPoint, string capsPath, ulong regionHandle, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void TeleportFinishEvent(ulong regionHandle, byte simAccess, System.Net.IPEndPoint regionExternalEndPoint, uint locationID, uint flags, string capsURL, UUID agentID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, System.Net.IPEndPoint newRegionExternalEndPoint, string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void ChatterboxInvitation(UUID sessionID, string sessionName, UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog, uint timeStamp, bool offline, int parentEstateID, Vector3 position, uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket) => throw new NotSupportedException();
            public void ChatterBoxSessionStartReply(UUID sessionID, string sessionName, int type, bool voiceEnabled, bool voiceModerated, UUID tmpSessionID, bool sucess, string error, UUID toAgent) => throw new NotSupportedException();
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

        private static A2ASession LiveSession()
        {
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-a");
            reg.MarkProvisioned(s.SessionId, Bob, "vs-b");
            return s;
        }

        // ---- the can_voice_chat invariant (impossible-false by construction) -----------------------

        [Test]
        public void EveryUpdateTheDeliveryCanProduce_HasCanVoiceTrue()
        {
            foreach (GroupChatListAgentUpdateData u in A2AAgentListDelivery.EnterUpdates(Bob, Alice))
            {
                Assert.That(u.canVoice, Is.True, "can_voice_chat:false hangs up a P2P call (llimview.cpp:4366-4382)");
                Assert.That(u.isModerator, Is.False);
                Assert.That(u.mutedText, Is.False);
                Assert.That(u.enterOrLeave, Is.True, "ENTER");
            }
            foreach (GroupChatListAgentUpdateData u in A2AAgentListDelivery.LeaveUpdates(Alice))
            {
                Assert.That(u.canVoice, Is.True, "true even on LEAVE: the flag must never read as a decline");
                Assert.That(u.enterOrLeave, Is.False, "LEAVE");
            }
            Assert.That(A2AAgentListDelivery.Update(Alice, true).canVoice, Is.True,
                "the single seam every list goes through hardcodes cv:true (the group 1-arg ctor defaults FALSE)");
        }

        [Test]
        public void EnterUpdates_CarryOtherThenSelf()
        {
            List<GroupChatListAgentUpdateData> ups = A2AAgentListDelivery.EnterUpdates(Bob, Alice);
            Assert.That(ups.Count, Is.EqualTo(2), "the other party plus, per the group pattern, the recipient's own entry");
            Assert.That(ups[0].agentID, Is.EqualTo(Bob));
            Assert.That(ups[1].agentID, Is.EqualTo(Alice));
        }

        // ---- delivery ------------------------------------------------------------------------------

        [Test]
        public void ActivePair_BothPartiesReceive()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            SceneHelpers.AddScenePresence(scene, Bob);
            var queue = new CapturingAgentListQueue();
            A2ASession s = LiveSession();

            List<string> lines = A2AAgentListDelivery.SendActivePair(new[] { scene }, s, _ => queue);

            Assert.That(queue.Sent.Count, Is.EqualTo(2));
            Assert.That(queue.Sent[0].ToAgent, Is.EqualTo(Alice), "caller first");
            Assert.That(queue.Sent[0].SessionId, Is.EqualTo(Xor));
            Assert.That(queue.Sent[0].Updates[0].agentID, Is.EqualTo(Bob), "the caller learns about the callee");
            Assert.That(queue.Sent[1].ToAgent, Is.EqualTo(Bob));
            Assert.That(queue.Sent[1].Updates[0].agentID, Is.EqualTo(Alice));
            foreach (var sent in queue.Sent)
                foreach (GroupChatListAgentUpdateData u in sent.Updates)
                    Assert.That(u.canVoice, Is.True);
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines[0], Does.Contain("[A2A AGENTLIST]").And.Contain($"agent={Alice}")
                .And.Contain("transition=ENTER").And.Contain($"about={Bob}").And.Contain("decision=sent"));
        }

        [Test]
        public void Leave_GoesToTheRemainingParty()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);           // only the remaining party is present
            var queue = new CapturingAgentListQueue();
            A2ASession s = LiveSession();

            string line = A2AAgentListDelivery.SendLeave(new[] { scene }, s, Alice, _ => queue);

            Assert.That(queue.Sent.Count, Is.EqualTo(1));
            Assert.That(queue.Sent[0].ToAgent, Is.EqualTo(Bob));
            Assert.That(queue.Sent[0].Updates.Count, Is.EqualTo(1));
            Assert.That(queue.Sent[0].Updates[0].agentID, Is.EqualTo(Alice));
            Assert.That(queue.Sent[0].Updates[0].enterOrLeave, Is.False);
            Assert.That(line, Does.Contain($"agent={Bob}").And.Contain("transition=LEAVE")
                .And.Contain($"about={Alice}").And.Contain("decision=sent"));
        }

        [Test]
        public void UnreachableRecipient_IsToleratedAndInstrumented()
        {
            Scene scene = new SceneHelpers().SetupScene();       // nobody present
            var queue = new CapturingAgentListQueue();
            A2ASession s = LiveSession();

            List<string> lines = A2AAgentListDelivery.SendActivePair(new[] { scene }, s, _ => queue);
            Assert.That(queue.Sent, Is.Empty);
            Assert.That(lines[0], Does.Contain("decision=unreachable(no-presence)"));

            string leave = A2AAgentListDelivery.SendLeave(new[] { scene }, s, Alice, _ => queue);
            Assert.That(leave, Does.Contain("decision=unreachable(no-presence)"));
        }

        [Test]
        public void ThrowingQueue_NeverPropagates()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var queue = new CapturingAgentListQueue { Throw = true };
            A2ASession s = LiveSession();
            string d = A2AAgentListDelivery.Deliver(new[] { scene }, Alice, Xor,
                A2AAgentListDelivery.EnterUpdates(Bob, Alice), _ => queue);
            Assert.That(d, Is.EqualTo(A2AAgentListDelivery.DecisionSendFailed));
        }

        // ---- no session ever formed -> nothing to announce ----------------------------------------

        [Test]
        public void InvitedStateRemoval_YieldsNoLeaveCandidates()
        {
            // Decline / TTL / client-close of an unanswered invitation: MarkGoneSessions removes only
            // Active records, so the handler's LEAVE loop has nothing to send -- by construction.
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-a");     // caller joined, still Invited
            Assert.That(reg.MarkGoneSessions(Alice, null), Is.Empty);
            Assert.That(reg.Decline(Xor, Bob, out _), Is.True, "the record is gone");
            Assert.That(reg.MarkGoneSessions(Bob, null), Is.Empty);
        }

        [Test]
        public void BothLogout_ReturnsTheSessionWithItsParties()
        {
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-a");
            reg.MarkProvisioned(s.SessionId, Bob, "vs-b");
            Assert.That(reg.MarkGoneSessions(Alice, "vs-a"), Is.Empty);
            List<A2ASession> removed = reg.MarkGoneSessions(Bob, "vs-b");
            Assert.That(removed, Has.Count.EqualTo(1));
            Assert.That(removed[0].OtherParty(Bob), Is.EqualTo(Alice), "the remaining-party lookup the LEAVE needs");
        }
    }
}
