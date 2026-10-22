/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The angular rate limiter, on under both behaviours: the angular motor direction a script sets is scaled by
/// (angular deflection timescale + 0.001) / (angular deflection efficiency + 0.00001) when that is below 1, but by no
/// less than [Jolt] VehicleAngularLimiterFloor (0.1). The formula and its floor are the InWorldz vehicle code's
/// (VehicleDynamics.cs:3107-3116; read, not run). Pure (a body with no engine), parallel.
/// </summary>
public class VehicleAngularLimiterTests
{
    private static VehicleSettings Settings(VehicleBehaviour behaviour, float? floor = null)
        => floor is float f
            ? new VehicleSettings { Behaviour = behaviour, AngularLimiterFloor = f }
            : new VehicleSettings { Behaviour = behaviour };

    private static BehaviourRig Rig(VehicleSettings s, float deflectionTimescale, float deflectionEfficiency)
    {
        var rig = new BehaviourRig(s, Vehicle.TYPE_CAR, gravity: false);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, deflectionTimescale);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, deflectionEfficiency);
        return rig;
    }

    // What the formula gives for the figures the controller holds (a timescale is held to at least 0.0156).
    private static double Scale(double timescale, double efficiency, double floor)
    {
        double s = (timescale + 0.001) / (efficiency + 0.00001);
        return s < 1.0 ? Math.Max(s, floor) : 1.0;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (VehicleBehaviour b in new[] { VehicleBehaviour.SecondLife, VehicleBehaviour.InWorldz })
        {
            yield return new object[] { b, 10f, 0f };       // no deflection at all: no limit
            yield return new object[] { b, 1f, 1f };        // timescale 1, efficiency 1: 1.001 / 1.00001, above 1: no limit
            yield return new object[] { b, 2f, 0.5f };      // 4: no limit
            yield return new object[] { b, 0.5f, 1f };      // 0.5005
            yield return new object[] { b, 0.2f, 0.8f };    // 0.2512
            yield return new object[] { b, 0.1f, 1f };      // 0.101
            yield return new object[] { b, 0.05f, 0.5f };   // 0.102, just above the floor
            yield return new object[] { b, 0.0156f, 1f };   // 0.0166: held to the floor
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_motor_direction_is_scaled_by_the_deflection_ratio_but_by_no_less_than_the_floor(VehicleBehaviour behaviour, float timescale, float efficiency)
    {
        var rig = Rig(Settings(behaviour), timescale, efficiency);
        var asked = new Vector3(0.5f, -1f, 2f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, asked);
        float scale = (float)Scale(timescale, efficiency, 0.1);
        Vector3 effect = rig.Car.AngularMotorDirectionInEffect;
        Assert.Equal(asked.X * scale, effect.X, 5);
        Assert.Equal(asked.Y * scale, effect.Y, 5);
        Assert.Equal(asked.Z * scale, effect.Z, 5);
        // What the script set is kept as it was.
        Assert.Equal(asked, rig.Car.GetVecParam(VehVectorParam.AngularMotorDirection));
    }

    [Fact]
    public void Where_the_ratio_is_not_below_one_the_direction_is_not_touched_to_the_bit()
    {
        foreach (VehicleBehaviour b in new[] { VehicleBehaviour.SecondLife, VehicleBehaviour.InWorldz })
        {
            var rig = Rig(Settings(b), 10f, 0f);
            var asked = new Vector3(0.123456789f, -1.7f, 3.3f);
            rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, asked);
            Assert.Equal(asked, rig.Car.AngularMotorDirectionInEffect);
        }
    }

    // The scalar form of the call sets all three axes and is limited as the vector form is.
    [Fact]
    public void The_scalar_form_of_the_call_is_limited_too()
    {
        var rig = Rig(Settings(VehicleBehaviour.SecondLife), 0.1f, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, 2f);
        float scale = (float)Scale(0.1, 1.0, 0.1);
        Assert.Equal(new Vector3(2f * scale), rig.Car.AngularMotorDirectionInEffect);
    }

    // InWorldz reads the deflection settings at the moment it is given the direction, and so does this: settings made
    // afterwards do not rescale a direction already set.
    [Fact]
    public void The_scale_is_taken_when_the_direction_is_set()
    {
        var rig = Rig(Settings(VehicleBehaviour.InWorldz), 10f, 0f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 2f));
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, 0.1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, 1f);
        Assert.Equal(new Vector3(0f, 0f, 2f), rig.Car.AngularMotorDirectionInEffect);
        // Set again, it is limited.
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 2f));
        Assert.Equal(2f * (float)Scale(0.1, 1.0, 0.1), rig.Car.AngularMotorDirectionInEffect.Z, 5);
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.2f, 0.2f)]
    [InlineData(1f, 1f)]
    [InlineData(0f, 0f)]
    public void The_floor_is_the_figure_of_its_key_and_one_turns_the_limiter_off(float floor, float expectedShare)
    {
        // Ratio 0.0166 (timescale 0.0156, efficiency 1): held to the floor; with floor 0 it is the ratio itself.
        var rig = Rig(Settings(VehicleBehaviour.SecondLife, floor), 0.0156f, 1f);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 2f));
        float ratio = (0.0156f + 0.001f) / (1f + 0.00001f);
        float share = floor == 0f ? ratio : expectedShare;
        Assert.Equal(2f * share, rig.Car.AngularMotorDirectionInEffect.Z, 5);
    }

    // What a driver notices: a car with a quick, strong deflection turns more slowly for the same motor setting than
    // the same car with the deflection off. The rate it settles on is the scaled direction.
    [Theory]
    [InlineData(VehicleBehaviour.SecondLife)]
    [InlineData(VehicleBehaviour.InWorldz)]
    public void A_strongly_deflecting_car_turns_at_the_scaled_rate(VehicleBehaviour behaviour)
    {
        double Settled(float timescale, float efficiency)
        {
            var rig = Rig(Settings(behaviour), timescale, efficiency);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_TIMESCALE, 0.2f);
            rig.Car.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 120f);
            rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(0f, 0f, 2f));
            rig.Run(1.0 / 45, 135);
            return rig.Body.AngularVelocity.Z;
        }
        double free = Settled(10f, 0f);
        double limited = Settled(0.2f, 0.8f);
        Assert.InRange(free, 1.97, 2.0);
        double expected = 2.0 * Scale(0.2, 0.8, 0.1);
        Assert.True(Math.Abs(limited - expected) < 0.02 * expected, $"turned at {limited:0.0000} rad/s, the scaled direction is {expected:0.0000}");
    }

    // No preset of either set, for any type, sits below the ratio: a vehicle is not limited until a script asks for it.
    [Theory]
    [InlineData(VehiclePresetSet.Documented)]
    [InlineData(VehiclePresetSet.Legacy)]
    public void No_preset_is_limited(VehiclePresetSet presets)
    {
        foreach (Vehicle type in new[] { Vehicle.TYPE_SLED, Vehicle.TYPE_CAR, Vehicle.TYPE_BOAT, Vehicle.TYPE_AIRPLANE, Vehicle.TYPE_BALLOON })
        {
            var rig = new BehaviourRig(new VehicleSettings { Presets = presets }, type, gravity: false);
            rig.Car.ProcessTypeChange(type);
            double ratio = (rig.Car.GetFloatParam(VehFloatParam.AngularDeflectionTimescale) + 0.001)
                         / (rig.Car.GetFloatParam(VehFloatParam.AngularDeflectionEfficiency) + 0.00001);
            Assert.True(ratio >= 1.0, $"{presets} {type}: ratio {ratio}");
        }
    }

    // [Jolt] VehicleAngularLimiterFloor: 0.1 with no key, the key changes it, a bad value is refused.
    [Fact]
    public void The_key_defaults_to_the_InWorldz_floor_and_changes_it()
    {
        Assert.Equal(0.1f, JoltConfig.FromConfig(new IniConfigSource(), null).ToVehicleSettings().AngularLimiterFloor);
        Assert.Equal(0.1f, VehicleSettings.Default.AngularLimiterFloor);
        var src = new IniConfigSource();
        src.AddConfig("Jolt").Set("VehicleAngularLimiterFloor", "0.4");
        var warnings = new List<string>();
        Assert.Equal(0.4f, JoltConfig.FromConfig(src, warnings).ToVehicleSettings().AngularLimiterFloor);
        Assert.Empty(warnings);
    }

    // The key is the same under both behaviours: the behaviour does not move it.
    [Fact]
    public void The_behaviour_does_not_change_the_floor()
    {
        var src = new IniConfigSource();
        src.AddConfig("Jolt").Set("VehicleBehaviour", "InWorldz");
        Assert.Equal(0.1f, JoltConfig.FromConfig(src, null).ToVehicleSettings().AngularLimiterFloor);
    }
}
