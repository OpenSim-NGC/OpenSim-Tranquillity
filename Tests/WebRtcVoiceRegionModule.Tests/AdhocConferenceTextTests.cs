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
using Xunit;
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

        [Fact(Timeout = 60000)]
        public void BothDirections_TheRingParserRejectsText_AndTheTextParserRejectsARing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            OSDMap ringBody = new OSDMap { ["session_id"] = OSD.FromUUID(UUID.Random()) };
            GridInstantMessage ring = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, ringBody);
            GridInstantMessage text = AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "hello");

            // The two are indistinguishable on the fields the carrier is chosen by -- which is the
            // whole reason a magic is needed.
            {
                var assertedValue = ring.dialog;
                TestAssert.WithMessage(() => Assert.Equal(text.dialog, assertedValue), "both are SessionSend");
            }
            {
                var assertedValue = ring.fromGroup;
                TestAssert.WithMessage(() => Assert.Equal(text.fromGroup, assertedValue), "both are fromGroup=false");
            }

            // A ring is not text.
            Assert.False(AdhocTextTransport.TryParse(ring, out _, out _, out _, out _, out _), "a group voice ring must never be delivered to a viewer as a chat line");

            // Text is not a ring -- and this is asserted against the LIVE, DEPLOYED parser, unchanged.
            Assert.False(GroupVoiceRingTransport.TryParse(text, out _, out _, out _), "the deployed ring parser already rejects LGVTEXT1 with no change to it");
        }

        [Fact(Timeout = 60000)]
        public void EachParserStillClaimsItsOwn()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID session = UUID.Random();
            OSDMap ringBody = new OSDMap { ["session_id"] = OSD.FromUUID(session) };
            GridInstantMessage ring = GroupVoiceRingTransport.Build(Bob, Alice, "Alice", session, Region, ringBody);
            GridInstantMessage text = AdhocTextTransport.Build(Bob, Alice, "Alice", session, Region, "hello there");

            Assert.True(GroupVoiceRingTransport.TryParse(ring, out UUID rt, out UUID rg, out OSDMap rb));
            Assert.Equal(Bob, rt);
            Assert.Equal(session, rg);
            Assert.NotNull(rb);

            Assert.True(AdhocTextTransport.TryParse(text, out UUID tt, out UUID ts, out string msg,
                                                    out string from, out _));
            Assert.Equal(Bob, tt);
            Assert.Equal(session, ts);
            Assert.Equal("hello there", msg);
            Assert.Equal("Alice", from);
        }

        [Fact(Timeout = 60000)]
        public void TheTwoMagicsAreDistinct_AndTextNeverStoresOffline()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.NotEqual(GroupVoiceRingTransport.BucketMagic, AdhocTextTransport.BucketMagic);
            Assert.False(AdhocTextTransport.BucketMagic.StartsWith(GroupVoiceRingTransport.BucketMagic, StringComparison.Ordinal));
            Assert.False(GroupVoiceRingTransport.BucketMagic.StartsWith(AdhocTextTransport.BucketMagic, StringComparison.Ordinal));
            // stated at the source as well as relied on in the two offline allowlists
            {
                var assertedValue = AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "x").offline;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "a conference line must never be stored and replayed at next login");
            }
        }

        [Fact(Timeout = 60000)]
        public void GarbageAndForeignBucketsAreNotOurs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            GridInstantMessage m = AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "hi");
            m.binaryBucket = System.Text.Encoding.UTF8.GetBytes("LGVTEXT1{not json");
            Assert.False(AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _));

            m.binaryBucket = System.Text.Encoding.UTF8.GetBytes("SOMETHINGELSE{}");
            Assert.False(AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _));

            m.binaryBucket = null;
            Assert.False(AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _));

            GridInstantMessage grouped = AdhocTextTransport.Build(Bob, Alice, "Alice", UUID.Random(), Region, "hi");
            grouped.fromGroup = true;
            Assert.False(AdhocTextTransport.TryParse(grouped, out _, out _, out _, out _, out _), "fromGroup=true is group chat's combination, never ours");
        }

        // ---- fan-out, membership, no echo ---------------------------------------------

        [Fact(Timeout = 60000)]
        public void EveryOtherSeatHolderReceivesTheLine()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.True(AdhocTextSession.TryPlan(e, true, session, Alice, "hello all",
                                                 out AdhocTextPlan plan, out string decision));
            Assert.Equal(AdhocTextSession.DecisionDelivered, decision);
            Assert.Equivalent(new[] { Bob, Carol }, plan.Recipients, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void TheSenderNeverReceivesTheirOwnMessageBack()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            ThreeWay(e, out UUID session);

            foreach (UUID speaker in new[] { Alice, Bob, Carol })
            {
                Assert.True(AdhocTextSession.TryPlan(e, true, session, speaker, "mine",
                                                     out AdhocTextPlan plan, out _));
                {
                    var assertedValue = plan.Recipients;
                    TestAssert.WithMessage(() => Assert.DoesNotContain(speaker, assertedValue), "no echo -- the viewer already rendered what it sent");
                }
                Assert.Equal(2, plan.Recipients.Count);
            }
        }

        [Fact(Timeout = 60000)]
        public void ANonMemberIsRefusedAndTheRefusalIsGreppable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.True(AdhocTextSession.TryPlan(e, true, session, Stranger, "let me in", out AdhocTextPlan plan, out string decision), "it IS our session, so this is a refusal and not a pass-through");
            Assert.Null(plan);
            Assert.Equal(AdhocTextSession.DecisionNotMember, decision);
            {
                var assertedValue = AdhocTextSession.Line(Stranger, session, decision);
                Assert.Contains("[ADHOC TEXT]", assertedValue);
                Assert.Contains("adhoc-text-refused-not-member", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void AnInviteeWhoNeverAcceptedCannotType_AndNeitherCanSomeoneWhoLeft()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            UUID session = start.Session.SessionId;
            e.Accept(session, Bob, Region, "vs-bob");
            // Carol is Invited only -- rung, never answered.

            Assert.True(AdhocTextSession.TryPlan(e, true, session, Carol, "hi", out AdhocTextPlan p1, out string d1));
            Assert.Null(p1);
            {
                var assertedValue = d1;
                TestAssert.WithMessage(() => Assert.Equal(AdhocTextSession.DecisionNotMember, assertedValue), "an invitation is not a write capability");
            }

            e.Depart(session, Bob, DepartureReason.ChatLeave);
            Assert.True(AdhocTextSession.TryPlan(e, true, session, Bob, "still here?", out AdhocTextPlan p2, out string d2));
            Assert.Null(p2);
            Assert.Equal(AdhocTextSession.DecisionNotMember, d2);
        }

        [Fact(Timeout = 60000)]
        public void AGroupOrUnknownSessionIsNotOurs_SoGroupTextIsUntouched()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            UUID group = UUID.Random();
            e.Start(NonSpatialSessionType.Group, Alice, group, UUID.Zero, Region);

            Assert.False(AdhocTextSession.TryPlan(e, true, group, Alice, "group line", out _, out _), "a GROUP id must fall straight through to GroupsMessagingModule");
            Assert.False(AdhocTextSession.TryPlan(e, true, UUID.Random(), Alice, "nowhere",
                                                 out _, out _));
        }

        [Fact(Timeout = 60000)]
        public void AConferenceOfOneHasNobodyToTellButIsStillOurs()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID>());

            Assert.True(AdhocTextSession.TryPlan(e, true, start.Session.SessionId, Alice, "anyone?",
                                                 out AdhocTextPlan plan, out string decision));
            Assert.Equal(AdhocTextSession.DecisionNoRecipients, decision);
            Assert.Empty(plan.Recipients);
        }

        [Fact(Timeout = 60000)]
        public void AnEmptyLineIsRefusedRatherThanFannedOut()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.True(AdhocTextSession.TryPlan(e, true, session, Alice, "", out AdhocTextPlan plan, out string decision));
            Assert.Null(plan);
            Assert.Equal(AdhocTextSession.DecisionEmpty, decision);
        }

        [Fact(Timeout = 60000)]
        public void TheFlagOffRefusesInsteadOfDelivering()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            ThreeWay(e, out UUID session);

            Assert.True(AdhocTextSession.TryPlan(e, false, session, Alice, "hi", out AdhocTextPlan plan, out string decision));
            Assert.Null(plan);
            Assert.Equal(AdhocTextSession.DecisionDisabled, decision);
        }

        [Fact(Timeout = 60000)]
        public void TheViewerMessageCarriesTheSessionAndNotTheCarrierMagic()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID session = UUID.Random();
            GridInstantMessage m = AdhocTextSession.BuildForViewer(Bob, Alice, "Alice Bazar", session, Region, "hello");

            Assert.Equal(AdhocTextSession.DialogSessionSend, m.dialog);
            {
                var assertedValue = new UUID(m.imSessionID);
                TestAssert.WithMessage(() => Assert.Equal(session, assertedValue), "the viewer keys its panel off this");
            }
            Assert.Equal(Bob, new UUID(m.toAgentID));
            Assert.Equal("hello", m.message);
            Assert.False(m.fromGroup);
            Assert.Equal(0, m.offline);
            string bucket = System.Text.Encoding.UTF8.GetString(m.binaryBucket).TrimEnd('\0');
            {
                var assertedValue = bucket;
                TestAssert.WithMessage(() => Assert.Equal(AdhocTextSession.DefaultSessionName, assertedValue), "the session name, as the group path puts its group name there -- never the carrier magic");
            }
            Assert.DoesNotContain(AdhocTextTransport.BucketMagic, bucket);
        }

        [Fact(Timeout = 60000)]
        public void TheCrossInstanceRoundTripPreservesTheLine()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID session = UUID.Random();
            GridInstantMessage carried = AdhocTextTransport.Build(Carol, Alice, "Alice Bazar", session, Region,
                                                                  "cross the host", "Conference");
            Assert.True(AdhocTextTransport.TryParse(carried, out UUID target, out UUID sid, out string msg,
                                                    out string from, out string name));
            Assert.Equal(Carol, target);
            Assert.Equal(session, sid);
            Assert.Equal("cross the host", msg);
            Assert.Equal("Alice Bazar", from);
            Assert.Equal("Conference", name);

            // and what the far side then hands the viewer is the same line, with no magic on it
            GridInstantMessage rebuilt = AdhocTextSession.BuildForViewer(target, Alice, from, sid, Region, msg, name);
            Assert.Equal(carried.message, rebuilt.message);
            Assert.Equal(new UUID(carried.imSessionID), new UUID(rebuilt.imSessionID));
        }

        // ---- the roster --------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AJoinerGetsTheWholeRoomAndTheRoomGetsTheJoiner()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            {
                var assertedValue = q.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "the joiner, plus one send per other seat holder");
            }

            var toCarol = q.Sent.Find(s => s.ToAgent == Carol);
            {
                var assertedValue = toCarol.Updates.Count;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue), "the joiner's panel gets the WHOLE roster at once, its own entry included");
            }
            Assert.Equal(session, toCarol.SessionId);

            foreach (UUID other in new[] { Alice, Bob })
            {
                var sent = q.Sent.Find(s => s.ToAgent == other);
                {
                    var assertedValue = sent.Updates.Count;
                    TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "everybody else gets just the new name");
                }
                Assert.Equal(Carol, sent.Updates[0].agentID);
                Assert.True(sent.Updates[0].enterOrLeave, "ENTER");
            }

            foreach (var sent in q.Sent)
                foreach (GroupChatListAgentUpdateData u in sent.Updates)
                    Assert.True(u.canVoice, "cv:true by construction -- the 1-arg ctor's FALSE default is known to hang a call up");

            Assert.Equal(3, lines.Count());
            {
                var assertedValue = lines[0];
                Assert.Contains("[ADHOC ROSTER]", assertedValue);
                Assert.Contains("transition=ENTER", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void ADepartureTellsEveryRemainingMemberAndNotTheDepartedOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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

            {
                var assertedValue = q.Sent.Count;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "Alice and Carol -- never Bob");
            }
            Assert.False(q.Sent.Exists(s => s.ToAgent == Bob));
            foreach (var sent in q.Sent)
            {
                Assert.Single(sent.Updates);
                Assert.Equal(Bob, sent.Updates[0].agentID);
                Assert.False(sent.Updates[0].enterOrLeave, "LEAVE");
            }
            Assert.Equal(2, lines.Count());
            {
                var assertedValue = lines[0];
                Assert.Contains("transition=LEAVE", assertedValue);
                Assert.Contains($"about={Bob}", assertedValue);
            }
        }

        [Fact(Timeout = 60000)]
        public void TheRosterIsTheSeatListAndNothingElse()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            SessionOutcome start = e.StartAdhoc(Alice, UUID.Random(), Region, new List<UUID> { Bob, Carol });
            UUID session = start.Session.SessionId;
            e.Accept(session, Bob, Region, "vs-bob");
            // Carol invited, never accepted; Stranger never involved at all.

            List<UUID> seats = AdhocConferenceRoster.SeatHolders(e.Store.Get(session));
            Assert.Equivalent(new[] { Alice, Bob }, seats, strict: true);
            {
                var assertedValue = seats;
                TestAssert.WithMessage(() => Assert.DoesNotContain(Carol, assertedValue), "Invited holds no seat, so it is not on the roster");
            }
            Assert.DoesNotContain(Stranger, seats);

            e.Decline(session, Carol);
            Assert.Equivalent(new[] { Alice, Bob }, AdhocConferenceRoster.SeatHolders(e.Store.Get(session)), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void AGroupSessionIsNeverGivenAConferenceRoster()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            UUID group = UUID.Random();
            SessionOutcome g = e.Start(NonSpatialSessionType.Group, Alice, group, UUID.Zero, Region);

            Scene scene = new SceneHelpers().SetupScene();
            SceneHelpers.AddScenePresence(scene, Alice);
            var q = new CapturingQueue();

            Assert.Empty(AdhocConferenceRoster.SendJoin(new[] { scene }, g.Session, Alice, _ => q));
            Assert.Empty(AdhocConferenceRoster.SendLeave(new[] { scene }, g.Session, Alice, _ => q));
            {
                var assertedValue = q.Sent;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "group chat's roster is GroupsMessagingModule's job, not ours");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnUnreachableMemberIsLoggedRatherThanLost()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var e = Engine();
            ThreeWay(e, out UUID session);

            // no scenes at all: nobody is reachable on this instance
            List<string> lines = AdhocConferenceRoster.SendJoin(Array.Empty<Scene>(), e.Store.Get(session), Carol);

            {
                var assertedValue = lines;
                TestAssert.WithMessage(() => Assert.Equal(3, assertedValue.Count()), "one line per intended recipient, reachable or not");
            }
            foreach (string l in lines)
                {
                    var assertedValue = l;
                    TestAssert.WithMessage(() => Assert.Contains(A2AAgentListDelivery.DecisionNoPresence, assertedValue), "their roster arrives from THEIR instance; this one says plainly that it could not send");
                }
        }
    }
}
