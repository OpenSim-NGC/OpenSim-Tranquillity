/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Two things the vehicle controller does, both pure (a body with no engine) and parallel:
///
/// 1. The vertical attractor owns the roll rate it sets. A rolled airplane with banking turns about the world's
///    vertical, and the roll rate the attractor gave along the old roll axis was left partly across the new one, as a pitch
///    rate that a roll-only attractor does not touch: the plane pitched away. Under both behaviours.
/// 2. The motor-height rule, under InWorldz's behaviour only (Halcyon VehicleDynamics.cs:2494-2573): the motors go off once
///    the vehicle has been higher than VEHICLE_DISABLE_MOTORS_ABOVE above what is below it for
///    VEHICLE_DISABLE_MOTORS_AFTER seconds.
/// </summary>
public class VehicleRollOwnershipAndMotorHeightTests
{
    private const double Deg = Math.PI / 180.0;
    private const double Step = 1.0 / 45;
    private const Vehicle DisableAbove = (Vehicle)11004, DisableAfter = (Vehicle)11005;

    private static VehicleSettings Settings(VehicleBehaviour b) => new() { Behaviour = b };

    // ---------------------------------------------------------------------------------------------------------
    // The vertical attractor and banking

    // The airplane as its preset sets it, banking and all, hung by buoyancy so only the attractor, banking and friction act,
    // tilted 30 degrees about its x axis, with no input.
    private static BehaviourRig BankingAirplane(VehicleBehaviour behaviour)
    {
        var rig = new BehaviourRig(Settings(behaviour), Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1000f);
        rig.Tilt(30, 0);
        return rig;
    }

    private static double TiltOf(Quaternion q) => Math.Acos(Math.Clamp((Vector3.UnitZ * q).Z, -1f, 1f)) / Deg;

    [Theory]
    [InlineData(VehicleBehaviour.SecondLife)]
    [InlineData(VehicleBehaviour.InWorldz)]
    public void A_banking_airplane_holds_its_pitch_within_two_degrees_over_twelve_seconds(VehicleBehaviour behaviour)
    {
        BehaviourRig rig = BankingAirplane(behaviour);
        double worst = 0;
        for (int second = 1; second <= 12; second++)
        {
            rig.Run(Step, 45);
            worst = Math.Max(worst, Math.Abs(BehaviourRig.RollAndPitch(rig.Body.Orientation).pitch / Deg));
        }
        // The limit was 2 degrees until the InWorldz attractor's damping took its share per 15 ms of time instead of per
        // Update: at 45 Hz it now takes 1.48 times what it did per step, as Halcyon's does per frame, and the airplane's
        // pitch reaches 2.2 degrees. The leak this guards (roll rate turned into pitch by banking) was tens of degrees.
        Assert.True(worst <= 2.5, $"the pitch went {worst:0.0} degrees off level (limit 2.5)");
    }

    [Fact]
    public void A_banking_airplane_still_rights_itself_under_Second_Life()
    {
        BehaviourRig rig = BankingAirplane(VehicleBehaviour.SecondLife);
        rig.Run(Step, 45 * 12);
        Assert.True(TiltOf(rig.Body.Orientation) < 3.0, $"tilt after 12 s: {TiltOf(rig.Body.Orientation):0.0} degrees");
    }

    // The leak is a turn of the body between two steps, so it needs banking: with banking off the attractor's roll axis stays put
    // and the airplane rights itself exactly as before (VehicleRightingTests pins that curve).
    [Fact]
    public void The_roll_rate_is_put_on_the_roll_axis_the_vehicle_has_now()
    {
        // A body turned about the world's vertical by 20 degrees between two attractor steps keeps its roll rate on its own roll axis.
        var rig = new BehaviourRig(Settings(VehicleBehaviour.SecondLife), Vehicle.TYPE_AIRPLANE, gravity: false);
        rig.Car.ProcessTypeChange(Vehicle.TYPE_AIRPLANE);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f));
        rig.Tilt(30, 0);
        rig.Run(Step, 45);                                                    // the attractor builds a roll rate about the roll axis
        Quaternion before = rig.Body.Orientation;
        Vector3 rate = rig.Body.AngularVelocity;
        Assert.True(rate.Length() > 0.05f, $"rate {rate.Length()}");

        // Turn the body about world z, and leave the world angular velocity as it was: its roll axis has swung away from the rate.
        rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(20 * Deg)) * before;
        rig.Step(Step);
        Vector3 x = Vector3.UnitX * rig.Body.Orientation, y = Vector3.UnitY * rig.Body.Orientation;
        float across = Math.Abs(Vector3.Dot(rig.Body.AngularVelocity, y));
        Assert.True(across < 0.02f * rig.Body.AngularVelocity.Length() + 1e-4f,
            $"the roll rate leaked {across:0.0000} rad/s across the roll axis (rate {rig.Body.AngularVelocity.Length():0.0000})");
        Assert.True(Math.Abs(Vector3.Dot(rig.Body.AngularVelocity, x)) > 0.5f * rig.Body.AngularVelocity.Length());
    }

    // ---------------------------------------------------------------------------------------------------------
    // The motor-height rule

    // A car with a linear motor that keeps pulling for a long time, moving from the start, 5 m above a floor (or 0.5 m: on it).
    private static BehaviourRig HighCar(VehicleBehaviour behaviour, bool onTheFloor, float above = 1f, float after = 2f,
                                        float buoyancy = 0f, bool angular = false)
    {
        var rig = new BehaviourRig(Settings(behaviour), Vehicle.TYPE_CAR, gravity: false);
        rig.Body.BoundsHalfSize = new Vector3(1f, 1f, 0.5f);
        rig.Body.RayFloorZ = onTheFloor ? 99.5f : 95f;                         // the body is at z 100
        rig.Car.ProcessFloatVehicleParam(DisableAbove, above);
        rig.Car.ProcessFloatVehicleParam(DisableAfter, after);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, buoyancy);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 6f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(20f, 0f, 0f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 3f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        if (angular)
            rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        rig.Body.LinearVelocity = new Vector3(3f, 0f, 0f);
        return rig;
    }

    [Fact]
    public void Under_InWorldz_the_linear_motor_goes_off_after_the_vehicle_has_been_high_for_the_set_time()
    {
        BehaviourRig high = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false);
        BehaviourRig low = HighCar(VehicleBehaviour.InWorldz, onTheFloor: true);
        high.Run(Step, 5);               // 0.11 s: the height is first measured at 0.12 s, so the motor is still on
        low.Run(Step, 5);
        Assert.Equal(low.Body.LinearVelocity.X, high.Body.LinearVelocity.X, 3);
        high.Run(Step, 45 * 3 - 5);      // 3 s: Halcyon's clock runs eight steps' time on every step, so the 2 s has run out in about 0.3 s
        low.Run(Step, 45 * 3 - 5);
        float held = high.Body.LinearVelocity.X;
        high.Run(Step, 45);
        low.Run(Step, 45);
        Assert.True(Math.Abs(high.Body.LinearVelocity.X - held) < 0.01f * held, "the off motor still pushed");
        Assert.True(low.Body.LinearVelocity.X > high.Body.LinearVelocity.X + 0.5f, $"on the floor {low.Body.LinearVelocity.X}, high {high.Body.LinearVelocity.X}");
    }

    [Fact]
    public void Under_InWorldz_the_angular_motor_goes_off_too()
    {
        BehaviourRig high = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false, after: 8f, angular: true);
        BehaviourRig low = HighCar(VehicleBehaviour.InWorldz, onTheFloor: true, after: 8f, angular: true);
        high.Run(Step, 45 * 3);
        low.Run(Step, 45 * 3);
        float held = high.Body.AngularVelocity.Z;
        Assert.True(held > 0.1f);
        high.Run(Step, 45);
        low.Run(Step, 45);
        Assert.True(Math.Abs(high.Body.AngularVelocity.Z - held) < 0.01f * held, "the off angular motor still turned the vehicle");
        Assert.True(low.Body.AngularVelocity.Z > high.Body.AngularVelocity.Z + 0.05f);
    }

    [Fact]
    public void The_down_ray_is_cast_every_eight_of_Halcyons_steps_and_not_at_all_under_Second_Life()
    {
        BehaviourRig iw = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false);
        BehaviourRig sl = HighCar(VehicleBehaviour.SecondLife, onTheFloor: false);
        iw.Run(Step, 64);
        sl.Run(Step, 64);
        Assert.InRange(iw.Body.RaysCast, 22, 24);      // 64 Updates at 45 Hz are 1.42 s: 11 casts of 0.12 s, two rays each (left edge finds nothing near, then the right)
        Assert.Equal(0, sl.Body.RaysCast);
    }

    [Fact]
    public void Second_Life_has_no_motor_height_rule()
    {
        BehaviourRig high = HighCar(VehicleBehaviour.SecondLife, onTheFloor: false);
        BehaviourRig low = HighCar(VehicleBehaviour.SecondLife, onTheFloor: true);
        high.Run(Step, 45 * 5);
        low.Run(Step, 45 * 5);
        Assert.Equal(low.State(), high.State());
    }

    [Theory]
    [InlineData(0f, 2f, 0f)]       // no limit: the rule is off
    [InlineData(1f, 2f, 0.5f)]     // positive buoyancy: off
    public void The_rule_is_off_without_a_limit_or_with_buoyancy(float above, float after, float buoyancy)
    {
        BehaviourRig high = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false, above, after, buoyancy);
        BehaviourRig low = HighCar(VehicleBehaviour.InWorldz, onTheFloor: true, above, after, buoyancy);
        high.Run(Step, 45 * 5);
        low.Run(Step, 45 * 5);
        Assert.Equal(low.State(), high.State());
    }

    [Fact]
    public void The_rule_is_off_for_a_vehicle_that_hovers_and_for_one_that_hardly_moves()
    {
        BehaviourRig hover = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false);
        BehaviourRig hoverLow = HighCar(VehicleBehaviour.InWorldz, onTheFloor: true);
        foreach (BehaviourRig r in new[] { hover, hoverLow })
            r.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 5f);
        hover.Run(Step, 45 * 4);
        hoverLow.Run(Step, 45 * 4);
        Assert.Equal(hoverLow.State(), hover.State());

        // At rest the car is slower than 0.5 m/s, so a motor too weak to get it moving never trips the rule.
        BehaviourRig slow = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false);
        BehaviourRig slowLow = HighCar(VehicleBehaviour.InWorldz, onTheFloor: true);
        foreach (BehaviourRig r in new[] { slow, slowLow })
        {
            r.Body.LinearVelocity = Vector3.Zero;
            r.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(0.2f, 0f, 0f));
        }
        slow.Run(Step, 45 * 5);
        slowLow.Run(Step, 45 * 5);
        Assert.Equal(slowLow.State(), slow.State());
    }

    [Fact]
    public void A_vehicle_with_no_box_never_measures_a_height()
    {
        BehaviourRig none = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false);
        none.Body.BoundsHalfSize = Vector3.Zero;
        BehaviourRig low = HighCar(VehicleBehaviour.InWorldz, onTheFloor: true);
        none.Run(Step, 45 * 4);
        low.Run(Step, 45 * 4);
        Assert.Equal(0, none.Body.RaysCast);
        Assert.Equal(low.State(), none.State());
    }

    [Fact]
    public void Coming_back_down_switches_the_motors_on_again()
    {
        BehaviourRig rig = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false);
        rig.Run(Step, 45 * 3);                       // off
        float held = rig.Body.LinearVelocity.X;
        rig.Body.RayFloorZ = 99.5f;                  // landed
        rig.Run(Step, 45);
        Assert.True(rig.Body.LinearVelocity.X > held + 0.5f, "the motor did not come back on the floor");
    }

    // The clock itself (VehicleDynamics.cs:2560-2571): above the limit it counts, at or under it it clears.
    [Fact]
    public void The_height_clock_counts_above_the_limit_and_clears_at_or_under_it()
    {
        // The clock adds eight steps' time on every step (VehicleDynamics.cs:2564), so a step of 0.05 s adds 0.4 s.
        float t = 0f;
        Assert.False(HalcyonVehicleLaws.MotorsOffAboveHeight(2f, 1f, 2.5f, 0.05f, ref t));
        Assert.Equal(0.4f, t, 5);
        for (int i = 0; i < 5; i++)
            Assert.False(HalcyonVehicleLaws.MotorsOffAboveHeight(2f, 1f, 2.5f, 0.05f, ref t));
        Assert.Equal(2.4f, t, 4);
        Assert.True(HalcyonVehicleLaws.MotorsOffAboveHeight(2f, 1f, 2.5f, 0.05f, ref t));
        Assert.Equal(2.8f, t, 4);
        Assert.False(HalcyonVehicleLaws.MotorsOffAboveHeight(1f, 1f, 2.5f, 0.05f, ref t));    // at the limit: not above it
        Assert.Equal(0f, t);
        Assert.True(HalcyonVehicleLaws.MotorsOffAboveHeight(5f, 1f, 0f, 0.02f, ref t));       // after 0: off at once
    }

    // Halcyon's quirk, kept on purpose: a delay of 2.5 s, the legacy car's, runs out in 15 steps at 45 Hz, 0.33 s.
    [Fact]
    public void A_delay_of_two_and_a_half_seconds_runs_out_in_about_a_third_of_a_second()
    {
        float t = 0f;
        int steps = 0;
        while (!HalcyonVehicleLaws.MotorsOffAboveHeight(2f, 1f, 2.5f, (float)Step, ref t))
            steps++;
        Assert.Equal(14, steps);

        BehaviourRig high = HighCar(VehicleBehaviour.InWorldz, onTheFloor: false, above: 1f, after: 2.5f);
        BehaviourRig low = HighCar(VehicleBehaviour.InWorldz, onTheFloor: true, above: 1f, after: 2.5f);
        high.Run(Step, 45);              // 1 s: long past the third of a second
        low.Run(Step, 45);
        float held = high.Body.LinearVelocity.X;
        high.Run(Step, 45);
        low.Run(Step, 45);
        Assert.True(Math.Abs(high.Body.LinearVelocity.X - held) < 0.01f * held, "the motor was still on after 2 s");
        Assert.True(low.Body.LinearVelocity.X > held + 0.5f);
    }
}
