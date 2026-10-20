/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The boundary the (later) Janus sender consumes. The VoiceStateFeeder is the sole producer;
 * no code emits to Janus yet (this task is the producer only).
 */

using System;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public interface IVisibilityFeed
    {
        /// The current full matrix (single source of truth for audio + roster).
        VisibilityMatrix Current { get; }

        /// Raised at most once per tick with the add/remove delta (never for an empty diff).
        event Action<VisibilityBatch> BatchProduced;

        /// A listener's full exclusion set as a Replace snapshot, for join/reconnect recovery.
        VisibilityBatch SnapshotFor(UUID listener);
    }
}
