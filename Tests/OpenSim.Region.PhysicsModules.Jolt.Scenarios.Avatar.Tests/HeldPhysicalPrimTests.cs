/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical object that crossed into the region with avatars is held still until they have arrived
/// (PhysicsActor.HoldStill): no gravity, no drift, nothing that touches it moves it, and it still stops what stands on it.
/// When the hold ends (ReleaseHold) it moves on with the velocity it arrived with. Both step modes: one step per
/// heartbeat and a fixed 45 Hz rate.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class HeldPhysicalPrimTests
{
    private readonly ITestOutputHelper _out;
    public HeldPhysicalPrimTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float StartHeight = Ground + 30f;
    private const uint BoxId = 700u;
    private const uint AvatarId = 99u;

    private static readonly float StandHalf = JoltCharacter.StandHalfFor(Run.AvatarSize);

    private static JoltScene NewScene(float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        // the region has been running: an object that arrives keeps the horizontal velocity it had (a region that is
        // still loading drops it)
        scene.Simulate(Heartbeat);
        return scene;
    }

    // A 2 x 2 x 1 m physical box arriving with a velocity: made, its velocity set, as core does for an arriving object.
    private static JoltPrim Arrive(JoltScene scene, float z, Vector3 velocity)
    {
        var box = Assert.IsType<JoltPrim>(scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(),
            new Vector3(100f, 128f, z), new Vector3(2f, 2f, 1f), Quaternion.Identity, true, BoxId));
        box.Velocity = velocity;
        return box;
    }

    private static void Step(JoltScene scene, int heartbeats)
    {
        for (int i = 0; i < heartbeats; i++)
            scene.Simulate(Heartbeat);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Control_a_physical_box_with_a_velocity_falls_and_moves_when_it_is_not_held(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim box = Arrive(scene, StartHeight, new Vector3(4f, 0f, 0f));
            Vector3 start = box.Position;
            Step(scene, 22);
            Say($"HOLD control, rate {physicsRate:0}: {start} -> {box.Position}");
            Assert.True(box.Position.X > start.X + 3f, $"it did not move: {box.Position}");
            Assert.True(box.Position.Z < start.Z - 3f, $"it did not fall: {box.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_held_box_stays_where_it_is(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim box = Arrive(scene, StartHeight, new Vector3(4f, 0f, 0f));
            box.HoldStill();
            Vector3 start = box.Position;
            Step(scene, 33);
            Say($"HOLD held, rate {physicsRate:0}: {start} -> {box.Position}");
            Assert.True((box.Position - start).Length() < 0.02f, $"the held box moved: {start} -> {box.Position}");
            Assert.True(box.Velocity.Length() < 0.01f, $"the held box reports a velocity: {box.Velocity}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_released_box_moves_on_with_the_velocity_it_arrived_with(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim box = Arrive(scene, StartHeight, new Vector3(4f, 0f, 0f));
            box.HoldStill();
            Step(scene, 22);
            Vector3 held = box.Position;

            box.ReleaseHold();
            Step(scene, 11);   // one second
            Say($"HOLD released, rate {physicsRate:0}: {held} -> {box.Position}, velocity {box.Velocity}");
            Assert.True(box.Position.X > held.X + 3f, $"it did not move on with its velocity: {held} -> {box.Position}");
            Assert.True(box.Position.Z < held.Z - 1f, $"gravity did not act after the release: {held} -> {box.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_standing_on_a_held_box_is_supported_and_does_not_move_it(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim box = Arrive(scene, Ground + 4f, new Vector3(4f, 0f, 0f));
            box.HoldStill();
            Vector3 start = box.Position;
            float top = start.Z + 0.5f;
            PhysicsActor pa = scene.AddAvatar(AvatarId, "Test User", new Vector3(100f, 128f, top + StandHalf + 0.01f), Run.AvatarSize, 0f, false);
            pa.SubscribeEvents(100);
            var av = Assert.IsType<JoltCharacter>(pa);
            Step(scene, 33);
            Say($"HOLD avatar on held box, rate {physicsRate:0}: box {start} -> {box.Position}, avatar {av.Position}, supported {av.IsSupported}");
            Assert.True(av.IsSupported, $"not supported at {av.Position}");
            Assert.True((box.Position - start).Length() < 0.02f, $"the avatar moved the held box: {start} -> {box.Position}");
            Assert.True(av.Position.Z > top, $"the avatar sank into the held box: {av.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_selection_that_ends_during_the_hold_does_not_end_the_hold(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim box = Arrive(scene, StartHeight, new Vector3(4f, 0f, 0f));
            box.HoldStill();
            Vector3 start = box.Position;
            box.Selected = true;
            Step(scene, 5);
            box.Selected = false;
            Step(scene, 22);
            Assert.True((box.Position - start).Length() < 0.02f, $"the box moved after the selection ended: {start} -> {box.Position}");

            box.ReleaseHold();
            Step(scene, 11);
            Assert.True(box.Position.X > start.X + 3f, $"it did not move on once released: {start} -> {box.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_box_that_is_selected_when_the_hold_ends_stays_still(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim box = Arrive(scene, StartHeight, new Vector3(4f, 0f, 0f));
            box.HoldStill();
            Vector3 start = box.Position;
            box.Selected = true;
            box.ReleaseHold();
            Step(scene, 22);
            Assert.True((box.Position - start).Length() < 0.02f, $"the selected box moved: {start} -> {box.Position}");

            box.Selected = false;
            Step(scene, 11);
            Assert.True(box.Position.Z < start.Z - 1f, $"it did not fall once deselected: {start} -> {box.Position}");
        }
        finally { scene.Dispose(); }
    }
}
