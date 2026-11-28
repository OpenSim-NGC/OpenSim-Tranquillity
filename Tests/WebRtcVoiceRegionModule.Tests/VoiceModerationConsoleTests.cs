/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for the two pieces the voice-moderation console commands rest on, both of which are
 * Scene-free by construction:
 *
 *   - VoiceModerationStore.Snapshot() and the bool-returning UnmuteAgent - the read surface the
 *     listing command needs and the "did anything actually change" answer the unmute reports.
 *   - VoiceModerationTargets.Resolve - the operator-typed token to one agent, including the two
 *     answers that matter operationally: absent and ambiguous.
 *
 * The console handlers themselves are not tested here: they need MainConsole, a live Scene, a land
 * channel and a user-management module, which is exactly the boundary this split was drawn at.
 */

using System.Collections.Generic;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class VoiceModerationStoreSnapshotTests
    {
        [Fact(Timeout = 60000)]
        public void Snapshot_EmptyStore_IsEmpty()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            Assert.Empty(s.Snapshot());
        }

        [Fact(Timeout = 60000)]
        public void Snapshot_ReportsMutedAgentsPerParcel()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID a = UUID.Random();
            UUID b = UUID.Random();
            s.MuteAgent(parcel, a);
            s.MuteAgent(parcel, b);

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            Assert.Single(snap);
            Assert.Equal(parcel, snap[0].ParcelGlobalId);
            Assert.False(snap[0].MuteEveryone);
            Assert.Equivalent(new[] { a, b }, snap[0].MutedAgents, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void Snapshot_ReportsMuteEveryoneWithNoIndividualMutes()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            s.SetMuteEveryone(parcel, true);

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            Assert.Single(snap);
            Assert.True(snap[0].MuteEveryone);
            Assert.Empty(snap[0].MutedAgents);
        }

        [Fact(Timeout = 60000)]
        public void Snapshot_IsADetachedCopy_LaterMutesDoNotAppearInIt()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            s.MuteAgent(parcel, UUID.Random());

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            s.MuteAgent(parcel, UUID.Random());

            {
                var assertedValue = snap[0].MutedAgents;
                TestAssert.WithMessage(() => Assert.Single(assertedValue), "the snapshot must not alias the live set");
            }
            Assert.Equal(2, s.Snapshot()[0].MutedAgents.Count());
        }

        [Fact(Timeout = 60000)]
        public void Snapshot_OrdersParcelsStably()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            var parcels = new List<UUID>();
            for (int i = 0; i < 8; i++)
            {
                UUID p = UUID.Random();
                parcels.Add(p);
                s.MuteAgent(p, UUID.Random());
            }

            var first = new List<UUID>();
            foreach (ParcelModerationView v in s.Snapshot())
                first.Add(v.ParcelGlobalId);
            var second = new List<UUID>();
            foreach (ParcelModerationView v in s.Snapshot())
                second.Add(v.ParcelGlobalId);

            {
                var assertedValue = second;
                TestAssert.WithMessage(() => Assert.Equal(first, assertedValue), "two listings of unchanged state must read the same");
            }
            Assert.Equivalent(parcels, first, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void UnmuteAgent_ReturnsTrueOnlyWhenAnEntryWasCleared()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();
            s.MuteAgent(parcel, agent);

            Assert.True(s.UnmuteAgent(parcel, agent), "first clear removes the entry");
            Assert.False(s.UnmuteAgent(parcel, agent), "second clear has nothing to remove");
            Assert.False(s.UnmuteAgent(UUID.Random(), agent), "unknown parcel");
            Assert.False(s.IsModerated(parcel, agent));
            {
                var assertedValue = s.Snapshot();
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "an emptied parcel is dropped from the store");
            }
        }

        [Fact(Timeout = 60000)]
        public void UnmuteAgent_LeavesMuteEveryoneAndTheParcelInPlace()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();
            s.SetMuteEveryone(parcel, true);
            s.MuteAgent(parcel, agent);

            Assert.True(s.UnmuteAgent(parcel, agent));
            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            Assert.Single(snap);
            Assert.True(snap[0].MuteEveryone);
            Assert.Empty(snap[0].MutedAgents);
            Assert.True(s.IsModerated(parcel, agent), "mute-everyone still covers this agent");
        }
    }

    public class VoiceModerationTargetsTests
    {
        private static List<VoiceModerationCandidate> Candidates(params VoiceModerationCandidate[] c)
            => new List<VoiceModerationCandidate>(c);

        [Fact(Timeout = 60000)]
        public void Resolve_Uuid_ResolvesEvenWhenNotInTheMutedSet()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID id = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                id.ToString(), Candidates(), out UUID target, out _);

            Assert.Equal(VoiceModerationTargetMatch.Resolved, m);
            Assert.Equal(id, target);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_ZeroUuid_IsNotATarget()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                UUID.Zero.ToString(), Candidates(), out UUID target, out _);

            Assert.Equal(VoiceModerationTargetMatch.NotFound, m);
            Assert.Equal(UUID.Zero, target);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_Name_MatchesCaseInsensitivelyAndIgnoresSurroundingSpace()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID id = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "  test USER ", Candidates(new VoiceModerationCandidate(id, "Test User")),
                out UUID target, out _);

            Assert.Equal(VoiceModerationTargetMatch.Resolved, m);
            Assert.Equal(id, target);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_SameAgentMutedOnSeveralParcels_IsOneCandidateNotAnAmbiguity()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID id = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "Test User",
                Candidates(new VoiceModerationCandidate(id, "Test User"),
                           new VoiceModerationCandidate(id, "Test User")),
                out UUID target, out _);

            Assert.Equal(VoiceModerationTargetMatch.Resolved, m);
            Assert.Equal(id, target);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_TwoDistinctAgentsWithOneName_IsAmbiguousAndListsBoth()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            UUID a = UUID.Random();
            UUID b = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "Test User",
                Candidates(new VoiceModerationCandidate(a, "Test User"),
                           new VoiceModerationCandidate(b, "test user")),
                out UUID target, out IReadOnlyList<VoiceModerationCandidate> ambiguous);

            Assert.Equal(VoiceModerationTargetMatch.Ambiguous, m);
            Assert.Equal(UUID.Zero, target);
            Assert.Equal(2, ambiguous.Count());
            var ids = new List<UUID>();
            foreach (VoiceModerationCandidate c in ambiguous)
                ids.Add(c.AgentId);
            Assert.Equivalent(new[] { a, b }, ids, strict: true);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_UnknownName_IsNotFound()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "Nobody Here", Candidates(new VoiceModerationCandidate(UUID.Random(), "Test User")),
                out UUID target, out _);

            Assert.Equal(VoiceModerationTargetMatch.NotFound, m);
            Assert.Equal(UUID.Zero, target);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_UnresolvedNameCandidates_NeverMatchByName()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Two entries whose names the scene could not resolve must not collide under a shared
            // pseudo-name; they stay UUID-addressable only.
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "(name unresolved)",
                Candidates(new VoiceModerationCandidate(UUID.Random(), null),
                           new VoiceModerationCandidate(UUID.Random(), "  ")),
                out _, out _);

            Assert.Equal(VoiceModerationTargetMatch.NotFound, m);
        }

        [Fact(Timeout = 60000)]
        public void Resolve_EmptyOrNullToken_IsNotFound()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(VoiceModerationTargetMatch.NotFound, VoiceModerationTargets.Resolve(null, Candidates(), out _, out _));
            Assert.Equal(VoiceModerationTargetMatch.NotFound, VoiceModerationTargets.Resolve("   ", Candidates(), out _, out _));
        }

        [Fact(Timeout = 60000)]
        public void Resolve_NullCandidateList_IsNotFound()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(VoiceModerationTargetMatch.NotFound, VoiceModerationTargets.Resolve("Test User", null, out _, out _));
        }
    }
}
