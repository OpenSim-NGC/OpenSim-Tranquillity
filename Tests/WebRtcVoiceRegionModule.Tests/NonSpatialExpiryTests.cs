/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Expiry of non-spatial sessions and invitations, driven by the maintenance pass the module runs on a
 * timer. The engine's Sweep() is correct on its own, but only the maintenance pass calls it, so these
 * tests go through NonSpatialMaintenance, the thing the module owns.
 */
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    public class NonSpatialExpiryTests
    {
        private sealed class Capture
        {
            public readonly List<string> Debug = new();
            public readonly List<(string Message, Exception Error)> Errors = new();
            public void OnDebug(string line) { lock (Debug) Debug.Add(line); }
            public void OnError(string message, Exception e) { lock (Errors) Errors.Add((message, e)); }
            public int DebugCount { get { lock (Debug) return Debug.Count; } }
        }

        private static NonSpatialMaintenance NewMaintenance(NonSpatialVoiceSessionEngine e, Capture c, TimeSpan? interval = null)
            => new NonSpatialMaintenance(e, c.OnDebug, c.OnError, interval);

        /// <summary>A store whose All() fails on demand and counts how often it is read.</summary>
        private sealed class FlakyStore : INonSpatialSessionStore
        {
            private readonly InMemoryNonSpatialSessionStore _inner = new();
            public bool FailAll;
            public int AllCalls;
            public NonSpatialVoiceSession StartOrGet(UUID id, Func<NonSpatialVoiceSession> f, out bool created) => _inner.StartOrGet(id, f, out created);
            public NonSpatialVoiceSession Get(UUID id) => _inner.Get(id);
            public NonSpatialVoiceSession GetByRoomKey(string k) => _inner.GetByRoomKey(k);
            public T Mutate<T>(UUID id, Func<NonSpatialVoiceSession, T> op, T ifMissing = default) => _inner.Mutate(id, op, ifMissing);
            public bool Remove(UUID id) => _inner.Remove(id);
            public IReadOnlyList<NonSpatialVoiceSession> All()
            {
                Interlocked.Increment(ref AllCalls);
                if (FailAll) throw new InvalidOperationException("store unavailable");
                return _inner.All();
            }
        }

        [Fact(Timeout = 60000)]
        public void AnInvitationLapsesAfterItsTtl_WhenTheMaintenancePassRuns()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, inviteTtl: TimeSpan.FromMinutes(2));
            NonSpatialMaintenance m = NewMaintenance(e, new Capture());
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            clock.Now = clock.Now.AddMinutes(1);
            m.RunOnce();
            {
                var assertedValue = e.Store.Get(id).Find(Bob).State;
                TestAssert.WithMessage(() => Assert.Equal(MemberState.Invited, assertedValue), "still inside the TTL");
            }

            clock.Now = clock.Now.AddMinutes(2);
            m.RunOnce();
            NonSpatialMember bob = e.Store.Get(id).Find(Bob);
            Assert.Equal(MemberState.Departed, bob.State);
            Assert.Equal(DepartureReason.Expired, bob.Departure);
            Assert.False(bob.HoldsSeat);
        }

        [Fact(Timeout = 60000)]
        public void AnIdleSessionIsRemoved_WhenTheMaintenancePassRuns()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            clock.Now = clock.Now.AddHours(9);
            int expired = NewMaintenance(e, new Capture()).RunOnce();

            Assert.Null(e.Store.Get(id));
            Assert.Empty(e.Store.All());
            Assert.True(expired >= 1);
        }

        [Fact(Timeout = 60000)]
        public void ALongCallWithAPresentMemberSurvivesPastTheIdleTtl()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            HashSet<UUID> here = new() { Alice, Bob };
            e.IsAgentPresent = here.Contains;
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            e.Accept(id, Bob, RegionA);
            NonSpatialMaintenance pass = NewMaintenance(e, new Capture());

            // Nobody speaks, types or joins for far longer than the idle time, and the pass runs throughout.
            for (int hour = 1; hour <= 30; hour++)
            {
                clock.Now = clock.Now.AddHours(1);
                pass.RunOnce();
            }

            NonSpatialVoiceSession kept = e.Store.Get(id);
            {
                var assertedValue = kept;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "someone is still in the call");
            }
            Assert.Equal(2, kept.SeatsHeld);
            Assert.NotEqual(MemberState.Departed, kept.Find(Alice).State);
        }

        [Fact(Timeout = 60000)]
        public void ACallIsKeptWhileOneMemberIsPresentAndExpiresOnceTheLastOneLeaves()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            HashSet<UUID> here = new() { Bob };   // Alice's viewer is gone; Bob is still there
            e.IsAgentPresent = here.Contains;
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            e.Accept(id, Bob, RegionA);
            NonSpatialMaintenance pass = NewMaintenance(e, new Capture());

            clock.Now = clock.Now.AddHours(9);
            pass.RunOnce();
            {
                var assertedValue = e.Store.Get(id);
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "Bob is present, so the call is live");
            }

            here.Clear();   // Bob goes too
            clock.Now = clock.Now.AddHours(9);
            pass.RunOnce();
            {
                var assertedValue = e.Store.Get(id);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "nobody is left, so the idle time applies");
            }
        }

        [Fact(Timeout = 60000)]
        public void ACallWhoseMembersHaveAllGoneStillExpiresAndReleasesTheirSeats()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            e.IsAgentPresent = _ => false;   // a crash or logout left both seats behind
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            e.Accept(id, Bob, RegionA);
            NonSpatialVoiceSession held = e.Store.Get(id);
            Assert.Equal(2, held.SeatsHeld);

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            Assert.Null(e.Store.Get(id));
            {
                var assertedValue = held.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "their seats are released");
            }
        }

        [Fact(Timeout = 60000)]
        public void APresentPersonWhoHoldsNoSeatDoesNotKeepACallAlive()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            e.IsAgentPresent = a => a == Bob;   // Bob is online but never accepted the invitation
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            Assert.Null(e.Store.Get(id));
        }

        [Fact(Timeout = 60000)]
        public void APresenceLookupThatFailsKeepsTheCall_NeverEndsItWithSomeoneInIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            e.IsAgentPresent = _ => throw new InvalidOperationException("scene unavailable");
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            Assert.NotNull(e.Store.Get(id));
        }

        [Fact(Timeout = 60000)]
        public void AnExpiredSessionReleasesItsSeats()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            NonSpatialVoiceSession held = e.Store.Get(id);
            Assert.Equal(2, held.SeatsHeld);

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            {
                var assertedValue = e.Store.Get(id);
                TestAssert.WithMessage(() => Assert.Null(assertedValue), "the session is gone");
            }
            {
                var assertedValue = held.SeatsHeld;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "and nobody is left holding a seat in it");
            }
            Assert.Equal(DepartureReason.Expired, held.Find(Alice).Departure);
            Assert.Equal(DepartureReason.Expired, held.Find(Bob).Departure);
            Assert.False(held.RingSent, "the ring cycle ended with the last seat");
        }

        [Fact(Timeout = 60000)]
        public void ADebugLineIsWrittenOnlyWhenSomethingExpired()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            Capture cap = new Capture();
            NonSpatialMaintenance m = NewMaintenance(e, cap);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);

            m.RunOnce();
            {
                var assertedValue = cap.DebugCount;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "nothing expired, nothing written");
            }

            clock.Now = clock.Now.AddHours(9);
            m.RunOnce();
            Assert.Equal(1, cap.DebugCount);
            Assert.Contains("sessions=1", cap.Debug[0]);
        }

        [Fact(Timeout = 60000)]
        public void AFailingPassIsCaughtAndLogged_AndTheNextPassStillRuns()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlakyStore store = new FlakyStore();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, store: store, idleTtl: TimeSpan.FromHours(8));
            Capture cap = new Capture();
            NonSpatialMaintenance m = NewMaintenance(e, cap);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            store.FailAll = true;
            {
                var assertedValue = () => m.RunOnce();
                Assert.Null(Record.Exception(() =>
                {
                    assertedValue();
                }));
            }
            Assert.Single(cap.Errors);
            Assert.IsAssignableFrom<InvalidOperationException>(cap.Errors[0].Error);

            store.FailAll = false;
            clock.Now = clock.Now.AddHours(9);
            m.RunOnce();
            Assert.Null(store.Get(id));
        }

        [Fact(Timeout = 60000)]
        public void TheTimerDrivesThePass_AndStopsWhenStopped()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            FlakyStore store = new FlakyStore();
            NonSpatialVoiceSessionEngine e = NewEngine(store: store);
            using NonSpatialMaintenance m = NewMaintenance(e, new Capture(), TimeSpan.FromMilliseconds(40));

            m.Start();
            m.Start();   // a second Start must not add a second timer
            SpinWait.SpinUntil(() => Volatile.Read(ref store.AllCalls) >= 3, TimeSpan.FromSeconds(8));
            Assert.True(Volatile.Read(ref store.AllCalls) >= 3, "the timer ran the pass");

            m.Stop();
            Thread.Sleep(100);   // let a tick that was already running finish
            int afterStop = Volatile.Read(ref store.AllCalls);
            Thread.Sleep(300);   // a "did not happen" window: no timer tick may arrive after Stop
            Assert.Equal(afterStop, Volatile.Read(ref store.AllCalls));

            m.Start();   // a region added after the last one was removed restarts it
            SpinWait.SpinUntil(() => Volatile.Read(ref store.AllCalls) > afterStop, TimeSpan.FromSeconds(8));
            Assert.True(Volatile.Read(ref store.AllCalls) > afterStop);
        }

        [Fact(Timeout = 60000)]
        public void TheDefaultIntervalIsThirtySeconds()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromSeconds(30), NonSpatialMaintenance.DefaultInterval);
        }
    }
}
