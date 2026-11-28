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

        [Fact(Timeout = 60000)]
        public void EveryUpdateTheDeliveryCanProduce_HasCanVoiceTrue()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            foreach (GroupChatListAgentUpdateData u in A2AAgentListDelivery.EnterUpdates(Bob, Alice))
            {
                Assert.True(u.canVoice, "can_voice_chat:false hangs up a P2P call (llimview.cpp:4366-4382)");
                Assert.False(u.isModerator);
                Assert.False(u.mutedText);
                Assert.True(u.enterOrLeave, "ENTER");
            }
            foreach (GroupChatListAgentUpdateData u in A2AAgentListDelivery.LeaveUpdates(Alice))
            {
                Assert.True(u.canVoice, "true even on LEAVE: the flag must never read as a decline");
                Assert.False(u.enterOrLeave, "LEAVE");
            }
            Assert.True(A2AAgentListDelivery.Update(Alice, true).canVoice, "the single seam every list goes through hardcodes cv:true (the group 1-arg ctor defaults FALSE)");
        }

        [Fact(Timeout = 60000)]
        public void EnterUpdates_CarryOtherThenSelf()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            List<GroupChatListAgentUpdateData> ups = A2AAgentListDelivery.EnterUpdates(Bob, Alice);
            {
                var assertedValue = ups.Count;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "the other party plus, per the group pattern, the recipient's own entry");
            }
            Assert.Equal(Bob, ups[0].agentID);
            Assert.Equal(Alice, ups[1].agentID);
        }

        // ---- delivery ------------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ActivePair_BothPartiesReceive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            SceneHelpers.AddScenePresence(scene, Bob);
            var queue = new CapturingAgentListQueue();
            A2ASession s = LiveSession();

            List<string> lines = A2AAgentListDelivery.SendActivePair(new[] { scene }, s, _ => queue);

            Assert.Equal(2, queue.Sent.Count);
            {
                var assertedValue = queue.Sent[0].ToAgent;
                TestAssert.WithMessage(() => Assert.Equal(Alice, assertedValue), "caller first");
            }
            Assert.Equal(Xor, queue.Sent[0].SessionId);
            {
                var assertedValue = queue.Sent[0].Updates[0].agentID;
                TestAssert.WithMessage(() => Assert.Equal(Bob, assertedValue), "the caller learns about the callee");
            }
            Assert.Equal(Bob, queue.Sent[1].ToAgent);
            Assert.Equal(Alice, queue.Sent[1].Updates[0].agentID);
            foreach (var sent in queue.Sent)
                foreach (GroupChatListAgentUpdateData u in sent.Updates)
                    Assert.True(u.canVoice);
            Assert.Equal(2, lines.Count());
            {
                var assertedValue = lines[0];
                Assert.Contains("[A2A AGENTLIST]", assertedValue);
                Assert.Contains($"agent={Alice}", assertedValue);
                Assert.Contains("transition=ENTER", assertedValue);
                Assert.Contains($"about={Bob}", assertedValue);
                Assert.Contains("decision=sent", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void Leave_GoesToTheRemainingParty()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Bob);           // only the remaining party is present
            var queue = new CapturingAgentListQueue();
            A2ASession s = LiveSession();

            string line = A2AAgentListDelivery.SendLeave(new[] { scene }, s, Alice, _ => queue);

            Assert.Single(queue.Sent);
            Assert.Equal(Bob, queue.Sent[0].ToAgent);
            Assert.Single(queue.Sent[0].Updates);
            Assert.Equal(Alice, queue.Sent[0].Updates[0].agentID);
            Assert.False(queue.Sent[0].Updates[0].enterOrLeave);
            {
                var assertedValue = line;
                Assert.Contains($"agent={Bob}", assertedValue);
                Assert.Contains("transition=LEAVE", assertedValue);
                Assert.Contains($"about={Alice}", assertedValue);
                Assert.Contains("decision=sent", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void UnreachableRecipient_IsToleratedAndInstrumented()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();       // nobody present
            var queue = new CapturingAgentListQueue();
            A2ASession s = LiveSession();

            List<string> lines = A2AAgentListDelivery.SendActivePair(new[] { scene }, s, _ => queue);
            Assert.Empty(queue.Sent);
            Assert.Contains("decision=unreachable(no-presence)", lines[0]);

            string leave = A2AAgentListDelivery.SendLeave(new[] { scene }, s, Alice, _ => queue);
            Assert.Contains("decision=unreachable(no-presence)", leave);
        }

        [Fact(Timeout = 60000)]
        public void ThrowingQueue_NeverPropagates()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var queue = new CapturingAgentListQueue { Throw = true };
            A2ASession s = LiveSession();
            string d = A2AAgentListDelivery.Deliver(new[] { scene }, Alice, Xor,
                A2AAgentListDelivery.EnterUpdates(Bob, Alice), _ => queue);
            Assert.Equal(A2AAgentListDelivery.DecisionSendFailed, d);
        }

        // ---- no session ever formed -> nothing to announce ----------------------------------------

        [Fact(Timeout = 60000)]
        public void InvitedStateRemoval_YieldsNoLeaveCandidates()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Decline / TTL / client-close of an unanswered invitation: MarkGoneSessions removes only
            // Active records, so the handler's LEAVE loop has nothing to send -- by construction.
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-a");     // caller joined, still Invited
            Assert.Empty(reg.MarkGoneSessions(Alice, null));
            Assert.True(reg.Decline(Xor, Bob, out _), "the record is gone");
            Assert.Empty(reg.MarkGoneSessions(Bob, null));
        }

        [Fact(Timeout = 60000)]
        public void BothLogout_ReturnsTheSessionWithItsParties()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var reg = new A2ARegistry();
            A2ASession s = reg.Record(Alice, Bob, out _);
            reg.IssueToken(s.SessionId, Alice);
            reg.MarkProvisioned(s.SessionId, Alice, "vs-a");
            reg.MarkProvisioned(s.SessionId, Bob, "vs-b");
            Assert.Empty(reg.MarkGoneSessions(Alice, "vs-a"));
            List<A2ASession> removed = reg.MarkGoneSessions(Bob, "vs-b");
            Assert.Single(removed);
            {
                var assertedValue = removed[0].OtherParty(Bob);
                TestAssert.WithMessage(() => Assert.Equal(Alice, assertedValue), "the remaining-party lookup the LEAVE needs");
            }
        }
    }
}
