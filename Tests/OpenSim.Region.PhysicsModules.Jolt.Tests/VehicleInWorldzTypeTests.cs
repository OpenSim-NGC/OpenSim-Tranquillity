/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The two vehicle types InWorldz has and Second Life does not: VEHICLE_TYPE_SAILBOAT (10001) and
/// VEHICLE_TYPE_MOTORCYCLE (10002), with the preset values and flags of the InWorldz vehicle code
/// (VehicleDynamics.cs:1886-1924 and 1806-1844; read, not run). They are the same under both preset sets and both
/// behaviours, except the sailboat's buoyancy (1 under Second Life's behaviour, 0 under InWorldz's). Pure, parallel.
/// </summary>
public class VehicleInWorldzTypeTests
{
    private const float Pi = (float)Math.PI;

    private static readonly VehiclePresetSet[] Sets = { VehiclePresetSet.Documented, VehiclePresetSet.Legacy };
    private static readonly VehicleBehaviour[] Behaviours = { VehicleBehaviour.SecondLife, VehicleBehaviour.InWorldz };

    public static IEnumerable<object[]> Combinations()
    {
        foreach (VehiclePresetSet p in Sets)
            foreach (VehicleBehaviour b in Behaviours)
                yield return new object[] { p, b };
    }

    private static VehicleController Make(VehiclePresetSet presets, VehicleBehaviour behaviour, Vehicle type)
    {
        var car = new VehicleController(new BehaviourBody()) { Settings = new VehicleSettings { Presets = presets, Behaviour = behaviour } };
        car.ProcessTypeChange(type);
        return car;
    }

    // What the code says, line by line: VehicleDynamics.cs:1807-1842 (motorcycle).
    private static readonly (VehVectorParam key, Vector3 value)[] MotorcycleVectors =
    {
        (VehVectorParam.LinearFrictionTimescale, new Vector3(100f, 0.1f, 10f)),
        (VehVectorParam.AngularFrictionTimescale, new Vector3(3f, 0.2f, 10f)),
        (VehVectorParam.LinearMotorDirection, Vector3.Zero),
        (VehVectorParam.AngularMotorDirection, Vector3.Zero),
        (VehVectorParam.LinearMotorOffset, new Vector3(0f, 0f, -0.1f)),
        (VehVectorParam.LinearMotorTimescale, new Vector3(0.5f, 1f, 1f)),
        (VehVectorParam.AngularMotorTimescale, new Vector3(0.1f, 0.1f, 0.05f)),
        (VehVectorParam.LinearMotorDecayTimescale, new Vector3(10f, 1f, 1f)),
        (VehVectorParam.AngularMotorDecayTimescale, new Vector3(0.2f, 0.8f, 0.1f)),
        (VehVectorParam.LinearWindEfficiency, Vector3.Zero),
        (VehVectorParam.AngularWindEfficiency, Vector3.Zero),
    };

    private static readonly (VehFloatParam key, float value)[] MotorcycleFloats =
    {
        (VehFloatParam.HoverHeight, 0f), (VehFloatParam.HoverEfficiency, 0f), (VehFloatParam.HoverTimescale, 1000f),
        (VehFloatParam.Buoyancy, 0f),
        (VehFloatParam.LinearDeflectionEfficiency, 1f), (VehFloatParam.LinearDeflectionTimescale, 2f),
        (VehFloatParam.AngularDeflectionEfficiency, 0.8f), (VehFloatParam.AngularDeflectionTimescale, 2f),
        (VehFloatParam.VerticalAttractionEfficiency, 1f), (VehFloatParam.VerticalAttractionTimescale, 1f),
        (VehFloatParam.BankingEfficiency, 0.95f), (VehFloatParam.InvertedBankingModifier, -0.5f),
        (VehFloatParam.BankingMix, 0.5f), (VehFloatParam.BankingTimescale, 0.1f),
        (VehFloatParam.MouselookAltitude, Pi / 4f), (VehFloatParam.MouselookAzimuth, Pi / 4f),
        (VehFloatParam.BankingAzimuth, Pi / 2f),
        (VehFloatParam.DisableMotorsAbove, 1.5f), (VehFloatParam.DisableMotorsAfter, 2.5f),
    };

    private const ExtendedVehicleFlags MotorcycleFlags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverUpOnly
        | ExtendedVehicleFlags.LimitMotorUp | ExtendedVehicleFlags.LimitMotorDown | ExtendedVehicleFlags.LimitRollOnly
        | ExtendedVehicleFlags.TorqueWorldZ;

    // VehicleDynamics.cs:1887-1922 (sailboat).
    private static readonly (VehVectorParam key, Vector3 value)[] SailboatVectors =
    {
        (VehVectorParam.LinearFrictionTimescale, new Vector3(200f, 0.5f, 3f)),
        (VehVectorParam.AngularFrictionTimescale, new Vector3(10f, 1f, 0.2f)),
        (VehVectorParam.LinearMotorDirection, Vector3.Zero),
        (VehVectorParam.AngularMotorDirection, Vector3.Zero),
        (VehVectorParam.LinearMotorOffset, Vector3.Zero),
        (VehVectorParam.LinearMotorTimescale, new Vector3(1f, 5f, 5f)),
        (VehVectorParam.AngularMotorTimescale, new Vector3(2f, 2f, 0.1f)),
        (VehVectorParam.LinearMotorDecayTimescale, new Vector3(1f, 10f, 10f)),
        (VehVectorParam.AngularMotorDecayTimescale, new Vector3(0.3f, 0.3f, 0.1f)),
        (VehVectorParam.LinearWindEfficiency, new Vector3(0.02f, 0.001f, 0f)),
        (VehVectorParam.AngularWindEfficiency, new Vector3(0.1f, 0.01f, 0f)),
    };

    private static readonly (VehFloatParam key, float value)[] SailboatFloats =
    {
        (VehFloatParam.HoverHeight, 0.0001f), (VehFloatParam.HoverEfficiency, 0.8f), (VehFloatParam.HoverTimescale, 0.5f),
        (VehFloatParam.Buoyancy, 0f),
        (VehFloatParam.LinearDeflectionEfficiency, 0.5f), (VehFloatParam.LinearDeflectionTimescale, 3f),
        (VehFloatParam.AngularDeflectionEfficiency, 0.5f), (VehFloatParam.AngularDeflectionTimescale, 5f),
        (VehFloatParam.VerticalAttractionEfficiency, 0.5f), (VehFloatParam.VerticalAttractionTimescale, 0.3f),
        (VehFloatParam.BankingEfficiency, 0.8f), (VehFloatParam.InvertedBankingModifier, -0.2f),
        (VehFloatParam.BankingMix, 0.5f), (VehFloatParam.BankingTimescale, 0.5f),
        (VehFloatParam.MouselookAltitude, Pi / 4f), (VehFloatParam.MouselookAzimuth, Pi / 4f),
        (VehFloatParam.BankingAzimuth, Pi / 2f),
        (VehFloatParam.DisableMotorsAbove, 0f), (VehFloatParam.DisableMotorsAfter, 0f),
    };

    private const ExtendedVehicleFlags SailboatFlags = ExtendedVehicleFlags.NoDeflectionUp | ExtendedVehicleFlags.HoverWaterOnly
        | ExtendedVehicleFlags.LimitMotorUp | ExtendedVehicleFlags.LimitMotorDown | ExtendedVehicleFlags.ReactToWind
        | ExtendedVehicleFlags.ReactToCurrents | ExtendedVehicleFlags.TorqueWorldZ;

    private static void CheckPreset(VehicleController car, (VehVectorParam key, Vector3 value)[] vectors,
                                    (VehFloatParam key, float value)[] floats, ExtendedVehicleFlags flags)
    {
        foreach ((VehVectorParam key, Vector3 value) in vectors)
            Assert.True(value == car.GetVecParam(key), $"{key}: {car.GetVecParam(key)} against {value}");
        foreach ((VehFloatParam key, float value) in floats)
            Assert.True(value == car.GetFloatParam(key), $"{key}: {car.GetFloatParam(key)} against {value}");
        Assert.Equal(flags, car.Flags);
        Assert.Equal(Quaternion.Identity, car.ReferenceFrame);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void The_motorcycle_has_the_InWorldz_preset_values_and_flags(VehiclePresetSet presets, VehicleBehaviour behaviour)
    {
        var car = Make(presets, behaviour, Vehicle.TYPE_MOTORCYCLE);
        Assert.True(car.IsActive);
        Assert.Equal(Vehicle.TYPE_MOTORCYCLE, car.Type);
        Assert.Equal(10002, (int)car.Type);
        CheckPreset(car, MotorcycleVectors, MotorcycleFloats, MotorcycleFlags);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void The_sailboat_has_the_InWorldz_preset_values_and_flags(VehiclePresetSet presets, VehicleBehaviour behaviour)
    {
        var car = Make(presets, behaviour, Vehicle.TYPE_SAILBOAT);
        Assert.True(car.IsActive);
        Assert.Equal(Vehicle.TYPE_SAILBOAT, car.Type);
        Assert.Equal(10001, (int)car.Type);
        CheckPreset(car, SailboatVectors, SailboatFloatsFor(behaviour), SailboatFlags);
    }

    // The preset's one difference between the behaviours: under Second Life's behaviour gravity stays on while the hover
    // acts, so the sailboat gets full buoyancy to float at the surface; under InWorldz's hover gravity is off and the
    // preset keeps its own buoyancy of 0.
    private static (VehFloatParam key, float value)[] SailboatFloatsFor(VehicleBehaviour behaviour)
    {
        if (behaviour != VehicleBehaviour.SecondLife)
            return SailboatFloats;
        return SailboatFloats.Select(f => f.key == VehFloatParam.Buoyancy ? (f.key, 1f) : f).ToArray();
    }

    // The type numbers are the InWorldz ones; the module's own internal numbers (6 and 7) are not types a script can set.
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(10000)]
    [InlineData(10003)]
    [InlineData(-1)]
    public void Any_other_number_is_still_no_vehicle(int number)
    {
        var car = Make(VehiclePresetSet.Documented, VehicleBehaviour.SecondLife, Vehicle.TYPE_CAR);
        Assert.True(car.IsActive);
        car.ProcessTypeChange((Vehicle)number);
        Assert.False(car.IsActive);
        Assert.Equal(Vehicle.TYPE_NONE, car.Type);
    }

    // Setting a type after another gives that type's preset whole, whichever it followed.
    [Theory]
    [MemberData(nameof(Combinations))]
    public void A_type_set_after_another_gives_its_own_preset(VehiclePresetSet presets, VehicleBehaviour behaviour)
    {
        var car = Make(presets, behaviour, Vehicle.TYPE_AIRPLANE);
        car.ProcessTypeChange(Vehicle.TYPE_SAILBOAT);
        CheckPreset(car, SailboatVectors, SailboatFloatsFor(behaviour), SailboatFlags);
        car.ProcessTypeChange(Vehicle.TYPE_MOTORCYCLE);
        CheckPreset(car, MotorcycleVectors, MotorcycleFloats, MotorcycleFlags);
        car.ProcessTypeChange(Vehicle.TYPE_BOAT);
        Assert.Equal(Vehicle.TYPE_BOAT, car.Type);
    }

    // The five types Second Life has keep what they had: the documented preset set still gives the page's values.
    [Fact]
    public void The_types_Second_Life_has_are_untouched()
    {
        var car = Make(VehiclePresetSet.Documented, VehicleBehaviour.SecondLife, Vehicle.TYPE_CAR);
        Assert.Equal(new Vector3(100f, 2f, 1000f), car.GetVecParam(VehVectorParam.LinearFrictionTimescale));
        var boat = Make(VehiclePresetSet.Documented, VehicleBehaviour.SecondLife, Vehicle.TYPE_BOAT);
        Assert.Equal(new Vector3(10f, 3f, 2f), boat.GetVecParam(VehVectorParam.LinearFrictionTimescale));
    }

    // Driven: the motorcycle's linear motor along its nose settles where the motor equation puts it, with the 0.5 s
    // motor timescale and the 100 s forward friction: v = M / (1 + Tm / Tf). The sailboat, 1 s and 200 s.
    [Theory]
    [InlineData(Vehicle.TYPE_MOTORCYCLE, 0.5, 100.0)]
    [InlineData(Vehicle.TYPE_SAILBOAT, 1.0, 200.0)]
    public void The_forward_motor_settles_where_the_motor_equation_puts_it(Vehicle type, double motorTimescale, double friction)
    {
        var rig = new BehaviourRig(new VehicleSettings(), type, gravity: false);
        // BehaviourRig turns friction off for its other scenarios; give back the preset's own.
        rig.Car.ProcessTypeChange(type);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 1000f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.HOVER_TIMESCALE, 1000f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0f);
        // The motorcycle's motor offset would pitch it as it pulls; this checks the speed alone.
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_OFFSET, Vector3.Zero);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(5f, 0f, 0f));
        rig.Run(1.0 / 45, 45 * 12);
        double expected = 5.0 / (1.0 + motorTimescale / friction);
        Assert.True(Math.Abs(rig.Body.LinearVelocity.X - expected) < 0.01 * expected, $"{type}: {rig.Body.LinearVelocity.X:0.0000} against {expected:0.0000}");
    }

    // Core: the type number a script gives reaches the physics actor as it is, and the simulator's own record keeps it
    // together with the type's InWorldz preset (the saved record holds both types, so a vehicle handed back from it
    // has the preset's values, not the no-vehicle values).
    [Fact]
    public void The_simulators_record_keeps_the_number_and_the_preset_for_it()
    {
        var record = new SOPVehicle();
        record.ProcessTypeChange((SharedVehicle)10001);
        Assert.Equal(10001, (int)record.Type);
        Assert.Equal(new Vector3(200f, 0.5f, 3f), record.vd.m_linearFrictionTimescale);
        Assert.NotEqual((OpenSim.Region.PhysicsModules.SharedBase.VehicleFlag)0, record.vd.m_flags);
        record.ProcessTypeChange((SharedVehicle)10002);
        Assert.Equal(10002, (int)record.Type);
        Assert.Equal(new Vector3(100f, 0.1f, 10f), record.vd.m_linearFrictionTimescale);
        Assert.NotEqual((OpenSim.Region.PhysicsModules.SharedBase.VehicleFlag)0, record.vd.m_flags);
    }
}
