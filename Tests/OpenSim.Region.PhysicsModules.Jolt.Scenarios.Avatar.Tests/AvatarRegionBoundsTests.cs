/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An avatar stays inside its region. ubODE forces the avatar's position into 0.01 .. size - 0.01 on both axes after every
/// step (ODECharacter, "check outbounds forcing to be in world"), so core's border check only ever sees an avatar that is
/// walking or flying at an edge, never one that was left outside it. An avatar that was left outside (stood up from a seat
/// that waits past the edge, a position core handed over beyond it) makes ScenePresence.CheckForBorderCrossing start a
/// crossing every frame; with a neighbour that cannot take the avatar each crossing holds the avatar in transit for as long
/// as the neighbour is waited for, and a teleport request made meanwhile is dropped (Scene.RequestTeleportLocation).
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarRegionBoundsTests
{
    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float Margin = 0.01f;     // ODECharacter: 0.01 m inside each edge

    private static JoltScene NewScene(float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", physicsRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    private static JoltCharacter Arrive(JoltScene scene, Vector3 at, bool flying = false)
        => Assert.IsType<JoltCharacter>(scene.AddAvatar(99u, "Test User", at, Run.AvatarSize, 0f, flying));

    private static void Step(JoltScene scene, int heartbeats)
    {
        for (int i = 0; i < heartbeats; i++)
            scene.Simulate(Heartbeat);
    }

    private static void AssertInside(JoltCharacter av)
    {
        Vector3 p = av.Position;
        Assert.InRange(p.X, Margin - 0.001f, Size - Margin + 0.001f);
        Assert.InRange(p.Y, Margin - 0.001f, Size - Margin + 0.001f);
    }

    [Theory]
    [InlineData(0f, 256.15f, 128f)]     // just past the east edge: where an avatar stands up from a seat that waits there
    [InlineData(0f, 300f, 128f)]
    [InlineData(0f, -2f, 128f)]         // west of the region
    [InlineData(0f, 128f, -0.5f)]       // south
    [InlineData(0f, 128f, 258f)]        // north
    [InlineData(0f, -2f, 258f)]         // a corner with no region diagonally past it
    [InlineData(45f, 256.15f, 128f)]
    [InlineData(45f, -2f, 258f)]
    public void An_avatar_put_outside_the_region_is_brought_inside_on_the_next_step(float physicsRate, float x, float y)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltCharacter av = Arrive(scene, new Vector3(x, y, Ground + 3f));
            Step(scene, 3);
            AssertInside(av);
            Step(scene, 22);
            AssertInside(av);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_avatar_whose_position_core_sets_outside_the_region_is_brought_inside()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground + 3f));
            Step(scene, 22);
            av.Position = new Vector3(-5f, 261f, Ground + 3f);
            Step(scene, 3);
            AssertInside(av);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_avatar_walking_at_an_edge_stops_inside_it_and_keeps_its_outward_velocity()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            JoltCharacter av = Arrive(scene, new Vector3(250f, 128f, Ground + 1.5f));
            Step(scene, 22);
            av.TargetVelocity = new Vector3(8f, 0f, 0f);
            for (int i = 0; i < 44; i++)
            {
                scene.Simulate(Heartbeat);
                AssertInside(av);
            }
            // core's border check reads position plus velocity times the frame time, so the outward push must still show
            Assert.True(av.Position.X + av.Velocity.X * Heartbeat >= Size || av.Position.X > Size - 1f,
                $"avatar at {av.Position} with velocity {av.Velocity}");
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_avatar_inside_the_region_is_not_moved()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            JoltCharacter av = Arrive(scene, new Vector3(100f, 60f, Ground + 1.5f));
            Step(scene, 44);
            Assert.InRange(av.Position.X, 99.99f, 100.01f);
            Assert.InRange(av.Position.Y, 59.99f, 60.01f);
        }
        finally { scene.Dispose(); }
    }
}
