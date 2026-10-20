/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Conference text across region servers when the grid call session service keeps the sessions. The service keeps a key
 * for each conference and gives it only to a member who holds a seat, so the lines members send are signed with a key a
 * stranger cannot have, and no secret is shared between region servers.
 */
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class CarrierKeyTests
    {
        private static readonly UUID Dave = new UUID("44444444-4444-4444-4444-444444444444");

        private sealed class Grid
        {
            public VoiceSessionService Service;
            public GridNonSpatialSessions Sender, Receiver;
            public UUID SessionId;
            public string RoomKey;
        }

        /// <summary>Alice starts a conference on the sender region and Bob, invited, joins it on the receiver region.</summary>
        private static Grid NewGrid()
        {
            Grid g = new Grid { Service = VoiceSessionServiceTests.NewService() };
            g.Sender = VoiceSessionServiceTests.NewRegion(g.Service);
            g.Receiver = VoiceSessionServiceTests.NewRegion(g.Service);
            SessionOutcome started = g.Sender.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol });
            Assert.That(started.Ok, Is.True, started.Decision);
            g.SessionId = started.Session.SessionId;
            g.RoomKey = started.Session.RoomKey;
            Assert.That(g.Receiver.Accept(g.SessionId, Bob, RegionB).Ok, Is.True);
            return g;
        }

        private static GridInstantMessage Line(Grid g, string key, string text = "hello", UUID? target = null)
            => AdhocTextTransport.Build(target ?? Bob, Alice, "Test User", g.SessionId, RegionA, text, null, g.RoomKey, null, key);

        private static TextIntake Receive(GridInstantMessage im, INonSpatialSessions store, CarrierAuth auth = null)
            => CarrierIntake.AcceptText(im, auth ?? CarrierAuth.Off(), store);

        // ---- who is given the key ---------------------------------------------------------------------

        [Test]
        public void ASeatedMemberIsGivenTheConferencesKey_AndItDoesNotChange()
        {
            Grid g = NewGrid();

            string first = g.Sender.CarrierKey(g.SessionId, Alice);
            string again = g.Receiver.CarrierKey(g.SessionId, Bob);

            Assert.That(first, Has.Length.GreaterThanOrEqualTo(CarrierAuth.MinSecretLength));
            Assert.That(again, Is.EqualTo(first), "every member of the conference has the same key");
        }

        [Test]
        public void AnInviteeWhoHasNotJoined_AStranger_AndAnUnknownSession_AreGivenNone()
        {
            Grid g = NewGrid();

            Assert.That(g.Receiver.CarrierKey(g.SessionId, Carol), Is.Null, "invited, not seated");
            Assert.That(g.Receiver.CarrierKey(g.SessionId, Dave), Is.Null, "not in the conference");
            Assert.That(g.Receiver.CarrierKey(UUID.Random(), Alice), Is.Null);
        }

        [Test]
        public void AGroupSessionHasNoTextKey()
        {
            Grid g = NewGrid();
            g.Sender.Start(NonSpatialSessionType.Group, Alice, NonSpatialIdentityTests.Group, NonSpatialIdentityTests.Group, RegionA);

            Assert.That(g.Sender.CarrierKey(NonSpatialIdentityTests.Group, Alice), Is.Null);
        }

        [Test]
        public void TwoConferencesHaveTwoKeys()
        {
            Grid g = NewGrid();
            SessionOutcome other = g.Sender.StartAdhoc(Alice, UUID.Random(), RegionA);

            Assert.That(g.Sender.CarrierKey(other.Session.SessionId, Alice), Is.Not.EqualTo(g.Sender.CarrierKey(g.SessionId, Alice)));
        }

        [Test]
        public void ADepartedMemberIsNoLongerGivenTheKey()
        {
            Grid g = NewGrid();
            g.Receiver.Depart(g.SessionId, Bob, DepartureReason.VoiceTeardown);
            GridNonSpatialSessions fresh = VoiceSessionServiceTests.NewRegion(g.Service);   // a region that has not kept it

            Assert.That(fresh.CarrierKey(g.SessionId, Bob), Is.Null);
        }

        [Test]
        public void TheKeyIsNeverInACopyOfTheSession()
        {
            Grid g = NewGrid();
            string key = g.Sender.CarrierKey(g.SessionId, Alice);

            Assert.That(g.Service.Get(g.SessionId, Alice).ToJson(), Does.Not.Contain(key));
            Assert.That(g.Service.Get(g.SessionId, Bob).ToJson(), Does.Not.Contain(key));
        }

        // ---- a line across regions --------------------------------------------------------------------

        [Test]
        public void ALineSignedWithTheConferencesKey_IsAccepted_WithNoSharedSecret()
        {
            Grid g = NewGrid();

            TextIntake t = Receive(Line(g, g.Sender.CarrierKey(g.SessionId, Alice)), g.Receiver);

            Assert.That(t.Ok, Is.True, t.Decision);
            Assert.That(t.Message, Is.EqualTo("hello"));
            Assert.That(t.Sender, Is.EqualTo(Alice));
        }

        [Test]
        public void ALineNobodySigned_IsRefused()
        {
            Grid g = NewGrid();

            TextIntake t = Receive(Line(g, key: null), g.Receiver);

            Assert.That(t.Ok, Is.False);
            Assert.That(t.Reason, Is.EqualTo(CarrierAuth.MissingMac));
        }

        [Test]
        public void ALineSignedWithAKeyMadeUp_OrAnotherConferencesKey_IsRefused()
        {
            Grid g = NewGrid();
            string other = g.Sender.CarrierKey(g.Sender.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId, Alice);

            Assert.That(Receive(Line(g, new string('f', 64)), g.Receiver).Reason, Is.EqualTo(CarrierAuth.BadMac));
            Assert.That(Receive(Line(g, other), g.Receiver).Reason, Is.EqualTo(CarrierAuth.BadMac));
        }

        [Test]
        public void ALineWhoseTextWasChangedAfterSigning_IsRefused()
        {
            Grid g = NewGrid();
            GridInstantMessage im = Line(g, g.Sender.CarrierKey(g.SessionId, Alice), "hello");
            string json = System.Text.Encoding.UTF8.GetString(im.binaryBucket).Replace("hello", "world");
            im.binaryBucket = System.Text.Encoding.UTF8.GetBytes(json);

            Assert.That(Receive(im, g.Receiver).Ok, Is.False);
        }

        [Test]
        public void AReplayedLine_IsRefused()
        {
            Grid g = NewGrid();
            GridInstantMessage im = Line(g, g.Sender.CarrierKey(g.SessionId, Alice));
            CarrierAuth receiving = CarrierAuth.Off();   // the one the region keeps, which remembers what it has seen

            Assert.That(Receive(im, g.Receiver, receiving).Ok, Is.True);
            Assert.That(Receive(im, g.Receiver, receiving).Reason, Is.EqualTo(CarrierAuth.Replay));
        }

        [Test]
        public void ALineForSomeoneWhoIsNotSeated_IsRefusedForWantOfAKey()
        {
            Grid g = NewGrid();

            TextIntake t = Receive(Line(g, g.Sender.CarrierKey(g.SessionId, Alice), target: Carol), g.Receiver);

            Assert.That(t.Ok, Is.False);
            Assert.That(t.Decision, Is.EqualTo(CarrierIntake.DecisionNoCarrierKey));
        }

        [Test]
        public void AnAgentWhoIsNotInTheConference_HasNoKeyToSignWith()
        {
            Grid g = NewGrid();

            Assert.That(g.Sender.CarrierKey(g.SessionId, Dave), Is.Null);
        }

        [Test]
        public void ARegionThatHasASecret_StillUsesTheConferencesKey()
        {
            Grid g = NewGrid();
            CarrierAuth configured = new CarrierAuth(new string('s', CarrierAuth.MinSecretLength));

            TextIntake t = Receive(Line(g, g.Sender.CarrierKey(g.SessionId, Alice)), g.Receiver, configured);

            Assert.That(t.Ok, Is.True, t.Decision);
        }

        // ---- the service is asked once, and may not answer ---------------------------------------------

        [Test]
        public void TheServiceIsAskedForTheKeyOnce_NotOnceForEveryLine()
        {
            Grid g = NewGrid();
            string key = g.Sender.CarrierKey(g.SessionId, Alice);
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service);
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            for (int i = 0; i < 5; i++)
                Assert.That(Receive(Line(g, key, "line " + i), receiver).Ok, Is.True);

            Assert.That(flaky.Delivered.Count(w => w == "carrierkey"), Is.EqualTo(1));
        }

        [Test]
        public void AStrangersLines_CostTheServiceOneQuestion_NotOnePerLine()
        {
            Grid g = NewGrid();
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service);
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            for (int i = 0; i < 20; i++)
                Receive(Line(g, new string('f', 64), target: Dave), receiver);

            Assert.That(flaky.Delivered.Count(w => w == "carrierkey"), Is.EqualTo(1));
        }

        [Test]
        public void WhenTheServiceCannotBeReached_ALineIsRefused_AndNothingIsKept()
        {
            Grid g = NewGrid();
            string key = g.Sender.CarrierKey(g.SessionId, Alice);
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service) { Up = false };
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            Assert.That(Receive(Line(g, key), receiver).Decision, Is.EqualTo(CarrierIntake.DecisionNoCarrierKey));

            flaky.Up = true;
            Assert.That(Receive(Line(g, key, "again"), receiver).Ok, Is.True, "and it works as soon as the service answers");
        }

        [Test]
        public void TheInProcessEngine_StillUsesTheSharedSecret()
        {
            Assert.That(NewEngine().StoreIssuesCarrierKeys, Is.False);
            Assert.That(VoiceSessionServiceTests.NewRegion(VoiceSessionServiceTests.NewService()).StoreIssuesCarrierKeys, Is.True);
        }
    }
}
