/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Idempotency under retry, reconnect and region-crossing races.
 *
 * One test per race. The concurrent ones use real threads through a Barrier
 * so they exercise the store's atomicity rather than a comfortable interleaving.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialIdempotencyTests
    {
        /// <summary>Run <paramref name="n"/> delegates as simultaneously as the scheduler allows.</summary>
        private static T[] InParallel<T>(int n, Func<int, T> body)
        {
            using Barrier gate = new Barrier(n);
            Task<T>[] tasks = new Task<T>[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                tasks[k] = Task.Factory.StartNew(() =>
                {
                    gate.SignalAndWait();
                    return body(k);
                }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);
            return tasks.Select(t => t.Result).ToArray();
        }

        // ---- race 1: a repeated start -----------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void RepeatedStart_CreatesOneSessionOneRoomAndOneMembership()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID temp = NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, Alice, Bob);

            SessionOutcome first = e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA);
            SessionOutcome second = e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA);

            Assert.True(first.Created);
            Assert.False(second.Created);
            Assert.Equal("start-idempotent", second.Decision);
            Assert.Equal(first.Session.SessionId, second.Session.SessionId);
            Assert.Equal(first.Session.RoomKey, second.Session.RoomKey);
            Assert.Single(e.Store.All());
            Assert.Equal(2, e.Store.Get(first.Session.SessionId).Members.Count);
        }

        [Fact(Timeout = 60000)]
        public void ARetriedAdhocStart_FindsTheFirstSession_BecauseTheViewerResendsItsTempId()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // The viewer mints its conference id once (llimview.cpp:2542) and re-POSTs with the same
            // one after a timeout, so a retry must not open a second room and a second start reply.
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID temp = UUID.Random();

            SessionOutcome first = e.StartAdhoc(Alice, temp, RegionA);
            SessionOutcome retry = e.StartAdhoc(Alice, temp, RegionA);

            Assert.False(retry.Created);
            Assert.Equal(first.Session.SessionId, retry.Session.SessionId);
            Assert.Single(e.Store.All());
        }

        [Fact(Timeout = 60000)]
        public void TwoDifferentAdhocStartsFromTheSameCreator_AreTwoSessions()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            {
                var assertedValue = e.Store.All().Count;
                TestAssert.WithMessage(() => Assert.Equal(2, assertedValue), "idempotency is per temp id, not per creator");
            }
        }

        // ---- race 2: both parties start the same P2P at once -------------------------------------

        [Fact(Timeout = 60000)]
        public void BothPartiesStartingTheSameP2PSimultaneously_ProduceOneSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();

            SessionOutcome[] r = InParallel(2, i => i == 0
                ? e.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Zero, RegionA)
                : e.Start(NonSpatialSessionType.P2P, Bob, Alice, UUID.Zero, RegionB));

            Assert.True(r.All(x => x.Ok));
            Assert.Equal(r[1].Session.SessionId, r[0].Session.SessionId);
            {
                var assertedValue = r.Count(x => x.Created);
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "exactly one creation");
            }
            Assert.Single(e.Store.All());
        }

        // ---- race 3: a retried invite ------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void RepeatedInvite_DoesNotDuplicateTheMembership()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            SessionOutcome first = e.Invite(id, Alice, Bob);
            SessionOutcome again = e.Invite(id, Alice, Bob);

            Assert.Equal("invited", first.Decision);
            Assert.Equal("invite-idempotent", again.Decision);
            Assert.Equal(1, e.Store.Get(id).Members.Count(m => m.AgentId == Bob));
        }

        [Fact(Timeout = 60000)]
        public void ConcurrentInvitesOfTheSameAgent_ProduceOneMembership()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            InParallel(8, _ => e.Invite(id, Alice, Bob));

            Assert.Equal(1, e.Store.Get(id).Members.Count(m => m.AgentId == Bob));
            {
                var assertedValue = e.Store.Get(id).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "still only the creator");
            }
        }

        // ---- race 4: a double accept -------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void RepeatedAccept_TakesOneSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            Assert.Equal("accepted", e.Accept(id, Bob, RegionA).Decision);
            Assert.Equal("accept-idempotent", e.Accept(id, Bob, RegionA).Decision);
            Assert.Equal(2, e.Store.Get(id).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void ConcurrentAcceptsByOneAgent_TakeOneSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            InParallel(16, _ => e.Accept(id, Bob, RegionA));

            Assert.Equal(2, e.Store.Get(id).SeatsHeld);
        }

        // ---- race 5: reconnect after departure ----------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AcceptAfterDeparture_ReSeatsWithoutDuplicating()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            e.Depart(id, Bob, DepartureReason.VoiceTeardown);
            Assert.Equal(1, e.Store.Get(id).SeatsHeld);

            SessionOutcome back = e.Accept(id, Bob, RegionA);

            Assert.True(back.Ok, back.Decision);
            Assert.Equal(1, e.Store.Get(id).Members.Count(m => m.AgentId == Bob));
            Assert.Equal(2, e.Store.Get(id).SeatsHeld);
            {
                var assertedValue = e.Store.Get(id).Find(Bob).Departure;
                TestAssert.WithMessage(() => Assert.Equal(DepartureReason.None, assertedValue), "the stale reason is cleared");
            }
        }

        [Fact(Timeout = 60000)]
        public void ARepeatStartAfterTheCreatorDeparted_ReSeatsTheCreator()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID temp = NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, Alice, Bob);
            UUID id = e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA).Session.SessionId;
            e.Depart(id, Alice, DepartureReason.ChatLeave);

            e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA);

            Assert.Equal(MemberState.Accepted, e.Store.Get(id).Find(Alice).State);
            Assert.Single(e.Store.All());
        }

        // ---- race 6: region crossing ---------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void AcceptFromASecondRegion_RepointsTheMembershipInsteadOfAddingOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA, "vs-1");

            e.Accept(id, Bob, RegionB, "vs-2");

            Assert.Equal(1, e.Store.Get(id).Members.Count(m => m.AgentId == Bob));
            {
                var assertedValue = e.Store.Get(id).Find(Bob).OriginRegion;
                TestAssert.WithMessage(() => Assert.Equal(RegionB, assertedValue), "latest region wins");
            }
            Assert.Equal("vs-2", e.Store.Get(id).Find(Bob).ViewerSession);
            Assert.Equal(2, e.Store.Get(id).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void SimultaneousAcceptsFromTwoRegions_TakeOneSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            InParallel(8, i => e.Accept(id, Bob, i % 2 == 0 ? RegionA : RegionB));

            Assert.Equal(2, e.Store.Get(id).SeatsHeld);
            Assert.Equal(1, e.Store.Get(id).Members.Count(m => m.AgentId == Bob));
        }

        // ---- race 7: the cap boundary ---------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void ConcurrentAcceptsAtTheCapBoundary_ProduceExactlyCapSeats()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            const int cap = 10;
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, requestedCap: cap).Session.SessionId;

            UUID[] agents = Enumerable.Range(0, 40).Select(_ => UUID.Random()).ToArray();
            foreach (UUID a in agents) e.Invite(id, Alice, a);

            SessionOutcome[] r = InParallel(agents.Length, i => e.Accept(id, agents[i], RegionA));

            int seated = r.Count(x => x.Ok);
            {
                var assertedValue = seated;
                TestAssert.WithMessage(() => Assert.Equal(cap - 1, assertedValue), "the creator already held one of the " + cap + " seats");
            }
            {
                var assertedValue = e.Store.Get(id).SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(cap, assertedValue), "never cap+1");
            }
            Assert.True(r.Where(x => !x.Ok).All(x => x.Decision == SessionOutcome.Capacity));
        }

        // ---- race 8: a repeated departure -------------------------------------------------------------

        [Fact(Timeout = 60000)]
        public void RepeatedDeparture_IsANoOp_AndDoesNotFreeASecondSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);

            Assert.Equal("departed", e.Depart(id, Bob, DepartureReason.ChatLeave).Decision);
            SessionOutcome again = e.Depart(id, Bob, DepartureReason.VoiceTeardown);

            Assert.Equal("depart-idempotent", again.Decision);
            {
                var assertedValue = e.Store.Get(id).Find(Bob).Departure;
                TestAssert.WithMessage(() => Assert.Equal(DepartureReason.ChatLeave, assertedValue), "the first reason stands");
            }
            Assert.Equal(1, e.Store.Get(id).SeatsHeld);
        }

        [Fact(Timeout = 60000)]
        public void AllThreeLifecyclesFiringAtOnce_ReleaseExactlyOneSeat()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);

            DepartureReason[] reasons = { DepartureReason.ChatLeave, DepartureReason.VoiceTeardown, DepartureReason.PresenceLost };
            InParallel(reasons.Length, i => e.Depart(id, Bob, reasons[i]));

            Assert.Equal(1, e.Store.Get(id).SeatsHeld);
            Assert.Equal(MemberState.Departed, e.Store.Get(id).Find(Bob).State);
        }
    }
}
