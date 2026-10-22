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
/// InWorldz's wind and water currents (Halcyon VehicleDynamics.cs:863-992, read, not run), under
/// VehicleBehaviour = InWorldz only: a vehicle with REACT_TO_WIND is pushed by the wind at its position and one with
/// REACT_TO_CURRENTS by the water's current, in proportion to its wind efficiencies (12001 linear, 12002 angular); the wind
/// is read in every step. Pure (a body with no engine, a stand-in wind module) and parallel.
/// </summary>
public class VehicleWindTests
{
    private const double Step = 1.0 / 45;
    // The flag numbers a script sends (llSetVehicleFlags): REACT_TO_CURRENTS and REACT_TO_WIND.
    private const int Currents = 0x10000;
    private const int Wind = 0x20000;

    private static VehicleSettings Settings(VehicleBehaviour b) => new() { Behaviour = b };

    // A boat with nothing acting on it but what a test sets: no gravity, hover, friction, attractor or deflection. Its wind
    // flags and efficiencies are as given (the boat preset reacts to nothing).
    private static BehaviourRig Boat(VehicleBehaviour behaviour, int flags, Vector3 linearEfficiency,
                                     Func<Vector3, Vector3> windAt, Vector3? angularEfficiency = null)
    {
        var rig = new BehaviourRig(Settings(behaviour), Vehicle.TYPE_BOAT, gravity: false);
        rig.Body.WindAt = windAt;
        rig.Car.ProcessVehicleFlags(flags, remove: false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, linearEfficiency);
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_WIND_EFFICIENCY, angularEfficiency ?? Vector3.Zero);
        return rig;
    }

    private static Func<Vector3, Vector3> Blowing(float x, float y = 0f) => _ => new Vector3(x, y, 0f);

    // The InWorldz sailboat as its preset sets it: reacts to wind and currents, efficiencies 0.02, 0.001, 0 and 0.1, 0.01, 0.
    private static BehaviourRig Sailboat(VehicleBehaviour behaviour, Func<Vector3, Vector3> windAt)
    {
        var rig = new BehaviourRig(Settings(behaviour), Vehicle.TYPE_SAILBOAT, gravity: false);
        rig.Body.WindAt = windAt;
        return rig;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The vehicle

    [Fact]
    public void A_sailboat_picks_up_speed_downwind()
    {
        BehaviourRig boat = Sailboat(VehicleBehaviour.InWorldz, Blowing(10f));
        boat.Run(Step, 45 * 2);
        // 10 m/s of wind at 0.02 is 0.2 m/s every second, less the 7 steps before the first read, and a little less for
        // the bow the wind has lifted by then (the preset's angular efficiency pitches it).
        Assert.InRange(boat.Body.LinearVelocity.X, 0.35f, 0.42f);
        Assert.InRange(Math.Abs(boat.Body.LinearVelocity.Y), 0f, 0.01f);
    }

    [Fact]
    public void The_speed_it_picks_up_is_in_proportion_to_its_efficiency()
    {
        BehaviourRig slow = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(0.02f, 0f, 0f), Blowing(10f));
        BehaviourRig quick = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(0.04f, 0f, 0f), Blowing(10f));
        BehaviourRig back = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(-0.02f, 0f, 0f), Blowing(10f));
        foreach (BehaviourRig r in new[] { slow, quick, back })
            r.Run(Step, 45 * 4);
        Assert.Equal(2f, quick.Body.LinearVelocity.X / slow.Body.LinearVelocity.X, 2);
        Assert.True(back.Body.LinearVelocity.X < 0f, "a negative efficiency pushes against the wind");
        Assert.Equal(-slow.Body.LinearVelocity.X, back.Body.LinearVelocity.X, 3);
    }

    [Fact]
    public void The_push_stops_once_the_vehicle_moves_as_fast_as_the_wind()
    {
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(1f, 0f, 0f), Blowing(4f));
        boat.Run(Step, 45 * 3);
        Assert.InRange(boat.Body.LinearVelocity.X, 3.9f, 4.2f);
        boat.Run(Step, 45 * 3);
        Assert.InRange(boat.Body.LinearVelocity.X, 3.9f, 4.2f);
    }

    [Fact]
    public void The_wind_acts_on_the_vehicles_own_axes()
    {
        // The boat's nose points along world y: a wind along world y is a wind along its x axis, and one along world x
        // pushes it along its y axis, by the efficiency of that axis.
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(0.1f, 0f, 0f), Blowing(0f, 10f));
        boat.Body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));
        boat.Run(Step, 45 * 2);
        Assert.True(boat.Body.LinearVelocity.Y > 1.5f, $"along its nose: {boat.Body.LinearVelocity}");
        Assert.InRange(Math.Abs(boat.Body.LinearVelocity.X), 0f, 0.01f);
    }

    [Fact]
    public void A_boat_with_REACT_TO_CURRENTS_moves_with_the_current_below_the_water()
    {
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Currents, new Vector3(1f, 0f, 0f), null);
        // The region's wind module hands the water's current below the water and the wind above it.
        boat.Body.WindAt = p => p.Z < boat.Body.WaterLevel ? new Vector3(3f, 0f, 0f) : new Vector3(-9f, 0f, 0f);
        boat.Body.WaterLevel = 101f;                       // the boat (z 100) is under the water
        boat.Run(Step, 45 * 4);
        Assert.InRange(boat.Body.LinearVelocity.X, 2.9f, 3.2f);
    }

    [Fact]
    public void REACT_TO_CURRENTS_alone_does_not_react_to_the_wind_above_the_water()
    {
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Currents, new Vector3(1f, 0f, 0f), Blowing(8f));
        boat.Run(Step, 45 * 3);
        Assert.Equal(Vector3.Zero, boat.Body.LinearVelocity);
    }

    [Fact]
    public void REACT_TO_WIND_alone_is_pushed_by_the_current_below_the_water_as_in_Halcyon()
    {
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(1f, 0f, 0f), null);
        boat.Body.WindAt = p => p.Z < boat.Body.WaterLevel ? new Vector3(3f, 0f, 0f) : new Vector3(-9f, 0f, 0f);
        boat.Body.WaterLevel = 101f;
        boat.Run(Step, 45 * 4);
        Assert.InRange(boat.Body.LinearVelocity.X, 2.9f, 3.2f);
    }

    [Fact]
    public void The_wind_rolls_and_pitches_a_vehicle_by_its_angular_efficiency()
    {
        // A wind along the nose pitches it (about y) and one along its right (-y) rolls it (about x), never about z. Halcyon's
        // taper counts a vehicle at rest as moving the positive way, so these are the two directions that start from rest.
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Wind, Vector3.Zero, Blowing(10f, -10f), new Vector3(0.05f, 0.05f, 0f));
        boat.Run(Step, 45 / 2);
        // 10 m/s at 0.05 is 0.5 rad/s every second, for the 22 steps of the run (the wind is read in every step).
        Assert.InRange(boat.Body.AngularVelocity.X, 0.23f, 0.26f);
        Assert.InRange(boat.Body.AngularVelocity.Y, 0.23f, 0.26f);
        Assert.InRange(Math.Abs(boat.Body.AngularVelocity.Z), 0f, 0.01f);
    }

    [Fact]
    public void A_vehicle_at_rest_is_pushed_by_a_wind_along_its_negative_axis()
    {
        // VehicleDynamics.cs:928-935 takes the sign of a velocity of exactly 0 as positive, so Halcyon cut a wind on the
        // negative side as if the vehicle had already outrun it; a vehicle that is not moving gets the push either way.
        BehaviourRig still = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(1f, 0f, 0f), Blowing(-5f));
        still.Run(Step, 45 * 2);
        Assert.True(still.Body.LinearVelocity.X < -4f, $"{still.Body.LinearVelocity.X}");

        BehaviourRig astern = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(1f, 0f, 0f), Blowing(5f));
        astern.Run(Step, 45 * 2);
        Assert.True(astern.Body.LinearVelocity.X > 4f, $"{astern.Body.LinearVelocity.X}");

        BehaviourRig moving = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(1f, 0f, 0f), Blowing(-5f));
        moving.Body.LinearVelocity = new Vector3(-0.5f, 0f, 0f);
        moving.Run(Step, 45 * 2);
        Assert.True(moving.Body.LinearVelocity.X < -4f, $"{moving.Body.LinearVelocity.X}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // What does not change

    [Fact]
    public void With_neither_flag_set_nothing_is_read_and_nothing_moves()
    {
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, 0, new Vector3(1f, 1f, 1f), Blowing(10f, 10f), new Vector3(1f, 1f, 0f));
        BehaviourRig calm = Boat(VehicleBehaviour.InWorldz, 0, new Vector3(1f, 1f, 1f), null, new Vector3(1f, 1f, 0f));
        boat.Run(Step, 45 * 3);
        calm.Run(Step, 45 * 3);
        Assert.Equal(0, boat.Body.WindReads);
        Assert.Equal(Vector3.Zero, boat.Body.LinearVelocity);
        Assert.Equal(Vector3.Zero, boat.Body.AngularVelocity);
        Assert.Equal(calm.State(), boat.State());
    }

    [Fact]
    public void Under_Second_Life_a_sailboat_ignores_the_wind_bit_for_bit()
    {
        BehaviourRig windy = Sailboat(VehicleBehaviour.SecondLife, Blowing(15f, 5f));
        BehaviourRig calm = Sailboat(VehicleBehaviour.SecondLife, null);
        windy.Run(Step, 45 * 4);
        calm.Run(Step, 45 * 4);
        Assert.Equal(0, windy.Body.WindReads);
        Assert.Equal(calm.State(), windy.State());
        Assert.Equal(Vector3.Zero, windy.Body.LinearVelocity);
    }

    [Fact]
    public void Under_Second_Life_the_wind_efficiencies_cannot_be_set()
    {
        var rig = new BehaviourRig(Settings(VehicleBehaviour.SecondLife), Vehicle.TYPE_BOAT, gravity: false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(1f, 2f, 3f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_WIND_EFFICIENCY, new Vector3(1f, 2f, 3f));
        Assert.Equal(Vector3.Zero, rig.Car.GetVecParam(VehVectorParam.LinearWindEfficiency));
        Assert.Equal(Vector3.Zero, rig.Car.GetVecParam(VehVectorParam.AngularWindEfficiency));
    }

    [Fact]
    public void The_efficiencies_are_held_to_minus_ten_and_ten_on_each_axis_under_InWorldz()
    {
        var rig = new BehaviourRig(Settings(VehicleBehaviour.InWorldz), Vehicle.TYPE_BOAT, gravity: false);
        rig.Car.ProcessVectorVehicleParam(Vehicle.LINEAR_WIND_EFFICIENCY, new Vector3(50f, -50f, 3f));
        rig.Car.ProcessVectorVehicleParam(Vehicle.ANGULAR_WIND_EFFICIENCY, new Vector3(-11f, 0.5f, 10f));
        Assert.Equal(new Vector3(10f, -10f, 3f), rig.Car.GetVecParam(VehVectorParam.LinearWindEfficiency));
        Assert.Equal(new Vector3(-10f, 0.5f, 10f), rig.Car.GetVecParam(VehVectorParam.AngularWindEfficiency));
    }

    [Fact]
    public void With_no_wind_module_the_vehicle_behaves_as_if_there_were_no_wind()
    {
        BehaviourRig none = Boat(VehicleBehaviour.InWorldz, Wind | Currents, new Vector3(1f, 1f, 1f), null, new Vector3(1f, 1f, 0f));
        BehaviourRig still = Boat(VehicleBehaviour.InWorldz, Wind | Currents, new Vector3(1f, 1f, 1f), Blowing(0f), new Vector3(1f, 1f, 0f));
        none.Run(Step, 45 * 3);
        still.Run(Step, 45 * 3);
        Assert.True(none.Body.WindReads > 0, "it asked");
        Assert.Equal(Vector3.Zero, none.Body.LinearVelocity);
        Assert.Equal(still.State(), none.State());
    }

    [Fact]
    public void A_wind_that_is_not_a_number_pushes_nothing()
    {
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(1f, 1f, 1f), _ => new Vector3(float.NaN, float.PositiveInfinity, 0f));
        boat.Run(Step, 45);
        Assert.Equal(Vector3.Zero, boat.Body.LinearVelocity);
    }

    [Fact]
    public void The_wind_is_read_in_every_step()
    {
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(0.02f, 0f, 0f), Blowing(10f));
        boat.Run(Step, 64);
        Assert.Equal(64, boat.Body.WindReads);
    }

    [Fact]
    public void A_gust_that_ends_is_not_felt_in_the_steps_after_it()
    {
        float gust = 10f;
        BehaviourRig boat = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(0.5f, 0f, 0f), _ => new Vector3(gust, 0f, 0f));
        boat.Run(Step, 16);
        float before = boat.Body.LinearVelocity.X;
        Assert.True(before > 0.1f, "the gust pushed it");
        gust = 0f;
        boat.Run(Step, 4);                       // the wind is read in every step: the calm is felt at once, and nothing pushes
        Assert.Equal(before, boat.Body.LinearVelocity.X, 5);
        boat.Run(Step, 20);
        Assert.Equal(before, boat.Body.LinearVelocity.X, 5);
    }

    [Fact]
    public void A_vehicle_that_reacts_to_wind_never_idles_under_InWorldz_and_does_under_Second_Life()
    {
        BehaviourRig iw = Boat(VehicleBehaviour.InWorldz, Wind, new Vector3(1f, 0f, 0f), Blowing(0f));
        BehaviourRig cur = Boat(VehicleBehaviour.InWorldz, Currents, new Vector3(1f, 0f, 0f), Blowing(0f));
        BehaviourRig plain = Boat(VehicleBehaviour.InWorldz, 0, Vector3.Zero, null);
        BehaviourRig sl = Boat(VehicleBehaviour.SecondLife, Wind, new Vector3(1f, 0f, 0f), Blowing(0f));
        foreach (BehaviourRig r in new[] { iw, cur, plain, sl })
            r.Run(Step, 3);
        Assert.False(iw.Car.IsIdle);
        Assert.False(cur.Car.IsIdle);
        Assert.True(plain.Car.IsIdle);
        Assert.True(sl.Car.IsIdle);
    }

    [Fact]
    public void A_physical_object_that_is_not_a_vehicle_is_never_given_the_wind()
    {
        // The wind is read in the vehicle controller's step and nowhere else (Halcyon's PhysX scene reads it only for
        // vehicles), and a controller without a vehicle type does not step.
        BehaviourRig none = new(Settings(VehicleBehaviour.InWorldz), Vehicle.TYPE_NONE, gravity: false);
        none.Body.WindAt = Blowing(10f);
        none.Car.ProcessVehicleFlags(Wind | Currents, remove: false);
        none.Run(Step, 45);
        Assert.Equal(0, none.Body.WindReads);
        Assert.Equal(Vector3.Zero, none.Body.LinearVelocity);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The law, in numbers (VehicleDynamics.cs:921-992)

    [Fact]
    public void The_linear_law_scales_the_wind_by_efficiency_and_the_step()
    {
        Vector3 dv = HalcyonVehicleLaws.WindLinearVelocityChange(new Vector3(6f, -2f, 0f), new Vector3(1f, 0.5f, 0f), new Vector3(0.5f, 2f, 9f), 0.1f);
        Assert.Equal(0.3f, dv.X, 5);
        Assert.Equal(-0.4f, dv.Y, 5);
        Assert.Equal(0f, dv.Z, 5);
    }

    [Theory]
    [InlineData(4f, 5f, 0f)]       // moving with the wind faster than it: nothing
    [InlineData(4f, 4f, 4f)]       // exactly as fast: not yet cut (the vehicle must be faster)
    [InlineData(4f, 3f, 4f)]       // slower: the whole wind (scaled by the efficiency afterwards)
    [InlineData(4f, -1f, 4f)]      // against it: the whole wind
    [InlineData(-4f, -5f, 0f)]
    [InlineData(-4f, -3f, -4f)]
    [InlineData(-4f, 1f, -4f)]
    [InlineData(-4f, 0f, -4f)]     // at rest: the wind from either side pushes
    [InlineData(4f, 0f, 4f)]
    public void The_taper_cuts_the_wind_once_the_vehicle_has_outrun_it(float wind, float velocity, float expected)
    {
        Vector3 dv = HalcyonVehicleLaws.WindLinearVelocityChange(new Vector3(wind, 0f, 0f), new Vector3(velocity, 0f, 0f), Vector3.One, 1f);
        Assert.Equal(expected, dv.X, 5);
    }

    [Fact]
    public void A_push_shorter_than_the_least_physics_force_is_dropped()
    {
        Assert.Equal(Vector3.Zero, HalcyonVehicleLaws.WindLinearVelocityChange(new Vector3(0.0009f, 0f, 0f), Vector3.Zero, Vector3.One, 1f));
        Assert.NotEqual(Vector3.Zero, HalcyonVehicleLaws.WindLinearVelocityChange(new Vector3(0.0011f, 0f, 0f), Vector3.Zero, Vector3.One, 1f));
    }

    [Fact]
    public void The_angular_law_rolls_by_minus_the_y_wind_and_pitches_by_the_x_wind()
    {
        Vector3 dw = HalcyonVehicleLaws.WindAngularVelocityChange(new Vector3(4f, -6f, 9f), Vector3.Zero, new Vector3(0.5f, 0.25f, 1f), 0.1f);
        Assert.Equal(0.3f, dw.X, 5);
        Assert.Equal(0.1f, dw.Y, 5);
        Assert.Equal(0f, dw.Z, 5);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(-5f)]
    public void A_vehicle_not_turning_is_rolled_by_a_wind_along_its_y_axis_from_either_side(float windY)
    {
        Vector3 dw = HalcyonVehicleLaws.WindAngularVelocityChange(new Vector3(0f, windY, 0f), Vector3.Zero, Vector3.One, 1f);
        Assert.Equal(-windY, dw.X, 5);
    }

    [Fact]
    public void A_wind_that_just_matches_a_zero_speed_pushes_from_either_side()
    {
        Assert.Equal(-3f, HalcyonVehicleLaws.WindLinearVelocityChange(new Vector3(-3f, 0f, 0f), Vector3.Zero, Vector3.One, 1f).X, 5);
        Assert.Equal(3f, HalcyonVehicleLaws.WindLinearVelocityChange(new Vector3(3f, 0f, 0f), Vector3.Zero, Vector3.One, 1f).X, 5);
    }
}
