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
/// What the module owes a platform that carries standing avatars across a region border. Core hands the platform to the
/// neighbouring region with the avatars that stand on it; there each avatar is made again (AddAvatar) over the platform's
/// new pose, while the platform is held at rest and then moves again by keyframed motion (a stream of position sets on a
/// non-physical body). A failed crossing takes the avatar out of physics, puts the platform back and makes the avatar again
/// at its old place on it. The tests make exactly those calls, in that order, under both step modes (one step per heartbeat
/// and a fixed 45 Hz rate), and read the contact reports core's standing-on-an-object detection depends on.
/// Serial with the other native tests: every scene steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class RidersCrossArrivalTests
{
    private readonly ITestOutputHelper _out;
    public RidersCrossArrivalTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }
    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private const float Heartbeat = 1f / 11f;
    private const int Size = 256;
    private const float Ground = 25f;
    private const float PlatformTop = Ground + 3f;
    private const float Thickness = 0.5f;
    private const float Tolerance = 0.05f;              // the figure the stand-in measured for a carried avatar
    private const uint AvatarId = 99u;
    private const uint PlatformId = 500u;

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
        return scene;
    }

    // A 6 x 6 x 0.5 m non-physical platform whose top is at PlatformTop, centred on (x, y).
    private static JoltPrim Platform(JoltScene scene, float x, float y = 128f)
        => Assert.IsType<JoltPrim>(scene.AddPrimShape("platform", PrimitiveBaseShape.CreateBox(),
            new Vector3(x, y, PlatformTop - Thickness * 0.5f), new Vector3(6f, 6f, Thickness), Quaternion.Identity, false, PlatformId));

    // ScenePresence.AddToPhysicalScene: AddAvatar, then SubscribeEvents(100).
    private static JoltCharacter Arrive(JoltScene scene, Vector3 at, Recorder rec = null)
    {
        PhysicsActor pa = scene.AddAvatar(AvatarId, "Test User", at, Run.AvatarSize, 0f, false);
        rec?.Listen(pa);
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
        public int Beat;
        public readonly List<(int Beat, bool PlatformFeet)> Updates = new();
        public void Listen(PhysicsActor pa)
        {
            pa.OnCollisionUpdate += e =>
            {
                var u = (CollisionEventUpdate)e;
                bool feet = u.m_objCollisionList.TryGetValue(PlatformId, out ContactPoint c) && c.CharacterFeet;
                Updates.Add((Beat, feet));
            };
        }
    }

    // ------------------------------------------------------------------ 1: made over a held platform, which then moves

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_made_standing_on_a_held_platform_is_carried_when_the_platforms_keyframed_motion_starts(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, 100f);
            Vector3 start = plat.Position;
            JoltCharacter av = Arrive(scene, new Vector3(100f, 128f, PlatformTop + StandHalf + 0.01f));
            Step(scene, 22);
            Vector3 offset0 = av.Position - plat.Position;
            double maxDrift = 0;
            plat.MovedByKeyframedMotion = true;
            for (int h = 1; h <= 66; h++)                    // 6 s at 1 m/s
            {
                plat.Position = start + new Vector3(h * Heartbeat, 0f, 0f);
                scene.Simulate(Heartbeat);
                maxDrift = Math.Max(maxDrift, (av.Position - plat.Position - offset0).Length());
            }
            Say($"RIDE held then moving, rate {physicsRate:0}: most the avatar was off its place on the platform {F(maxDrift)} m");
            Assert.True(av.IsSupported, $"not supported at {av.Position}");
            Assert.True(maxDrift <= Tolerance, $"the avatar was {maxDrift:0.000} m off its place on the platform");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 2: made over a platform that is already moving

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_made_over_a_platform_that_is_already_moving_does_not_drop(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, 100f);
            Vector3 start = plat.Position;
            plat.MovedByKeyframedMotion = true;
            JoltCharacter av = null;
            Vector3 offset0 = default;
            double maxSink = 0, maxDrift = 0;
            float seat = PlatformTop + StandHalf;
            for (int h = 1; h <= 77; h++)
            {
                plat.Position = start + new Vector3(h * Heartbeat, 0f, 0f);
                if (h == 11)
                {
                    // Where the avatar goes is the platform's pose at that moment, as the arrival code asks for it.
                    av = Arrive(scene, new Vector3(plat.Position.X, 128f, seat + 0.01f));
                    offset0 = av.Position - plat.Position;
                }
                scene.Simulate(Heartbeat);
                if (av != null)
                {
                    maxSink = Math.Max(maxSink, seat - av.Position.Z);
                    if (h >= 22)
                        maxDrift = Math.Max(maxDrift, (av.Position - plat.Position - offset0).Length());
                }
            }
            Say($"RIDE arrival on a moving platform, rate {physicsRate:0}: lowest the avatar went under its seat {F(maxSink)} m, most off its place after the first second {F(maxDrift)} m");
            Assert.True(av.IsSupported, $"not supported at {av.Position}");
            Assert.True(av.GroundBody.IsValid && av.GroundBody.Value == plat.BodyHandle.Value, "not standing on the platform");
            Assert.True(maxSink <= Tolerance, $"the avatar dropped {maxSink:0.000} m under its seat");
            Assert.True(maxDrift <= Tolerance, $"the avatar was {maxDrift:0.000} m off its place on the platform");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3: outside the heightfield

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_placed_west_of_the_region_over_a_platform_that_reaches_inside_stays_on_the_platform(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, -1f);            // spans x from -4 to 2
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = Arrive(scene, new Vector3(-2f, 128f, seat + 0.05f));
            Step(scene, 55);
            Say($"RIDE west of the region, rate {physicsRate:0}: avatar at {av.Position}, supported {av.IsSupported}");
            Assert.True(av.IsSupported, $"not supported at {av.Position}");
            Assert.True(av.GroundBody.IsValid && av.GroundBody.Value == plat.BodyHandle.Value, $"not standing on the platform at {av.Position}");
            Assert.InRange(av.Position.Z, seat - Tolerance, seat + Tolerance);
        }
        finally { scene.Dispose(); }
    }

    // The clamp holds the avatar inside the region wherever its platform goes, so an avatar carried out with a platform is
    // moved off it. This is what the carried-rider arrival has to exempt (the clamp keeps the avatar out of the places the
    // platform has taken it to); the test records the module as it is.
    [Fact]
    public void An_avatar_carried_with_a_platform_past_the_west_edge_is_held_inside_by_the_clamp_and_leaves_the_platform()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            JoltPrim plat = Platform(scene, 4f);
            Vector3 start = plat.Position;
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = Arrive(scene, new Vector3(4f, 128f, seat + 0.01f));
            Step(scene, 22);
            plat.MovedByKeyframedMotion = true;
            for (int h = 1; h <= 121; h++)                   // 11 m west at 1 m/s: the platform ends centred on x = -7, clear of the region
            {
                plat.Position = start - new Vector3(h * Heartbeat, 0f, 0f);
                scene.Simulate(Heartbeat);
            }
            Say($"RIDE carried past the west edge: avatar at {av.Position}, platform at {plat.Position}, on the platform {av.GroundBody.IsValid && av.GroundBody.Value == plat.BodyHandle.Value}");
            Assert.InRange(av.Position.X, 0.009f, Size);
            Assert.True(av.Position.Z < seat - 0.5f, $"the avatar is still up on the platform at {av.Position}");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3b: a carried avatar is exempt from the clamp

    // Core sets AllowOutsideRegion on the actor of an avatar that an object carried in, right after AddAvatar.
    private static JoltCharacter ArriveCarried(JoltScene scene, Vector3 at)
    {
        PhysicsActor pa = scene.AddAvatar(AvatarId, "Test User", at, Run.AvatarSize, 0f, false);
        pa.AllowOutsideRegion = true;
        pa.SubscribeEvents(100);
        return Assert.IsType<JoltCharacter>(pa);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_carried_avatar_made_west_of_the_region_over_a_platform_stays_where_it_was_put(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, -1f);            // spans x from -4 to 2
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = ArriveCarried(scene, new Vector3(-2f, 128f, seat + 0.05f));
            Step(scene, 55);
            Say($"RIDE carried, west of the region, rate {physicsRate:0}: avatar at {av.Position}");
            Assert.InRange(av.Position.X, -2.05f, -1.95f);
            Assert.True(av.IsSupported, $"not supported at {av.Position}");
            Assert.True(av.GroundBody.IsValid && av.GroundBody.Value == plat.BodyHandle.Value, $"not standing on the platform at {av.Position}");
            Assert.InRange(av.Position.Z, seat - Tolerance, seat + Tolerance);
        }
        finally { scene.Dispose(); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void A_carried_avatar_stays_on_its_platform_when_the_platform_is_wholly_outside_the_region(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, 4f);
            Vector3 start = plat.Position;
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = ArriveCarried(scene, new Vector3(4f, 128f, seat + 0.01f));
            Step(scene, 22);
            Vector3 offset0 = av.Position - plat.Position;
            plat.MovedByKeyframedMotion = true;
            double maxDrift = 0;
            for (int h = 1; h <= 121; h++)                   // 11 m west: the platform ends centred on x = -7
            {
                plat.Position = start - new Vector3(h * Heartbeat, 0f, 0f);
                scene.Simulate(Heartbeat);
                maxDrift = Math.Max(maxDrift, (av.Position - plat.Position - offset0).Length());
            }
            Say($"RIDE carried out of the region, rate {physicsRate:0}: avatar at {av.Position}, platform at {plat.Position}, most off its place {F(maxDrift)} m");
            Assert.True(av.Position.X < -5f, $"the avatar is at {av.Position}");
            Assert.True(av.IsSupported);
            Assert.True(av.GroundBody.IsValid && av.GroundBody.Value == plat.BodyHandle.Value, "not standing on the platform");
            Assert.True(maxDrift <= Tolerance, $"the avatar was {maxDrift:0.000} m off its place on the platform");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3c: the flag is there from creation

    // Another thread stepping the region can run a whole step between the moment the character is made and the moment its
    // creator has it back. The hook stands in for that thread: it steps the scene from inside the creation.
    private static (JoltCharacter Avatar, bool FlagSeenAtCreation) ArriveWhileTheRegionSteps(JoltScene scene, Vector3 at, bool allowOutside)
    {
        bool seen = false;
        scene.AvatarCreatedHook = c =>
        {
            seen = c.AllowOutsideRegion;
            for (int i = 0; i < 3; i++)
                scene.Simulate(Heartbeat);
        };
        PhysicsActor pa = scene.AddAvatar(AvatarId, "Test User", at, Run.AvatarSize, 0f, false, allowOutside);
        scene.AvatarCreatedHook = null;
        pa.SubscribeEvents(100);
        return (Assert.IsType<JoltCharacter>(pa), seen);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_added_with_the_flag_has_it_from_creation_and_is_not_pulled_inside_by_a_step_in_between(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            Platform(scene, -1f);
            float seat = PlatformTop + StandHalf;
            var (av, seen) = ArriveWhileTheRegionSteps(scene, new Vector3(-2f, 128f, seat + 0.05f), allowOutside: true);
            Step(scene, 11);
            Say($"RIDE added with the flag, stepped during creation: avatar at {av.Position}, flag at creation {seen}");
            Assert.True(seen, "the flag was not set when the region could first step the character");
            Assert.True(av.AllowOutsideRegion);
            Assert.InRange(av.Position.X, -2.05f, -1.95f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void An_avatar_added_without_the_flag_is_still_pulled_inside()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            Platform(scene, -1f);
            float seat = PlatformTop + StandHalf;
            var (av, seen) = ArriveWhileTheRegionSteps(scene, new Vector3(-2f, 128f, seat + 0.05f), allowOutside: false);
            Step(scene, 3);
            Assert.False(seen);
            Assert.False(av.AllowOutsideRegion);
            Assert.InRange(av.Position.X, 0.009f, 0.5f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_carried_avatar_is_pulled_inside_by_the_clamp_once_it_is_no_longer_carried()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            Platform(scene, -1f);
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = ArriveCarried(scene, new Vector3(-2f, 128f, seat + 0.05f));
            Step(scene, 22);
            Assert.InRange(av.Position.X, -2.05f, -1.95f);

            av.AllowOutsideRegion = false;                   // core: the link ended
            Step(scene, 3);
            Say($"RIDE no longer carried: avatar at {av.Position}");
            Assert.InRange(av.Position.X, 0.009f, 0.5f);
            float held = av.Position.X;
            Step(scene, 11);
            Assert.InRange(av.Position.X, held - 0.05f, held + 0.05f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_exemption_does_not_change_an_avatar_inside_the_region()
    {
        JoltScene scene = NewScene(0f);
        try
        {
            Platform(scene, 100f);
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = ArriveCarried(scene, new Vector3(101f, 128f, seat + 0.01f));
            Step(scene, 33);
            Assert.InRange(av.Position.X, 100.95f, 101.05f);
            Assert.InRange(av.Position.Y, 127.95f, 128.05f);
            Assert.InRange(av.Position.Z, seat - Tolerance, seat + Tolerance);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 4: a failed crossing

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    public void An_avatar_removed_while_its_platform_is_put_back_and_made_again_at_its_old_place_is_carried_again(float physicsRate)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, 100f);
            Vector3 start = plat.Position;
            float seat = PlatformTop + StandHalf;
            JoltCharacter av = Arrive(scene, new Vector3(101.5f, 128f, seat + 0.01f));
            Step(scene, 22);
            Vector3 offset0 = av.Position - plat.Position;

            // The crossing starts: the avatar leaves physics, the platform is sent a metre and a half away and comes back.
            scene.RemoveAvatar(av);
            plat.MovedByKeyframedMotion = true;
            plat.Position = start + new Vector3(1.5f, 0f, 0f);
            Step(scene, 5);
            plat.Position = start;
            plat.CrossingFailure();
            Step(scene, 3);

            // The rollback makes the avatar again at its old place on the platform, and the motion runs again.
            av = Arrive(scene, plat.Position + offset0 + new Vector3(0f, 0f, 0.01f));
            Step(scene, 11);
            Vector3 offset1 = av.Position - plat.Position;
            double maxDrift = 0;
            for (int h = 1; h <= 44; h++)
            {
                plat.Position = start + new Vector3(h * Heartbeat, 0f, 0f);
                scene.Simulate(Heartbeat);
                maxDrift = Math.Max(maxDrift, (av.Position - plat.Position - offset1).Length());
            }
            Say($"RIDE failed crossing, rate {physicsRate:0}: place after the rollback {F((offset1 - offset0).Length())} m from the old one, most off its place while carried {F(maxDrift)} m");
            Assert.True(av.IsSupported, $"not supported at {av.Position}");
            Assert.True(av.GroundBody.IsValid && av.GroundBody.Value == plat.BodyHandle.Value, "not standing on the platform");
            Assert.True((offset1 - offset0).Length() <= Tolerance, $"the avatar settled {(offset1 - offset0).Length():0.000} m from its old place on the platform");
            Assert.True(maxDrift <= Tolerance, $"the avatar was {maxDrift:0.000} m off its place on the platform while carried");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ the reports core's detection reads

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0f, true)]
    [InlineData(45f, false)]
    [InlineData(45f, true)]
    public void A_standing_avatar_is_told_every_report_that_its_feet_are_on_the_platform_still_or_moving(float physicsRate, bool moving)
    {
        JoltScene scene = NewScene(physicsRate);
        try
        {
            JoltPrim plat = Platform(scene, 100f);
            Vector3 start = plat.Position;
            var rec = new Recorder();
            JoltCharacter av = Arrive(scene, new Vector3(100f, 128f, PlatformTop + StandHalf + 0.01f), rec);
            Step(scene, 22);
            rec.Updates.Clear();
            if (moving)
                plat.MovedByKeyframedMotion = true;
            const int beats = 66;                            // 6 s
            for (int h = 1; h <= beats; h++)
            {
                rec.Beat = h;
                if (moving)
                    plat.Position = start + new Vector3(h * Heartbeat, 0f, 0f);
                scene.Simulate(Heartbeat);
            }
            int lastFeet = 0, longestGap = 0, withFeet = 0;
            foreach ((int beat, bool feet) in rec.Updates)
            {
                if (!feet)
                    continue;
                withFeet++;
                longestGap = Math.Max(longestGap, beat - lastFeet);
                lastFeet = beat;
            }
            longestGap = Math.Max(longestGap, beats - lastFeet);
            Say($"RIDE reports, rate {physicsRate:0}, moving {moving}: {rec.Updates.Count} updates in {beats} heartbeats, {withFeet} with feet on the platform, longest gap {longestGap} heartbeats ({longestGap * Heartbeat * 1000:0} ms)");
            Assert.True(av.IsSupported);
            Assert.True(withFeet > 0, "no report named the platform with the feet");
            Assert.True(longestGap * Heartbeat * 1000 < 1000, $"a gap of {longestGap} heartbeats between reports that name the platform with the feet");
        }
        finally { scene.Dispose(); }
    }
}
