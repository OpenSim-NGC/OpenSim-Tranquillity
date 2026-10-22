/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Halcyon's vehicle code has figures that are applied per step, or in the wrong units, held under
/// <see cref="VehicleBehaviour.InWorldz"/> as Halcyon ran them at its own 15 ms step and made the same at every rate:
/// the in-air motor-disable rule's ray cadence and clock, the upward-motor gravity check, the linear deflection's 0.005 m/s
/// threshold and the tilt measure; and other behaviours that are checked here on the same grid: the sled's
/// slope assist, the attractor's escalation, its dormancy and its damping. Each is run at 32, 45 and 64 Hz Updates with 1
/// to 4 collision steps (the controller's own step listener split, <see cref="BehaviourRig.StepUpdate"/>), within 1% of
/// the scale of the answer. Pure (a body with no engine), so the class runs in parallel.
/// </summary>
public class InWorldzStepRateFiguresTests
{
    private readonly ITestOutputHelper _out;
    public InWorldzStepRateFiguresTests(ITestOutputHelper output) { _out = output; }

    private const double Deg = Math.PI / 180.0;
    private const double HalcyonStep = 0.015;
    private const Vehicle DisableAbove = (Vehicle)11004, DisableAfter = (Vehicle)11005;
    private static VehicleSettings InWorldz => new() { Behaviour = VehicleBehaviour.InWorldz };
    private static VehicleSettings SecondLife => new() { Behaviour = VehicleBehaviour.SecondLife };

    public static IEnumerable<object[]> Grid()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = 1; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private void Near(double got, double expected, double scale, string what, double rate, int n, double percent = 1.0)
    {
        double error = Math.Abs(got - expected) / scale * 100.0;
        _out.WriteLine($"[step-rate] {what} | {rate} Hz x {n} | {got:0.00000} against {expected:0.00000} | {error:0.000}% of {scale}");
        Assert.True(error <= percent, $"{what} at {rate} Hz with {n} collision steps: {got:0.00000} against {expected:0.00000} (within {percent}% of {scale})");
    }

    // ---- The motor-height rule's ray cadence and clock -------------------------------------------------------

    // A car 5 m above a floor it never reaches the rays of (they stop short), moving, with the rule's limit and delay set.
    private static BehaviourRig HighCar(VehicleSettings s, float after)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Body.BoundsHalfSize = new Vector3(1f, 1f, 0.5f);
        rig.Body.RayFloorZ = 95f;
        rig.Car.ProcessFloatVehicleParam(DisableAbove, 1f);
        rig.Car.ProcessFloatVehicleParam(DisableAfter, after);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 6f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(20f, 0f, 0f));
        rig.Body.LinearVelocity = new Vector3(3f, 0f, 0f);
        return rig;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_down_ray_is_cast_every_eight_of_Halcyons_steps_whatever_the_rate(double rate, int n)
    {
        // Two rays a cast (left edge finds nothing near, then the right); 12 s of simulated time.
        var rig = HighCar(InWorldz, after: 1000f);
        int updates = (int)Math.Round(12 * rate);
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
        double casts = rig.Body.RaysCast / 2.0;
        double expected = updates / rate / (8 * HalcyonStep);
        Near(casts, expected, expected, "ray casts in 12 s", rate, n, 1.0);
        Assert.Equal(0, new BehaviourRig(SecondLife, Vehicle.TYPE_CAR).Body.RaysCast);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_motors_go_off_when_the_clock_runs_out_at_the_same_time_at_every_rate(double rate, int n)
    {
        // The clock runs eight times fast (Halcyon adds eight steps' time on every step): a delay of 80 s runs out 10 s after
        // the first cast, 0.12 s in. The Update that sees it is the first with the motors off.
        var rig = HighCar(InWorldz, after: 80f);
        int updates = 0;
        while (!rig.Car.MotorsOffByHeight && updates < 20 * rate)
        {
            rig.StepUpdate(1.0 / rate, n);
            updates++;
        }
        double at = updates / rate;
        Near(at, 8 * HalcyonStep + 80.0 / 8.0, 10.12, "time the motors go off", rate, n, 1.0);
    }

    // ---- The upward-motor gravity check ----------------------------------------------------------------------

    // A motor that closes a gap of 12 m/s to its target in one step keeps 12 - 9.8 = 2.2 m/s, whatever the step; one that
    // closes 8 m/s keeps nothing; buoyancy carries its share of gravity; a downward change is never touched.
    [Theory]
    [MemberData(nameof(Grid))]
    public void An_up_push_must_gain_over_gravity_a_step_whatever_the_step(double rate, int n)
    {
        Near(HalcyonVehicleLaws.UpwardMotorChange(12f, -9.8f, 0f), 2.2, 12.0, "kept part of a 12 m/s gap", rate, n, 0.01);
        Assert.Equal(0f, HalcyonVehicleLaws.UpwardMotorChange(8f, -9.8f, 0f));
        Assert.Equal(7.1f, HalcyonVehicleLaws.UpwardMotorChange(12f, -9.8f, 0.5f), 4);
        Assert.Equal(0.3f, HalcyonVehicleLaws.UpwardMotorChange(0.3f, -9.8f, 1f));
        Assert.Equal(-0.3f, HalcyonVehicleLaws.UpwardMotorChange(-0.3f, -9.8f, 0f));
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_slow_upward_motor_does_not_lift_a_car_under_InWorldz_and_does_under_Second_Life(double rate, int n)
    {
        // An up motor of 4 m/s with a 1 s timescale pushes 4 m/s^2 at first: far under 650. Over 0.5 s Halcyon's car falls
        // as if there were no motor; Second Life's is held up by it.
        double Vz(VehicleSettings s)
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: true);
            rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 4f));
            int updates = (int)Math.Round(0.5 * rate);
            for (int i = 0; i < updates; i++)
                rig.StepUpdate(1.0 / rate, n);
            return rig.Body.LinearVelocity.Z;
        }
        double fall = -9.80665 * Math.Round(0.5 * rate) / rate;
        Near(Vz(InWorldz), fall, 9.80665 * 0.5, "vertical speed with a slow up motor, InWorldz", rate, n, 1.0);
        Assert.True(Vz(SecondLife) > fall + 0.5, $"Second Life's up motor held the car up ({Vz(SecondLife):0.000} against {fall:0.000})");
    }

    // ---- The first step ---------------------------------------------------------------------------------------

    // Halcyon's first call integrates 0.2 of the step (the smoothed step starts from a Timestep of 0). The controller smooths
    // nothing: the first Update of a new vehicle moves it as far as any other, so a motor's first step is the exact one.
    [Theory]
    [MemberData(nameof(Grid))]
    public void The_first_update_of_a_new_vehicle_is_a_whole_step(double rate, int n)
    {
        var rig = new BehaviourRig(SecondLife, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        rig.StepUpdate(1.0 / rate, n);
        double t = 1.0 / rate, td = 120.0;
        double expected = 8 * (1.0 - Math.Exp(-td * (1.0 - Math.Exp(-t / td)) / 2.0));
        Near(rig.Body.LinearVelocity.X, expected, expected, "speed after the first update", rate, n, 1.0);
    }

    // ---- The deflection's 0.005 m/s threshold ---------------------------------------------------------------

    private static double DeflectedAngle(double startDegrees, double rate, int n, double seconds)
    {
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1f);
        double a = startDegrees * Deg;
        rig.Body.LinearVelocity = new Vector3((float)(10 * Math.Cos(a)), 0f, (float)(10 * Math.Sin(a)));
        int updates = (int)Math.Round(seconds * rate);
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
        Vector3 v = rig.Body.LinearVelocity;
        return Math.Atan2(v.Z, v.X) / Deg;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_push_under_005_m_per_s_a_step_is_dropped_by_acceleration_not_by_step(double rate, int n)
    {
        // Speed 10 m/s, timescale 1 s, so the push is about 10 a m/s^2 for an angle a: Halcyon's 0.005 m/s in 15 ms is
        // 0.333 m/s^2, an angle of 1.9 degrees. 1.2 degrees off the nose is not deflected at all; 3 degrees is turned until
        // the push falls under the line, and stays there.
        Near(DeflectedAngle(1.2, rate, n, 1.0), 1.2, 3.0, "1.2 degrees off the nose after 1 s", rate, n, 0.5);
        double stopped = DeflectedAngle(3.0, rate, n, 1.5);
        Near(stopped, 1.9099, 3.0, "3 degrees off the nose after 1.5 s", rate, n, 1.5);
    }

    // ---- The tilt measure ------------------------------------------------------------------------------------

    [Fact]
    public void The_tilt_is_the_angle_of_the_roll_and_pitch_rotation_without_the_yaw()
    {
        Quaternion Tilt(double roll, double pitch, double yaw) =>
            // llEuler2Rot's order: the yaw is applied first, so the z axis does not depend on it.
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(roll * Deg))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(pitch * Deg))
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(yaw * Deg));
        Assert.Equal(40.0, HalcyonVehicleLaws.TiltAngle(Tilt(40, 0, 0)) / Deg, 2);
        Assert.Equal(40.0, HalcyonVehicleLaws.TiltAngle(Tilt(0, 40, 0)) / Deg, 2);
        Assert.Equal(40.0, HalcyonVehicleLaws.TiltAngle(Tilt(0, -40, 77)) / Deg, 2);    // the yaw makes no difference
        double both = HalcyonVehicleLaws.TiltAngle(Tilt(45, 45, 0)) / Deg;
        Assert.Equal(2 * Math.Acos(Math.Cos(22.5 * Deg) * Math.Cos(22.5 * Deg)) / Deg, both, 2);   // 62.8 degrees
        // The z axis is 60 degrees off up there: the measures differ.
        Assert.True(both > 62.0 && both < 63.5, $"{both}");
        Assert.Equal(180.0, HalcyonVehicleLaws.TiltAngle(Tilt(180, 0, 0)) / Deg, 2);
    }

    // ---- The sled's slope assist (regression) ------------------------------------------

    private static double SledGain(double rate, int n, Vector3? offset = null)
    {
        double Speed(float assist)
        {
            var rig = new BehaviourRig(new VehicleSettings { Behaviour = VehicleBehaviour.InWorldz, SledAssist = assist }, Vehicle.TYPE_SLED, gravity: true);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(30f, 1000f, 1000f));
            if (offset.HasValue)
                rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_OFFSET, offset.Value);
            rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(15 * Deg));
            rig.Body.HoldOrientation = true;   // held at its pitch
            int updates = (int)Math.Round(rate);
            for (int i = 0; i < updates; i++)
                rig.StepUpdate(1.0 / rate, n);
            return Vector3.Dot(rig.Body.LinearVelocity, Vector3.UnitX * rig.Body.Orientation);
        }
        return Speed(VehicleSettings.DefaultSledAssist) - Speed(0f);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_sleds_assist_gives_the_same_speed_at_every_rate(double rate, int n)
    {
        double a = 3 * HalcyonStep * 9.80665 * Math.Sqrt(Math.Sin(15 * Deg));
        double expected = a * 30.0 * (1 - Math.Exp(-1.0 / 30.0));
        Near(SledGain(rate, n), expected, expected, "sled assist gain after 1 s", rate, n, 1.0);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_sleds_assist_is_the_same_with_and_without_a_linear_motor_offset(double rate, int n)
    {
        // Halcyon applies the assist as a force at the offset as an impulse, which is 1 / 15 ms times the force the same
        // code applies without an offset; the result is a unit slip about 66 times too large (a default sled would reach about
        // 70 m/s within 4 s on a 15 degree slope), so the assist is not copied. It is the rate-independent 0.045 g
        // sqrt(|sin pitch|) with the offset the default sled has, and without one.
        double expected = 0.045 * 9.80665 * Math.Sqrt(Math.Sin(15 * Deg)) * 30.0 * (1 - Math.Exp(-1.0 / 30.0));
        Near(SledGain(rate, n, new Vector3(0f, 0f, -0.1f)), expected, expected, "sled assist, default offset", rate, n, 1.0);
        Near(SledGain(rate, n, Vector3.Zero), expected, expected, "sled assist, no offset", rate, n, 1.0);
    }

    // ---- The attractor (regression) ------------------------------------------------

    private static BehaviourRig HeldOnItsBack(double rate, int n, int updates)
    {
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1.5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.6f);
        rig.Tilt(150, 0);
        rig.Body.HoldOrientation = true;   // held: the orientation does not change
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
        return rig;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void An_overturned_vehicle_has_its_strength_grow_by_1_3_per_15_ms_at_every_rate(double rate, int n)
    {
        int updates = (int)Math.Round(0.09 * rate);
        var rig = HeldOnItsBack(rate, n, updates);
        double expected = Math.Pow(1.3, updates / rate / HalcyonStep);
        Near(rig.Car.AttractorEscalation, expected, expected, "escalation", rate, n, 0.1);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_vehicle_that_improves_has_its_strength_fall_by_1_1_per_15_ms_at_every_rate(double rate, int n)
    {
        int updates = (int)Math.Round(0.12 * rate);
        // Up to its limit of 10 on its back, then upright-ish: tilt below 0.6 pi, so it falls.
        var rig = HeldOnItsBack(rate, n, (int)Math.Round(0.3 * rate));
        Assert.Equal(VehicleController.MaxAttractorEscalation, rig.Car.AttractorEscalation);
        rig.Tilt(60, 0);
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
        double expected = Math.Max(1.0, 10.0 / Math.Pow(1.1, updates / rate / HalcyonStep));
        Near(rig.Car.AttractorEscalation, expected, expected, "escalation after recovery", rate, n, 0.1);
    }

    private static void RunTo(BehaviourRig rig, double rate, int n, ref int done, int to)
    {
        for (; done < to; done++)
            rig.StepUpdate(1.0 / rate, n);
    }

    private static float AngularChange(BehaviourRig rig, double rate, int n, ref int done, int from, int to)
    {
        RunTo(rig, rate, n, ref done, from);
        Vector3 before = rig.Body.AngularVelocity;
        RunTo(rig, rate, n, ref done, to);
        return (rig.Body.AngularVelocity - before).Length();
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_slowly_tilting_vehicle_goes_dormant_after_1_5_s_at_every_rate(double rate, int n)
    {
        int At(double seconds) => (int)Math.Round(rate * seconds);
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 5f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
        rig.Tilt(3, 0);
        int done = 0;
        Assert.True(AngularChange(rig, rate, n, ref done, At(1.0), At(1.2)) > 1e-4f, $"{rate} Hz x {n}: still righting at 1 s");
        Assert.True(AngularChange(rig, rate, n, ref done, At(1.6), At(1.9)) < 1e-5f, $"{rate} Hz x {n}: still acting at 1.6 s");
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_attractors_damping_takes_the_same_share_a_second_at_every_rate(double rate, int n)
    {
        // A car rolling at 1 rad/s from a 20 degree roll with a slow attractor (400 s, so its push is small beside the
        // damping), efficiency 0.5: e^(0.5/e) - 1 = 0.2019 of the roll rate goes every 15 ms.
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 400f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.5f);
        rig.Tilt(20, 0);
        Vector3 x = Vector3.UnitX * rig.Body.Orientation;
        rig.Body.AngularVelocity = x;       // 1 rad/s about the body's x
        int updates = (int)Math.Round(0.1 * rate);
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
        double expected = Math.Pow(1.0 - (Math.Exp(0.5 / Math.E) - 1.0), updates / rate / HalcyonStep);
        Near(Vector3.Dot(rig.Body.AngularVelocity, Vector3.UnitX * rig.Body.Orientation), expected, 1.0, "roll rate left", rate, n, 1.5);
    }
}
