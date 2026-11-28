/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A region keeps alive in the grid session service the seats it still holds, and only those. A seat it has departed
 * is never renewed, whether or not the departure reached the service, so the service's seat lease gives the seat
 * back even while the agent stays in the region.
 */
using System;
using System.Collections.Generic;
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;
using static osWebRtcVoice.Tests.GridStoreResilienceTests;

namespace osWebRtcVoice.Tests
{
    public class SeatRenewalTests
    {
        private static readonly UUID GroupOne = new UUID("99999999-9999-9999-9999-9999999999a1");
        private static readonly UUID GroupTwo = new UUID("99999999-9999-9999-9999-9999999999a2");
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

        private sealed class Region
        {
            public FakeClock Clock = Clock();
            public VoiceSessionService Service;
            public FlakyService Flaky;
            public DepartOutbox Outbox;
            public GridNonSpatialSessions Sessions;
            public GridSeatRenewer Renewer;
            public HashSet<UUID> InRegion = new HashSet<UUID>();
        }

        /// <summary>A region over a service that can stop answering, with every agent in <paramref name="agents"/> staying in it.</summary>
        private static Region NewRegionOver(params UUID[] agents)
        {
            Region r = new Region();
            r.Service = NewReal(r.Clock);
            r.Flaky = new FlakyService(r.Service);
            r.Outbox = NewOutbox(r.Flaky, r.Clock, ageSeconds: 60);
            r.Sessions = NewRegion(r.Flaky, r.Outbox, r.Clock);
            foreach (UUID a in agents) r.InRegion.Add(a);
            r.Renewer = new GridSeatRenewer(r.Sessions, () => new List<UUID>(r.InRegion), (message, e) => { }, Interval);
            return r;
        }

        /// <summary>The agent holds a seat in the session. A session the service has let go of holds none.</summary>
        private static bool SeatHeld(VoiceSessionService service, UUID session, UUID agent)
            => service.Get(session).Session != null && Holds(service, session, agent);

        /// <summary>One renewal interval goes by: the renewer runs and the service sweeps, as their timers do.</summary>
        private static void OneInterval(Region r)
        {
            r.Clock.Now += Interval;
            r.Renewer.RunOnce();
            r.Service.Sweep();
        }

        /// <summary>The departure is not received by the service and is given up by the outbox, as when the service stays down.</summary>
        private static void DepartIsLost(Region r, UUID session, UUID agent)
        {
            r.Flaky.Up = false;
            r.Sessions.Depart(session, agent, DepartureReason.VoiceTeardown);
            {
                var assertedValue = r.Outbox.Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "the departure waits in the outbox");
            }
            r.Clock.Now += TimeSpan.FromSeconds(61);
            r.Flaky.Up = true;
            r.Outbox.RunOnce();
            {
                var assertedValue = r.Outbox.Dropped;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "and is given up after its age limit");
            }
        }

        [Fact(Timeout = 60000)]
        public void ADepartureTheServiceNeverReceived_StillFreesTheSeatWhenTheLeaseRunsOut_WhileTheAgentStaysInTheRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice, Bob);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Accept(GroupOne, Bob, RegionA);
            OneInterval(r);
            Assert.True(SeatHeld(r.Service, GroupOne, Alice));

            DepartIsLost(r, GroupOne, Alice);
            for (int i = 0; i < 4; i++)   // longer than the 180 s lease
                OneInterval(r);

            Assert.False(SeatHeld(r.Service, GroupOne, Alice), "Alice left the call here; her seat is not renewed, so the lease returns it");
            Assert.True(SeatHeld(r.Service, GroupOne, Bob), "Bob's seat is still held here and is kept");
            {
                var assertedValue = r.InRegion;
                TestAssert.WithMessage(() => Assert.Contains(Alice, assertedValue), "all the while Alice stayed in the region");
            }
        }

        [Fact(Timeout = 60000)]
        public void OnlyTheSeatThatWasDeparted_IsLeftToTheLease_AnotherSeatOfTheSameAgentIsKept()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            OneInterval(r);

            DepartIsLost(r, GroupOne, Alice);
            for (int i = 0; i < 4; i++)
                OneInterval(r);

            Assert.False(SeatHeld(r.Service, GroupOne, Alice), "the departed seat is released by its lease");
            Assert.True(SeatHeld(r.Service, GroupTwo, Alice), "the seat she still holds here is renewed");
        }

        [Fact(Timeout = 60000)]
        public void ASeatTakenAgainAfterADeparture_IsRenewedAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The queued departure is dropped when the agent takes the seat again (the departure would remove the seat
            // just taken), and the seat is one the region holds once more, so it is kept.
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Flaky.Up = false;
            r.Sessions.Depart(GroupOne, Alice, DepartureReason.VoiceTeardown);
            r.Flaky.Up = true;
            Assert.Equal(1, r.Outbox.Count);
            r.Sessions.Accept(GroupOne, Alice, RegionA);
            {
                var assertedValue = r.Outbox.Count;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "the stale departure is dropped");
            }

            for (int i = 0; i < 4; i++)
                OneInterval(r);

            Assert.True(SeatHeld(r.Service, GroupOne, Alice));
        }

        [Fact(Timeout = 60000)]
        public void ASeatAnotherRegionTook_IsNotKeptAliveByThisRegionJustBecauseTheAgentStandsHere()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region a = NewRegionOver(Alice);
            Region b = NewRegionOver(Bob);
            b.Service = a.Service;                       // two regions, one session service
            b.Clock = a.Clock;
            b.Flaky = new FlakyService(a.Service);
            b.Outbox = NewOutbox(b.Flaky, b.Clock, ageSeconds: 60);
            b.Sessions = NewRegion(b.Flaky, b.Outbox, b.Clock);
            b.Renewer = new GridSeatRenewer(b.Sessions, () => new List<UUID>(b.InRegion), (message, e) => { }, Interval);

            a.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            b.Sessions.Accept(GroupOne, Bob, RegionB);   // Bob's seat is region B's
            a.InRegion.Add(Bob);                         // and Bob is standing in region A as well (a crossing)
            b.InRegion.Remove(Bob);                      // while region B has already lost him

            for (int i = 0; i < 4; i++)
            {
                a.Clock.Now += Interval;
                a.Renewer.RunOnce();
                b.Renewer.RunOnce();
                a.Service.Sweep();
            }

            Assert.True(SeatHeld(a.Service, GroupOne, Alice), "Alice's seat is region A's and region A keeps it");
            Assert.False(SeatHeld(a.Service, GroupOne, Bob), "Bob's seat is region B's; region A does not renew it");
        }

        // ---- what the region records as held ---------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ASeatIsHeldFromTheMomentTheRegionTakesIt_UntilTheRegionDepartsIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice, Bob);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Accept(GroupOne, Bob, RegionA, "vs-bob");
            Assert.Equivalent(new[] { new SeatKey(GroupOne, Alice), new SeatKey(GroupOne, Bob) }, r.Sessions.HeldSeats(), strict: true);

            r.Sessions.Depart(GroupOne, Bob, DepartureReason.VoiceTeardown);

            Assert.Equivalent(new[] { new SeatKey(GroupOne, Alice) }, r.Sessions.HeldSeats(), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void ADepartureForgetsTheSeatWhetherOrNotTheServiceReceivedIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            r.Flaky.Up = false;

            r.Sessions.Depart(GroupOne, Alice, DepartureReason.VoiceTeardown);

            Assert.Equivalent(new[] { new SeatKey(GroupTwo, Alice) }, r.Sessions.HeldSeats(), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void ALogoutForgetsOnlyTheSeatTaggedWithThatViewerSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            r.Sessions.MarkPresent(GroupOne, Alice, RegionA, "vs-one");
            r.Sessions.MarkPresent(GroupTwo, Alice, RegionA, "vs-two");
            r.Flaky.Up = false;

            r.Sessions.DepartByViewerSession(Alice, "vs-one", DepartureReason.VoiceTeardown);

            Assert.Equivalent(new[] { new SeatKey(GroupTwo, Alice) }, r.Sessions.HeldSeats(), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void AnAgentLeavingTheRegion_ForgetsEverySeatItHeld_ThatCouldNotBeDeparted()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice, Bob);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            r.Sessions.Accept(GroupOne, Bob, RegionA);
            r.Flaky.Up = false;

            r.Sessions.DepartAll(Alice, DepartureReason.PresenceLost);

            Assert.Equivalent(new[] { new SeatKey(GroupOne, Bob) }, r.Sessions.HeldSeats(), strict: true);
        }

        [Fact(Timeout = 60000)]
        public void ADeclinedInvitation_IsNotAHeldSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice, Bob);
            UUID id = r.Sessions.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            r.Sessions.Accept(id, Bob, RegionA);
            Assert.Contains(new SeatKey(id, Bob), r.Sessions.HeldSeats());

            r.Sessions.Decline(id, Bob);

            Assert.DoesNotContain(new SeatKey(id, Bob), r.Sessions.HeldSeats());
        }

        [Fact(Timeout = 60000)]
        public void ARefusedOrUnansweredRequest_HoldsNoSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Region r = NewRegionOver(Alice);
            r.Flaky.Up = false;

            SessionOutcome started = r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);

            Assert.False(started.Ok);
            Assert.Empty(r.Sessions.HeldSeats());
        }

        // ---- the service renews by seat ----------------------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void TheServiceRenewsTheSeatsNamed_NotEveryOtherSeatOfTheSameAgent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = Clock();
            VoiceSessionService service = NewReal(clock);
            GridNonSpatialSessions region = NewRegion(service, clock: clock);
            region.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            region.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);

            clock.Now += TimeSpan.FromSeconds(120);
            region.RenewSeats(new List<SeatKey> { new SeatKey(GroupTwo, Alice) });
            clock.Now += TimeSpan.FromSeconds(100);
            VoiceSessionService.SweepResult swept = service.Sweep();

            Assert.Equal(1, swept.LeasesExpired);
            Assert.False(SeatHeld(service, GroupOne, Alice), "not named, so its lease ran out");
            Assert.True(SeatHeld(service, GroupTwo, Alice));
        }

        [Fact(Timeout = 60000)]
        public void ARenewalWithNoSeats_IsAnsweredAndChangesNothing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceSessionService service = NewReal(Clock());

            SessionReply reply = service.RenewSeats(new List<SeatKey>());

            Assert.True(reply.Ok);
            Assert.Equal(0, reply.Count);
        }
    }
}
