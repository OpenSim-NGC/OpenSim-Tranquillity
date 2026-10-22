/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A flying avatar that crosses a thin volume-detect prim inside one update raises one collision_start and one collision_end on
/// it, however fast it goes (llVolumeDetect: "VolumeDetect objects trigger collision_start and collision_end events when
/// interpenetrating", wiki.secondlife.com/wiki/LlVolumeDetect; the page names no speed beyond which a crossing is missed).
/// A physical body that does this is found by the backend's sweep of fast bodies against the sensors
/// (VolumeDetectFastCrossingTests); an avatar is not a body in that sweep, so these tests ask whether the avatar's own update
/// finds the detector. The detector is a fixed slab 0.1 m thick across the avatar's path; the avatar flies east at the speed
/// the harness's avatar-fly scenario asks for (16.384 m/s) times a speed modifier (osSetSpeed), a step per heartbeat or 45 Hz.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VolumeDetectFastAvatarTests
{
    private readonly ITestOutputHelper _out;
    public VolumeDetectFastAvatarTests(ITestOutputHelper output) { _out = output; }

    private const uint AvatarId = Run.ActorLocalId;
    private const float SlabX = 150f, FlyY = 128f, Height = 10f;
    private const float Fly = 16.384f;   // Harness AvatarFly: the speed a flying avatar asks for

    private static RunResult FlyThrough(float modifier, double physicsRate, float offsetY = 0f)
    {
        var scenario = new Scenario
        {
            Name = "vd-fast-avatar",
            Description = "A flying avatar through a thin volume-detect slab.",
            DefaultDuration = _ => 1.2f,
            Setup = r =>
            {
                float ground = r.GroundAt(SlabX, FlyY);
                PhantomScenarios.AddPart(r, "slab", new Vector3(0.1f, 6f, 6f), new Vector3(SlabX, FlyY, ground + Height), false, false, true, true);
                r.AddAvatarAt(new Vector3(SlabX - 12f, FlyY + offsetY, ground + Height), true);
                r.Actor.TargetVelocity = new Vector3(Fly * modifier, 0f, 0f);
            },
        };
        return Harness.Harness.Run(scenario, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, AvatarSpeedModifier = modifier });
    }

    [Theory]
    [InlineData(1f, 0.0)]
    [InlineData(1f, 45.0)]
    [InlineData(3f, 0.0)]
    [InlineData(3f, 45.0)]
    [InlineData(6f, 0.0)]
    [InlineData(6f, 45.0)]
    public void A_flying_avatar_crossing_a_thin_detector_raises_one_start_and_one_end(float modifier, double physicsRate)
    {
        RunResult r = FlyThrough(modifier, physicsRate);
        CollisionWatch slab = r.Watches.Single(w => w.Name == "slab");
        float top = r.Samples.Max(s => s.HorizontalSpeed);
        string line = $"VD-AVATAR modifier={modifier} physics_rate={physicsRate}: top speed {top:0.0} m/s, slab starts={slab.StartsOf(AvatarId)} ends={slab.EndsOf(AvatarId)} avatar_end_x={r.Summary.End.X:0.0}";
        _out.WriteLine(line);
        Console.WriteLine(line);
        Assert.True(r.Summary.End.X > SlabX + 1f, "the avatar did not pass the slab");   // a sensor does not stop it
        Assert.Equal(1, slab.StartsOf(AvatarId));
        Assert.Equal(1, slab.EndsOf(AvatarId));
    }

    // The slab is 6 m wide; the avatar passes 5 m to one side of its middle, clear of it by 2 m.
    [Theory]
    [InlineData(3f, 0.0)]
    [InlineData(6f, 0.0)]
    [InlineData(6f, 45.0)]
    public void A_flying_avatar_that_passes_beside_a_detector_raises_nothing(float modifier, double physicsRate)
    {
        RunResult r = FlyThrough(modifier, physicsRate, offsetY: 5f);
        CollisionWatch slab = r.Watches.Single(w => w.Name == "slab");
        _out.WriteLine($"VD-AVATAR beside modifier={modifier} physics_rate={physicsRate}: slab starts={slab.StartsOf(AvatarId)} avatar_end_x={r.Summary.End.X:0.0}");
        Assert.True(r.Summary.End.X > SlabX + 1f, "the avatar did not pass the slab");
        Assert.Equal(0, slab.StartsOf(AvatarId));
        Assert.Equal(0, slab.EndsOf(AvatarId));
    }
}
