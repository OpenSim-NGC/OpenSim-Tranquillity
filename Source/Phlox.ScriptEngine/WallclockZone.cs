/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// The time zone llGetWallclock reports in. SL answers Pacific time; YEngine converts to the zone named by
    /// GetWallclockTimeZone in [YEngine] (and uses the server's local time when it is unset). Phlox reads the same key
    /// from [InWorldz.Phlox], else from [YEngine], and stays on UTC when neither is set.
    /// </summary>
    internal static class WallclockZone
    {
        private static readonly ConcurrentDictionary<string, TimeZoneInfo> s_zones = new();

        /// <summary>
        /// The zone for <paramref name="id"/> (an IANA id or a Windows id); null, meaning UTC, when the id is empty or
        /// unknown. An unknown id calls <paramref name="warn"/> once for the life of the process, however many scripts
        /// ask, and never throws.
        /// </summary>
        internal static TimeZoneInfo Resolve(string id, Action<string> warn)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            if (s_zones.TryGetValue(id, out TimeZoneInfo known)) return known;
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException || e is InvalidTimeZoneException)
            {
                zone = null;
            }
            // The first caller to store the answer is the one that warns.
            if (s_zones.TryAdd(id, zone) && zone == null)
                warn?.Invoke("GetWallclockTimeZone '" + id + "' is not a known time zone; llGetWallclock uses UTC");
            return zone;
        }

        /// <summary>Whole seconds since midnight in <paramref name="zone"/> (UTC when null) at <paramref name="utcNow"/>; truncated, as in SL.</summary>
        internal static float SecondsSinceMidnight(DateTimeOffset utcNow, TimeZoneInfo zone)
        {
            DateTime local = zone == null ? utcNow.UtcDateTime : TimeZoneInfo.ConvertTimeFromUtc(utcNow.UtcDateTime, zone);
            return (float)Math.Truncate(local.TimeOfDay.TotalSeconds);
        }
    }
}
