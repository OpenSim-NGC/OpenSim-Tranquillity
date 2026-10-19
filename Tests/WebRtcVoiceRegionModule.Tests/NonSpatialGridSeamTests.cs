/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The engine is grid-scoped in SHAPE even though the store here runs in-process.
 *
 * Two things have to be true for a service to be put behind INonSpatialSessionStore without
 * touching the engine:
 *   (a) the engine holds NO state of its own -- two engine instances over one store must be
 *       indistinguishable from one, which is what two region modules on one grid would be;
 *   (b) every mutation goes through StartOrGet or Mutate, so a remote store has exactly two
 *       atomic operations to honour and no read-modify-write is stranded on the engine side.
 * The recording store below asserts (b) mechanically rather than by reading the code.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class NonSpatialGridSeamTests
    {
        /// <summary>Counts what the engine asks the store to do, and refuses to be bypassed.</summary>
        private sealed class RecordingStore : INonSpatialSessionStore
        {
            private readonly InMemoryNonSpatialSessionStore _inner = new InMemoryNonSpatialSessionStore();
            public int StartOrGets, Mutates, Gets, RoomKeyGets, Removes, Alls;

            public NonSpatialVoiceSession StartOrGet(UUID id, Func<NonSpatialVoiceSession> f, out bool created)
            { StartOrGets++; return _inner.StartOrGet(id, f, out created); }

            public NonSpatialVoiceSession Get(UUID id) { Gets++; return _inner.Get(id); }
            public NonSpatialVoiceSession GetByRoomKey(string k) { RoomKeyGets++; return _inner.GetByRoomKey(k); }
            public T Mutate<T>(UUID id, Func<NonSpatialVoiceSession, T> op, T ifMissing = default)
            { Mutates++; return _inner.Mutate(id, op, ifMissing); }
            public bool Remove(UUID id) { Removes++; return _inner.Remove(id); }
            public IReadOnlyList<NonSpatialVoiceSession> All() { Alls++; return _inner.All(); }
        }

        [Test]
        public void EveryMutatingOperation_GoesThroughTheStore()
        {
            RecordingStore store = new RecordingStore();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(store: store);

            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            Assert.That(store.StartOrGets, Is.EqualTo(1));

            int before = store.Mutates;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            e.MarkPresent(id, Bob, RegionA, "vs");
            e.Depart(id, Bob, DepartureReason.ChatLeave);
            Assert.That(store.Mutates - before, Is.EqualTo(4), "invite, accept, present and depart are each one atomic store op");
        }

        [Test]
        public void TwoEnginesOverOneStore_AreIndistinguishableFromOne()
        {
            // This is the grid case in miniature: two region modules, one voice session authority.
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine regionA = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSessionEngine regionB = NonSpatialIdentityTests.NewEngine(store: shared);

            UUID id = regionA.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            Assert.That(regionA.Invite(id, Alice, Bob).Ok, Is.True);

            // Bob answers on the OTHER region entirely.
            SessionOutcome accepted = regionB.Accept(id, Bob, RegionB);

            Assert.That(accepted.Ok, Is.True, accepted.Decision);
            Assert.That(shared.Get(id).SeatsHeld, Is.EqualTo(2));
            Assert.That(shared.All().Count, Is.EqualTo(1), "one session, not one per region");
            Assert.That(regionA.Store.Get(id).Find(Bob).OriginRegion, Is.EqualTo(RegionB));
        }

        [Test]
        public void ARepeatStartOnTheOtherRegion_FindsTheSameSession()
        {
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine regionA = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSessionEngine regionB = NonSpatialIdentityTests.NewEngine(store: shared);

            SessionOutcome a = regionA.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Zero, RegionA);
            SessionOutcome b = regionB.Start(NonSpatialSessionType.P2P, Bob, Alice, UUID.Zero, RegionB);

            Assert.That(b.Created, Is.False);
            Assert.That(b.Session.SessionId, Is.EqualTo(a.Session.SessionId));
        }

        [Test]
        public void TheRoomKeyIsDerivedFromGridAndSession_NotFromTheRegion()
        {
            // Two regions must resolve the SAME media room with no coordination, or a voice session that
            // crosses a border would land in two rooms.
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine regionA = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSessionEngine regionB = NonSpatialIdentityTests.NewEngine(store: shared);

            UUID id = regionA.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            string fromA = regionA.Store.Get(id).RoomKey;
            string fromB = regionB.Store.Get(id).RoomKey;

            Assert.That(fromB, Is.EqualTo(fromA));
            Assert.That(fromA, Is.EqualTo(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, id)));
        }

        [Test]
        public void ASessionIsFindableByItsRoomKey_WhichIsWhatAProvisionCarries()
        {
            // A multiagent provision carries `channel` and nothing else that identifies the voice session
            // (SL llvoicewebrtc.cpp:3342, Firestorm llvoicewebrtc.cpp:3696), so the reverse lookup is load-bearing.
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(store: shared);

            NonSpatialVoiceSession s = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session;

            Assert.That(shared.GetByRoomKey(s.RoomKey)?.SessionId, Is.EqualTo(s.SessionId));
            Assert.That(shared.GetByRoomKey("nsv1:adhoc:" + UUID.Random()), Is.Null);
            Assert.That(shared.GetByRoomKey(null), Is.Null);
        }

        [Test]
        public void RemovingASession_RemovesItsRoomKeyIndexToo()
        {
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSession s = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session;

            Assert.That(shared.Remove(s.SessionId), Is.True);
            Assert.That(shared.GetByRoomKey(s.RoomKey), Is.Null);
            Assert.That(shared.Remove(s.SessionId), Is.False);
        }

        [Test]
        public void MutateOnAMissingSession_ReturnsTheSuppliedDefault_RatherThanThrowing()
        {
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            SessionOutcome fallback = SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            SessionOutcome got = shared.Mutate(UUID.Random(), _ => new SessionOutcome(true, "x", null), fallback);
            Assert.That(got.Decision, Is.EqualTo(SessionOutcome.NoSuchSession));
        }
    }
}
