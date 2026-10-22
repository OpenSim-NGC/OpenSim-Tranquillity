/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A contact whose bodies listen for collisions gets an impulse estimate (JoltPhysicsSharp's
/// EstimateCollisionResponse). joltc mallocs that estimate's per-point impulse array on every call, so the
/// backend must free it. Memory is a process-wide figure, so the run happens in a child test process: a kicked pile
/// of listening boxes, stepped for a long time. After a warm-up, private bytes are read (each after a forced
/// collection) every few hundred steps, and the median growth per step over those intervals must stay below a small
/// limit. A leak grows every interval: unfreed, every listening contact report kept its array, about 34 KB per step
/// for this pile. The median lets one interval hold a one-time rise: on Linux the C runtime's heap takes one step of a
/// few MB part way through the run and then stays flat.
/// </summary>
public class ContactEstimateMemoryTests
{
    private readonly ITestOutputHelper _out;
    public ContactEstimateMemoryTests(ITestOutputHelper output) { _out = output; }

    private const string ChildVar = "JOLT_CONTACT_ESTIMATE_MEMORY_CHILD";
    private const int WarmUpSteps = 300;
    private const int WindowSteps = 2000;
    private const int ReadEvery = 250;

    // The limit on the median growth per step of the window's intervals. Freed, the pile measures within a few hundred
    // bytes of zero per step either way; unfreed, it grew about 34 KB per step in every interval.
    private const long LimitBytesPerStep = 1024;

    private static PhysicsBackendSettings Settings() => new()
    {
        Gravity = new Vector3(0f, 0f, -9.8f),
        MaxBodies = 65536,
        MaxBodyPairs = 65536,
        MaxContactConstraints = 10240,
        CollisionSteps = 6,
        PositionIterations = 2,
        VelocityIterations = 10,
    };

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64;
    }

    // 100 listening boxes in a touching pile on a flat terrain, a share of them kicked up every step so contacts keep
    // beginning and persisting. Reads private bytes every ReadEvery steps from the end of the warm-up on; returns the
    // readings and the number of contact reports the steps delivered.
    private static (List<long> readings, long reports) RunPile(int warmUp, int window)
    {
        var readings = new List<long>();
        long reports = 0;
        var b = new JoltPhysicsBackend();
        b.Initialize(Settings());
        try
        {
            float[] field = new float[257 * 257];
            Array.Fill(field, 25f);
            ShapeId terrain = b.CreateHeightFieldShape(field, 257, 257, new Vector3(1f, 1f, 1f));
            b.SetTerrain(terrain, Vector3.Zero);
            ShapeId box = b.CreateBoxShape(new Vector3(0.4f, 0.4f, 0.4f));
            var bodies = new List<BodyId>();
            for (int i = 0; i < 100; i++)
            {
                int x = i % 6, y = (i / 6) % 6, z = i / 36;
                bodies.Add(b.CreateBody(new BodyDesc
                {
                    Shape = box,
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(120f + x * 0.85f + (z % 2) * 0.3f, 120f + y * 0.85f + (z % 2) * 0.3f, 26f + z * 0.9f),
                    Layer = PhysicsLayer.Dynamic,
                    MotionType = BodyMotionType.Dynamic,
                    Density = 1000f,
                    Friction = 0.5f,
                    GravityFactor = 1f,
                    WantsContactEvents = true,
                }));
            }
            var bs = new BodyState[256];
            var cs = new CharacterState[8];
            var cr = new ContactReport[4096];
            for (int k = 0; k < warmUp + window; k++)
            {
                // The run's first forced collection gives back several MB the warm-up left; that one is not a reading,
                // or a drop at the window's start would hide growth after it.
                if (k == warmUp)
                    PrivateBytes();
                if (k >= warmUp && (k - warmUp) % ReadEvery == 0)
                    readings.Add(PrivateBytes());
                for (int i = k % 11; i < bodies.Count; i += 11)
                    b.SetBodyLinearVelocity(bodies[i], new Vector3(0f, 0f, 3f));
                reports += b.Step(1f / 11f, bs, cs, cr).ContactCount;
            }
            readings.Add(PrivateBytes());
            return (readings, reports);
        }
        finally { b.Dispose(); }
    }

    /// <summary>The child entry: gated on an environment variable so it never runs in an ordinary test pass.</summary>
    [Fact]
    public void Child_runs()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildVar)))
            return;   // not the child; nothing to do

        RunPile(0, 200);   // warm the process: first-use growth lands here
        var (readings, reports) = RunPile(WarmUpSteps, WindowSteps);
        long[] intervals = readings.Zip(readings.Skip(1), (a, b) => (b - a) / ReadEvery).Order().ToArray();
        long median = (intervals[(intervals.Length - 1) / 2] + intervals[intervals.Length / 2]) / 2;
        long overall = (readings[^1] - readings[0]) / WindowSteps;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CONTACT-ESTIMATE-MEMORY median_bytes_per_step={median} overall_bytes_per_step={overall} reports={reports} " +
            $"window={WindowSteps} readings_kb={string.Join(",", readings.Select(r => r / 1024))}"));
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
        psi.ArgumentList.Add("FullyQualifiedName~ContactEstimateMemoryTests.Child_runs");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("console;verbosity=detailed");
        psi.Environment[ChildVar] = "1";

        using var p = Process.Start(psi)!;
        Task<string> se = p.StandardError.ReadToEndAsync();
        string so = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10 * 60 * 1000);
        return (p.ExitCode, so + se.Result);
    }

    private static long Value(string line, string key)
        => long.Parse(line.Split(' ').Single(f => f.StartsWith(key + "=", StringComparison.Ordinal)).Substring(key.Length + 1), CultureInfo.InvariantCulture);

    [Fact]
    public void Listening_contacts_hold_memory_flat_over_a_long_run()
    {
        var (exit, output) = RunChild();
        string line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("CONTACT-ESTIMATE-MEMORY ", StringComparison.Ordinal));
        Assert.True(exit == 0 && line != null, $"child exit {exit}:\n{output}");
        _out.WriteLine(line);

        // Every report of this pile is a listening contact, which gets an estimate: thousands of them, so the path
        // was taken.
        Assert.True(Value(line, "reports") > 10000, line);

        long median = Value(line, "median_bytes_per_step");
        Assert.True(median < LimitBytesPerStep,
            $"private bytes grew a median {median} bytes per step over {WindowSteps / ReadEvery} intervals of {ReadEvery} steps (limit {LimitBytesPerStep}): {line}");
    }
}
