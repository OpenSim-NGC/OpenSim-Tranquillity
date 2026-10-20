/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Pure builder for the peer_ctl_batch request body (mixer-feed-protocol.md). Produces the
 * ROOM-LESS inner request { "request":"peer_ctl_batch", "op":..., "excl":{ L:[S,...], ... } };
 * the sink stamps "room" (correction: room lives sink-side, feeder/orchestrator stay room-agnostic).
 *
 * It is the SINGLE producer of the wire body, so format is guarded HERE with an invariant. That is
 * deliberate: the mixer's Ok does not mean applied (an unknown room / a malformed batch both read as
 * janus:"success"), so we cannot detect a format error by inspecting responses — we prevent
 * it at the source instead. Dependency-free beyond OpenMetaverse; directly unit-testable.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    public static class PeerCtlBatchSerializer
    {
        public static string OpString(VisOp op)
        {
            switch (op)
            {
                case VisOp.Add: return "add";
                case VisOp.Remove: return "remove";
                case VisOp.Replace: return "replace";
                default: throw new ArgumentOutOfRangeException(nameof(op));
            }
        }

        /// <summary>Build the room-less peer_ctl_batch request. Format invariant: every listener and
        /// source is a non-zero UUID; a violation throws (the caller guards, never inspects responses).</summary>
        public static OSDMap BuildRequest(VisOp op,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> excl,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> mute = null)
        {
            if (excl == null)
                throw new ArgumentNullException(nameof(excl));

            var body = new OSDMap
            {
                ["request"] = new OSDString("peer_ctl_batch"),
                ["op"] = new OSDString(OpString(op)),
                ["excl"] = BuildChannel(excl)
            };

            // ADDITIVE moderation-mute channel (Option A).
            //   - On REPLACE (snapshot): ALWAYS emit "mute", even as an empty object. A REPLACE is
            //     authoritative, so a NEW sim always carries the key; a mixer then knows that an
            //     ABSENT "mute" on a replace means an OLD sim (skew disambiguation), never "no change".
            //     An empty {} names no listener, so it clears nothing — clearing a listener requires
            //     that listener's key with an empty array, which the sender's clear-tracking emits.
            //   - On ADD/REMOVE (delta): emit only when non-empty, so a pure-ban delta is byte-for-byte
            //     the pre-mute wire. An older mixer ignores the key either way (skew-safe).
            if (op == VisOp.Replace || (mute != null && mute.Count > 0))
                body["mute"] = BuildChannel(mute);

            return body;
        }

        /// <summary>Serialize one per-listener {L:[S,...]} channel with the zero-UUID invariant.
        /// Shared by the "excl" and "mute" channels so both enforce the same guard.</summary>
        private static OSDMap BuildChannel(IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> channel)
        {
            var map = new OSDMap();
            if (channel == null)
                return map;   // REPLACE with no mutes -> emit an empty "mute" object (skew disambiguation)
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in channel)
            {
                if (kv.Key == UUID.Zero)
                    throw new InvalidOperationException("peer_ctl_batch invariant: zero listener UUID");
                var arr = new OSDArray();
                foreach (UUID s in kv.Value)
                {
                    if (s == UUID.Zero)
                        throw new InvalidOperationException("peer_ctl_batch invariant: zero source UUID");
                    arr.Add(new OSDString(s.ToString()));
                }
                map[kv.Key.ToString()] = arr;
            }
            return map;
        }

        /// <summary>Added and Removed must be disjoint per (listener, source) pair. A pair in BOTH is
        /// a feeder bug (it added and removed the same exclusion in one tick) — do not resolve it by
        /// ordering; throw naming the first overlap. The caller runs this inside its never-throw guard.</summary>
        public static void EnsureDisjoint(
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> added,
            IReadOnlyDictionary<UUID, IReadOnlyCollection<UUID>> removed)
        {
            if (added == null || removed == null)
                return;
            foreach (KeyValuePair<UUID, IReadOnlyCollection<UUID>> kv in added)
            {
                if (!removed.TryGetValue(kv.Key, out IReadOnlyCollection<UUID> rem))
                    continue;
                foreach (UUID s in kv.Value)
                {
                    foreach (UUID r in rem)
                    {
                        if (s == r)
                            throw new InvalidOperationException(
                                $"peer_ctl_batch: (listener {kv.Key}, source {s}) is in BOTH add and remove — feeder bug");
                    }
                }
            }
        }
    }
}
