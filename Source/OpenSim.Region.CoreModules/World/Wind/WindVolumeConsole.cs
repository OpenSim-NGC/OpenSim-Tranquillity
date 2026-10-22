/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.World.Wind;

/// <summary>
/// The text and the effect of the operator console commands "weather volumes" and "weather clear". They take the store
/// and a place to write to, so the wind module's console handlers stay thin and the commands can be tested without a
/// console.
/// </summary>
public static class WindVolumeConsole
{
    public const string ClearUsage =
        "Usage: weather clear | weather clear <handle> | weather clear object <uuid>";

    /// <summary>Writes one line per wind volume, with the region's name and the count against the cap.</summary>
    /// <param name="nowMs">The store's clock now (milliseconds), for the time left.</param>
    public static void List(IWindVolumeStore store, string regionName, long nowMs, Action<string> output)
    {
        if (store == null || !store.Enabled)
        {
            output($"Weather is off in {regionName}.");
            return;
        }

        IReadOnlyList<WindVolume> volumes = store.List(UUID.Zero);
        output($"Wind volumes in {regionName}: {volumes.Count} of at most {store.Limits.MaxVolumesRegion}.");
        foreach (WindVolume v in volumes)
        {
            string size = v.Shape == WindVolumeShape.Sphere
                ? $"radius {F(v.Radius)}"
                : $"radius {F(v.Radius)} height {F(v.Height)}";
            long left = Math.Max(0L, v.ExpiresAtMs - nowMs);
            output(string.Format(CultureInfo.InvariantCulture,
                "  handle {0} object {1} owner {2} {3} centre {4} {5} wind {6} {7} s left",
                v.Id, v.CreatorObject, v.Owner, v.Shape.ToString().ToLowerInvariant(),
                V(v.Center), size, V(v.Velocity), (left / 1000L)));
        }
    }

    /// <summary>
    /// Runs "weather clear" with the words after it: none (every volume), a handle, or "object" and a UUID.
    /// Writes the result and returns how many volumes were removed; 0 with a message on a usage error, an unknown
    /// handle, or weather off, in which case nothing is changed.
    /// </summary>
    public static int Clear(IWindVolumeStore store, string regionName, string[] args, Action<string> output)
    {
        if (store == null || !store.Enabled)
        {
            output($"Weather is off in {regionName}; nothing was changed.");
            return 0;
        }

        args ??= Array.Empty<string>();

        if (args.Length == 0 || (args.Length == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            int n = store.Clear();
            output($"Removed {n} wind volume(s) in {regionName}.");
            return n;
        }

        if (args.Length == 1 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int handle))
        {
            int result = store.Remove(handle, UUID.Zero);
            if (result == WindVolumeError.Ok)
            {
                output($"Removed wind volume {handle} in {regionName}.");
                return 1;
            }
            output($"There is no wind volume {handle} in {regionName}.");
            return 0;
        }

        if (args.Length == 2 && args[0].Equals("object", StringComparison.OrdinalIgnoreCase)
            && UUID.TryParse(args[1], out UUID creator) && !creator.IsZero())
        {
            int n = store.RemoveByCreator(creator);
            output($"Removed {n} wind volume(s) made by object {creator} in {regionName}.");
            return n;
        }

        output(ClearUsage);
        return 0;
    }

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string V(Vector3 v) => $"<{F(v.X)}, {F(v.Y)}, {F(v.Z)}>";
}
