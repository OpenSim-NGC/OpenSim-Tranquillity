/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;

namespace OpenSim.Region.CoreModules.World.Wind;

/// <summary>
/// Reads wind settings from the [Wind] section, keeping out values that are not numbers. A float that parses as NaN
/// or as plus or minus infinity ("NaN", "Infinity", or a number too large for a float) would otherwise reach the wind
/// arrays and every sample taken from them.
/// </summary>
internal static class WindValues
{
    /// <summary>
    /// Reads a float key. A value that is not finite falls back to <paramref name="defaultValue"/>, with one warning
    /// line naming the plugin and the key. A missing key gives the default, as before.
    /// </summary>
    public static float ReadFinite(IConfig config, string plugin, string key, float defaultValue, ILogger log)
    {
        float value = config.GetFloat(key, defaultValue);
        if (float.IsFinite(value))
            return value;

        log.LogWarning("[{0}] {1} = {2} is not a finite number; using the default {3}.", plugin, key, value, defaultValue);
        return defaultValue;
    }
}
