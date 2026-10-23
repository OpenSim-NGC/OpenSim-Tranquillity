/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Conference TEXT -- roster, fan-out, membership, no-echo, and the ring/text discrimination.
 *
 * The discrimination tests are the ones to read first. The sharpest risk is that a conference
 * chat line and a voice ring are the SAME dialog with the SAME fromGroup, so a receiver
 * that guessed would either ring a viewer with somebody's typing or swallow a chat line as a ring.
 * BothDirections_* assert that neither parser can claim the other's message, and they assert it
 * against the REAL GroupVoiceRingTransport rather than a stand-in, because the whole argument for
 * a separate bucket magic is that the deployed ring parser already rejects text unmodified.
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class AdhocConferenceTextTests
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

        /// <summary>Alice opens a conference; Bob and Carol are invited and accept. Stranger is not in it.</summary>
        private static NonSpatialVoiceSession ThreeWay(NonSpatialVoiceSessionEngine e, out UUID sessionId)
        {
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            sessionId = start.Session.SessionId;
            e.Accept(sessionId, Bob, Region, "vs-bob");
            e.Accept(sessionId, Carol, Region, "vs-carol");
            return e.Store.Get(sessionId);
        }

        private sealed class CapturingQueue : IEventQueue
        {
            public readonly List<(UUID SessionId, UUID ToAgent, List<GroupChatListAgentUpdateData> Updates)> Sent = new();

            public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates)
                => Sent.Add((sessionID, toAgent, updates));

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

        // ---- ring / text discrimination, BOTH DIRECTIONS -----------------------------------

        [Test]
        public void BothDirections_TheRingParserRejectsText_AndTheTextParserRejectsARing()
        {
            OSDMap ringBody = new OSDMap { ["session_id"] = OSD.FromUUID(UUID.Random()) };
            GridInstantMessage ring = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, ringBody);
            GridInstantMessage text = AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "hello");

            // The two are indistinguishable on the fields the carrier is chosen by -- which is the
            // whole reason a magic is needed.
            Assert.That(ring.dialog, Is.EqualTo(text.dialog), "both are SessionSend");
            Assert.That(ring.fromGroup, Is.EqualTo(text.fromGroup), "both are fromGroup=false");

            // A ring is not text.
            Assert.That(AdhocTextTransport.TryParse(ring, out _, out _, out _, out _, out _), Is.False,
                "a group voice ring must never be delivered to a viewer as a chat line");

            // Text is not a ring -- and this is asserted against the LIVE, DEPLOYED parser, unchanged.
            Assert.That(GroupVoiceRingTransport.TryParse(text, out _, out _, out _), Is.False,
                "the deployed ring parser already rejects LGVTEXT1 with no change to it");
        }

        [Test]
        public void EachParserStillClaimsItsOwn()
        {
            UUID session = UUID.Random();
            OSDMap ringBody = new OSDMap { ["session_id"] = OSD.FromUUID(session) };
            GridInstantMessage ring = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", session, Region, ringBody);
            GridInstantMessage text = AdhocTextTransport.Build(Bob, Alice, "Alice", session, Region, "hello there");

            Assert.That(GroupVoiceRingTransport.TryParse(ring, out UUID rt, out UUID rg, out OSDMap rb), Is.True);
            Assert.That(rt, Is.EqualTo(Bob));
            Assert.That(rg, Is.EqualTo(session));
            Assert.That(rb, Is.Not.Null);

            Assert.That(AdhocTextTransport.TryParse(text, out UUID tt, out UUID ts, out string msg,
                                                    out string from, out _), Is.True);
            Assert.That(tt, Is.EqualTo(Bob));
            Assert.That(ts, Is.EqualTo(session));
            Assert.That(msg, Is.EqualTo("hello there"));
            Assert.That(from, Is.EqualTo("Alice"));
        }

        [Test]
        public void TheTwoMagicsAreDistinct_AndTextNeverStoresOffline()
        {
            Assert.That(AdhocTextTransport.BucketMagic, Is.Not.EqualTo(GroupVoiceRingTransport.BucketMagic));
            Assert.That(AdhocTextTransport.BucketMagic.StartsWith(GroupVoiceRingTransport.BucketMagic, StringComparison.Ordinal), Is.False);
            Assert.That(GroupVoiceRingTransport.BucketMagic.StartsWith(AdhocTextTransport.BucketMagic, StringComparison.Ordinal), Is.False);
            // stated at the source as well as relied on in the two offline allowlists
            Assert.That(AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "x").offline,
                Is.EqualTo(0), "a conference line must never be stored and replayed at next login");
        }

        [Test]
        public void GarbageAndForeignBucketsAreNotOurs()
        {
            GridInstantMessage m = AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "hi");
            m.binaryBucket = System.Text.Encoding.UTF8.GetBytes("LGVTEXT1{not json");
            Assert.That(AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _), Is.False);

            m.binaryBucket = System.Text.Encoding.UTF8.GetBytes("SOMETHINGELSE{}");
            Assert.That(AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _), Is.False);

            m.binaryBucket = null;
            Assert.That(AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _), Is.False);

            GridInstantMessage grouped = AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "hi");
            grouped.fromGroup = true;
            Assert.That(AdhocTextTransport.TryParse(grouped, out _, out _, out _, out _, out _), Is.False,
                "fromGroup=true is group chat's combination, never ours");
        }

        // ---- fan-out, membership, no echo ---------------------------------------------

        [Test]
        public void EveryOtherSeatHolderReceivesTheLine()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Alice, "hello all",
                                                 out AdhocTextPlan plan, out string decision), Is.True);
            Assert.That(decision, Is.EqualTo(AdhocTextSession.DecisionDelivered));
            Assert.That(plan.Recipients, Is.EquivalentTo(new[] { Bob, Carol }));
        }

        [Test]
        public void TheSenderNeverReceivesTheirOwnMessageBack()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            foreach (UUID speaker in new[] { Alice, Bob, Carol })
            {
                Assert.That(AdhocTextSession.TryPlan(e, true, session, speaker, "mine",
                                                     out AdhocTextPlan plan, out _), Is.True);
                Assert.That(plan.Recipients, Does.Not.Contain(speaker),
                    "no echo -- the viewer already rendered what it sent");
                Assert.That(plan.Recipients.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void ANonMemberIsRefusedAndTheRefusalIsGreppable()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Stranger, "let me in",
                                                 out AdhocTextPlan plan, out string decision), Is.True,
                "it IS our session, so this is a refusal and not a pass-through");
            Assert.That(plan, Is.Null);
            Assert.That(decision, Is.EqualTo(AdhocTextSession.DecisionNotMember));
            Assert.That(AdhocTextSession.Line(Stranger, session, decision),
                Does.Contain("[ADHOC TEXT]").And.Contain("adhoc-text-refused-not-member"));
        }

        [Test]
        public void AnInviteeWhoNeverAcceptedCannotType_AndNeitherCanSomeoneWhoLeft()
        {
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            UUID session = start.Session.SessionId;
            e.Accept(session, Bob, Region, "vs-bob");
            // Carol is Invited only -- rung, never answered.

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Carol, "hi", out AdhocTextPlan p1, out string d1), Is.True);
            Assert.That(p1, Is.Null);
            Assert.That(d1, Is.EqualTo(AdhocTextSession.DecisionNotMember),
                "an invitation is not a write capability");

            e.Depart(session, Bob, DepartureReason.ChatLeave);
            Assert.That(AdhocTextSession.TryPlan(e, true, session, Bob, "still here?", out AdhocTextPlan p2, out string d2), Is.True);
            Assert.That(p2, Is.Null);
            Assert.That(d2, Is.EqualTo(AdhocTextSession.DecisionNotMember));
        }

        [Test]
        public void AGroupOrUnknownSessionIsNotOurs_SoGroupTextIsUntouched()
        {
            var e = Engine();
            UUID group = UUID.Random();
            e.Start(NonSpatialSessionType.Group, Alice, group, UUID.Zero, Region);

            Assert.That(AdhocTextSession.TryPlan(e, true, group, Alice, "group line",
                                                 out _, out _), Is.False,
                "a GROUP id must fall straight through to GroupsMessagingModule");
            Assert.That(AdhocTextSession.TryPlan(e, true, UUID.Random(), Alice, "nowhere",
                                                 out _, out _), Is.False);
        }

        [Test]
        public void AConferenceOfOneHasNobodyToTellButIsStillOurs()
        {
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID>());

            Assert.That(AdhocTextSession.TryPlan(e, true, start.Session.SessionId, Alice, "anyone?",
                                                 out AdhocTextPlan plan, out string decision), Is.True);
            Assert.That(decision, Is.EqualTo(AdhocTextSession.DecisionNoRecipients));
            Assert.That(plan.Recipients, Is.Empty);
        }

        [Test]
        public void AnEmptyLineIsRefusedRatherThanFannedOut()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.That(AdhocTextSession.TryPlan(e, true, session, Alice, "", out AdhocTextPlan plan, out string decision), Is.True);
            Assert.That(plan, Is.Null);
            Assert.That(decision, Is.EqualTo(AdhocTextSession.DecisionEmpty));
        }

        [Test]
        public void TheFlagOffRefusesInsteadOfDelivering()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.That(AdhocTextSession.TryPlan(e, false, session, Alice, "hi", out AdhocTextPlan plan, out string decision), Is.True);
            Assert.That(plan, Is.Null);
            Assert.That(decision, Is.EqualTo(AdhocTextSession.DecisionDisabled));
        }

        [Test]
        public void TheViewerMessageCarriesTheSessionAndNotTheCarrierMagic()
        {
            UUID session = UUID.Random();
            GridInstantMessage m = AdhocTextSession.BuildForViewer(Bob, Alice, "Alice Bazar", session, Region, "hello");

            Assert.That(m.dialog, Is.EqualTo(AdhocTextSession.DialogSessionSend));
            Assert.That(new UUID(m.imSessionID), Is.EqualTo(session), "the viewer keys its panel off this");
            Assert.That(new UUID(m.toAgentID), Is.EqualTo(Bob));
            Assert.That(m.message, Is.EqualTo("hello"));
            Assert.That(m.fromGroup, Is.False);
            Assert.That(m.offline, Is.EqualTo(0));
            string bucket = System.Text.Encoding.UTF8.GetString(m.binaryBucket).TrimEnd('\0');
            Assert.That(bucket, Is.EqualTo(AdhocTextSession.DefaultSessionName),
                "the session name, as the group path puts its group name there -- never the carrier magic");
            Assert.That(bucket, Does.Not.Contain(AdhocTextTransport.BucketMagic));
        }

        [Test]
        public void TheCrossInstanceRoundTripPreservesTheLine()
        {
            UUID session = UUID.Random();
            GridInstantMessage carried = AdhocTextTransport.Build(Carol, Alice, "Alice Bazar", session, Region,
                                                                  "cross the host", "Conference");
            Assert.That(AdhocTextTransport.TryParse(carried, out UUID target, out UUID sid, out string msg,
                                                    out string from, out string name), Is.True);
            Assert.That(target, Is.EqualTo(Carol));
            Assert.That(sid, Is.EqualTo(session));
            Assert.That(msg, Is.EqualTo("cross the host"));
            Assert.That(from, Is.EqualTo("Alice Bazar"));
            Assert.That(name, Is.EqualTo("Conference"));

            // and what the far side then hands the viewer is the same line, with no magic on it
            GridInstantMessage rebuilt = AdhocTextSession.BuildForViewer(target, Alice, from, sid, Region, msg, name);
            Assert.That(rebuilt.message, Is.EqualTo(carried.message));
            Assert.That(new UUID(rebuilt.imSessionID), Is.EqualTo(new UUID(carried.imSessionID)));
        }

        // ---- the roster --------------------------------------------------------------------

        [Test]
        public void AJoinerGetsTheWholeRoomAndTheRoomGetsTheJoiner()
        {
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            UUID session = start.Session.SessionId;
            e.Accept(session, Bob, Region, "vs-bob");
            e.Accept(session, Carol, Region, "vs-carol");

            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            SceneHelpers.AddScenePresence(scene, Bob);
            SceneHelpers.AddScenePresence(scene, Carol);
            var q = new CapturingQueue();

            List<string> lines = AdhocConferenceRoster.SendJoin(new[] { scene }, e.Store.Get(session), Carol, _ => q);

            Assert.That(q.Sent.Count, Is.EqualTo(3), "the joiner, plus one send per other seat holder");

            var toCarol = q.Sent.Find(s => s.ToAgent == Carol);
            Assert.That(toCarol.Updates.Count, Is.EqualTo(3),
                "the joiner's panel gets the WHOLE roster at once, its own entry included");
            Assert.That(toCarol.SessionId, Is.EqualTo(session));

            foreach (UUID other in new[] { Alice, Bob })
            {
                var sent = q.Sent.Find(s => s.ToAgent == other);
                Assert.That(sent.Updates.Count, Is.EqualTo(1), "everybody else gets just the new name");
                Assert.That(sent.Updates[0].agentID, Is.EqualTo(Carol));
                Assert.That(sent.Updates[0].enterOrLeave, Is.True, "ENTER");
            }

            foreach (var sent in q.Sent)
                foreach (GroupChatListAgentUpdateData u in sent.Updates)
                    Assert.That(u.canVoice, Is.True,
                        "cv:true by construction -- the 1-arg ctor's FALSE default is known to hang a call up");

            Assert.That(lines, Has.Count.EqualTo(3));
            Assert.That(lines[0], Does.Contain("[ADHOC ROSTER]").And.Contain("transition=ENTER"));
        }

        [Test]
        public void ADepartureTellsEveryRemainingMemberAndNotTheDepartedOne()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            SceneHelpers.AddScenePresence(scene, Bob);
            SceneHelpers.AddScenePresence(scene, Carol);
            var q = new CapturingQueue();

            // the engine records the departure FIRST, which is what takes Bob out of SeatHolders
            e.Depart(session, Bob, DepartureReason.VoiceTeardown);
            List<string> lines = AdhocConferenceRoster.SendLeave(new[] { scene }, e.Store.Get(session), Bob, _ => q);

            Assert.That(q.Sent.Count, Is.EqualTo(2), "Alice and Carol -- never Bob");
            Assert.That(q.Sent.Exists(s => s.ToAgent == Bob), Is.False);
            foreach (var sent in q.Sent)
            {
                Assert.That(sent.Updates.Count, Is.EqualTo(1));
                Assert.That(sent.Updates[0].agentID, Is.EqualTo(Bob));
                Assert.That(sent.Updates[0].enterOrLeave, Is.False, "LEAVE");
            }
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines[0], Does.Contain("transition=LEAVE").And.Contain($"about={Bob}"));
        }

        [Test]
        public void TheRosterIsTheSeatListAndNothingElse()
        {
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            UUID session = start.Session.SessionId;
            e.Accept(session, Bob, Region, "vs-bob");
            // Carol invited, never accepted; Stranger never involved at all.

            List<UUID> seats = AdhocConferenceRoster.SeatHolders(e.Store.Get(session));
            Assert.That(seats, Is.EquivalentTo(new[] { Alice, Bob }));
            Assert.That(seats, Does.Not.Contain(Carol), "Invited holds no seat, so it is not on the roster");
            Assert.That(seats, Does.Not.Contain(Stranger));

            e.Decline(session, Carol);
            Assert.That(AdhocConferenceRoster.SeatHolders(e.Store.Get(session)), Is.EquivalentTo(new[] { Alice, Bob }));
        }

        [Test]
        public void AGroupSessionIsNeverGivenAConferenceRoster()
        {
            var e = Engine();
            UUID group = UUID.Random();
            SessionOutcome g = e.Start(NonSpatialSessionType.Group, Alice, group, UUID.Zero, Region);

            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var q = new CapturingQueue();

            Assert.That(AdhocConferenceRoster.SendJoin(new[] { scene }, g.Session, Alice, _ => q), Is.Empty);
            Assert.That(AdhocConferenceRoster.SendLeave(new[] { scene }, g.Session, Alice, _ => q), Is.Empty);
            Assert.That(q.Sent, Is.Empty, "group chat's roster is GroupsMessagingModule's job, not ours");
        }

        [Test]
        public void AnUnreachableMemberIsLoggedRatherThanLost()
        {
            var e = Engine();
            ThreeWay(e, out UUID session);

            // no scenes at all: nobody is reachable on this instance
            List<string> lines = AdhocConferenceRoster.SendJoin(Array.Empty<Scene>(), e.Store.Get(session), Carol);

            Assert.That(lines, Has.Count.EqualTo(3), "one line per intended recipient, reachable or not");
            foreach (string l in lines)
                Assert.That(l, Does.Contain(A2AAgentListDelivery.DecisionNoPresence),
                    "their roster arrives from THEIR instance; this one says plainly that it could not send");
        }
    }
}
