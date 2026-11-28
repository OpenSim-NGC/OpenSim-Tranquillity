/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Unit tests for the visibility matrix producer (Addons/os-webrtc-janus/Visibility).
 *
 * Scene-free, per the house pattern: a FakeWorld implements IFeederWorld so the matrix logic is
 * driven by injected facts, the way RoomSelectionConcurrencyTests injected Func<Task<JanusRoom>>
 * instead of a live Janus. Covers the six required scenarios plus the banned-inside degenerate
 * case (Decision 2b).
 */

using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    public class VoiceStateFeederTests
    {
        private const int Room = -999;

        private static UUID Id(int n)
        {
            var b = new byte[16];
            b[15] = (byte)n;
            b[14] = (byte)(n >> 8);
            return new UUID(b, 0);
        }

        // --- Fake world -----------------------------------------------------------------------
        // Parcels live in integer "zones". A root resolves its parcel by CurrentParcelUUID; a
        // child resolves by position.X == zone (children get no crossing event).
        private sealed class FakeWorld : IFeederWorld
        {
            public readonly List<AgentView> Agents = new();
            public readonly Dictionary<int, ParcelView> Zones = new();
            public readonly Dictionary<UUID, int> ZoneByGlobalId = new();
            public EstateView EstateVal = new EstateView(true, false, _ => false);
            public bool ThrowOnSnapshot;   // simulate a live-collection enumeration blowing up

            public IReadOnlyList<AgentView> SnapshotAgents()
            {
                if (ThrowOnSnapshot)
                    throw new InvalidOperationException("Collection was modified during enumeration");
                return Agents.ToList();
            }
            public ParcelView GetParcelAt(Vector3 p) => Zones[(int)p.X];
            public ParcelView GetParcelByGlobalId(UUID id) => Zones[ZoneByGlobalId[id]];
            public EstateView Estate => EstateVal;

            public ParcelView AddZone(int zone, ParcelView parcel)
            {
                Zones[zone] = parcel;
                ZoneByGlobalId[parcel.GlobalId] = zone;
                return parcel;
            }
        }

        private static ParcelView Parcel(UUID gid, bool seeAVs = true, bool allowVoice = true,
            HashSet<UUID> banned = null, HashSet<UUID> moderated = null, bool muteEveryone = false)
            => new ParcelView(gid, seeAVs, allowVoice,
                   isBannedFromLand: a => banned != null && banned.Contains(a),
                   isRestrictedFromLand: _ => false,
                   isVoiceModerated: a => muteEveryone || (moderated != null && moderated.Contains(a)));

        private static AgentView Root(UUID id, ParcelView parcel, bool god = false)
            => new AgentView(id, false, Vector3.Zero, parcel.GlobalId, god);

        private static AgentView Child(UUID id, int zone, bool god = false)
            => new AgentView(id, true, new Vector3(zone, 0, 0), UUID.Zero, god);

        // ======================================================================================
        // 1. Iain's ban scenario: A banned from P -> A <-> P-occupants mutually hidden; a third
        //    avatar hears all. Symmetry asserted both directions.
        [Fact(Timeout = 60000)]
        public void BanScenario_BannedAndOccupantsMutuallyHidden_ThirdHearsAll()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView P = w.AddZone(1, Parcel(Id(101), banned: new HashSet<UUID> { Id(1) }));
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            UUID a = Id(1), b = Id(2), c = Id(3);
            w.Agents.Add(Root(a, Q));   // A: on Q, banned from P
            w.Agents.Add(Root(b, P));   // B: occupant of P
            w.Agents.Add(Root(c, Q));   // C: third avatar

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.True(m.IsExcluded(b, a), "occupant B must not hear A (A banned from P)");
            Assert.True(m.IsExcluded(a, b), "A must not hear occupant B (symmetric)");
            Assert.False(m.IsExcluded(c, a), "third avatar hears A");
            Assert.False(m.IsExcluded(a, c));
            Assert.False(m.IsExcluded(c, b), "third avatar hears B");
            Assert.False(m.IsExcluded(b, c));
        }

        // ======================================================================================
        // 2. SeeAVs parcel: occupants hidden from outside AND outside hidden from occupants
        //    (symmetric, per Decision 1b). Co-occupants still hear each other.
        [Fact(Timeout = 60000)]
        public void SeeAvs_OccupantAndOutsiderMutuallyHidden_CoOccupantsHearEachOther()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView H = w.AddZone(3, Parcel(Id(103), seeAVs: false));
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            UUID o1 = Id(11), o2 = Id(12), x = Id(20);
            w.Agents.Add(Root(o1, H));
            w.Agents.Add(Root(o2, H));
            w.Agents.Add(Root(x, Q));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.True(m.IsExcluded(o1, x), "outsider hidden from occupant");
            Assert.True(m.IsExcluded(x, o1), "occupant hidden from outsider (symmetric)");
            Assert.False(m.IsExcluded(o1, o2), "co-occupants of a hidden parcel hear each other");
            Assert.False(m.IsExcluded(o2, o1));
        }

        // ======================================================================================
        // 3. Estate deny-voice: AllowVoice == false empties audibility (every source excluded).
        [Fact(Timeout = 60000)]
        public void EstateVoiceDisabled_MatrixExcludesEveryone()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            w.EstateVal = new EstateView(allowVoice: false, taxFree: false, isBanned: _ => false);
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, Q));
            w.Agents.Add(Root(b, Q));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.True(m.IsExcluded(a, b));
            Assert.True(m.IsExcluded(b, a));
        }

        // ======================================================================================
        // 4. Child agent: parcel from position; matrix updates when position crosses a boundary
        //    between ticks.
        [Fact(Timeout = 60000)]
        public void ChildAgent_ParcelFromPosition_MatrixUpdatesOnBoundaryCross()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView H = w.AddZone(3, Parcel(Id(103), seeAVs: false));
            w.AddZone(2, Parcel(Id(102)));                    // Q (normal)
            UUID r = Id(30), c = Id(31);
            w.Agents.Add(Root(r, H));                         // root on hidden H
            int ci = w.Agents.Count;
            w.Agents.Add(Child(c, zone: 2));                  // child starts outside, on Q

            var feeder = new VoiceStateFeeder(w, Room);
            Assert.True(feeder.Current.IsExcluded(r, c), "child outside H is hidden from R");
            Assert.True(feeder.Current.IsExcluded(c, r));

            w.Agents[ci] = Child(c, zone: 3);                 // child walks into H
            VisibilityBatch batch = feeder.Tick();

            Assert.False(feeder.Current.IsExcluded(r, c), "co-located now: not hidden");
            Assert.False(feeder.Current.IsExcluded(c, r));
            Assert.Contains(c, batch.Removed[r]);
            Assert.Contains(r, batch.Removed[c]);
        }

        // ======================================================================================
        // 5. Delta correctness - the SeeAVs fan-out. A mover stepping onto an (empty) hidden
        //    parcel becomes hidden from EVERY outsider: the add-side fan-out the churn example
        //    (semantics doc 3.2.1) originally omitted. Sized so the fan-out count is 17.
        [Fact(Timeout = 60000)]
        public void SeeAvsFanOut_MoverEntersHiddenParcel_AddsExclusionWithEveryOutsider()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            const int Outsiders = 17;
            var w = new FakeWorld();
            w.AddZone(3, Parcel(Id(103), seeAVs: false));     // H (hidden, empty)
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));     // normal
            UUID mover = Id(50);
            var outsiders = new List<UUID>();
            for (int i = 0; i < Outsiders; i++)
            {
                UUID id = Id(60 + i);
                outsiders.Add(id);
                w.Agents.Add(Root(id, Q));
            }
            int mi = w.Agents.Count;
            w.Agents.Add(Child(mover, zone: 2));              // mover starts on Q with the outsiders

            var feeder = new VoiceStateFeeder(w, Room);
            {
                var assertedValue = feeder.Current.Listeners;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "all on one normal parcel: no exclusions");
            }

            w.Agents[mi] = Child(mover, zone: 3);             // mover crosses into empty hidden H
            VisibilityBatch batch = feeder.Tick();

            {
                var assertedValue = batch.Removed;
                TestAssert.WithMessage(() => Assert.Empty(assertedValue), "H was empty: nothing to un-hide");
            }
            {
                var assertedValue = batch.Added.Count;
                TestAssert.WithMessage(() => Assert.Equal(Outsiders + 1, assertedValue), "mover + each outsider touched");
            }
            {
                var assertedValue = batch.Added[mover].Count;
                TestAssert.WithMessage(() => Assert.Equal(Outsiders, assertedValue), "mover hidden from all 17 outsiders");
            }
            foreach (UUID o in outsiders)
                {
                    var assertedValue = batch.Added[o];
                    TestAssert.WithMessage(() => Assert.Equivalent(new[] { mover }, assertedValue, strict: true), "each outsider gains exactly the mover");
                }
        }

        // ======================================================================================
        // 6. Snapshot-replace reproduces the same set as incremental add/remove accumulation.
        [Fact(Timeout = 60000)]
        public void SnapshotReplace_EqualsIncrementalAccumulation()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView H = w.AddZone(3, Parcel(Id(103), seeAVs: false));
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            UUID o = Id(70), x = Id(71), wOut = Id(72);
            w.Agents.Add(Root(o, H));                         // occupant of hidden H
            w.Agents.Add(Root(wOut, Q));                      // outsider staying on Q
            int xi = w.Agents.Count;
            w.Agents.Add(Child(x, zone: 2));                  // outsider that will move into H

            VisibilityMatrix m1 = VisibilityMatrix.Build(w);
            w.Agents[xi] = Child(x, zone: 3);                 // x moves into H
            VisibilityMatrix m2 = VisibilityMatrix.Build(w);

            var acc = new Dictionary<UUID, HashSet<UUID>>();
            Apply(acc, DeltaComputer.Diff(VisibilityMatrix.Empty, m1, Room));   // adds
            Apply(acc, DeltaComputer.Diff(m1, m2, Room));                       // adds + removes

            foreach (UUID l in new[] { o, x, wOut })
            {
                IReadOnlyCollection<UUID> snap = DeltaComputer.SnapshotFor(m2, l, Room).Added[l];
                HashSet<UUID> accSet = acc.TryGetValue(l, out HashSet<UUID> s) ? s : new HashSet<UUID>();
                {
                    var assertedValue = accSet;
                    TestAssert.WithMessage(() => Assert.Equivalent(snap, assertedValue, strict: true), $"accumulated set for listener {l} must match its snapshot");
                }
            }
        }

        // ======================================================================================
        // 7. Degenerate case (Decision 2b): a banned avatar physically INSIDE the banning parcel
        //    is still mutually excluded from that parcel's occupants (membership-based, not
        //    position-based). Legit occupants still hear each other.
        [Fact(Timeout = 60000)]
        public void BannedAvatarInsideBanningParcel_StillMutuallyExcludedFromOccupants()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            UUID a = Id(1);
            ParcelView P = w.AddZone(1, Parcel(Id(101), banned: new HashSet<UUID> { a }));
            UUID b = Id(2), c = Id(3);
            w.Agents.Add(Root(a, P));   // A banned from P yet physically on P
            w.Agents.Add(Root(b, P));   // legit occupant
            w.Agents.Add(Root(c, P));   // legit occupant

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.True(m.IsExcluded(b, a), "occupant must not hear the banned avatar even if it is inside P");
            Assert.True(m.IsExcluded(a, b), "symmetric");
            Assert.True(m.IsExcluded(c, a));
            Assert.True(m.IsExcluded(a, c));
            Assert.False(m.IsExcluded(b, c), "two legit occupants still hear each other");
            Assert.False(m.IsExcluded(c, b));
        }

        // ======================================================================================
        // §E fix under TaxFree: a parcel ban MUST still be enforced when the estate is TaxFree.
        // TaxFree voiding parcel bans in voice was the §E defect; IsBannedIgnoringTaxFree fixes it
        // at the adapter (LandBanTests), and VisibilityRules must not re-introduce the void — its
        // parcel-ban step (VisibilityRules.cs:34) carries no TaxFree guard. The banned cross-parcel
        // pair can only be excluded by that ban rule (voice is on, no estate ban, SeeAVs on both,
        // different parcels), so this pins that TaxFree does not suppress it.
        [Fact(Timeout = 60000)]
        public void ParcelBanUnderTaxFree_StillMutuallyExcluded_TaxFreeDoesNotVoidTheBan()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            w.EstateVal = new EstateView(allowVoice: true, taxFree: true, isBanned: _ => false);
            ParcelView P = w.AddZone(1, Parcel(Id(101), banned: new HashSet<UUID> { Id(1) }));
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            UUID a = Id(1), b = Id(2), c = Id(3);
            w.Agents.Add(Root(a, Q));   // A: banned from P
            w.Agents.Add(Root(b, P));   // B: occupant of P
            w.Agents.Add(Root(c, Q));   // C: third avatar, not banned

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.True(m.IsExcluded(b, a), "under TaxFree the parcel ban still excludes: occupant B must not hear banned A");
            Assert.True(m.IsExcluded(a, b), "symmetric under TaxFree");
            Assert.False(m.IsExcluded(c, a), "unbanned third avatar still hears A under TaxFree");
            Assert.False(m.IsExcluded(a, c));
        }

        // ======================================================================================
        // Voice-ENABLE under TaxFree: a parcel with voice turned off gets no voice whether or not the
        // estate is TaxFree. TaxFree only means the sim skips parcel access checks; it is not a way
        // to switch a parcel's voice back on.
        [Fact(Timeout = 60000)]
        public void VoiceOffParcelUnderTaxFree_IsNotAudible()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            w.AddZone(1, Parcel(Id(101), allowVoice: false));   // parcel voice-chat OFF
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, w.Zones[1]));
            w.Agents.Add(Root(b, w.Zones[1]));

            w.EstateVal = new EstateView(allowVoice: true, taxFree: true, isBanned: _ => false);
            VisibilityMatrix taxFree = VisibilityMatrix.Build(w);
            Assert.True(taxFree.IsExcluded(a, b), "TaxFree does not override the parcel voice-off flag");
            Assert.True(taxFree.IsExcluded(b, a), "symmetric");

            w.EstateVal = new EstateView(allowVoice: true, taxFree: false, isBanned: _ => false);
            VisibilityMatrix noTaxFree = VisibilityMatrix.Build(w);
            Assert.True(noTaxFree.IsExcluded(a, b), "and the same without TaxFree");
        }

        // ======================================================================================
        // Voice moderation: mute-everyone on a parcel makes every speaker on it inaudible
        // to ALL listeners. Source-side, so a muted speaker still HEARS others.
        [Fact(Timeout = 60000)]
        public void ModerationMuteEveryone_ExcludesSpeakerForAllListeners()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView M = w.AddZone(1, Parcel(Id(201), muteEveryone: true));   // moderated parcel
            ParcelView Q = w.AddZone(2, Parcel(Id(202)));                       // normal
            UUID s = Id(1), l1 = Id(2), l2 = Id(3);
            w.Agents.Add(Root(s, M));    // speaker on the moderated parcel
            w.Agents.Add(Root(l1, Q));   // listener on another parcel
            w.Agents.Add(Root(l2, M));   // listener co-located with the speaker

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            // Option A: moderation is now the MUTE channel (row stays, greyed), NOT the exclusion set.
            Assert.True(m.IsMuted(l1, s), "moderated speaker is muted for a listener on another parcel");
            Assert.True(m.IsMuted(l2, s), "moderated speaker is muted even for a co-located listener");
            Assert.False(m.IsExcluded(l1, s), "moderation does NOT exclude/remove the row (it mutes)");
            Assert.False(m.IsExcluded(l2, s), "moderation does NOT exclude/remove the row (it mutes)");
            Assert.False(m.IsMuted(s, l1), "source-side: the muted speaker still hears others");
        }

        // ======================================================================================
        // An individually-muted agent is excluded; an un-muted co-occupant of the same parcel is not.
        [Fact(Timeout = 60000)]
        public void ModerationIndividualMute_ExcludesOnlyThatAgent()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            UUID muted = Id(1), other = Id(2), listener = Id(3);
            ParcelView M = w.AddZone(1, Parcel(Id(201), moderated: new HashSet<UUID> { muted }));
            w.Agents.Add(Root(muted, M));
            w.Agents.Add(Root(other, M));      // same parcel, NOT muted
            w.Agents.Add(Root(listener, M));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.True(m.IsMuted(listener, muted), "individually-muted agent is muted (row stays, greyed)");
            Assert.False(m.IsExcluded(listener, muted), "moderation mutes, it does not exclude/remove");
            Assert.False(m.IsMuted(listener, other), "an un-muted co-occupant is not muted");
            Assert.False(m.IsExcluded(listener, other), "an un-muted co-occupant is still audible");
        }

        // ======================================================================================
        // A parcel with no moderation excludes nobody by moderation.
        [Fact(Timeout = 60000)]
        public void ModerationAbsent_NoOneExcluded()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView P = w.AddZone(1, Parcel(Id(201)));   // no moderation
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, P));
            w.Agents.Add(Root(b, P));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.False(m.IsExcluded(a, b));
            Assert.False(m.IsExcluded(b, a));
        }

        // ======================================================================================
        // PARCEL-STICKY, not avatar-sticky: the rule reads the source's CURRENT parcel, so a
        // moderated avatar who moves off the moderated parcel is no longer moderated. (Uses a child
        // agent, whose parcel is position-derived, to move between ticks.)
        [Fact(Timeout = 60000)]
        public void ModerationParcelSticky_MoverLeavingModeratedParcelIsAudibleAgain()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            UUID mover = Id(1), listener = Id(2);
            w.AddZone(1, Parcel(Id(201), moderated: new HashSet<UUID> { mover }));   // M: mover muted here
            ParcelView Q = w.AddZone(2, Parcel(Id(202)));                            // Q: clean
            w.Agents.Add(Root(listener, Q));
            int mi = w.Agents.Count;
            w.Agents.Add(Child(mover, zone: 1));   // mover starts ON the moderated parcel M

            VisibilityMatrix m1 = VisibilityMatrix.Build(w);
            Assert.True(m1.IsMuted(listener, mover), "muted while on the moderated parcel");

            w.Agents[mi] = Child(mover, zone: 2);  // mover walks onto Q
            VisibilityMatrix m2 = VisibilityMatrix.Build(w);
            Assert.False(m2.IsMuted(listener, mover), "parcel-sticky: no longer moderated after leaving the parcel");
        }

        // ======================================================================================
        // Hardening: a derivation exception on a tick is reported and swallowed - the
        // last good matrix is kept and the next tick re-derives (the timer is never killed).
        [Fact(Timeout = 60000)]
        public void Tick_WhenDerivationThrows_LogsAndKeepsLastMatrix_ThenRecovers()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView H = w.AddZone(3, Parcel(Id(103), seeAVs: false));
            w.AddZone(2, Parcel(Id(102)));
            UUID r = Id(80), c = Id(81);
            w.Agents.Add(Root(r, H));
            int ci = w.Agents.Count;
            w.Agents.Add(Child(c, zone: 2));                 // outside -> mutually hidden with R

            Exception captured = null;
            var feeder = new VoiceStateFeeder(w, Room, ex => captured = ex);
            Assert.True(feeder.Current.IsExcluded(r, c));

            w.ThrowOnSnapshot = true;
            VisibilityBatch bad = feeder.Tick();
            {
                var assertedValue = captured;
                TestAssert.WithMessage(() => Assert.NotNull(assertedValue), "derivation exception must be reported, not thrown");
            }
            Assert.True(bad.IsEmpty, "no batch emitted on a failed tick");
            Assert.True(feeder.Current.IsExcluded(r, c), "last good matrix preserved across failure");

            w.ThrowOnSnapshot = false;
            w.Agents[ci] = Child(c, zone: 3);                // recover: child steps into H
            VisibilityBatch good = feeder.Tick();
            Assert.False(feeder.Current.IsExcluded(r, c), "next tick re-derives after recovery");
            Assert.Contains(c, good.Removed[r]);
        }

        // ======================================================================================
        // Debug guard: the tick thread is captured on first tick and is stable across
        // ticks (single-threaded mutation). The Debug.Assert inside the feeder fires only on a
        // cross-thread mutation; here we assert the observable invariant holds under normal use.
        [Fact(Timeout = 60000)]
        public void TickThread_CapturedOnFirstTick_AndStable()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            w.AddZone(2, Parcel(Id(102)));
            w.Agents.Add(Child(Id(90), zone: 2));

            var feeder = new VoiceStateFeeder(w, Room);
            {
                var assertedValue = feeder.TickThreadId;
                TestAssert.WithMessage(() => Assert.Equal(0, assertedValue), "no tick thread before the first tick");
            }

            feeder.Tick();
            int t = feeder.TickThreadId;
            Assert.Equal(System.Environment.CurrentManagedThreadId, t);

            feeder.Tick();
            {
                var assertedValue = feeder.TickThreadId;
                TestAssert.WithMessage(() => Assert.Equal(t, assertedValue), "tick thread stays fixed across ticks");
            }
        }

        // Apply a delta batch onto an accumulator (test helper mirroring the mixer's merge).
        private static void Apply(Dictionary<UUID, HashSet<UUID>> acc, VisibilityBatch b)
        {
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in b.Added)
            {
                if (!acc.TryGetValue(kv.Key, out HashSet<UUID> s))
                {
                    s = new HashSet<UUID>();
                    acc[kv.Key] = s;
                }
                foreach (UUID u in kv.Value) s.Add(u);
            }
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in b.Removed)
            {
                if (acc.TryGetValue(kv.Key, out HashSet<UUID> s))
                {
                    foreach (UUID u in kv.Value) s.Remove(u);
                    if (s.Count == 0) acc.Remove(kv.Key);
                }
            }
        }
        // ======================================================================================
        // RESUME REBUILDS FROM THE WORLD.
        //
        // When the feeder is starved past the mixer's staleness window, the service calls Invalidate() before the
        // tick that finally runs. That tick must describe the world as it is NOW -- after a stall of 60 s or more,
        // who is in the region and on which parcel can be entirely different -- and not replay the view
        // cached before the stall.
        // ======================================================================================

        [Fact(Timeout = 60000)]
        public void AfterInvalidate_TheNextTickDescribesTheCurrentWorld_NotThePreStallView()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var w = new FakeWorld();
            ParcelView P = w.AddZone(1, Parcel(Id(201), banned: new HashSet<UUID> { Id(1) }));
            ParcelView Q = w.AddZone(2, Parcel(Id(202)));
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, Q));   // A on Q, banned from P
            w.Agents.Add(Root(b, P));   // B occupies P

            var feeder = new VoiceStateFeeder(w, Room);
            Assert.True(feeder.Current.IsExcluded(b, a), "before the stall: B cannot hear the banned A");

            // The stall. While the feeder is not ticking the world moves on: B leaves P for Q, and a newcomer C
            // arrives. A cached view would still be hiding A from B and would not know C exists at all.
            w.Agents[1] = Root(b, Q);
            UUID c = Id(3);
            w.Agents.Add(Root(c, Q));

            feeder.Invalidate();
            Assert.True(feeder.HasPendingInvalidation, "Invalidate arms the rebuild the resume path needs");
            feeder.Tick();

            Assert.False(feeder.HasPendingInvalidation, "the rebuild consumed the invalidation");
            Assert.False(feeder.Current.IsExcluded(b, a), "after the resume B is on Q with A: the pre-stall exclusion must be gone");
            Assert.False(feeder.Current.IsExcluded(a, b));
            // and the newcomer is known, which a replayed snapshot could not have told us
            Assert.False(feeder.Current.IsExcluded(c, a), "C arrived during the stall and hears A");
            Assert.False(feeder.Current.IsExcluded(a, c));
        }

        // ======================================================================================
        // ALARM AND REBUILD TOGETHER: the alarm must actually reach the rebuild.
        //
        // The test above proves Invalidate() rebuilds from the world. This one proves the wiring around it: the
        // Watchdog's alarm trips the latch by the same route OnFeederAlarm uses, and the tick that follows
        // CONSUMES it and calls Invalidate. The failure being excluded is the quiet one -- alarm fires, flag is
        // set, nothing consumes it, and the next tick derives happily from the pre-stall cache.
        // ======================================================================================

        [Fact(Timeout = 60000)]
        public void AWatchdogAlarm_MakesTheNextTickRebuild_AndOnlyThatTick()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var latch = new FeederResumeLatch();
            int invalidated = 0;

            // A tick with no alarm behind it must not rebuild: rebuilding every tick would throw away the delta
            // machinery the feeder exists for.
            Assert.False(latch.ResumeIfStalled(() => invalidated++), "no alarm, no resume");
            Assert.Equal(0, invalidated);

            latch.Trip();                       // <- exactly what OnFeederAlarm does, off the Watchdog's thread
            Assert.True(latch.IsTripped);

            Assert.True(latch.ResumeIfStalled(() => invalidated++), "the tick after the alarm resumes");
            {
                var assertedValue = invalidated;
                TestAssert.WithMessage(() => Assert.Equal(1, assertedValue), "and it invalidated, so the derivation rebuilds from the world");
            }
            Assert.False(latch.IsTripped, "the latch is consumed");

            // One alarm, one rebuild. The tick after that is an ordinary delta tick again.
            Assert.False(latch.ResumeIfStalled(() => invalidated++), "a consumed latch does not re-fire");
            Assert.Equal(1, invalidated);

            // A stall that begins again is a fresh resume, not swallowed by the previous one.
            latch.Trip();
            Assert.True(latch.ResumeIfStalled(() => invalidated++), "a second stall rebuilds again");
            Assert.Equal(2, invalidated);
        }

    }
}
