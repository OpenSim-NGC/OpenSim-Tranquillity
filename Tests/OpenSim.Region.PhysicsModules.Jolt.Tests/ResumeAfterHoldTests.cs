/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An object that crossed in with riders and waited for them is moved on by its velocity times the time it waited when the
/// wait ends, under <c>[Jolt] BasicForcesBehaviour = InWorldz</c> (PhysicsActor.ReleaseHold(float); Halcyon's
/// PhysxPrim.DoResumeInterpolation does the same). It is kept above the ground, and not moved into another body: it
/// stays where it was held then. Under SecondLife it only lets go, as before. Core caps the time it hands over, so the
/// engine takes the time as given.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ResumeAfterHoldTests
{
    private readonly ITestOutputHelper _out;
    public ResumeAfterHoldTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float Height = Ground + 30f;
    private const uint BoxId = 700u;
    private const uint WallId = 701u;

    private static JoltScene NewScene(string basicForces, float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(CultureInfo.InvariantCulture));
        jolt.Set("BasicForcesBehaviour", basicForces);
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        scene.Simulate(Heartbeat);
        return scene;
    }

    // A 2 x 2 x 1 m physical box arriving with a velocity, held as core holds an object that crossed in with riders.
    private static JoltPrim ArriveHeld(JoltScene scene, float z, Vector3 velocity)
    {
        var box = Assert.IsType<JoltPrim>(scene.AddPrimShape("box", PrimitiveBaseShape.CreateBox(),
            new Vector3(100f, 128f, z), new Vector3(2f, 2f, 1f), Quaternion.Identity, true, BoxId));
        box.Velocity = velocity;
        box.HoldStill();
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
    public void Under_InWorldz_a_box_released_after_a_second_is_a_second_of_travel_on(float physicsRate)
    {
        JoltScene scene = NewScene("InWorldz", physicsRate);
        try
        {
            JoltPrim box = ArriveHeld(scene, Height, new Vector3(4f, 3f, 0f));
            Step(scene, 11);
            Vector3 held = box.Position;

            box.ReleaseHold(1f);

            Vector3 moved = box.Position - held;
            Say($"RESUME InWorldz, rate {physicsRate:0}: held {held}, moved {moved} by the release alone");
            Assert.Equal(4.0, moved.X, 0.01);
            Assert.Equal(3.0, moved.Y, 0.01);
            Assert.Equal(0.0, moved.Z, 0.01);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Under_InWorldz_the_box_goes_on_with_its_velocity_from_the_new_place()
    {
        JoltScene scene = NewScene("InWorldz", 0f);
        try
        {
            JoltPrim box = ArriveHeld(scene, Height, new Vector3(4f, 0f, 0f));
            Vector3 held = box.Position;
            box.ReleaseHold(2f);
            Vector3 resumed = box.Position;

            Step(scene, 11);

            Say($"RESUME onward: held {held}, resumed {resumed}, one second later {box.Position}, velocity {box.Velocity}");
            Assert.Equal(8.0, resumed.X - held.X, 0.01);
            Assert.True(box.Position.X > resumed.X + 3f, $"it did not go on with its velocity: {resumed} -> {box.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Under_SecondLife_the_release_does_not_move_the_box_on(float physicsRate)
    {
        JoltScene scene = NewScene("SecondLife", physicsRate);
        try
        {
            JoltPrim box = ArriveHeld(scene, Height, new Vector3(4f, 3f, 0f));
            Step(scene, 11);
            Vector3 held = box.Position;

            box.ReleaseHold(1f);

            Vector3 moved = box.Position - held;
            Say($"RESUME SecondLife, rate {physicsRate:0}: held {held}, moved {moved} by the release alone");
            Assert.True(moved.Length() < 0.01f, $"the box was moved on: {moved}");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_release_with_no_time_waited_moves_nothing()
    {
        JoltScene scene = NewScene("InWorldz", 0f);
        try
        {
            JoltPrim box = ArriveHeld(scene, Height, new Vector3(4f, 0f, 0f));
            Vector3 held = box.Position;

            box.ReleaseHold(0f);

            Assert.True((box.Position - held).Length() < 0.01f, $"moved: {held} -> {box.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Under_InWorldz_a_box_that_would_end_below_the_ground_is_lifted_onto_it()
    {
        JoltScene scene = NewScene("InWorldz", 0f);
        try
        {
            // 3 m above the ground, falling at 10 m/s: a second on is 7 m under it
            JoltPrim box = ArriveHeld(scene, Ground + 3f, new Vector3(4f, 0f, -10f));
            Vector3 held = box.Position;

            box.ReleaseHold(1f);

            Say($"RESUME ground: held {held}, after {box.Position}");
            Assert.Equal(4.0, box.Position.X - held.X, 0.01);
            Assert.True(box.Position.Z >= Ground + 0.4f, $"it is under the ground: {box.Position}");
            Assert.True(box.Position.Z < held.Z, $"it was not moved down at all: {held} -> {box.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void Under_InWorldz_a_box_that_would_end_inside_another_body_stays_where_it_was_held()
    {
        JoltScene scene = NewScene("InWorldz", 0f);
        try
        {
            // a wall 1 m thick across the way, 4 m ahead: a second at 4 m/s ends inside it
            scene.AddPrimShape("wall", PrimitiveBaseShape.CreateBox(), new Vector3(104f, 128f, Height), new Vector3(1f, 8f, 8f),
                Quaternion.Identity, false, WallId);
            Step(scene, 2);
            JoltPrim box = ArriveHeld(scene, Height, new Vector3(4f, 0f, 0f));
            Vector3 held = box.Position;

            box.ReleaseHold(1f);

            Say($"RESUME wall: held {held}, after {box.Position}");
            Assert.True((box.Position - held).Length() < 0.01f, $"it was moved into the wall: {held} -> {box.Position}");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_box_is_moved_on_when_the_way_is_clear_of_a_wall_that_is_not_in_it()
    {
        JoltScene scene = NewScene("InWorldz", 0f);
        try
        {
            // the same wall, 12 m ahead: the box ends 4 m on, well short of it
            scene.AddPrimShape("wall", PrimitiveBaseShape.CreateBox(), new Vector3(112f, 128f, Height), new Vector3(1f, 8f, 8f),
                Quaternion.Identity, false, WallId);
            Step(scene, 2);
            JoltPrim box = ArriveHeld(scene, Height, new Vector3(4f, 0f, 0f));
            Vector3 held = box.Position;

            box.ReleaseHold(1f);

            Assert.Equal(4.0, box.Position.X - held.X, 0.01);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData("InWorldz", true)]
    [InlineData("SecondLife", false)]
    public void The_actor_asks_for_the_wait_before_crossing_back_under_InWorldz_only(string basicForces, bool expected)
    {
        JoltScene scene = NewScene(basicForces, 0f);
        try
        {
            JoltPrim box = ArriveHeld(scene, Height, Vector3.Zero);
            Assert.Equal(expected, box.WaitsBeforeCrossingBack);
        }
        finally { scene.Dispose(); }
    }
}
