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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void RepeatedStart_CreatesOneSessionOneRoomAndOneMembership()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID temp = NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, Alice, Bob);

            SessionOutcome first = e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA);
            SessionOutcome second = e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA);

            Assert.That(first.Created, Is.True);
            Assert.That(second.Created, Is.False);
            Assert.That(second.Decision, Is.EqualTo("start-idempotent"));
            Assert.That(second.Session.SessionId, Is.EqualTo(first.Session.SessionId));
            Assert.That(second.Session.RoomKey, Is.EqualTo(first.Session.RoomKey));
            Assert.That(e.Store.All().Count, Is.EqualTo(1));
            Assert.That(e.Store.Get(first.Session.SessionId).Members.Count, Is.EqualTo(2));
        }

        [Test]
        public void ARetriedAdhocStart_FindsTheFirstSession_BecauseTheViewerResendsItsTempId()
        {
            // The viewer mints its conference id once (llimview.cpp:2542) and re-POSTs with the same
            // one after a timeout, so a retry must not open a second room and a second start reply.
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID temp = UUID.Random();

            SessionOutcome first = e.StartAdhoc(Alice, temp, RegionA);
            SessionOutcome retry = e.StartAdhoc(Alice, temp, RegionA);

            Assert.That(retry.Created, Is.False);
            Assert.That(retry.Session.SessionId, Is.EqualTo(first.Session.SessionId));
            Assert.That(e.Store.All().Count, Is.EqualTo(1));
        }

        [Test]
        public void TwoDifferentAdhocStartsFromTheSameCreator_AreTwoSessions()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);
            Assert.That(e.Store.All().Count, Is.EqualTo(2), "idempotency is per temp id, not per creator");
        }

        // ---- race 2: both parties start the same P2P at once -------------------------------------

        [Test]
        public void BothPartiesStartingTheSameP2PSimultaneously_ProduceOneSession()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();

            SessionOutcome[] r = InParallel(2, i => i == 0
                ? e.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Zero, RegionA)
                : e.Start(NonSpatialSessionType.P2P, Bob, Alice, UUID.Zero, RegionB));

            Assert.That(r.All(x => x.Ok), Is.True);
            Assert.That(r[0].Session.SessionId, Is.EqualTo(r[1].Session.SessionId));
            Assert.That(r.Count(x => x.Created), Is.EqualTo(1), "exactly one creation");
            Assert.That(e.Store.All().Count, Is.EqualTo(1));
        }

        // ---- race 3: a retried invite ------------------------------------------------------------

        [Test]
        public void RepeatedInvite_DoesNotDuplicateTheMembership()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            SessionOutcome first = e.Invite(id, Alice, Bob);
            SessionOutcome again = e.Invite(id, Alice, Bob);

            Assert.That(first.Decision, Is.EqualTo("invited"));
            Assert.That(again.Decision, Is.EqualTo("invite-idempotent"));
            Assert.That(e.Store.Get(id).Members.Count(m => m.AgentId == Bob), Is.EqualTo(1));
        }

        [Test]
        public void ConcurrentInvitesOfTheSameAgent_ProduceOneMembership()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            InParallel(8, _ => e.Invite(id, Alice, Bob));

            Assert.That(e.Store.Get(id).Members.Count(m => m.AgentId == Bob), Is.EqualTo(1));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1), "still only the creator");
        }

        // ---- race 4: a double accept -------------------------------------------------------------

        [Test]
        public void RepeatedAccept_TakesOneSeat()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            Assert.That(e.Accept(id, Bob, RegionA).Decision, Is.EqualTo("accepted"));
            Assert.That(e.Accept(id, Bob, RegionA).Decision, Is.EqualTo("accept-idempotent"));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void ConcurrentAcceptsByOneAgent_TakeOneSeat()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            InParallel(16, _ => e.Accept(id, Bob, RegionA));

            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(2));
        }

        // ---- race 5: reconnect after departure ----------------------------------------------------

        [Test]
        public void AcceptAfterDeparture_ReSeatsWithoutDuplicating()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            e.Depart(id, Bob, DepartureReason.VoiceTeardown);
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1));

            SessionOutcome back = e.Accept(id, Bob, RegionA);

            Assert.That(back.Ok, Is.True, back.Decision);
            Assert.That(e.Store.Get(id).Members.Count(m => m.AgentId == Bob), Is.EqualTo(1));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(2));
            Assert.That(e.Store.Get(id).Find(Bob).Departure, Is.EqualTo(DepartureReason.None), "the stale reason is cleared");
        }

        [Test]
        public void ARepeatStartAfterTheCreatorDeparted_ReSeatsTheCreator()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID temp = NonSpatialVoiceSessionEngine.DeriveSessionId(NonSpatialSessionType.P2P, Alice, Bob);
            UUID id = e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA).Session.SessionId;
            e.Depart(id, Alice, DepartureReason.ChatLeave);

            e.Start(NonSpatialSessionType.P2P, Alice, Bob, temp, RegionA);

            Assert.That(e.Store.Get(id).Find(Alice).State, Is.EqualTo(MemberState.Accepted));
            Assert.That(e.Store.All().Count, Is.EqualTo(1));
        }

        // ---- race 6: region crossing ---------------------------------------------------------------

        [Test]
        public void AcceptFromASecondRegion_RepointsTheMembershipInsteadOfAddingOne()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA, "vs-1");

            e.Accept(id, Bob, RegionB, "vs-2");

            Assert.That(e.Store.Get(id).Members.Count(m => m.AgentId == Bob), Is.EqualTo(1));
            Assert.That(e.Store.Get(id).Find(Bob).OriginRegion, Is.EqualTo(RegionB), "latest region wins");
            Assert.That(e.Store.Get(id).Find(Bob).ViewerSession, Is.EqualTo("vs-2"));
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(2));
        }

        [Test]
        public void SimultaneousAcceptsFromTwoRegions_TakeOneSeat()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            InParallel(8, i => e.Accept(id, Bob, i % 2 == 0 ? RegionA : RegionB));

            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(2));
            Assert.That(e.Store.Get(id).Members.Count(m => m.AgentId == Bob), Is.EqualTo(1));
        }

        // ---- race 7: the cap boundary ---------------------------------------------------------------

        [Test]
        public void ConcurrentAcceptsAtTheCapBoundary_ProduceExactlyCapSeats()
        {
            const int cap = 10;
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, requestedCap: cap).Session.SessionId;

            UUID[] agents = Enumerable.Range(0, 40).Select(_ => UUID.Random()).ToArray();
            foreach (UUID a in agents) e.Invite(id, Alice, a);

            SessionOutcome[] r = InParallel(agents.Length, i => e.Accept(id, agents[i], RegionA));

            int seated = r.Count(x => x.Ok);
            Assert.That(seated, Is.EqualTo(cap - 1), "the creator already held one of the " + cap + " seats");
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(cap), "never cap+1");
            Assert.That(r.Where(x => !x.Ok).All(x => x.Decision == SessionOutcome.Capacity), Is.True);
        }

        // ---- race 8: a repeated departure -------------------------------------------------------------

        [Test]
        public void RepeatedDeparture_IsANoOp_AndDoesNotFreeASecondSeat()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);

            Assert.That(e.Depart(id, Bob, DepartureReason.ChatLeave).Decision, Is.EqualTo("departed"));
            SessionOutcome again = e.Depart(id, Bob, DepartureReason.VoiceTeardown);

            Assert.That(again.Decision, Is.EqualTo("depart-idempotent"));
            Assert.That(e.Store.Get(id).Find(Bob).Departure, Is.EqualTo(DepartureReason.ChatLeave), "the first reason stands");
            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1));
        }

        [Test]
        public void AllThreeLifecyclesFiringAtOnce_ReleaseExactlyOneSeat()
        {
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine();
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);

            DepartureReason[] reasons = { DepartureReason.ChatLeave, DepartureReason.VoiceTeardown, DepartureReason.PresenceLost };
            InParallel(reasons.Length, i => e.Depart(id, Bob, reasons[i]));

            Assert.That(e.Store.Get(id).SeatsHeld, Is.EqualTo(1));
            Assert.That(e.Store.Get(id).Find(Bob).State, Is.EqualTo(MemberState.Departed));
        }
    }
}
