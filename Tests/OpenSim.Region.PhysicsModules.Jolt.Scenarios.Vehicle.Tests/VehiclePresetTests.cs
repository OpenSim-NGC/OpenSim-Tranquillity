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
/// The values llSetVehicleType gives each type under each preset set: the documented set against Second Life's wiki
/// page of each type (VEHICLE_TYPE_SLED, _CAR, _BOAT, _AIRPLANE, _BALLOON), the legacy set against the values the
/// module had before. The tables here are written out independently of the controller's. Pure, parallel.
/// </summary>
public class VehiclePresetTests
{
    private sealed record Preset(
        Vector3 LinearFriction, Vector3 AngularFriction,
        Vector3 LinearMotorTimescale, Vector3 LinearMotorDecay, Vector3 AngularMotorTimescale, Vector3 AngularMotorDecay,
        Vector3 MotorOffset,
        float HoverHeight, float HoverEfficiency, float HoverTimescale, float Buoyancy,
        float LinearDeflectionEfficiency, float LinearDeflectionTimescale,
        float AngularDeflectionEfficiency, float AngularDeflectionTimescale,
        float AttractionEfficiency, float AttractionTimescale,
        float BankingEfficiency, float BankingMix, float BankingTimescale,
        ExtendedVehicleFlags Flags);

    private static Vector3 V(float s) => new(s, s, s);
    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    private const ExtendedVehicleFlags NoDeflectionUp = ExtendedVehicleFlags.NoDeflectionUp;
    private const ExtendedVehicleFlags RollOnly = ExtendedVehicleFlags.LimitRollOnly;
    private const ExtendedVehicleFlags UpOnly = ExtendedVehicleFlags.HoverUpOnly;
    private const ExtendedVehicleFlags MotorUp = ExtendedVehicleFlags.LimitMotorUp;
    private const ExtendedVehicleFlags MotorDown = ExtendedVehicleFlags.LimitMotorDown;
    private const ExtendedVehicleFlags WaterOnly = ExtendedVehicleFlags.HoverWaterOnly;
    private const ExtendedVehicleFlags WorldZ = ExtendedVehicleFlags.TorqueWorldZ;

    // Second Life's documented defaults (the wiki page of each type; the sled's HOVER_EFFICIENCY 10 held to 1, and its
    // hover off, HOVER_TIMESCALE 1000 for the page's 10, as the page's own comment says "no hover").
    private static readonly Dictionary<Vehicle, Preset> Documented = new()
    {
        [Vehicle.TYPE_SLED] = new(V(30, 1, 1000), V(1000), V(1000), V(120), V(1000), V(120), V(0),
            0, 1, 1000, 0, 1, 1, 0, 10, 1, 1000, 0, 1, 10, NoDeflectionUp | RollOnly | MotorUp),
        [Vehicle.TYPE_CAR] = new(V(100, 2, 1000), V(1000), V(1), V(60), V(1), V(0.8f), V(0),
            0, 0, 1000, 0, 1, 2, 0, 10, 1, 10, -0.2f, 1, 1, NoDeflectionUp | RollOnly | UpOnly | MotorUp),
        [Vehicle.TYPE_BOAT] = new(V(10, 3, 2), V(10), V(5), V(60), V(4), V(4), V(0),
            0, 0.5f, 2, 1, 0.5f, 3, 0.5f, 5, 0.5f, 5, -0.3f, 0.8f, 1, NoDeflectionUp | WaterOnly | UpOnly | MotorUp),
        [Vehicle.TYPE_AIRPLANE] = new(V(200, 10, 5), V(20), V(2), V(60), V(4), V(8), V(0),
            0, 0.5f, 1000, 0, 0.5f, 0.5f, 1, 2, 0.9f, 2, 1, 0.7f, 2, RollOnly),
        [Vehicle.TYPE_BALLOON] = new(V(5), V(10), V(5), V(60), V(6), V(10), V(0),
            5, 0.8f, 10, 1, 0, 5, 0, 5, 1, 1000, 0, 0.7f, 5, ExtendedVehicleFlags.None),
    };

    // The values the module had before the documented set (InWorldz Halcyon).
    private static readonly Dictionary<Vehicle, Preset> Legacy = new()
    {
        [Vehicle.TYPE_SLED] = new(V(1000, 1, 1000), V(1000), V(1000), V(120), V(1000), V(120), V(0, 0, -0.1f),
            0, 0, 1000, 0, 1, 0.3f, 1, 1, 0.1f, 10, 0, 1, 10, NoDeflectionUp | RollOnly | MotorUp),
        // InWorldz's car (VehicleDynamics.cs:1767-1804): not the documented car, and without TORQUE_WORLD_Z.
        [Vehicle.TYPE_CAR] = new(V(100, 0.1f, 10), V(100, 100, 0.3f), V(0.5f, 1, 1), V(10, 2, 2), V(0.2f, 0.2f, 0.05f), V(0.3f, 0.3f, 0.1f), V(0),
            0, 0, 1000, 0, 1, 2, 0.5f, 2, 0.6f, 2, -0.2f, 1, 1, NoDeflectionUp | RollOnly | UpOnly | MotorUp),
        [Vehicle.TYPE_BOAT] = new(V(200, 0.5f, 3), V(10, 1, 0.1f), V(1, 5, 5), V(1, 10, 10), V(0.2f, 2, 0.1f), V(0.3f, 0.3f, 0.1f), V(0),
            0.5f, 0.8f, 0.2f, 1, 0.5f, 3, 0.5f, 5, 0.5f, 0.2f, 1, 0.5f, 0.2f, NoDeflectionUp | WaterOnly | MotorUp | MotorDown | WorldZ),
        [Vehicle.TYPE_AIRPLANE] = new(V(200, 10, 5), V(1, 0.1f, 0.5f), V(2), V(60), V(1, 2, 1), V(8), V(0),
            0, 0.5f, 1000, 0, 0.5f, 0.5f, 1, 2, 0.9f, 2, 1, 0.7f, 1, WorldZ | RollOnly),
        [Vehicle.TYPE_BALLOON] = new(V(1, 1, 5), V(2, 0.5f, 1), V(1, 5, 5), V(60), V(2, 2, 0.3f), V(0.3f, 0.3f, 1), V(0),
            5, 0.8f, 10, 1, 0, 5, 0, 5, 0.5f, 4, 0.05f, 0.5f, 5, ExtendedVehicleFlags.None),
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (VehiclePresetSet set in new[] { VehiclePresetSet.Documented, VehiclePresetSet.Legacy })
            foreach (Vehicle type in new[] { Vehicle.TYPE_SLED, Vehicle.TYPE_CAR, Vehicle.TYPE_BOAT, Vehicle.TYPE_AIRPLANE, Vehicle.TYPE_BALLOON })
                yield return new object[] { set, type };
    }

    private static VehicleController Make(VehiclePresetSet set, Vehicle type)
    {
        var v = new VehicleController(null!) { Settings = new VehicleSettings { Presets = set } };
        v.ProcessTypeChange(type);
        return v;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_type_gets_the_preset_table_of_the_set(VehiclePresetSet set, Vehicle type)
    {
        Preset p = (set == VehiclePresetSet.Documented ? Documented : Legacy)[type];
        VehicleController v = Make(set, type);

        Assert.Equal(p.LinearFriction, v.GetVecParam(VehVectorParam.LinearFrictionTimescale));
        Assert.Equal(p.AngularFriction, v.GetVecParam(VehVectorParam.AngularFrictionTimescale));
        Assert.Equal(p.LinearMotorTimescale, v.GetVecParam(VehVectorParam.LinearMotorTimescale));
        Assert.Equal(p.LinearMotorDecay, v.GetVecParam(VehVectorParam.LinearMotorDecayTimescale));
        Assert.Equal(p.AngularMotorTimescale, v.GetVecParam(VehVectorParam.AngularMotorTimescale));
        Assert.Equal(p.AngularMotorDecay, v.GetVecParam(VehVectorParam.AngularMotorDecayTimescale));
        Assert.Equal(p.MotorOffset, v.GetVecParam(VehVectorParam.LinearMotorOffset));
        Assert.Equal(Vector3.Zero, v.GetVecParam(VehVectorParam.LinearMotorDirection));
        Assert.Equal(Vector3.Zero, v.GetVecParam(VehVectorParam.AngularMotorDirection));

        Assert.Equal(p.HoverHeight, v.GetFloatParam(VehFloatParam.HoverHeight));
        Assert.Equal(p.HoverEfficiency, v.GetFloatParam(VehFloatParam.HoverEfficiency));
        Assert.Equal(p.HoverTimescale, v.GetFloatParam(VehFloatParam.HoverTimescale));
        Assert.Equal(p.Buoyancy, v.GetFloatParam(VehFloatParam.Buoyancy));
        Assert.Equal(p.LinearDeflectionEfficiency, v.GetFloatParam(VehFloatParam.LinearDeflectionEfficiency));
        Assert.Equal(p.LinearDeflectionTimescale, v.GetFloatParam(VehFloatParam.LinearDeflectionTimescale));
        Assert.Equal(p.AngularDeflectionEfficiency, v.GetFloatParam(VehFloatParam.AngularDeflectionEfficiency));
        Assert.Equal(p.AngularDeflectionTimescale, v.GetFloatParam(VehFloatParam.AngularDeflectionTimescale));
        Assert.Equal(p.AttractionEfficiency, v.GetFloatParam(VehFloatParam.VerticalAttractionEfficiency));
        Assert.Equal(p.AttractionTimescale, v.GetFloatParam(VehFloatParam.VerticalAttractionTimescale));
        Assert.Equal(p.BankingEfficiency, v.GetFloatParam(VehFloatParam.BankingEfficiency));
        Assert.Equal(p.BankingMix, v.GetFloatParam(VehFloatParam.BankingMix));
        Assert.Equal(p.BankingTimescale, v.GetFloatParam(VehFloatParam.BankingTimescale));
        Assert.Equal(p.Flags, v.Flags);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_scripts_own_parameters_override_either_set(VehiclePresetSet set, Vehicle type)
    {
        VehicleController v = Make(set, type);
        v.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
        v.ProcessFloatVehicleParam(Vehicle.LINEAR_MOTOR_TIMESCALE, 0.7f);
        v.ProcessFloatVehicleParam(Vehicle.HOVER_HEIGHT, 3f);
        v.ProcessFloatVehicleParam(Vehicle.VERTICAL_ATTRACTION_TIMESCALE, 3f);
        v.ProcessFloatVehicleParam(Vehicle.BANKING_EFFICIENCY, 0.4f);
        v.ProcessVehicleFlags((int)ExtendedVehicleFlags.HoverGlobalHeight, false);
        v.ProcessVehicleFlags((int)ExtendedVehicleFlags.LimitRollOnly, true);

        Assert.Equal(new Vector3(1f, 1f, 1000f), v.GetVecParam(VehVectorParam.LinearFrictionTimescale));
        Assert.Equal(new Vector3(0.7f), v.GetVecParam(VehVectorParam.LinearMotorTimescale));
        Assert.Equal(3f, v.GetFloatParam(VehFloatParam.HoverHeight));
        Assert.Equal(3f, v.GetFloatParam(VehFloatParam.VerticalAttractionTimescale));
        Assert.Equal(0.4f, v.GetFloatParam(VehFloatParam.BankingEfficiency));
        Assert.True((v.Flags & ExtendedVehicleFlags.HoverGlobalHeight) != 0);
        Assert.True((v.Flags & ExtendedVehicleFlags.LimitRollOnly) == 0);
        // Everything not set keeps the set's value.
        Preset p = (set == VehiclePresetSet.Documented ? Documented : Legacy)[type];
        Assert.Equal(p.AngularFriction, v.GetVecParam(VehVectorParam.AngularFrictionTimescale));
        Assert.Equal(p.Buoyancy, v.GetFloatParam(VehFloatParam.Buoyancy));
    }

    [Fact]
    public void The_documented_set_is_the_default()
    {
        Assert.Equal(VehiclePresetSet.Documented, VehicleSettings.Default.Presets);
        var v = new VehicleController(null!);
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        Assert.Equal(new Vector3(100f, 2f, 1000f), v.GetVecParam(VehVectorParam.LinearFrictionTimescale));
    }

    [Fact]
    public void A_new_type_starts_again_from_its_preset()
    {
        VehicleController v = Make(VehiclePresetSet.Documented, Vehicle.TYPE_CAR);
        v.ProcessFloatVehicleParam(Vehicle.BUOYANCY, 0.5f);
        v.ProcessTypeChange(Vehicle.TYPE_BOAT);
        Assert.Equal(1f, v.GetFloatParam(VehFloatParam.Buoyancy));
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        Assert.Equal(0f, v.GetFloatParam(VehFloatParam.Buoyancy));
    }

    [Theory]
    [InlineData(null, 0f)]            // the default: the documented boat hovers at the water (HOVER_HEIGHT 0)
    [InlineData("documented", 0f)]
    [InlineData("legacy", 0.5f)]      // the legacy boat at 0.5 m over it
    public void The_region_key_chooses_the_set(string presets, float height)
    {
        // The boat scenario (5 s under its motor), to rest: it ends at its preset's hover height over the water. (The
        // documented boat, set down 0.5 m over its height, falls under the water first: HOVER_UP_ONLY takes its
        // buoyancy away above the height, and its vertical friction of 2 s brings it back up slowly.)
        var o = new HarnessOptions { RateHz = 11.0 }.SecondLife();
        if (presets != null)
            o.Jolt["VehiclePresets"] = presets;
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("boat"), o);
        float z = r.Samples[^1].Position.Z;
        Assert.True(Math.Abs(z - (Course.Water + height)) < 0.1f, $"{presets}: z {z:0.000}, expected {Course.Water + height:0.000}");
    }
}
