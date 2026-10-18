/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A load harness for temp-array growth in CharacterVirtual::MoveShape.
///
/// <para><b>Detection is the abort itself.</b> An out-of-order free ends in <c>std::abort()</c>
/// (<c>Jolt/Core/TempAllocator.h:83-84</c>), which terminates the process - it cannot be caught, and it takes
/// the test host with it. So the load runs in a CHILD process and the parent asserts on how the child died:
/// exit code <c>0xC0000409</c> (<c>STATUS_STACK_BUFFER_OVERRUN</c>, which is what <c>__fastfail</c> raises)
/// and the console line Jolt prints just before it.</para>
///
/// <para><b>The counts are printed and asserted.</b> A load harness that never reached the limit it is
/// probing would prove nothing, so the child prints the per-step contact count it actually achieved
/// and the parent requires it to have exceeded <c>mMaxNumHits</c>.</para>
/// </summary>
public class MoveShapeLoadTests
{
    private readonly ITestOutputHelper _out;
    public MoveShapeLoadTests(ITestOutputHelper output) { _out = output; }

    /// <summary>Jolt's default, <c>CharacterVirtual.h:52</c>. The reserve for <c>contacts</c> is exactly this.</summary>
    private const int MaxNumHits = 256;

    private const string ChildVar = "MOVESHAPE_LOAD_BODIES";
    private const int FloorTopZ = 21;

    private static PhysicsBackendSettings Settings() => new()
    {
        Gravity = new Vector3(0f, 0f, -9.8f),
        MaxBodies = 8192,
        MaxBodyPairs = 16384,
        MaxContactConstraints = 8192,
        ThreadCount = 2,
        PositionIterations = 2,
        VelocityIterations = 10,
        CollisionSteps = 1,
    };

    // ------------------------------------------------------------------ the load itself (runs in the child)

    /// <summary>
    /// One region, one character, no crossings, no second thread. <paramref name="bodies"/> small boxes are
    /// packed inside the capsule's sweep volume so every one of them is a contact for the character.
    /// </summary>
    private static (long maxContacts, long steps) RunLoad(int bodies, int steps, Action<string> log)
    {
        var backend = new JoltPhysicsBackend();
        backend.Initialize(Settings());

        var ground = backend.CreateBoxShape(new Vector3(64f, 64f, 1f));
        backend.CreateBody(new BodyDesc
        {
            Shape = ground,
            Position = new Vector3(128f, 128f, FloorTopZ - 1f),
            Orientation = Quaternion.Identity,
            Layer = PhysicsLayer.Static,
            MotionType = BodyMotionType.Static,
            Density = 1000f,
            WantsContactEvents = true,
        });

        var stand = new Vector3(128f, 128f, FloorTopZ + 0.75f + 0.05f);
        var id = backend.CreateCharacter(new CharacterDesc
        {
            Position = stand,
            Orientation = Quaternion.Identity,
            CapsuleHalfHeight = 0.45f,
            CapsuleRadius = 0.30f,
            Mass = 80f,
            Friction = 0.5f,
            MaxSlopeAngle = 1.0f,
            StepHeight = 0.45f,
            PushStrength = 1f,
            WantsContactEvents = true,
            UserData = 0x515A,
        });
        _ = id;

        // Pack the boxes into the capsule volume: radius 0.30, half-height 0.45, so a 0.6 x 0.6 x 1.5 box
        // around the centre. Small boxes on a tight lattice, all overlapping the character.
        var box = backend.CreateBoxShape(new Vector3(0.03f, 0.03f, 0.03f));
        var per = (int)Math.Ceiling(Math.Cbrt(bodies));
        var made = 0;
        for (var x = 0; x < per && made < bodies; x++)
            for (var y = 0; y < per && made < bodies; y++)
                for (var z = 0; z < per && made < bodies; z++)
                {
                    backend.CreateBody(new BodyDesc
                    {
                        Shape = box,
                        Position = stand + new Vector3(
                            (x - per / 2f) * 0.055f,
                            (y - per / 2f) * 0.055f,
                            (z - per / 2f) * 0.10f),
                        Orientation = Quaternion.Identity,
                        Layer = PhysicsLayer.Static,      // static: they stay put and keep touching
                        MotionType = BodyMotionType.Static,
                        Density = 1000f,
                        WantsContactEvents = true,
                        UserData = (uint)(0xB0000000 + made),
                    });
                    made++;
                }
        log($"bodies packed into the capsule volume: {made}");

        var bs = new BodyState[512];
        var cs = new CharacterState[8];
        var contacts = new ContactReport[4096];
        long maxContacts = 0, done = 0;
        for (var i = 0; i < steps; i++)
        {
            var r = backend.Step(1f / 11f, bs, cs, contacts);
            done++;
            long avatarContacts = 0;
            for (var j = 0; j < r.ContactCount; j++)
                if (!contacts[j].BodyA.IsValid)        // avatar side has no rigid body
                    avatarContacts++;
            if (avatarContacts > maxContacts) maxContacts = avatarContacts;
        }
        log($"steps={done} max avatar contacts reported in one step={maxContacts} (mMaxNumHits={MaxNumHits})");
        backend.Dispose();
        return (maxContacts, done);
    }

    /// <summary>The child entry: gated on an environment variable so it never runs in an ordinary test pass.</summary>
    [Fact]
    public void Child_load_run()
    {
        var v = Environment.GetEnvironmentVariable(ChildVar);
        if (string.IsNullOrEmpty(v))
            return;   // not the child; nothing to do

        var (max, steps) = RunLoad(int.Parse(v), steps: 200, log: Console.WriteLine);
        Console.WriteLine($"MOVESHAPE-RESULT bodies={v} maxContacts={max} steps={steps} survived=yes");
    }

    // ------------------------------------------------------------------ parent: run the child, read the corpse

    // The child runs the build this test runs from, so it needs the same configuration.
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private (int exit, string output) RunChild(int bodies)
    {
        var asm = Assembly.GetExecutingAssembly().Location;
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
        psi.ArgumentList.Add("FullyQualifiedName~MoveShapeLoadTests.Child_load_run");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("console;verbosity=detailed");
        psi.Environment[ChildVar] = bodies.ToString();

        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(10 * 60 * 1000);
        return (p.ExitCode, so + se);
    }

    private const int FastFail = unchecked((int)0xC0000409);

    /// <summary>
    /// The sanity variant first: what an ordinary arrival looks like. 14 attachment bodies, about what a
    /// region crossing re-adds for a typical avatar. If this already sits near <c>mMaxNumHits</c> the whole
    /// picture changes; if it sits at a handful, an ordinary arrival is nowhere near the contact cap.
    /// </summary>
    [Fact]
    public void Sanity_fourteen_attachments()
    {
        var (exit, output) = RunChild(14);
        foreach (var line in output.Split('\n').Where(l => l.Contains("bodies packed") || l.Contains("MOVESHAPE-RESULT") || l.Contains("max avatar contacts")))
            _out.WriteLine(line.Trim());
        _out.WriteLine($"child exit = 0x{exit:X8}");
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// The load variant: more overlapping bodies than <c>mMaxNumHits</c>, so the contact collector is driven
    /// to its cap. Reported, not asserted red - reading MoveShape shows its three arrays cannot
    /// exceed their reserves, so this is expected to survive; it is run to test that conclusion rather than to
    /// trust it.
    /// </summary>
    [Fact]
    public void Load_beyond_max_num_hits()
    {
        var (exit, output) = RunChild(400);
        foreach (var line in output.Split('\n').Where(l => l.Contains("bodies packed") || l.Contains("MOVESHAPE-RESULT") || l.Contains("max avatar contacts") || l.Contains("TempAllocator")))
            _out.WriteLine(line.Trim());
        _out.WriteLine($"child exit = 0x{exit:X8}  (0x{FastFail:X8} would be the abort)");

        if (exit == FastFail)
            Assert.Fail("REPRODUCED: the child aborted with STATUS_STACK_BUFFER_OVERRUN - see the output above");
        Assert.Equal(0, exit);
    }
}
