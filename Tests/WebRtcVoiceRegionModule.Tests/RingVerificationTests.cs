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
using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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
            Assert.True(started.Ok, started.Decision);
            g.SessionId = started.Session.SessionId;
            g.Token = g.Sender.IssueToken(g.SessionId, Alice);
            if (markBob) g.Sender.MarkInvited(g.SessionId, new[] { Bob });
            return g;
        }

        private const int Adhoc = (int)NonSpatialSessionType.Adhoc;

        // ---- the service's answer ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ARingTheSenderMarkedIsVerified_AndCarriesTheStoresCredential()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            SessionOutcome o = g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.True(o.Ok, o.Decision);
            Assert.Equal("ring-verified", o.Decision);
            {
                var assertedValue = o.Session.Token;
                TestAssert.WithMessage(() => Assert.Equal(g.Token, assertedValue), "an invited target is given the credential");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARingForASessionThatDoesNotExist_IsRefused_AndNothingIsCreated()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            SessionOutcome o = g.Receiver.VerifyRing(UUID.Random(), Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.False(o.Ok);
            Assert.Equal(SessionOutcome.NoSuchSession, o.Decision);
            {
                var assertedValue = g.Service.SessionCount;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "only the conference that was started");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARingForATargetNobodyInvited_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            SessionOutcome o = g.Receiver.VerifyRing(g.SessionId, Carol, Alice, NonSpatialSessionType.Adhoc);

            Assert.False(o.Ok);
            Assert.Equal("refused-target-not-invited", o.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ARingBeforeTheSenderMarkedTheTarget_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid(markBob: false);

            Assert.Equal("refused-target-not-invited", g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Decision);
        }

        [Fact(Timeout = 60000)]
        public void AReplayedRing_IsRefused_AfterTheFirstUsesItUp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            Assert.True(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok);
            SessionOutcome again = g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.False(again.Ok);
            Assert.Equal("refused-ring-used", again.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ANewInvitationEntitlesTheTargetToOneMoreRing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            Assert.True(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok);
            g.Receiver.Decline(g.SessionId, Bob);                      // a mis-click; a later invite rings again
            g.Sender.MarkInvited(g.SessionId, new[] { Bob });

            Assert.True(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok);
            Assert.False(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, "and only one");
        }

        [Fact(Timeout = 60000)]
        public void ARingNamingACallerWhoHoldsNoSeat_IsRefused_AndDoesNotUseTheRingUp()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            SessionOutcome forged = g.Receiver.VerifyRing(g.SessionId, Bob, Carol, NonSpatialSessionType.Adhoc);
            Assert.False(forged.Ok);
            Assert.Equal("refused-caller-not-seated", forged.Decision);

            Assert.True(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok, "the genuine ring still arrives");
        }

        [Fact(Timeout = 60000)]
        public void ARingOfTheWrongType_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            Assert.Equal("refused-session-type-mismatch", g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Group).Decision);
        }

        [Fact(Timeout = 60000)]
        public void ARingForACallBetweenTwoPeople_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            g.Sender.Start(NonSpatialSessionType.P2P, Alice, Carol, A2ACalls.ComputeSessionId(Alice, Carol), RegionA);
            UUID call = A2ACalls.ComputeSessionId(Alice, Carol);
            g.Sender.MarkInvited(call, new[] { Carol });

            Assert.False(g.Receiver.VerifyRing(call, Carol, Alice, NonSpatialSessionType.Group).Ok);
            Assert.False(g.Receiver.VerifyRing(call, Carol, Alice, NonSpatialSessionType.Adhoc).Ok);
        }

        [Fact(Timeout = 60000)]
        public void AnEndedSessionsRing_IsRefused()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            g.Sender.Depart(g.SessionId, Alice, DepartureReason.VoiceTeardown);
            g.Service.Sweep();

            Assert.False(g.Receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc).Ok);
        }

        [Fact(Timeout = 60000)]
        public void WhenTheStoreCannotBeReached_TheRingIsRefusedAsUnavailable_NotAdopted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridStoreResilienceTests.FlakyService flaky = new GridStoreResilienceTests.FlakyService(g.Service) { Up = false };
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(flaky);

            SessionOutcome o = receiver.VerifyRing(g.SessionId, Bob, Alice, NonSpatialSessionType.Adhoc);

            Assert.False(o.Ok);
            Assert.Equal(SessionOutcome.StoreUnavailable, o.Decision);
        }

        [Fact(Timeout = 60000)]
        public void AServiceRefusesARingOfATypeItDoesNotKnow()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            Assert.Equal("refused-bad-request", g.Service.VerifyRing(g.SessionId, Bob, Alice, 99).Decision);
        }

        [Fact(Timeout = 60000)]
        public void TheInProcessEngine_StillAdoptsRatherThanVerifies()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.False(NewEngine().StoreVerifiesRings);
            Assert.True(VoiceSessionServiceTests.NewRegion(VoiceSessionServiceTests.NewService()).StoreVerifiesRings);
        }

        // ---- the receiving region ---------------------------------------------------------------------

        private static GridInstantMessage Ring(Grid g, UUID target, OSDMap body, CarrierAuth auth = null)
            => GroupVoiceRingTransport.Build(target, Alice, "Test User", g.SessionId, RegionA, body, auth);

        private static OSDMap Body(Grid g, string credential = null)
            => AdhocVoiceInvite.BuildBody(g.Sender.Get(g.SessionId, Alice), credential ?? g.Token, Alice, "Test User");

        private static RingIntake Offer(GridInstantMessage im, CarrierAuth auth, INonSpatialSessions store)
            => CarrierIntake.AcceptRing(im, auth, store, groupUsable: true, groupCap: 50, adhocEnabled: true);

        [Fact(Timeout = 60000)]
        public void AGenuineRing_NeedsNoCarrierSecret_WhenTheStoreVerifiesRings()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            RingIntake r = Offer(Ring(g, Bob, Body(g)), CarrierAuth.Off(), g.Receiver);

            Assert.True(r.Claimed);
            Assert.True(r.Ok, r.Decision);
            Assert.Null(r.Reason);
            Assert.Equal(Bob, r.Target);
        }

        [Fact(Timeout = 60000)]
        public void TheInvitationTheViewerSeesCarriesTheStoresCredential_NotTheWiresOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridInstantMessage forgedCredential = Ring(g, Bob, Body(g, credential: "a-credential-the-forger-made-up"));

            RingIntake r = Offer(forgedCredential, CarrierAuth.Off(), g.Receiver);

            Assert.True(r.Ok);
            GroupVoiceRingTransport.ReadBody(r.Body, out _, out _, out string credential, out _);
            Assert.Equal(g.Token, credential);
        }

        [Fact(Timeout = 60000)]
        public void AForgedRingForASessionThatDoesNotExist_IsDropped_AndCreatesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridInstantMessage im = GroupVoiceRingTransport.Build(Bob, Alice, "Test User", UUID.Random(), RegionA, Body(g), null);

            RingIntake r = Offer(im, CarrierAuth.Off(), g.Receiver);

            Assert.True(r.Claimed);
            Assert.False(r.Ok);
            Assert.Equal(1, g.Service.SessionCount);
        }

        [Fact(Timeout = 60000)]
        public void AForgedRingForARealSessionAndAnUninvitedTarget_IsDropped()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();

            RingIntake r = Offer(Ring(g, Carol, Body(g)), CarrierAuth.Off(), g.Receiver);

            Assert.False(r.Ok);
            Assert.Equal("refused-target-not-invited", r.Decision);
        }

        [Fact(Timeout = 60000)]
        public void AReplayedRing_IsDroppedTheSecondTime()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridInstantMessage im = Ring(g, Bob, Body(g));

            Assert.True(Offer(im, CarrierAuth.Off(), g.Receiver).Ok);
            RingIntake second = Offer(im, CarrierAuth.Off(), g.Receiver);

            Assert.False(second.Ok);
            Assert.Equal("refused-ring-used", second.Decision);
        }

        [Fact(Timeout = 60000)]
        public void ARegionWithACarrierSecret_StillRequiresTheMac_EvenWhenTheStoreVerifies()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            CarrierAuth configured = new CarrierAuth(new string('s', CarrierAuth.MinSecretLength));

            RingIntake r = Offer(Ring(g, Bob, Body(g)), configured, g.Receiver);

            Assert.False(r.Ok);
            Assert.Equal(CarrierAuth.MissingMac, r.Reason);
        }

        [Fact(Timeout = 60000)]
        public void AStoreThatCannotBeReached_DropsTheRing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Grid g = NewGrid();
            GridNonSpatialSessions receiver = VoiceSessionServiceTests.NewRegion(
                new GridStoreResilienceTests.FlakyService(g.Service) { Up = false });

            RingIntake r = Offer(Ring(g, Bob, Body(g)), CarrierAuth.Off(), receiver);

            Assert.False(r.Ok);
            Assert.Equal(SessionOutcome.StoreUnavailable, r.Decision);
        }
    }
}
