/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Each vehicle type's basic run with the documented presets (the default): its main figure at 11 Hz, and the same
/// figure at 22.5, 45 and 90 Hz within a stated share of it. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehiclePresetDriveTests
{
    private static float Figure(Summary m, string figure) => figure switch
    {
        "release" => m.ReleaseSpeed,
        "before" => m.DistanceBeforeRelease,
        "steady" => m.SteadySpeed,
        "top" => m.TopSpeed,
        "endz" => m.End.Z,
        _ => throw new ArgumentException(figure),
    };

    // The 11 Hz figure (measured with the documented presets) and how far the other rates may lie from it.
    [Theory]
    [InlineData("car", 0f, "release", 7.537f, 1f)]       // motor 8 m/s, timescale 1 s, forward friction 100 s
    // (16.315: the 11 Hz figure with the vehicle run in each collision step. Run once per heartbeat it was 16.569 at 11 Hz and 16.28 to 16.37
    // at the other rates; run in each collision step it is 16.15 to 16.32 at all four, a spread of 1.0% against 1.8%.)
    [InlineData("car", 0f, "before", 16.315f, 2.5f)]
    [InlineData("carturn", 0f, "before", 14.125f, 2.5f)]
    [InlineData("car-down", 15f, "steady", 10.674f, 1.5f)]
    [InlineData("sled", 15f, "steady", 12.649f, 1.5f)]  // forward friction 30 s, the slope assist, no hover
    [InlineData("boat", 0f, "steady", 2.498f, 1f)]       // motor 5 m/s, timescale 5 s, forward friction 10 s
    [InlineData("airplane", 0f, "steady", 14.897f, 1f)]
    [InlineData("balloon", 0f, "endz", 28.522f, 0.1f)]
    public void The_documented_presets_drive_alike_at_every_rate(string scenario, float slope, string figure, float at11, float percent)
    {
        foreach (double rate in Harness.Harness.Rates)
        {
            float got = Figure(Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions { RateHz = rate, SlopeDeg = slope }.SecondLife()).Summary, figure);
            float tol = Math.Abs(at11) * percent / 100f;
            Assert.True(Math.Abs(got - at11) <= tol, $"{scenario} {figure} at {rate} Hz: {got:0.000} against {at11:0.000} (within {percent}%)");
        }
    }
}
