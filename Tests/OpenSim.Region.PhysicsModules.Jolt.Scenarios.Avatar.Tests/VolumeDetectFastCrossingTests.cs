/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical object that crosses a volume-detect prim raises one collision_start and one collision_end on the detector,
/// however fast it goes: llVolumeDetect, "VolumeDetect objects trigger collision_start and collision_end events when
/// interpenetrating" (wiki.secondlife.com/wiki/LlVolumeDetect), which says nothing of a speed beyond which a crossing is
/// missed. Jolt's own step finds a sensor only where a body overlaps it at a step, so a ball that is wholly on one side of a
/// thin slab at one step and wholly on the other at the next was never found; the backend now sweeps each fast body's path
/// of the update against the sensors and raises the contact itself, unless the engine raised it. The scenario is the
/// harness's tunnel-vd-slab: a 0.2 m ball shot along x through a fixed volume-detect slab 0.1 m thick.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class VolumeDetectFastCrossingTests
{
    private readonly ITestOutputHelper _out;
    public VolumeDetectFastCrossingTests(ITestOutputHelper output) { _out = output; }

    private static RunResult Shoot(double speed, double physicsRate, float ball = 0.2f)
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, ShotSpeed = (float)speed, ShotBall = ball, Duration = 4f };   // long enough for the slowest to cross and for the end to show
        return Harness.Harness.Run(Harness.Harness.Find("tunnel-vd-slab"), o);
    }

    [Theory]
    [InlineData(5.0, 0.0)]
    [InlineData(5.0, 45.0)]
    [InlineData(25.0, 0.0)]
    [InlineData(25.0, 45.0)]
    [InlineData(50.0, 0.0)]
    [InlineData(50.0, 45.0)]
    [InlineData(200.0, 0.0)]
    [InlineData(200.0, 45.0)]
    public void A_ball_crossing_a_thin_detector_raises_one_start_and_one_end(double speed, double physicsRate)
    {
        RunResult r = Shoot(speed, physicsRate);
        CollisionWatch slab = r.Watches.Single(w => w.Name == "slab");
        string line = $"VD-CROSS speed={speed} physics_rate={physicsRate}: slab starts={slab.StartsOf(Run.ActorLocalId)} ends={slab.EndsOf(Run.ActorLocalId)} ball_end_x={r.Summary.End.X:0.0}";
        _out.WriteLine(line);
        Console.WriteLine(line);
        Assert.True(r.Summary.End.X > Harness.TunnelScenarios.TargetX, "the ball did not pass the slab");   // a sensor does not stop it
        Assert.Equal(1, slab.StartsOf(Run.ActorLocalId));
        Assert.Equal(1, slab.EndsOf(Run.ActorLocalId));
    }

    [Fact]
    public void A_fast_ball_through_a_detector_and_into_a_wall_beyond_it_raises_one_start_and_one_end()
    {
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 0.0, ShotSpeed = 200f };
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("tunnel-vd-wall"), o);
        CollisionWatch slab = r.Watches.Single(w => w.Name == "slab");
        // tunnel-vd-wall puts a wall 2 m beyond a 1 m slab: the ball crosses the slab and is stopped by the wall.
        Assert.Equal(1, slab.StartsOf(Run.ActorLocalId));
        Assert.Equal(1, slab.EndsOf(Run.ActorLocalId));
    }

    // A fixed wall 1 m thick stands in front of a detector: the ball is stopped by the wall, so it never crosses the detector,
    // although the line it was moving along would have.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_ball_stopped_by_a_wall_before_the_detector_raises_nothing_on_it(double physicsRate)
    {
        const float x = Harness.TunnelScenarios.TargetX, y = Harness.TunnelScenarios.ShotY;
        var scenario = new Scenario
        {
            Name = "vd-behind-wall",
            Description = "A ball shot at a wall with a volume-detect slab behind it.",
            DefaultDuration = _ => 2f,
            Setup = r =>
            {
                float ground = r.GroundAt(x, y);
                r.AddOtherBox(new OpenMetaverse.Vector3(1f, 6f, 6f), new OpenMetaverse.Vector3(x, y, ground + 3f), OpenMetaverse.Quaternion.Identity, false);
                PhantomScenarios.AddPart(r, "slab", new OpenMetaverse.Vector3(0.1f, 6f, 6f), new OpenMetaverse.Vector3(x + 3f, y, ground + 3f), false, false, true, true);
                r.AddSphere(0.2f, new OpenMetaverse.Vector3(x - 3f, y, ground + 3f));
                r.ReleaseAt = r.Dt;
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= r.Dt - 1e-9)
                {
                    r.Actor.Velocity = new OpenMetaverse.Vector3(200f, 0f, 0f);
                    r.Stage = 1;
                }
            },
        };
        RunResult r = Harness.Harness.Run(scenario, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate });
        CollisionWatch slab = r.Watches.Single(w => w.Name == "slab");
        _out.WriteLine($"VD-CROSS behind a wall physics_rate={physicsRate}: ball_end_x={r.Summary.End.X:0.0} slab starts={slab.StartsOf(Run.ActorLocalId)}");
        Assert.True(r.Summary.End.X < x + 3f, "the ball got past the wall");
        Assert.Equal(0, slab.StartsOf(Run.ActorLocalId));
    }
}
