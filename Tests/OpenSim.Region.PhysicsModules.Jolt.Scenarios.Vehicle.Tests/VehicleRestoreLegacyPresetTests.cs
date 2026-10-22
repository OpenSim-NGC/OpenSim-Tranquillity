/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A saved vehicle handed back where the region's presets are the InWorldz ones ([Jolt] VehicleBehaviour = InWorldz, or
// VehiclePresets = legacy). The simulator's saved record (SOPVehicle) holds the Second Life preset of the type plus
// what the script changed, one value per motor timescale and decay; a vehicle set up by script holds the InWorldz
// preset plus what the script changed, with a per-axis value where the preset has one (the motorcycle's linear motor
// timescale is (0.5, 1, 1)). A restore must end with the vehicle its script made: the InWorldz preset, with the
// record's values only where they differ from the simulator's preset for the type (VehicleRestoreRouteTests builds the
// vehicle, saves it and hands it back by each route).
//
// Under Second Life behaviour with the documented presets nothing changes: the vehicle gets exactly its record.
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;
using OpenSim.Region.PhysicsModules.SharedBase;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.VehicleRestoreRouteTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class VehicleRestoreLegacyPresetTests
{
    // InWorldz's two extra types, as a script names them (VEHICLE_TYPE_SAILBOAT, VEHICLE_TYPE_MOTORCYCLE).
    private const SharedVehicle Sailboat = (SharedVehicle)10001, Motorcycle = (SharedVehicle)10002;

    private readonly ITestOutputHelper _out;
    public VehicleRestoreLegacyPresetTests(ITestOutputHelper output) => _out = output;

    private static void InWorldz(HarnessOptions o) => o.Jolt["VehicleBehaviour"] = "InWorldz";
    private static void LegacyPresetsOnly(HarnessOptions o) => o.Jolt["VehiclePresets"] = "legacy";
    private static void SecondLife(HarnessOptions o) => o.Jolt["VehicleBehaviour"] = "SecondLife";

    // The routes that read the record back and build a new actor from it.
    public static IEnumerable<object[]> Routes()
    {
        foreach (VehicleRoute route in new[] { VehicleRoute.Rez, VehicleRoute.RegionStart, VehicleRoute.Copy, VehicleRoute.Crossing })
            yield return new object[] { route };
    }

    public static IEnumerable<object[]> TypesAndRoutes(params SharedVehicle[] types)
    {
        foreach (SharedVehicle type in types)
            foreach (object[] route in Routes())
                yield return new object[] { type, route[0] };
    }

    public static IEnumerable<object[]> AllTypes() => TypesAndRoutes(SharedVehicle.TYPE_SLED, SharedVehicle.TYPE_CAR, SharedVehicle.TYPE_BOAT,
        SharedVehicle.TYPE_AIRPLANE, SharedVehicle.TYPE_BALLOON, Sailboat, Motorcycle);

    // Types the simulator has a preset for: a script can also remove a flag the preset has, which the record shows.
    public static IEnumerable<object[]> SimulatorTypes() => TypesAndRoutes(SharedVehicle.TYPE_CAR, SharedVehicle.TYPE_BOAT, SharedVehicle.TYPE_AIRPLANE);

    // What a script that changes values but removes no flag does: three floats, a vector and two flags that no simulator
    // preset sets (a flag a preset already has, set by a script, shows nothing in the record).
    private static void ScriptChanges(Run r, SharedVehicle type)
    {
        r.MakeVehicle(type);
        r.SetFlags((int)VehicleFlag.MOUSELOOK_BANK, false);
        r.SetFlags((int)VehicleFlag.HOVER_TERRAIN_ONLY, false);
        r.SetFloat(SharedVehicle.LINEAR_MOTOR_TIMESCALE, 0.7f);
        r.SetFloat(SharedVehicle.ANGULAR_MOTOR_DECAY_TIMESCALE, 2.5f);
        r.SetFloat(SharedVehicle.BANKING_EFFICIENCY, 0.4f);
        r.SetVector(SharedVehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(2f, 4f, 1000f));
    }

    private static void TypeOnly(Run r, SharedVehicle type) => r.MakeVehicle(type);

    private void AssertRestoredIsWhatTheScriptMade(Outcome o, string what)
    {
        Assert.NotNull(o.Restored);
        List<string> diff = Diff(o.ScriptSet, o.Restored);
        _out.WriteLine($"{what}: {diff.Count} differences from the vehicle its script made");
        foreach (string line in diff)
            _out.WriteLine("  " + line);
        Assert.Empty(diff);
    }

    // A vehicle whose script set nothing after the type gets the type's InWorldz preset back, whole.
    [Theory]
    [MemberData(nameof(AllTypes))]
    public void A_vehicle_with_only_its_type_restores_to_the_InWorldz_preset(SharedVehicle type, VehicleRoute route)
        => AssertRestoredIsWhatTheScriptMade(RunRoute(type, route, setup: TypeOnly, configure: InWorldz), $"{type} by {route}");

    // The same where only the presets are legacy, the behaviour the default.
    [Theory]
    [InlineData(SharedVehicle.TYPE_CAR, VehicleRoute.Rez)]
    [InlineData(SharedVehicle.TYPE_BOAT, VehicleRoute.RegionStart)]
    [InlineData(Motorcycle, VehicleRoute.Crossing)]
    [InlineData(Sailboat, VehicleRoute.Copy)]
    public void Legacy_presets_alone_restore_the_same_way(SharedVehicle type, VehicleRoute route)
        => AssertRestoredIsWhatTheScriptMade(RunRoute(type, route, setup: TypeOnly, configure: LegacyPresetsOnly), $"{type} by {route}");

    // Values the script changed are applied on the InWorldz preset: a float timescale to all three axes, as the script call did.
    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Script_changed_values_are_applied_on_the_InWorldz_preset(SharedVehicle type, VehicleRoute route)
        => AssertRestoredIsWhatTheScriptMade(RunRoute(type, route, setup: ScriptChanges, configure: InWorldz), $"{type} by {route}");

    // The existing setup of VehicleRestoreRouteTests (six flag calls, four floats, two vectors, a frame) on the types the
    // simulator has a preset for, flag removals included.
    [Theory]
    [MemberData(nameof(SimulatorTypes))]
    public void A_full_script_setup_restores_to_what_the_script_made(SharedVehicle type, VehicleRoute route)
        => AssertRestoredIsWhatTheScriptMade(RunRoute(type, route, configure: InWorldz), $"{type} by {route}");

    // The per-axis values the report named: the motorcycle's linear motor timescale (0.5, 1, 1) and the angular decay.
    [Fact]
    public void The_motorcycle_keeps_its_per_axis_timescales_through_a_restore()
    {
        Outcome o = RunRoute(Motorcycle, VehicleRoute.Rez, setup: TypeOnly, configure: InWorldz);
        Assert.Equal("<0.5, 1, 1>", o.Restored[nameof(VehVectorParam.LinearMotorTimescale)]);
        Assert.Equal("<10, 1, 1>", o.Restored[nameof(VehVectorParam.LinearMotorDecayTimescale)]);
        Assert.Equal("<0.1, 0.1, 0.05>", o.Restored[nameof(VehVectorParam.AngularMotorTimescale)]);
        Assert.Equal("<0.2, 0.8, 0.1>", o.Restored[nameof(VehVectorParam.AngularMotorDecayTimescale)]);
    }

    // A timescale the script did set goes to all three axes, as Halcyon does when a script sets that float.
    [Fact]
    public void A_timescale_the_script_set_goes_to_all_three_axes()
    {
        Outcome o = RunRoute(Motorcycle, VehicleRoute.Rez, setup: ScriptChanges, configure: InWorldz);
        Assert.Equal("<0.7, 0.7, 0.7>", o.Restored[nameof(VehVectorParam.LinearMotorTimescale)]);
        Assert.Equal("<2.5, 2.5, 2.5>", o.Restored[nameof(VehVectorParam.AngularMotorDecayTimescale)]);
        Assert.Equal("<0.1, 0.1, 0.05>", o.Restored[nameof(VehVectorParam.AngularMotorTimescale)]);
    }

    // The same script changes plus a flag the types' presets lack being set.
    private static void ScriptChangesAndFlagSet(Run r, SharedVehicle type)
    {
        ScriptChanges(r, type);
        r.SetFlags((int)VehicleFlag.MOUSELOOK_STEER, true);
    }

    public static IEnumerable<object[]> InWorldzOnlyTypes() => TypesAndRoutes(Sailboat, Motorcycle);

    // Second Life behaviour with the documented presets, the two types the simulator has no preset for: a fresh
    // llSetVehicleType gives them the InWorldz preset under every behaviour, so a restore must too.
    [Theory]
    [MemberData(nameof(InWorldzOnlyTypes))]
    public void Under_Second_Life_behaviour_an_InWorldz_only_type_with_only_its_type_restores_to_the_preset(SharedVehicle type, VehicleRoute route)
        => AssertRestoredIsWhatTheScriptMade(RunRoute(type, route, setup: TypeOnly, configure: SecondLife), $"{type} by {route}");

    [Theory]
    [MemberData(nameof(InWorldzOnlyTypes))]
    public void Under_Second_Life_behaviour_an_InWorldz_only_type_keeps_what_the_script_changed(SharedVehicle type, VehicleRoute route)
        => AssertRestoredIsWhatTheScriptMade(RunRoute(type, route, setup: ScriptChanges, configure: SecondLife), $"{type} by {route}");

    [Theory]
    [MemberData(nameof(InWorldzOnlyTypes))]
    public void Under_Second_Life_behaviour_an_InWorldz_only_type_keeps_a_flag_the_script_set(SharedVehicle type, VehicleRoute route)
        => AssertRestoredIsWhatTheScriptMade(RunRoute(type, route, setup: ScriptChangesAndFlagSet, configure: SecondLife), $"{type} by {route}");

    [Fact]
    public void Under_Second_Life_behaviour_the_motorcycle_keeps_per_axis_timescales_and_a_script_timescale_goes_to_all_axes()
    {
        Outcome o = RunRoute(Motorcycle, VehicleRoute.Rez, setup: TypeOnly, configure: SecondLife);
        Assert.Equal("<0.5, 1, 1>", o.Restored[nameof(VehVectorParam.LinearMotorTimescale)]);
        o = RunRoute(Motorcycle, VehicleRoute.Rez, setup: ScriptChanges, configure: SecondLife);
        Assert.Equal("<0.7, 0.7, 0.7>", o.Restored[nameof(VehVectorParam.LinearMotorTimescale)]);
        Assert.Equal("<0.1, 0.1, 0.05>", o.Restored[nameof(VehVectorParam.AngularMotorTimescale)]);
    }

    // Second Life behaviour with the documented presets: the vehicle gets exactly its record, as before.
    [Theory]
    [InlineData(SharedVehicle.TYPE_CAR, VehicleRoute.Rez)]
    [InlineData(SharedVehicle.TYPE_CAR, VehicleRoute.RegionStart)]
    [InlineData(SharedVehicle.TYPE_BOAT, VehicleRoute.Copy)]
    [InlineData(SharedVehicle.TYPE_BOAT, VehicleRoute.Crossing)]
    public void Under_Second_Life_behaviour_a_restore_holds_exactly_the_record(SharedVehicle type, VehicleRoute route)
    {
        foreach (Action<Run, SharedVehicle> setup in new Action<Run, SharedVehicle>[] { TypeOnly, ScriptChanges, null })
        {
            Outcome o = RunRoute(type, route, setup: setup, configure: SecondLife);
            Assert.NotNull(o.Restored);
            List<string> diff = Diff(Expected(o.Saved, o.FreshType), o.Restored);
            foreach (string line in diff)
                _out.WriteLine($"{type} by {route}: {line}");
            Assert.Empty(diff);
        }
    }
}
