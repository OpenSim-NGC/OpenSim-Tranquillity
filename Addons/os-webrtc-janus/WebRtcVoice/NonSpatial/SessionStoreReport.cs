/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * What an operator is told about where this region keeps its call sessions: the mode, the grid service it
 * talks to, whether it answered, and what this region is holding. Pure text from plain values, so the
 * console command and the tests share it.
 */
using System.Collections.Generic;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class SessionStoreReport
    {
        /// <summary>True when the sessions are kept by the grid service.</summary>
        public bool Grid { get; init; }

        /// <summary>The grid service's address. Empty in local mode.</summary>
        public string ServerUri { get; init; } = string.Empty;

        /// <summary>Whether the service answered the check made when the region started: null while that is still running.</summary>
        public bool? ProbeAnswered { get; init; }

        public BreakerState Breaker { get; init; } = BreakerState.Closed;

        /// <summary>Sessions this process holds (local mode).</summary>
        public int LocalSessions { get; init; }

        /// <summary>Seats this region took and still holds (grid mode).</summary>
        public int HeldSeats { get; init; }

        /// <summary>Departures waiting to reach the service, and how many were given up.</summary>
        public int QueuedDeparts { get; init; }
        public int DroppedDeparts { get; init; }

        /// <summary>Whether group and conference rings are signed with a shared secret.</summary>
        public bool CarriersSigned { get; init; }

        public IReadOnlyList<string> Format()
        {
            List<string> lines = new List<string>();
            if (!Grid)
            {
                lines.Add($"Call sessions: kept in this process (SessionStoreMode local); {LocalSessions} held.");
                lines.Add(CarriersSigned
                    ? "  Cross-process rings and conference text: signed with the shared carrier secret."
                    : "  Cross-process rings and conference text: off (no carrier secret).");
                return lines;
            }

            lines.Add($"Call sessions: kept by the grid session service at {ServerUri} (SessionStoreMode grid).");
            string probe = ProbeAnswered switch
            {
                true => "answered when this region started",
                false => "did not answer when this region started; calls are refused until it does",
                null => "being checked",
            };
            lines.Add($"  Service: {probe}. Circuit breaker: {Breaker}.");
            lines.Add($"  This region holds {HeldSeats} seat(s) and renews them; {QueuedDeparts} departure(s) waiting, {DroppedDeparts} given up.");
            lines.Add(CarriersSigned
                ? "  Rings: checked by the service, and also signed with the shared carrier secret."
                : "  Rings: checked by the service. Conference text: signed with a key the service keeps for each conference.");
            return lines;
        }
    }
}
