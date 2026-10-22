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
/// joltc mallocs both supporting-face arrays of a contact for its OnContactValidate callback and, before joltc
/// bc8a8002a, never frees them; the step calls it once per colliding body pair per step. The backend therefore
/// gives joltc no validate callback. Memory is a process-wide figure, so the run happens in a child test process:
/// a kicked pile of boxes with no collision listeners, stepped for a long time, must hold its private bytes
/// flat. With the callback in place the same run grew about 33 KB per step.
/// </summary>
public class ContactValidateMemoryTests
{
    private readonly ITestOutputHelper _out;
    public ContactValidateMemoryTests(ITestOutputHelper output) { _out = output; }

    private const string ChildVar = "JOLT_CONTACT_VALIDATE_MEMORY_CHILD";
    private const int Steps = 2000;

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

    // 100 boxes in a touching pile on a flat terrain, a share of them kicked up every step so they stay awake and
    // keep colliding. Returns the growth of private bytes from step 100 to the end.
    private static long RunPile(int steps)
    {
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
                }));
            }
            var bs = new BodyState[256];
            var cs = new CharacterState[8];
            var cr = new ContactReport[4096];
            long start = 0;
            for (int k = 0; k < steps; k++)
            {
                if (k == 100)
                    start = PrivateBytes();
                for (int i = k % 11; i < bodies.Count; i += 11)
                    b.SetBodyLinearVelocity(bodies[i], new Vector3(0f, 0f, 3f));
                b.Step(1f / 11f, bs, cs, cr);
            }
            return PrivateBytes() - start;
        }
        finally { b.Dispose(); }
    }

    /// <summary>The child entry: gated on an environment variable so it never runs in an ordinary test pass.</summary>
    [Fact]
    public void Child_runs()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildVar)))
            return;   // not the child; nothing to do

        RunPile(200);   // warm the process: first-use growth lands here
        long grown = RunPile(Steps);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CONTACT-VALIDATE-MEMORY grown_kb={grown / 1024} steps={Steps - 100} cleared={JoltPhysicsBackend.ContactValidateProcCleared}"));
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
        psi.ArgumentList.Add("FullyQualifiedName~ContactValidateMemoryTests.Child_runs");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("console;verbosity=detailed");
        psi.Environment[ChildVar] = "1";

        using var p = Process.Start(psi)!;
        Task<string> se = p.StandardError.ReadToEndAsync();
        string so = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10 * 60 * 1000);
        return (p.ExitCode, so + se.Result);
    }

    private static string Value(string line, string key)
        => line.Split(' ').Single(f => f.StartsWith(key + "=", StringComparison.Ordinal)).Substring(key.Length + 1);

    [Fact]
    public void Colliding_bodies_hold_memory_flat_over_a_long_run()
    {
        var (exit, output) = RunChild();
        string line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("CONTACT-VALIDATE-MEMORY ", StringComparison.Ordinal));
        Assert.True(exit == 0 && line != null, $"child exit {exit}:\n{output}");
        _out.WriteLine(line);

        Assert.Equal("True", Value(line, "cleared"));

        // With the validate callback the 1900 steps grew about 60 MB. The allowance (2 MB, about 1 KB per step) is
        // for heap noise. On Linux glibc keeps freed native memory in its arenas and the figure carries a little more of it:
        // 2.2 MB on CI's ubuntu Release runs. 8 MB is still far under the leak.
        long grownKb = long.Parse(Value(line, "grown_kb"), CultureInfo.InvariantCulture);
        long allowedKb = OperatingSystem.IsWindows() ? 2 * 1024 : 8 * 1024;
        Assert.True(grownKb < allowedKb, $"the pile grew {grownKb} KB over {Steps - 100} steps: {line}");
    }
}
