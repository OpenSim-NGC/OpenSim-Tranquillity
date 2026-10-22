/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// InWorldz's linear motor uses its target in place of a lower velocity (VehicleDynamics.cs:2596-2598, "the target ensures
/// forward progress"). That is a jump of the whole gap once per step of 15 ms, so over a second it must make the same push
/// whatever the step: a gap that something else keeps open (the motor-up limit drops the vertical part of the push that
/// closes it, and a car on a slope is tilted, so the rest points along its path; a car jammed against another is pushed back
/// into it) is closed again in every step.
/// </summary>
public class VehicleInWorldzTargetStepTests
{
    // A car sliding level down a slope: 8 m/s forward at the motor's direction, 3.6 m/s down in its own frame, and a motor target
    // of 0.2 m/s up that the undriven axis has ramped to. Every step the same state comes back (the engine's gravity and the
    // limit on the motor's upward push leave the velocity as it was), so the sum of the motor's changes over a second is
    // what the target jump pushes in that second.
    private static double PushOverOneSecond(float step)
    {
        var last = new Vector3(8f, 0f, -3.6f);
        var direction = new Vector3(8f, 0f, 0f);
        var target = new Vector3(8f, 0f, 0.2f);
        float index = 0.05f;
        double sum = 0;
        int steps = (int)Math.Round(1.0 / step);
        for (int i = 0; i < steps; i++)
        {
            Vector3 result = HalcyonVehicleLaws.LinearMotor(step, last, direction, new Vector3(0.5f, 1f, 1f), new Vector3(10f, 2f, 2f),
                ref index, ref target, 200f, () => false);
            sum += result.Z - last.Z;
            index = 0.05f;   // the held key keeps the motor's clock near its start
            target = new Vector3(8f, 0f, 0.2f);
        }
        return sum;
    }

    [Theory]
    [InlineData(1f / 270)]
    [InlineData(1f / 128)]
    [InlineData(1f / 90)]
    [InlineData(1f / 45)]
    public void The_targets_jump_over_a_second_is_the_same_at_every_step(float step)
    {
        double atHalcyonStep = PushOverOneSecond((float)VehicleController.HalcyonStep);
        double atThisStep = PushOverOneSecond(step);
        // A step longer than Halcyon's takes the whole gap, so it makes fewer jumps a second; only the shorter ones are held to it.
        double expected = step < (float)VehicleController.HalcyonStep ? atHalcyonStep : atThisStep;
        Assert.True(Math.Abs(atThisStep - expected) <= 0.02 * Math.Abs(expected),
            $"step {step:0.0000}: {atThisStep:0.0} against {atHalcyonStep:0.0} at Halcyon's step");
    }

    [Fact]
    public void A_step_of_Halcyons_length_takes_the_whole_gap()
    {
        var last = new Vector3(0f, 0f, -3.6f);
        var target = new Vector3(0f, 0f, 0.2f);
        float index = 0.05f;
        Vector3 result = HalcyonVehicleLaws.LinearMotor((float)VehicleController.HalcyonStep, last, Vector3.Zero, new Vector3(1f),
            new Vector3(10f), ref index, ref target, 200f, () => false);
        Assert.True(result.Z > 0f, $"{result.Z}: the velocity was not brought up to the target");
    }

    // ---- on a slope, with the shipped cars ----

    [Collection(JoltNativeSerial.Name)]
    public class On_a_slope
    {
        // The car preset's motor is 8 m/s and its key is held down a 15 degree ramp (car-down) or a 5 degree one (testcar-down).
        // Gravity along the ramp gives a car that lifts off its crest a few m/s over the motor, not more: before the target was
        // taken by its share of the gap, the car reached 66 m/s at 45 Hz on the ramp and 42 m/s at 90 Hz on the 5 degree one.
        [Theory]
        [InlineData("car-down", 15f, 18.0)]
        [InlineData("testcar-down", 5f, 14.0)]
        public void A_car_driven_down_a_ramp_does_not_run_away_at_any_rate(string scenario, float slope, double limit)
        {
            foreach (double rate in Harness.Harness.Rates)
            {
                float top = Harness.Harness.Run(Harness.Harness.Find(scenario),
                    new HarnessOptions { RateHz = rate, SlopeDeg = slope }).Summary.TopSpeed;
                Assert.True(top <= limit, $"{scenario} {slope} degrees at {rate} Hz: top speed {top:0.0} m/s (limit {limit})");
            }
        }
    }
}
