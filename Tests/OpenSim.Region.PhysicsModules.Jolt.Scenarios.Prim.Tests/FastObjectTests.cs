/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Small fast physical objects do not pass through a thin fixed wall, a physical box or the ground, at any heartbeat rate
/// and up to the engine's speed cap ([Jolt] BodyMaxLinearSpeed, Jolt's 500 m/s), through the harness's shot scenarios
/// (Tests/JoltPhysicsHarness/TunnelScenarios.cs). A fast object that hits something bounces with the restitution and
/// slides with the friction its materials give, and a phantom or volume-detect prim still lets it through.
/// Rates: each heartbeat rate with one physics step per heartbeat, and the 11 Hz heartbeat with [Jolt] PhysicsStepRate 45.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class FastObjectTests
{
    private static readonly float[] Balls = { 0.05f, 0.2f, 1f };
    private static readonly float[] Speeds = { 10f, 25f, 50f, 100f, 200f, 500f };
    private const uint BallId = 1000;   // Run's ActorLocalId: the ball

    public static IEnumerable<object[]> Rates()
    {
        foreach (double r in Harness.Harness.Rates)
            yield return new object[] { r, 0.0 };
        yield return new object[] { 11.0, 45.0 };
    }

    public static IEnumerable<object[]> TargetsAndRates()
    {
        foreach (string t in new[] { "tunnel-wall-1cm", "tunnel-wall-10cm", "tunnel-box", "tunnel-ground" })
            foreach (object[] r in Rates())
                yield return new object[] { t, r[0], r[1] };
    }

    private static RunResult Shot(Scenario sc, double rate, double physicsRate, float ball, float speed, params (string Key, string Value)[] jolt)
    {
        var o = new HarnessOptions { RateHz = rate, PhysicsRateHz = physicsRate, ShotBall = ball, ShotSpeed = speed };
        foreach ((string key, string value) in jolt)
            o.Jolt[key] = value;
        return Harness.Harness.Run(sc, o);
    }

    private static RunResult Shot(string scenario, double rate, double physicsRate, float ball, float speed)
        => Shot(Harness.Harness.Find(scenario), rate, physicsRate, ball, speed);

    private static string Label(double rate, double physicsRate)
        => rate.ToString(CultureInfo.InvariantCulture) + (physicsRate > 0 ? "/p" + physicsRate.ToString(CultureInfo.InvariantCulture) : "");

    [Theory]
    [MemberData(nameof(TargetsAndRates))]
    public void A_fast_ball_does_not_pass_through_what_it_is_shot_at(string target, double rate, double physicsRate)
    {
        var through = new List<string>();
        foreach (float ball in Balls)
            foreach (float speed in Speeds)
            {
                RunResult r = Shot(target, rate, physicsRate, ball, speed);
                Assert.Equal(0, r.Summary.NonFinite);
                // It was shot: it covered the gap to what it was shot at.
                // A ball that struck it and was on its way back before the next sample (a prim bounces: wood on wood combines to 0.5)
                // has covered the gap as well: it is seen moving back toward where it started.
                Vector3 start = r.Samples[0].Position;
                float moved = r.Samples.Max(s => Vector3.Distance(s.Position, start));
                bool comingBack = r.Samples.Any(s => Vector3.Distance(s.Position, start) > TunnelScenarios.StartGap * 0.5f
                                                     && Vector3.Dot(s.Velocity, s.Position - start) < 0f);
                Assert.True(moved > TunnelScenarios.StartGap * 0.9f || comingBack, $"{r.Name}: moved {moved:0.00} m");
                if (r.Summary.Tunneled != 0)
                    through.Add(r.Name);
            }
        Assert.True(through.Count == 0, $"{target} at {Label(rate, physicsRate)}: passed through in {string.Join(", ", through)}");
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void A_ball_through_a_volume_detect_slab_raises_its_start_and_end_and_the_wall_behind_stops_it(double rate, double physicsRate)
    {
        RunResult r = Shot("tunnel-vd-wall", rate, physicsRate, 0.2f, 50f);
        CollisionWatch slab = r.Watches.Single(w => w.Name == "slab");
        Assert.True(slab.StartsOf(BallId) >= 1, $"{r.Name}: {slab}");
        Assert.Equal(slab.StartsOf(BallId), slab.EndsOf(BallId));
        Assert.Equal(0, r.Summary.Tunneled);
        // Through the slab: the ball reached the wall, 2 m beyond the slab's far face. A prim bounces (restitution 0.5), so at
        // a slow heartbeat the ball can strike the wall and be on its way back before the next sample: it also counts as
        // having reached the wall if it is seen coming back. The slab is a volume detector, which cannot turn the ball, so the
        // wall is the only thing it can have bounced off (the start and end counts above show it went through the slab).
        Assert.Contains(r.Samples, s => s.Position.X > TunnelScenarios.TargetX - 0.05f - 0.2f || s.Velocity.X < 0f);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void A_fast_ball_knocks_a_resting_box_along_and_does_not_pass_through_it(double rate, double physicsRate)
    {
        RunResult r = Shot("tunnel-box", rate, physicsRate, 0.2f, 50f);
        Assert.Equal(0, r.Summary.Tunneled);
        Sample last = r.Samples[^1];
        // The 0.5 m box (125 kg) takes the 0.2 m ball's (4.2 kg) momentum: about 1.7 m/s, so it slides some way.
        Assert.True(last.Other.X - TunnelScenarios.TargetX > 0.05f, $"{r.Name}: the box ended at x {last.Other.X:0.000}");
        Assert.True(r.Samples.Max(s => s.OtherSpeed) > 0.5f, $"{r.Name}: the box's top speed {r.Samples.Max(s => s.OtherSpeed):0.000}");
        Assert.True(last.Position.X < last.Other.X, $"{r.Name}: the ball ended at x {last.Position.X:0.000}, the box at {last.Other.X:0.000}");
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void A_phantom_box_and_a_phantom_ball_still_let_a_fast_ball_through(double rate, double physicsRate)
    {
        foreach (string scenario in new[] { "tunnel-phantom-box", "tunnel-phantom-ball" })
            foreach (float speed in new[] { 10f, 200f })
            {
                RunResult r = Shot(scenario, rate, physicsRate, 0.2f, speed);
                Assert.True(r.Summary.Tunneled == 1, $"{r.Name}: did not pass");
            }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void A_fast_phantom_ball_shot_at_the_ground_rests_on_it(double rate, double physicsRate)
    {
        // A physical phantom object collides with the ground (llVolumeDetect's comparison table, SL wiki).
        Scenario ground = Harness.Harness.Find("tunnel-ground");
        var sc = new Scenario
        {
            Name = "tunnel-ground-phantom", UsesShot = true, DefaultDuration = ground.DefaultDuration,
            Setup = r => { ground.Setup(r); r.Actor.Phantom = true; },
            Input = ground.Input, PassedThrough = ground.PassedThrough,
        };
        RunResult res = Shot(sc, rate, physicsRate, 0.2f, 200f);
        Assert.Equal(0, res.Summary.Tunneled);
    }

    // The ball's speed back off the wall: the most it moved away from the wall (along -x) after it was shot.
    private static float Rebound(RunResult r) => -r.Samples.Min(s => s.Velocity.X);

    private static Scenario WithMaterials(string scenario, float ballFriction, float ballRestitution, float otherFriction, float otherRestitution)
    {
        Scenario b = Harness.Harness.Find(scenario);
        return new Scenario
        {
            Name = scenario + "-material", UsesShot = true, DefaultDuration = b.DefaultDuration,
            Setup = r =>
            {
                b.Setup(r);
                r.Actor.Friction = ballFriction;
                r.Actor.Restitution = ballRestitution;
                if (r.Other != null)
                {
                    r.Other.Friction = otherFriction;
                    r.Other.Restitution = otherRestitution;
                }
            },
            Input = b.Input, PassedThrough = b.PassedThrough,
        };
    }

    // The contact's restitution is the two prims' average (the shipped rule): the ball's 0.5 against a wall of 0.5 gives 0.5.
    [Theory]
    [MemberData(nameof(Rates))]
    public void A_fast_ball_bounces_off_a_wall_with_its_restitution_as_a_slow_one_does(double rate, double physicsRate)
    {
        Scenario bouncy = WithMaterials("tunnel-wall-10cm", 0f, 0.5f, 0f, 0.5f);
        // 3 m/s: never cast at any of these rates (a 0.2 m ball is cast from 5 m/s at 11 Hz).
        RunResult slow = Shot(bouncy, rate, physicsRate, 0.2f, 3f);
        RunResult fast = Shot(bouncy, rate, physicsRate, 0.2f, 100f);
        Assert.Equal(0, slow.Summary.Tunneled);
        Assert.Equal(0, fast.Summary.Tunneled);
        float slowShare = Rebound(slow) / 3f, fastShare = Rebound(fast) / 100f;
        Assert.InRange(slowShare, 0.4f, 0.6f);
        Assert.InRange(fastShare, 0.4f, 0.6f);

        Scenario dead = WithMaterials("tunnel-wall-10cm", 0f, 0f, 0f, 0f);
        RunResult fastDead = Shot(dead, rate, physicsRate, 0.2f, 100f);
        Assert.Equal(0, fastDead.Summary.Tunneled);
        Assert.True(Rebound(fastDead) < 0.05f * 100f, $"{fastDead.Name}: came back at {Rebound(fastDead):0.00} m/s with restitution 0");
    }

    // Shot down at the ground at 45 degrees: with no friction the ball keeps its speed along the ground; with friction the
    // contact takes some of it, as it does from a slow ball.
    [Theory]
    [MemberData(nameof(Rates))]
    public void A_fast_ball_hitting_the_ground_slides_with_the_friction_its_materials_give(double rate, double physicsRate)
    {
        float Along(float friction, float speed)
        {
            Scenario ground = Harness.Harness.Find("tunnel-ground");
            var sc = new Scenario
            {
                Name = "tunnel-ground-slant", UsesShot = true, DefaultDuration = _ => 1.5f,
                Setup = r =>
                {
                    ground.Setup(r);
                    r.Actor.Friction = friction;
                    r.Actor.Restitution = 0f;
                },
                Input = r =>
                {
                    if (r.Stage == 0 && r.Now >= r.Dt - 1e-9)
                    {
                        r.Actor.Velocity = new Vector3(1f, 0f, -1f) * (r.Options.ShotSpeed / MathF.Sqrt(2f));
                        r.Stage = 1;
                    }
                },
                PassedThrough = ground.PassedThrough,
            };
            // The land's own friction is 0 here, so a ball of friction 0 meets none (the shipped land friction of 0.4 averages with the
            // ball's: 0.2 for a ball of 0, 0.5 for a ball of 1).
            RunResult res = Shot(sc, rate, physicsRate, 0.2f, speed, ("TerrainFriction", "0"));
            Assert.Equal(0, res.Summary.Tunneled);
            // The fastest it moved along x after the first contact with the ground (the ball's lowest point).
            int hit = res.Samples.FindIndex(s => s.Height < 0.1f + 0.02f);
            Assert.True(hit >= 0, $"{res.Name}: never reached the ground");
            return res.Samples.Skip(hit + 1).Take(3).Max(s => s.Velocity.X) / (speed / MathF.Sqrt(2f));
        }

        Assert.True(Along(0f, 100f) > 0.95f, "fast, no friction");
        Assert.True(Along(0f, 3f) > 0.95f, "slow, no friction");
        float fastRough = Along(1f, 100f), slowRough = Along(1f, 3f);
        Assert.True(fastRough < 0.9f, $"fast, friction 1: kept {fastRough:0.000} of its speed along the ground");
        Assert.True(slowRough < 0.9f, $"slow, friction 1: kept {slowRough:0.000} of its speed along the ground");
    }
}
