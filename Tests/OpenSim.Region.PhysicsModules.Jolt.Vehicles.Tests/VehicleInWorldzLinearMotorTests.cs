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
/// InWorldz's linear motor and the clamp of its angular motor against the vertical attractor, under
/// [Jolt] VehicleBehaviour = InWorldz, against the scalar emulation above (Halcyon's formulas, read, not run) and against
/// the closed form of the ramp. Second Life's behaviour keeps its own motor, fixed to the bit by
/// VehicleSolverUnchangedTests. Pure (a body with no engine), parallel.
/// </summary>
public class VehicleInWorldzLinearMotorTests
{
    private static VehicleSettings SecondLife => new VehicleSettings { Behaviour = VehicleBehaviour.SecondLife };
    private static VehicleSettings InWorldz => new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz };

    private static void Near(double expected, double actual, double relative, double absolute, string what)
        => Assert.True(Math.Abs(expected - actual) <= absolute + relative * Math.Abs(expected), $"{what}: {actual:R} against {expected:R}");

    // A car with only its linear motor acting, by the float setters (all three axes the same) unless a vector is given.
    private static BehaviourRig Car(VehicleSettings s, float tm, float td, Vector3 direction, bool gravity = false)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, tm);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, td);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, direction);
        return rig;
    }

    private static double[] D(Vector3 v) => new double[] { v.X, v.Y, v.Z };

    // ---- the ramp and the decay against the emulation ----

    [Theory]
    [InlineData(0.5f, 1000f, 45.0, 8f, 4.0)]
    [InlineData(2f, 1000f, 45.0, 8f, 6.0)]
    [InlineData(1f, 3f, 45.0, 5f, 12.0)]
    [InlineData(0.05f, 2f, 45.0, 10f, 6.0)]
    [InlineData(1.5f, 6f, 11.0, 10f, 20.0)]
    [InlineData(4f, 500f, 90.0, 6f, 10.0)]
    public void The_linear_motor_follows_Halcyons_stepwise_law(float tm, float td, double hz, float direction, double seconds)
    {
        double h = 1.0 / hz;
        var rig = Car(InWorldz, tm, td, new Vector3(direction, direction * 0.4f, 0f));
        var want = new HalcyonLinearReference(new double[] { direction, direction * 0.4f, 0 }, new double[] { tm, tm, tm }, new double[] { td, td, td });
        int steps = (int)Math.Round(seconds * hz);
        for (int i = 1; i <= steps; i++)
        {
            rig.Step(h);
            want.Step(h);
            if (i % Math.Max(1, steps / 12) == 0)
            {
                Near(want.V[0], rig.Body.LinearVelocity.X, 0.002, 0.003, $"x at step {i}");
                Near(want.V[1], rig.Body.LinearVelocity.Y, 0.002, 0.003, $"y at step {i}");
            }
        }
    }

    // The ramp is "an exponential fit between two velocity points in TAU time": ln(v / M) = ln(v0 / M) e^(-t / Tm), the
    // closed form of v' = v ln(M / v) / Tm, from the stiction start v0 = 0.04 m/s. Halcyon's motor takes the ramp in steps of
    // 15 ms, each Euler's, a share ln(M / v) / (Tm / 0.015) of the velocity, and takes its first step 15 ms after it is set (the
    // first of the clock's steps to fall due). For a timescale of 2 s and over the step's own error is within the tolerance and
    // the curve is the closed form, started 15 ms after the set; for a quick motor (0.5 s: a share of 16 % in the first step) the
    // step's error is more than the tolerance, so the curve is the stepwise recursion, worked out here by itself.
    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    [InlineData(8f)]
    public void The_ramp_is_the_closed_form_curve_of_its_timescale(float tm)
    {
        var rig = Car(InWorldz, tm, 1000f, new Vector3(8f, 0f, 0f));
        double h = 1.0 / 360;
        double v0 = 0.04, m = 8.0;
        double stepwise = 0.0;   // the recursion's velocity after the fires that have fallen due
        double carry = 0.0;
        for (int i = 1; i <= (int)(5.0 / h); i++)
        {
            rig.Step(h);
            carry += h;
            while (carry >= 0.015 - 1e-9)
            {
                carry -= 0.015;
                stepwise = stepwise == 0.0 ? v0 : stepwise * (1.0 + Math.Log(m / stepwise) / (tm / 0.015));
            }
            if (i % 180 == 0)
            {
                double t = i * h;
                double expected = tm < 2f ? stepwise : m * Math.Exp(Math.Log(v0 / m) * Math.Exp(-(t - 0.015) / tm));
                Near(expected, rig.Body.LinearVelocity.X, 0.03, 0.02, $"t = {t:0.00} s");
            }
        }
    }

    [Fact]
    public void A_motor_at_rest_starts_at_eight_times_the_stiction_speed()
    {
        var rig = Car(InWorldz, 2f, 1000f, new Vector3(8f, 0f, 0f));
        rig.Step(1.0 / 45);
        Assert.Equal(0.04, rig.Body.LinearVelocity.X, 5);
        // Second Life's motor, over the same step, pulls by a share of the direction instead: 8 x (1 - e^(-h / 2)) = 0.088 m/s.
        var sl = Car(SecondLife, 2f, 1000f, new Vector3(8f, 0f, 0f));
        sl.Step(1.0 / 45);
        Assert.Equal(8.0 * (1.0 - Math.Exp(-(1.0 / 45) / 2.0)), sl.Body.LinearVelocity.X, 3);
    }

    // Friction is a change of velocity of its own, on top of the motor's: it does not hold the ramp down, which works from the
    // motor's target. The documented equation settles at M Tf / (Tf + Tm) = 1.67 m/s here.
    [Fact]
    public void A_short_friction_does_not_hold_the_InWorldz_linear_motor_down()
    {
        float Run(VehicleSettings s)
        {
            var rig = Car(s, 1f, 1000f, new Vector3(5f, 0f, 0f));
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(0.5f, 1000f, 1000f));
            rig.Run(1.0 / 45, 45 * 8);
            return rig.Body.LinearVelocity.X;
        }
        float iw = Run(InWorldz), sl = Run(SecondLife);
        Assert.True(iw > 4.5, $"{iw}");
        Assert.InRange(sl, 1.4, 2.0);
    }

    [Fact]
    public void The_motor_ends_with_its_decay_and_the_vehicle_keeps_its_speed()
    {
        var rig = Car(InWorldz, 1f, 1f, new Vector3(5f, 0f, 0f));
        rig.Run(1.0 / 45, 45 * 8);
        float at8 = rig.Body.LinearVelocity.X;
        rig.Run(1.0 / 45, 45 * 4);
        Assert.Equal((double)at8, rig.Body.LinearVelocity.X, 4);
        Assert.InRange(at8, 0.5, 5.0);
    }

    [Fact]
    public void A_vehicle_faster_than_its_motor_is_slowed_toward_it()
    {
        var rig = Car(InWorldz, 1f, 1000f, new Vector3(5f, 0f, 0f));
        rig.Body.LinearVelocity = new Vector3(20f, 0f, 0f);
        var want = new HalcyonLinearReference(new double[] { 5, 0, 0 }, new double[] { 1, 1, 1 }, new double[] { 1000, 1000, 1000 });
        want.V[0] = 20;
        for (int i = 0; i < 90; i++)
        {
            rig.Step(1.0 / 45);
            want.Step(1.0 / 45);
        }
        Near(want.V[0], rig.Body.LinearVelocity.X, 0.002, 0.003, "x");
        Assert.True(rig.Body.LinearVelocity.X < 19.0, $"{rig.Body.LinearVelocity.X}");
    }

    [Fact]
    public void A_new_direction_after_a_decay_starts_from_the_decayed_target_and_a_reversal_crosses_zero()
    {
        var rig = Car(InWorldz, 1f, 2f, new Vector3(5f, 0f, 0f));
        var want = new HalcyonLinearReference(new double[] { 5, 0, 0 }, new double[] { 1, 1, 1 }, new double[] { 2, 2, 2 });
        for (int i = 0; i < 45 * 3; i++)
        {
            rig.Step(1.0 / 45);
            want.Step(1.0 / 45);
        }
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(-4f, 0f, 0f));
        want.Move(new double[] { -4, 0, 0 });
        for (int i = 0; i < 45 * 6; i++)
        {
            rig.Step(1.0 / 45);
            want.Step(1.0 / 45);
        }
        Near(want.V[0], rig.Body.LinearVelocity.X, 0.002, 0.004, "x after the reversal");
        Assert.True(rig.Body.LinearVelocity.X < 0f, $"{rig.Body.LinearVelocity.X}");
    }

    // ---- the law itself ----

    [Fact]
    public void A_stalled_motor_ends_and_adds_nothing()
    {
        float index = 3f;
        Vector3 target = new Vector3(2f, 0f, 0f);
        Vector3 last = new Vector3(1f, 0f, 0f);
        Vector3 result = HalcyonVehicleLaws.LinearMotor(1f / 45, last, new Vector3(5f, 0f, 0f), new Vector3(1f), new Vector3(10f),
            ref index, ref target, 200f, () => true);
        Assert.Equal(last, result);
        Assert.Equal(VehicleLimits.MaxDecayTimescale * 100.0f, index);
        Assert.Equal(Vector3.Zero, target);

        float index2 = 3f;
        Vector3 target2 = new Vector3(2f, 0f, 0f);
        Vector3 moving = HalcyonVehicleLaws.LinearMotor(1f / 45, last, new Vector3(5f, 0f, 0f), new Vector3(1f), new Vector3(10f),
            ref index2, ref target2, 200f, () => false);
        Assert.True(moving.X > last.X);
    }

    [Fact]
    public void A_motor_whose_decay_has_run_out_follows_the_velocity()
    {
        float index = 10000f;
        Vector3 target = new Vector3(2f, 3f, 4f);
        Vector3 last = new Vector3(1f, 0.5f, -1f);
        bool asked = false;
        Vector3 result = HalcyonVehicleLaws.LinearMotor(1f / 45, last, new Vector3(5f, 0f, 0f), new Vector3(1f), new Vector3(10f),
            ref index, ref target, 200f, () => asked = true);
        Assert.Equal(last, result);
        Assert.Equal(last, target);
        Assert.False(asked);
    }

    [Fact]
    public void The_speed_is_held_to_the_cap()
    {
        float index = 0f;
        Vector3 target = new Vector3(150f, 0f, 0f);
        Vector3 result = HalcyonVehicleLaws.LinearMotor(1f / 45, new Vector3(150f, 0f, 0f), new Vector3(400f, 0f, 0f), new Vector3(0.5f), new Vector3(1000f),
            ref index, ref target, 200f, () => false);
        Assert.True(result.X <= 200f + 1e-3f, $"{result.X}");
    }

    // ---- the angular motor's clamp against the vertical attractor (VehicleDynamics.cs:2809-2856) ----

    [Theory]
    [InlineData(VehicleType.Car, 1.1)]
    [InlineData(VehicleType.Motorcycle, 1.1)]
    [InlineData(VehicleType.Boat, 0.95)]
    [InlineData(VehicleType.Sailboat, 0.95)]
    [InlineData(VehicleType.Airplane, 0.8)]
    [InlineData(VehicleType.Sled, 0.8)]
    [InlineData(VehicleType.Balloon, 0.8)]
    public void The_clamp_point_is_pi_times_the_types_factor_and_the_excess_is_cut(VehicleType type, double factor)
    {
        double clamp = Math.PI * factor;
        double over = Math.Log(1.0 + 0.06) / Math.Log(500.0);   // a vertical attraction timescale of 1 s
        // Under the clamp point nothing changes, either way round.
        Assert.Equal(clamp - 0.1, HalcyonVehicleLaws.AttractorClamp((float)(clamp - 0.1), type, 1f), 4);
        Assert.Equal(-(clamp - 0.1), HalcyonVehicleLaws.AttractorClamp((float)-(clamp - 0.1), type, 1f), 4);
        // Over it, the excess is cut to the share ln(T + 0.06) / ln(500) of itself.
        Assert.Equal(clamp + 2.0 * over, HalcyonVehicleLaws.AttractorClamp((float)(clamp + 2.0), type, 1f), 4);
        Assert.Equal(-(clamp + 2.0 * over), HalcyonVehicleLaws.AttractorClamp((float)-(clamp + 2.0), type, 1f), 4);
    }

    [Fact]
    public void A_quick_attractor_lowers_the_clamp_point_and_a_slow_one_leaves_more_of_the_excess()
    {
        double clamp = Math.PI * 0.8;
        // Under 1 s the clamp point falls in proportion; the cut still reads the timescale as 1 s.
        double over1 = Math.Log(1.06) / Math.Log(500.0);
        Assert.Equal(clamp * 0.25 + (3.0 - clamp * 0.25) * over1, HalcyonVehicleLaws.AttractorClamp(3f, VehicleType.Airplane, 0.25f), 4);
        // 100 s: the clamp point is the same as at 1 s and the excess keeps ln(100.06) / ln(500) of itself.
        double over100 = Math.Log(100.06) / Math.Log(500.0);
        Assert.Equal(clamp + (6.0 - clamp) * over100, HalcyonVehicleLaws.AttractorClamp(6f, VehicleType.Airplane, 100f), 4);
        Assert.True(over100 > 0.7 && over1 < 0.01);
    }

    // The ramp of a roll motor with a quick, weak-spring attractor saturates a little above the clamp point; with the
    // attractor off (a timescale of 500 s or more) it goes on to the motor's direction. The spring's own pull is kept
    // small (efficiency 0, a timescale of 3 s) so the figures are the motor's.
    [Fact]
    public void The_roll_motor_saturates_near_the_clamp_point_while_the_attractor_is_on()
    {
        double Peak(VehicleSettings s, float attractor)
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_AIRPLANE, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, attractor);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.3f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 1000f);
            rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(6f, 0f, 0f));
            double peak = 0;
            for (int i = 0; i < 60; i++)
            {
                rig.Step(1.0 / 45);
                peak = Math.Max(peak, rig.Body.AngularVelocity.X);
            }
            return peak;
        }
        double clamp = Math.PI * 0.8;
        double on = Peak(InWorldz, 3f);
        double off = Peak(InWorldz, 1000f);
        Assert.InRange(on, clamp * 0.95, clamp * 1.15);
        Assert.True(off > 4.5, $"{off}");
        // Second Life has no such clamp.
        Assert.True(Peak(SecondLife, 3f) > 4.5);
    }
}
