/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * in-memory sticky per-parcel voice-moderation
 * state, keyed by parcel GlobalID. NON-PERSISTENT by decision: a region restart clears every
 * mute. Written by the SpatialVoiceModerationRequest CAP handler (WebRtcVoiceRegionModule) and
 * purged on parcel removal by VoiceVisibilityService.OnLandObjectRemoved so join/delete orphans
 * self-heal.
 *
 * Read by the matrix: FeederWorldFromScene folds IsModerated plus the moderator exemption
 * (VoiceModerationAuth) into the source-side ParcelView.IsVoiceModerated delegate, which
 * VisibilityRules evaluates as rule 2b on every matrix build.
 *
 * Thread-safe: the CAP handler writes on an HTTP worker thread, the purge runs on a sim thread,
 * and the future feeder will read on the tick thread — all under one lock.
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    /// One parcel's moderation state, detached from the store — an immutable copy handed out by
    /// Snapshot() for reporting. Holds no reference back into the store, so it cannot be used to
    /// mutate moderation state and cannot go stale-and-torn while it is being printed.
    public sealed class ParcelModerationView
    {
        public UUID ParcelGlobalId { get; }
        public bool MuteEveryone { get; }
        public IReadOnlyList<UUID> MutedAgents { get; }

        public ParcelModerationView(UUID parcelGlobalId, bool muteEveryone, IReadOnlyList<UUID> mutedAgents)
        {
            ParcelGlobalId = parcelGlobalId;
            MuteEveryone = muteEveryone;
            MutedAgents = mutedAgents;
        }
    }

    public sealed class VoiceModerationStore
    {
        private sealed class ParcelModeration
        {
            public bool MuteEveryone;
            public readonly HashSet<UUID> MutedAgents = new HashSet<UUID>();
            public bool IsEmpty => !MuteEveryone && MutedAgents.Count == 0;
        }

        private readonly Dictionary<UUID, ParcelModeration> m_byParcel = new Dictionary<UUID, ParcelModeration>();
        private readonly object m_lock = new object();

        /// mute_all / unmute_all — the sticky parcel-wide flag.
        public void SetMuteEveryone(UUID parcelGlobalId, bool muted)
        {
            lock (m_lock)
            {
                if (muted)
                {
                    GetOrCreate(parcelGlobalId).MuteEveryone = true;
                }
                else if (m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p))
                {
                    p.MuteEveryone = false;
                    if (p.IsEmpty)
                        m_byParcel.Remove(parcelGlobalId);
                }
            }
        }

        /// mute <agent_id> — add one avatar to the parcel's muted set.
        public void MuteAgent(UUID parcelGlobalId, UUID agentId)
        {
            lock (m_lock)
                GetOrCreate(parcelGlobalId).MutedAgents.Add(agentId);
        }

        /// unmute <agent_id> — remove one avatar from the parcel's muted set. Returns TRUE if an
        /// entry was actually removed, FALSE if the avatar was not muted on this parcel. The CAP
        /// handler ignores the result (the viewer's unmute is idempotent); the console unmute
        /// command needs it to tell "cleared" from "there was nothing there", which is exactly the
        /// distinction an operator chasing an unreachable mute is trying to establish.
        public bool UnmuteAgent(UUID parcelGlobalId, UUID agentId)
        {
            lock (m_lock)
            {
                if (!m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p))
                    return false;
                bool removed = p.MutedAgents.Remove(agentId);
                if (p.IsEmpty)
                    m_byParcel.Remove(parcelGlobalId);
                return removed;
            }
        }

        /// Purge all moderation state for a parcel that no longer exists (join / delete), so an
        /// orphaned GlobalID cannot linger. Called from OnLandObjectRemoved.
        public void Remove(UUID parcelGlobalId)
        {
            lock (m_lock)
                m_byParcel.Remove(parcelGlobalId);
        }

        /// True if the given avatar is moderated (muted) on the given parcel — the parcel-wide
        /// mute-everyone flag OR the individual muted set. Takes the SAME lock as the writers, so a
        /// read never observes a torn set. Moderator EXEMPTION is not decided here (this class knows
        /// only state, not permissions) — the adapter applies it, see FeederWorldFromScene.
        public bool IsModerated(UUID parcelGlobalId, UUID agentId)
        {
            lock (m_lock)
            {
                return m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p)
                    && (p.MuteEveryone || p.MutedAgents.Contains(agentId));
            }
        }

        /// A deep COPY of every parcel that currently carries moderation state, for read-only
        /// reporting ("show voice moderation"). Copies are taken under the SAME lock as the writers
        /// so a caller never enumerates a live set, and the caller can hold the result while it does
        /// slow work (name resolution) without pinning the lock.
        ///
        /// Ordering is imposed here, not left to Dictionary/HashSet enumeration order: a console
        /// listing that reshuffles between two runs is unreadable, and an operator comparing before
        /// and after an unmute needs the rows to stay put. Parcels sort by GlobalID, agents within a
        /// parcel by agent id; the display layer re-sorts by name where it has one.
        ///
        /// This is a read accessor only. The store stays in-memory and non-persistent.
        public IReadOnlyList<ParcelModerationView> Snapshot()
        {
            var views = new List<ParcelModerationView>();
            lock (m_lock)
            {
                foreach (KeyValuePair<UUID, ParcelModeration> kv in m_byParcel)
                {
                    var agents = new List<UUID>(kv.Value.MutedAgents);
                    agents.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
                    views.Add(new ParcelModerationView(kv.Key, kv.Value.MuteEveryone, agents));
                }
            }
            views.Sort((a, b) => string.CompareOrdinal(a.ParcelGlobalId.ToString(), b.ParcelGlobalId.ToString()));
            return views;
        }

        private ParcelModeration GetOrCreate(UUID parcelGlobalId)
        {
            if (!m_byParcel.TryGetValue(parcelGlobalId, out ParcelModeration p))
            {
                p = new ParcelModeration();
                m_byParcel[parcelGlobalId] = p;
            }
            return p;
        }
    }
}
