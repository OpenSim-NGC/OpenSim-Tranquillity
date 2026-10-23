/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Threading;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>
    /// For a warning that can fire again and again (a prim whose shape cannot be cooked, a body whose position is
    /// not a number): the first occurrence is logged, then every <see cref="Every"/>th, so a fault that repeats
    /// every step does not fill the console. One limiter per warning, so one kind of fault never hides another.
    /// Thread-safe; the count is for the process.
    /// </summary>
    internal sealed class RepeatLimiter
    {
        internal const int Every = 100;

        private long _count;

        /// <summary>True when this occurrence is to be logged (the first, then every 100th); <paramref name="n"/> is its number.</summary>
        internal bool ShouldLog(out long n)
        {
            n = Interlocked.Increment(ref _count);
            return (n - 1) % Every == 0;
        }

        /// <summary>How many times the warning has fired, logged or not.</summary>
        internal long Count => Interlocked.Read(ref _count);

        /// <summary>The words that end a limited warning, so a reader knows lines are missing.</summary>
        internal static string Suffix(long n) => $" (occurrence {n}; the first and then every {Every}th are logged)";
    }

    /// <summary>The limiters of the module's repeating warnings, one each.</summary>
    internal static class RepeatingWarnings
    {
        internal static readonly RepeatLimiter AddPrimShapeFailed = new RepeatLimiter();
        internal static readonly RepeatLimiter MeshScaleThrew = new RepeatLimiter();
        internal static readonly RepeatLimiter NoMesher = new RepeatLimiter();
        internal static readonly RepeatLimiter MesherGeometryUnusable = new RepeatLimiter();
        internal static readonly RepeatLimiter MesherExtractionThrew = new RepeatLimiter();
        internal static readonly RepeatLimiter MesherIndicesOutOfRange = new RepeatLimiter();
        internal static readonly RepeatLimiter CookThrew = new RepeatLimiter();
        internal static readonly RepeatLimiter MeshAssetCookThrew = new RepeatLimiter();
        internal static readonly RepeatLimiter TerrainTooShort = new RepeatLimiter();
        internal static readonly RepeatLimiter NonFinitePosition = new RepeatLimiter();
    }
}
