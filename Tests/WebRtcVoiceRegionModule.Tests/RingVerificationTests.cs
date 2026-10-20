/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A region that keeps its call sessions in the grid service does not adopt anything from a ring it receives.
 * It asks the service whether the ring is genuine: the session exists, its ring was sent, the caller holds a
 * seat in it, the target was marked invited and has not already used the ring. The credential in the invitation
 * the viewer sees comes from the service, not from the wire.
 */
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class RingVerificationTests
    {
        private sealed class Grid
        {
            public VoiceSessionService Service;
            public GridNonSpatialSessions Sender, Receiver;
            public UUID SessionId;
            public string Token;
        }

        /// <summary>Alice starts a conference from the sender region, inviting Bob; the sender marks Bob invited, as it does before it sends.</summary>
        private static Grid NewGrid(bool markBob = true)
        {
            Grid g = new Grid { Service = VoiceSessionServiceTests.NewService() };
            g.Sender = VoiceSessionServiceTests.NewRegion(g.Service);
            g.Receiver = VoiceSessionServiceTests.NewRegion(g.Service);
            SessionOutcome started = g.Sender.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob });
            Assert.That(started.Ok, Is.True, started.Decision);
            g.SessionId = started.Session.SessionId;
            g.Token = g.Sender.IssueToken(g.SessionId, Alice);
            if (markBob) g.Sender.MarkInvited(g.SessionId, new[] { Bob });
            return g;
        }

        private const int Adhoc = (int)NonSpatialSessionType.Adhoc;

        // ---- the service's answer ---------------------------------------------------------------------

        [Test]
        public void ARingTheSenderMarkedIsVerified_AndCarriesTheStoresCredential()
        {
            Grid g = NewGrid();

            SessionOutcome o = g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.That(o.Ok, Is.True, o.Decision);
            Assert.That(o.Decision, Is.EqualTo("ring-verified"));
            Assert.That(o.Session.Token, Is.EqualTo(g.Token), "an invited target is given the credential");
        }

        [Test]
        public void ARingForASessionThatDoesNotExist_IsRefused_AndNothingIsCreated()
        {
            Grid g = NewGrid();

            SessionOutcome o = g.Receiver.VerifyRing(UUID.Random(), Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.That(o.Ok, Is.False);
            Assert.That(o.Decision, Is.EqualTo(SessionOutcome.NoSuchSession));
            Assert.That(g.Service.SessionCount, Is.EqualTo(1), "only the conference that was started");
        }

        [Test]
        public void ARingForATargetNobodyInvited_IsRefused()
        {
            Grid g = NewGrid();

            SessionOutcome o = g.Receiver.VerifyRing(g.SessionId, Carol, Alice, NonSpatialSessionType.Adhoc);

            Assert.That(o.Ok, Is.False);
            Assert.That(o.Decision, Is.EqualTo("refused-target-not-invited"));
        }

        [Test]
        public void ARingBeforeTheSenderMarkedTheTarget_IsRefused()
        {
            Grid g = NewGrid(markBob: false);

            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Decision, Is.EqualTo("refused-target-not-invited"));
        }

        [Test]
        public void AReplayedRing_IsRefused_AfterTheFirstUsesItUp()
        {
            Grid g = NewGrid();

            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, Is.True);
            SessionOutcome again = g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.That(again.Ok, Is.False);
            Assert.That(again.Decision, Is.EqualTo("refused-ring-used"));
        }

        [Test]
        public void ANewInvitationEntitlesTheTargetToOneMoreRing()
        {
            Grid g = NewGrid();
            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, Is.True);
            g.Receiver.Decline(g.SessionId, Bob);                      // a mis-click; a later invite rings again
            g.Sender.MarkInvited(g.SessionId, new[] { Bob });

            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, Is.True);
            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, Is.False, "and only one");
        }

        [Test]
        public void ARingNamingACallerWhoHoldsNoSeat_IsRefused_AndDoesNotUseTheRingUp()
        {
            Grid g = NewGrid();

            SessionOutcome forged = g.Receiver.VerifyRing(g.SessionId, Bob, Carol, NonSpatialSessionType.Adhoc);
            Assert.That(forged.Ok, Is.False);
            Assert.That(forged.Decision, Is.EqualTo("refused-caller-not-seated"));

            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, Is.True, "the genuine ring still arrives");
        }

        [Test]
        public void ARingOfTheWrongType_IsRefused()
        {
            Grid g = NewGrid();

            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Group).Decision, Is.EqualTo("refused-session-type-mismatch"));
        }

        [Test]
        public void ARingForACallBetweenTwoPeople_IsRefused()
        {
            Grid g = NewGrid();
            g.Sender.Start(NonSpatialSessionType.P2P, Alice, Carol, A2ACalls.ComputeSessionId(Alice, Carol), RegionA);
            UUID call = A2ACalls.ComputeSessionId(Alice, Carol);
            g.Sender.MarkInvited(call, new[] { Carol });

            Assert.That(g.Receiver.VerifyRing(call, Carol, Alice, NonSpatialSessionType.Group).Ok, Is.False);
            Assert.That(g.Receiver.VerifyRing(call, Carol, Alice, NonSpatialSessionType.Adhoc).Ok, Is.False);
        }

        [Test]
        public void AnEndedSessionsRing_IsRefused()
        {
            Grid g = NewGrid();
            g.Sender.Depart(g.SessionId, Alice, DepartureReason.VoiceTeardown);
            g.Service.Sweep();

            Assert.That(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, Is.False);
        }

        [Test]
        public void WhenTheStoreCannotBeReached_TheRingIsRefusedAsUnavailable_NotAdopted()
        {
            Grid g = NewGrid();
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service) { Up = false };
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            SessionOutcome o = receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.That(o.Ok, Is.False);
            Assert.That(o.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
        }

        [Test]
        public void AServiceRefusesARingOfATypeItDoesNotKnow()
        {
            Grid g = NewGrid();

            Assert.That(g.Service.VerifyRing(g.SessionId, Bob, Alice, 99).Decision, Is.EqualTo("refused-bad-request"));
        }

        [Test]
        public void TheInProcessEngine_StillAdoptsRatherThanVerifies()
        {
            Assert.That(NewEngine().StoreVerifiesRings, Is.False);
            Assert.That(VoiceSessionServiceTests.NewRegion(VoiceSessionServiceTests.NewService()).StoreVerifiesRings, Is.True);
        }

        // ---- the receiving region ---------------------------------------------------------------------

        private static GridInstantMessage Ring(Grid g, UUID target, OSDMap body, CarrierAuth auth = null)
            => GroupVoiceRingTransport.Build(target, Alice, "Test User", g.SessionId, RegionA, body, auth);

        private static OSDMap Body(Grid g, string credential = null)
            => AdhocVoiceInvite.BuildBody(g.Sender.Get(g.SessionId, Alice), credential ?? g.Token, Alice, "Test User");

        private static RingIntake Offer(GridInstantMessage im, CarrierAuth auth, INonSpatialSessions store)
            => CarrierIntake.AcceptRing(im, auth, store, groupUsable: true, groupCap: 50, adhocEnabled: true);

        [Test]
        public void AGenuineRing_NeedsNoCarrierSecret_WhenTheStoreVerifiesRings()
        {
            Grid g = NewGrid();

            RingIntake r = Offer(Ring(g, Bob, Body(g)), CarrierAuth.Off(), g.Receiver);

            Assert.That(r.Claimed, Is.True);
            Assert.That(r.Ok, Is.True, r.Decision);
            Assert.That(r.Reason, Is.Null);
            Assert.That(r.Target, Is.EqualTo(Bob));
        }

        [Test]
        public void TheInvitationTheViewerSeesCarriesTheStoresCredential_NotTheWiresOne()
        {
            Grid g = NewGrid();
            GridInstantMessage forgedCredential = Ring(g, Bob, Body(g, credential: "a-credential-the-forger-made-up"));

            RingIntake r = Offer(forgedCredential, CarrierAuth.Off(), g.Receiver);

            Assert.That(r.Ok, Is.True);
            GroupVoiceRingTransport.ReadBody(r.Body, out _, out _, out string credential, out _);
            Assert.That(credential, Is.EqualTo(g.Token));
        }

        [Test]
        public void AForgedRingForASessionThatDoesNotExist_IsDropped_AndCreatesNothing()
        {
            Grid g = NewGrid();
            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", UUID.Random(), RegionA, Body(g), null);

            RingIntake r = Offer(im, CarrierAuth.Off(), g.Receiver);

            Assert.That(r.Claimed, Is.True);
            Assert.That(r.Ok, Is.False);
            Assert.That(g.Service.SessionCount, Is.EqualTo(1));
        }

        [Test]
        public void AForgedRingForARealSessionAndAnUninvitedTarget_IsDropped()
        {
            Grid g = NewGrid();

            RingIntake r = Offer(Ring(g, Carol, Body(g)), CarrierAuth.Off(), g.Receiver);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Decision, Is.EqualTo("refused-target-not-invited"));
        }

        [Test]
        public void AReplayedRing_IsDroppedTheSecondTime()
        {
            Grid g = NewGrid();
            GridInstantMessage im = Ring(g, Bob, Body(g));

            Assert.That(Offer(im, CarrierAuth.Off(), g.Receiver).Ok, Is.True);
            RingIntake second = Offer(im, CarrierAuth.Off(), g.Receiver);

            Assert.That(second.Ok, Is.False);
            Assert.That(second.Decision, Is.EqualTo("refused-ring-used"));
        }

        [Test]
        public void ARegionWithACarrierSecret_StillRequiresTheMac_EvenWhenTheStoreVerifies()
        {
            Grid g = NewGrid();
            CarrierAuth configured = new CarrierAuth(new string('s', CarrierAuth.MinSecretLength));

            RingIntake r = Offer(Ring(g, Bob, Body(g)), configured, g.Receiver);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Reason, Is.EqualTo(CarrierAuth.MissingMac));
        }

        [Test]
        public void AStoreThatCannotBeReached_DropsTheRing()
        {
            Grid g = NewGrid();
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(
                new GridStoreResilienceTests.FlakyService(g.Service) { Up = false });

            RingIntake r = Offer(Ring(g, Bob, Body(g)), CarrierAuth.Off(), receiver);

            Assert.That(r.Ok, Is.False);
            Assert.That(r.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
        }
    }
}
