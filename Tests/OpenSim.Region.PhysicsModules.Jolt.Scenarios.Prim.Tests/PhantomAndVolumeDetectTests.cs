/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Phantom and volume-detect prims behave as Second Life documents (wiki.secondlife.com):
/// - llVolumeDetect: physical objects and avatars pass through the object, and it raises collision_start and
///   collision_end while they interpenetrate it; the scene passes only those two on (SceneObjectPart.PhysicsCollision).
/// - STATUS_PHANTOM: objects and avatars pass through it; a physical phantom object still collides with the ground
///   without passing through, and queues land collision events (the comparison table on
///   wiki.secondlife.com/wiki/LlVolumeDetect).
/// Each scenario runs in the harness at one physics step per heartbeat (0) and at 45 Hz, with an 11 Hz heartbeat.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PhantomAndVolumeDetectTests
{
    private const uint AvatarId = 1000;     // Run's ActorLocalId: the avatar or the dropped box
    private const uint OtherId = 1001;      // Run's OtherLocalId: the box dropped on the toggled one
    private const float Ground = 25f;       // Course.Ground; the scenarios here are on level ground
    private const float WallFront = PhantomScenarios.WallX - 0.5f;

    private static RunResult Run(string scenario, double physicsRate)
        => Harness.Harness.Run(Harness.Harness.Find(scenario), new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate });

    private static HarnessPart Part(RunResult r, string name) => r.Parts.Single(p => p.Name == name);
    private static CollisionWatch Watch(RunResult r, string name) => r.Watches.Single(w => w.Name == name);

    // The same walk with nothing in its way: the harness's avatar-walk on level ground starts at the same place and asks
    // for the same 4.096 m/s east.
    private static RunResult OpenWalk(double physicsRate)
        => Harness.Harness.Run(Harness.Harness.Find("avatar-walk"),
                               new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, SlopeDeg = 0f, Duration = 3f });

    // The avatar's path through the walk scenario matches the open walk's, heartbeat by heartbeat: nothing slowed it.
    private static void WalkedThroughUnslowed(RunResult r, double physicsRate)
    {
        RunResult open = OpenWalk(physicsRate);
        Assert.Equal(open.Samples.Count, r.Samples.Count);
        for (int i = 0; i < r.Samples.Count; i++)
        {
            Assert.True(MathF.Abs(open.Samples[i].Position.X - r.Samples[i].Position.X) < 1e-3f,
                $"{r.Name} t {r.Samples[i].T:0.000}: x {r.Samples[i].Position.X:0.0000}, open walk {open.Samples[i].Position.X:0.0000}");
            Assert.True(MathF.Abs(open.Samples[i].Position.Z - r.Samples[i].Position.Z) < 1e-3f,
                $"{r.Name} t {r.Samples[i].T:0.000}: z {r.Samples[i].Position.Z:0.0000}, open walk {open.Samples[i].Position.Z:0.0000}");
        }
        Assert.True(r.Samples[^1].Position.X > PhantomScenarios.WallX + 3f, $"{r.Name}: ended at x {r.Samples[^1].Position.X:0.00}");
    }

    // The avatar stopped at the wall's front face.
    private static void Blocked(RunResult r)
    {
        float maxX = r.Samples.Max(s => s.Position.X);
        Assert.True(maxX < WallFront, $"{r.Name}: reached x {maxX:0.000}, the wall's face is at {WallFront}");
        Assert.True(maxX > WallFront - 0.6f, $"{r.Name}: stopped at x {maxX:0.000}, short of the wall");
    }

    // ------------------------------------------------------------------ volume detect

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_avatar_walks_through_a_volume_detect_prim_and_it_raises_one_start_and_one_end(double physicsRate)
    {
        RunResult r = Run("vd-walk", physicsRate);
        WalkedThroughUnslowed(r, physicsRate);

        CollisionWatch wall = Watch(r, "wall1");
        Assert.Equal(1, wall.StartsOf(AvatarId));
        Assert.Equal(1, wall.EndsOf(AvatarId));
        Assert.Equal(1, wall.ObjectStarts);                       // nothing else
        Assert.Equal(0, wall.LandStarts);                         // a fixed detector never touches the land
        Assert.DoesNotContain(Part(r, "wall1").LocalId, Watch(r, "avatar").Touched);   // the avatar is not told
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_physical_box_falls_through_a_volume_detect_prim_onto_the_ground(double physicsRate)
    {
        RunResult r = Run("vd-drop", physicsRate);
        HarnessPart slab = Part(r, "slab"), box = Part(r, "box");
        Assert.InRange(box.EndPosition.Z, Ground + 0.2f, Ground + 0.3f);   // resting on the ground (half height 0.25)

        CollisionWatch detector = Watch(r, "slab");
        Assert.Equal(1, detector.StartsOf(box.LocalId));
        Assert.Equal(1, detector.EndsOf(box.LocalId));
        Assert.Equal(1, detector.ObjectStarts);
        CollisionWatch boxEvents = Watch(r, "box");
        Assert.DoesNotContain(slab.LocalId, boxEvents.Touched);
        // An ordinary box lands, bounces and lands again: wood on land combines to (0.5 + 0.05) / 2 = 0.275 under the shipped
        // averaging, so it leaves the 6 m fall's 10.8 m/s at 2.97 m/s and rises e^2 x 6 m = 0.45 m (measured 0.42 m), which ends
        // its land contact. Its next rebound (0.73 m/s, 0.03 m) is too low to end the contact again: one start more, not two.
        Assert.Equal(2, boxEvents.LandStarts);
    }

    // ------------------------------------------------------------------ phantom

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_avatar_walks_through_a_non_physical_phantom_prim_with_no_events(double physicsRate)
    {
        RunResult r = Run("phantom-walk", physicsRate);
        WalkedThroughUnslowed(r, physicsRate);
        HarnessPart wall = Part(r, "wall1");
        Assert.Null(wall.Actor);                                  // the scene keeps it out of physics
        Assert.Empty(wall.Watch.Touched);
        Assert.DoesNotContain(wall.LocalId, Watch(r, "avatar").Touched);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_physical_box_falls_through_a_non_physical_phantom_prim_with_no_events(double physicsRate)
    {
        RunResult r = Run("phantom-drop", physicsRate);
        HarnessPart slab = Part(r, "slab"), box = Part(r, "box");
        Assert.InRange(box.EndPosition.Z, Ground + 0.2f, Ground + 0.3f);
        Assert.Empty(slab.Watch.Touched);
        Assert.DoesNotContain(slab.LocalId, box.Watch.Touched);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_physical_phantom_box_falls_through_a_solid_prim_and_rests_on_the_ground(double physicsRate)
    {
        RunResult r = Run("phantom-physical-drop", physicsRate);
        HarnessPart slab = Part(r, "slab"), box = Part(r, "box");
        Assert.InRange(box.EndPosition.Z, Ground + 0.2f, Ground + 0.3f);
        // The same bounce chain as the volume-detect drop above (a 6 m fall onto land at 0.275 combined): two land starts.
        Assert.Equal(2, box.Watch.LandStarts);                    // "land collision events are queued"
        Assert.Equal(0, box.Watch.ObjectStarts);
        Assert.DoesNotContain(box.LocalId, slab.Watch.Touched);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_avatar_walks_through_a_physical_phantom_box_that_stays_on_the_ground(double physicsRate)
    {
        RunResult r = Run("phantom-physical-walk", physicsRate);
        WalkedThroughUnslowed(r, physicsRate);
        HarnessPart box = Part(r, "box");
        Assert.True(Vector3.Distance(box.EndPosition, new Vector3(PhantomScenarios.WallX, PhantomScenarios.WalkY, Ground + 0.5f)) < 0.02f,
            $"box ended at {box.EndPosition}");
        Assert.Equal(0, box.Watch.ObjectStarts);
        Assert.DoesNotContain(box.LocalId, Watch(r, "avatar").Touched);
    }

    // ------------------------------------------------------------------ switching on a live prim and a linkset

    // A change made on a live actor keeps its body and leaves it where it was.
    private static void ChangedInPlace(HarnessPart p)
    {
        Assert.True(p.BodyBeforeChange.IsValid, $"{p.Name}: no body before the change");
        Assert.Equal(p.BodyBeforeChange, p.BodyAfterChange);
        Assert.Equal(p.PositionBeforeChange, p.PositionAfterChange);
    }

    [Theory]
    [InlineData("vd-on-walk", 0.0)]
    [InlineData("vd-on-walk", 45.0)]
    [InlineData("vd-on-walk-linkset", 0.0)]
    [InlineData("vd-on-walk-linkset", 45.0)]
    public void Volume_detect_switched_on_lets_the_avatar_through_with_one_start_and_one_end_per_part(string scenario, double physicsRate)
    {
        RunResult r = Run(scenario, physicsRate);
        WalkedThroughUnslowed(r, physicsRate);
        foreach (HarnessPart p in r.Parts)
        {
            ChangedInPlace(p);
            Assert.Equal(p.Position, p.EndPosition);
            Assert.Equal(1, p.Watch.StartsOf(AvatarId));
            Assert.Equal(1, p.Watch.EndsOf(AvatarId));
            Assert.DoesNotContain(p.LocalId, Watch(r, "avatar").Touched);
        }
    }

    [Theory]
    [InlineData("vd-off-walk", 0.0)]
    [InlineData("vd-off-walk", 45.0)]
    [InlineData("vd-off-walk-linkset", 0.0)]
    [InlineData("vd-off-walk-linkset", 45.0)]
    public void Volume_detect_switched_off_blocks_the_avatar_again(string scenario, double physicsRate)
    {
        RunResult r = Run(scenario, physicsRate);
        Blocked(r);
        foreach (HarnessPart p in r.Parts)
        {
            ChangedInPlace(p);
            Assert.Equal(p.Position, p.EndPosition);
            // Solid again: the first wall is touched as any solid prim is, and nothing reaches the ones behind it.
            if (p.Name == "wall1")
                Assert.True(p.Watch.StartsOf(AvatarId) >= 1);
            else
                Assert.Empty(p.Watch.Touched);
        }
    }

    // A non-physical prim that turns phantom leaves physics, and comes back when it turns solid: the scene does that
    // (SceneObjectPart.UpdatePrimFlags), at the same place.
    [Theory]
    [InlineData("phantom-on-walk", 0.0)]
    [InlineData("phantom-on-walk", 45.0)]
    [InlineData("phantom-on-walk-linkset", 0.0)]
    [InlineData("phantom-on-walk-linkset", 45.0)]
    public void Phantom_switched_on_a_fixed_prim_lets_the_avatar_through_with_no_events(string scenario, double physicsRate)
    {
        RunResult r = Run(scenario, physicsRate);
        WalkedThroughUnslowed(r, physicsRate);
        foreach (HarnessPart p in r.Parts)
        {
            Assert.Null(p.Actor);
            Assert.Empty(p.Watch.Touched);
            Assert.DoesNotContain(p.LocalId, Watch(r, "avatar").Touched);
        }
    }

    [Theory]
    [InlineData("phantom-off-walk", 0.0)]
    [InlineData("phantom-off-walk", 45.0)]
    [InlineData("phantom-off-walk-linkset", 0.0)]
    [InlineData("phantom-off-walk-linkset", 45.0)]
    public void Phantom_switched_off_a_fixed_prim_blocks_the_avatar(string scenario, double physicsRate)
    {
        RunResult r = Run(scenario, physicsRate);
        Blocked(r);
        foreach (HarnessPart p in r.Parts)
        {
            Assert.NotNull(p.Actor);
            Assert.Equal(p.Position, p.EndPosition);
        }
    }

    [Theory]
    [InlineData("phantom-physical-toggle", 0.0)]
    [InlineData("phantom-physical-toggle", 45.0)]
    [InlineData("phantom-physical-toggle-linkset", 0.0)]
    [InlineData("phantom-physical-toggle-linkset", 45.0)]
    public void A_physical_object_switched_phantom_falls_to_the_ground_and_is_solid_again_when_switched_back(string scenario, double physicsRate)
    {
        RunResult r = Run(scenario, physicsRate);
        HarnessPart root = Part(r, "root");
        ChangedInPlace(root);                                      // the last change (solid again) kept the body

        // Phantom from 1 s: fell through the platform onto the ground, and rests there.
        Sample beforeOff = r.Samples.Last(s => s.T <= PhantomScenarios.PhantomOffAt);
        Assert.InRange(beforeOff.Position.Z, Ground + 0.45f, Ground + 0.55f);
        Assert.InRange(root.EndPosition.Z, Ground + 0.45f, Ground + 0.55f);
        // Land events queued: a linkset's terrain contact names the part that touched (SceneObjectPart passes it on to
        // the root's script), so count every part.
        Assert.True(r.Parts.Where(p => p.Watch != null).Sum(p => p.Watch.LandStarts) >= 1);

        // Solid from 3 s: the box dropped on it at 3.5 s rests on its top, 1 m up.
        Sample last = r.Samples[^1];
        Assert.InRange(last.Other.Z, Ground + 1.2f, Ground + 1.3f);
        Assert.True(root.Watch.StartsOf(OtherId) >= 1);
    }

    // ------------------------------------------------------------------ a scene of its own

    private const float Heartbeat = 1f / 11f;

    private static JoltScene NewScene()
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        config.AddConfig("Jolt").Set("PhysicsStepRate", "0");
        var scene = new JoltScene();
        scene.Initialise(config);
        var heights = new float[256 * 256];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Test Region", 256, 256, heights, 20f, Heartbeat);
        return scene;
    }

    // A 2 x 2 x 0.5 m prim 3 m up at the region's centre, added as SceneObjectPart.AddToPhysics adds it.
    private static PhysicsActor Target(JoltScene scene, bool physical, bool volumeDetect)
    {
        PhysicsActor pa = scene.AddPrimShape("target", PrimitiveBaseShape.CreateBox(), new Vector3(128f, 128f, Ground + 3f),
                                             new Vector3(2f, 2f, 0.5f), Quaternion.Identity, physical, true, (byte)PhysShapeType.prim, 77);
        pa.Density = 1000f;
        if (volumeDetect)
            pa.SetVolumeDetect(1);
        return pa;
    }

    // A physical volume detect object falls through the ground, at the risk of leaving the world (wiki.secondlife.com/wiki/LlVolumeDetect)
    [Fact]
    public void A_physical_volume_detect_prim_falls_through_the_ground()
    {
        JoltScene scene = NewScene();
        try
        {
            PhysicsActor pa = Target(scene, true, true);
            for (int i = 0; i < 33; i++)                          // 3 s
                scene.Simulate(Heartbeat);
            Assert.True(pa.Position.Z < Ground - 5f, $"at z {pa.Position.Z:0.00}");
        }
        finally
        {
            scene.Dispose();
        }
    }

    // ------------------------------------------------------------------ ray casts

    // llCastRay finds phantom and volume-detect prims only with RC_DETECT_PHANTOM (RayFilterFlags.phantom or volumedtc
    // here); a cast that does not ask for them goes through to what is behind.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_ray_finds_phantom_and_volume_detect_prims_only_when_it_asks_for_them(bool physical, bool volumeDetect)
    {
        JoltScene scene = NewScene();
        try
        {
            Target(scene, physical, volumeDetect);
            const RayFilterFlags solid = RayFilterFlags.land | RayFilterFlags.agent | RayFilterFlags.physical | RayFilterFlags.nonphysical;
            var from = new Vector3(128f, 128f, Ground + 10f);
            var plain = (List<ContactResult>)scene.RaycastWorld(from, -Vector3.UnitZ, 20f, 4, solid);
            Assert.DoesNotContain(plain, h => h.ConsumerID == 77);
            Assert.Contains(plain, h => h.ConsumerID == 0);       // the ground behind it
            var asked = (List<ContactResult>)scene.RaycastWorld(from, -Vector3.UnitZ, 20f, 4,
                                                                solid | (volumeDetect ? RayFilterFlags.volumedtc : RayFilterFlags.phantom));
            Assert.Contains(asked, h => h.ConsumerID == 77);

            // The 4-argument entry (Phlox's llCastRay) has no way to ask for them.
            List<ContactResult> unfiltered = scene.RaycastWorld(from, -Vector3.UnitZ, 20f, 4);
            Assert.DoesNotContain(unfiltered, h => h.ConsumerID == 77);
            Assert.Contains(unfiltered, h => h.ConsumerID == 0);
        }
        finally
        {
            scene.Dispose();
        }
    }
}
