/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The harness builds a run the way a simulator builds a region, so a pass in the harness is not a pass that a region
// would fail. Each test here is a way the harness used to differ, and fails on the harness as it was:
//   - the mesher: a region hands the physics module a Meshmerizer loaded in a load context of its own (a second copy of
//     the class), where the earlier harness gave it none, or the module's own class (see RegionMesher);
//   - the region-load window: a prim rezzed or an avatar arriving in a running region is added after the first step, not
//     before it;
//   - the [Jolt] settings: a simulator reads RegionServerDefaults.ini before its own ini.
// The prim and avatar values (SimulatorDefaultsHarnessTests) and the phantom route have their own cases. Serial with the
// other native tests: every run steps a real backend on the shared job pool.

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class HarnessBuildsLikeSimulatorTests
{
    private const byte Prim = (byte)PhysShapeType.prim;
    private const uint HollowBoxId = 5301;

    private static RunResult RunScenario(string name, Action<Run> setup, Action<Run> input, HarnessOptions options, float seconds = 4f)
    {
        // The run reads the scenario's own object each heartbeat, so a setup that adds none gets a small box out of the way.
        var sc = new Scenario
        {
            Name = name,
            DefaultDuration = _ => seconds,
            Setup = r =>
            {
                setup(r);
                if (r.Actor == null)
                    r.AddBox(new Vector3(0.5f), new Vector3(20f, 20f, Course.Ground + 40f), Quaternion.Identity);
            },
            Input = input ?? (_ => { }),
        };
        options.RateHz = 11.0;
        options.PhysicsRateHz = 0;
        return Harness.Harness.Run(sc, options);
    }

    private static string KindOf(PhysicsActor a) => ((JoltPrim)a).ShapeKind;

    [Fact]
    public void Every_way_the_harness_builds_a_run_is_the_simulators_unless_it_is_turned_off()
    {
        var o = new HarnessOptions();
        Assert.True(o.SimulatorPrims && o.SimulatorAvatars && o.SimulatorDefaults);
        Assert.True(o.RegionMesher);
        Assert.True(o.WarmUpStep);
        Assert.True(o.ShippedConfig);
    }

    // ------------------------------------------------------------------ the mesher

    [Fact]
    public void The_scenes_mesher_is_a_Meshmerizer_of_a_load_context_of_its_own()
    {
        IMesher seen = null;
        RunScenario("harness-mesher", r => seen = r.Mesher, null, new HarnessOptions(), 0.2f);
        Assert.NotNull(seen);
        Assert.NotSame(typeof(Meshing.Meshmerizer), seen.GetType());   // a second copy of the class, as in a region
        Assert.Equal(typeof(Meshing.Meshmerizer).FullName, seen.GetType().FullName);
        Assert.False(seen is Meshing.Meshmerizer);
        Assert.IsAssignableFrom<IMesherShapeData>(seen);   // the interface is what reaches it across the two contexts

        IMesher none = seen;
        RunScenario("harness-no-mesher", r => none = r.Mesher, null, new HarnessOptions { RegionMesher = false }, 0.2f);
        Assert.Null(none);
    }

    // A 1 m hollow box (a 0.5 m hole up and down through it) on the ground, and a 0.2 m cube let go over the hole. With the
    // mesher a region has, the box is split into convex cells and the cube falls in and rests on the ground inside; with a
    // mesher the module does not recognise, or none, the box is one hull (or its bounding box) and the cube rests on top.
    private static (string kind, float restHeight) HollowBoxAndCube(HarnessOptions options)
    {
        string kind = null;
        PhysicsActor box = null;
        RunResult r = RunScenario("harness-hollow-box", run =>
        {
            box = run.AddPart(PrimHullProbeTests.Shapes().Single(s => s.Name == "hollow box 0.5").Build(), Vector3.One,
                new Vector3(CentreX, CentreY, Course.Ground + 0.505f), Quaternion.Identity, true, HollowBoxId);
            run.AddBox(new Vector3(0.2f), new Vector3(CentreX + 0.04f, CentreY - 0.03f, Course.Ground + 1f + 0.15f + 0.1f), Quaternion.Identity);
        }, run => { if (run.Heartbeats == 2) kind = KindOf(box); }, options, 4f);
        return (kind, r.Samples[^1].Position.Z - Course.Ground);
    }

    [Fact]
    public void A_hollow_physical_prim_is_split_into_cells_with_the_mesher_the_harness_gives_the_scene()
    {
        (string kind, float rest) = HollowBoxAndCube(new HarnessOptions());
        Assert.StartsWith(JoltScene.PrimCellsKind, kind, StringComparison.Ordinal);
        Assert.InRange(rest, 0.05f, 0.13f);   // in the hole (a 0.2 m cube's centre 0.1 m up, a little less where the ground gives), not on top of the box (1.1 m)
    }

    [Fact]
    public void Without_the_region_mesher_the_hollow_box_is_not_split_and_the_cube_rests_on_top()
    {
        (string kind, float rest) = HollowBoxAndCube(new HarnessOptions { RegionMesher = false });
        Assert.DoesNotContain(JoltScene.PrimCellsKind, kind);
        Assert.True(rest > 1f, $"cube rests {rest:0.000} m up");
    }

    // A physical mesh whose asset carries a hull list collides as those hulls.
    [Fact]
    public void A_physical_mesh_with_a_hull_list_cooks_as_its_hulls_with_the_harnesss_mesher()
    {
        string kind = null;
        RunScenario("harness-hull-list", run =>
        {
            AddMesh((JoltScene)run.PhysicsScene, Asset(Soup(Trough), Trough, false), TroughHeight, true, Prim, out kind);
        }, null, new HarnessOptions(), 2f);
        Assert.Equal("hulls(asset x3)", kind);
    }

    // ------------------------------------------------------------------ the region-load window

    [Fact]
    public void The_scenarios_setup_runs_after_the_first_step_as_a_rezzed_prim_is_added()
    {
        bool loading = true;
        RunScenario("harness-warm-up", r => loading = IsRegionLoading(r), null, new HarnessOptions(), 0.2f);
        Assert.False(loading);

        RunScenario("harness-no-warm-up", r => loading = IsRegionLoading(r), null, new HarnessOptions { WarmUpStep = false }, 0.2f);
        Assert.True(loading);
    }

    private static bool IsRegionLoading(Run r)
        => (bool)typeof(JoltScene).GetProperty("IsRegionLoading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(r.PhysicsScene)!;

    // A script's llSetVelocity on a prim that was just rezzed counts from the first heartbeat; in the region-load window it
    // drops the horizontal part (a reloaded body must not coast), so a prim set moving in the earlier harness's setup
    // started a heartbeat late.
    [Fact]
    public void A_velocity_given_in_the_setup_is_kept_for_a_rezzed_prim()
    {
        Vector3 seen = default;
        void Setup(Run r)
        {
            r.AddBox(new Vector3(1f), new Vector3(100f, 100f, Course.Ground + 20f), Quaternion.Identity);
            r.Actor.Velocity = new Vector3(3f, 0f, 0f);
            seen = r.Actor.Velocity;
        }
        RunScenario("harness-setup-velocity", Setup, null, new HarnessOptions(), 0.2f);
        Assert.Equal(3f, seen.X);

        RunScenario("harness-setup-velocity-loading", Setup, null, new HarnessOptions { WarmUpStep = false }, 0.2f);
        Assert.Equal(0f, seen.X);
    }

    [Fact]
    public void A_region_start_restore_keeps_the_region_load_window()
    {
        bool loading = false;
        RunScenario("harness-region-start", r =>
        {
            r.AddBox(new Vector3(2f, 1f, 0.5f), new Vector3(100f, 100f, Course.Ground + 0.3f), Quaternion.Identity);
            r.MakeVehicle(Vehicle.TYPE_CAR);
            loading = IsRegionLoading(r);
        }, null, new HarnessOptions { VehicleRoute = VehicleRoute.RegionStart }, 0.2f);
        Assert.True(loading);
    }

    // ------------------------------------------------------------------ the settings

    [Fact]
    public void The_harness_starts_from_the_Jolt_section_every_simulator_reads_first()
    {
        IConfig shipped = Harness.Harness.ShippedJolt();
        Assert.NotEmpty(shipped.GetKeys());

        var fromShipped = new IniConfigSource();
        IConfig section = fromShipped.AddConfig("Jolt");
        foreach (string key in shipped.GetKeys())
            section.Set(key, shipped.Get(key));

        // The shipped section is what the code defaults to, so starting from it moves nothing. This holds the two together:
        // a key added to the ini with a value other than the code's default is found here, and the harness rows move with it.
        var warnings = new List<string>();
        object shippedConfig = ParseJolt(fromShipped, warnings);
        object defaults = ParseJolt(new IniConfigSource(), warnings);
        Assert.Empty(warnings);
        foreach (FieldInfo f in shippedConfig.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            Assert.True(Equals(f.GetValue(shippedConfig), f.GetValue(defaults)),
                $"{f.Name}: RegionServerDefaults.ini gives {f.GetValue(shippedConfig)}, the code's default is {f.GetValue(defaults)}");
    }

    private static object ParseJolt(IConfigSource source, List<string> warnings)
        => typeof(JoltScene).Assembly.GetType("OpenSim.Region.PhysicsModules.Jolt.JoltConfig", true)!
            .GetMethod("FromConfig", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { source, warnings })!;
}
