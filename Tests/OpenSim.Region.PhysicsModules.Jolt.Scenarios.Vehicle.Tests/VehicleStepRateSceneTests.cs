/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The vehicle controller on a real backend, running inside the engine's collision steps (the step listener), gives the same
/// result at every physics rate and number of collision steps: 32, 45 and 64 Hz with 2 to 4 collision steps, and at 64 Hz
/// with 1 (the module keeps each solver step to 1/60 s or shorter, so 32 and 45 Hz cannot run with one; the controller-level
/// tests in VehicleStepRateTests do). Each case that has a closed-form answer is read against it at the time it is read, within
/// 2% of the scale of the answer; sliding in contact and the 15 degree sled are read against the same run at 64 Hz with 2
/// collision steps. The figures are written to the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleStepRateSceneTests
{
    private readonly ITestOutputHelper _out;
    public VehicleStepRateSceneTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const double Deg = Math.PI / 180.0;

    public static IEnumerable<object[]> Cells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = rate >= 60 ? 1 : 2; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private sealed class Rig : IDisposable
    {
        public readonly JoltScene Scene;
        public JoltPrim Car;
        private readonly long _steps0;

        public Rig(double rate, int collisionSteps, Action<IConfig> more = null)
        {
            var config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("physics", "Jolt");
            startup.Set("meshing", "Meshmerizer");
            IConfig jolt = config.AddConfig("Jolt");
            jolt.Set("PhysicsStepRate", rate.ToString(CultureInfo.InvariantCulture));
            jolt.Set("PhysicsStepCollisionSteps", collisionSteps.ToString(CultureInfo.InvariantCulture));
            MaterialPins.Apply(jolt);
            jolt.Set("VehicleBehaviour", "SecondLife");   // closed-form answers of the documented laws; the default is InWorldz
            more?.Invoke(jolt);
            Scene = new JoltScene();
            Scene.Initialise(config);
            var heights = new float[Size * Size];
            Array.Fill(heights, Ground);
            Scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 0f, Heartbeat);
            Scene.Simulate(Heartbeat);
            _steps0 = Scene.Substeps.Steps;
        }

        /// <summary>Physics time since the rig was built.</summary>
        public double Time => (Scene.Substeps.Steps - _steps0) * (double)Scene.Substeps.StepSeconds;

        public int CollisionSteps => Scene.CollisionSteps;

        /// <summary>A vehicle with only what a test sets acting (no attractor, banking, deflection, hover or friction; no
        /// motor-up limit), weightless unless <paramref name="weight"/>.</summary>
        public JoltPrim AddVehicle(Vector3 position, Vector3 size, Quaternion rotation, bool weight = false)
        {
            Car = Assert.IsType<JoltPrim>(Scene.AddPrimShape("car", PrimitiveBaseShape.CreateBox(), position, size, rotation, true, 900u));
            Car.Density = 1000f;
            Car.VehicleType = (int)Vehicle.TYPE_CAR;
            Float(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
            Float(Vehicle.BANKING_EFFICIENCY, 0f);
            Float(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1000f);
            Float(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 1000f);
            Float(Vehicle.HOVER_TIMESCALE, 1000f);
            Float(Vehicle.BUOYANCY, weight ? 0f : 1f);
            Vector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f));
            Vector(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f));
            Car.VehicleFlags((int)ExtendedVehicleFlags.LimitMotorUp, true);
            return Car;
        }

        public void Float(Vehicle code, float v) => Car.VehicleFloatParam((int)code, v);
        public void Vector(Vehicle code, Vector3 v) => Car.VehicleVectorParam((int)code, v);

        /// <summary>Steps heartbeats until physics time is at least <paramref name="seconds"/>; false if the time overshoots by
        /// more than two heartbeats.</summary>
        public void RunTo(double seconds)
        {
            while (Time < seconds - 1e-9)
                Scene.Simulate(Heartbeat);
            // A provider that throws is counted and swallowed by the engine's listener; none may have.
            var backend = (OpenSim.Region.PhysicsModules.Jolt.Backend.JoltPhysicsBackend)typeof(JoltScene)
                .GetField("_backend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(Scene);
            Assert.True(backend.StepForceProviderFaults == 0, $"the vehicle's step provider threw: {backend.LastStepForceProviderFault}");
        }

        public void Dispose() => Scene.Dispose();
    }

    private double Check(string test, double rate, int n, double t, double got, double expected, double scale, double percent = 2.0)
    {
        double error = Math.Abs(got - expected) / scale * 100.0;
        Say($"[rate-table] {test} | {rate} Hz x {n} | t {t:0.000} | {got:0.0000} against {expected:0.0000} | {error:0.00}% of {scale}");
        Assert.True(error <= percent, $"{test} at {rate} Hz with {n} collision steps, t = {t:0.000}: {got:0.0000} against {expected:0.0000} (within {percent}% of {scale})");
        return error;
    }

    private static double Ramp(double target, double tm, double td, double t) => target * (1.0 - Math.Exp(-td * (1.0 - Math.Exp(-t / td)) / tm));

    private static double Spring(double e0, double t, double timescale, double efficiency)
    {
        double w0 = 1.0 / timescale, z = efficiency;
        double wd = w0 * Math.Sqrt(1 - z * z);
        return e0 * Math.Exp(-z * w0 * t) * (Math.Cos(wd * t) + z * w0 / wd * Math.Sin(wd * t));
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void A_linear_motor_ramps_to_speed_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 100f), new Vector3(2f, 1f, 0.5f), Quaternion.Identity);
        Assert.Equal(n, rig.CollisionSteps);
        rig.Float(Vehicle.LINEAR_MOTOR_TIMESCALE, 2f);
        rig.Float(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Vector(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            Check("linear motor", rate, n, rig.Time, rig.Car.Velocity.X, Ramp(8, 2, 120, rig.Time), 8);
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void An_angular_motor_ramps_to_speed_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 100f), new Vector3(1f, 1f, 1f), Quaternion.Identity);
        rig.Float(Vehicle.ANGULAR_MOTOR_TIMESCALE, 2f);
        rig.Float(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Vector(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 1f));
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            Check("angular motor", rate, n, rig.Time, rig.Car.RotationalVelocity.Z, Ramp(1, 2, 120, rig.Time), 1);
        }
    }

    // Timescale friction alone: a vehicle in the air (nothing touches it, and its engine damping is 0).
    [Theory]
    [MemberData(nameof(Cells))]
    public void Timescale_friction_slows_a_vehicle_in_the_air_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 100f), new Vector3(1f, 1f, 1f), Quaternion.Identity);
        rig.Vector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(2f, 1000f, 1000f));
        rig.Vector(Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 2f));
        rig.Car.Velocity = new Vector3(10f, 0f, 0f);
        rig.Car.RotationalVelocity = new Vector3(0f, 0f, 3f);
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            // The vehicle turns at the same time, but its nose turns with it, so the velocity along the nose decays as e^(-t / 2)
            // only while the turn is small; read the angular rate, which is exact, and the speed, which does not depend on the turn.
            Check("angular friction", rate, n, rig.Time, rig.Car.RotationalVelocity.Z, 3 * Math.Exp(-rig.Time / 2), 3);
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Linear_timescale_friction_slows_a_vehicle_in_the_air_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 100f), new Vector3(1f, 1f, 1f), Quaternion.Identity);
        rig.Vector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(2f, 1000f, 1000f));
        rig.Car.Velocity = new Vector3(10f, 0f, 0f);
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            Check("linear friction", rate, n, rig.Time, rig.Car.Velocity.X, 10 * Math.Exp(-rig.Time / 2), 10);
        }
    }

    // A vehicle sliding on the ground: the engine's contact friction (the material's, while it touches the terrain) and the
    // vehicle's own timescale friction, which acts along its axes whether or not it touches anything. Each is read against the same
    // run at 64 Hz with two collision steps, at the same physics time.
    private static (double t, double v)[] Slide(double rate, int n, bool contact, bool timescale, double seconds)
    {
        using var rig = new Rig(rate, n, jolt => jolt.Set("VehicleContactFriction", contact ? "0.6" : "0"));
        rig.AddVehicle(new Vector3(60f, 128f, Ground + 0.26f), new Vector3(2f, 1f, 0.5f), Quaternion.Identity, weight: true);
        rig.Float(Vehicle.BUOYANCY, 0f);
        if (timescale)
            rig.Vector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(4f, 1000f, 1000f));
        rig.Car.Velocity = new Vector3(20f, 0f, 0f);
        var series = new List<(double, double)> { (0, 20) };
        while (rig.Time < seconds)
        {
            rig.Scene.Simulate(Heartbeat);
            series.Add((rig.Time, rig.Car.Velocity.X));
        }
        return series.ToArray();
    }

    private static double At((double t, double v)[] series, double t)
    {
        for (int i = 1; i < series.Length; i++)
            if (series[i].t >= t - 1e-9)
                return series[i - 1].v + (series[i].v - series[i - 1].v) * (t - series[i - 1].t) / (series[i].t - series[i - 1].t);
        return series[^1].v;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Contact_friction_and_timescale_friction_slow_a_sliding_vehicle_alike(double rate, int n)
    {
        foreach ((string name, bool contact, bool timescale) in new[] { ("contact friction only", true, false), ("timescale friction only", false, true), ("contact and timescale friction", true, true) })
        {
            var reference = Slide(64.0, 2, contact, timescale, 2.5);
            var run = Slide(rate, n, contact, timescale, 2.5);
            foreach (double t in new[] { 0.5, 1.0, 1.5, 2.0 })
                Check("sliding, " + name, rate, n, t, At(run, t), At(reference, t), 20, 2.0);
        }
        // The two parts are separate: contact friction slows the vehicle on the ground, the timescale friction slows it more.
        var contactOnly = Slide(rate, n, true, false, 1.5);
        var both = Slide(rate, n, true, true, 1.5);
        var timescaleOnly = Slide(rate, n, false, true, 1.5);
        Assert.True(At(contactOnly, 1.0) < 19.0, $"contact friction does not slow the vehicle: {At(contactOnly, 1.0)}");
        Assert.True(At(timescaleOnly, 1.0) < 19.0 && At(timescaleOnly, 1.0) > At(both, 1.0) + 0.5, $"timescale only {At(timescaleOnly, 1.0)}, both {At(both, 1.0)}");
        Assert.True(At(both, 1.0) < At(contactOnly, 1.0) - 0.5, $"both {At(both, 1.0)}, contact only {At(contactOnly, 1.0)}");
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Linear_deflection_turns_the_velocity_toward_the_nose_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 100f), new Vector3(2f, 1f, 0.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(40 * Deg)));
        rig.Float(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 1f);
        rig.Float(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 2f);
        rig.Car.Velocity = new Vector3(10f, 0f, 0f);
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            Vector3 nose = Vector3.UnitX * rig.Car.Orientation;
            Vector3 carVelocity = rig.Car.Velocity;
            Vector3 v = Vector3.Normalize(in carVelocity);
            double angle = Math.Acos(Math.Clamp(Vector3.Dot(nose, v), -1f, 1f)) / Deg;
            Check("deflection angle (degrees)", rate, n, rig.Time, angle, 40 * Math.Exp(-rig.Time / 2), 40);
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void The_vertical_attractor_rights_a_tilted_vehicle_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 100f), new Vector3(1f, 1f, 1f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(30 * Deg)));
        rig.Float(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 2f);
        rig.Float(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0.5f);
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            Quaternion q = rig.Car.Orientation;
            Vector3 y = Vector3.UnitY * q, z = Vector3.UnitZ * q;
            double roll = Math.Atan2(y.Z, z.Z) / Deg;
            Check("roll (degrees)", rate, n, rig.Time, roll, Spring(30, rig.Time, 2, 0.5), 30);
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Hover_settles_a_vehicle_at_its_height_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 55f), new Vector3(1f, 1f, 1f), Quaternion.Identity);
        rig.Float(Vehicle.HOVER_HEIGHT, 60f);
        rig.Float(Vehicle.HOVER_TIMESCALE, 2f);
        rig.Float(Vehicle.HOVER_EFFICIENCY, 0.5f);
        rig.Car.VehicleFlags((int)ExtendedVehicleFlags.HoverGlobalHeight, false);
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            Check("hover height", rate, n, rig.Time, rig.Car.Position.Z, 60 + Spring(-5, rig.Time, 2, 0.5), 5);
        }
    }

    // Held at a 20 degree roll by the attractor's very long timescale, a cube (no gyroscopic coupling) turns about the vertical
    // at the banking rate: w(t) = -roll x efficiency x pi (1 - e^(-t / T)).
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_banking_turn_comes_to_its_rate_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n);
        rig.AddVehicle(new Vector3(100f, 128f, 100f), new Vector3(1f, 1f, 1f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(20 * Deg)));
        rig.Float(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 400f);
        rig.Float(Vehicle.BANKING_EFFICIENCY, 1f);
        rig.Float(Vehicle.BANKING_MIX, 0f);
        rig.Float(Vehicle.BANKING_TIMESCALE, 2f);
        double target = -20 * Deg * Math.PI;
        foreach (int second in new[] { 1, 2 })
        {
            rig.RunTo(second);
            Check("banking yaw rate", rate, n, rig.Time, rig.Car.RotationalVelocity.Z, target * (1 - Math.Exp(-rig.Time / 2)), Math.Abs(target));
        }
    }

    // The documented sled let go at rest on the harness's 15 degree slope (no landing to toss a coin): its speed at 1, 2, 3 and
    // 4 s against the same run at 64 Hz with 2 collision steps. The sled's slope assist, friction and the contact are in it.
    private static readonly System.Lazy<double[]> SledReference = new(() => SledSpeeds(64.0, 2));

    private static double[] SledSpeeds(double rate, int n)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = rate, SlopeDeg = 15f, Duration = 5f }.SecondLife();
        o.Jolt["PhysicsStepCollisionSteps"] = n.ToString(CultureInfo.InvariantCulture);
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("sled"), o);
        var samples = r.Samples;
        double SpeedAt(double t)
        {
            for (int i = 1; i < samples.Count; i++)
                if (samples[i].T >= t - 1e-9)
                    return samples[i - 1].Speed + (samples[i].Speed - samples[i - 1].Speed) * (t - samples[i - 1].T) / (samples[i].T - samples[i - 1].T);
            return samples[^1].Speed;
        }
        return new[] { SpeedAt(1), SpeedAt(2), SpeedAt(3), SpeedAt(4) };
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void The_sled_slides_down_a_fifteen_degree_slope_alike(double rate, int n)
    {
        double[] run = SledSpeeds(rate, n);
        double[] reference = SledReference.Value;
        for (int i = 0; i < 4; i++)
            Check("sled on 15 degrees", rate, n, i + 1, run[i], reference[i], reference[i], 2.0);
    }
}
