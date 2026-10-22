/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What a cast sees from where it starts and which hits it counts, as llCastRay documents it
/// (wiki.secondlife.com/wiki/LlCastRay): a cast does not detect a prim its line starts inside; the
/// same for an avatar a ray starts inside; and RC_MAX_HITS, the most hits to return, which a type the cast
/// rejects must not use up (the cast is filtered by type inside the engine, so a rejected body is never a hit).
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RayCastStartInsideAndRejectTests
{
    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const uint Big = 5001, Far = 5002;

    private const RayFilterFlags AllTypes =
        RayFilterFlags.land | RayFilterFlags.agent | RayFilterFlags.physical | RayFilterFlags.nonphysical | RayFilterFlags.BackFaceCull;

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt").Set("PhysicsStepRate", "0");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    private static PhysicsActor Box(JoltScene scene, uint localId, Vector3 position, Vector3 size, bool physical)
    {
        PhysicsActor pa = scene.AddPrimShape("test box", PrimitiveBaseShape.CreateBox(), position, size, Quaternion.Identity, physical, localId);
        if (physical)
            pa.Density = 1000f;
        return pa;
    }

    private static List<ContactResult> Cast5(JoltScene scene, Vector3 from, float length, int count, RayFilterFlags flags)
        => (List<ContactResult>)scene.RaycastWorld(from, Vector3.UnitX, length, count, flags);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cast_that_starts_inside_a_prim_does_not_hit_it_but_finds_what_lies_beyond(bool physical)
    {
        JoltScene scene = NewScene();
        try
        {
            var centre = new Vector3(128f, 128f, Ground + 3f);
            Box(scene, Big, centre, new Vector3(4f, 4f, 4f), false);
            Box(scene, Far, new Vector3(140f, 128f, Ground + 3f), new Vector3(1f, 1f, 1f), false);
            if (physical)
                Box(scene, 5003, new Vector3(128f, 128f, Ground + 10f), new Vector3(1f, 1f, 1f), true);   // keeps the scene stepping
            scene.Simulate(Heartbeat);

            // Four-argument entry (Phlox) and five-argument entry (YEngine), from inside the big box.
            foreach (List<ContactResult> hits in new[] { scene.RaycastWorld(centre, Vector3.UnitX, 20f, 4), Cast5(scene, centre, 20f, 4, AllTypes) })
            {
                Assert.Equal(new[] { Far }, hits.Select(h => h.ConsumerID).ToArray());
                Assert.True(hits[0].Depth > 10f, $"the far box is 11.5 m away, depth {hits[0].Depth}");
            }

            // Control: from outside the big box the same ray hits it first, then the far box.
            var outside = new Vector3(120f, 128f, Ground + 3f);
            Assert.Equal(new[] { Big, Far }, Cast5(scene, outside, 30f, 4, AllTypes).Select(h => h.ConsumerID).ToArray());
            Assert.Equal(new[] { Big, Far }, scene.RaycastWorld(outside, Vector3.UnitX, 30f, 4).Select(h => h.ConsumerID).ToArray());
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_cast_that_starts_inside_a_physical_prim_does_not_hit_it()
    {
        JoltScene scene = NewScene();
        try
        {
            // Held up on the ground it rests on: a 2 m cube sitting on the terrain.
            Box(scene, Big, new Vector3(128f, 128f, Ground + 1.01f), new Vector3(2f, 2f, 2f), true);
            Box(scene, Far, new Vector3(140f, 128f, Ground + 1f), new Vector3(1f, 1f, 1f), false);
            for (int i = 0; i < 6; i++)
                scene.Simulate(Heartbeat);
            var inside = new Vector3(128f, 128f, Ground + 1.05f);
            Assert.Equal(new[] { Far }, Cast5(scene, inside, 20f, 4, AllTypes).Where(h => h.ConsumerID != 0).Select(h => h.ConsumerID).ToArray());
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_cast_that_starts_inside_an_avatar_does_not_hit_it()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor av = scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            Box(scene, Far, new Vector3(140f, 128f, Ground + 1f), new Vector3(1f, 2f, 2f), false);
            for (int i = 0; i < 12; i++)
                scene.Simulate(Heartbeat);
            Vector3 centre = av.Position;
            List<ContactResult> hits = Cast5(scene, centre, 20f, 4, AllTypes);
            Assert.Equal(new[] { Far }, hits.Select(h => h.ConsumerID).ToArray());
            Assert.Equal(new[] { Far }, scene.RaycastWorld(centre, Vector3.UnitX, 20f, 4).Select(h => h.ConsumerID).ToArray());
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_type_the_cast_rejects_does_not_use_up_a_hit()
    {
        // An avatar stands between the cast's start and a wall. With RC_MAX_HITS 1 and RC_REJECT_AGENTS the cast is
        // filtered in the engine (no agent flag), so its one hit is the wall; asked for agents, its one hit is the avatar.
        JoltScene scene = NewScene();
        try
        {
            scene.AddAvatar(99u, "Test User", new Vector3(128f, 128f, Ground + 2f), Run.AvatarSize, 0f, false);
            Box(scene, Far, new Vector3(140f, 128f, Ground + 1f), new Vector3(1f, 4f, 4f), false);
            for (int i = 0; i < 12; i++)
                scene.Simulate(Heartbeat);
            var from = new Vector3(120f, 128f, Ground + 1f);

            List<ContactResult> noAgents = Cast5(scene, from, 30f, 1, RayFilterFlags.land | RayFilterFlags.physical | RayFilterFlags.nonphysical | RayFilterFlags.BackFaceCull);
            Assert.Equal(new[] { Far }, noAgents.Select(h => h.ConsumerID).ToArray());

            List<ContactResult> withAgents = Cast5(scene, from, 30f, 1, AllTypes);
            Assert.Single(withAgents);
            Assert.NotEqual(Far, withAgents[0].ConsumerID);
            Assert.True(withAgents[0].Depth < 10f, $"the avatar is about 8 m away, depth {withAgents[0].Depth}");
        }
        finally { scene.Dispose(); }
    }
}
