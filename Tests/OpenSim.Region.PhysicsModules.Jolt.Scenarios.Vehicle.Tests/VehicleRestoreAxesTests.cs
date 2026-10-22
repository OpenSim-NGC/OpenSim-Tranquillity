/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A vehicle whose script set a value for each axis of the motor timescales (llSetVehicleVectorParam with the four motor
// timescale codes) and a decay above 120 s, saved the way the simulator saves it and handed back by each way it hands a saved
// vehicle back to the engine (VehicleRestoreRouteTests lists them). The saved record keeps the per-axis values beside the
// single ones and the decay as sent. Under [Jolt] VehicleBehaviour = InWorldz the vehicle ends with the values its script set;
// under SecondLife, which takes no vector for these four and holds a decay to 120 s, it ends exactly as its script's vehicle was.
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
public class VehicleRestoreAxesTests
{
    private readonly ITestOutputHelper _out;
    public VehicleRestoreAxesTests(ITestOutputHelper output) => _out = output;

    private static readonly Vector3 LinearTs = new(0.5f, 2f, 8f);
    private static readonly Vector3 LinearDecay = new(600f, 30f, 5f);
    private static readonly Vector3 AngularTs = new(0.1f, 0.4f, 3f);

    // The script's calls on top of VehicleRestoreRouteTests.ScriptSetup: three vectors, and a decay of 600 s as a single value.
    private static void AxisCalls(Run r)
    {
        r.SetVector(SharedVehicle.LINEAR_MOTOR_TIMESCALE, LinearTs);
        r.SetVector(SharedVehicle.LINEAR_MOTOR_DECAY_TIMESCALE, LinearDecay);
        r.SetVector(SharedVehicle.ANGULAR_MOTOR_TIMESCALE, AngularTs);
        r.SetFloat(SharedVehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 600f);
    }

    public static IEnumerable<object[]> Routes()
        => new[] { VehicleRoute.Rez, VehicleRoute.Crossing, VehicleRoute.RegionStart, VehicleRoute.Copy }.Select(r => new object[] { r });

    // The vehicle's values after each route are what its script set, under InWorldz.
    [Theory]
    [MemberData(nameof(Routes))]
    public void Under_InWorldz_per_axis_timescales_and_a_decay_of_600_survive_each_route(VehicleRoute route)
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_CAR, route, moreScriptCalls: AxisCalls, behaviour: "InWorldz");

        Assert.NotNull(o.Restored);
        // The script's vehicle holds the per-axis values...
        Assert.Equal("<0.5, 2, 8>", o.ScriptSet[nameof(VehVectorParam.LinearMotorTimescale)]);
        Assert.Equal("<600, 30, 5>", o.ScriptSet[nameof(VehVectorParam.LinearMotorDecayTimescale)]);
        Assert.Equal("<0.1, 0.4, 3>", o.ScriptSet[nameof(VehVectorParam.AngularMotorTimescale)]);
        Assert.Equal("<600, 600, 600>", o.ScriptSet[nameof(VehVectorParam.AngularMotorDecayTimescale)]);
        // ...the record keeps them and the decay as sent...
        Assert.Equal(LinearTs, o.Saved.m_linearMotorTimescaleAxes);
        Assert.Equal(LinearDecay, o.Saved.m_linearMotorDecayTimescaleAxes);
        Assert.Equal(AngularTs, o.Saved.m_angularMotorTimescaleAxes);
        Assert.Equal(600f, o.Saved.m_angularMotorDecayTimescale);
        // ...and the vehicle handed back holds them again.
        Assert.Equal("<0.5, 2, 8>", o.Restored[nameof(VehVectorParam.LinearMotorTimescale)]);
        Assert.Equal("<600, 30, 5>", o.Restored[nameof(VehVectorParam.LinearMotorDecayTimescale)]);
        Assert.Equal("<0.1, 0.4, 3>", o.Restored[nameof(VehVectorParam.AngularMotorTimescale)]);
        Assert.Equal("<600, 600, 600>", o.Restored[nameof(VehVectorParam.AngularMotorDecayTimescale)]);
        // The four motor timescale settings, as the controller holds them, are what the script's vehicle had. (The rest of the
        // vehicle is VehicleRestoreRouteTests's: a restored vehicle holds the region's limit on the hover timescale, which the
        // script's did not, and so on.)
        foreach (string key in new[] { nameof(VehVectorParam.LinearMotorTimescale), nameof(VehVectorParam.LinearMotorDecayTimescale),
                                       nameof(VehVectorParam.AngularMotorTimescale), nameof(VehVectorParam.AngularMotorDecayTimescale) })
        {
            _out.WriteLine($"{route} {key}: script {o.ScriptSet[key]}, restored {o.Restored[key]}");
            Assert.Equal(o.ScriptSet[key], o.Restored[key]);
        }
    }

    // Under SecondLife the four vectors are not taken (as from a script), and a decay is held to 120 s: the vehicle handed back
    // is the vehicle its script set up.
    [Theory]
    [MemberData(nameof(Routes))]
    public void Under_SecondLife_a_restored_vehicle_is_the_one_its_script_set_up(VehicleRoute route)
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_CAR, route, moreScriptCalls: AxisCalls, behaviour: "SecondLife");

        Assert.NotNull(o.Restored);
        Assert.Equal("<120, 120, 120>", o.ScriptSet[nameof(VehVectorParam.AngularMotorDecayTimescale)]);
        // The record has the values; the engine takes none of them.
        Assert.Equal(LinearTs, o.Saved.m_linearMotorTimescaleAxes);
        Assert.Equal(600f, o.Saved.m_angularMotorDecayTimescale);
        Assert.Equal(o.ScriptSet[nameof(VehVectorParam.LinearMotorTimescale)], o.Restored[nameof(VehVectorParam.LinearMotorTimescale)]);
        Assert.Equal("<120, 120, 120>", o.Restored[nameof(VehVectorParam.AngularMotorDecayTimescale)]);
        // The four motor timescale settings, as the controller holds them, are what the script's vehicle had. (The rest of the
        // vehicle is VehicleRestoreRouteTests's: a restored vehicle holds the region's limit on the hover timescale, which the
        // script's did not, and so on.)
        foreach (string key in new[] { nameof(VehVectorParam.LinearMotorTimescale), nameof(VehVectorParam.LinearMotorDecayTimescale),
                                       nameof(VehVectorParam.AngularMotorTimescale), nameof(VehVectorParam.AngularMotorDecayTimescale) })
        {
            _out.WriteLine($"{route} {key}: script {o.ScriptSet[key]}, restored {o.Restored[key]}");
            Assert.Equal(o.ScriptSet[key], o.Restored[key]);
        }
    }

    // A record with no per-axis value restores the single values as it did, here under InWorldz as well (the InWorldz preset's
    // own per-axis values are the preset-then-differences restore's, VehicleRestoreLegacyPresetTests, not this).
    [Fact]
    public void Under_InWorldz_a_record_with_no_per_axis_value_restores_the_single_values()
    {
        VehicleRestoreRouteTests.Outcome o = VehicleRestoreRouteTests.RunRoute(SharedVehicle.TYPE_CAR, VehicleRoute.Rez, behaviour: "InWorldz");

        Assert.Null(o.Saved.m_linearMotorTimescaleAxes);
        Assert.Equal("<0.5, 0.5, 0.5>", o.Restored[nameof(VehVectorParam.LinearMotorTimescale)]);
        Assert.Equal("<2, 2, 2>", o.Restored[nameof(VehVectorParam.LinearMotorDecayTimescale)]);
    }
}
