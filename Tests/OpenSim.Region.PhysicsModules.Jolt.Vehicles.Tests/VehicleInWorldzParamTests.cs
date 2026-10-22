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
/// The six InWorldz vehicle float parameters a script can send with llSetVehicleFloatParam: VEHICLE_MOUSELOOK_AZIMUTH
/// 11001, VEHICLE_MOUSELOOK_ALTITUDE 11002, VEHICLE_BANKING_AZIMUTH 11003, VEHICLE_DISABLE_MOTORS_ABOVE 11004,
/// VEHICLE_DISABLE_MOTORS_AFTER 11005 and VEHICLE_INVERTED_BANKING_MODIFIER 11006 (the InWorldz simulator's
/// FloatParams.cs). The controller holds and returns each, to the ranges of the InWorldz simulator's ClampFloatParam
/// (VehicleDynamics.cs:164-218). The mouselook range and the banking range act under the InWorldz behaviour only. Pure,
/// parallel.
/// </summary>
public class VehicleInWorldzParamTests
{
    private const float Pi = (float)Math.PI;
    private const double Deg = Math.PI / 180.0;

    private static VehicleController Car(VehicleBehaviour b)
    {
        var v = new VehicleController(new BehaviourBody()) { Settings = new VehicleSettings { Behaviour = b } };
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        return v;
    }

    // code, the value, what is held (the ranges: azimuth and altitude pi / 32 to pi, banking azimuth pi / 128 to pi,
    // inverted modifier -10 to 10, the two disable params as sent).
    public static IEnumerable<object[]> Params()
    {
        yield return new object[] { 11001, VehFloatParam.MouselookAzimuth, 1f, 1f };
        yield return new object[] { 11001, VehFloatParam.MouselookAzimuth, 0f, Pi / 32f };
        yield return new object[] { 11001, VehFloatParam.MouselookAzimuth, 10f, Pi };
        yield return new object[] { 11002, VehFloatParam.MouselookAltitude, 0.5f, 0.5f };
        yield return new object[] { 11002, VehFloatParam.MouselookAltitude, -1f, Pi / 32f };
        yield return new object[] { 11002, VehFloatParam.MouselookAltitude, 4f, Pi };
        yield return new object[] { 11003, VehFloatParam.BankingAzimuth, 2f, 2f };
        yield return new object[] { 11003, VehFloatParam.BankingAzimuth, 0f, Pi / 128f };
        yield return new object[] { 11003, VehFloatParam.BankingAzimuth, 9f, Pi };
        yield return new object[] { 11004, VehFloatParam.DisableMotorsAbove, 3.5f, 3.5f };
        yield return new object[] { 11005, VehFloatParam.DisableMotorsAfter, 4f, 4f };
        yield return new object[] { 11006, VehFloatParam.InvertedBankingModifier, -0.7f, -0.7f };
        yield return new object[] { 11006, VehFloatParam.InvertedBankingModifier, 50f, 10f };
        yield return new object[] { 11006, VehFloatParam.InvertedBankingModifier, -50f, -10f };
    }

    [Theory]
    [MemberData(nameof(Params))]
    public void A_script_sets_each_one_and_it_is_returned_held_to_its_range(int code, VehFloatParam key, float sent, float held)
    {
        foreach (VehicleBehaviour b in new[] { VehicleBehaviour.SecondLife, VehicleBehaviour.InWorldz })
        {
            VehicleController v = Car(b);
            v.ProcessFloatVehicleParam((Vehicle)code, sent);
            Assert.Equal(held, v.GetFloatParam(key), 5);
        }
    }

    [Theory]
    [InlineData(11001)]
    [InlineData(11002)]
    [InlineData(11003)]
    [InlineData(11004)]
    [InlineData(11005)]
    [InlineData(11006)]
    public void A_value_that_is_not_a_number_is_refused(int code)
    {
        VehicleController v = Car(VehicleBehaviour.InWorldz);
        v.ProcessFloatVehicleParam((Vehicle)code, 0.9f);
        float before = v.GetFloatParam(CodeKey(code));
        v.ProcessFloatVehicleParam((Vehicle)code, float.NaN);
        v.ProcessFloatVehicleParam((Vehicle)code, float.PositiveInfinity);
        Assert.Equal(before, v.GetFloatParam(CodeKey(code)));
    }

    private static VehFloatParam CodeKey(int code) => code switch
    {
        11001 => VehFloatParam.MouselookAzimuth, 11002 => VehFloatParam.MouselookAltitude, 11003 => VehFloatParam.BankingAzimuth,
        11004 => VehFloatParam.DisableMotorsAbove, 11005 => VehFloatParam.DisableMotorsAfter, _ => VehFloatParam.InvertedBankingModifier,
    };

    [Fact]
    public void A_type_change_puts_the_presets_back()
    {
        VehicleController v = Car(VehicleBehaviour.InWorldz);
        v.ProcessFloatVehicleParam((Vehicle)11001, 2f);
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        Assert.Equal(Pi / 4f, v.GetFloatParam(VehFloatParam.MouselookAzimuth), 5);
    }

    // ---- the mouselook range feeds the dead zone ----

    private static BehaviourRig Aimed(VehicleBehaviour b, double yawDegrees, double pitchDegrees, Vector3 motor, Action<VehicleController> setup)
    {
        var rig = new BehaviourRig(new VehicleSettings { Behaviour = b }, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
        setup(rig.Car);
        rig.Car.ProcessVehicleFlags(0x2, remove: true);      // the car's LIMIT_ROLL_ONLY: under InWorldz it keeps the pitch out of the steering
        rig.Car.ProcessVehicleFlags(0x80, remove: false);
        double y = yawDegrees * Deg, p = pitchDegrees * Deg;
        var at = new Vector3((float)(Math.Cos(p) * Math.Cos(y)), (float)(Math.Cos(p) * Math.Sin(y)), (float)-Math.Sin(p));
        rig.Car.DriverCamera = () => new DriverView(at, true);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, motor);
        return rig;
    }

    // The zone is asin(azimuth / 16): 2.81 degrees at pi / 4, 5.63 at pi / 2.
    [Fact]
    public void A_wider_mouselook_azimuth_widens_the_yaw_zone_under_InWorldz()
    {
        var turn = new Vector3(0f, 0f, 2f);
        var preset = Aimed(VehicleBehaviour.InWorldz, 4.0, 0, turn, _ => { });
        var wide = Aimed(VehicleBehaviour.InWorldz, 4.0, 0, turn, c => c.ProcessFloatVehicleParam((Vehicle)11001, Pi / 2f));
        preset.Run(1.0 / 45, 45);
        wide.Run(1.0 / 45, 45);
        Assert.NotEqual(0f, preset.Body.AngularVelocity.Z);   // 4 degrees is outside 2.81
        Assert.Equal(0f, wide.Body.AngularVelocity.Z);        // and inside 5.63
    }

    [Fact]
    public void A_narrower_mouselook_azimuth_narrows_the_yaw_zone_under_InWorldz()
    {
        var turn = new Vector3(0f, 0f, 2f);
        var preset = Aimed(VehicleBehaviour.InWorldz, 2.0, 0, turn, _ => { });
        var narrow = Aimed(VehicleBehaviour.InWorldz, 2.0, 0, turn, c => c.ProcessFloatVehicleParam((Vehicle)11001, Pi / 8f));
        preset.Run(1.0 / 45, 45);
        narrow.Run(1.0 / 45, 45);
        Assert.Equal(0f, preset.Body.AngularVelocity.Z);      // 2 degrees is inside 2.81
        Assert.NotEqual(0f, narrow.Body.AngularVelocity.Z);   // and outside 1.41
    }

    [Fact]
    public void A_wider_mouselook_altitude_widens_the_pitch_zone_under_InWorldz()
    {
        var pitch = new Vector3(0f, 2f, 0f);
        var preset = Aimed(VehicleBehaviour.InWorldz, 0, 4.0, pitch, _ => { });
        var wide = Aimed(VehicleBehaviour.InWorldz, 0, 4.0, pitch, c => c.ProcessFloatVehicleParam((Vehicle)11002, Pi / 2f));
        preset.Run(1.0 / 45, 45);
        wide.Run(1.0 / 45, 45);
        Assert.NotEqual(0f, preset.Body.AngularVelocity.Y);
        Assert.Equal(0f, wide.Body.AngularVelocity.Y);
    }

    [Fact]
    public void Second_Life_has_no_zone_whatever_the_range()
    {
        var turn = new Vector3(0f, 0f, 2f);
        var a = Aimed(VehicleBehaviour.SecondLife, 4.0, 0, turn, _ => { });
        var b = Aimed(VehicleBehaviour.SecondLife, 4.0, 0, turn, c => c.ProcessFloatVehicleParam((Vehicle)11001, Pi / 2f));
        a.Run(1.0 / 45, 45);
        b.Run(1.0 / 45, 45);
        Assert.NotEqual(0f, b.Body.AngularVelocity.Z);
        Assert.Equal(a.Body.AngularVelocity.Z, b.Body.AngularVelocity.Z);
    }

    // ---- the banking range ----

    // The yaw rate banking adds in one step to a vehicle rolled 0.3 rad: the rate with banking on, less the rate with it
    // off (the attractor's own part is the same in both).
    private static float BankingYaw(VehicleBehaviour b, Vehicle type, double rollDegrees, Action<VehicleController> setup, int steps = 1)
    {
        float Run(float efficiency)
        {
            var rig = new BehaviourRig(new VehicleSettings { Behaviour = b }, type, gravity: false);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 2f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 1f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_TIMESCALE, 1f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_MIX, 0f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, efficiency);
            setup(rig.Car);
            rig.Tilt(rollDegrees, 0);
            for (int i = 0; i < steps; i++)
                rig.Step(1.0 / 45);
            return rig.Body.AngularVelocity.Z;
        }
        return Run(1f) - Run(0f);
    }

    [Fact]
    public void A_wider_banking_azimuth_banks_less_under_InWorldz()
    {
        // Halcyon's turn starts at pi / 64 whatever the rate banking asks for and ramps toward it, so the range shows after some steps.
        float preset = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_CAR, 17, _ => { }, steps: 30);
        float wide = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_CAR, 17, c => c.ProcessFloatVehicleParam((Vehicle)11003, Pi), steps: 30);
        Assert.True(Math.Abs(preset) > 1e-4f);
        Assert.True(Math.Abs(wide) > 1e-4f);
        Assert.Equal(Math.Sign(preset), Math.Sign(wide));
        Assert.True(Math.Abs(wide) < Math.Abs(preset), $"preset {preset}, wide {wide}");   // the roll counts as a share of the range
    }

    [Fact]
    public void Second_Life_banks_over_the_documented_range_whatever_the_azimuth()
    {
        float preset = BankingYaw(VehicleBehaviour.SecondLife, Vehicle.TYPE_CAR, 17, _ => { });
        float wide = BankingYaw(VehicleBehaviour.SecondLife, Vehicle.TYPE_CAR, 17, c => c.ProcessFloatVehicleParam((Vehicle)11003, Pi));
        Assert.True(Math.Abs(preset) > 1e-4f);
        Assert.Equal(preset, wide);
    }

    // ---- the inverted banking modifier ----

    // An airplane rolled to 170 degrees (inverted, 10 degrees from level the other way up).
    [Fact]
    public void An_inverted_airplane_banks_by_the_modifier_under_InWorldz()
    {
        // Halcyon's turn starts at pi / 64 whatever the rate banking asks for and ramps toward it, so the modifier shows after some steps.
        float one = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_AIRPLANE, 170, c => c.ProcessFloatVehicleParam((Vehicle)11006, 1f), steps: 30);
        float minus = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_AIRPLANE, 170, c => c.ProcessFloatVehicleParam((Vehicle)11006, -1f), steps: 30);
        float half = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_AIRPLANE, 170, c => c.ProcessFloatVehicleParam((Vehicle)11006, 0.5f), steps: 30);
        Assert.True(Math.Abs(one) > 1e-4f);
        Assert.Equal(-Math.Sign(one), Math.Sign(minus));
        Assert.Equal(Math.Sign(one), Math.Sign(half));
        Assert.True(Math.Abs(half) < Math.Abs(one), $"one {one}, half {half}");
    }

    [Fact]
    public void An_upright_airplane_ignores_the_modifier()
    {
        float one = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_AIRPLANE, 10, c => c.ProcessFloatVehicleParam((Vehicle)11006, 1f));
        float minus = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_AIRPLANE, 10, c => c.ProcessFloatVehicleParam((Vehicle)11006, -1f));
        Assert.True(Math.Abs(one) > 1e-4f);
        Assert.Equal(one, minus);
    }

    [Fact]
    public void A_vehicle_that_is_not_an_airplane_ignores_the_modifier_even_inverted()
    {
        float one = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_CAR, 170, c => c.ProcessFloatVehicleParam((Vehicle)11006, 1f));
        float minus = BankingYaw(VehicleBehaviour.InWorldz, Vehicle.TYPE_CAR, 170, c => c.ProcessFloatVehicleParam((Vehicle)11006, -1f));
        Assert.Equal(one, minus);
    }

    [Fact]
    public void Second_Life_ignores_the_modifier()
    {
        float one = BankingYaw(VehicleBehaviour.SecondLife, Vehicle.TYPE_AIRPLANE, 170, c => c.ProcessFloatVehicleParam((Vehicle)11006, 1f));
        float minus = BankingYaw(VehicleBehaviour.SecondLife, Vehicle.TYPE_AIRPLANE, 170, c => c.ProcessFloatVehicleParam((Vehicle)11006, -1f));
        Assert.True(Math.Abs(one) > 1e-4f);
        Assert.Equal(one, minus);
    }
}
