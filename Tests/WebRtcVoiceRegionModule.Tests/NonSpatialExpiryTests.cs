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
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
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

        [Test]
        public void AnInvitationLapsesAfterItsTtl_WhenTheMaintenancePassRuns()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, inviteTtl: TimeSpan.FromMinutes(2));
            NonSpatialMaintenance m = NewMaintenance(e, new Capture());
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);

            clock.Now = clock.Now.AddMinutes(1);
            m.RunOnce();
            Assert.That(e.Store.Get(id).Find(Bob).State, Is.EqualTo(MemberState.Invited), "still inside the TTL");

            clock.Now = clock.Now.AddMinutes(2);
            m.RunOnce();
            NonSpatialMember bob = e.Store.Get(id).Find(Bob);
            Assert.That(bob.State, Is.EqualTo(MemberState.Departed));
            Assert.That(bob.Departure, Is.EqualTo(DepartureReason.Expired));
            Assert.That(bob.HoldsSeat, Is.False);
        }

        [Test]
        public void AnIdleSessionIsRemoved_WhenTheMaintenancePassRuns()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            clock.Now = clock.Now.AddHours(9);
            int expired = NewMaintenance(e, new Capture()).RunOnce();

            Assert.That(e.Store.Get(id), Is.Null);
            Assert.That(e.Store.All(), Is.Empty);
            Assert.That(expired, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void ALongCallWithAPresentMemberSurvivesPastTheIdleTtl()
        {
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
            Assert.That(kept, Is.Not.Null, "someone is still in the call");
            Assert.That(kept.SeatsHeld, Is.EqualTo(2));
            Assert.That(kept.Find(Alice).State, Is.Not.EqualTo(MemberState.Departed));
        }

        [Test]
        public void ACallIsKeptWhileOneMemberIsPresentAndExpiresOnceTheLastOneLeaves()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            HashSet<UUID> here = new() { Bob };   // Alice's viewer is gone; Bob is still there
            e.IsAgentPresent = here.Contains;
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            e.Accept(id, Bob, RegionA);
            NonSpatialMaintenance pass = NewMaintenance(e, new Capture());

            clock.Now = clock.Now.AddHours(9);
            pass.RunOnce();
            Assert.That(e.Store.Get(id), Is.Not.Null, "Bob is present, so the call is live");

            here.Clear();   // Bob goes too
            clock.Now = clock.Now.AddHours(9);
            pass.RunOnce();
            Assert.That(e.Store.Get(id), Is.Null, "nobody is left, so the idle time applies");
        }

        [Test]
        public void ACallWhoseMembersHaveAllGoneStillExpiresAndReleasesTheirSeats()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            e.IsAgentPresent = _ => false;   // a crash or logout left both seats behind
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;
            e.Accept(id, Bob, RegionA);
            NonSpatialVoiceSession held = e.Store.Get(id);
            Assert.That(held.SeatsHeld, Is.EqualTo(2));

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            Assert.That(e.Store.Get(id), Is.Null);
            Assert.That(held.SeatsHeld, Is.EqualTo(0), "their seats are released");
        }

        [Test]
        public void APresentPersonWhoHoldsNoSeatDoesNotKeepACallAlive()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            e.IsAgentPresent = a => a == Bob;   // Bob is online but never accepted the invitation
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob }).Session.SessionId;

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            Assert.That(e.Store.Get(id), Is.Null);
        }

        [Test]
        public void APresenceLookupThatFailsKeepsTheCall_NeverEndsItWithSomeoneInIt()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            e.IsAgentPresent = _ => throw new InvalidOperationException("scene unavailable");
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            Assert.That(e.Store.Get(id), Is.Not.Null);
        }

        [Test]
        public void AnExpiredSessionReleasesItsSeats()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            NonSpatialVoiceSession held = e.Store.Get(id);
            Assert.That(held.SeatsHeld, Is.EqualTo(2));

            clock.Now = clock.Now.AddHours(9);
            NewMaintenance(e, new Capture()).RunOnce();

            Assert.That(e.Store.Get(id), Is.Null, "the session is gone");
            Assert.That(held.SeatsHeld, Is.EqualTo(0), "and nobody is left holding a seat in it");
            Assert.That(held.Find(Alice).Departure, Is.EqualTo(DepartureReason.Expired));
            Assert.That(held.Find(Bob).Departure, Is.EqualTo(DepartureReason.Expired));
            Assert.That(held.RingSent, Is.False, "the ring cycle ended with the last seat");
        }

        [Test]
        public void ADebugLineIsWrittenOnlyWhenSomethingExpired()
        {
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, idleTtl: TimeSpan.FromHours(8));
            Capture cap = new Capture();
            NonSpatialMaintenance m = NewMaintenance(e, cap);
            e.StartAdhoc(Alice, UUID.Random(), RegionA);

            m.RunOnce();
            Assert.That(cap.DebugCount, Is.EqualTo(0), "nothing expired, nothing written");

            clock.Now = clock.Now.AddHours(9);
            m.RunOnce();
            Assert.That(cap.DebugCount, Is.EqualTo(1));
            Assert.That(cap.Debug[0], Does.Contain("sessions=1"));
        }

        [Test]
        public void AFailingPassIsCaughtAndLogged_AndTheNextPassStillRuns()
        {
            FlakyStore store = new FlakyStore();
            FakeClock clock = new FakeClock();
            NonSpatialVoiceSessionEngine e = NewEngine(clock, store: store, idleTtl: TimeSpan.FromHours(8));
            Capture cap = new Capture();
            NonSpatialMaintenance m = NewMaintenance(e, cap);
            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;

            store.FailAll = true;
            Assert.That(() => m.RunOnce(), Throws.Nothing);
            Assert.That(cap.Errors, Has.Count.EqualTo(1));
            Assert.That(cap.Errors[0].Error, Is.InstanceOf<InvalidOperationException>());

            store.FailAll = false;
            clock.Now = clock.Now.AddHours(9);
            m.RunOnce();
            Assert.That(store.Get(id), Is.Null);
        }

        [Test]
        public void TheTimerDrivesThePass_AndStopsWhenStopped()
        {
            FlakyStore store = new FlakyStore();
            NonSpatialVoiceSessionEngine e = NewEngine(store: store);
            using NonSpatialMaintenance m = NewMaintenance(e, new Capture(), TimeSpan.FromMilliseconds(40));

            m.Start();
            m.Start();   // a second Start must not add a second timer
            SpinWait.SpinUntil(() => Volatile.Read(ref store.AllCalls) >= 3, TimeSpan.FromSeconds(8));
            Assert.That(Volatile.Read(ref store.AllCalls), Is.GreaterThanOrEqualTo(3), "the timer ran the pass");

            m.Stop();
            Thread.Sleep(100);   // let a tick that was already running finish
            int afterStop = Volatile.Read(ref store.AllCalls);
            Thread.Sleep(300);   // a "did not happen" window: no timer tick may arrive after Stop
            Assert.That(Volatile.Read(ref store.AllCalls), Is.EqualTo(afterStop));

            m.Start();   // a region added after the last one was removed restarts it
            SpinWait.SpinUntil(() => Volatile.Read(ref store.AllCalls) > afterStop, TimeSpan.FromSeconds(8));
            Assert.That(Volatile.Read(ref store.AllCalls), Is.GreaterThan(afterStop));
        }

        [Test]
        public void TheDefaultIntervalIsThirtySeconds()
        {
            Assert.That(NonSpatialMaintenance.DefaultInterval, Is.EqualTo(TimeSpan.FromSeconds(30)));
        }
    }
}
