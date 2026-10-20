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
    [TestFixture]
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
        [Test]
        public void BanScenario_BannedAndOccupantsMutuallyHidden_ThirdHearsAll()
        {
            var w = new FakeWorld();
            ParcelView P = w.AddZone(1, Parcel(Id(101), banned: new HashSet<UUID> { Id(1) }));
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            UUID a = Id(1), b = Id(2), c = Id(3);
            w.Agents.Add(Root(a, Q));   // A: on Q, banned from P
            w.Agents.Add(Root(b, P));   // B: occupant of P
            w.Agents.Add(Root(c, Q));   // C: third avatar

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.That(m.IsExcluded(b, a), Is.True, "occupant B must not hear A (A banned from P)");
            Assert.That(m.IsExcluded(a, b), Is.True, "A must not hear occupant B (symmetric)");
            Assert.That(m.IsExcluded(c, a), Is.False, "third avatar hears A");
            Assert.That(m.IsExcluded(a, c), Is.False);
            Assert.That(m.IsExcluded(c, b), Is.False, "third avatar hears B");
            Assert.That(m.IsExcluded(b, c), Is.False);
        }

        // ======================================================================================
        // 2. SeeAVs parcel: occupants hidden from outside AND outside hidden from occupants
        //    (symmetric, per Decision 1b). Co-occupants still hear each other.
        [Test]
        public void SeeAvs_OccupantAndOutsiderMutuallyHidden_CoOccupantsHearEachOther()
        {
            var w = new FakeWorld();
            ParcelView H = w.AddZone(3, Parcel(Id(103), seeAVs: false));
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            UUID o1 = Id(11), o2 = Id(12), x = Id(20);
            w.Agents.Add(Root(o1, H));
            w.Agents.Add(Root(o2, H));
            w.Agents.Add(Root(x, Q));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.That(m.IsExcluded(o1, x), Is.True, "outsider hidden from occupant");
            Assert.That(m.IsExcluded(x, o1), Is.True, "occupant hidden from outsider (symmetric)");
            Assert.That(m.IsExcluded(o1, o2), Is.False, "co-occupants of a hidden parcel hear each other");
            Assert.That(m.IsExcluded(o2, o1), Is.False);
        }

        // ======================================================================================
        // 3. Estate deny-voice: AllowVoice == false empties audibility (every source excluded).
        [Test]
        public void EstateVoiceDisabled_MatrixExcludesEveryone()
        {
            var w = new FakeWorld();
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            w.EstateVal = new EstateView(allowVoice: false, taxFree: false, isBanned: _ => false);
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, Q));
            w.Agents.Add(Root(b, Q));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.That(m.IsExcluded(a, b), Is.True);
            Assert.That(m.IsExcluded(b, a), Is.True);
        }

        // ======================================================================================
        // 4. Child agent: parcel from position; matrix updates when position crosses a boundary
        //    between ticks.
        [Test]
        public void ChildAgent_ParcelFromPosition_MatrixUpdatesOnBoundaryCross()
        {
            var w = new FakeWorld();
            ParcelView H = w.AddZone(3, Parcel(Id(103), seeAVs: false));
            w.AddZone(2, Parcel(Id(102)));                    // Q (normal)
            UUID r = Id(30), c = Id(31);
            w.Agents.Add(Root(r, H));                         // root on hidden H
            int ci = w.Agents.Count;
            w.Agents.Add(Child(c, zone: 2));                  // child starts outside, on Q

            var feeder = new VoiceStateFeeder(w, Room);
            Assert.That(feeder.Current.IsExcluded(r, c), Is.True, "child outside H is hidden from R");
            Assert.That(feeder.Current.IsExcluded(c, r), Is.True);

            w.Agents[ci] = Child(c, zone: 3);                 // child walks into H
            VisibilityBatch batch = feeder.Tick();

            Assert.That(feeder.Current.IsExcluded(r, c), Is.False, "co-located now: not hidden");
            Assert.That(feeder.Current.IsExcluded(c, r), Is.False);
            Assert.That(batch.Removed[r], Does.Contain(c));
            Assert.That(batch.Removed[c], Does.Contain(r));
        }

        // ======================================================================================
        // 5. Delta correctness - the SeeAVs fan-out. A mover stepping onto an (empty) hidden
        //    parcel becomes hidden from EVERY outsider: the add-side fan-out the churn example
        //    (semantics doc 3.2.1) originally omitted. Sized so the fan-out count is 17.
        [Test]
        public void SeeAvsFanOut_MoverEntersHiddenParcel_AddsExclusionWithEveryOutsider()
        {
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
            Assert.That(feeder.Current.Listeners, Is.Empty, "all on one normal parcel: no exclusions");

            w.Agents[mi] = Child(mover, zone: 3);             // mover crosses into empty hidden H
            VisibilityBatch batch = feeder.Tick();

            Assert.That(batch.Removed, Is.Empty, "H was empty: nothing to un-hide");
            Assert.That(batch.Added.Count, Is.EqualTo(Outsiders + 1), "mover + each outsider touched");
            Assert.That(batch.Added[mover].Count, Is.EqualTo(Outsiders), "mover hidden from all 17 outsiders");
            foreach (UUID o in outsiders)
                Assert.That(batch.Added[o], Is.EquivalentTo(new[] { mover }), "each outsider gains exactly the mover");
        }

        // ======================================================================================
        // 6. Snapshot-replace reproduces the same set as incremental add/remove accumulation.
        [Test]
        public void SnapshotReplace_EqualsIncrementalAccumulation()
        {
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
                Assert.That(accSet, Is.EquivalentTo(snap), $"accumulated set for listener {l} must match its snapshot");
            }
        }

        // ======================================================================================
        // 7. Degenerate case (Decision 2b): a banned avatar physically INSIDE the banning parcel
        //    is still mutually excluded from that parcel's occupants (membership-based, not
        //    position-based). Legit occupants still hear each other.
        [Test]
        public void BannedAvatarInsideBanningParcel_StillMutuallyExcludedFromOccupants()
        {
            var w = new FakeWorld();
            UUID a = Id(1);
            ParcelView P = w.AddZone(1, Parcel(Id(101), banned: new HashSet<UUID> { a }));
            UUID b = Id(2), c = Id(3);
            w.Agents.Add(Root(a, P));   // A banned from P yet physically on P
            w.Agents.Add(Root(b, P));   // legit occupant
            w.Agents.Add(Root(c, P));   // legit occupant

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.That(m.IsExcluded(b, a), Is.True, "occupant must not hear the banned avatar even if it is inside P");
            Assert.That(m.IsExcluded(a, b), Is.True, "symmetric");
            Assert.That(m.IsExcluded(c, a), Is.True);
            Assert.That(m.IsExcluded(a, c), Is.True);
            Assert.That(m.IsExcluded(b, c), Is.False, "two legit occupants still hear each other");
            Assert.That(m.IsExcluded(c, b), Is.False);
        }

        // ======================================================================================
        // §E fix under TaxFree: a parcel ban MUST still be enforced when the estate is TaxFree.
        // TaxFree voiding parcel bans in voice was the §E defect; IsBannedIgnoringTaxFree fixes it
        // at the adapter (LandBanTests), and VisibilityRules must not re-introduce the void — its
        // parcel-ban step (VisibilityRules.cs:34) carries no TaxFree guard. The banned cross-parcel
        // pair can only be excluded by that ban rule (voice is on, no estate ban, SeeAVs on both,
        // different parcels), so this pins that TaxFree does not suppress it.
        [Test]
        public void ParcelBanUnderTaxFree_StillMutuallyExcluded_TaxFreeDoesNotVoidTheBan()
        {
            var w = new FakeWorld();
            w.EstateVal = new EstateView(allowVoice: true, taxFree: true, isBanned: _ => false);
            ParcelView P = w.AddZone(1, Parcel(Id(101), banned: new HashSet<UUID> { Id(1) }));
            ParcelView Q = w.AddZone(2, Parcel(Id(102)));
            UUID a = Id(1), b = Id(2), c = Id(3);
            w.Agents.Add(Root(a, Q));   // A: banned from P
            w.Agents.Add(Root(b, P));   // B: occupant of P
            w.Agents.Add(Root(c, Q));   // C: third avatar, not banned

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.That(m.IsExcluded(b, a), Is.True, "under TaxFree the parcel ban still excludes: occupant B must not hear banned A");
            Assert.That(m.IsExcluded(a, b), Is.True, "symmetric under TaxFree");
            Assert.That(m.IsExcluded(c, a), Is.False, "unbanned third avatar still hears A under TaxFree");
            Assert.That(m.IsExcluded(a, c), Is.False);
        }

        // ======================================================================================
        // Voice-ENABLE under TaxFree: a parcel with voice turned off gets no voice whether or not the
        // estate is TaxFree. TaxFree only means the sim skips parcel access checks; it is not a way
        // to switch a parcel's voice back on.
        [Test]
        public void VoiceOffParcelUnderTaxFree_IsNotAudible()
        {
            var w = new FakeWorld();
            w.AddZone(1, Parcel(Id(101), allowVoice: false));   // parcel voice-chat OFF
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, w.Zones[1]));
            w.Agents.Add(Root(b, w.Zones[1]));

            w.EstateVal = new EstateView(allowVoice: true, taxFree: true, isBanned: _ => false);
            VisibilityMatrix taxFree = VisibilityMatrix.Build(w);
            Assert.That(taxFree.IsExcluded(a, b), Is.True, "TaxFree does not override the parcel voice-off flag");
            Assert.That(taxFree.IsExcluded(b, a), Is.True, "symmetric");

            w.EstateVal = new EstateView(allowVoice: true, taxFree: false, isBanned: _ => false);
            VisibilityMatrix noTaxFree = VisibilityMatrix.Build(w);
            Assert.That(noTaxFree.IsExcluded(a, b), Is.True, "and the same without TaxFree");
        }

        // ======================================================================================
        // Voice moderation: mute-everyone on a parcel makes every speaker on it inaudible
        // to ALL listeners. Source-side, so a muted speaker still HEARS others.
        [Test]
        public void ModerationMuteEveryone_ExcludesSpeakerForAllListeners()
        {
            var w = new FakeWorld();
            ParcelView M = w.AddZone(1, Parcel(Id(201), muteEveryone: true));   // moderated parcel
            ParcelView Q = w.AddZone(2, Parcel(Id(202)));                       // normal
            UUID s = Id(1), l1 = Id(2), l2 = Id(3);
            w.Agents.Add(Root(s, M));    // speaker on the moderated parcel
            w.Agents.Add(Root(l1, Q));   // listener on another parcel
            w.Agents.Add(Root(l2, M));   // listener co-located with the speaker

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            // Option A: moderation is now the MUTE channel (row stays, greyed), NOT the exclusion set.
            Assert.That(m.IsMuted(l1, s), Is.True, "moderated speaker is muted for a listener on another parcel");
            Assert.That(m.IsMuted(l2, s), Is.True, "moderated speaker is muted even for a co-located listener");
            Assert.That(m.IsExcluded(l1, s), Is.False, "moderation does NOT exclude/remove the row (it mutes)");
            Assert.That(m.IsExcluded(l2, s), Is.False, "moderation does NOT exclude/remove the row (it mutes)");
            Assert.That(m.IsMuted(s, l1), Is.False, "source-side: the muted speaker still hears others");
        }

        // ======================================================================================
        // An individually-muted agent is excluded; an un-muted co-occupant of the same parcel is not.
        [Test]
        public void ModerationIndividualMute_ExcludesOnlyThatAgent()
        {
            var w = new FakeWorld();
            UUID muted = Id(1), other = Id(2), listener = Id(3);
            ParcelView M = w.AddZone(1, Parcel(Id(201), moderated: new HashSet<UUID> { muted }));
            w.Agents.Add(Root(muted, M));
            w.Agents.Add(Root(other, M));      // same parcel, NOT muted
            w.Agents.Add(Root(listener, M));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.That(m.IsMuted(listener, muted), Is.True, "individually-muted agent is muted (row stays, greyed)");
            Assert.That(m.IsExcluded(listener, muted), Is.False, "moderation mutes, it does not exclude/remove");
            Assert.That(m.IsMuted(listener, other), Is.False, "an un-muted co-occupant is not muted");
            Assert.That(m.IsExcluded(listener, other), Is.False, "an un-muted co-occupant is still audible");
        }

        // ======================================================================================
        // A parcel with no moderation excludes nobody by moderation.
        [Test]
        public void ModerationAbsent_NoOneExcluded()
        {
            var w = new FakeWorld();
            ParcelView P = w.AddZone(1, Parcel(Id(201)));   // no moderation
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, P));
            w.Agents.Add(Root(b, P));

            VisibilityMatrix m = VisibilityMatrix.Build(w);

            Assert.That(m.IsExcluded(a, b), Is.False);
            Assert.That(m.IsExcluded(b, a), Is.False);
        }

        // ======================================================================================
        // PARCEL-STICKY, not avatar-sticky: the rule reads the source's CURRENT parcel, so a
        // moderated avatar who moves off the moderated parcel is no longer moderated. (Uses a child
        // agent, whose parcel is position-derived, to move between ticks.)
        [Test]
        public void ModerationParcelSticky_MoverLeavingModeratedParcelIsAudibleAgain()
        {
            var w = new FakeWorld();
            UUID mover = Id(1), listener = Id(2);
            w.AddZone(1, Parcel(Id(201), moderated: new HashSet<UUID> { mover }));   // M: mover muted here
            ParcelView Q = w.AddZone(2, Parcel(Id(202)));                            // Q: clean
            w.Agents.Add(Root(listener, Q));
            int mi = w.Agents.Count;
            w.Agents.Add(Child(mover, zone: 1));   // mover starts ON the moderated parcel M

            VisibilityMatrix m1 = VisibilityMatrix.Build(w);
            Assert.That(m1.IsMuted(listener, mover), Is.True, "muted while on the moderated parcel");

            w.Agents[mi] = Child(mover, zone: 2);  // mover walks onto Q
            VisibilityMatrix m2 = VisibilityMatrix.Build(w);
            Assert.That(m2.IsMuted(listener, mover), Is.False, "parcel-sticky: no longer moderated after leaving the parcel");
        }

        // ======================================================================================
        // Hardening: a derivation exception on a tick is reported and swallowed - the
        // last good matrix is kept and the next tick re-derives (the timer is never killed).
        [Test]
        public void Tick_WhenDerivationThrows_LogsAndKeepsLastMatrix_ThenRecovers()
        {
            var w = new FakeWorld();
            ParcelView H = w.AddZone(3, Parcel(Id(103), seeAVs: false));
            w.AddZone(2, Parcel(Id(102)));
            UUID r = Id(80), c = Id(81);
            w.Agents.Add(Root(r, H));
            int ci = w.Agents.Count;
            w.Agents.Add(Child(c, zone: 2));                 // outside -> mutually hidden with R

            Exception captured = null;
            var feeder = new VoiceStateFeeder(w, Room, ex => captured = ex);
            Assert.That(feeder.Current.IsExcluded(r, c), Is.True);

            w.ThrowOnSnapshot = true;
            VisibilityBatch bad = feeder.Tick();
            Assert.That(captured, Is.Not.Null, "derivation exception must be reported, not thrown");
            Assert.That(bad.IsEmpty, Is.True, "no batch emitted on a failed tick");
            Assert.That(feeder.Current.IsExcluded(r, c), Is.True, "last good matrix preserved across failure");

            w.ThrowOnSnapshot = false;
            w.Agents[ci] = Child(c, zone: 3);                // recover: child steps into H
            VisibilityBatch good = feeder.Tick();
            Assert.That(feeder.Current.IsExcluded(r, c), Is.False, "next tick re-derives after recovery");
            Assert.That(good.Removed[r], Does.Contain(c));
        }

        // ======================================================================================
        // Debug guard: the tick thread is captured on first tick and is stable across
        // ticks (single-threaded mutation). The Debug.Assert inside the feeder fires only on a
        // cross-thread mutation; here we assert the observable invariant holds under normal use.
        [Test]
        public void TickThread_CapturedOnFirstTick_AndStable()
        {
            var w = new FakeWorld();
            w.AddZone(2, Parcel(Id(102)));
            w.Agents.Add(Child(Id(90), zone: 2));

            var feeder = new VoiceStateFeeder(w, Room);
            Assert.That(feeder.TickThreadId, Is.EqualTo(0), "no tick thread before the first tick");

            feeder.Tick();
            int t = feeder.TickThreadId;
            Assert.That(t, Is.EqualTo(System.Environment.CurrentManagedThreadId));

            feeder.Tick();
            Assert.That(feeder.TickThreadId, Is.EqualTo(t), "tick thread stays fixed across ticks");
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

        [Test]
        public void AfterInvalidate_TheNextTickDescribesTheCurrentWorld_NotThePreStallView()
        {
            var w = new FakeWorld();
            ParcelView P = w.AddZone(1, Parcel(Id(201), banned: new HashSet<UUID> { Id(1) }));
            ParcelView Q = w.AddZone(2, Parcel(Id(202)));
            UUID a = Id(1), b = Id(2);
            w.Agents.Add(Root(a, Q));   // A on Q, banned from P
            w.Agents.Add(Root(b, P));   // B occupies P

            var feeder = new VoiceStateFeeder(w, Room);
            Assert.That(feeder.Current.IsExcluded(b, a), Is.True, "before the stall: B cannot hear the banned A");

            // The stall. While the feeder is not ticking the world moves on: B leaves P for Q, and a newcomer C
            // arrives. A cached view would still be hiding A from B and would not know C exists at all.
            w.Agents[1] = Root(b, Q);
            UUID c = Id(3);
            w.Agents.Add(Root(c, Q));

            feeder.Invalidate();
            Assert.That(feeder.HasPendingInvalidation, Is.True, "Invalidate arms the rebuild the resume path needs");
            feeder.Tick();

            Assert.That(feeder.HasPendingInvalidation, Is.False, "the rebuild consumed the invalidation");
            Assert.That(feeder.Current.IsExcluded(b, a), Is.False,
                "after the resume B is on Q with A: the pre-stall exclusion must be gone");
            Assert.That(feeder.Current.IsExcluded(a, b), Is.False);
            // and the newcomer is known, which a replayed snapshot could not have told us
            Assert.That(feeder.Current.IsExcluded(c, a), Is.False, "C arrived during the stall and hears A");
            Assert.That(feeder.Current.IsExcluded(a, c), Is.False);
        }

        // ======================================================================================
        // ALARM AND REBUILD TOGETHER: the alarm must actually reach the rebuild.
        //
        // The test above proves Invalidate() rebuilds from the world. This one proves the wiring around it: the
        // Watchdog's alarm trips the latch by the same route OnFeederAlarm uses, and the tick that follows
        // CONSUMES it and calls Invalidate. The failure being excluded is the quiet one -- alarm fires, flag is
        // set, nothing consumes it, and the next tick derives happily from the pre-stall cache.
        // ======================================================================================

        [Test]
        public void AWatchdogAlarm_MakesTheNextTickRebuild_AndOnlyThatTick()
        {
            var latch = new FeederResumeLatch();
            int invalidated = 0;

            // A tick with no alarm behind it must not rebuild: rebuilding every tick would throw away the delta
            // machinery the feeder exists for.
            Assert.That(latch.ResumeIfStalled(() => invalidated++), Is.False, "no alarm, no resume");
            Assert.That(invalidated, Is.EqualTo(0));

            latch.Trip();                       // <- exactly what OnFeederAlarm does, off the Watchdog's thread
            Assert.That(latch.IsTripped, Is.True);

            Assert.That(latch.ResumeIfStalled(() => invalidated++), Is.True, "the tick after the alarm resumes");
            Assert.That(invalidated, Is.EqualTo(1), "and it invalidated, so the derivation rebuilds from the world");
            Assert.That(latch.IsTripped, Is.False, "the latch is consumed");

            // One alarm, one rebuild. The tick after that is an ordinary delta tick again.
            Assert.That(latch.ResumeIfStalled(() => invalidated++), Is.False, "a consumed latch does not re-fire");
            Assert.That(invalidated, Is.EqualTo(1));

            // A stall that begins again is a fresh resume, not swallowed by the previous one.
            latch.Trip();
            Assert.That(latch.ResumeIfStalled(() => invalidated++), Is.True, "a second stall rebuilds again");
            Assert.That(invalidated, Is.EqualTo(2));
        }

    }
}
