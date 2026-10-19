/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The three membership lifecycles, independently and in every combination.
 *
 * The rule is blunt: THREE SEPARATE LIFECYCLES, and nobody may conflate
 * them: (1) CHAT MEMBERSHIP ends with UDP IM_SESSION_LEAVE; (2) an OUTSTANDING INVITATION ends
 * with the cap methods; (3) a VOICE CONNECTION ends with the provision teardown. An agent can hold
 * any one without the others.
 *
 * Chat membership and the voice seat are separate bits. Folding them into one would leave two of the
 * eight combinations below unrepresentable, and one of those (in the chat, no voice) is the ordinary
 * case of hanging up a call and carrying on typing.
 *
 * TheEightCombinations is the test to read first: it walks the whole product of the three axes.
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class AdhocChatLifecycleTests
    {
        private const string Grid = "example-grid";

        private static readonly UUID Alice = new UUID("11111111-1111-1111-1111-111111111111");
        private static readonly UUID Bob = new UUID("22222222-2222-2222-2222-222222222222");
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");
        private static readonly UUID Stranger = new UUID("99999999-9999-9999-9999-999999999999");
        private static readonly UUID Region = new UUID("44444444-4444-4444-4444-444444444444");

        private static NonSpatialVoiceSessionEngine Engine()
            => new NonSpatialVoiceSessionEngine(
                new InMemoryNonSpatialSessionStore(),
                new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission(),
                                             new GroupAdmission((a, g) => true, (a, g) => true) },
                Grid);

        private static NonSpatialVoiceSession ThreeWay(NonSpatialVoiceSessionEngine e, out UUID sessionId)
        {
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            sessionId = start.Session.SessionId;
            e.Accept(sessionId, Bob, Region, "vs-bob");
            e.Accept(sessionId, Carol, Region, "vs-carol");
            return e.Store.Get(sessionId);
        }

        private static (bool InChat, bool Seated, bool Invited) State(NonSpatialVoiceSessionEngine e, UUID s, UUID a)
        {
            NonSpatialMember m = e.Store.Get(s)?.Find(a);
            if (m is null) return (false, false, false);
            return (m.InChat, m.HoldsSeat, m.State == MemberState.Invited);
        }

        // ---- the dialog value, measured rather than remembered ---------------------------------

        [Test]
        public void SessionDropAndSessionLeaveAreTheSameDialogValue()
        {
            Assert.That(AdhocChatMembership.DialogSessionLeave, Is.EqualTo(18),
                "libomv SessionDrop == the viewer's IM_SESSION_LEAVE (llinstantmessage.h:112)");
            Assert.That((byte)OpenMetaverse.InstantMessageDialog.SessionDrop, Is.EqualTo((byte)18),
                "asserted against the real enum, not a mirrored constant");
            Assert.That((byte)OpenMetaverse.InstantMessageDialog.SessionAdd, Is.EqualTo((byte)13),
                "SessionAdd is IM_SESSION_INVITE, a viewer-side session TYPE the viewer never sends");
            Assert.That(AdhocChatMembership.DialogSessionAddUnused, Is.EqualTo(13));
            Assert.That(AdhocChatMembership.DialogSessionLeave, Is.Not.EqualTo(AdhocTextSession.DialogSessionSend));
        }

        // ---- lifecycle (1) alone: chat leave never touches voice --------------------------------

        [Test]
        public void AChatLeaveRemovesTheMemberFromTheRosterAndKeepsTheirVoiceSeat()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);
            int seatsBefore = e.Store.Get(session).SeatsHeld;

            Assert.That(AdhocChatMembership.TryHandleLeave(e, true, session, Bob,
                            out string decision, out bool stillSeated), Is.True);

            Assert.That(decision, Is.EqualTo(AdhocChatMembership.DecisionLeft));
            Assert.That(stillSeated, Is.True, "the whole point: leaving the chat does not hang up the call");
            Assert.That(e.Store.Get(session).SeatsHeld, Is.EqualTo(seatsBefore));
            Assert.That(State(e, session, Bob), Is.EqualTo((false, true, false)));
            Assert.That(AdhocConferenceRoster.Roster(e.Store.Get(session)), Is.EquivalentTo(new[] { Alice, Carol }));
            Assert.That(AdhocConferenceRoster.SeatHolders(e.Store.Get(session)),
                Is.EquivalentTo(new[] { Alice, Bob, Carol }), "the seat list is unchanged");
        }

        [Test]
        public void AVoiceTeardownKeepsTheMemberInTheChat()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            e.Depart(session, Bob, DepartureReason.VoiceTeardown);

            Assert.That(State(e, session, Bob), Is.EqualTo((true, false, false)),
                "still in the chat with voice hung up");
            Assert.That(AdhocConferenceRoster.Roster(e.Store.Get(session)),
                Is.EquivalentTo(new[] { Alice, Bob, Carol }), "the panel keeps the name");
            Assert.That(AdhocConferenceRoster.SeatHolders(e.Store.Get(session)),
                Is.EquivalentTo(new[] { Alice, Carol }));
        }

        [Test]
        public void SomeoneWhoLeftTheChatButKeptTheirSeatCanNoLongerType_AndGetsNoLines()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);
            e.LeaveChat(session, Bob);

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Bob, "still here",
                            out AdhocTextPlan mine, out string d), Is.True);
            Assert.That(mine, Is.Null);
            Assert.That(d, Is.EqualTo(AdhocTextSession.DecisionNotMember));

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Alice, "anyone?",
                            out AdhocTextPlan theirs, out _), Is.True);
            Assert.That(theirs.Recipients, Is.EquivalentTo(new[] { Carol }), "Bob left the conversation");
        }

        [Test]
        public void SomeoneWhoHungUpVoiceIsStillInTheConversation()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);
            e.Depart(session, Bob, DepartureReason.VoiceTeardown);

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Bob, "can still type",
                            out AdhocTextPlan mine, out _), Is.True);
            Assert.That(mine, Is.Not.Null, "hanging up must not silence a member");
            Assert.That(mine.Recipients, Is.EquivalentTo(new[] { Alice, Carol }));

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Alice, "hi",
                            out AdhocTextPlan theirs, out _), Is.True);
            Assert.That(theirs.Recipients, Does.Contain(Bob), "and must not stop them hearing");
        }

        // ---- the whole product of the three axes -----------------------------------------------

        [Test]
        public void TheEightCombinations()
        {
            // (invited, in chat, seated) -- every state an agent can be in, reached by the real
            // transitions rather than by poking the model.
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            UUID s = start.Session.SessionId;

            // 000 never involved
            Assert.That(State(e, s, Stranger), Is.EqualTo((false, false, false)));

            // 100 invited, not answered -- holds no seat and is not in the conversation
            Assert.That(State(e, s, Bob), Is.EqualTo((false, false, true)));

            // 011 accepted: in the chat AND seated
            e.Accept(s, Bob, Region, "vs-bob");
            Assert.That(State(e, s, Bob), Is.EqualTo((true, true, false)));

            // 010 hung up voice, still typing  (lifecycle 3 only)
            e.Depart(s, Bob, DepartureReason.VoiceTeardown);
            Assert.That(State(e, s, Bob), Is.EqualTo((true, false, false)));

            // 001 closed the window, still in the call  (lifecycle 1 only)
            e.Accept(s, Bob, Region, "vs-bob");          // back in both
            e.LeaveChat(s, Bob);
            Assert.That(State(e, s, Bob), Is.EqualTo((false, true, false)));

            // 000 again, by the presence backstop -- which ends everything at once
            e.Depart(s, Bob, DepartureReason.PresenceLost);
            Assert.That(State(e, s, Bob), Is.EqualTo((false, false, false)));

            // 100 again: a fresh invite reopens the invitation without granting anything else
            e.Invite(s, Alice, Bob);
            Assert.That(State(e, s, Bob), Is.EqualTo((false, false, true)));

            // declined: no seat, no chat, no outstanding invitation
            e.Decline(s, Bob);
            Assert.That(State(e, s, Bob), Is.EqualTo((false, false, false)));
            Assert.That(e.Store.Get(s).Find(Bob).State, Is.EqualTo(MemberState.Declined));
        }

        [Test]
        public void EachLifecycleIsIdempotentAndDoesNotDisturbTheOthers()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.That(e.LeaveChat(session, Bob).Decision, Is.EqualTo("chat-left"));
            Assert.That(e.LeaveChat(session, Bob).Decision, Is.EqualTo("chat-leave-idempotent"));
            Assert.That(State(e, session, Bob), Is.EqualTo((false, true, false)),
                "a second leave must not take the seat as a consolation prize");

            Assert.That(e.Depart(session, Bob, DepartureReason.VoiceTeardown).Ok, Is.True);
            Assert.That(e.Depart(session, Bob, DepartureReason.VoiceTeardown).Decision, Is.EqualTo("depart-idempotent"));
            Assert.That(State(e, session, Bob), Is.EqualTo((false, false, false)));
        }

        [Test]
        public void LeavingAConferenceYouAreNotInIsANoOpNotAFailure()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.That(AdhocChatMembership.TryHandleLeave(e, true, session, Stranger,
                            out string decision, out bool seated), Is.True);
            Assert.That(decision, Is.EqualTo(AdhocChatMembership.DecisionNotMember));
            Assert.That(seated, Is.False);
            Assert.That(AdhocConferenceRoster.Roster(e.Store.Get(session)).Count, Is.EqualTo(3),
                "nobody else is disturbed");
        }

        [Test]
        public void AGroupOrP2PLeaveIsNotOurs()
        {
            var e = Engine();
            UUID group = UUID.Random();
            e.Start(NonSpatialSessionType.Group, Alice, group, UUID.Zero, Region);

            Assert.That(AdhocChatMembership.TryHandleLeave(e, true, group, Alice, out _, out _), Is.False,
                "a group chat leave belongs to GroupsMessagingModule");
            Assert.That(AdhocChatMembership.TryHandleLeave(e, true, UUID.Random(), Alice, out _, out _), Is.False);
        }

        [Test]
        public void TheFlagOffRefusesTheLeaveInsteadOfSilentlyApplyingIt()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.That(AdhocChatMembership.TryHandleLeave(e, false, session, Bob,
                            out string decision, out _), Is.True);
            Assert.That(decision, Is.EqualTo(AdhocChatMembership.DecisionDisabled));
            Assert.That(State(e, session, Bob), Is.EqualTo((true, true, false)), "nothing moved");
        }

        // ---- the roster follows the chat axis, and the instrument says so -----------------------

        [Test]
        public void TheRosterUpdateOnAChatLeaveReachesEveryoneRemaining()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            SceneHelpers.AddScenePresence(scene, Bob);
            SceneHelpers.AddScenePresence(scene, Carol);
            var q = new CapturingQueue();

            e.LeaveChat(session, Bob);
            List<string> lines = AdhocConferenceRoster.SendLeave(new[] { scene }, e.Store.Get(session), Bob, _ => q);

            Assert.That(q.Sent.Count, Is.EqualTo(2), "Alice and Carol");
            Assert.That(q.Sent.Exists(x => x.ToAgent == Bob), Is.False);
            foreach (var sent in q.Sent)
            {
                Assert.That(sent.Updates[0].agentID, Is.EqualTo(Bob));
                Assert.That(sent.Updates[0].enterOrLeave, Is.False, "LEAVE");
            }
            Assert.That(lines, Has.Count.EqualTo(2));
        }

        [Test]
        public void TheInstrumentLineRecordsThatTheSeatSurvived()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);
            AdhocChatMembership.TryHandleLeave(e, true, session, Bob, out string decision, out bool seated);

            string line = AdhocChatMembership.Line(Bob, session, decision, seated);
            Assert.That(line, Does.Contain("[ADHOC CHAT]")
                                 .And.Contain("adhoc-chat-left")
                                 .And.Contain("voice_seat=kept"),
                "the log is where a conflated lifecycle would be visible first");
        }

        /// <summary>Shared with AdhocConferenceTextTests; kept local so the fixtures stay independent.</summary>
        private sealed class CapturingQueue : IEventQueue
        {
            public readonly List<(UUID SessionId, UUID ToAgent, List<GroupChatListAgentUpdateData> Updates)> Sent = new();

            public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates)
                => Sent.Add((sessionID, toAgent, updates));

            public byte[] BuildEvent(string eventName, OpenMetaverse.StructuredData.OSD eventBody) => throw new NotSupportedException();
            public bool Enqueue(byte[] o, UUID avatarID) => throw new NotSupportedException();
            public bool Enqueue(OpenMetaverse.StructuredData.OSD o, UUID avatarID) => throw new NotSupportedException();
            public bool Enqueue(osUTF8 o, UUID avatarID) => throw new NotSupportedException();
            public void EnableSimulator(ulong handle, System.Net.IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void EstablishAgentCommunication(UUID avatarID, System.Net.IPEndPoint endPoint, string capsPath, ulong regionHandle, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void TeleportFinishEvent(ulong regionHandle, byte simAccess, System.Net.IPEndPoint regionExternalEndPoint, uint locationID, uint flags, string capsURL, UUID agentID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, System.Net.IPEndPoint newRegionExternalEndPoint, string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY) => throw new NotSupportedException();
            public void ChatterboxInvitation(UUID sessionID, string sessionName, UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog, uint timeStamp, bool offline, int parentEstateID, Vector3 position, uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket) => throw new NotSupportedException();
            public void ChatterBoxSessionStartReply(UUID sessionID, string sessionName, int type, bool voiceEnabled, bool voiceModerated, UUID tmpSessionID, bool sucess, string error, UUID toAgent) => throw new NotSupportedException();
            public void ChatterBoxForceClose(UUID toAgent, UUID sessionID, string reason) => throw new NotSupportedException();
            public void GroupMembershipData(UUID receiverAgent, OpenSim.Framework.GroupMembershipData[] data) => throw new NotSupportedException();
            public void ScriptRunningEvent(UUID objectID, UUID itemID, bool running, UUID avatarID) => throw new NotSupportedException();
            public void partPhysicsProperties(uint localID, byte physhapetype, float density, float friction, float bounce, float gravmod, UUID avatarID) => throw new NotSupportedException();
            public void WindlightRefreshEvent(int interpolate, UUID avatarID) => throw new NotSupportedException();
            public void SendEnvironmentUpdate(UUID experience_id, UUID agent_id, OpenSim.Framework.EnvironmentUpdate update) => throw new NotSupportedException();
            public void SendBulkUpdateInventoryItem(OpenSim.Framework.InventoryItemBase item, UUID avatarID, UUID? transationID = null) => throw new NotSupportedException();
            public osUTF8 StartEvent(string eventName) => throw new NotSupportedException();
            public osUTF8 StartEvent(string eventName, int cap) => throw new NotSupportedException();
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<byte[]> message) => throw new NotSupportedException();
            public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<string> message) => throw new NotSupportedException();
            public byte[] EndEventToBytes(osUTF8 sb) => throw new NotSupportedException();
        }
    }
}
