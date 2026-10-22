/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A parked vehicle sleeps, and a sleeping vehicle reads no camera. The InWorldz simulator therefore sets the sleep
/// threshold of a vehicle with a mouselook flag to 0 so that it never sleeps (VehicleDynamics.cs:784-789: "a minor hack
/// ... plumb mouselook ... events to communicate with the object at all times"). Here a vehicle with
/// VEHICLE_FLAG_MOUSELOOK_STEER or _BANK is not idle, so the host does not let it sleep. Second Life's pages say nothing
/// about sleeping. Pure, parallel.
/// </summary>
public class VehicleMouselookSleepTests
{
    public static IEnumerable<object[]> Behaviours()
    {
        yield return new object[] { VehicleBehaviour.SecondLife };
        yield return new object[] { VehicleBehaviour.InWorldz };
    }

    private static VehicleController Car(VehicleBehaviour behaviour)
    {
        var v = new VehicleController(new BehaviourBody()) { Settings = new VehicleSettings { Behaviour = behaviour } };
        v.ProcessTypeChange(Vehicle.TYPE_CAR);
        return v;
    }

    [Theory]
    [MemberData(nameof(Behaviours))]
    public void A_parked_car_with_no_mouselook_flag_is_idle(VehicleBehaviour b)
    {
        Assert.True(Car(b).IsIdle);
    }

    [Theory]
    [MemberData(nameof(Behaviours))]
    public void A_parked_car_with_mouselook_steer_is_not_idle(VehicleBehaviour b)
    {
        VehicleController v = Car(b);
        v.ProcessVehicleFlags(0x80, remove: false);
        Assert.False(v.IsIdle);
        Assert.False(v.HoldsAtRest);
    }

    [Theory]
    [MemberData(nameof(Behaviours))]
    public void A_parked_car_with_mouselook_bank_is_not_idle(VehicleBehaviour b)
    {
        VehicleController v = Car(b);
        v.ProcessVehicleFlags(0x100, remove: false);
        Assert.False(v.IsIdle);
    }

    [Theory]
    [MemberData(nameof(Behaviours))]
    public void Clearing_the_flag_lets_it_sleep_again(VehicleBehaviour b)
    {
        VehicleController v = Car(b);
        v.ProcessVehicleFlags(0x180, remove: false);
        v.ProcessVehicleFlags(0x180, remove: true);
        Assert.True(v.IsIdle);
    }

    [Theory]
    [MemberData(nameof(Behaviours))]
    public void Another_flag_does_not_keep_it_awake(VehicleBehaviour b)
    {
        VehicleController v = Car(b);
        v.ProcessVehicleFlags(0x1 | 0x2 | 0x20 | 0x40 | 0x200, remove: false);
        Assert.True(v.IsIdle);
    }
}
