/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Reflection;
using JoltPhysicsSharp;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Backend teardown destroys the region's native physics system (JoltPhysicsSharp 2.19.1's PhysicsSystem.Dispose
/// does not), and creating and destroying systems from several threads is safe around joltc's global map
/// of systems. Serial collection: these tests read the process-wide Foundation reference count, and other tests'
/// backends would disturb it. The memory test measures in a child test host of its own (<see cref="ChildTestHost"/>).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SystemTeardownTests
{
    private readonly ITestOutputHelper _out;
    public SystemTeardownTests(ITestOutputHelper output) { _out = output; }

    private const long MB = 1024 * 1024;

    // A destroyed system leaves nothing behind; the bound leaves room for heap growth and fragmentation. Every
    // leaked system holds at least its 8 MB temp allocator, so it crosses the bound within a few cycles, and a leak
    // of 100 KB a cycle crosses it in about 330 of the 600. The growth is checked every CheckEvery cycles, so a large
    // leak stops the loop early instead of filling the machine.
    private const int Cycles = 600;
    private const int CheckEvery = 10;
    private const long GrowthBound = 32 * MB;

    // Set in the child test host that runs the memory test on its own.
    private const string MemoryChildVariable = "JOLT_TEST_MEMORY_CHILD";

    // The process's private memory, and of it what the process holds: the private memory less what the GC keeps
    // committed but free. A collection does not hand freed GC memory back at once, and how much it keeps depends
    // on what the process allocated before, not on a leak; the GC heap's live size still counts.
    private static (long Private, long Held) Memory()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return (p.PrivateMemorySize64, p.PrivateMemorySize64 - gc.TotalCommittedBytes + gc.HeapSizeBytes);
    }

    private static int FoundationRefCount()
        => (int)typeof(JoltPhysicsBackend).GetField("s_foundationRefCount", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static PhysicsSystem NativeSystem(JoltPhysicsBackend b)
        => (PhysicsSystem)typeof(JoltPhysicsBackend).GetField("_system", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(b);

    private static void Cycle()
    {
        var t = new JoltTestBackend();
        t.Ground();
        t.Step();
        t.Dispose();
    }

    [Fact]
    public void The_binding_has_the_fields_teardown_reads()
    {
        // A binding upgrade that renames them would leave the listeners and the GCHandle behind; fail here instead.
        Assert.True(JoltPhysicsBackend.PhysicsSystemFieldsFound);
    }

    [Fact]
    public void Repeated_create_and_teardown_does_not_grow_private_memory()
    {
        // The process's private memory moves with everything the process ran before: after the rest of the suite,
        // 30 cycles left the GC 27 MB more committed memory with no change in the rest, where alone they leave
        // about none. So the cycles run in a test host that runs this test alone, and count the GC's live heap
        // rather than what it keeps committed (Memory).
        if (Environment.GetEnvironmentVariable(MemoryChildVariable) != "1")
        {
            _out.WriteLine(ChildTestHost.RunAndAssertPassed(GetType(), nameof(Repeated_create_and_teardown_does_not_grow_private_memory),
                new Dictionary<string, string> { [MemoryChildVariable] = "1" }));
            return;
        }

        // A region that stays up keeps Foundation and the job pools alive, as on a simulator with other regions,
        // so the cycles measure the per-region system alone.
        using var anchor = new JoltTestBackend();
        anchor.Ground();
        anchor.Step();
        for (var i = 0; i < 3; i++)
            Cycle();   // warm the heaps

        var before = Memory();
        var after = before;
        var done = 0;
        while (done < Cycles && after.Held - before.Held < GrowthBound)
        {
            for (var i = 0; i < CheckEvery; i++)
                Cycle();
            done += CheckEvery;
            after = Memory();
        }

        long growth = after.Held - before.Held;
        _out.WriteLine($"held bytes: before={before.Held / MB} MB after={after.Held / MB} MB growth={growth / MB} MB "
            + $"over {done} cycles ({growth / done / 1024} KB per cycle); bound {GrowthBound / MB} MB. "
            + $"Private bytes, with the GC's free committed memory: {before.Private / MB} -> {after.Private / MB} MB");
        Assert.True(growth < GrowthBound,
            $"private memory held grew {growth / MB} MB over {done} create/teardown cycles (bound {GrowthBound / MB} MB)");
    }

    // Each cycle cooks a 257 x 257 terrain, makes the terrain body from it, lets go of the shape and tears the backend
    // down. The body creation settings the binding never frees held the shape: about 243 KB a cycle, 73 MB over
    // the 300 cycles here, against a bound of 32 MB.
    [Fact]
    public void Repeated_terrain_cook_and_teardown_does_not_grow_private_memory()
    {
        if (Environment.GetEnvironmentVariable(MemoryChildVariable) != "1")
        {
            _out.WriteLine(ChildTestHost.RunAndAssertPassed(GetType(), nameof(Repeated_terrain_cook_and_teardown_does_not_grow_private_memory),
                new Dictionary<string, string> { [MemoryChildVariable] = "1" }));
            return;
        }

        const int TerrainCycles = 300;
        var heights = new float[257 * 257];
        Array.Fill(heights, 25f);
        void TerrainCycle()
        {
            var t = new JoltTestBackend();
            ShapeId terrain = t.B.CreateHeightFieldShape(heights, 257, 257, new System.Numerics.Vector3(1f));
            t.B.SetTerrain(terrain, System.Numerics.Vector3.Zero);
            t.B.ReleaseShape(terrain);
            t.Step();
            t.Dispose();
        }

        using var anchor = new JoltTestBackend();
        anchor.Ground();
        anchor.Step();
        for (var i = 0; i < 3; i++)
            TerrainCycle();

        var before = Memory();
        var after = before;
        var done = 0;
        while (done < TerrainCycles && after.Held - before.Held < GrowthBound)
        {
            for (var i = 0; i < CheckEvery; i++)
                TerrainCycle();
            done += CheckEvery;
            after = Memory();
        }

        long growth = after.Held - before.Held;
        _out.WriteLine($"held bytes: growth={growth / MB} MB over {done} terrain cycles ({growth / done / 1024} KB per cycle); bound {GrowthBound / MB} MB");
        Assert.True(growth < GrowthBound,
            $"private memory held grew {growth / MB} MB over {done} terrain cook and teardown cycles (bound {GrowthBound / MB} MB)");
    }

    [Fact]
    public void Backends_created_and_torn_down_on_several_threads_while_another_steps()
    {
        const int Workers = 4;
        const int CyclesPerWorker = 8;

        using var stepper = new JoltTestBackend();
        stepper.Ground();
        var box = stepper.B.CreateBoxShape(new System.Numerics.Vector3(0.5f));
        for (var i = 0; i < 20; i++)
            stepper.Dynamic(box, new System.Numerics.Vector3(128f, 128f, 2f + i * 1.1f));

        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var stop = 0;
        long steps = 0;
        var stepThread = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    stepper.Step();
                    Interlocked.Increment(ref steps);
                }
            }
            catch (Exception e) { errors.Enqueue(e); }
        }) { IsBackground = true, Name = "stepper" };

        using var go = new ManualResetEventSlim(false);
        var workers = new List<Thread>();
        for (var w = 0; w < Workers; w++)
        {
            workers.Add(new Thread(() =>
            {
                try
                {
                    go.Wait();
                    for (var i = 0; i < CyclesPerWorker; i++)
                        Cycle();
                }
                catch (Exception e) { errors.Enqueue(e); }
            }) { IsBackground = true, Name = $"worker {w}" });
        }

        stepThread.Start();
        foreach (var th in workers)
            th.Start();
        go.Set();

        var limit = TimeSpan.FromSeconds(120);
        var clock = Stopwatch.StartNew();
        foreach (var th in workers)
            Assert.True(th.Join(limit - clock.Elapsed > TimeSpan.Zero ? limit - clock.Elapsed : TimeSpan.Zero),
                $"{th.Name} did not finish within {limit.TotalSeconds} s");
        Volatile.Write(ref stop, 1);
        Assert.True(stepThread.Join(TimeSpan.FromSeconds(30)), "the stepping thread did not stop");

        _out.WriteLine($"{Workers} workers x {CyclesPerWorker} cycles in {clock.ElapsedMilliseconds} ms; stepper stepped {steps} times");
        Assert.Empty(errors);
        Assert.True(steps > 0);
        stepper.Step();   // the region that kept stepping is still usable
    }

    [Fact]
    public void Teardown_twice_destroys_once_and_gives_back_one_reference()
    {
        using var anchor = new JoltTestBackend();
        int baseline = FoundationRefCount();

        var t = new JoltTestBackend();
        t.Ground();
        t.Step();
        PhysicsSystem system = NativeSystem(t.B)!;
        Assert.Equal(baseline + 1, FoundationRefCount());

        t.Dispose();
        Assert.True(system.IsDisposed);
        Assert.Equal(IntPtr.Zero, system.Handle);
        Assert.Null(NativeSystem(t.B));
        Assert.Equal(baseline, FoundationRefCount());

        t.Dispose();
        Assert.Equal(baseline, FoundationRefCount());

        anchor.Step();   // the region that stays up still steps
    }

    [Fact]
    public void Teardown_after_Initialize_failed_part_way_destroys_the_system()
    {
        using var anchor = new JoltTestBackend();
        int baseline = FoundationRefCount();

        var b = new JoltPhysicsBackend();
        PhysicsSystem system = null;
        b.AfterSystemCreatedForTest = () =>
        {
            system = NativeSystem(b);
            throw new InvalidOperationException("injected start failure");
        };
        var e = Assert.Throws<InvalidOperationException>(() => b.Initialize(JoltTestBackend.Settings()));
        Assert.Equal("injected start failure", e.Message);
        Assert.NotNull(system);
        Assert.Equal(baseline + 1, FoundationRefCount());

        b.Dispose();
        Assert.True(system!.IsDisposed);
        Assert.Equal(IntPtr.Zero, system.Handle);
        Assert.Equal(baseline, FoundationRefCount());

        b.Dispose();
        Assert.Equal(baseline, FoundationRefCount());
        anchor.Step();
    }

    [Fact]
    public void A_scene_whose_backend_fails_to_initialize_gives_back_what_the_backend_took()
    {
        using var anchor = new JoltTestBackend();
        int baseline = FoundationRefCount();

        var config = new Nini.Config.IniConfigSource();
        Nini.Config.IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(config);

        PhysicsSystem system = null;
        scene.BackendCreatedForTest = b => b.AfterSystemCreatedForTest = () =>
        {
            system = NativeSystem(b);
            throw new InvalidOperationException("injected start failure");
        };
        var heights = new float[256 * 256];
        var e = Assert.Throws<InvalidOperationException>(() => scene.InitialiseWithoutScene("Test Region", 256, 256, heights, 20f, 0.0909f));
        Assert.Equal("injected start failure", e.Message);

        Assert.NotNull(system);
        Assert.True(system!.IsDisposed);
        Assert.Equal(IntPtr.Zero, system.Handle);
        Assert.Null(scene.Backend);
        Assert.Equal(baseline, FoundationRefCount());

        scene.Dispose();   // nothing left to give back
        Assert.Equal(baseline, FoundationRefCount());
        anchor.Step();
    }

    [Fact]
    public void Teardown_of_a_backend_never_initialized_leaves_other_regions_alone()
    {
        using var anchor = new JoltTestBackend();
        anchor.Ground();
        int baseline = FoundationRefCount();

        var b = new JoltPhysicsBackend();
        b.Dispose();
        b.Dispose();

        Assert.Equal(baseline, FoundationRefCount());
        anchor.Step();
    }
}
