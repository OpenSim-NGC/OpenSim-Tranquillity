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
    [TestFixture]
    public class VoiceModerationStoreSnapshotTests
    {
        [Test]
        public void Snapshot_EmptyStore_IsEmpty()
        {
            var s = new VoiceModerationStore();
            Assert.That(s.Snapshot(), Is.Empty);
        }

        [Test]
        public void Snapshot_ReportsMutedAgentsPerParcel()
        {
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID a = UUID.Random();
            UUID b = UUID.Random();
            s.MuteAgent(parcel, a);
            s.MuteAgent(parcel, b);

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            Assert.That(snap, Has.Count.EqualTo(1));
            Assert.That(snap[0].ParcelGlobalId, Is.EqualTo(parcel));
            Assert.That(snap[0].MuteEveryone, Is.False);
            Assert.That(snap[0].MutedAgents, Is.EquivalentTo(new[] { a, b }));
        }

        [Test]
        public void Snapshot_ReportsMuteEveryoneWithNoIndividualMutes()
        {
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            s.SetMuteEveryone(parcel, true);

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            Assert.That(snap, Has.Count.EqualTo(1));
            Assert.That(snap[0].MuteEveryone, Is.True);
            Assert.That(snap[0].MutedAgents, Is.Empty);
        }

        [Test]
        public void Snapshot_IsADetachedCopy_LaterMutesDoNotAppearInIt()
        {
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            s.MuteAgent(parcel, UUID.Random());

            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            s.MuteAgent(parcel, UUID.Random());

            Assert.That(snap[0].MutedAgents, Has.Count.EqualTo(1), "the snapshot must not alias the live set");
            Assert.That(s.Snapshot()[0].MutedAgents, Has.Count.EqualTo(2));
        }

        [Test]
        public void Snapshot_OrdersParcelsStably()
        {
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

            Assert.That(second, Is.EqualTo(first), "two listings of unchanged state must read the same");
            Assert.That(first, Is.EquivalentTo(parcels));
        }

        [Test]
        public void UnmuteAgent_ReturnsTrueOnlyWhenAnEntryWasCleared()
        {
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();
            s.MuteAgent(parcel, agent);

            Assert.That(s.UnmuteAgent(parcel, agent), Is.True, "first clear removes the entry");
            Assert.That(s.UnmuteAgent(parcel, agent), Is.False, "second clear has nothing to remove");
            Assert.That(s.UnmuteAgent(UUID.Random(), agent), Is.False, "unknown parcel");
            Assert.That(s.IsModerated(parcel, agent), Is.False);
            Assert.That(s.Snapshot(), Is.Empty, "an emptied parcel is dropped from the store");
        }

        [Test]
        public void UnmuteAgent_LeavesMuteEveryoneAndTheParcelInPlace()
        {
            var s = new VoiceModerationStore();
            UUID parcel = UUID.Random();
            UUID agent = UUID.Random();
            s.SetMuteEveryone(parcel, true);
            s.MuteAgent(parcel, agent);

            Assert.That(s.UnmuteAgent(parcel, agent), Is.True);
            IReadOnlyList<ParcelModerationView> snap = s.Snapshot();
            Assert.That(snap, Has.Count.EqualTo(1));
            Assert.That(snap[0].MuteEveryone, Is.True);
            Assert.That(snap[0].MutedAgents, Is.Empty);
            Assert.That(s.IsModerated(parcel, agent), Is.True, "mute-everyone still covers this agent");
        }
    }

    [TestFixture]
    public class VoiceModerationTargetsTests
    {
        private static List<VoiceModerationCandidate> Candidates(params VoiceModerationCandidate[] c)
            => new List<VoiceModerationCandidate>(c);

        [Test]
        public void Resolve_Uuid_ResolvesEvenWhenNotInTheMutedSet()
        {
            UUID id = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                id.ToString(), Candidates(), out UUID target, out _);

            Assert.That(m, Is.EqualTo(VoiceModerationTargetMatch.Resolved));
            Assert.That(target, Is.EqualTo(id));
        }

        [Test]
        public void Resolve_ZeroUuid_IsNotATarget()
        {
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                UUID.Zero.ToString(), Candidates(), out UUID target, out _);

            Assert.That(m, Is.EqualTo(VoiceModerationTargetMatch.NotFound));
            Assert.That(target, Is.EqualTo(UUID.Zero));
        }

        [Test]
        public void Resolve_Name_MatchesCaseInsensitivelyAndIgnoresSurroundingSpace()
        {
            UUID id = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "  test USER ", Candidates(new VoiceModerationCandidate(id, "Test User")),
                out UUID target, out _);

            Assert.That(m, Is.EqualTo(VoiceModerationTargetMatch.Resolved));
            Assert.That(target, Is.EqualTo(id));
        }

        [Test]
        public void Resolve_SameAgentMutedOnSeveralParcels_IsOneCandidateNotAnAmbiguity()
        {
            UUID id = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "Test User",
                Candidates(new VoiceModerationCandidate(id, "Test User"),
                           new VoiceModerationCandidate(id, "Test User")),
                out UUID target, out _);

            Assert.That(m, Is.EqualTo(VoiceModerationTargetMatch.Resolved));
            Assert.That(target, Is.EqualTo(id));
        }

        [Test]
        public void Resolve_TwoDistinctAgentsWithOneName_IsAmbiguousAndListsBoth()
        {
            UUID a = UUID.Random();
            UUID b = UUID.Random();
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "Test User",
                Candidates(new VoiceModerationCandidate(a, "Test User"),
                           new VoiceModerationCandidate(b, "test user")),
                out UUID target, out IReadOnlyList<VoiceModerationCandidate> ambiguous);

            Assert.That(m, Is.EqualTo(VoiceModerationTargetMatch.Ambiguous));
            Assert.That(target, Is.EqualTo(UUID.Zero));
            Assert.That(ambiguous, Has.Count.EqualTo(2));
            var ids = new List<UUID>();
            foreach (VoiceModerationCandidate c in ambiguous)
                ids.Add(c.AgentId);
            Assert.That(ids, Is.EquivalentTo(new[] { a, b }));
        }

        [Test]
        public void Resolve_UnknownName_IsNotFound()
        {
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "Nobody Here", Candidates(new VoiceModerationCandidate(UUID.Random(), "Test User")),
                out UUID target, out _);

            Assert.That(m, Is.EqualTo(VoiceModerationTargetMatch.NotFound));
            Assert.That(target, Is.EqualTo(UUID.Zero));
        }

        [Test]
        public void Resolve_UnresolvedNameCandidates_NeverMatchByName()
        {
            // Two entries whose names the scene could not resolve must not collide under a shared
            // pseudo-name; they stay UUID-addressable only.
            VoiceModerationTargetMatch m = VoiceModerationTargets.Resolve(
                "(name unresolved)",
                Candidates(new VoiceModerationCandidate(UUID.Random(), null),
                           new VoiceModerationCandidate(UUID.Random(), "  ")),
                out _, out _);

            Assert.That(m, Is.EqualTo(VoiceModerationTargetMatch.NotFound));
        }

        [Test]
        public void Resolve_EmptyOrNullToken_IsNotFound()
        {
            Assert.That(VoiceModerationTargets.Resolve(null, Candidates(), out _, out _),
                Is.EqualTo(VoiceModerationTargetMatch.NotFound));
            Assert.That(VoiceModerationTargets.Resolve("   ", Candidates(), out _, out _),
                Is.EqualTo(VoiceModerationTargetMatch.NotFound));
        }

        [Test]
        public void Resolve_NullCandidateList_IsNotFound()
        {
            Assert.That(VoiceModerationTargets.Resolve("Test User", null, out _, out _),
                Is.EqualTo(VoiceModerationTargetMatch.NotFound));
        }
    }
}
