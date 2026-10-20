/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Per-region record of the mixer room each agent's LATEST successful provision joined
 * (OQ1(a)/OQ4/OQ7).
 *
 * Written by VoiceVisibilityService.OnListenerProvisioned from the `room` the voice service
 * reports in its success map; read by the sink's room resolver. Pure and
 * Scene-free so the semantics unit-test without a region:
 *   - Record: add, or REPLACE — the newest provision wins (OQ7). A relog overlap's older
 *     session is therefore addressed at the new room until its teardown.
 *   - Resolve: null when there is no record. The caller (the sink) maps null to the estate
 *     room for listeners and sources alike ("one policy for a missing room record"); this
 *     table never guesses.
 *   - Nothing here removes an entry. A departed agent is never a matrix listener (membership
 *     is gated by VoiceViewerSession.IsAgentInRegion), so a stale record is unreachable until
 *     the agent re-provisions and overwrites it. See the report for why close-time removal
 *     was NOT wired: without the generation token the teardown uses, a late close of an OLD
 *     login would erase the NEW login's record.
 *
 * Thread-safe: written on CAP handler threads, read on the feeder tick / sender path.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public sealed class AgentRoomTable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<UUID, int> _rooms = new Dictionary<UUID, int>();

        /// <summary>Record the room this agent's latest successful provision joined. Replaces any
        /// earlier record (newest wins, OQ7). A zero UUID is ignored, mirroring the sender's
        /// pending-join guard.</summary>
        public void Record(UUID agent, int room)
        {
            if (agent == UUID.Zero)
                return;
            lock (_lock)
                _rooms[agent] = room;
        }

        /// <summary>The recorded room, or null if this agent has never been recorded here. This is
        /// the resolver the sink consumes; null means "no record", never "estate room".</summary>
        public int? Resolve(UUID agent)
        {
            lock (_lock)
                return _rooms.TryGetValue(agent, out int room) ? room : (int?)null;
        }

        /// <summary>Number of agents with a record (diagnostics / tests).</summary>
        public int Count
        {
            get { lock (_lock) return _rooms.Count; }
        }
    }
}
