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
/// Linear deflection turns a velocity toward the vehicle's nose axis. A vehicle that backs along that axis is already on it:
/// a sideways speed it picks up is turned out (toward the axis, the way it is travelling), and it is not turned round, which
/// would swing a reversing vehicle out sideways whenever a little sideways speed gets into it. Pure (a body with no engine), parallel.
/// </summary>
public class VehicleReverseDeflectionTests
{
    private static BehaviourRig Sled(VehicleBehaviour behaviour, Vector3 velocity, bool noDeflectionUp = false)
    {
        var rig = new BehaviourRig(new VehicleSettings { Behaviour = behaviour }, Vehicle.TYPE_SLED, gravity: false);
        rig.Flag(ExtendedVehicleFlags.NoDeflectionUp, noDeflectionUp);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 1f);
        rig.Car.ProcessFloatVehicleParam(Vehicle.LINEAR_DEFLECTION_TIMESCALE, 0.3f);
        rig.Body.LinearVelocity = velocity;
        return rig;
    }

    [Theory]
    [InlineData(VehicleBehaviour.InWorldz, 3f, 0.05f, false, 45.0)]
    [InlineData(VehicleBehaviour.InWorldz, 3f, -0.05f, false, 45.0)]
    [InlineData(VehicleBehaviour.SecondLife, 3f, 0.05f, false, 45.0)]
    [InlineData(VehicleBehaviour.SecondLife, 3f, -0.05f, false, 45.0)]
    [InlineData(VehicleBehaviour.SecondLife, 3f, 0.05f, false, 11.0)]
    [InlineData(VehicleBehaviour.SecondLife, 3f, -0.05f, false, 11.0)]
    [InlineData(VehicleBehaviour.SecondLife, 3f, 0.05f, true, 45.0)]
    [InlineData(VehicleBehaviour.SecondLife, 3f, 0.05f, true, 11.0)]
    public void A_vehicle_backing_along_its_nose_axis_does_not_swing_out_sideways(VehicleBehaviour behaviour, float speed, float sideways,
        bool noDeflectionUp, double rate)
    {
        BehaviourRig rig = Sled(behaviour, new Vector3(-speed, sideways, 0f), noDeflectionUp);
        rig.Run(1.0 / rate, (int)(3 * rate));
        Vector3 v = rig.Body.LinearVelocity;
        Assert.True(Math.Abs(v.Y) <= Math.Abs(sideways) + 1e-4f, $"the sideways speed grew from {sideways} to {v.Y}");
        Assert.True(v.X < -speed * 0.99f, $"the vehicle is no longer backing at its speed: velocity {v}");
    }

    [Theory]
    [InlineData(VehicleBehaviour.InWorldz, 3f)]
    [InlineData(VehicleBehaviour.InWorldz, -3f)]
    [InlineData(VehicleBehaviour.SecondLife, 3f)]
    public void A_vehicle_with_sideways_speed_is_still_turned_onto_its_axis(VehicleBehaviour behaviour, float ahead)
    {
        BehaviourRig rig = Sled(behaviour, new Vector3(ahead, 0.5f, 0f));
        rig.Run(1.0 / 45, 135);
        Vector3 v = rig.Body.LinearVelocity;
        Assert.True(Math.Abs(v.Y) < 0.2f, $"the sideways speed is still {v.Y}");
    }
}
