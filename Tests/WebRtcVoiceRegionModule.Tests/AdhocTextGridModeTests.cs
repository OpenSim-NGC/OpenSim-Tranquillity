/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A typed conference line across processes when the sessions live in the grid session service.
 *
 * Two region-server processes are two modules over two scenes, each talking to ONE session service, as
 * two regions of a grid do. The service records every answer wherever it is given, so a line reaches the
 * members it records as in the conversation, signed with the key the service gives each seated member, and
 * nobody it records as invited, declined or gone. Carriers move between the processes by hand.
 */
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
    public class AdhocTextGridModeTests
    {
        private const string Secret = "0123456789abcdef0123456789abcdef-example";
        private static readonly UUID Carol = new UUID("33333333-3333-3333-3333-333333333333");

        private sealed class Node
        {
            public Process Process;
            public WebRtcVoiceRegionModule Module;
            public GridNonSpatialSessions Sessions;
            public readonly Dictionary<UUID, List<GridInstantMessage>> Heard = new();

            public void Listen(UUID agent)
            {
                List<GridInstantMessage> heard = new();
                Heard[agent] = heard;
                ((TestClient)Process.Scene.GetScenePresence(agent).ControllingClient).OnReceivedInstantMessage += heard.Add;
            }

            public List<GridInstantMessage> HeardBy(UUID agent) => Heard.TryGetValue(agent, out var l) ? l : new();
        }

        private static Node MakeNode(string region, uint x, VoiceSessionService service)
        {
            Process p = new Process(region, x, 1000);
            GridNonSpatialSessions sessions = VoiceSessionServiceTests.NewRegion(service);
            WebRtcVoiceRegionModule mod = new WebRtcVoiceRegionModule();
            SetPrivate(mod, "m_Enabled", true);
            SetPrivate(mod, "m_adhocVoiceEnabled", true);
            SetPrivate(mod, "m_nonSpatial", sessions);
            SetPrivate(mod, "m_carrierAuth", new CarrierAuth(Secret));
            ((List<Scene>)GetPrivate(mod, "m_scenes")).Add(p.Scene);
            return new Node { Process = p, Module = mod, Sessions = sessions };
        }

        /// <summary>Alice starts a conference in the first process and invites <paramref name="invitees"/>, who are root in the second.</summary>
        private static (Node One, Node Two, UUID Session) Start(params UUID[] invitees)
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            Node one = MakeNode("Region One", 1000, service);
            Node two = MakeNode("Region Two", 1001, service);

            one.Process.AddRoot(Alice);
            one.Listen(Alice);
            one.Process.Presence.SetOnline(invitees);
            two.Process.Presence.SetOnline(Alice);
            foreach (UUID i in invitees)
            {
                two.Process.AddRoot(i);
                two.Listen(i);
            }

            SessionOutcome start = one.Sessions.StartAdhoc(Alice, UUID.Random(), RegionA, invitees.ToList());
            Assert.That(start.Ok, Is.True, start.Decision);
            Invoke(one.Module, "RingAdhocMembers", start.Session, Alice, "Test User", null);
            foreach (GridInstantMessage ring in one.Process.Transfer.Sent.ToList())
                Invoke(two.Module, "OnIncomingGroupRing", ring);
            one.Process.Transfer.Sent.Clear();
            return (one, two, start.Session.SessionId);
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

        private static List<GridInstantMessage> Carry(Node from, Node to)
        {
            List<GridInstantMessage> carried = from.Process.Transfer.Sent
                .Where(m => AdhocTextTransport.TryParse(m, out _, out _, out _, out _, out _)).ToList();
            from.Process.Transfer.Sent.Clear();
            foreach (GridInstantMessage m in carried)
                Invoke(to.Module, "OnIncomingConferenceText", m);
            return carried;
        }

        [Test]
        public void TheStartersLine_ReachesAMemberWhoAcceptedInAnotherProcess_AndNotTheSender()
        {
            var (one, two, session) = Start(Bob);
            Assert.That(two.Sessions.Accept(session, Bob, RegionB, "vs-bob").Ok, Is.True);

            Type(one, Alice, session, "hello from the first process");
            List<GridInstantMessage> carried = Carry(one, two);

            Assert.That(carried, Has.Count.EqualTo(1));
            Assert.That(two.HeardBy(Bob).Select(m => m.message), Is.EqualTo(new[] { "hello from the first process" }));
            Assert.That(one.HeardBy(Alice), Is.Empty, "the sender is never sent their own line back");
        }

        [Test]
        public void AMembersLine_ReachesTheStarterInTheOtherProcess()
        {
            var (one, two, session) = Start(Bob);
            two.Sessions.Accept(session, Bob, RegionB, "vs-bob");

            Type(two, Bob, session, "hello from the second process");
            List<GridInstantMessage> carried = Carry(two, one);

            Assert.That(carried, Has.Count.EqualTo(1));
            Assert.That(one.HeardBy(Alice).Select(m => m.message), Is.EqualTo(new[] { "hello from the second process" }));
            Assert.That(two.HeardBy(Bob), Is.Empty);
        }

        [Test]
        public void AMemberWhoDeclined_AndOneWhoNeverAnswered_AreNotWrittenTo()
        {
            var (one, two, session) = Start(Bob, Carol);
            UUID dave = UUID.Random();
            two.Sessions.Accept(session, Bob, RegionB, "vs-bob");
            two.Sessions.Decline(session, Carol);
            one.Sessions.Invite(session, Alice, dave);   // rung, never answered
            one.Process.Presence.SetOnline(dave);

            Type(one, Alice, session, "for those who stayed");
            List<GridInstantMessage> carried = Carry(one, two);

            Assert.That(carried.Select(m => new UUID(m.toAgentID)), Is.EqualTo(new[] { Bob }),
                "the service records who answered, so nothing is carried to a member it records as invited or declined");
            Assert.That(two.HeardBy(Bob), Has.Count.EqualTo(1));
            Assert.That(two.HeardBy(Carol), Is.Empty);
        }

        [Test]
        public void AMemberWhoLeftTheChat_DoesNotReceive()
        {
            var (one, two, session) = Start(Bob);
            two.Sessions.Accept(session, Bob, RegionB, "vs-bob");
            two.Sessions.LeaveChat(session, Bob);

            Type(one, Alice, session, "anyone there?");
            Carry(one, two);

            Assert.That(two.HeardBy(Bob), Is.Empty);
        }

        [Test]
        public void AReplayedCarrier_IsRefused()
        {
            var (one, two, session) = Start(Bob);
            two.Sessions.Accept(session, Bob, RegionB, "vs-bob");
            Type(one, Alice, session, "once only");
            GridInstantMessage carried = Carry(one, two).Single();

            Invoke(two.Module, "OnIncomingConferenceText", carried);

            Assert.That(two.HeardBy(Bob), Has.Count.EqualTo(1));
        }

        [Test]
        public void ALineSignedWithoutTheConferenceKey_IsRefused()
        {
            var (one, two, session) = Start(Bob);
            two.Sessions.Accept(session, Bob, RegionB, "vs-bob");
            string roomKey = two.Sessions.Get(session).RoomKey;

            // signed with the grid-wide secret only, not the key the service gives the conference's members
            GridInstantMessage withoutKey = AdhocTextTransport.Build(Bob, Alice, "Test User", session, RegionA, "no key",
                null, roomKey, new CarrierAuth(Secret));
            Invoke(two.Module, "OnIncomingConferenceText", withoutKey);

            Assert.That(two.HeardBy(Bob), Is.Empty);
        }
    }
}
