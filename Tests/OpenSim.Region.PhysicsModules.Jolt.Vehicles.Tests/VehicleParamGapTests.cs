/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What a script's vehicle parameters do when they are not numbers (a non-finite value is refused and the stored one
/// kept, for every float, vector and rotation parameter), the legacy car preset, and that the vehicle debug switches
/// belong to a region's settings and not to the process. Pure, parallel.
/// </summary>
public class VehicleParamGapTests
{
    private static VehicleController Car()
    {
        var v = new VehicleController(null!);
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        return v;
    }

    // Everything the controller exposes, as text: the float and vector params and the reference frame.
    private static string Snapshot(VehicleController v)
    {
        var parts = new List<string>();
        foreach (VehFloatParam k in Enum.GetValues<VehFloatParam>())
            parts.Add($"{k}={v.GetFloatParam(k):R}");
        foreach (VehVectorParam k in Enum.GetValues<VehVectorParam>())
            parts.Add($"{k}={v.GetVecParam(k)}");
        parts.Add($"frame={v.ReferenceFrame}");
        return string.Join(";", parts);
    }

    public static IEnumerable<object[]> NotFinite()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (int axis = 0; axis < 4; axis++)
                yield return new object[] { bad, axis };
    }

    // VEHICLE_REFERENCE_FRAME: a quaternion with a NaN or infinite component is not a rotation. The old frame stays.
    [Theory]
    [MemberData(nameof(NotFinite))]
    public void A_reference_frame_that_is_not_finite_is_refused_and_the_old_one_kept(float bad, int axis)
    {
        VehicleController v = Car();
        Quaternion frame = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        v.ProcessRotationVehicleParam(Vehicle.REFERENCE_FRAME, frame);
        Assert.Equal(frame, v.ReferenceFrame);

        var q = new Quaternion(0f, 0f, 0f, 1f);
        switch (axis)
        {
            case 0: q.X = bad; break;
            case 1: q.Y = bad; break;
            case 2: q.Z = bad; break;
            default: q.W = bad; break;
        }
        v.ProcessRotationVehicleParam(Vehicle.REFERENCE_FRAME, q);

        Assert.Equal(frame, v.ReferenceFrame);
    }

    [Fact]
    public void A_finite_reference_frame_is_still_set_and_normalised()
    {
        VehicleController v = Car();
        v.ProcessRotationVehicleParam(Vehicle.REFERENCE_FRAME, new Quaternion(0f, 0f, 2f, 2f));
        Quaternion f = v.ReferenceFrame;
        Assert.Equal(1f, MathF.Sqrt(f.X * f.X + f.Y * f.Y + f.Z * f.Z + f.W * f.W), 5);
        Assert.Equal(f.Z, f.W, 5);
    }

    // Every float and vector parameter code a script can set: a non-finite value changes nothing.
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void No_float_or_vector_parameter_takes_a_value_that_is_not_finite(float bad)
    {
        VehicleController v = Car();
        string before = Snapshot(v);

        foreach (Vehicle code in Enum.GetValues<Vehicle>())
        {
            v.ProcessFloatVehicleParam(code, bad);
            v.ProcessVectorVehicleParam(code, new Vector3(bad, 1f, 1f));
            v.ProcessVectorVehicleParam(code, new Vector3(1f, bad, 1f));
            v.ProcessVectorVehicleParam(code, new Vector3(1f, 1f, bad));
            v.ProcessRotationVehicleParam(code, new Quaternion(bad, 0f, 0f, 1f));
        }

        Assert.Equal(before, Snapshot(v));
    }

    // The legacy car is InWorldz's, not the documented one (VehicleDynamics.cs:1767-1804): every value and flag as
    // Halcyon's car preset has it, and, like it, no torque about world z. It differs from the documented car in exactly
    // these values (the first is the documented car's, the second the legacy car's).
    [Fact]
    public void The_legacy_car_is_the_InWorldz_car_with_no_torque_about_world_z()
    {
        var legacy = new VehicleController(null!) { Settings = new VehicleSettings { Presets = VehiclePresetSet.Legacy } };
        legacy.ProcessTypeChange(Vehicle.TYPE_CAR);
        var documented = new VehicleController(null!) { Settings = new VehicleSettings { Presets = VehiclePresetSet.Documented } };
        documented.ProcessTypeChange(Vehicle.TYPE_CAR);

        Assert.Equal(ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.LimitRollOnly
                     | ExtendedVehicleFlags.HoverUpOnly | ExtendedVehicleFlags.LimitMotorUp, legacy.Flags);
        Assert.Equal(0, (int)(legacy.Flags & ExtendedVehicleFlags.TorqueWorldZ));
        Assert.Equal(documented.Flags, legacy.Flags);

        // Halcyon's car, line by line.
        Assert.Equal(new Vector3(100f, 0.1f, 10f), legacy.GetVecParam(VehVectorParam.LinearFrictionTimescale));
        Assert.Equal(new Vector3(100f, 100f, 0.3f), legacy.GetVecParam(VehVectorParam.AngularFrictionTimescale));
        Assert.Equal(new Vector3(0.5f, 1f, 1f), legacy.GetVecParam(VehVectorParam.LinearMotorTimescale));
        Assert.Equal(new Vector3(0.2f, 0.2f, 0.05f), legacy.GetVecParam(VehVectorParam.AngularMotorTimescale));
        Assert.Equal(new Vector3(10f, 2f, 2f), legacy.GetVecParam(VehVectorParam.LinearMotorDecayTimescale));
        Assert.Equal(new Vector3(0.3f, 0.3f, 0.1f), legacy.GetVecParam(VehVectorParam.AngularMotorDecayTimescale));
        Assert.Equal(Vector3.Zero, legacy.GetVecParam(VehVectorParam.LinearMotorOffset));
        Assert.Equal(1f, legacy.GetFloatParam(VehFloatParam.LinearDeflectionEfficiency));
        Assert.Equal(2f, legacy.GetFloatParam(VehFloatParam.LinearDeflectionTimescale));
        Assert.Equal(0.5f, legacy.GetFloatParam(VehFloatParam.AngularDeflectionEfficiency));
        Assert.Equal(2f, legacy.GetFloatParam(VehFloatParam.AngularDeflectionTimescale));
        Assert.Equal(0.6f, legacy.GetFloatParam(VehFloatParam.VerticalAttractionEfficiency));
        Assert.Equal(2f, legacy.GetFloatParam(VehFloatParam.VerticalAttractionTimescale));
        Assert.Equal(-0.2f, legacy.GetFloatParam(VehFloatParam.BankingEfficiency));
        Assert.Equal(1f, legacy.GetFloatParam(VehFloatParam.BankingMix));
        Assert.Equal(1f, legacy.GetFloatParam(VehFloatParam.BankingTimescale));
        Assert.Equal(1f, legacy.GetFloatParam(VehFloatParam.InvertedBankingModifier));
        Assert.Equal(0.75f, legacy.GetFloatParam(VehFloatParam.DisableMotorsAbove));
        Assert.Equal(2.5f, legacy.GetFloatParam(VehFloatParam.DisableMotorsAfter));

        // And not the documented car's.
        Assert.NotEqual(documented.GetVecParam(VehVectorParam.LinearFrictionTimescale), legacy.GetVecParam(VehVectorParam.LinearFrictionTimescale));
        Assert.NotEqual(documented.GetVecParam(VehVectorParam.AngularFrictionTimescale), legacy.GetVecParam(VehVectorParam.AngularFrictionTimescale));
        Assert.NotEqual(documented.GetVecParam(VehVectorParam.LinearMotorTimescale), legacy.GetVecParam(VehVectorParam.LinearMotorTimescale));
        Assert.NotEqual(documented.GetFloatParam(VehFloatParam.AngularDeflectionEfficiency), legacy.GetFloatParam(VehFloatParam.AngularDeflectionEfficiency));
        Assert.NotEqual(documented.GetFloatParam(VehFloatParam.VerticalAttractionTimescale), legacy.GetFloatParam(VehFloatParam.VerticalAttractionTimescale));
    }

    // The legacy car's other types are unchanged: the sled is still the InWorldz sled.
    [Fact]
    public void The_legacy_sled_is_still_the_old_one()
    {
        var legacy = new VehicleController(null!) { Settings = new VehicleSettings { Presets = VehiclePresetSet.Legacy } };
        legacy.ProcessTypeChange(Vehicle.TYPE_SLED);
        Assert.Equal(new Vector3(1000f, 1f, 1000f), legacy.GetVecParam(VehVectorParam.LinearFrictionTimescale));
    }

    // The debug and enablement switches are settings of a region, not process-wide state: the vehicle limits class
    // holds no mutable static switch that one region could change under another.
    [Fact]
    public void The_vehicle_limits_hold_no_mutable_static_switch()
    {
        var mutable = typeof(VehicleLimits)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => !f.IsLiteral && !f.IsInitOnly)
            .Select(f => f.Name)
            .ToList();
        Assert.Empty(mutable);
    }

    private sealed class FreeBody : IVehicleBody
    {
        public Vector3 Position { get; set; } = new(128f, 128f, 100f);
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public float Mass => 1000f;
        public Vector3 InertiaDiagonal => new(100f, 100f, 100f);
        public Vector3 Gravity => Vector3.Zero;
        public void SetGravityFactor(float factor) { }
        public bool HasCollision => false;
        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void KeepAwake() { }
        public float GetTerrainHeight(Vector3 pos) => 0f;
        public float GetWaterLevel(Vector3 pos) => -100f;
    }

    // A car in free air with its linear motor set to 10 m/s forward: the speed along its nose after one 1/45 s step.
    private static (VehicleController car, FreeBody body) MotorCar(VehicleSettings settings)
    {
        var body = new FreeBody();
        var car = new VehicleController(body) { Settings = settings };
        DateTime t0 = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        car.Clock = () => t0;
        car.ProcessTypeChange(Vehicle.TYPE_CAR);
        car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(10f, 0f, 0f));
        return (car, body);
    }

    [Fact]
    public void Every_switch_is_on_for_enablement_and_off_for_debugging_by_default()
    {
        VehicleSettings d = VehicleSettings.Default;
        Assert.True(d.DoAngularDeflection && d.DoLinearDeflection && d.DoMotors && d.DoAngularFriction
                    && d.DoLinearFriction && d.DoVerticalAttractor && d.DoBanking);
        Assert.False(d.DebugPrintParams || d.DebugTimestep || d.DebugSpikeDetection || d.DebugVehicleChange
                     || d.DebugAngular || d.DebugBanking || d.DebugBlendedZ || d.DebugLinear || d.DebugAttractor
                     || d.DebugRayCast || d.DebugWind || d.DebugAngularFriction || d.DebugLinearFriction
                     || d.DebugAngularMotor || d.DebugLinearMotor || d.DebugDeflection || d.DebugRegionChange);
    }

    // One region switches the motors off: its vehicle does not drive, and a vehicle in another region, stepped between
    // the first one's steps, still does.
    [Fact]
    public void One_regions_switch_does_not_change_another_regions_vehicles()
    {
        var (off, offBody) = MotorCar(new VehicleSettings { DoMotors = false });
        var (on, onBody) = MotorCar(new VehicleSettings());
        var (alsoOn, alsoOnBody) = MotorCar(VehicleSettings.Default);

        for (int i = 0; i < 3; i++)
        {
            off.Step(1f / 45f);
            on.Step(1f / 45f);
            alsoOn.Step(1f / 45f);
        }

        Assert.True(on.Settings.DoMotors);
        Assert.False(off.Settings.DoMotors);
        Assert.True(onBody.LinearVelocity.X > 0.1f, $"driving region: {onBody.LinearVelocity.X}");
        Assert.True(alsoOnBody.LinearVelocity.X > 0.1f, $"default region: {alsoOnBody.LinearVelocity.X}");
        Assert.Equal(0f, offBody.LinearVelocity.X);
    }
}
