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
/// The vehicle controller's per-collision-step terms give the same result at every Update rate and number of collision
/// steps. The controller runs as the engine's step listener runs it: BeginUpdate once per Update, then StepOnce before each
/// of n collision steps of the Update's length / n (BehaviourRig.StepUpdate). Each case is an equation with a closed-form
/// solution, checked at 1 s and 2 s of simulated time at 32, 45 and 64 Hz Updates with 1 to 4 collision steps, within 1% of
/// the scale of the answer. Pure (a body with no engine), so the classes run in parallel.
/// </summary>
public class VehicleStepRateTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public VehicleStepRateTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

    private const double Deg = Math.PI / 180.0;

    public static IEnumerable<object[]> Grid()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = 1; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private static BehaviourRig NewRig(Vehicle type = Vehicle.TYPE_CAR, bool gravity = false) => new(new VehicleSettings(), type, gravity);

    // Steps the rig to each whole second in turn and reads its value there.
    private static double[] AtSeconds(BehaviourRig rig, double rate, int n, Func<BehaviourRig, double> read, params int[] seconds)
    {
        var values = new List<double>();
        int updates = 0;
        foreach (int s in seconds)
        {
            int target = (int)Math.Round(s * rate);
            for (; updates < target; updates++)
                rig.StepUpdate(1.0 / rate, n);
            values.Add(read(rig));
        }
        return values.ToArray();
    }

    private void Near(double got, double expected, double scale, string what, double rate, int n, int second, double percent = 1.0)
    {
        double error = Math.Abs(got - expected) / scale * 100.0;
        _out.WriteLine($"[rate-table-ctl] {what} | {rate} Hz x {n} | t {second} | {error:0.000}% of {scale}");
        Assert.True(error <= percent,
            $"{what} at {rate} Hz with {n} collision steps, {second} s: {got:0.00000} against {expected:0.00000} (within {percent}% of {scale})");
    }

    // dv/dt = g(s) (M - v) / Tm, g(s) = e^(-s / Td), s the time since the motor was set.
    private static double Ramp(double target, double tm, double td, double t) => target * (1.0 - Math.Exp(-td * (1.0 - Math.Exp(-t / td)) / tm));

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_linear_motor_ramps_to_speed_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        double[] v = AtSeconds(rig, rate, n, r => r.Body.LinearVelocity.X, 1, 2);
        Near(v[0], Ramp(8, 2, 120, 1), 8, "linear motor", rate, n, 1);
        Near(v[1], Ramp(8, 2, 120, 2), 8, "linear motor", rate, n, 2);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void An_angular_motor_ramps_to_speed_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        double[] w = AtSeconds(rig, rate, n, r => r.Body.AngularVelocity.Z, 1, 2);
        Near(w[0], Ramp(1, 2, 120, 1), 1, "angular motor", rate, n, 1);
        Near(w[1], Ramp(1, 2, 120, 2), 1, "angular motor", rate, n, 2);
    }

    // The timescale friction alone, a vehicle in the air (a body with no contact): v(t) = v0 e^(-t / T) on each axis.
    [Theory]
    [MemberData(nameof(Grid))]
    public void Timescale_friction_slows_a_vehicle_in_the_air_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(2f, 1000f, 1000f));
        rig.Body.LinearVelocity = new Vector3(10f, 0f, 0f);
        double[] v = AtSeconds(rig, rate, n, r => r.Body.LinearVelocity.X, 1, 2);
        Near(v[0], 10 * Math.Exp(-0.5), 10, "linear friction", rate, n, 1);
        Near(v[1], 10 * Math.Exp(-1.0), 10, "linear friction", rate, n, 2);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Timescale_angular_friction_slows_a_turning_vehicle_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 2f));
        rig.Body.AngularVelocity = new Vector3(0f, 0f, 3f);
        double[] w = AtSeconds(rig, rate, n, r => r.Body.AngularVelocity.Z, 1, 2);
        Near(w[0], 3 * Math.Exp(-0.5), 3, "angular friction", rate, n, 1);
        Near(w[1], 3 * Math.Exp(-1.0), 3, "angular friction", rate, n, 2);
    }

    // The friction acts along the vehicle's own axes, with its rotation at each collision step: a vehicle turning at 1 rad/s
    // with a friction of 2 s along its nose only, moving along the world's x at 10 m/s. The reference is the same vehicle
    // stepped at 4096 Hz.
    [Theory]
    [MemberData(nameof(Grid))]
    public void Friction_follows_the_axes_of_a_turning_vehicle_alike(double rate, int n)
    {
        BehaviourRig Make()
        {
            var rig = NewRig();
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(2f, 1000f, 1000f));
            rig.Body.LinearVelocity = new Vector3(10f, 0f, 0f);
            rig.Body.AngularVelocity = new Vector3(0f, 0f, 1f);
            return rig;
        }
        var reference = Make();
        for (int i = 0; i < 4096 * 2; i++)
            reference.StepUpdate(1.0 / 4096, 1);
        var run = Make();
        AtSeconds(run, rate, n, r => 0, 2);
        Near(run.Body.LinearVelocity.X, reference.Body.LinearVelocity.X, 10, "turning vehicle's vx", rate, n, 2);
        Near(run.Body.LinearVelocity.Y, reference.Body.LinearVelocity.Y, 10, "turning vehicle's vy", rate, n, 2);
    }

    // Linear deflection turns the velocity toward the nose: the angle between them decays as e^(-efficiency t / T).
    [Theory]
    [MemberData(nameof(Grid))]
    public void Linear_deflection_turns_the_velocity_toward_the_nose_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 2f);
        rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(40 * Deg));
        rig.Body.LinearVelocity = new Vector3(10f, 0f, 0f);
        double Angle(BehaviourRig r)
        {
            Vector3 nose = Vector3.UnitX * r.Body.Orientation;
            Vector3 velocity = r.Body.LinearVelocity;
            Vector3 v = Vector3.Normalize(in velocity);
            return Math.Acos(Math.Clamp(Vector3.Dot(nose, v), -1f, 1f)) / Deg;
        }
        double[] a = AtSeconds(rig, rate, n, Angle, 1, 2);
        Near(a[0], 40 * Math.Exp(-0.5), 40, "deflection angle (degrees)", rate, n, 1);
        Near(a[1], 40 * Math.Exp(-1.0), 40, "deflection angle (degrees)", rate, n, 2);
        Assert.Equal(10.0, rig.Body.LinearVelocity.Length(), 2);   // it turns the velocity and keeps its speed
    }

    // e'' = -e / T^2 - 2 eff e' / T from e0 with e'(0) = 0, the vertical attractor's and hover's spring.
    private static double Spring(double e0, double t, double timescale, double efficiency)
    {
        double w0 = 1.0 / timescale, z = efficiency;
        if (z < 1.0)
        {
            double wd = w0 * Math.Sqrt(1 - z * z);
            return e0 * Math.Exp(-z * w0 * t) * (Math.Cos(wd * t) + z * w0 / wd * Math.Sin(wd * t));
        }
        return e0 * Math.Exp(-w0 * t) * (1 + w0 * t);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_vertical_attractor_rights_a_tilted_vehicle_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.5f);
        rig.Tilt(30, 0);
        double[] roll = AtSeconds(rig, rate, n, r => BehaviourRig.RollAndPitch(r.Body.Orientation).roll / Deg, 1, 2);
        Near(roll[0], Spring(30, 1, 2, 0.5), 30, "roll (degrees)", rate, n, 1);
        Near(roll[1], Spring(30, 2, 2, 0.5), 30, "roll (degrees)", rate, n, 2);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Hover_settles_a_vehicle_at_its_height_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 10f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 2f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_EFFICIENCY, 0.5f);
        rig.Flag(ExtendedVehicleFlags.HoverGlobalHeight, true);
        rig.Body.Position = new Vector3(128f, 128f, 5f);
        double[] z = AtSeconds(rig, rate, n, r => r.Body.Position.Z, 1, 2);
        Near(z[0], 10 + Spring(-5, 1, 2, 0.5), 5, "hover height", rate, n, 1);
        Near(z[1], 10 + Spring(-5, 2, 2, 0.5), 5, "hover height", rate, n, 2);
    }

    // A vehicle held at a 20 degree roll with the attractor on: the yaw rate approaches -roll x efficiency x pi with the
    // banking timescale, w(t) = target (1 - e^(-t / T)). The roll is held by putting the orientation back after each
    // collision step, its yaw following the vehicle's angular velocity about the vertical.
    [Theory]
    [MemberData(nameof(Grid))]
    public void A_banking_turn_comes_to_its_rate_alike(double rate, int n)
    {
        var rig = NewRig();
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 100f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, 0f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, 2f);
        const double Roll = 20 * Deg;
        double yaw = 0;
        void Hold(double sub)
        {
            yaw += rig.Body.AngularVelocity.Z * sub;
            rig.Body.AngularVelocity = new Vector3(0f, 0f, rig.Body.AngularVelocity.Z);
            rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Roll);
        }
        rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Roll);
        double target = -Roll * Math.PI;
        var w = new List<double>();
        double h = 1.0 / rate;
        int updates = 0;
        foreach (int second in new[] { 1, 2 })
        {
            for (; updates < (int)Math.Round(second * rate); updates++)
            {
                rig.Car.BeginUpdate((float)h);
                for (int i = 0; i < n; i++)
                {
                    rig.Car.StepOnce((float)(h / n), rig.Body);
                    Hold(h / n);
                }
            }
            w.Add(rig.Body.AngularVelocity.Z);
        }
        Near(w[0], target * (1 - Math.Exp(-0.5)), Math.Abs(target), "banking yaw rate", rate, n, 1);
        Near(w[1], target * (1 - Math.Exp(-1.0)), Math.Abs(target), "banking yaw rate", rate, n, 2);
    }

    // The sled's slope assist (k g sqrt(sin pitch) along the nose) with friction and gravity, held at 15 degrees nose down:
    // the speed along the nose against the InWorldz force's value at its 15 ms step, within 2%.
    [Theory]
    [MemberData(nameof(Grid))]
    public void The_sled_slides_down_a_fifteen_degree_slope_alike(double rate, int n)
    {
        const double G = 9.80665;
        double Slide(float assist)
        {
            var rig = new BehaviourRig(new VehicleSettings { SledAssist = assist }, Vehicle.TYPE_SLED);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(30f, 1000f, 1000f));
            rig.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(15 * Deg));
            double h = 1.0 / rate;
            for (int i = 0; i < (int)Math.Round(2 * rate); i++)
            {
                rig.Car.BeginUpdate((float)h);
                for (int k = 0; k < n; k++)
                {
                    rig.Car.StepOnce((float)(h / n), rig.Body);
                    rig.Body.Advance(h / n);
                    rig.Body.AngularVelocity = Vector3.Zero;   // held at its pitch
                }
            }
            return Vector3.Dot(rig.Body.LinearVelocity, Vector3.UnitX * rig.Body.Orientation);
        }
        double sin = Math.Sin(15 * Deg);
        double without = G * sin * 30 * (1 - Math.Exp(-2.0 / 30));
        double oldAt15ms = 3 * G * 0.015 * Math.Sqrt(sin);
        double assisted = oldAt15ms * 30 * (1 - Math.Exp(-2.0 / 30));
        double slideWithout = Slide(0f), slideWith = Slide(VehicleSettings.DefaultSledAssist);
        Near(slideWithout, without, without, "sled without the assist", rate, n, 2, 2.0);
        Near(slideWith - slideWithout, assisted, assisted, "the sled's slope assist", rate, n, 2, 2.0);
    }
}
