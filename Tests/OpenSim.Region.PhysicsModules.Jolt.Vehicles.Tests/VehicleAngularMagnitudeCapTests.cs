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
/// The cap on the angular motor direction. Second Life holds its magnitude to two rotations per second, 4 pi radians per
/// second (wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial, "Steering the Vehicle", and the
/// VEHICLE_ANGULAR_MOTOR_DIRECTION page). So under the Second
/// Life behaviour the vector is scaled down, direction kept, to a length of 4 pi. The InWorldz simulator holds each axis
/// to 4 pi instead (VehicleDynamics.cs:394-398), so a diagonal can be longer. Pure, parallel.
/// </summary>
public class VehicleAngularMagnitudeCapTests
{
    private const float Cap = 4f * MathF.PI;

    private static VehicleController Car(VehicleBehaviour behaviour)
    {
        var v = new VehicleController(new BehaviourBody()) { Settings = new VehicleSettings { Behaviour = behaviour } };
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        return v;
    }

    private static Vector3 Stored(VehicleController v) => v.GetVecParam(VehVectorParam.AngularMotorDirection);

    [Theory]
    [InlineData(10f, 10f, 10f)]
    [InlineData(12f, 12f, 0f)]
    [InlineData(0f, -20f, 20f)]
    [InlineData(100f, 1f, -2f)]
    public void Second_Life_holds_the_magnitude_and_keeps_the_direction(float x, float y, float z)
    {
        var v = Car(VehicleBehaviour.SecondLife);
        v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(x, y, z));
        Vector3 held = Stored(v);
        Assert.Equal(Cap, held.Length(), 3);
        Vector3 asked = new Vector3(x, y, z);
        Vector3 want = Vector3.Normalize(in asked);
        Vector3 heldDirection = Vector3.Normalize(in held);
        Assert.Equal(want.X, heldDirection.X, 4);
        Assert.Equal(want.Y, heldDirection.Y, 4);
        Assert.Equal(want.Z, heldDirection.Z, 4);
    }

    [Theory]
    [InlineData(1f, 2f, 3f)]
    [InlineData(12f, 0f, 0f)]
    [InlineData(0f, 0f, -12.5f)]
    [InlineData(7f, 7f, 0f)]       // length 9.9, inside
    public void Second_Life_leaves_a_direction_inside_the_cap_as_it_is(float x, float y, float z)
    {
        var v = Car(VehicleBehaviour.SecondLife);
        v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(x, y, z));
        Assert.Equal(new Vector3(x, y, z), Stored(v));
    }

    [Theory]
    [InlineData(10f, 10f, 10f, 10f, 10f, 10f)]
    [InlineData(100f, 1f, -2f, Cap, 1f, -2f)]
    [InlineData(-30f, 30f, 0f, -Cap, Cap, 0f)]
    public void InWorldz_holds_each_axis(float x, float y, float z, float ex, float ey, float ez)
    {
        var v = Car(VehicleBehaviour.InWorldz);
        v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(x, y, z));
        Assert.Equal(new Vector3(ex, ey, ez), Stored(v));
    }

    [Fact]
    public void The_scalar_form_is_held_by_the_same_rule()
    {
        var sl = Car(VehicleBehaviour.SecondLife);
        sl.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, 100f);
        Assert.Equal(Cap, Stored(sl).Length(), 3);
        Assert.Equal(Cap / MathF.Sqrt(3f), Stored(sl).X, 3);

        var iw = Car(VehicleBehaviour.InWorldz);
        iw.ProcessFloatVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, 100f);
        Assert.Equal(new Vector3(Cap, Cap, Cap), Stored(iw));
    }

    [Fact]
    public void A_saved_motor_direction_is_held_by_the_same_rule()
    {
        var sl = Car(VehicleBehaviour.SecondLife);
        sl.RestoreMotorDirections(Vector3.Zero, new Vector3(20f, 20f, 20f));
        Assert.Equal(Cap, Stored(sl).Length(), 3);

        var iw = Car(VehicleBehaviour.InWorldz);
        iw.RestoreMotorDirections(Vector3.Zero, new Vector3(20f, 20f, 20f));
        Assert.Equal(new Vector3(Cap, Cap, Cap), Stored(iw));
    }

    [Fact]
    public void The_key_sets_the_figure_of_the_cap()
    {
        var v = new VehicleController(new BehaviourBody())
            { Settings = new VehicleSettings { Behaviour = VehicleBehaviour.SecondLife, MaxAngularSpeed = 20f } };
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        v.ProcessVectorVehicleParam(Vehicle.ANGULAR_MOTOR_DIRECTION, new Vector3(30f, 30f, 30f));
        Assert.Equal(20f, Stored(v).Length(), 3);
    }
}
