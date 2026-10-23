/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * One per-tick unit of change fed toward the mixer - the C# side of the protocol's
 * slvoice_vis message (semantics doc 3.3). A tick emits at most one batch:
 *   - Delta:    per-listener Added / Removed source UUIDs (the frequent movement case).
 *   - Snapshot: one listener's FULL exclusion set as a Replace (reconnect/bootstrap).
 * Wire emission is out of scope here; the (later) sender serializes Added -> op:"add",
 * Removed -> op:"remove", Snapshot -> op:"replace".
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public enum VisOp { Add, Remove, Replace }

    public sealed class VisibilityBatch
    {
        private static readonly IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> EmptyMap
            = new Dictionary<UUID, IReadOnlyCollection<UUID>>();

        public int Room { get; }
        public bool IsSnapshot { get; }
        // Exclusion (ban/visibility) channel - unchanged shape and meaning.
        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> Added { get; }
        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> Removed { get; }
        // Moderation MUTE channel (Option A), additive. Same per-listener shape as Added/Removed.
        // Empty on every path that predates moderation, so an unchanged excl case is unaffected.
        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> MuteAdded { get; }
        public IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> MuteRemoved { get; }

        private VisibilityBatch(int room, bool isSnapshot,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> added,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> removed,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteAdded,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteRemoved)
        {
            Room = room;
            IsSnapshot = isSnapshot;
            Added = added ?? EmptyMap;
            Removed = removed ?? EmptyMap;
            MuteAdded = muteAdded ?? EmptyMap;
            MuteRemoved = muteRemoved ?? EmptyMap;
        }

        /// A delta carries no per-listener changes at all (in EITHER channel) -> the tick can be
        /// dropped. (A snapshot is never empty: an empty set is a meaningful "clear this listener".)
        public bool IsEmpty => !IsSnapshot
            && Added.Count == 0 && Removed.Count == 0
            && MuteAdded.Count == 0 && MuteRemoved.Count == 0;

        public static VisibilityBatch Delta(int room,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> added,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> removed,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteAdded = null,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteRemoved = null)
            => new VisibilityBatch(room, false, added, removed, muteAdded, muteRemoved);

        /// A no-change delta - returned on a skipped/failed tick so callers get a non-null batch.
        public static VisibilityBatch EmptyDelta(int room)
            => new VisibilityBatch(room, false, EmptyMap, EmptyMap, EmptyMap, EmptyMap);

        /// One listener's FULL current state as a Replace snapshot: excl set in Added, mute set in
        /// MuteAdded. Both are authoritative-empty-is-a-clear for that listener.
        public static VisibilityBatch Snapshot(int room, UUID listener,
            IReadOnlyCollection<UUID> fullSet, IReadOnlyCollection<UUID> fullMuteSet = null)
        {
            var map = new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = fullSet };
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> muteMap = fullMuteSet == null
                ? EmptyMap
                : new Dictionary<UUID, IReadOnlyCollection<UUID>> { [listener] = fullMuteSet };
            return new VisibilityBatch(room, true, map, EmptyMap, muteMap, EmptyMap);
        }
    }
}
