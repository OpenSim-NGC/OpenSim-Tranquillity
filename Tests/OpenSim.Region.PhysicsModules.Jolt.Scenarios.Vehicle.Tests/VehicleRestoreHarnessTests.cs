/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Vehicles handed back from their saved record (the harness's VehicleRoute, VehicleRestore.cs) drive the harness's
// courses, at the module's default physics step rate (45 Hz) and at one step per heartbeat, both with 11 Hz heartbeats.
//
// A vehicle handed back by any route drives the same course as the same vehicle set up by its script, heartbeat for
// heartbeat, when its record holds what the vehicle had. Each route is compared with itself followed by the script's own
// calls again (HarnessOptions.VehicleScriptAgain), so both bodies have the same history: the engine moves a body it is
// given after another was removed (a route that builds a new actor, or physics switched off and on, which rebuilds the
// body) slightly differently once it slows to rest, whatever the body is.
//
// The record holds what the vehicle had for a car and an airplane as their type alone sets them. For a sled, a boat and a
// balloon the simulator saves its own type presets (SOPVehicle), which differ from the controller's documented ones in a
// few fields (VehicleRestoreRouteTests lists them). So those three are driven twice: once with the script also setting
// those fields to the documented values, so the record holds what the vehicle had, and then every route gives the
// identical trace; and once with the type alone, where the vehicle handed back drives as the same vehicle whose script
// set the simulator's values in those fields does.
//
// The three flag calls do what they do on ubODE: set these flags, remove these flags, and with -1 remove every flag
// (and set every flag).
//
// Serial with the other native tests: every run steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using SharedVehicle = OpenSim.Region.PhysicsModules.SharedBase.Vehicle;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
public class VehicleRestoreHarnessTests
{
    private readonly ITestOutputHelper _out;
    public VehicleRestoreHarnessTests(ITestOutputHelper output) => _out = output;

    private const double Heartbeat = 11.0;

    private static readonly VehicleRoute[] Routes =
    {
        VehicleRoute.Rez, VehicleRoute.RegionStart, VehicleRoute.Copy, VehicleRoute.Crossing, VehicleRoute.Detach, VehicleRoute.PhysicsOffOn,
    };

    private static VehicleParamSetting Float(SharedVehicle code, float v) => new(code, new Vector3(v, 0f, 0f), false);

    // A script's own settings on top of the type: none, the documented value in each field where the simulator's saved
    // preset differs from it, or the simulator's value there.
    private static (List<VehicleParamSetting> p, List<VehicleFlagSetting> f) Settings(string scenario, string which)
    {
        var p = new List<VehicleParamSetting>();
        var f = new List<VehicleFlagSetting>();
        bool documented = which == "documented";
        if (which == "type")
            return (p, f);
        switch (scenario)
        {
            case "sled":
                p.Add(Float(SharedVehicle.HOVER_TIMESCALE, documented ? 1000f : 10f));
                p.Add(Float(SharedVehicle.ANGULAR_DEFLECTION_TIMESCALE, documented ? 10f : 1000f));
                p.Add(Float(SharedVehicle.VERTICAL_ATTRACTION_EFFICIENCY, documented ? 1f : 0f));
                break;
            case "boat":
                f.Add(new VehicleFlagSetting((int)VehicleFlag.HOVER_UP_ONLY, !documented));
                break;
            case "balloon":
                p.Add(Float(SharedVehicle.VERTICAL_ATTRACTION_EFFICIENCY, documented ? 1f : 0f));
                f.Add(new VehicleFlagSetting((int)(VehicleFlag.LIMIT_ROLL_ONLY | VehicleFlag.HOVER_GLOBAL_HEIGHT), documented));
                break;
        }
        return (p, f);
    }

    private static RunResult Drive(string scenario, double physicsRate, VehicleRoute route, string settings, bool scriptAgain = false, bool warmUp = true)
    {
        var o = new HarnessOptions { RateHz = Heartbeat, PhysicsRateHz = physicsRate, VehicleRoute = route, VehicleScriptAgain = scriptAgain,
                                     WarmUpStep = warmUp }.SecondLife();
        (List<VehicleParamSetting> p, List<VehicleFlagSetting> f) = Settings(scenario, settings);
        o.VehicleParams.AddRange(p);
        o.VehicleFlags.AddRange(f);
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    // Every route, against the same vehicle set up by its script: the same trace, heartbeat for heartbeat.
    [Theory]
    [InlineData("testcar", 45.0, "type")]
    [InlineData("testcar", 0.0, "type")]
    [InlineData("carturn", 45.0, "type")]
    [InlineData("carturn", 0.0, "type")]
    [InlineData("airplane", 45.0, "type")]
    [InlineData("airplane", 0.0, "type")]
    [InlineData("sled", 45.0, "documented")]
    [InlineData("sled", 0.0, "documented")]
    [InlineData("boat", 45.0, "documented")]
    [InlineData("boat", 0.0, "documented")]
    [InlineData("balloon", 45.0, "documented")]
    [InlineData("balloon", 0.0, "documented")]
    public void A_vehicle_handed_back_by_any_route_drives_the_course_as_its_script_set_it_up(string scenario, double physicsRate, string settings)
    {
        _out.WriteLine($"{scenario} p{physicsRate} as set up: {Drive(scenario, physicsRate, VehicleRoute.None, settings).SummaryLine()}");
        foreach (VehicleRoute route in Routes)
        {
            RunResult script = Drive(scenario, physicsRate, route, settings, scriptAgain: true);
            RunResult restored = Drive(scenario, physicsRate, route, settings);
            Assert.True(script.Samples.Count > 10);
            Assert.True(script.ToCsv() == restored.ToCsv(), $"{scenario} p{physicsRate} by {route}: the trace differs");
            Assert.Equal(script.SummaryLine(), restored.SummaryLine());
        }
    }

    // A sled, a boat and a balloon with their type alone: handed back by any route, each drives exactly as the same vehicle
    // whose script also set the simulator's preset values where they differ from the documented ones, since that is what
    // their record holds.
    [Theory]
    [InlineData("sled", 45.0)]
    [InlineData("sled", 0.0)]
    [InlineData("boat", 45.0)]
    [InlineData("boat", 0.0)]
    [InlineData("balloon", 45.0)]
    [InlineData("balloon", 0.0)]
    public void A_sled_boat_or_balloon_handed_back_drives_as_its_saved_record(string scenario, double physicsRate)
    {
        _out.WriteLine($"{scenario} p{physicsRate} set up by its script, type alone:  {Drive(scenario, physicsRate, VehicleRoute.None, "type").SummaryLine()}");
        _out.WriteLine($"{scenario} p{physicsRate} set up with the simulator's preset: {Drive(scenario, physicsRate, VehicleRoute.None, "saved").SummaryLine()}");
        foreach (VehicleRoute route in Routes.Where(r => r != VehicleRoute.PhysicsOffOn))
        {
            RunResult restored = Drive(scenario, physicsRate, route, "type");
            RunResult asSaved = Drive(scenario, physicsRate, route, "saved", scriptAgain: true);
            RunResult typeOnly = Drive(scenario, physicsRate, route, "type", scriptAgain: true);
            Assert.True(asSaved.ToCsv() == restored.ToCsv(), $"{scenario} p{physicsRate} by {route}: the trace differs from its record's");
            Assert.Equal(asSaved.SummaryLine(), restored.SummaryLine());
            // And not as the same vehicle with the type alone: the record's values act.
            Assert.False(typeOnly.ToCsv() == restored.ToCsv(), $"{scenario} p{physicsRate} by {route}: drives as the type alone");
        }
        // Physics off and on keeps the vehicle as it was (no record is read): the type alone.
        RunResult switched = Drive(scenario, physicsRate, VehicleRoute.PhysicsOffOn, "type");
        RunResult switchedScript = Drive(scenario, physicsRate, VehicleRoute.PhysicsOffOn, "type", scriptAgain: true);
        Assert.True(switchedScript.ToCsv() == switched.ToCsv(), $"{scenario} p{physicsRate}: physics off and on changed the vehicle");
    }

    // In a running region a sled handed back falls as a body does, and so does the same sled with its script run again: a
    // type call after the region's first step holds nothing still (JoltPrim.VehicleType). With the region loading both are
    // held, and the traces are the same. (VehicleTypeCallHoldTests holds the fall itself, 40 m up.)
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_sled_handed_back_in_a_running_region_falls_as_one_a_script_set_does(double physicsRate)
    {
        RunResult restored = Drive("sled", physicsRate, VehicleRoute.Rez, "documented", warmUp: true);
        RunResult script = Drive("sled", physicsRate, VehicleRoute.Rez, "documented", scriptAgain: true, warmUp: true);
        Assert.True(Math.Abs(restored.Samples[1].Velocity.Z - script.Samples[1].Velocity.Z) < 0.05f,
            $"sled p{physicsRate}: restored {restored.Samples[1].Velocity.Z:0.000} m/s, script {script.Samples[1].Velocity.Z:0.000}");
        // And with the region loading, both are zeroed, and the traces are the same.
        Assert.Equal(Drive("sled", physicsRate, VehicleRoute.Rez, "documented", scriptAgain: true, warmUp: false).ToCsv(),
                     Drive("sled", physicsRate, VehicleRoute.Rez, "documented", warmUp: false).ToCsv());
    }

    // The flags a car ends with after the calls (a car alone has NoDeflectionUp, LimitRollOnly, HoverUpOnly, LimitMotorUp).
    private static uint FlagsAfter(double physicsRate, params (int flags, bool remove)[] calls)
    {
        uint seen = 0;
        var sc = new Scenario
        {
            Name = "vehicle-flags",
            DefaultDuration = _ => (float)(1.0 / Heartbeat),
            Setup = r =>
            {
                r.AddBox(new Vector3(2f, 1f, 0.5f), new Vector3(128f, 128f, Course.Ground + 0.27f), Quaternion.Identity);
                r.MakeVehicle(SharedVehicle.TYPE_CAR);
                foreach ((int flags, bool remove) in calls)
                    r.SetFlags(flags, remove);
                seen = (uint)((JoltPrim)r.Actor).VehicleControl.Flags;
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = Heartbeat, PhysicsRateHz = physicsRate }.SecondLife());
        return seen;
    }

    private const uint CarFlags = 0x1 | 0x2 | 0x20 | 0x40;

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Each_flag_call_does_what_it_does_on_ubODE(double physicsRate)
    {
        // Set these flags: the car's own stay.
        Assert.Equal(CarFlags | 0x8 | 0x400, FlagsAfter(physicsRate, ((int)(VehicleFlag.HOVER_TERRAIN_ONLY | VehicleFlag.NO_X), false)));
        // Remove these flags: only those go.
        Assert.Equal(0x1u | 0x2u, FlagsAfter(physicsRate, ((int)(VehicleFlag.HOVER_UP_ONLY | VehicleFlag.LIMIT_MOTOR_UP), true)));
        // Remove with -1: every flag goes, also one the script set.
        Assert.Equal(0u, FlagsAfter(physicsRate, ((int)VehicleFlag.MOUSELOOK_STEER, false), (-1, true)));
        // Set with -1: every flag is set, as on ubODE.
        Assert.Equal(0xFFFFFFFFu, FlagsAfter(physicsRate, (-1, true), (-1, false)));
    }

    private static RunResult DriveWithFlags(double physicsRate, params (int flags, bool remove)[] calls)
    {
        var o = new HarnessOptions { RateHz = Heartbeat, PhysicsRateHz = physicsRate }.SecondLife();
        foreach ((int flags, bool remove) in calls)
            o.VehicleFlags.Add(new VehicleFlagSetting(flags, remove));
        return Harness.Harness.Run(Harness.Harness.Find("carturn"), o);
    }

    // The same calls made one flag at a time drive the same course: a call acts on exactly the flags it names, and -1 on
    // every flag.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_flag_call_drives_as_the_same_flags_one_at_a_time(double physicsRate)
    {
        // Set these flags.
        Assert.Equal(DriveWithFlags(physicsRate, (0x8 | 0x80, false)).ToCsv(), DriveWithFlags(physicsRate, (0x8, false), (0x80, false)).ToCsv());
        // Remove these flags.
        Assert.Equal(DriveWithFlags(physicsRate, (0x2 | 0x40, true)).ToCsv(), DriveWithFlags(physicsRate, (0x2, true), (0x40, true)).ToCsv());
        // Remove every flag: as removing each of the car's own.
        RunResult none = DriveWithFlags(physicsRate, (-1, true));
        Assert.Equal(none.ToCsv(), DriveWithFlags(physicsRate, (0x1, true), (0x2, true), (0x20, true), (0x40, true)).ToCsv());
        // And that differs from the car with its flags: the calls act.
        Assert.NotEqual(none.ToCsv(), DriveWithFlags(physicsRate).ToCsv());
        // Set every flag: as setting each flag the controller knows (bits 0 to 21).
        (int, bool)[] each = Enumerable.Range(0, 22).Select(b => (1 << b, false)).ToArray();
        Assert.Equal(DriveWithFlags(physicsRate, (-1, false)).ToCsv(), DriveWithFlags(physicsRate, each).ToCsv());
    }

    [Fact]
    public void The_option_is_off_by_default()
    {
        Assert.Equal(VehicleRoute.None, new HarnessOptions().SecondLife().VehicleRoute);
    }
}
