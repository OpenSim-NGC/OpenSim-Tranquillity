/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The region's settings for where non-spatial call sessions are kept, read from [WebRtcVoice].
 * The default is "local": each region process keeps its own sessions, as before the grid store existed.
 * "grid" sends every session operation to the grid session service in Robust.
 */
using Nini.Config;

namespace osWebRtcVoice.NonSpatial
{
    public sealed class SessionStoreSettings
    {
        public const string DefaultMode = "local";
        public const int DefaultTimeoutMs = 3000;
        public const int DefaultRenewSeconds = GridSeatRenewer.DefaultRenewSeconds;
        public const int DefaultOutboxMax = 256;
        public const int DefaultOutboxSeconds = 60;
        public const int DefaultBreakerFailures = 5;
        public const int DefaultBreakerWindowSeconds = 10;
        public const int DefaultBreakerOpenSeconds = 5;

        /// <summary>True when sessions are kept by the grid service.</summary>
        public bool Grid { get; init; }

        /// <summary>The Robust base URI. Empty in local mode.</summary>
        public string ServerUri { get; init; } = string.Empty;

        public int TimeoutMs { get; init; } = DefaultTimeoutMs;
        public int RenewSeconds { get; init; } = DefaultRenewSeconds;

        /// <summary>Departures kept while the service cannot be reached, and how long each is kept.</summary>
        public int OutboxMax { get; init; } = DefaultOutboxMax;
        public int OutboxSeconds { get; init; } = DefaultOutboxSeconds;

        /// <summary>Failures inside the window that open the breaker, and how long it then refuses calls.</summary>
        public int BreakerFailures { get; init; } = DefaultBreakerFailures;
        public int BreakerWindowSeconds { get; init; } = DefaultBreakerWindowSeconds;
        public int BreakerOpenSeconds { get; init; } = DefaultBreakerOpenSeconds;

        /// <summary>Set when the settings asked for something unusable and local mode was kept; null otherwise.</summary>
        public string Problem { get; init; }

        private static int Positive(IConfig voice, string key, int fallback)
        {
            int value = voice?.GetInt(key, fallback) ?? fallback;
            return value > 0 ? value : fallback;
        }

        public static SessionStoreSettings Read(IConfig voice)
        {
            string mode = (voice?.GetString("SessionStoreMode", DefaultMode) ?? DefaultMode).Trim().ToLowerInvariant();
            int timeout = voice?.GetInt("SessionStoreTimeoutMs", DefaultTimeoutMs) ?? DefaultTimeoutMs;
            int renew = voice?.GetInt("SessionSeatRenewSeconds", DefaultRenewSeconds) ?? DefaultRenewSeconds;
            if (timeout < 100) timeout = DefaultTimeoutMs;
            if (renew < 5) renew = DefaultRenewSeconds;
            int outboxMax = Positive(voice, "SessionStoreOutboxMax", DefaultOutboxMax);
            int outboxSeconds = Positive(voice, "SessionStoreOutboxSeconds", DefaultOutboxSeconds);
            int breakerFailures = Positive(voice, "SessionStoreBreakerFailures", DefaultBreakerFailures);
            int breakerWindow = Positive(voice, "SessionStoreBreakerWindowSeconds", DefaultBreakerWindowSeconds);
            int breakerOpen = Positive(voice, "SessionStoreBreakerOpenSeconds", DefaultBreakerOpenSeconds);

            if (mode == "local" || mode.Length == 0)
                return new SessionStoreSettings { TimeoutMs = timeout, RenewSeconds = renew, OutboxMax = outboxMax, OutboxSeconds = outboxSeconds, BreakerFailures = breakerFailures, BreakerWindowSeconds = breakerWindow, BreakerOpenSeconds = breakerOpen };
            if (mode != "grid")
                return new SessionStoreSettings { TimeoutMs = timeout, RenewSeconds = renew, OutboxMax = outboxMax, OutboxSeconds = outboxSeconds, BreakerFailures = breakerFailures, BreakerWindowSeconds = breakerWindow, BreakerOpenSeconds = breakerOpen, Problem = $"SessionStoreMode \"{mode}\" is not local or grid" };

            string uri = (voice.GetString("SessionStoreServerURI", string.Empty) ?? string.Empty).Trim();
            if (uri.Length == 0)
                return new SessionStoreSettings { TimeoutMs = timeout, RenewSeconds = renew, OutboxMax = outboxMax, OutboxSeconds = outboxSeconds, BreakerFailures = breakerFailures, BreakerWindowSeconds = breakerWindow, BreakerOpenSeconds = breakerOpen, Problem = "SessionStoreMode is grid but SessionStoreServerURI is not set" };
            return new SessionStoreSettings { Grid = true, ServerUri = uri, TimeoutMs = timeout, RenewSeconds = renew, OutboxMax = outboxMax, OutboxSeconds = outboxSeconds, BreakerFailures = breakerFailures, BreakerWindowSeconds = breakerWindow, BreakerOpenSeconds = breakerOpen };
        }
    }
}
