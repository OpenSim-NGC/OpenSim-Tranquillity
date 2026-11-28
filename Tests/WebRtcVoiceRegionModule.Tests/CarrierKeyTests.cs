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
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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
            Assert.True(started.Ok, started.Decision);
            g.SessionId = started.Session.SessionId;
            g.RoomKey = started.Session.RoomKey;
            Assert.True(g.Receiver.Accept(g.SessionId, Bob, RegionB).Ok);
            return g;
        }

        private static GridInstantMessage Line(Grid g, string key, string text = "hello", UUID? target = null)
            => AdhocTextTransport.Build(target ?? Bob, Alice, "Test User", g.SessionId, RegionA, text, null, g.RoomKey, null, key);

        private static TextIntake Receive(GridInstantMessage im, INonSpatialSessions store, CarrierAuth auth = null)
            => CarrierIntake.AcceptText(im, auth ?? CarrierAuth.Off(), store);

        // ---- who is given the key ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ASeatedMemberIsGivenTheConferencesKey_AndItDoesNotChange()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            string first = g.Sender.CarrierKey(g.SessionId, Alice);
            string again = g.Receiver.CarrierKey(g.SessionId, Bob);

            Assert.True(first.Length >= CarrierAuth.MinSecretLength);
            {
                var assertedValue = again;
                TestAssert.WithMessage(() => Assert.Equal(first, assertedValue), "every member of the conference has the same key");
            }
        }

        [Fact(Timeout = 60000)]
        public void AnInviteeWhoHasNotJoined_AStranger_AndAnUnknownSession_AreGivenNone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            {
                var assertedValue = g.Receiver.CarrierKey(g.SessionId, Carol);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "invited, not seated");
            }
            {
                var assertedValue = g.Receiver.CarrierKey(g.SessionId, Dave);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "not in the conference");
            }
            Assert.Null(g.Receiver.CarrierKey(UUID.Random(), Alice));
        }

        [Fact(Timeout = 60000)]
        public void AGroupSessionHasNoTextKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            g.Sender.Start(NonSpatialSessionType.Group, Alice, NonSpatialIdentityTests.Group, NonSpatialIdentityTests.Group, RegionA);

            Assert.Null(g.Sender.CarrierKey(NonSpatialIdentityTests.Group, Alice));
        }

        [Fact(Timeout = 60000)]
        public void TwoConferencesHaveTwoKeys()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            SessionOutcome other = g.Sender.StartAdhoc(Alice, UUID.Random(), RegionA);

            Assert.NotEqual(g.Sender.CarrierKey(g.SessionId, Alice), g.Sender.CarrierKey(other.Session.SessionId, Alice));
        }

        [Fact(Timeout = 60000)]
        public void ADepartedMemberIsNoLongerGivenTheKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            g.Receiver.Depart(g.SessionId, Bob, DepartureReason.VoiceTeardown);
            GridNonSpatialSessions fresh = VoiceSessionServiceTests.NewRegion(g.Service);   // a region that has not kept it

            Assert.Null(fresh.CarrierKey(g.SessionId, Bob));
        }

        [Fact(Timeout = 60000)]
        public void TheKeyIsNeverInACopyOfTheSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            string key = g.Sender.CarrierKey(g.SessionId, Alice);

            Assert.DoesNotContain(key, g.Service.Get(g.SessionId, Alice).ToJson());
            Assert.DoesNotContain(key, g.Service.Get(g.SessionId, Bob).ToJson());
        }

        // ---- a line across regions --------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ALineSignedWithTheConferencesKey_IsAccepted_WithNoSharedSecret()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            TextIntake t = Receive(Line(g, g.Sender.CarrierKey(g.SessionId, Alice)), g.Receiver);

            Assert.True(t.Ok, t.Decision);
            Assert.Equal("hello", t.Message);
            Assert.Equal(Alice, t.Sender);
        }

        [Fact(Timeout = 60000)]
        public void ALineNobodySigned_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            TextIntake t = Receive(Line(g, key: null), g.Receiver);

            Assert.False(t.Ok);
            Assert.Equal(CarrierAuth.MissingMac, t.Reason);
        }

        [Fact(Timeout = 60000)]
        public void ALineSignedWithAKeyMadeUp_OrAnotherConferencesKey_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            string other = g.Sender.CarrierKey(g.Sender.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId, Alice);

            Assert.Equal(CarrierAuth.BadMac, Receive(Line(g, new string('f', 64)), g.Receiver).Reason);
            Assert.Equal(CarrierAuth.BadMac, Receive(Line(g, other), g.Receiver).Reason);
        }

        [Fact(Timeout = 60000)]
        public void ALineWhoseTextWasChangedAfterSigning_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridInstantMessage im = Line(g, g.Sender.CarrierKey(g.SessionId, Alice), "hello");
            string json = System.Text.Encoding.UTF8.GetString(im.binaryBucket).Replace("hello", "world");
            im.binaryBucket = System.Text.Encoding.UTF8.GetBytes(json);

            Assert.False(Receive(im, g.Receiver).Ok);
        }

        [Fact(Timeout = 60000)]
        public void AReplayedLine_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridInstantMessage im = Line(g, g.Sender.CarrierKey(g.SessionId, Alice));
            CarrierAuth receiving = CarrierAuth.Off();   // the one the region keeps, which remembers what it has seen

            Assert.True(Receive(im, g.Receiver, receiving).Ok);
            Assert.Equal(CarrierAuth.Replay, Receive(im, g.Receiver, receiving).Reason);
        }

        [Fact(Timeout = 60000)]
        public void ALineForSomeoneWhoIsNotSeated_IsRefusedForWantOfAKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            TextIntake t = Receive(Line(g, g.Sender.CarrierKey(g.SessionId, Alice), target: Carol), g.Receiver);

            Assert.False(t.Ok);
            Assert.Equal(CarrierIntake.DecisionNoCarrierKey, t.Decision);
        }

        [Fact(Timeout = 60000)]
        public void AnAgentWhoIsNotInTheConference_HasNoKeyToSignWith()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            Assert.Null(g.Sender.CarrierKey(g.SessionId, Dave));
        }

        [Fact(Timeout = 60000)]
        public void ARegionThatHasASecret_StillUsesTheConferencesKey()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            CarrierAuth configured = new CarrierAuth(new string('s', CarrierAuth.MinSecretLength));

            TextIntake t = Receive(Line(g, g.Sender.CarrierKey(g.SessionId, Alice)), g.Receiver, configured);

            Assert.True(t.Ok, t.Decision);
        }

        // ---- the service is asked once, and may not answer ---------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheServiceIsAskedForTheKeyOnce_NotOnceForEveryLine()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            string key = g.Sender.CarrierKey(g.SessionId, Alice);
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service);
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            for (int i = 0; i < 5; i++)
                Assert.True(Receive(Line(g, key, "line " + i), receiver).Ok);

            Assert.Equal(1, flaky.Delivered.Count(w => w == "carrierkey"));
        }

        [Fact(Timeout = 60000)]
        public void AStrangersLines_CostTheServiceOneQuestion_NotOnePerLine()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service);
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            for (int i = 0; i < 20; i++)
                Receive(Line(g, new string('f', 64), target: Dave), receiver);

            Assert.Equal(1, flaky.Delivered.Count(w => w == "carrierkey"));
        }

        [Fact(Timeout = 60000)]
        public void WhenTheServiceCannotBeReached_ALineIsRefused_AndNothingIsKept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            string key = g.Sender.CarrierKey(g.SessionId, Alice);
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service) { Up = false };
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            Assert.Equal(CarrierIntake.DecisionNoCarrierKey, Receive(Line(g, key), receiver).Decision);

            flaky.Up = true;
            Assert.True(Receive(Line(g, key, "again"), receiver).Ok, "and it works as soon as the service answers");
        }

        [Fact(Timeout = 60000)]
        public void TheInProcessEngine_StillUsesTheSharedSecret()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(NewEngine().StoreIssuesCarrierKeys);
            Assert.True(VoiceSessionServiceTests.NewRegion(VoiceSessionServiceTests.NewService()).StoreIssuesCarrierKeys);
        }
    }
}
