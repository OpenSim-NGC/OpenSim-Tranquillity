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
/// What an avatar reports it stands on. The terrain is a body in the solve, so the avatar's ground body alone does
/// not tell the terrain from a prim. Two things carry the answer to the simulator: the ground flags
/// (CollidingGround, CollidingObj, IsColliding) and the collision updates ScenePresence.PhysicsCollisionUpdate reads,
/// where collider 0 is land (land_collision on the avatar's attachments) and any other id is an object (collision).
/// ScenePresence subscribes the avatar to collision events right after AddAvatar; each test does the same.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarGroundTests
{
    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float PlatformTop = Ground + 3f;
    private const uint AvatarId = 99u;
    private const uint PrimId = 500u;

    private static readonly float StandHalf = JoltCharacter.StandHalfFor(Run.AvatarSize);

    private static JoltScene NewScene(float physicsRate)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        // Always set: 0 is one step per heartbeat, and the module's own default is 45 Hz.
        jolt.Set("PhysicsStepRate", physicsRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var scene = new JoltScene();
        scene.Initialise(config);
        float[] heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    private static PhysicsActor Box(JoltScene scene, Vector3 centre, Vector3 size, bool physical)
        => scene.AddPrimShape("prim", PrimitiveBaseShape.CreateBox(), centre, size, Quaternion.Identity, physical, PrimId);

    // ScenePresence.AddToPhysicalScene: AddAvatar, then SubscribeEvents(100) on the new actor.
    private static JoltCharacter Arrive(JoltScene scene, Vector3 at, bool flying, Recorder rec)
    {
        PhysicsActor pa = scene.AddAvatar(AvatarId, "Test User", at, Run.AvatarSize, 0f, flying);
        rec.Listen(pa);
        pa.SubscribeEvents(100);
        return Assert.IsType<JoltCharacter>(pa);
    }

    private static void Step(JoltScene scene, int heartbeats)
    {
        for (int i = 0; i < heartbeats; i++)
            scene.Simulate(Heartbeat);
    }

    private sealed class Recorder
    {
        public readonly List<uint[]> Updates = new();   // one entry per update delivered, the colliders it lists
        public readonly List<ContactPoint> LandContacts = new();
        public void Listen(PhysicsActor pa)
        {
            pa.OnCollisionUpdate += e =>
            {
                var u = (CollisionEventUpdate)e;
                Updates.Add(u.m_objCollisionList.Keys.OrderBy(k => k).ToArray());
                if (u.m_objCollisionList.TryGetValue(0u, out ContactPoint land))
                    LandContacts.Add(land);
            };
        }
        public void Clear() { Updates.Clear(); LandContacts.Clear(); }
    }

    // What ScenePresence.RaiseCollisionScriptEvents makes of consecutive updates: land (collider 0) gives
    // land_start / land / land_end, any other collider start / collision / end.
    private static List<string> Events(List<uint[]> updates)
    {
        var events = new List<string>();
        uint[] last = Array.Empty<uint>();
        foreach (uint[] u in updates)
        {
            foreach (uint id in u)
                events.Add(id == 0 ? (last.Contains(id) ? "land" : "land_start") : (last.Contains(id) ? $"collision {id}" : $"start {id}"));
            foreach (uint id in last)
                if (!u.Contains(id))
                    events.Add(id == 0 ? "land_end" : $"end {id}");
            last = u;
        }
        return events;
    }

    private static void AssertOnGround(JoltCharacter av)
    {
        Assert.True(av.IsColliding, $"not colliding at {av.Position}");
        Assert.True(av.GroundIsTerrain, "ground body is not the terrain");
        Assert.True(av.CollidingGround, "standing on the terrain, CollidingGround is false");
        Assert.False(av.CollidingObj, "standing on the terrain, CollidingObj is true");
    }

    private static void AssertOnObject(JoltCharacter av)
    {
        Assert.True(av.IsColliding, $"not colliding at {av.Position}");
        Assert.False(av.GroundIsTerrain, "standing on a prim, the ground body reads as the terrain");
        Assert.True(av.CollidingObj, "standing on a prim, CollidingObj is false");
        Assert.False(av.CollidingGround, "standing on a prim, CollidingGround is true");
    }

    // Every update of a steady stand lists exactly the one collider, one update per heartbeat, none empty.
    private static void AssertSteadyContact(Recorder rec, uint collider, int heartbeats)
    {
        Assert.Equal(heartbeats, rec.Updates.Count);
        foreach (uint[] u in rec.Updates)
            Assert.Equal(new[] { collider }, u);
    }

    // ------------------------------------------------------------------ the flags

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Standing_on_the_terrain_reads_as_ground_and_reports_land(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            var rec = new Recorder();
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground + StandHalf), false, rec);
            Step(scene, 22);
            AssertOnGround(av);

            rec.Clear();
            Step(scene, 33);   // 3 s standing still
            AssertOnGround(av);
            AssertSteadyContact(rec, 0u, 33);

            // The land contact is at the feet, on the terrain, its SurfaceNormal pointing down into the ground
            // (ScenePresence turns it over to make the collision plane).
            foreach (ContactPoint c in rec.LandContacts)
            {
                Assert.True(c.CharacterFeet, $"the floor contact is not at the feet: at {c.Position}, surface normal {c.SurfaceNormal}");
                Assert.InRange(c.Position.Z, Ground - 0.05f, Ground + 0.05f);
                Assert.True(c.SurfaceNormal.Z < -0.99f, $"surface normal {c.SurfaceNormal}");
            }
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Standing_on_a_prim_reads_as_an_object_and_reports_the_prim(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            Box(scene, new Vector3(128f, 128f, PlatformTop - 0.25f), new Vector3(4f, 4f, 0.5f), physical: false);
            var rec = new Recorder();
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, PlatformTop + StandHalf), false, rec);
            Step(scene, 22);
            AssertOnObject(av);

            rec.Clear();
            Step(scene, 33);
            AssertOnObject(av);
            AssertSteadyContact(rec, PrimId, 33);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(45f, false)]
    [InlineData(0f, true)]
    [InlineData(45f, true)]
    public void Standing_on_a_prim_resting_on_the_terrain_reads_as_an_object(float physicsRate, bool physical)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            // A 4 x 4 x 0.2 m slab lying on the terrain (a physical one is dropped from 1 cm and left to settle).
            Box(scene, new Vector3(128f, 128f, Ground + 0.1f + (physical ? 0.01f : 0f)), new Vector3(4f, 4f, 0.2f), physical);
            Step(scene, 22);
            var rec = new Recorder();
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground + 0.2f + StandHalf + 0.01f), false, rec);
            Step(scene, 22);
            AssertOnObject(av);
            Assert.InRange(av.Position.Z, Ground + 0.2f + StandHalf - 0.05f, Ground + 0.2f + StandHalf + 0.05f);

            rec.Clear();
            Step(scene, 33);
            AssertOnObject(av);
            AssertSteadyContact(rec, PrimId, 33);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void In_the_air_reads_as_touching_nothing_and_reports_nothing(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            var rec = new Recorder();
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground + 10f), true, rec);
            Step(scene, 33);
            Assert.False(av.IsColliding);
            Assert.False(av.CollidingGround);
            Assert.False(av.CollidingObj);
            Assert.All(rec.Updates, u => Assert.Empty(u));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ events across a change of ground

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Walking_off_a_slab_onto_the_terrain_ends_the_object_and_starts_land(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            Box(scene, new Vector3(128f, 128f, Ground + 0.1f), new Vector3(4f, 4f, 0.2f), physical: false);
            var rec = new Recorder();
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground + 0.2f + StandHalf + 0.01f), false, rec);
            Step(scene, 22);
            AssertOnObject(av);

            av.TargetVelocity = new Vector3(2f, 0f, 0f);   // east, off the slab's edge at x 130
            Step(scene, 22);
            av.TargetVelocity = Vector3.Zero;
            Step(scene, 22);
            AssertOnGround(av);

            List<string> events = Events(rec.Updates);
            Assert.Equal($"start {PrimId}", events[0]);
            int end = events.IndexOf($"end {PrimId}");
            int land = events.IndexOf("land_start");
            Assert.True(end > 0, string.Join(", ", events));
            Assert.True(land > 0, string.Join(", ", events));
            Assert.Equal(1, events.Count(e => e == "land_start"));
            Assert.DoesNotContain("land_end", events);
            Assert.Equal("land", events[^1]);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void Jumping_ends_land_and_landing_starts_it_again(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            var rec = new Recorder();
            JoltCharacter av = Arrive(scene, new Vector3(128f, 128f, Ground + StandHalf), false, rec);
            Step(scene, 22);
            av.AvatarJump(1f);
            float top = av.Position.Z;
            for (int i = 0; i < 33; i++)
            {
                scene.Simulate(Heartbeat);
                top = MathF.Max(top, av.Position.Z);
            }
            AssertOnGround(av);
            Assert.True(top > Ground + StandHalf + 0.5f, $"did not jump: top {top}");

            // Arrival starts land; the take-off ends it (an empty update); the landing starts it again.
            List<string> events = Events(rec.Updates);
            Assert.True(1 == events.Count(e => e == "land_end"), string.Join(", ", events));
            Assert.Equal(2, events.Count(e => e == "land_start"));
            Assert.Equal("land_start", events[0]);
            int end = events.IndexOf("land_end");
            Assert.Equal("land_start", events[end + 1]);
            Assert.Equal("land", events[^1]);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_avatar_not_subscribed_gets_no_updates()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            PhysicsActor pa = scene.AddAvatar(AvatarId, "Test User", new Vector3(128f, 128f, Ground + StandHalf), Run.AvatarSize, 0f, false);
            int updates = 0;
            pa.OnCollisionUpdate += _ => updates++;
            Step(scene, 22);
            Assert.Equal(0, updates);
            Assert.True(pa.CollidingGround);
        }
        finally { scene.Dispose(); }
    }
}
