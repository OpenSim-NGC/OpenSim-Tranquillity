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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;
using static osWebRtcVoice.Tests.GridStoreResilienceTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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
            Assert.That(r.Outbox.Count, Is.EqualTo(1), "the departure waits in the outbox");
            r.Clock.Now += TimeSpan.FromSeconds(61);
            r.Flaky.Up = true;
            r.Outbox.RunOnce();
            Assert.That(r.Outbox.Dropped, Is.EqualTo(1), "and is given up after its age limit");
        }

        [Test]
        public void ADepartureTheServiceNeverReceived_StillFreesTheSeatWhenTheLeaseRunsOut_WhileTheAgentStaysInTheRegion()
        {
            Region r = NewRegionOver(Alice, Bob);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Accept(GroupOne, Bob, RegionA);
            OneInterval(r);
            Assert.That(SeatHeld(r.Service, GroupOne, Alice), Is.True);

            DepartIsLost(r, GroupOne, Alice);
            for (int i = 0; i < 4; i++)   // longer than the 180 s lease
                OneInterval(r);

            Assert.That(SeatHeld(r.Service, GroupOne, Alice), Is.False, "Alice left the call here; her seat is not renewed, so the lease returns it");
            Assert.That(SeatHeld(r.Service, GroupOne, Bob), Is.True, "Bob's seat is still held here and is kept");
            Assert.That(r.InRegion, Does.Contain(Alice), "all the while Alice stayed in the region");
        }

        [Test]
        public void OnlyTheSeatThatWasDeparted_IsLeftToTheLease_AnotherSeatOfTheSameAgentIsKept()
        {
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            OneInterval(r);

            DepartIsLost(r, GroupOne, Alice);
            for (int i = 0; i < 4; i++)
                OneInterval(r);

            Assert.That(SeatHeld(r.Service, GroupOne, Alice), Is.False, "the departed seat is released by its lease");
            Assert.That(SeatHeld(r.Service, GroupTwo, Alice), Is.True, "the seat she still holds here is renewed");
        }

        [Test]
        public void ASeatTakenAgainAfterADeparture_IsRenewedAgain()
        {
            // The queued departure is dropped when the agent takes the seat again (the departure would remove the seat
            // just taken), and the seat is one the region holds once more, so it is kept.
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Flaky.Up = false;
            r.Sessions.Depart(GroupOne, Alice, DepartureReason.VoiceTeardown);
            r.Flaky.Up = true;
            Assert.That(r.Outbox.Count, Is.EqualTo(1));
            r.Sessions.Accept(GroupOne, Alice, RegionA);
            Assert.That(r.Outbox.Count, Is.EqualTo(0), "the stale departure is dropped");

            for (int i = 0; i < 4; i++)
                OneInterval(r);

            Assert.That(SeatHeld(r.Service, GroupOne, Alice), Is.True);
        }

        [Test]
        public void ASeatAnotherRegionTook_IsNotKeptAliveByThisRegionJustBecauseTheAgentStandsHere()
        {
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

            Assert.That(SeatHeld(a.Service, GroupOne, Alice), Is.True, "Alice's seat is region A's and region A keeps it");
            Assert.That(SeatHeld(a.Service, GroupOne, Bob), Is.False, "Bob's seat is region B's; region A does not renew it");
        }

        // ---- what the region records as held ---------------------------------------------------------------------

        [Test]
        public void ASeatIsHeldFromTheMomentTheRegionTakesIt_UntilTheRegionDepartsIt()
        {
            Region r = NewRegionOver(Alice, Bob);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Accept(GroupOne, Bob, RegionA, "vs-bob");
            Assert.That(r.Sessions.HeldSeats(), Is.EquivalentTo(new[] { new SeatKey(GroupOne, Alice), new SeatKey(GroupOne, Bob) }));

            r.Sessions.Depart(GroupOne, Bob, DepartureReason.VoiceTeardown);

            Assert.That(r.Sessions.HeldSeats(), Is.EquivalentTo(new[] { new SeatKey(GroupOne, Alice) }));
        }

        [Test]
        public void ADepartureForgetsTheSeatWhetherOrNotTheServiceReceivedIt()
        {
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            r.Flaky.Up = false;

            r.Sessions.Depart(GroupOne, Alice, DepartureReason.VoiceTeardown);

            Assert.That(r.Sessions.HeldSeats(), Is.EquivalentTo(new[] { new SeatKey(GroupTwo, Alice) }));
        }

        [Test]
        public void ALogoutForgetsOnlyTheSeatTaggedWithThatViewerSession()
        {
            Region r = NewRegionOver(Alice);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            r.Sessions.MarkPresent(GroupOne, Alice, RegionA, "vs-one");
            r.Sessions.MarkPresent(GroupTwo, Alice, RegionA, "vs-two");
            r.Flaky.Up = false;

            r.Sessions.DepartByViewerSession(Alice, "vs-one", DepartureReason.VoiceTeardown);

            Assert.That(r.Sessions.HeldSeats(), Is.EquivalentTo(new[] { new SeatKey(GroupTwo, Alice) }));
        }

        [Test]
        public void AnAgentLeavingTheRegion_ForgetsEverySeatItHeld_ThatCouldNotBeDeparted()
        {
            Region r = NewRegionOver(Alice, Bob);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);
            r.Sessions.Accept(GroupOne, Bob, RegionA);
            r.Flaky.Up = false;

            r.Sessions.DepartAll(Alice, DepartureReason.PresenceLost);

            Assert.That(r.Sessions.HeldSeats(), Is.EquivalentTo(new[] { new SeatKey(GroupOne, Bob) }));
        }

        [Test]
        public void ADeclinedInvitation_IsNotAHeldSeat()
        {
            Region r = NewRegionOver(Alice, Bob);
            UUID id = r.Sessions.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            r.Sessions.Accept(id, Bob, RegionA);
            Assert.That(r.Sessions.HeldSeats(), Does.Contain(new SeatKey(id, Bob)));

            r.Sessions.Decline(id, Bob);

            Assert.That(r.Sessions.HeldSeats(), Does.Not.Contain(new SeatKey(id, Bob)));
        }

        [Test]
        public void ARefusedOrUnansweredRequest_HoldsNoSeat()
        {
            Region r = NewRegionOver(Alice);
            r.Flaky.Up = false;

            SessionOutcome started = r.Sessions.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);

            Assert.That(started.Ok, Is.False);
            Assert.That(r.Sessions.HeldSeats(), Is.Empty);
        }

        // ---- the service renews by seat ----------------------------------------------------------------------------

        [Test]
        public void TheServiceRenewsTheSeatsNamed_NotEveryOtherSeatOfTheSameAgent()
        {
            FakeClock clock = Clock();
            VoiceSessionService service = NewReal(clock);
            GridNonSpatialSessions region = NewRegion(service, clock: clock);
            region.Start(NonSpatialSessionType.Group, Alice, GroupOne, GroupOne, RegionA);
            region.Start(NonSpatialSessionType.Group, Alice, GroupTwo, GroupTwo, RegionA);

            clock.Now += TimeSpan.FromSeconds(120);
            region.RenewSeats(new List<SeatKey> { new SeatKey(GroupTwo, Alice) });
            clock.Now += TimeSpan.FromSeconds(100);
            VoiceSessionService.SweepResult swept = service.Sweep();

            Assert.That(swept.LeasesExpired, Is.EqualTo(1));
            Assert.That(SeatHeld(service, GroupOne, Alice), Is.False, "not named, so its lease ran out");
            Assert.That(SeatHeld(service, GroupTwo, Alice), Is.True);
        }

        [Test]
        public void ARenewalWithNoSeats_IsAnsweredAndChangesNothing()
        {
            VoiceSessionService service = NewReal(Clock());

            SessionReply reply = service.RenewSeats(new List<SeatKey>());

            Assert.That(reply.Ok, Is.True);
            Assert.That(reply.Count, Is.EqualTo(0));
        }
    }
}
