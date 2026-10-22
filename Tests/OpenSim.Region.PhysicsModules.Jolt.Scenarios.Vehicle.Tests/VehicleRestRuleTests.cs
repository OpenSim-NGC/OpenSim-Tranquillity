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
/// The rest rule ([Jolt] VehicleRestSpeed, VehicleController.HoldsAtRest): a vehicle with no motor pulling, moving
/// slower than the rest speed, whose motor-and-friction equation gives a steady speed slower than that from its
/// present pose, is held still so it can sleep. Pure checks of the rule on a body set by hand, then the harness at 11
/// and 45 Hz: parked cars sleep and stay put, while a sled on a slope starts and cars roll and coast as before.
/// </summary>
public class VehicleRestRuleTests
{
    private sealed class Body : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 25f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => new(0f, 0f, -9.80665f);
        public void SetGravityFactor(float factor) { }
        public bool HasCollision { get; set; } = true;
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    private const float G = 9.80665f;

    // A car (documented preset, or the test car's friction <1,1,1000>) or a sled, resting on what it touches.
    private static (VehicleController v, Body body) Make(Vehicle type, bool testCar = false, float restSpeed = VehicleSettings.DefaultRestSpeed)
    {
        var body = new Body();
        var v = new VehicleController(body) { Settings = new VehicleSettings { RestSpeed = restSpeed } };
        v.ProcessTypeChange(type);
        if (testCar)
            v.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
        return (v, body);
    }

    private static Quaternion Roll(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg * MathF.PI / 180f);
    private static Quaternion NoseDown(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg * MathF.PI / 180f);

    [Fact]
    public void A_car_at_rest_on_level_ground_is_held()
    {
        var (car, _) = Make(Vehicle.TYPE_CAR);
        Assert.True(car.HoldsAtRest);
        var (test, _) = Make(Vehicle.TYPE_CAR, testCar: true);
        Assert.True(test.HoldsAtRest);
    }

    // Rolled 0.3 degrees (what a car resting inside the engine's 2 cm contact allowance can be): the test car's steady
    // sideways speed is g sin(0.3) * 1 s = 0.051 m/s, under the rest speed.
    [Fact]
    public void The_test_car_rolled_a_fraction_of_a_degree_is_held()
    {
        var (car, body) = Make(Vehicle.TYPE_CAR, testCar: true);
        body.Orientation = Roll(0.3f);
        Assert.InRange(car.SteadySpeed().Length(), 0.050f, 0.052f);
        Assert.True(car.HoldsAtRest);
    }

    // On 5 degrees the test car's steady roll is g sin(5) * 1 s = 0.855 m/s: it must roll.
    [Theory]
    [InlineData(5f)]
    [InlineData(15f)]
    public void A_car_on_a_slope_is_not_held(float deg)
    {
        var (car, body) = Make(Vehicle.TYPE_CAR, testCar: true);
        body.Orientation = NoseDown(deg);
        Assert.InRange(car.SteadySpeed().X, G * MathF.Sin(deg * MathF.PI / 180f) * 0.999f, G * MathF.Sin(deg * MathF.PI / 180f) * 1.001f);
        Assert.False(car.HoldsAtRest);
    }

    [Fact]
    public void A_sled_on_a_slope_is_not_held_and_on_level_ground_is()
    {
        var (sled, body) = Make(Vehicle.TYPE_SLED);
        Assert.True(sled.HoldsAtRest);
        body.Orientation = NoseDown(15f);
        Assert.False(sled.HoldsAtRest);
    }

    // The documented car's forward friction is 100 s: with no motor grip, even 0.3 degrees of pitch gives it a steady
    // roll of g sin(0.3) * 100 s = 5 m/s, so it is not held. With its motor set to zero (a key let go), the motor's
    // grip brakes it with its 1 s timescale and the steady speed is about g sin(0.3) * 1 s: held.
    [Fact]
    public void The_motor_set_to_zero_brakes_in_the_equation()
    {
        var (car, body) = Make(Vehicle.TYPE_CAR);
        body.Orientation = NoseDown(0.3f);
        Assert.False(car.HoldsAtRest);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, Vector3.Zero);
        Assert.True(car.HoldsAtRest);
    }

    [Fact]
    public void A_moving_pulled_or_falling_vehicle_is_not_held()
    {
        var (moving, mb) = Make(Vehicle.TYPE_CAR);
        mb.LinearVelocity = new Vector3(0.2f, 0f, 0f);
        Assert.False(moving.HoldsAtRest);
        mb.LinearVelocity = Vector3.Zero;
        mb.AngularVelocity = new Vector3(0f, 0f, 0.2f);
        Assert.False(moving.HoldsAtRest);

        var (pulled, _) = Make(Vehicle.TYPE_CAR);
        pulled.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        Assert.False(pulled.HoldsAtRest);

        var (falling, fb) = Make(Vehicle.TYPE_CAR);
        fb.HasCollision = false;
        Assert.False(falling.HoldsAtRest);

        // Floating (buoyancy 1, so no gravity to hold) and turning slowly through upright: not resting on anything.
        var (floating, flb) = Make(Vehicle.TYPE_BALLOON);
        floating.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 1f);
        flb.HasCollision = false;
        flb.AngularVelocity = new Vector3(0.05f, 0f, 0f);
        Assert.False(floating.HoldsAtRest);
    }

    [Fact]
    public void A_rest_speed_of_zero_turns_the_rule_off()
    {
        var (car, _) = Make(Vehicle.TYPE_CAR, restSpeed: 0f);
        Assert.False(car.HoldsAtRest);
    }

    [Fact]
    public void The_steady_speed_on_one_axis_is_the_equations()
    {
        // No motor: a Tf.
        Assert.Equal(2.0, VehicleController.SteadyOnAxis(1.0, 0, 1, 60, double.PositiveInfinity, 2.0), 9);
        // Full grip: (M / Tm + a) / (1 / Tm + 1 / Tf).
        Assert.Equal((8.0 / 1 + 0.5) / (1.0 / 1 + 1.0 / 100), VehicleController.SteadyOnAxis(0.5, 8, 1, 60, 0, 100), 9);
        // Neither grip nor friction: no steady speed under any push, none with no push.
        Assert.True(double.IsPositiveInfinity(VehicleController.SteadyOnAxis(0.1, 0, 1, 60, double.PositiveInfinity, 1000)));
        Assert.Equal(0.0, VehicleController.SteadyOnAxis(0.0, 0, 1, 60, double.PositiveInfinity, 1000));
    }
}

/// <summary>
/// The rest rule in the harness at 11 and 45 Hz. Serial with the other native tests: every run steps a real backend on
/// the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class VehicleRestRuleHarnessTests
{
    private static RunResult Run(string scenario, double rate, float? slope = null, bool rule = true)
    {
        var o = new HarnessOptions { RateHz = rate }.SecondLife();
        if (slope.HasValue) o.SlopeDeg = slope.Value;
        if (!rule) o.Jolt["VehicleRestSpeed"] = "0";
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    // Driven 3 s on level ground and let go: at rest (under 0.1 m/s for 1 s) and asleep within 10 s of the key, then
    // it moves less than 5 cm to the end of the run and stays asleep.
    [Theory]
    [InlineData("park-car", 11.0)]
    [InlineData("park-car", 45.0)]
    [InlineData("park-drive", 11.0)]
    [InlineData("park-drive", 45.0)]
    public void A_car_parked_on_level_ground_sleeps_within_ten_seconds_and_stays_put(string scenario, double rate)
    {
        RunResult r = Run(scenario, rate);
        Summary m = r.Summary;
        Assert.Equal(0, m.NonFinite);
        Assert.False(double.IsNaN(m.TimeToRest), $"{r.Name}: never at rest");
        Assert.False(double.IsNaN(m.SleepAfter), $"{r.Name}: still awake at the end");
        Assert.InRange(m.SleepAfter, 0.0, 10.0);
        double restAt = m.ReleaseT + m.TimeToRest;
        Sample atRest = r.Samples.First(s => s.T >= restAt - 1e-9);
        float moved = (r.Samples[^1].Position - atRest.Position).Length();
        Assert.True(moved < 0.05f, $"{r.Name}: moved {moved:0.0000} m after coming to rest");
        Assert.All(r.Samples.Where(s => s.T >= r.Samples[^1].T - 5.0), s => Assert.Equal(0, s.Active));
    }

    // What the rule must leave alone is compared with the same run with the rule off ([Jolt] VehicleRestSpeed 0), on
    // the same platform, so the figures do not depend on the platform's last digits.

    // A documented sled let go at rest on 15 degrees starts and reaches the steady speed it reaches without the rule
    // (12.65 m/s here), within 1%.
    [Theory]
    [InlineData(11.0)]
    [InlineData(45.0)]
    public void A_sled_let_go_on_a_slope_starts_and_reaches_its_steady_speed(double rate)
    {
        RunResult r = Run("sled", rate, 15f), off = Run("sled", rate, 15f, rule: false);
        Sample at2 = r.Samples.First(s => s.T >= 2.0);
        Assert.True((at2.Position - r.Samples[0].Position).Length() > 1f, "the sled started");
        Assert.InRange(r.Summary.SteadySpeed, off.Summary.SteadySpeed * 0.99f, off.Summary.SteadySpeed * 1.01f);
        Assert.True(r.Summary.SteadySpeed > 10f, $"steady {r.Summary.SteadySpeed:0.000}");
    }

    // The test car let go on 5 and 15 degrees rolls at the speed it rolls at without the rule (about the equation's
    // g sin(angle) * Tf with its 1 s forward friction: 0.855 / 2.538 m/s), within 1%.
    [Theory]
    [InlineData(11.0, 5f)]
    [InlineData(45.0, 5f)]
    [InlineData(11.0, 15f)]
    [InlineData(45.0, 15f)]
    public void The_test_car_let_go_on_a_slope_rolls(double rate, float slope)
    {
        RunResult r = Run("testcar", rate, slope), off = Run("testcar", rate, slope, rule: false);
        Assert.InRange(r.Summary.SteadySpeed, off.Summary.SteadySpeed * 0.99f, off.Summary.SteadySpeed * 1.01f);
        float equation = 9.80665f * MathF.Sin(slope * MathF.PI / 180f);
        Assert.InRange(r.Summary.SteadySpeed, equation * 0.95f, equation * 1.05f);
    }

    // After its key a car coasts to rest (under 0.1 m/s for 1 s) in the time it takes without the rule, within 3%:
    // the rule only acts below its 0.1 m/s.
    [Theory]
    [InlineData("car", 11.0)]
    [InlineData("car", 45.0)]
    [InlineData("testcar", 11.0)]
    [InlineData("testcar", 45.0)]
    public void A_car_coasting_after_its_key_comes_to_rest_in_the_time_it_takes_without_the_rule(string scenario, double rate)
    {
        RunResult r = Run(scenario, rate, 0f), off = Run(scenario, rate, 0f, rule: false);
        Assert.False(double.IsNaN(off.Summary.TimeToRest));
        Assert.InRange(r.Summary.TimeToRest, off.Summary.TimeToRest * 0.97, off.Summary.TimeToRest * 1.03);
    }
}
