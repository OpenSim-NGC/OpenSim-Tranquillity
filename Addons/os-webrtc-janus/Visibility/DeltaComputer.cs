/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Diff two matrices into one batch of per-listener add/remove changes, and produce a
 * per-listener Replace snapshot. Pure and deterministic - the delta-correctness unit tests
 * assert against these directly.
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public static class DeltaComputer
    {
        /// Per-listener exclusion changes between prev and next. Only listeners whose set
        /// actually changed appear; both directions of a symmetric change are already present in
        /// the matrices, so a boundary crossing yields the full fan-out (both the co-occupant
        /// removals AND the outsider additions - see VoiceStateFeederTests fan-out case).
        public static VisibilityBatch Diff(VisibilityMatrix prev, VisibilityMatrix next, int room)
        {
            var added = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var removed = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var muteAdded = new Dictionary<UUID, IReadOnlyCollection<UUID>>();
            var muteRemoved = new Dictionary<UUID, IReadOnlyCollection<UUID>>();

            // Exclusion channel - the union of listeners present in either matrix's EXCL sets.
            var exclListeners = new HashSet<UUID>();
            foreach (UUID l in prev.Listeners) exclListeners.Add(l);
            foreach (UUID l in next.Listeners) exclListeners.Add(l);
            foreach (UUID L in exclListeners)
                DiffChannel(prev.ExcludedFor(L), next.ExcludedFor(L), L, added, removed);

            // Mute channel - the union of listeners present in either matrix's MUTE sets. Independent
            // of the excl listener set: a listener may have a mute change with no excl change.
            var muteListeners = new HashSet<UUID>();
            foreach (UUID l in prev.MutedListeners) muteListeners.Add(l);
            foreach (UUID l in next.MutedListeners) muteListeners.Add(l);
            foreach (UUID L in muteListeners)
                DiffChannel(prev.MutedFor(L), next.MutedFor(L), L, muteAdded, muteRemoved);

            return VisibilityBatch.Delta(room, added, removed, muteAdded, muteRemoved);
        }

        // One listener's before/after set diff into the given add/remove maps. Shared by both channels.
        private static void DiffChannel(IReadOnlySet<UUID> before, IReadOnlySet<UUID> after, UUID L,
            Dictionary<UUID, IReadOnlyCollection<UUID>> add, Dictionary<UUID, IReadOnlyCollection<UUID>> rem)
        {
            List<UUID> a = null;
            foreach (UUID s in after)
                if (!before.Contains(s))
                    (a ??= new List<UUID>()).Add(s);

            List<UUID> r = null;
            foreach (UUID s in before)
                if (!after.Contains(s))
                    (r ??= new List<UUID>()).Add(s);

            if (a != null) add[L] = a;
            if (r != null) rem[L] = r;
        }

        /// One listener's full current exclusion set (Added) and mute set (MuteAdded) as a Replace
        /// snapshot (join/reconnect). Both channels are re-sent authoritatively.
        public static VisibilityBatch SnapshotFor(VisibilityMatrix matrix, UUID listener, int room)
        {
            var full = new List<UUID>(matrix.ExcludedFor(listener));
            var fullMute = new List<UUID>(matrix.MutedFor(listener));
            return VisibilityBatch.Snapshot(room, listener, full, fullMute);
        }
    }
}
