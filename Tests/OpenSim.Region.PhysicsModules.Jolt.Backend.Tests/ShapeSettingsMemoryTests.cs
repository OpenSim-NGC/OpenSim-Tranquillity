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
/// A shape cooked and released gives back all its native memory. JoltPhysicsSharp 2.19.1 builds the settings of a
/// convex hull, a mesh, a scaled shape and the terrain's Z-up wrapper so that their Dispose frees nothing native; the
/// settings kept their copy of the input and the shape they cached, so every cook of those kinds stayed in memory
/// (a 257-sample terrain about 240 KB, every time a region loaded or its terrain changed). Memory is a process-wide
/// figure, so the cooks run in a child test process, which reports how much each kind grew.
/// </summary>
public class ShapeSettingsMemoryTests
{
    private readonly ITestOutputHelper _out;
    public ShapeSettingsMemoryTests(ITestOutputHelper output) { _out = output; }

    private const string ChildVar = "JOLT_SHAPE_SETTINGS_MEMORY_CHILD";
    private const int Cooks = 300;

    // The process's private bytes outside the managed heap: the native memory the cooks leave. The managed heap's
    // own size moves with the cooks' temporary arrays, so it is taken out.
    private static long NativeBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 - GC.GetGCMemoryInfo().TotalCommittedBytes;
    }

    private static readonly float[] Field = MakeField();
    private static float[] MakeField()
    {
        var f = new float[257 * 257];
        for (int i = 0; i < f.Length; i++)
            f[i] = 25f + (i % 7) * 0.1f;
        return f;
    }

    // A 40 x 40 grid of quads: 3200 triangles.
    private static (Vector3[] vertices, int[] indices) Grid()
    {
        const int n = 41;
        var v = new Vector3[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                v[y * n + x] = new Vector3(x * 0.25f, y * 0.25f, (x + y) % 3 * 0.05f);
        var idx = new List<int>();
        for (int y = 0; y < n - 1; y++)
            for (int x = 0; x < n - 1; x++)
            {
                int a = y * n + x;
                idx.AddRange(new[] { a, a + 1, a + n, a + 1, a + n + 1, a + n });
            }
        return (v, idx.ToArray());
    }

    private static Vector3[] Sphere()
    {
        var pts = new List<Vector3>();
        for (int i = 0; i < 16; i++)
            for (int j = 0; j < 8; j++)
            {
                float t = i * MathF.PI / 8f, p = (j + 0.5f) * MathF.PI / 8f;
                pts.Add(new Vector3(MathF.Cos(t) * MathF.Sin(p), MathF.Sin(t) * MathF.Sin(p), MathF.Cos(p)));
            }
        return pts.ToArray();
    }

    // Cooks one kind of shape on one region's backend, releasing each: `warm` cooks, then `cooks` more. The growth of
    // native memory over the second lot: the first takes the native heap's one-time growth.
    private static long Run(string kind, int warm, int cooks)
    {
        var b = new JoltPhysicsBackend();
        b.Initialize(PhysicsBackendSettings.Default);
        try
        {
            (Vector3[] verts, int[] indices) = Grid();
            Vector3[] hull = Sphere();
            ShapeId box = b.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));
            long start = 0;
            for (int i = 0; i < warm + cooks; i++)
            {
                if (i == warm)
                    start = NativeBytes();
                ShapeId s = kind switch
                {
                    "terrain" => b.CreateHeightFieldShape(Field, 257, 257, new Vector3(1f, 1f, 1f)),
                    "mesh" => b.CreateMeshShape(verts, indices),
                    "hull" => b.CreateConvexHullShape(hull),
                    "scaled" => b.CreateScaledShape(box, new Vector3(1f + i % 5, 2f, 3f)),
                    _ => throw new ArgumentException(kind),
                };
                b.ReleaseShape(s);
            }
            return NativeBytes() - start;
        }
        finally { b.Dispose(); }
    }

    private static readonly string[] Kinds = { "terrain", "mesh", "hull", "scaled" };

    /// <summary>The child entry: gated on an environment variable so it never runs in an ordinary test pass.</summary>
    [Fact]
    public void Child_cooks()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildVar)))
            return;   // not the child; nothing to do

        var parts = new List<string>();
        foreach (string kind in Kinds)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{kind}_kb={Run(kind, Cooks, Cooks) / 1024}"));
        Console.WriteLine("SHAPE-SETTINGS-MEMORY " + string.Join(" ", parts));
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
        psi.ArgumentList.Add("FullyQualifiedName~ShapeSettingsMemoryTests.Child_cooks");
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
        => long.Parse(line.Split(' ').Single(f => f.StartsWith(key + "=", StringComparison.Ordinal)).Substring(key.Length + 1),
                      CultureInfo.InvariantCulture);

    [Fact]
    public void A_shape_cooked_and_released_gives_its_memory_back()
    {
        var (exit, output) = RunChild();
        string line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("SHAPE-SETTINGS-MEMORY ", StringComparison.Ordinal));
        Assert.True(exit == 0 && line != null, $"child exit {exit}:\n{output}");
        _out.WriteLine(line);

        // Each kind is cooked and released 300 times. The allowance (4 MB, about 14 KB a cook) is for heap noise;
        // with the settings left behind, 300 terrain cooks grew about 70 MB and 300 mesh cooks about 46 MB.
        foreach (string kind in Kinds)
        {
            long grownKb = Value(line, kind + "_kb");
            Assert.True(grownKb < 4 * 1024, $"{Cooks} {kind} cooks grew {grownKb} KB: {line}");
        }
    }
}
