/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The 90 degree turn of the camera under the InWorldz behaviour, beside the default's (VehicleMouselookHuntingTests: an
/// overshoot of 11 to 16 degrees and the heading within half a degree by 12 s). A car facing east, MOUSELOOK_STEER, the
/// driver's camera looking north, its angular motor <0,0,1.5> held for 16 s, on the real engine. Under InWorldz the turn is
/// a share of that motor, shaped by (e^x - 1) / (e - 1), which is whole from 30 degrees off the heading and dies away
/// toward the dead zone, so the car turns at the full rate until it is within 30 degrees and then slows. Serial with the
/// other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleInWorldzMouselookTurnTests
{
    private readonly ITestOutputHelper _out;
    public VehicleInWorldzMouselookTurnTests(ITestOutputHelper output) => _out = output;

    private const double Rad = 180.0 / Math.PI;

    private static RunResult Run(string behaviour, double physicsRate)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, Hold = 16f, Duration = 20f };
        o.Jolt["VehicleBehaviour"] = behaviour;
        return Harness.Harness.Run(Harness.Harness.Find("mouselook-turn"), o);
    }

    private static double HeadingDegrees(Sample s) => Harness.Harness.Heading(s.Rotation) * Rad;

    // Measured on the real engine (the figures of these runs, each also written to the test output):
    //   Second Life, 45 Hz: overshoot 11.43 degrees, within half a degree from 8.7 s on.
    //   InWorldz,    45 Hz: no overshoot (89.78 degrees at most), 20.8 degrees short at 1 s, 2.6 at 2 s, within 0.4 from 3 s,
    //                       0.9 short from 12 s on.
    //   InWorldz,    11 Hz: no overshoot (89.15 at most), 39.6 degrees short at 1 s (19.7 once the motor ramps in each collision step),
    //                       within 1.5 degrees from 4 s on (readings 0.85 to 1.47 short)
    //                       (inside the dead zone's 2.8 degrees, where InWorldz stops steering).
    // The car drives on under Halcyon's linear motor, which ramps from a 0.04 m/s start and takes the car through the turn at a
    // different speed than the documented equation did; it comes to rest inside the dead zone a little short of the camera
    // (the earlier figures, before that motor, were 0.01 degrees off at 12 s at 45 Hz).
    [Theory]
    [InlineData("InWorldz", 45.0)]
    [InlineData("InWorldz", 0.0)]
    [InlineData("SecondLife", 45.0)]
    public void The_ninety_degree_turn_under_each_behaviour(string behaviour, double physicsRate)
    {
        RunResult r = Run(behaviour, physicsRate);
        var hold = r.Samples.Where(s => s.T <= 16.0).ToArray();
        double peak = hold.Max(HeadingDegrees);
        double Err(double t) => HeadingDegrees(hold.First(s => s.T >= t)) - 90.0;
        // The time the heading is last more than half a degree from the camera's: when it has settled.
        double settled = hold.Where(s => Math.Abs(HeadingDegrees(s) - 90.0) > 0.5).Select(s => s.T).DefaultIfEmpty(0.0).Max();
        // Under InWorldz steering stops inside the dead zone: the time the heading is last more than a degree from the camera's.
        double settledInDeadZone = hold.Where(s => Math.Abs(HeadingDegrees(s) - 90.0) > 1.0).Select(s => s.T).DefaultIfEmpty(0.0).Max();
        _out.WriteLine($"{behaviour} physics {physicsRate,4} Hz: peak heading {peak:0.00} deg (overshoot {peak - 90.0:0.00}); settled (last time over 0.5 deg off) {settled:0.00} s; " +
                       $"error at 1 s {Err(1.0):0.00}, 2 s {Err(2.0):0.00}, 3 s {Err(3.0):0.00}, 5 s {Err(5.0):0.00}, 8 s {Err(8.0):0.00}, 12 s {Err(12.0):0.00}, 16 s {Err(16.0):0.00}");
        if (behaviour == "SecondLife")
        {
            Assert.InRange(peak - 90.0, 5.0, 20.0);
            Assert.True(settled < 12.0, $"settled at {settled} s");
            return;
        }
        Assert.True(peak - 90.0 < 0.5, $"overshoot {peak - 90.0}");                     // the turn does not carry past the camera
        // Full rate until within 30 degrees, then slower. The angular motor is Halcyon's stepwise ramp, whose growth per step is
        // the same share of a timescale at any rate, from its stiction start. It used to start later at one step per heartbeat
        // (11 Hz), because the ramp took a whole heartbeat's step from the start: 36.9 degrees short at 1 s there, against about
        // 20 at 45 Hz. It runs in each collision step now (15 ms at 11 Hz with the default 6, Halcyon's own step), and is 19.7
        // short at 1 s at 11 Hz and 20 at 45 Hz.
        Assert.InRange(Err(1.0), -25.0, -15.0);
        Assert.True(hold.Where(s => s.T >= 4.0).All(s => Math.Abs(HeadingDegrees(s) - 90.0) < 2.0), $"within two degrees of the camera from 4 s on: errors {string.Join(" ", hold.Where(s => s.T >= 4.0).Select(s => $"{s.T:0.0}:{HeadingDegrees(s) - 90.0:0.00}").Where((x, i) => i % 6 == 0))}");
        Assert.True(hold.Where(s => s.T >= 3.0).All(s => Math.Abs(HeadingDegrees(s) - 90.0) < 2.81), "inside the dead zone from 3 s on");
        if (physicsRate > 0)
            Assert.True(settledInDeadZone < 12.0, $"within a degree of the camera's heading only from {settledInDeadZone} s (half a degree: {settled} s)");
    }
}
