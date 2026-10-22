/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.PrimHullProbeTests;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Concave physical prims with the same shape parameters share one cooked compound of cells, cooked once at unit size and
/// scaled to each prim's size. Any change to a parameter is a new entry; an entry goes with its last prim; a region's
/// entries go with the region. Each result is a line "PRIM-CACHE ..." in the test output. Serial with the other native
/// tests (the memory run is a child process).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PrimCellCacheTests
{
    private readonly ITestOutputHelper _out;
    public PrimCellCacheTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const byte Prim = (byte)PhysShapeType.prim;

    private static Spec Hollow(float hollow = 0.5f) => new Spec { Name = "hollow box " + hollow, Profile = ProfileShape.Square, Hollow = hollow };

    private static JoltPrim Add(JoltScene scene, PrimitiveBaseShape pbs, Vector3 size, int n, bool physical = true)
        => (JoltPrim)scene.AddPrimShape("prim", pbs, new Vector3(CentreX + 3f * (n % 20), CentreY + 3f * (n / 20), Ground + 3f), size, Quaternion.Identity, physical, false, Prim, _id++);

    private static int Live(JoltScene scene) => scene.Backend.GetCapacityStats().LiveShapeCount;

    // ------------------------------------------------------------------ sharing

    [Fact]
    public void Identical_prims_share_one_cooked_compound_at_every_size()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            var prims = new List<JoltPrim>();
            for (int i = 0; i < 50; i++)
                prims.Add(Add(scene, Hollow().Build(), Vector3.One, i));
            Say($"PRIM-CACHE 50 identical prims: cooked={scene.PrimCellShapesCooked} bases={scene.PrimCellBaseCount} entries={scene.SharedShapeCount} live shapes={Live(scene)}");
            Assert.Equal(1, scene.PrimCellShapesCooked);
            Assert.Equal(1, scene.PrimCellBaseCount);
            Assert.Equal(1, scene.SharedShapeCount);
            Assert.All(prims, p => Assert.Equal(4, p.HullCount));
            Assert.All(prims, p => Assert.Equal(prims[0].ShapeKind, p.ShapeKind));

            // other sizes of the same parameters: scaled shapes over the one compound, nothing cooked
            for (int i = 0; i < 10; i++)
                prims.Add(Add(scene, Hollow().Build(), new Vector3(1f + i * 0.5f, 2f, 3f - i * 0.2f), 50 + i));
            Say($"PRIM-CACHE ten more sizes: cooked={scene.PrimCellShapesCooked} bases={scene.PrimCellBaseCount} entries={scene.SharedShapeCount}");
            Assert.Equal(1, scene.PrimCellShapesCooked);
            Assert.Equal(1, scene.PrimCellBaseCount);
            Assert.Equal(11, scene.SharedShapeCount);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_scaled_shared_compound_is_the_compound_a_direct_cook_makes()
    {
        string scratch = Scratch();
        JoltScene cached = NewScene(scratch);
        JoltScene direct = NewScene(scratch);
        direct.CellShapeCacheEnabled = false;
        try
        {
            Vector3 size = new(2f, 3f, 1.5f);
            foreach (Spec spec in new[] { Hollow(), Shapes().Single(s => s.Name == "torus"), Shapes().Single(s => s.Name == "bowl (hollow sphere 0.8, dimple 0..0.5)"),
                                          Shapes().Single(s => s.Name == "tube, hollow 0.5") })
            {
                JoltPrim a = Add(cached, spec.Build(), Vector3.One, 0);       // makes the unit compound
                JoltPrim b = Add(cached, spec.Build(), size, 1);              // scaled over it
                JoltPrim c = Add(direct, spec.Build(), size, 0);              // cooked at its own size
                float va = cached.Backend.GetShapeVolume(ShapeOf(b)), vc = direct.Backend.GetShapeVolume(ShapeOf(c));
                SVector3 ca = cached.Backend.GetShapeCenterOfMass(ShapeOf(b)), cc = direct.Backend.GetShapeCenterOfMass(ShapeOf(c));
                Say($"PRIM-CACHE scaled against direct \"{spec.Name}\": hulls {b.HullCount}/{c.HullCount} volume {va:0.0000}/{vc:0.0000} centre ({ca.X:0.000},{ca.Y:0.000},{ca.Z:0.000})/({cc.X:0.000},{cc.Y:0.000},{cc.Z:0.000}) mass {b.Mass:0.000}/{c.Mass:0.000}");
                Assert.Equal(c.HullCount, b.HullCount);
                Assert.InRange(va / vc, 0.999f, 1.001f);
                Assert.InRange((ca - cc).Length(), 0f, 0.002f);
                Assert.InRange(b.Mass / c.Mass, 0.999f, 1.001f);
                Assert.True(cached.Backend.IsScaleValid(ShapeOf(a), new SVector3(size.X, size.Y, size.Z)));
            }
        }
        finally { cached.Dispose(); direct.Dispose(); }
    }

    private static readonly FieldInfo ShapeField = typeof(JoltPrim).GetField("_shape", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static ShapeId ShapeOf(JoltPrim p) => (ShapeId)ShapeField.GetValue(p)!;

    // ------------------------------------------------------------------ invalidation

    [Fact]
    public void A_change_to_any_shape_parameter_is_a_new_entry()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            PrimitiveBaseShape Base() => new Spec { Name = "base", Profile = ProfileShape.Circle, Hollow = 0.5f, Circular = true, ScaleX = 100, ScaleY = 175 }.Build();
            var changes = new (string field, Action<PrimitiveBaseShape> change)[]
            {
                ("ProfileHollow", p => p.ProfileHollow = 20000), ("HollowShape", p => p.HollowShape = HollowShape.Square),
                ("ProfileBegin", p => p.ProfileBegin = 2000), ("ProfileEnd", p => p.ProfileEnd = 2000), ("ProfileCurve", p => p.ProfileCurve = (byte)ProfileShape.Square),
                ("PathBegin", p => p.PathBegin = 3000), ("PathEnd", p => p.PathEnd = 3000), ("PathScaleX", p => p.PathScaleX = 90),
                ("PathScaleY", p => p.PathScaleY = 160), ("PathShearX", p => p.PathShearX = 10), ("PathShearY", p => p.PathShearY = 10),
                ("PathTwist", p => p.PathTwist = 5), ("PathTwistBegin", p => p.PathTwistBegin = 5), ("PathRadiusOffset", p => p.PathRadiusOffset = 10),
                ("PathTaperX", p => p.PathTaperX = 10), ("PathTaperY", p => p.PathTaperY = 10), ("PathRevolutions", p => p.PathRevolutions = 10),
                ("PathSkew", p => p.PathSkew = 10),
            };
            string baseKey = JoltScene.PrimShapeParameters(Base());
            var seen = new HashSet<string> { baseKey };
            foreach (var (field, change) in changes)
            {
                PrimitiveBaseShape pbs = Base();
                change(pbs);
                Assert.True(seen.Add(JoltScene.PrimShapeParameters(pbs)), $"{field} is not part of the key");
            }
            Assert.Equal(changes.Length + 1, seen.Count);

            // a prim of each of a few of them is cooked once; a second of each shares it
            int cooked = 0;
            JoltPrim first = Add(scene, Base(), Vector3.One, 0);
            Assert.Equal(++cooked, scene.PrimCellShapesCooked);
            Add(scene, Base(), Vector3.One, 1);
            Assert.Equal(cooked, scene.PrimCellShapesCooked);
            foreach (string field in new[] { "ProfileHollow", "HollowShape", "PathTwist", "PathScaleX" })
            {
                PrimitiveBaseShape pbs = Base();
                changes.Single(c => c.field == field).change(pbs);
                PrimitiveBaseShape again = Base();
                changes.Single(c => c.field == field).change(again);
                JoltPrim p = Add(scene, pbs, Vector3.One, 2);
                Say($"PRIM-CACHE changed {field}: kind={p.ShapeKind}");
                Assert.True(p.HullCount > 1, field);
                Assert.Equal(++cooked, scene.PrimCellShapesCooked);
                Add(scene, again, Vector3.One, 3);
                Assert.Equal(cooked, scene.PrimCellShapesCooked);
            }
            Assert.Equal(5, scene.PrimCellBaseCount);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Changing_a_prims_shape_moves_it_to_the_new_entry_and_frees_the_old_with_its_last_prim()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            int baseline = Live(scene);
            JoltPrim a = Add(scene, Hollow(0.5f).Build(), Vector3.One, 0);
            JoltPrim b = Add(scene, Hollow(0.5f).Build(), Vector3.One, 1);
            Assert.Equal(1, scene.PrimCellBaseCount);

            a.Shape = Hollow(0.6f).Build();            // the old entry still has b
            Assert.Equal(2, scene.PrimCellBaseCount);
            Assert.Equal(2, scene.SharedShapeCount);
            Assert.Equal(2, scene.PrimCellShapesCooked);

            b.Shape = Hollow(0.6f).Build();            // the last of the old entry's prims leaves: it goes
            Say($"PRIM-CACHE shape change: bases={scene.PrimCellBaseCount} entries={scene.SharedShapeCount} cooked={scene.PrimCellShapesCooked}");
            Assert.Equal(1, scene.PrimCellBaseCount);
            Assert.Equal(1, scene.SharedShapeCount);
            Assert.Equal(2, scene.PrimCellShapesCooked);   // the new entry was already cooked for a

            // and the prim weighs the new shape: 1 - 0.6^2 of a box
            JoltPrim solid = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, 2);
            Assert.Equal(0.64f, a.Mass / solid.Mass, 2);
            Assert.True(baseline < Live(scene));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ release

    [Fact]
    public void Every_entry_is_released_with_its_last_prim_and_leaves_no_shape_behind()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            int baseline = Live(scene);
            var prims = new List<JoltPrim>();
            Spec[] specs = { Hollow(0.5f), Hollow(0.7f), Shapes().Single(s => s.Name == "torus") };
            for (int i = 0; i < 60; i++)
                prims.Add(Add(scene, specs[i % 3].Build(), new Vector3(1f + (i % 4) * 0.5f, 1f, 1f + (i % 5) * 0.25f), i));
            int held = Live(scene);
            Say($"PRIM-CACHE 60 prims, 3 shapes, 4 x 5 sizes: cooked={scene.PrimCellShapesCooked} bases={scene.PrimCellBaseCount} entries={scene.SharedShapeCount} live shapes {baseline} -> {held}");
            Assert.Equal(3, scene.PrimCellShapesCooked);
            Assert.Equal(3, scene.PrimCellBaseCount);

            foreach (JoltPrim p in prims)
                scene.RemovePrim(p);
            Say($"PRIM-CACHE all removed: bases={scene.PrimCellBaseCount} entries={scene.SharedShapeCount} live shapes {Live(scene)}");
            Assert.Equal(0, scene.PrimCellBaseCount);
            Assert.Equal(0, scene.SharedShapeCount);
            Assert.Equal(baseline, Live(scene));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_region_restart_starts_with_no_entries_and_the_old_scene_gives_everything_back()
    {
        string scratch = Scratch();
        for (int restart = 0; restart < 3; restart++)
        {
            JoltScene scene = NewScene(scratch);
            Assert.Equal(0, scene.PrimCellBaseCount);
            Assert.Equal(0, scene.SharedShapeCount);
            for (int i = 0; i < 20; i++)
                Add(scene, Hollow().Build(), new Vector3(1f + i % 3, 1f, 1f), i);
            Assert.Equal(1, scene.PrimCellShapesCooked);
            Assert.Equal(1, scene.PrimCellBaseCount);
            scene.Dispose();
        }
        Say("PRIM-CACHE three region restarts: each began empty, cooked once, disposed cleanly");
    }

    // ------------------------------------------------------------------ a part of a linkset

    [Fact]
    public void A_linkset_part_that_weighs_less_than_its_cells_takes_a_compound_of_its_own_without_a_new_split()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            Spec thin = new Spec { Name = "thin torus", Profile = ProfileShape.Circle, Hollow = 0.95f, Circular = true, ScaleX = 100, ScaleY = 175 };
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, 0);
            JoltPrim a = Add(scene, thin.Build(), new Vector3(1.5f, 1.5f, 1.5f), 1);
            JoltPrim loner = Add(scene, thin.Build(), new Vector3(1.5f, 1.5f, 1.5f), 2);   // stays alone and keeps the shared compound
            Assert.Equal(1, scene.PrimCellShapesCooked);
            int splits = scene.PrimCellShapesCooked;
            a.link(root);
            a.link(root);
            Step(scene, 0.3f);
            Say($"PRIM-CACHE thin part welded: splits={scene.PrimCellShapesCooked} own copies={scene.PrimCellCopiesMade} bases={scene.PrimCellBaseCount} entries={scene.SharedShapeCount}");
            Assert.Equal(splits, scene.PrimCellShapesCooked);        // no search at the weld
            Assert.Equal(1, scene.PrimCellCopiesMade);               // its own compound from the points held
            Assert.Equal(1, scene.PrimCellBaseCount);                // the loner still shares the base
            Assert.Equal(loner.Mass, a.Mass, 3);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ memory

    private const string ChildVar = "JOLT_PRIM_CELL_CACHE_MEMORY_CHILD";

    private static long NativeBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 - GC.GetGCMemoryInfo().TotalCommittedBytes;
    }

    // N prims made, the native memory they hold while alive, then removed and what is left: one scene, one scenario.
    private static (long held, long left, int cooked, int live, long managed, int liveLeft, int entriesLeft) Scenario(string scratch, bool cache, bool different, int prims)
    {
        JoltScene scene = NewScene(scratch);
        scene.CellShapeCacheEnabled = cache;
        try
        {
            PrimitiveBaseShape Make(int i)
            {
                // different: each prim's hollow differs (0.30 .. 0.89 in steps of 0.003), so no two share a key; the shape is a
                // torus-class prim with a dozen cells, the commonest concave kind
                float hollow = different ? 0.30f + 0.003f * i : 0.5f;
                return new Spec { Name = "m" + i, Profile = ProfileShape.Circle, Hollow = hollow, Circular = true, ScaleX = 100, ScaleY = 175 }.Build();
            }
            // warm the allocator and the mesher: the first cooks take one-time growth
            var warm = new List<JoltPrim>();
            for (int i = 0; i < 10; i++)
                warm.Add(Add(scene, Make(1000 + i), Vector3.One, i));
            foreach (JoltPrim p in warm)
                scene.RemovePrim(p);
            long start = NativeBytes();
            int liveStart = Live(scene);
            long managedStart = GC.GetTotalMemory(true);
            int cookedStart = scene.PrimCellShapesCooked;
            var list = new List<JoltPrim>();
            for (int i = 0; i < prims; i++)
                list.Add(Add(scene, Make(i), Vector3.One, i));
            long held = NativeBytes() - start;
            long managed = GC.GetTotalMemory(true) - managedStart;
            int cooked = scene.PrimCellShapesCooked - cookedStart;
            int live = Live(scene);
            foreach (JoltPrim p in list)
                scene.RemovePrim(p);
            long left = NativeBytes() - start;
            return (held, left, cooked, live, managed, Live(scene) - liveStart, scene.SharedShapeCount);
        }
        finally { scene.Dispose(); }
    }

    /// <summary>The child entry: gated on an environment variable so it never runs in an ordinary test pass.</summary>
    [Fact]
    public void Child_measures_memory()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildVar)))
            return;
        string scratch = Scratch();
        var parts = new List<string>();
        foreach (var (name, cache, different) in new[] { ("identical_nocache", false, false), ("identical_cache", true, false), ("different_nocache", false, true), ("different_cache", true, true) })
        {
            // the worse of two runs of the same scenario is left out: the first of a kind takes the heap's growth
            Scenario(scratch, cache, different, 40);
            var (held, left, cooked, live, managed, liveLeft, entriesLeft) = Scenario(scratch, cache, different, 200);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name}_kb={held / 1024} {name}_left_kb={left / 1024} {name}_cooks={cooked} {name}_live_shapes={live} {name}_managed_kb={managed / 1024} {name}_live_left={liveLeft} {name}_entries_left={entriesLeft}"));
        }
        // regions started and stopped: what the native heap keeps across them
        long before = NativeBytes();
        int restartShapesLeft = 0;
        for (int r = 0; r < 20; r++)
        {
            var run = Scenario(scratch, true, false, 50);
            restartShapesLeft += run.liveLeft + run.entriesLeft;
        }
        long after = NativeBytes();
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"restarts_x20_kb={(after - before) / 1024} restarts_x20_shapes_left={restartShapesLeft}"));
        Console.WriteLine("PRIM-CELL-CACHE-MEMORY " + string.Join(" ", parts));
    }

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private (int exit, string output) RunChild()
    {
        string asm = Assembly.GetExecutingAssembly().Location;
        string project = Path.GetDirectoryName(asm)!.Split(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)[0];
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = project,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Configuration);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add("FullyQualifiedName~PrimCellCacheTests.Child_measures_memory");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("console;verbosity=detailed");
        psi.Environment[ChildVar] = "1";
        // the child builds nothing and leaves no build server or worker behind
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using var p = Process.Start(psi)!;
        Task<string> se = p.StandardError.ReadToEndAsync();
        string so = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10 * 60 * 1000);
        return (p.ExitCode, so + se.Result);
    }

    private static long Value(string line, string key)
        => long.Parse(line.Split(' ').Single(f => f.StartsWith(key + "=", StringComparison.Ordinal)).Substring(key.Length + 1), CultureInfo.InvariantCulture);

    [Fact]
    public void Two_hundred_identical_prims_and_two_hundred_different_ones_against_no_cache()
    {
        var (exit, output) = RunChild();
        string line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("PRIM-CELL-CACHE-MEMORY ", StringComparison.Ordinal));
        Assert.True(exit == 0 && line != null, $"child exit {exit}:\n{output}");
        Say(line);

        long idNo = Value(line, "identical_nocache_kb"), idYes = Value(line, "identical_cache_kb");
        long diNo = Value(line, "different_nocache_kb"), diYes = Value(line, "different_cache_kb");
        Say($"PRIM-CACHE memory, 200 prims: identical {idNo} KB without the cache, {idYes} KB with; different {diNo} KB without, {diYes} KB with; " +
            $"cooks identical {Value(line, "identical_cache_cooks")} / different {Value(line, "different_cache_cooks")}");
        Assert.Equal(1, Value(line, "identical_cache_cooks"));
        Assert.Equal(200, Value(line, "different_cache_cooks"));
        Assert.Equal(200, Value(line, "identical_nocache_cooks"));
        // What the cache holds is counted, not weighed: the shapes alive while the prims exist, and none left once they are gone.
        // One compound and one wrapper per prim would be 201; the shared case is the compound and a shared base.
        Assert.Equal(201, Value(line, "identical_nocache_live_shapes"));
        Assert.Equal(2, Value(line, "identical_cache_live_shapes"));
        // different prims share nothing, so the cache adds no shape of its own
        Assert.Equal(Value(line, "different_nocache_live_shapes"), Value(line, "different_cache_live_shapes"));
        foreach (string run in new[] { "identical_nocache", "identical_cache", "different_nocache", "different_cache" })
        {
            Assert.True(Value(line, run + "_live_left") == 0, $"{run} left {Value(line, run + "_live_left")} shapes alive after its prims were removed");
            Assert.True(Value(line, run + "_entries_left") == 0, $"{run} left {Value(line, run + "_entries_left")} cache entries after its prims were removed");
        }
        Assert.True(Value(line, "restarts_x20_shapes_left") == 0, $"twenty region starts and stops left {Value(line, "restarts_x20_shapes_left")} shapes or entries");
        // identical prims: one compound and two hundred small wrappers against two hundred compounds. The saving is about
        // seventeen megabytes against a few hundred kilobytes, far beyond allocator noise on Windows and Linux. On macOS the
        // process figure moves by megabytes either way (a run without the cache measured less than nothing), so its counts
        // above carry the check there.
        if (!OperatingSystem.IsMacOS())
            Assert.True(idYes * 4 < idNo, $"the cache saved too little: {idYes} against {idNo} KB");
        // The byte figures below are the process's private memory less the managed heap. Windows returns freed native memory to
        // the OS, so they hold there. glibc keeps freed memory in its arenas, so on Linux they move by megabytes either way
        // with nothing to do with the cache (a run with the cache off "kept" 18 MB, another with it on cost 1 MB more than
        // without). The counts above carry the same checks on every platform. The bounds were set on Windows x64; Windows on
        // Arm64 keeps freed memory longer (a run kept 15 MB after its prims were gone, with no shape or cache entry left),
        // so they are asked of x64 only.
        if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            // different prims: a unit compound and a wrapper each against one compound: no more than a quarter over
            Assert.True(diYes < diNo * 1.25 + 512, $"the cache cost too much with nothing to share: {diYes} against {diNo} KB");
            // everything made is given back (the allowance is heap noise, as in the shape settings memory test)
            foreach (string run in new[] { "identical_nocache", "identical_cache", "different_nocache", "different_cache" })
                Assert.True(Value(line, run + "_left_kb") < 4 * 1024, $"{run} kept {Value(line, run + "_left_kb")} KB after its prims were removed");
            Assert.True(Value(line, "restarts_x20_kb") < 8 * 1024, $"twenty region starts and stops kept {Value(line, "restarts_x20_kb")} KB");
        }
    }
}
