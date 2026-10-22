/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using JoltPhysicsSharp;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What the backend and the scene give back when they are torn down or resized: native shapes, the region's console
/// commands and physics, a character made on a disposed backend, gravity under the simulation lock, the avatar
/// capsule's cook check, the supporting plane after a resize, and the buoyancy call. Serial: these read the
/// process-wide Foundation state and set MainConsole.Instance.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class TeardownGapTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;

    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    // ------------------------------------------------------------------ helpers

    private static HandleTable<JoltShapeRecord> ShapeTable(JoltPhysicsBackend b)
        => (HandleTable<JoltShapeRecord>)typeof(JoltPhysicsBackend).GetField("_shapes", Private)!.GetValue(b)!;

    private static JoltCharacterRecord CharacterRecord(JoltPhysicsBackend b, CharacterId id)
    {
        var table = (HandleTable<JoltCharacterRecord>)typeof(JoltPhysicsBackend).GetField("_characters", Private)!.GetValue(b)!;
        Assert.True(table.TryGet(id.Value, out JoltCharacterRecord rec));
        return rec;
    }

    private static JoltScene NewScene(string name)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, 25f);
        scene.InitialiseWithoutScene(name, Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    /// <summary>A console that keeps what is written to it.</summary>
    private sealed class CapturingConsole : MockConsole, ICommandConsole
    {
        public readonly List<string> Lines = new();
        public new void Output(string format) { lock (Lines) Lines.Add(format); }
        public new void Output(string format, params object[] components) { lock (Lines) Lines.Add(string.Format(format, components)); }
    }

    // ------------------------------------------------------------------ 1. native shapes

    [Fact]
    public void Shapes_still_registered_when_the_backend_is_disposed_are_released_by_it()
    {
        var t = new JoltTestBackend();
        ShapeId box = t.B.CreateBoxShape(new Vector3(0.5f));
        ShapeId scaled = t.B.CreateScaledShape(box, new Vector3(2f));
        ShapeId compound = t.B.CreateCompoundShape(new[]
        {
            new CompoundChild { Shape = box, Orientation = Quaternion.Identity },
            new CompoundChild { Shape = t.B.CreateSphereShape(0.5f), Position = new Vector3(2f, 0f, 0f), Orientation = Quaternion.Identity },
        });
        var natives = new List<Shape>();
        foreach (ShapeId id in new[] { box, scaled, compound })
        {
            Assert.True(ShapeTable(t.B).TryGet(id.Value, out JoltShapeRecord rec));
            natives.Add(rec.NativeShape!);
        }
        Assert.All(natives, s => Assert.False(s.IsDisposed));

        t.Dispose();   // the callers never released their shape handles

        Assert.All(natives, s => Assert.True(s.IsDisposed, "a shape handle still registered at Dispose was left to its finalizer"));
        Assert.Equal(0, ShapeTable(t.B).Count);
    }

    // ------------------------------------------------------------------ 2. removing a region

    [Fact]
    public void RemoveRegion_tears_down_the_regions_physics()
    {
        JoltScene scene = NewScene("Region A");
        Assert.NotNull(scene.Backend);

        scene.RemoveRegion(null);

        Assert.Null(scene.Backend);
        scene.Dispose();   // the scene's own close after the module's RemoveRegion: harmless
    }

    [Fact]
    public void RemoveRegion_unregisters_the_regions_console_handler_and_leaves_the_others()
    {
        ICommandConsole saved = MainConsole.Instance;
        var console = new CapturingConsole();
        MainConsole.Instance = console;
        try
        {
            var commands = new Commands();
            JoltScene a = NewScene("Region A");
            JoltScene b = NewScene("Region B");
            a.AttachConsole(commands);
            b.AttachConsole(commands);

            commands.Resolve(new[] { "jolt", "capacity" });
            Assert.Contains(console.Lines, l => l.Contains("'Region A'"));
            Assert.Contains(console.Lines, l => l.Contains("'Region B'"));
            console.Lines.Clear();

            a.RemoveRegion(null);
            commands.Resolve(new[] { "jolt", "capacity" });

            Assert.DoesNotContain(console.Lines, l => l.Contains("'Region A'"));
            Assert.Contains(console.Lines, l => l.Contains("'Region B'"));
            b.RemoveRegion(null);
        }
        finally { MainConsole.Instance = saved; }
    }

    [Fact]
    public void A_removed_regions_scene_is_not_kept_alive_by_the_console()
    {
        ICommandConsole saved = MainConsole.Instance;
        MainConsole.Instance = new CapturingConsole();
        try
        {
            var commands = new Commands();
            WeakReference weak = AttachAndRemove(commands);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(weak.IsAlive, "the console's command table still refers to the removed region's scene");
            GC.KeepAlive(commands);
        }
        finally { MainConsole.Instance = saved; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndRemove(Commands commands)
    {
        JoltScene scene = NewScene("Region C");
        scene.AttachConsole(commands);
        scene.RemoveRegion(null);
        return new WeakReference(scene);
    }

    // ------------------------------------------------------------------ 3. a character on a disposed backend

    [Fact]
    public void CreateCharacter_on_a_disposed_backend_is_refused_and_leaves_nothing()
    {
        var t = new JoltTestBackend();
        t.Dispose();
        CharacterDesc d = CharacterDesc.Default;
        d.Position = new Vector3(128f, 128f, 5f);

        CharacterId id = t.B.CreateCharacter(d);

        Assert.False(id.IsValid);
        Assert.Equal(0, ((HandleTable<JoltCharacterRecord>)typeof(JoltPhysicsBackend).GetField("_characters", Private)!.GetValue(t.B)!).Count);
        t.B.Dispose();   // and a second dispose stays harmless
    }

    // ------------------------------------------------------------------ 4. gravity under the simulation lock

    [Fact]
    public void SetGravity_waits_for_the_simulation_lock()
    {
        using var t = new JoltTestBackend();
        object simLock = typeof(JoltPhysicsBackend).GetField("_simLock", Private)!.GetValue(t.B)!;
        using var done = new ManualResetEventSlim();
        Thread call;
        lock (simLock)
        {
            call = new Thread(() => { t.B.SetGravity(new Vector3(0f, 0f, -3f)); done.Set(); });
            call.Start();
            Assert.False(done.Wait(400), "SetGravity changed the system while another thread held the simulation lock");
        }
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
        call.Join();
    }

    [Fact]
    public void SetGravity_after_the_backend_is_disposed_does_nothing()
    {
        var t = new JoltTestBackend();
        t.Dispose();
        t.B.SetGravity(new Vector3(0f, 0f, -3f));   // no native call on a destroyed system
    }

    // ------------------------------------------------------------------ 5. the avatar capsule's cook check

    [Fact]
    public void The_avatar_capsule_goes_through_the_failed_cook_check()
    {
        // A cook that fails cannot be provoked from outside: Jolt cooks any finite capsule. So the check is read in the
        // method body: both the capsule and its Z-up wrapper are passed to RequireCooked, as every other shape is.
        MethodInfo build = typeof(JoltPhysicsBackend).GetMethod("BuildStandingCapsule", BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] il = build.GetMethodBody()!.GetILAsByteArray()!;
        int checks = 0;
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28) continue;   // call
            try
            {
                MethodBase target = build.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                if (target?.Name == "RequireCooked") checks++;
            }
            catch (ArgumentException) { }
        }
        Assert.True(checks >= 2, $"BuildStandingCapsule calls RequireCooked {checks} times; the capsule and the wrapper each need one");
    }

    // ------------------------------------------------------------------ 6. the supporting plane after a resize

    private static CharacterId Avatar(JoltTestBackend t, float halfHeight, float radius)
    {
        CharacterDesc d = CharacterDesc.Default;
        d.Position = new Vector3(128f, 128f, 5f);
        d.CapsuleHalfHeight = halfHeight;
        d.CapsuleRadius = radius;
        d.UserData = 77;
        return t.B.CreateCharacter(d);
    }

    [Fact]
    public void A_resize_to_another_radius_rebuilds_the_supporting_plane_and_keeps_the_avatars_state()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        CharacterId id = Avatar(t, 0.45f, 0.30f);
        t.Step();
        JoltCharacterRecord rec = CharacterRecord(t.B, id);
        CharacterVirtual before = rec.Character!;
        Assert.Equal(0.30f, rec.SupportRadius);
        Vector3 position = before.Position;
        before.LinearVelocity = new Vector3(1f, 2f, 0.5f);

        t.B.SetCharacterShape(id, 0.40f, 0.55f);

        Assert.Equal(0.55f, rec.SupportRadius);
        Assert.Equal(0.55f, rec.CapsuleRadius);
        Assert.NotSame(before, rec.Character);
        Assert.True(before.IsDisposed);
        Assert.Equal(position.X, rec.Character!.Position.X, 3);
        Assert.Equal(position.Y, rec.Character.Position.Y, 3);
        Assert.Equal(position.Z, rec.Character.Position.Z, 3);
        Assert.Equal(new Vector3(1f, 2f, 0.5f), rec.Character.LinearVelocity);
        Assert.Equal(77u, rec.Character.UserData);

        // It still steps, still stands on the ground, and is still found by a query as itself.
        for (var i = 0; i < 5; i++)
            t.Step();
        Assert.True(t.B.TryGetCharacterState(id, out CharacterState state));
        Assert.True(JoltTestBackend.Finite(state.Position));
        Assert.True(t.B.RayCast(new Vector3(128f, 128f, 20f), -Vector3.UnitZ, 40f, QueryFilter.All, out RayHit hit));
        Assert.NotEqual(0u, hit.UserData);
    }

    [Fact]
    public void A_resize_that_keeps_the_radius_keeps_the_controller()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        CharacterId id = Avatar(t, 0.45f, 0.30f);
        JoltCharacterRecord rec = CharacterRecord(t.B, id);
        CharacterVirtual before = rec.Character!;

        t.B.SetCharacterShape(id, 0.70f, 0.30f);

        Assert.Same(before, rec.Character);
        Assert.Equal(0.70f, rec.CapsuleHalfHeight);
        Assert.Equal(0.30f, rec.SupportRadius);
    }

    [Fact]
    public void A_resized_avatar_still_blocks_another_avatar()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        CharacterId a = Avatar(t, 0.45f, 0.30f);
        CharacterId b = Avatar(t, 0.45f, 0.30f);
        t.B.SetCharacterTransform(b, new Vector3(130f, 128f, 1.0f), Quaternion.Identity);
        t.B.SetCharacterTransform(a, new Vector3(128f, 128f, 1.0f), Quaternion.Identity);
        t.B.SetCharacterShape(a, 0.45f, 0.50f);
        for (var i = 0; i < 20; i++)
        {
            t.B.SetCharacterMovement(a, new Vector3(3f, 0f, 0f), false, false);
            t.Step();
        }
        Assert.True(t.B.TryGetCharacterState(a, out CharacterState sa));
        Assert.True(t.B.TryGetCharacterState(b, out CharacterState sb));
        Assert.True(sa.Position.X < sb.Position.X, $"the resized avatar walked through the other one ({sa.Position.X} vs {sb.Position.X})");
    }

    // ------------------------------------------------------------------ 7. buoyancy

    [Fact]
    public void ApplyBuoyancy_with_finite_arguments_is_a_no_op()
    {
        using var t = new JoltTestBackend();
        t.Ground();
        BodyId body = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(128f, 128f, 10f));
        t.Step();
        t.B.TryGetBodyState(body, out BodyState before);

        t.B.ApplyBuoyancy(body, 12f, 1f, 0.1f, 0.1f);

        t.B.TryGetBodyState(body, out BodyState after);
        Assert.Equal(before.LinearVelocity, after.LinearVelocity);
        Assert.Equal(0, t.B.GetCapacityStats().RejectedNonFinite);
    }
}
