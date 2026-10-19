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
/// Linear deflection with and without VEHICLE_FLAG_NO_DEFLECTION_UP, on the controller alone: a body that keeps
/// the velocities the controller sets and moves nowhere, so deflection is the only thing changing the linear
/// velocity (friction off, motors released, hover off). Pure, parallel.
/// </summary>
public class VehicleDeflectionTests
{
    private static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    /// <summary>A vehicle body that only holds what the controller sets: no integration, no contacts.</summary>
    private sealed class HeldBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -9.80665f);
        public void SetGravityFactor(float factor) { }
        public bool HasCollision { get; set; }
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // A car (linear deflection efficiency 1, timescale 2 s), friction off, motors released.
    private static (VehicleController car, HeldBody body, Func<double, DateTime> tick) Car(Vector3 velocity, bool noDeflectionUp)
    {
        var body = new HeldBody { LinearVelocity = velocity };
        var car = new VehicleController(body);
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double now = 0;
        car.Clock = () => t0.AddTicks((long)(now * TimeSpan.TicksPerSecond));
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
        if (!noDeflectionUp)
            car.ProcessVehicleFlags((int)ExtendedVehicleFlags.NoDeflectionUp, remove: true);
        now = 2.0;   // the first step finds the motors stale and releases them
        return (car, body, dt => { now += dt; return car.Clock(); });
    }

    private static void Run(VehicleController car, Func<double, DateTime> tick, double rate, double seconds, Action afterStep = null)
    {
        int n = (int)Math.Round(seconds * rate);
        for (int i = 0; i < n; i++)
        {
            car.Step((float)(1.0 / rate));
            tick(1.0 / rate);
            afterStep?.Invoke();
        }
    }

    [Fact]
    public void In_the_air_deflection_never_adds_speed_at_any_rate()
    {
        // Falling nose-level, moving partly sideways: deflection turns the horizontal part toward the nose and
        // leaves the fall alone. (It used to drop the upward part of a whole-velocity turn, which kept "more forward".)
        Vector3 start = new(4f, 3f, -5f);
        foreach (double rate in Rates)
        {
            var (car, body, tick) = Car(start, noDeflectionUp: true);
            float startSpeed = start.Length();
            Run(car, tick, rate, 2.0, () =>
            {
                Assert.True(body.LinearVelocity.Length() <= startSpeed + 1e-4f, $"{rate} Hz: speed {body.LinearVelocity.Length()} above {startSpeed}");
                Assert.Equal(start.Z, body.LinearVelocity.Z, 5);
            });
            Vector3 v = body.LinearVelocity;
            Assert.Equal(5f, MathF.Sqrt(v.X * v.X + v.Y * v.Y), 3);   // horizontal speed kept
            Assert.True(v.Y < 2.0f, $"{rate} Hz: still heading sideways ({v})");     // turned toward the nose (+x)
        }
    }

    // The angle between a velocity and +x (the nose), and the speed.
    private static double AngleToNose(Vector3 v) => Math.Atan2(Math.Sqrt(v.Y * v.Y + v.Z * v.Z), v.X);

    [Fact]
    public void On_the_ground_with_the_flag_the_angle_to_the_nose_decays_exactly()
    {
        // Level ground, velocity horizontal: the angle to the nose decays as e^(-eff t / T) (the car: 1, 2 s), the
        // speed kept, at every rate.
        foreach (double rate in Rates)
        {
            Vector3 start = new(4f, 3f, 0f);
            var (car, body, tick) = Car(start, noDeflectionUp: true);
            double a0 = AngleToNose(start), t = 0;
            Run(car, tick, rate, 2.0, () =>
            {
                t += 1.0 / rate;
                double expected = a0 * Math.Exp(-t / 2.0);
                Assert.True(Math.Abs(AngleToNose(body.LinearVelocity) - expected) < 1e-4, $"{rate} Hz at {t:0.000} s: {AngleToNose(body.LinearVelocity)} against {expected}");
                Assert.Equal(5f, body.LinearVelocity.Length(), 3);
                Assert.Equal(0f, body.LinearVelocity.Z, 5);
            });
        }
    }

    [Fact]
    public void Without_the_flag_the_whole_velocity_turns_to_the_nose_exactly()
    {
        foreach (double rate in Rates)
        {
            Vector3 start = new(4f, 3f, -5f);
            var (car, body, tick) = Car(start, noDeflectionUp: false);
            double a0 = AngleToNose(start), t = 0;
            Run(car, tick, rate, 2.0, () =>
            {
                t += 1.0 / rate;
                double expected = a0 * Math.Exp(-t / 2.0);
                Assert.True(Math.Abs(AngleToNose(body.LinearVelocity) - expected) < 1e-4, $"{rate} Hz at {t:0.000} s: {AngleToNose(body.LinearVelocity)} against {expected}");
                Assert.Equal(start.Length(), body.LinearVelocity.Length(), 3);
            });
        }
    }
}
