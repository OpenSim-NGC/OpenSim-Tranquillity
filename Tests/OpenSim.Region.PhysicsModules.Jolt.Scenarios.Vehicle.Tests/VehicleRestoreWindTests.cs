/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A vehicle whose script set the two wind efficiency vectors (llSetVehicleVectorParam with VEHICLE_LINEAR_WIND_EFFICIENCY, 12001,
// and VEHICLE_ANGULAR_WIND_EFFICIENCY, 12002), saved the way the simulator saves it and handed back by each way it hands a saved
// vehicle back to the engine (VehicleRestoreRouteTests lists them). The saved record keeps the vectors. Under
// [Jolt] VehicleBehaviour = InWorldz the vehicle ends with the values its script set; a record that holds none gives the
// vehicle its type's own; under SecondLife, which takes no such parameters, it ends as its script's vehicle was.
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class VehicleRestoreWindTests
{
    private readonly ITestOutputHelper _out;
    public VehicleRestoreWindTests(ITestOutputHelper output) => _out = output;

    private static readonly Vector3 LinearWind = new(0.3f, -0.2f, 0.7f);
    private static readonly Vector3 AngularWind = new(0.05f, 0.02f, 0f);

    private static void WindCalls(Run r)
    {
        r.SetVector(SharedVehicle.LINEAR_WIND_EFFICIENCY, LinearWind);
        r.SetVector(SharedVehicle.ANGULAR_WIND_EFFICIENCY, AngularWind);
    }

    public static IEnumerable<object[]> Routes()
        => new[] { VehicleRoute.Rez, VehicleRoute.Crossing, VehicleRoute.RegionStart, VehicleRoute.Copy }.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(Routes))]
    public void Under_InWorldz_the_wind_efficiencies_a_script_set_survive_each_route(VehicleRoute route)
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_SAILBOAT, route, moreScriptCalls: WindCalls, behaviour: "InWorldz");

        Assert.NotNull(o.Restored);
        // The script's vehicle holds them, the record keeps them, and the vehicle handed back holds them again.
        Assert.Equal("<0.3, -0.2, 0.7>", o.ScriptSet[nameof(VehVectorParam.LinearWindEfficiency)]);
        Assert.Equal("<0.05, 0.02, 0>", o.ScriptSet[nameof(VehVectorParam.AngularWindEfficiency)]);
        Assert.Equal(LinearWind, o.Saved.m_linearWindEfficiency);
        Assert.Equal(AngularWind, o.Saved.m_angularWindEfficiency);
        foreach (string key in new[] { nameof(VehVectorParam.LinearWindEfficiency), nameof(VehVectorParam.AngularWindEfficiency) })
        {
            _out.WriteLine($"{route} {key}: script {o.ScriptSet[key]}, restored {o.Restored[key]}");
            Assert.Equal(o.ScriptSet[key], o.Restored[key]);
        }
    }

    // A vehicle whose script set only one of them gets its type's own for the other.
    [Fact]
    public void Under_InWorldz_one_vector_set_leaves_the_types_own_for_the_other()
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_SAILBOAT, VehicleRoute.Rez,
            moreScriptCalls: r => r.SetVector(SharedVehicle.ANGULAR_WIND_EFFICIENCY, AngularWind), behaviour: "InWorldz");

        Assert.Null(o.Saved.m_linearWindEfficiency);
        Assert.Equal(AngularWind, o.Saved.m_angularWindEfficiency);
        Assert.Equal(o.FreshType[nameof(VehVectorParam.LinearWindEfficiency)], o.Restored[nameof(VehVectorParam.LinearWindEfficiency)]);
        Assert.Equal("<0.05, 0.02, 0>", o.Restored[nameof(VehVectorParam.AngularWindEfficiency)]);
    }

    // A record that holds none (every record saved before they were kept) restores the type's own, as it did.
    [Theory]
    [MemberData(nameof(Routes))]
    public void Under_InWorldz_a_record_with_no_wind_efficiency_restores_the_types_own(VehicleRoute route)
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_SAILBOAT, route, behaviour: "InWorldz");

        Assert.Null(o.Saved.m_linearWindEfficiency);
        Assert.Null(o.Saved.m_angularWindEfficiency);
        Assert.Equal("<0.02, 0.001, 0>", o.Restored[nameof(VehVectorParam.LinearWindEfficiency)]);
        Assert.Equal("<0.1, 0.01, 0>", o.Restored[nameof(VehVectorParam.AngularWindEfficiency)]);
        Assert.Equal(o.ScriptSet[nameof(VehVectorParam.LinearWindEfficiency)], o.Restored[nameof(VehVectorParam.LinearWindEfficiency)]);
    }

    // A zero vector a script set (wind off) is a value and is kept, not read as "none".
    [Fact]
    public void Under_InWorldz_a_zero_vector_a_script_set_is_kept()
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_SAILBOAT, VehicleRoute.Rez,
            moreScriptCalls: r => r.SetVector(SharedVehicle.LINEAR_WIND_EFFICIENCY, Vector3.Zero), behaviour: "InWorldz");

        Assert.Equal(Vector3.Zero, o.Saved.m_linearWindEfficiency);
        Assert.Equal("<0, 0, 0>", o.Restored[nameof(VehVectorParam.LinearWindEfficiency)]);
    }

    // Under SecondLife the two codes are not taken (as from a script): the vehicle handed back is the one its script set up.
    [Theory]
    [MemberData(nameof(Routes))]
    public void Under_SecondLife_a_restored_vehicle_is_the_one_its_script_set_up(VehicleRoute route)
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_SAILBOAT, route, moreScriptCalls: WindCalls, behaviour: "SecondLife");

        Assert.NotNull(o.Restored);
        Assert.Equal(LinearWind, o.Saved.m_linearWindEfficiency);   // the record has it; the engine takes none
        foreach (string key in new[] { nameof(VehVectorParam.LinearWindEfficiency), nameof(VehVectorParam.AngularWindEfficiency) })
        {
            _out.WriteLine($"{route} {key}: script {o.ScriptSet[key]}, restored {o.Restored[key]}");
            Assert.Equal(o.ScriptSet[key], o.Restored[key]);
        }
    }
}
