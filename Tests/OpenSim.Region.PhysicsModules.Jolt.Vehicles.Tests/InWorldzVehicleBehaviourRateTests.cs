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
/// The vehicle behaviours ported from the InWorldz Halcyon code, each under
/// <see cref="VehicleBehaviour.InWorldz"/> with Second Life's behaviour checked unchanged beside it: a boat's ground drag
/// (SimulateBoatGroundDrag), the motor offset on every linear force (VehicleMotor.AddForce), NO_DEFLECTION_UP zeroing only
/// an upward change (SimulateLinearDeflection), banking by the sine of the roll and without the attractor
/// (SimulateBankingToYaw), the gravity multiplier honoured by a vehicle (FloatParamChanged, PhysxPrim.WantsGravity), and a
/// vehicle's running motors carried over a region border. Each is run at 32, 45 and 64 Hz Updates with 1 to 4 collision
/// steps. Pure (a body with no engine), so the class runs in parallel.
/// </summary>
public class InWorldzVehicleBehaviourRateTests
{
    private readonly ITestOutputHelper _out;
    public InWorldzVehicleBehaviourRateTests(ITestOutputHelper output) { _out = output; }

    private static VehicleSettings InWorldz => new() { Behaviour = VehicleBehaviour.InWorldz };
    private static VehicleSettings SecondLife => new() { Behaviour = VehicleBehaviour.SecondLife };

    public static IEnumerable<object[]> Grid()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = 1; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private static void RunFor(BehaviourRig rig, double rate, int n, double seconds)
    {
        int updates = (int)Math.Round(seconds * rate);
        for (int i = 0; i < updates; i++)
            rig.StepUpdate(1.0 / rate, n);
    }

    private void Near(double got, double expected, double scale, string what, double rate, int n, double percent = 1.5)
    {
        double error = Math.Abs(got - expected) / scale * 100.0;
        _out.WriteLine($"[inworldz-rate] {what} | {rate} Hz x {n} | {got:0.00000} against {expected:0.00000} | {error:0.000}% of {scale}");
        Assert.True(error <= percent, $"{what} at {rate} Hz with {n} collision steps: {got:0.00000} against {expected:0.00000} (within {percent}% of {scale})");
    }

    // ---- A boat's ground drag (VehicleDynamics.cs:1037-1073) ------------------------------------------------

    // A boat with a 4 x 2 x 1 hull moving along its nose at 1 m/s with a linear motor set to 8 m/s, for a second. The terrain
    // is at 0 under the body; the hull's leading bottom edge is at z - 0.49.
    private static double BoatSpeed(VehicleSettings settings, Vehicle type, float z, double rate, int n,
        ExtendedVehicleFlags water = ExtendedVehicleFlags.HoverWaterOnly, bool terrainOnly = false)
    {
        var rig = new BehaviourRig(settings, type, gravity: false);
        rig.Body.BoundsHalfSize = new Vector3(2f, 1f, 0.5f);
        rig.Body.Position = new Vector3(128f, 128f, z);
        rig.Body.LinearVelocity = new Vector3(1f, 0f, 0f);
        rig.Flag(ExtendedVehicleFlags.HoverWaterOnly, false);
        rig.Flag(ExtendedVehicleFlags.HoverTerrainOnly, false);
        rig.Flag(water, true);
        rig.Flag(ExtendedVehicleFlags.HoverTerrainOnly, terrainOnly);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
        RunFor(rig, rate, n, 1.0);
        return rig.Body.LinearVelocity.X;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_boat_with_its_bow_on_the_ground_loses_its_motor_under_InWorldz(double rate, int n)
    {
        double aground = BoatSpeed(InWorldz, Vehicle.TYPE_BOAT, 0.4f, rate, n);
        double afloat = BoatSpeed(InWorldz, Vehicle.TYPE_BOAT, 5f, rate, n);
        Near(aground, 1.0, 1.0, "speed of a boat aground", rate, n);
        Assert.True(afloat > 3.0, $"a boat afloat gains speed ({afloat:0.00})");
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Second_Life_has_no_boat_ground_drag(double rate, int n)
    {
        Assert.True(BoatSpeed(SecondLife, Vehicle.TYPE_BOAT, 0.4f, rate, n) > 3.0);
    }

    [Fact]
    public void The_drag_needs_a_boat_with_water_only_hover_and_not_terrain_only()
    {
        // Each case against the same vehicle afloat: aground it must go as fast.
        Assert.Equal(BoatSpeed(InWorldz, Vehicle.TYPE_CAR, 5f, 45, 2), BoatSpeed(InWorldz, Vehicle.TYPE_CAR, 0.4f, 45, 2), 4);
        Assert.Equal(BoatSpeed(InWorldz, Vehicle.TYPE_BOAT, 5f, 45, 2, water: ExtendedVehicleFlags.HoverGlobalHeight),
            BoatSpeed(InWorldz, Vehicle.TYPE_BOAT, 0.4f, 45, 2, water: ExtendedVehicleFlags.HoverGlobalHeight), 4);
        Assert.Equal(BoatSpeed(InWorldz, Vehicle.TYPE_BOAT, 5f, 45, 2, terrainOnly: true),
            BoatSpeed(InWorldz, Vehicle.TYPE_BOAT, 0.4f, 45, 2, terrainOnly: true), 4);
        Assert.Equal(1.0, BoatSpeed(InWorldz, Vehicle.TYPE_SAILBOAT, 0.4f, 45, 2), 2);
    }

    [Fact]
    public void Only_the_edge_that_leads_the_motion_counts()
    {
        // Terrain under the back of the hull only (x below 128): moving forward is clear, moving back is aground.
        float Ground(float x, float y) => x < 128f ? 10f : 0f;
        var at = new Vector3(128f, 128f, 1.49f);
        var half = new Vector3(2f, 1f, 0.5f);
        Assert.False(HalcyonVehicleLaws.BoatLeadingEdgeOnGround(new Vector3(1f, 0f, 0f), at, Quaternion.Identity, Vector3.Zero, half, Ground));
        Assert.True(HalcyonVehicleLaws.BoatLeadingEdgeOnGround(new Vector3(-1f, 0f, 0f), at, Quaternion.Identity, Vector3.Zero, half, Ground));
        Assert.False(HalcyonVehicleLaws.BoatLeadingEdgeOnGround(new Vector3(0.004f, 0f, 0f), at, Quaternion.Identity, Vector3.Zero, half, (x, y) => 99f));
    }

    // ---- NO_DEFLECTION_UP zeroes only an upward change (VehicleDynamics.cs:1198-1202) -----------------------

    private static BehaviourRig DeflectingCar(VehicleSettings settings, Vector3 velocity, bool noUp)
    {
        var rig = new BehaviourRig(settings, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 1f);
        rig.Flag(ExtendedVehicleFlags.NoDeflectionUp, noUp);
        rig.Body.LinearVelocity = velocity;
        return rig;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_vehicle_sinking_does_not_have_its_sinking_deflected_up_under_InWorldz(double rate, int n)
    {
        var rig = DeflectingCar(InWorldz, new Vector3(10f, 0f, -5f), noUp: true);
        RunFor(rig, rate, n, 1.0);
        Near(rig.Body.LinearVelocity.Z, -5.0, 5.0, "vz of a sinking vehicle", rate, n, 0.5);
        Assert.True(rig.Body.LinearVelocity.X > 10.5, $"its forward speed grows ({rig.Body.LinearVelocity.X:0.00})");
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_vehicle_rising_is_still_deflected_down_under_InWorldz(double rate, int n)
    {
        var rig = DeflectingCar(InWorldz, new Vector3(10f, 0f, 5f), noUp: true);
        RunFor(rig, rate, n, 1.0);
        // The angle to the nose, 26.57 degrees, decays as e^-1 over a second at full efficiency; the speed is kept.
        double speed = Math.Sqrt(125.0), angle = Math.Atan2(5, 10) * Math.Exp(-1.0);
        Near(rig.Body.LinearVelocity.Z, speed * Math.Sin(angle), 5.0, "vz of a rising vehicle", rate, n, 1.5);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Second_Life_turns_only_the_horizontal_part_and_a_vehicle_without_the_flag_turns_all_of_it(double rate, int n)
    {
        var sl = DeflectingCar(SecondLife, new Vector3(10f, 0f, 5f), noUp: true);
        RunFor(sl, rate, n, 1.0);
        Near(sl.Body.LinearVelocity.Z, 5.0, 5.0, "vz under Second Life", rate, n, 0.5);
        var free = DeflectingCar(InWorldz, new Vector3(10f, 0f, -5f), noUp: false);
        RunFor(free, rate, n, 1.0);
        double speed = Math.Sqrt(125.0), angle = -Math.Atan2(5, 10) * Math.Exp(-1.0);
        Near(free.Body.LinearVelocity.Z, speed * Math.Sin(angle), 5.0, "vz without the flag", rate, n, 1.5);
    }

    // ---- The motor offset acts on every linear force (VehicleMotor.AddForce) --------------------------------

    // A car in the air at 10 m/s along its nose, a 20 s friction along it, its offset 0.1 m below the centre of mass. The
    // friction's slowing is a force at the offset, which pitches the vehicle: m dv r / I about y (1000 kg, I 400).
    [Theory]
    [MemberData(nameof(Grid))]
    public void Friction_acts_through_the_motor_offset_under_InWorldz(double rate, int n)
    {
        BehaviourRig Make(VehicleSettings s)
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(20f, 1000f, 1000f));
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_OFFSET, new Vector3(0f, 0f, -0.1f));
            rig.Body.LinearVelocity = new Vector3(10f, 0f, 0f);
            return rig;
        }
        var iw = Make(InWorldz);
        RunFor(iw, rate, n, 1.0);
        double dv = 10.0 * (1.0 - Math.Exp(-1.0 / 20.0));
        Near(iw.Body.AngularVelocity.Y, 1000.0 * dv * 0.1 / 400.0, 0.1219, "pitch rate from friction at the offset", rate, n, 3.0);
        var sl = Make(SecondLife);
        RunFor(sl, rate, n, 1.0);
        Assert.True(Math.Abs(sl.Body.AngularVelocity.Y) < 1e-5, $"Second Life's offset belongs to the motor only ({sl.Body.AngularVelocity.Y})");
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Deflection_acts_through_the_motor_offset_under_InWorldz(double rate, int n)
    {
        BehaviourRig Make(VehicleSettings s)
        {
            var rig = DeflectingCar(s, new Vector3(10f, 0f, 5f), noUp: false);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_OFFSET, new Vector3(0f, 0f, -0.1f));
            return rig;
        }
        var iw = Make(InWorldz);
        RunFor(iw, rate, n, 0.5);
        Assert.True(iw.Body.AngularVelocity.Y < -0.1, $"the deflection's push at the offset pitches the vehicle ({iw.Body.AngularVelocity.Y})");
        var sl = Make(SecondLife);
        RunFor(sl, rate, n, 0.5);
        Assert.True(Math.Abs(sl.Body.AngularVelocity.Y) < 1e-5);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_motor_still_acts_through_its_offset_under_both(double rate, int n)
    {
        foreach (VehicleSettings s in new[] { InWorldz, SecondLife })
        {
            var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_OFFSET, new Vector3(0f, 0f, -0.1f));
            rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
            rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
            RunFor(rig, rate, n, 0.5);
            Assert.True(rig.Body.AngularVelocity.Y < -0.05, $"{s.Behaviour}: {rig.Body.AngularVelocity.Y}");
        }
    }

    // ---- Banking by the sine of the roll, without the attractor (VehicleDynamics.cs:1338-1398) ---------------

    private static BehaviourRig BankingCar(VehicleSettings s, float attractorTimescale)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, attractorTimescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, 0f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_AZIMUTH, (float)(Math.PI / 2.0));
        rig.Tilt(60, 0);
        return rig;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void InWorldz_banks_by_the_sine_of_the_roll_with_no_attractor(double rate, int n)
    {
        var rig = BankingCar(InWorldz, 1000f);
        RunFor(rig, rate, n, 1.0);
        double target = -Math.Sin(60.0 * Math.PI / 180.0) * Math.PI;
        // The turn is Halcyon's banking motor (a log-growth ramp toward the rate banking asks for, VehicleDynamics.cs:2949-3040),
        // one step of 15 ms for each 15 ms: 66 of them in a second.
        float yaw = 0f, ramp = 0f;
        for (int i = 0; i < 66; i++)
            yaw += HalcyonVehicleLaws.BankingTurnChange(yaw, (float)target, ref ramp, 1f / 0.015f, InWorldz.MaxAngularSpeed, false, false);
        Near(rig.Body.AngularVelocity.Z, yaw, Math.Abs(target), "yaw rate banked by the sine", rate, n);
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void Second_Life_banks_only_with_the_attractor_and_by_the_roll_angle(double rate, int n)
    {
        var off = BankingCar(SecondLife, 1000f);
        RunFor(off, rate, n, 1.0);
        Assert.True(Math.Abs(off.Body.AngularVelocity.Z) < 1e-6, "no attractor, no banking under Second Life");
        var on = BankingCar(SecondLife, 450f);
        RunFor(on, rate, n, 1.0);
        double target = -(60.0 * Math.PI / 180.0) * Math.PI;
        Near(on.Body.AngularVelocity.Z, target * (1.0 - Math.Exp(-1.0)), Math.Abs(target), "yaw rate banked by the angle", rate, n, 2.0);
    }

    // ---- A vehicle honours the gravity multiplier under InWorldz (PhysxPrim.cs:349, 1614; VehicleDynamics.cs:318) ----

    private static float GravityShareOf(VehicleSettings s, float multiplier, float buoyancy, double rate, int n)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR);
        rig.Body.GravityMultiplier = multiplier;
        rig.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, buoyancy);
        rig.StepUpdate(1.0 / rate, n);
        return rig.Body.GravityFactor;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_vehicle_weighs_its_gravity_multiplier_less_its_buoyancy_under_InWorldz(double rate, int n)
    {
        Assert.Equal(3f, GravityShareOf(InWorldz, 3f, 0f, rate, n), 4);
        Assert.Equal(2f, GravityShareOf(InWorldz, 3f, 1f, rate, n), 4);
        Assert.Equal(0f, GravityShareOf(InWorldz, 3f, 3f, rate, n), 4);       // up to the multiplier
        Assert.Equal(0f, GravityShareOf(InWorldz, 3f, 5f, rate, n), 4);       // held to the multiplier
        Assert.Equal(4f, GravityShareOf(InWorldz, 3f, -1f, rate, n), 4);
        Assert.Equal(1f, GravityShareOf(InWorldz, 1f, 0f, rate, n), 4);       // the default: unchanged
        Assert.Equal(2f, GravityShareOf(InWorldz, 1f, -2f, rate, n), 4);      // held to -1 at a multiplier of 1
        Assert.Equal(0f, GravityShareOf(InWorldz, 1f, 1f, rate, n), 4);
        Assert.Equal(0f, GravityShareOf(InWorldz, 1f, 0.98f, rate, n), 4);    // the 0.977 hack, after the clamp
        Assert.Equal(0f, GravityShareOf(InWorldz, 0.5f, 2f, rate, n), 4);     // buoyancy held to 0.5, the weight 0.5 - 0.5
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_vehicle_ignores_the_gravity_multiplier_under_Second_Life(double rate, int n)
    {
        Assert.Equal(1f, GravityShareOf(SecondLife, 3f, 0f, rate, n), 4);
        Assert.Equal(0f, GravityShareOf(SecondLife, 3f, 1f, rate, n), 4);
        Assert.Equal(2f, GravityShareOf(SecondLife, 3f, -1f, rate, n), 4);
    }

    [Fact]
    public void InWorldz_holds_a_vehicles_buoyancy_to_minus_2_to_30_and_Second_Life_to_minus_1_to_1()
    {
        var iw = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR);
        iw.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 50f);
        Assert.Equal(30f, iw.Car.GetFloatParam(VehFloatParam.Buoyancy));
        iw.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, -5f);
        Assert.Equal(-2f, iw.Car.GetFloatParam(VehFloatParam.Buoyancy));
        var sl = new BehaviourRig(SecondLife, Vehicle.TYPE_CAR);
        sl.Car.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 50f);
        Assert.Equal(1f, sl.Car.GetFloatParam(VehFloatParam.Buoyancy));
    }

    // ---- A vehicle's running motors go on across a region border -----------------------------------------------

    [Theory]
    [MemberData(nameof(Grid))]
    public void A_vehicle_that_crossed_goes_on_driving_under_both_behaviours(double rate, int n)
    {
        foreach (VehicleSettings s in new[] { InWorldz, SecondLife })
        {
            BehaviourRig Drive()
            {
                var r = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
                r.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 2f);
                r.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
                r.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(8f, 0f, 0f));
                return r;
            }
            var original = Drive();
            RunFor(original, rate, n, 1.0);
            original.Car.GetRunningMotors(out MotorRunningState? linear, out MotorRunningState? angular);
            Assert.NotNull(linear);
            Assert.Null(angular);

            // The neighbour region builds the vehicle from the record: its parameters and the motor directions (which leave the
            // motors off), then the running state. The body arrives with the velocity it left with.
            BehaviourRig Arrive(bool withRunning)
            {
                var r = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
                r.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 2f);
                r.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
                r.Car.RestoreMotorDirections(new Vector3(8f, 0f, 0f), Vector3.Zero);
                if (withRunning)
                    r.Car.RestoreRunningMotors(linear, angular);
                r.Body.LinearVelocity = original.Body.LinearVelocity;
                return r;
            }
            var carried = Arrive(true);
            var notCarried = Arrive(false);
            double atBorder = original.Body.LinearVelocity.X;
            RunFor(original, rate, n, 1.0);
            RunFor(carried, rate, n, 1.0);
            RunFor(notCarried, rate, n, 1.0);
            Near(carried.Body.LinearVelocity.X, original.Body.LinearVelocity.X, 8.0, $"{s.Behaviour}: speed after the border", rate, n, 0.5);
            Assert.True(original.Body.LinearVelocity.X > atBorder + 0.5, "the motor was still adding speed");
            Near(notCarried.Body.LinearVelocity.X, atBorder, 8.0, $"{s.Behaviour}: speed of a motor left off", rate, n, 0.5);
        }
    }

    [Fact]
    public void A_motor_that_is_off_is_not_carried_and_a_bad_running_state_is_refused()
    {
        var rig = new BehaviourRig(InWorldz, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.GetRunningMotors(out MotorRunningState? linear, out MotorRunningState? angular);
        Assert.Null(linear);
        Assert.Null(angular);
        rig.Car.RestoreRunningMotors(new MotorRunningState(float.NaN, Vector3.UnitX, Vector3.Zero), null);
        rig.Car.RestoreRunningMotors(new MotorRunningState(5000f, Vector3.UnitX, Vector3.Zero), null);
        rig.Car.GetRunningMotors(out linear, out _);
        Assert.Null(linear);
    }
}
