/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The first ray cast after a start costs about what a later one does. The first cast in a process pays one-time
/// costs (JIT, the native call's binding, the binding's first use of its filter and normal code); the scene pays
/// them while it loads, with casts charged to no budget. That can only be seen in a process that has made no cast
/// yet, so the casts run in a child test process and the parent reads its figures.
/// What is asserted is what the warm-up does, not how long anything took, so a stalled machine cannot fail it: the
/// warm-up ran, charged nothing to either budget, and left the first real cast nothing to compile (the methods the
/// JIT compiled on the casting thread, System.Runtime.JitInfo). The times are printed, not asserted.
/// </summary>
public class FirstRayCastTests
{
    private readonly ITestOutputHelper _out;
    public FirstRayCastTests(ITestOutputHelper output) { _out = output; }

    private const string ChildVar = "JOLT_FIRST_RAY_CAST_CHILD";
    private const float Ground = 25f;
    private const int LaterCasts = 10;

    // ScenePresence.MakeRootAgent's landing ray: straight down onto prims and avatars, not the land.
    private const RayFilterFlags Landing = RayFilterFlags.BackFaceCull | RayFilterFlags.PrimsNonPhantomAgents;

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(config);
        float[] heights = new float[256 * 256];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", 256, 256, heights, 20f, 1f / 11f);
        return scene;
    }

    /// <summary>The child entry: gated on an environment variable so it never runs in an ordinary test pass.</summary>
    [Fact]
    public void Child_casts()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildVar)))
            return;   // not the child; nothing to do

        JoltScene scene = NewScene();
        try
        {
            // Two platforms, one above the other, under the ray: every cast has two hits to sort.
            scene.AddPrimShape("upper", PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, Ground + 6f), new Vector3(4f, 4f, 0.5f), Quaternion.Identity, false, 500);
            scene.AddPrimShape("lower", PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, Ground + 3f), new Vector3(4f, 4f, 0.5f), Quaternion.Identity, false, 501);
            scene.Simulate(1f / 11f);
            var loaded = scene.CapacityStats();

            var jit = new long[LaterCasts + 1];
            var backendMs = new double[LaterCasts + 1];
            var allMs = new double[LaterCasts + 1];
            int hits = int.MaxValue;
            for (int i = 0; i <= LaterCasts; i++)
            {
                double before = scene.CapacityStats().SimulatorRayCasts.MsTotal;
                long compiled = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
                long start = Stopwatch.GetTimestamp();
                var r = (List<ContactResult>)scene.RaycastWorld(new Vector3(128f, 128f, Ground + 50f), new Vector3(0f, 0f, -1f), 51f, 5, Landing);
                allMs[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                jit[i] = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - compiled;
                backendMs[i] = scene.CapacityStats().SimulatorRayCasts.MsTotal - before;
                hits = Math.Min(hits, r.Count);
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"FIRST-RAY-CAST warmup={scene.RayWarmUpMs:0.0000} hits={hits} " +
                $"counted={loaded.SimulatorRayCasts.Casts + loaded.ScriptRayCasts.Casts} jit={string.Join(",", jit)} " +
                $"backend={string.Join(",", backendMs.Select(v => v.ToString("0.0000", CultureInfo.InvariantCulture)))} " +
                $"all={string.Join(",", allMs.Select(v => v.ToString("0.0000", CultureInfo.InvariantCulture)))}"));
        }
        finally { scene.Dispose(); }
    }

    // The child runs the build this test runs from, so it needs the same configuration.
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private (int exit, string output) RunChild()
    {
        string asm = Assembly.GetExecutingAssembly().Location;
        // The project folder: the part of the output path before its bin folder.
        string project = Path.GetDirectoryName(asm)!.Split(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)[0];
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Run where the repository's global.json applies, not wherever the parent test host was started.
            WorkingDirectory = project,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Configuration);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add("FullyQualifiedName~FirstRayCastTests.Child_casts");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("console;verbosity=detailed");
        psi.Environment[ChildVar] = "1";

        using var p = Process.Start(psi)!;
        Task<string> se = p.StandardError.ReadToEndAsync();
        string so = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10 * 60 * 1000);
        return (p.ExitCode, so + se.Result);
    }

    private static double[] Values(string line, string key)
    {
        string field = line.Split(' ').Single(f => f.StartsWith(key + "=", StringComparison.Ordinal));
        return field.Substring(key.Length + 1).Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    }

    [Fact]
    public void The_first_cast_after_the_scene_loads_costs_about_what_a_later_one_does()
    {
        var (exit, output) = RunChild();
        string line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("FIRST-RAY-CAST ", StringComparison.Ordinal));
        Assert.True(exit == 0 && line != null, $"child exit {exit}:\n{output}");
        _out.WriteLine(line);

        Assert.Equal(2, (int)Values(line, "hits")[0]);

        // Loading ran the warm-up (it reports the time it took) and charged its casts to no budget.
        Assert.True(Values(line, "warmup")[0] > 0.0, line);
        Assert.Equal(0, (int)Values(line, "counted")[0]);

        // Before the warm-up the first cast took 3-4 ms in the backend (what the simulator's budget is charged) and
        // 4-8 ms in all, against a few microseconds for a later one: most of it the JIT compiling the cast path. Now
        // loading has compiled it, so the first cast, like every later one, compiles nothing.
        double[] jit = Values(line, "jit");
        Assert.True(jit.All(n => n == 0), $"methods compiled by each cast, first to last: {string.Join(",", jit)}");
    }
}
