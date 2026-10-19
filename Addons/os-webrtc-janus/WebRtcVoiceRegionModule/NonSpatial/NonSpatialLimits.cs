/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Ceilings on how much non-spatial session state one process holds and how fast one agent can create it.
 *
 * Without them a single agent could open sessions until the process ran out of memory, and one request
 * could ring every online agent. The numbers are settings in [WebRtcVoice] (see the region module's
 * ReadNonSpatialLimits); the defaults here are finite on purpose.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class NonSpatialLimits
    {
        /// <summary>Sessions of every type this process holds at once.</summary>
        public const int DefaultMaxNonSpatialSessions = 5000;

        /// <summary>Live sessions one agent may have started.</summary>
        public const int DefaultMaxSessionsPerAgent = 20;

        public int MaxNonSpatialSessions { get; init; } = DefaultMaxNonSpatialSessions;
        public int MaxSessionsPerAgent { get; init; } = DefaultMaxSessionsPerAgent;

        /// <summary>"Start conference" and "invite" requests one agent can make back to back.</summary>
        public const int DefaultConferenceRequestBurst = 5;

        /// <summary>Seconds for one more request to become available after the burst is spent.</summary>
        public const double DefaultConferenceRequestRefillSeconds = 10;

        public int ConferenceRequestBurst { get; init; } = DefaultConferenceRequestBurst;
        public double ConferenceRequestRefillSeconds { get; init; } = DefaultConferenceRequestRefillSeconds;

        public static NonSpatialLimits Default => new NonSpatialLimits();
    }

    /// <summary>
    /// A token bucket per agent. A bucket holds up to <c>burst</c> tokens and gains one every
    /// <c>refillSeconds</c>; a request takes one, and is refused when none is left. State is kept only for
    /// agents that have asked, and <see cref="Prune"/> drops the ones not seen for a while: a bucket left alone
    /// that long is full again, so forgetting it changes nothing.
    /// </summary>
    public sealed class ConferenceRequestLimiter
    {
        private sealed class Bucket
        {
            public double Tokens;
            public DateTime LastUtc;
        }

        private readonly int _burst;
        private readonly double _refillSeconds;
        private readonly Dictionary<UUID, Bucket> _buckets = new Dictionary<UUID, Bucket>();

        public ConferenceRequestLimiter(int burst, double refillSeconds)
        {
            _burst = Math.Max(1, burst);
            _refillSeconds = refillSeconds > 0 ? refillSeconds : NonSpatialLimits.DefaultConferenceRequestRefillSeconds;
        }

        /// <summary>Take a token for <paramref name="agent"/>; false when its bucket is empty.</summary>
        public bool TryTake(UUID agent, DateTime nowUtc)
        {
            lock (_buckets)
            {
                if (!_buckets.TryGetValue(agent, out Bucket b))
                {
                    b = new Bucket { Tokens = _burst, LastUtc = nowUtc };
                    _buckets[agent] = b;
                }
                else
                {
                    double elapsed = (nowUtc - b.LastUtc).TotalSeconds;
                    if (elapsed > 0)
                        b.Tokens = Math.Min(_burst, b.Tokens + elapsed / _refillSeconds);
                    b.LastUtc = nowUtc;
                }
                if (b.Tokens < 1)
                    return false;
                b.Tokens -= 1;
                return true;
            }
        }

        /// <summary>How many agents have a bucket.</summary>
        public int TrackedAgents
        {
            get { lock (_buckets) return _buckets.Count; }
        }

        /// <summary>Forget agents not seen for more than <paramref name="maxAge"/>. Returns how many.</summary>
        public int Prune(DateTime nowUtc, TimeSpan maxAge)
        {
            lock (_buckets)
            {
                List<UUID> stale = new List<UUID>();
                foreach (KeyValuePair<UUID, Bucket> kv in _buckets)
                    if (nowUtc - kv.Value.LastUtc > maxAge)
                        stale.Add(kv.Key);
                foreach (UUID k in stale)
                    _buckets.Remove(k);
                return stale.Count;
            }
        }
    }

    /// <summary>
    /// Lets a log line through at most once per key per interval. A refused start repeated in a loop
    /// would otherwise write one Warning per attempt.
    /// </summary>
    public sealed class RefusalLogGate
    {
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

        private readonly TimeSpan _interval;
        private readonly Dictionary<UUID, DateTime> _last = new Dictionary<UUID, DateTime>();

        public RefusalLogGate(TimeSpan? interval = null)
        {
            _interval = interval ?? DefaultInterval;
        }

        /// <summary>True the first time a key is seen and again once the interval has passed since the last true.</summary>
        public bool ShouldLog(UUID key, DateTime nowUtc)
        {
            lock (_last)
            {
                if (_last.TryGetValue(key, out DateTime prior) && nowUtc - prior < _interval)
                    return false;
                _last[key] = nowUtc;
                return true;
            }
        }

        /// <summary>Forget keys whose last line is older than <paramref name="maxAge"/>. Returns how many.</summary>
        public int Prune(DateTime nowUtc, TimeSpan maxAge)
        {
            lock (_last)
            {
                List<UUID> stale = new List<UUID>();
                foreach (KeyValuePair<UUID, DateTime> kv in _last)
                    if (nowUtc - kv.Value > maxAge)
                        stale.Add(kv.Key);
                foreach (UUID k in stale)
                    _last.Remove(k);
                return stale.Count;
            }
        }
    }
}
