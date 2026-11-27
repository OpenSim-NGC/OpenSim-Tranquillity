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
using Xunit;
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
    public class AdhocTextCrossProcessTests
    {
        internal const string Grid = "example-grid";
        internal const string Secret = "0123456789abcdef0123456789abcdef-example";
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");
        internal static readonly UUID Stranger = new UUID("99999999-9999-9999-9999-999999999999");

        internal sealed class Node
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

        internal static Node MakeNode(string region, uint x, INonSpatialSessionStore store, string secret = Secret, string grid = Grid)
        {
            Process p = new Process(region, x, 1000);
            NonSpatialVoiceSessionEngine engine = new NonSpatialVoiceSessionEngine(
                store, new INonSpatialAdmission[] { new P2PAdmission(), new AdhocAdmission() }, grid);
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
        internal static (Node One, Node Two, UUID Session) Start(bool sharedStore, params UUID[] invitees)
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

        internal static void Type(Node node, UUID sender, UUID session, string text)
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
            Assert.Single(got);
            Assert.Equal(text, got[0].message);
            Assert.Equal(session, new UUID(got[0].imSessionID));
            Assert.Equal(AdhocTextSession.DialogSessionSend, got[0].dialog);
        }

        [InlineData(false)]
        [InlineData(true)]
        [Theory(Timeout = 60000)]
        public void TheStartersLine_ReachesAMemberWhoAcceptedInAnotherProcess(bool sharedStore)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(sharedStore, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");

            Type(one, Alice, session, "hello from the first process");
            List<GridInstantMessage> carried = Carry(one, two);

            {
                var assertedValue = carried;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "one signed text carrier, to Bob");
            }
            ExpectOneLine(two, Bob, session, "hello from the first process");
            {
                var assertedValue = one.HeardBy(Alice);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the sender is never sent their own line back");
            }
        }

        [InlineData(false)]
        [InlineData(true)]
        [Theory(Timeout = 60000)]
        public void AMembersLine_ReachesTheStarterInTheOtherProcess(bool sharedStore)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(sharedStore, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");

            Type(two, Bob, session, "hello from the second process");
            List<GridInstantMessage> carried = Carry(two, one);

            {
                var assertedValue = carried;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "one signed text carrier, to Alice");
            }
            ExpectOneLine(one, Alice, session, "hello from the second process");
            {
                var assertedValue = two.HeardBy(Bob);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "the sender is never sent their own line back");
            }
        }

        [Fact(Timeout = 60000)]
        public void TheTextCarrier_IsSentToRootOnly()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");

            Type(one, Alice, session, "hello");

            Assert.Single(one.Process.Transfer.Sent, m => AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _));
            {
                var assertedValue = one.Process.Transfer.SentRootOnly;
                TestAssert.WithMessage(() => Assert.All(assertedValue, item => Assert.True(item)), "a child presence here must not be handed the carrier as an instant message");
            }
        }

        [Fact(Timeout = 60000)]
        public void AMemberWhoDeclinedInTheOtherProcess_DoesNotReceive_AndTheAcceptingMemberDoes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob, Carol);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            two.Engine.Decline(session, Carol);

            Type(one, Alice, session, "for those who stayed");
            Carry(one, two);

            ExpectOneLine(two, Bob, session, "for those who stayed");
            Assert.Empty(two.HeardBy(Carol));
        }

        [Fact(Timeout = 60000)]
        public void AMemberWhoLeftTheChatInTheOtherProcess_DoesNotReceive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            two.Engine.LeaveChat(session, Bob);

            Type(one, Alice, session, "anyone there?");
            Carry(one, two);

            Assert.Empty(two.HeardBy(Bob));
        }

        [Fact(Timeout = 60000)]
        public void AnInviteeWhoHasNotAcceptedInTheOtherProcess_DoesNotReceive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob, Carol);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");   // Carol was rung and has not answered

            Type(one, Alice, session, "only the people in the chat");
            Carry(one, two);

            ExpectOneLine(two, Bob, session, "only the people in the chat");
            {
                var assertedValue = two.HeardBy(Carol);
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "an invitation is not membership of the conversation");
            }
        }

        [Fact(Timeout = 60000)]
        public void ALineIsNotCarriedToAMemberWhoDeclinedInTheSendersOwnProcess()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob, Carol);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            one.Engine.Decline(session, Carol);

            Type(one, Alice, session, "not for Carol");
            List<GridInstantMessage> carried = Carry(one, two);

            Assert.Equal(new[] { Bob }, carried.Select(m => new UUID(m.toAgentID)));
        }

        [Fact(Timeout = 60000)]
        public void AReplayedCarrier_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            Type(one, Alice, session, "once only");
            GridInstantMessage carried = Carry(one, two).Single();

            Invoke(two.Module, "OnIncomingConferenceText", carried);   // the same carrier again

            ExpectOneLine(two, Bob, session, "once only");
        }

        [Fact(Timeout = 60000)]
        public void AForgedCarrier_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
            Assert.True(AdhocTextTransport.TryParse(genuine, out _, out _, out string text, out _, out _));
            genuine.binaryBucket = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(genuine.binaryBucket).Replace("the original", "the changed!"));
            Invoke(two.Module, "OnIncomingConferenceText", genuine);

            Assert.Empty(two.HeardBy(Bob));
        }

        [Fact(Timeout = 60000)]
        public void ASignedLineFromANonMember_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            string roomKey = two.Engine.Store.Get(session).RoomKey;

            GridInstantMessage fromStranger = AdhocTextTransport.Build(Bob, Stranger, "Test Stranger", session, RegionA,
                "let me in", null, roomKey, (CarrierAuth)GetPrivate(one.Module, "m_carrierAuth"));
            Invoke(two.Module, "OnIncomingConferenceText", fromStranger);

            Assert.Empty(two.HeardBy(Bob));
        }

        [Fact(Timeout = 60000)]
        public void ThePlan_NamesWhoMayBeInTheChatElsewhere_AndLeavesOutDeclinedAndLeft()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob, Carol);
            UUID dave = UUID.Random();
            one.Engine.Invite(session, Alice, dave);
            one.Engine.Decline(session, Carol);

            Assert.True(AdhocTextSession.TryPlan(one.Engine, true, session, Alice, "hi", out AdhocTextPlan plan, out string decision));

            Assert.Equal(AdhocTextSession.DecisionDelivered, decision);
            {
                var assertedValue = plan.Recipients;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "nobody is recorded as in the conversation here but the sender");
            }
            Assert.Equivalent(new[] { Bob, dave }, plan.Elsewhere, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void AProcessThatAdoptedTheConversation_CarriesToTheStarterToo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (_, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");

            Assert.True(AdhocTextSession.TryPlan(two.Engine, true, session, Bob, "hi", out AdhocTextPlan plan, out _));

            Assert.Empty(plan.Recipients);
            {
                var assertedValue = plan.Elsewhere;
                TestAssert.WithMessage(() => Assert.Equal(new[] { Alice }, assertedValue), "the adopting process holds no record of the starter");
            }
        }

        [Fact(Timeout = 60000)]
        public void ANonMembersTypedLine_IsRefusedAndNothingIsCarried()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var (one, two, session) = Start(false, Bob);
            two.Engine.Accept(session, Bob, RegionB, "vs-bob");
            one.Process.AddRoot(Stranger);

            Type(one, Stranger, session, "let me in");

            Assert.Empty(one.Process.Transfer.Sent);
            Assert.Empty(two.HeardBy(Bob));
        }
    }
}
