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
using Xunit;
using OpenMetaverse;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
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

        [Fact(Timeout = 60000)]
        public void EveryMutatingOperation_GoesThroughTheStore()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            RecordingStore store = new RecordingStore();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(store: store);

            UUID id = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            Assert.Equal(1, store.StartOrGets);

            int before = store.Mutates;
            e.Invite(id, Alice, Bob);
            e.Accept(id, Bob, RegionA);
            e.MarkPresent(id, Bob, RegionA, "vs");
            e.Depart(id, Bob, DepartureReason.ChatLeave);
            {
                var assertedValue = store.Mutates - before;
                TestAssert.WithMessage(() => Assert.Equal(4, assertedValue), "invite, accept, present and depart are each one atomic store op");
            }
        }

        [Fact(Timeout = 60000)]
        public void TwoEnginesOverOneStore_AreIndistinguishableFromOne()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // This is the grid case in miniature: two region modules, one voice session authority.
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine regionA = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSessionEngine regionB = NonSpatialIdentityTests.NewEngine(store: shared);

            UUID id = regionA.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            Assert.True(regionA.Invite(id, Alice, Bob).Ok);

            // Bob answers on the OTHER region entirely.
            SessionOutcome accepted = regionB.Accept(id, Bob, RegionB);

            Assert.True(accepted.Ok, accepted.Decision);
            Assert.Equal(2, shared.Get(id).SeatsHeld);
            {
                var assertedValue = shared.All().Count;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "one session, not one per region");
            }
            Assert.Equal(RegionB, regionA.Store.Get(id).Find(Bob).OriginRegion);
        }

        [Fact(Timeout = 60000)]
        public void ARepeatStartOnTheOtherRegion_FindsTheSameSession()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine regionA = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSessionEngine regionB = NonSpatialIdentityTests.NewEngine(store: shared);

            SessionOutcome a = regionA.Start(NonSpatialSessionType.P2P, Alice, Bob, UUID.Zero, RegionA);
            SessionOutcome b = regionB.Start(NonSpatialSessionType.P2P, Bob, Alice, UUID.Zero, RegionB);

            Assert.False(b.Created);
            Assert.Equal(a.Session.SessionId, b.Session.SessionId);
        }

        [Fact(Timeout = 60000)]
        public void TheRoomKeyIsDerivedFromGridAndSession_NotFromTheRegion()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Two regions must resolve the SAME media room with no coordination, or a voice session that
            // crosses a border would land in two rooms.
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine regionA = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSessionEngine regionB = NonSpatialIdentityTests.NewEngine(store: shared);

            UUID id = regionA.StartAdhoc(Alice, UUID.Random(), RegionA).Session.SessionId;
            string fromA = regionA.Store.Get(id).RoomKey;
            string fromB = regionB.Store.Get(id).RoomKey;

            Assert.Equal(fromA, fromB);
            Assert.Equal(NonSpatialRoomKey.Derive("example-grid", NonSpatialSessionType.Adhoc, id), fromA);
        }

        [Fact(Timeout = 60000)]
        public void ASessionIsFindableByItsRoomKey_WhichIsWhatAProvisionCarries()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // A multiagent provision carries `channel` and nothing else that identifies the voice session
            // (SL llvoicewebrtc.cpp:3342, Firestorm llvoicewebrtc.cpp:3696), so the reverse lookup is load-bearing.
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(store: shared);

            NonSpatialVoiceSession s = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session;

            Assert.Equal(s.SessionId, shared.GetByRoomKey(s.RoomKey)?.SessionId);
            Assert.Null(shared.GetByRoomKey("nsv1:adhoc:" + UUID.Random()));
            Assert.Null(shared.GetByRoomKey(null));
        }

        [Fact(Timeout = 60000)]
        public void RemovingASession_RemovesItsRoomKeyIndexToo()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            NonSpatialVoiceSessionEngine e = NonSpatialIdentityTests.NewEngine(store: shared);
            NonSpatialVoiceSession s = e.StartAdhoc(Alice, UUID.Random(), RegionA).Session;

            Assert.True(shared.Remove(s.SessionId));
            Assert.Null(shared.GetByRoomKey(s.RoomKey));
            Assert.False(shared.Remove(s.SessionId));
        }

        [Fact(Timeout = 60000)]
        public void MutateOnAMissingSession_ReturnsTheSuppliedDefault_RatherThanThrowing()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            InMemoryNonSpatialSessionStore shared = new InMemoryNonSpatialSessionStore();
            SessionOutcome fallback = SessionOutcome.Fail(SessionOutcome.NoSuchSession);
            SessionOutcome got = shared.Mutate(UUID.Random(), _ => new SessionOutcome(true, "x", null), fallback);
            Assert.Equal(SessionOutcome.NoSuchSession, got.Decision);
        }
    }
}
