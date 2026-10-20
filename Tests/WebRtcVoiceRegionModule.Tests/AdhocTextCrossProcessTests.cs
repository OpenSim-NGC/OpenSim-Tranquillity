/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A typed conference line reaches every current member wherever they are.
 *
 * Two region-server processes are two modules over two scenes. By default each keeps its own session
 * store (SharedStore = false), so a member who accepts in the second process is recorded only there and
 * the starter's process learns of the acceptance from nothing but the signed carrier. The same flow
 * is run once over ONE store shared by both processes, which is what a grid-wide session store looks like
 * to the module. Carriers move between the processes by hand: what the sender's transfer module recorded
 * is offered to the receiving module's incoming-message handler.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.CrossProcessHarness;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class AdhocTextCrossProcessTests
    {
        private const string Grid = "example-grid";
        private const string Secret = "0123456789abcdef0123456789abcdef-example";
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");
        private static readonly UUID Stranger = new UUID("99999999-9999-9999-9999-999999999999");

        private sealed class Node
        {
            public Process Process;
            public WebRtcVoiceRegionModule Module;
            public NonSpatialVoiceSessionEngine Engine;
            public readonly Dictionary<UUID, List<GridInstantMessage>> Heard = new();

            public void Listen(UUID agent)
            {
                List<GridInstantMessage> heard = new();
                Heard[agent] = heard;
                ((TestClient)Process.Scene.GetScenePresence(agent).ControllingClient).OnReceivedInstantMessage += heard.Add;
            }

            public List<GridInstantMessage> HeardBy(UUID agent) => Heard.TryGetValue(agent, out var l) ? l : new();
        }

        private static Node MakeNode(string region, uint x, INonSpatialSessionStore store, string secret = Secret)
        {
            Process p = new Process(region, x, 1000);
            NonSpatialVoiceSessionEngine engine = new NonSpatialVoiceSessionEngine(
                store, new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission() }, Grid);
            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_adhocVoiceEnabled", true);
            SetPrivate(mod, "m_nonSpatial", engine);
            SetPrivate(mod, "m_carrierAuth", new CarrierAuth(secret));
            ((List<Scene>)GetPrivate(mod, "m_scenes")).Add(p.Scene);
            return new Node { Process = p, Module = mod, Engine = engine };
        }

        /// <summary>
        /// Alice starts a conference in the first process and invites <paramref name="invitees"/>, who are root in
        /// the second. The ring crosses as a carrier and is adopted there. Nobody has accepted yet.
        /// </summary>
        private static (Node One, Node Two, UUID Session) Start(bool sharedStore, params UUID[] invitees)
        {
            INonSpatialSessionStore store1 = new InMemoryNonSpatialSessionStore();
            Node one = MakeNode("Region One", 1000, store1);
            Node two = MakeNode("Region Two", 1001, sharedStore ? store1 : new InMemoryNonSpatialSessionStore());

            one.Process.AddRoot(Alice);
            one.Listen(Alice);
            one.Process.Presence.SetOnline(invitees);
            two.Process.Presence.SetOnline(Alice);
            foreach (UUID i in invitees)
            {
                two.Process.AddRoot(i);
                two.Listen(i);
            }

            SessionOutcome start = one.Engine.StartAdhoc(Alice, UUID.Random(), RegionA, invitees.ToList());
            UUID session = start.Session.SessionId;
            Invoke(one.Module, "RingAdhocMembers", start.Session, Alice, "Test User", null);
            foreach (GridInstantMessage ring in one.Process.Transfer.Sent.ToList())
                Invoke(two.Module, "OnIncomingGroupRing", ring);
            one.Process.Transfer.Sent.Clear();
            return (one, two, session);
        }

        private static void Type(Node node, UUID sender, UUID session, string text)
            => Invoke(node.Module, "OnViewerConferenceText", null, new GridInstantMessage
            {
                dialog = AdhocTextSession.DialogSessionSend,
                fromAgentID = sender.Guid,
                fromAgentName = "Test User",
                imSessionID = session.Guid,
                message = text,
            });

        /// <summary>Hands every text carrier the sender's process recorded to the other process.</summary>
        private static List<GridInstantMessage> Carry(Node from, Node to)
        {
            List<GridInstantMessage> carried = from.Process.Transfer.Sent
                .Where(m => AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _)).ToList();
            from.Process.Transfer.Sent.Clear();
            foreach (GridInstantMessage m in carried)
                Invoke(to.Module, "OnIncomingConferenceText", m);
            return carried;
        }

        private static void ExpectOneLine(Node node, UUID agent, UUID session, string text)
        {
            List<GridInstantMessage> got = node.HeardBy(agent);
            Assert.That(got, Has.Count.EqualTo(1));
            Assert.That(got[0].message, Is.EqualTo(text));
            Assert.That(new UUID(got[0].imSessionID), Is.EqualTo(session));
            Assert.That(got[0].dialog, Is.EqualTo(AdhocTextSession.DialogSessionSend));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TheStartersLine_ReachesAMemberWhoAcceptedInAnotherProcess(bool sharedStore)
        {
            var (one, two, session) = Start(sharedStore, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");

            Type(one, Alice, session, "hello from the first process");
            List<GridInstantMessage> carried = Carry(one, two);

            Assert.That(carried, Has.Count.EqualTo(1), "one signed text carrier, to Bob");
            ExpectOneLine(two, Bob, session, "hello from the first process");
            Assert.That(one.HeardBy(Alice), Is.Empty, "the sender is never sent their own line back");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AMembersLine_ReachesTheStarterInTheOtherProcess(bool sharedStore)
        {
            var (one, two, session) = Start(sharedStore, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");

            Type(two, Bob, session, "hello from the second process");
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1), "one signed text carrier, to Alice");
            ExpectOneLine(one, Alice, session, "hello from the second process");
            Assert.That(two.HeardBy(Bob), Is.Empty, "the sender is never sent their own line back");
        }

        [Test]
        public void AMemberWhoDeclinedInTheOtherProcess_DoesNotReceive_AndTheAcceptingMemberDoes()
        {
            var (one, two, session) = Start(false, Bob, Carol);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            two.Engine.Decline(session, Carol);

            Type(one, Alice, session, "for those who stayed");
            Carry(one, two);

            ExpectOneLine(two, Bob, session, "for those who stayed");
            Assert.That(two.HeardBy(Carol), Is.Empty);
        }

        [Test]
        public void AMemberWhoLeftTheChatInTheOtherProcess_DoesNotReceive()
        {
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            two.Engine.LeaveChat(session, Bob);

            Type(one, Alice, session, "anyone there?");
            Carry(one, two);

            Assert.That(two.HeardBy(Bob), Is.Empty);
        }

        [Test]
        public void AnInviteeWhoHasNotAcceptedInTheOtherProcess_DoesNotReceive()
        {
            var (one, two, session) = Start(false, Bob, Carol);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");   // Carol was rung and has not answered

            Type(one, Alice, session, "only the people in the chat");
            Carry(one, two);

            ExpectOneLine(two, Bob, session, "only the people in the chat");
            Assert.That(two.HeardBy(Carol), Is.Empty, "an invitation is not membership of the conversation");
        }

        [Test]
        public void ALineIsNotCarriedToAMemberWhoDeclinedInTheSendersOwnProcess()
        {
            var (one, two, session) = Start(false, Bob, Carol);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            one.Engine.Decline(session, Carol);

            Type(one, Alice, session, "not for Carol");
            List<GridInstantMessage> carried = Carry(one, two);

            Assert.That(carried.Select(m => new UUID(m.toAgentID)), Is.EqualTo(new[] { Bob }));
        }

        [Test]
        public void AReplayedCarrier_IsRefused()
        {
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            Type(one, Alice, session, "once only");
            GridInstantMessage carried = Carry(one, two).Single();

            Invoke(two.Module, "OnIncomingConferenceText", carried);   // the same carrier again

            ExpectOneLine(two, Bob, session, "once only");
        }

        [Test]
        public void AForgedCarrier_IsRefused()
        {
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            string roomKey = two.Engine.Store.Get(session).RoomKey;

            // signed with another secret
            Node outsider = MakeNode("Region Three", 1002, new InMemoryNonSpatialSessionStore(), "ffffffffffffffffffffffffffffffff-other");
            SetPrivate(outsider.Module, "m_carrierAuth", new CarrierAuth("ffffffffffffffffffffffffffffffff-other"));
            GridInstantMessage wrongKey = AdhocTextTransport.Build(Bob, Alice, "Test User", session, RegionA, "forged",
                null, roomKey, (CarrierAuth)GetPrivate(outsider.Module, "m_carrierAuth"));
            Invoke(two.Module, "OnIncomingConferenceText", wrongKey);

            // no signature at all
            GridInstantMessage unsigned = AdhocTextTransport.Build(Bob, Alice, "Test User", session, RegionA, "unsigned", null, roomKey, null);
            Invoke(two.Module, "OnIncomingConferenceText", unsigned);

            // a genuine carrier with its text changed in transit
            Type(one, Alice, session, "the original");
            GridInstantMessage genuine = one.Process.Transfer.Sent.Last();
            one.Process.Transfer.Sent.Clear();
            Assert.That(AdhocTextTransport.TryParse(genuine, out _, out _, out string text, out _, out _), Is.True);
            genuine.binaryBucket = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(genuine.binaryBucket).Replace("the original", "the changed!"));
            Invoke(two.Module, "OnIncomingConferenceText", genuine);

            Assert.That(two.HeardBy(Bob), Is.Empty);
        }

        [Test]
        public void ASignedLineFromANonMember_IsRefused()
        {
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            string roomKey = two.Engine.Store.Get(session).RoomKey;

            GridInstantMessage fromStranger = AdhocTextTransport.Build(Bob, Stranger, "Test Stranger", session, RegionA,
                "let me in", null, roomKey, (CarrierAuth)GetPrivate(one.Module, "m_carrierAuth"));
            Invoke(two.Module, "OnIncomingConferenceText", fromStranger);

            Assert.That(two.HeardBy(Bob), Is.Empty);
        }

        [Test]
        public void ThePlan_NamesWhoMayBeInTheChatElsewhere_AndLeavesOutDeclinedAndLeft()
        {
            var (one, two, session) = Start(false, Bob, Carol);
            UUID dave = UUID.Random();
            one.Engine.Invite(session, Alice, dave);
            one.Engine.Decline(session, Carol);

            Assert.That(AdhocTextSession.TryPlan(one.Engine, true, session, Alice, "hi", out AdhocTextPlan plan, out string decision), Is.True);

            Assert.That(decision, Is.EqualTo(AdhocTextSession.DecisionDelivered));
            Assert.That(plan.Recipients, Is.Empty, "nobody is recorded as in the conversation here but the sender");
            Assert.That(plan.Elsewhere, Is.EquivalentTo(new[] { Bob, dave }));
        }

        [Test]
        public void AProcessThatAdoptedTheConversation_CarriesToTheStarterToo()
        {
            var (_, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");

            Assert.That(AdhocTextSession.TryPlan(two.Engine, true, session, Bob, "hi", out AdhocTextPlan plan, out _), Is.True);

            Assert.That(plan.Recipients, Is.Empty);
            Assert.That(plan.Elsewhere, Is.EqualTo(new[] { Alice }), "the adopting process holds no record of the starter");
        }

        [Test]
        public void ANonMembersTypedLine_IsRefusedAndNothingIsCarried()
        {
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            one.Process.AddRoot(Stranger);

            Type(one, Stranger, session, "let me in");

            Assert.That(one.Process.Transfer.Sent, Is.Empty);
            Assert.That(two.HeardBy(Bob), Is.Empty);
        }
    }
}
