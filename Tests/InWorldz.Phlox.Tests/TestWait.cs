/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The ceiling for a wait that is for something to HAPPEN (a script answering, a load finishing, a row being written).
/// Such a wait polls for its condition and returns the moment it holds, so the ceiling costs a passing run nothing; it only
/// decides how long a run that is not going to pass waits before it says so. On a machine busy with other builds a script
/// can take several times its usual time to get a timeslice, so the ceiling is generous: 120 s.
/// A window that proves something does NOT happen is not a ceiling and does not use this.
/// Setting the environment variable PHLOX_TEST_WAIT_SECONDS replaces the ceiling (for example 5, to see a deliberately
/// broken build fail quickly). It replaces only waits that use <see cref="DefaultSeconds"/>.
/// </summary>
internal static class TestWait
{
    /// <summary>The ceiling, in seconds, when PHLOX_TEST_WAIT_SECONDS is not set.</summary>
    public const int DefaultSeconds = 120;

    private static double? Override
    {
        get
        {
            string v = Environment.GetEnvironmentVariable("PHLOX_TEST_WAIT_SECONDS");
            return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0 ? s : null;
        }
    }

    /// <summary>The ceiling as a TimeSpan, for the waits that take one.</summary>
    public static TimeSpan Default => TimeSpan.FromSeconds(Override ?? DefaultSeconds);

    /// <summary>
    /// The ceiling a wait helper uses for its <paramref name="seconds"/> argument: the override when the argument is the
    /// default ceiling and the override is set, otherwise the argument itself.
    /// </summary>
    public static double Cap(double seconds) => seconds == DefaultSeconds ? Override ?? seconds : seconds;
}
