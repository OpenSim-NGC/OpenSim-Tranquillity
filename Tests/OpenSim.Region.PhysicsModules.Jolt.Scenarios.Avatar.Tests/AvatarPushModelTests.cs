/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The push model for avatars, at the step rate a region runs (11 Hz heartbeat, 45 Hz physics, the simulator's own prim and
/// avatar values, every [Jolt] push setting at its default):
///  1. an avatar's mass comes from its shape and size, and is 1.954922 for the default avatar (the figure Second Life's
///     llGetObjectMass page gives for an avatar before it went by shape), which llGetObjectMass and llGetMass report;
///  2. a push on an avatar (llPushObject) changes its velocity by impulse / mass, up to the ceiling ([Jolt]
///     AvatarPushMaxSpeed), so a stronger push throws it further until it reaches that ceiling;
///  3. an avatar walking or running into a physical object pushes it with a force in proportion to its mass and speed
///     (held to [Jolt] AvatarObjectPushMaxForce), so running pushes harder than walking.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarPushModelTests
{
    private const float X = AvatarHitScenarios.X, Y = AvatarHitScenarios.Y;
    private const uint CubeId = 1700;
    private readonly ITestOutputHelper _out;

    public AvatarPushModelTests(ITestOutputHelper output) => _out = output;

    private static RunResult Run(Scenario sc, double physicsHz = 45.0)
        => Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz, SimulatorDefaults = true });

    private static float Level(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // ------------------------------------------------------------------ 1. mass from shape and size

    [Fact]
    public void The_default_avatar_weighs_the_second_life_figure()
    {
        Assert.Equal(1.954922f, JoltCharacter.MassFor(Harness.Run.SimulatorAvatarSize), 4);
        Assert.Equal(1.954922f, JoltCharacter.DefaultAvatarMass, 6);
    }

    [Fact]
    public void A_taller_or_wider_avatar_weighs_more_and_a_shorter_or_thinner_one_less()
    {
        Vector3 d = Harness.Run.SimulatorAvatarSize;
        float m = JoltCharacter.MassFor(d);
        Assert.True(JoltCharacter.MassFor(new Vector3(d.X, d.Y, d.Z + 0.4f)) > m * 1.1f);
        Assert.True(JoltCharacter.MassFor(new Vector3(d.X, d.Y, d.Z - 0.4f)) < m * 0.9f);
        Assert.True(JoltCharacter.MassFor(new Vector3(d.X + 0.2f, d.Y + 0.2f, d.Z)) > m * 1.2f);
        Assert.True(JoltCharacter.MassFor(new Vector3(d.X - 0.1f, d.Y - 0.2f, d.Z)) < m * 0.9f);
    }

    [Fact]
    public void The_avatars_actor_reports_that_mass_and_follows_a_resize()
    {
        float atStart = 0f, bigger = 0f;
        Vector3 big = Harness.Run.SimulatorAvatarSize + new Vector3(0.1f, 0.1f, 0.5f);
        var sc = new Scenario
        {
            Name = "avatar-mass",
            DefaultDuration = _ => 3f,
            Setup = r => r.AddAvatar(X, Y),
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= 1.0)
                {
                    atStart = r.Actor.Mass;
                    r.Actor.Size = big;
                    bigger = r.Actor.Mass;
                    r.Stage = 1;
                }
            },
        };
        Run(sc);
        _out.WriteLine($"mass at start {atStart:0.0000}, after a resize to {big}: {bigger:0.0000}");
        Assert.Equal(1.954922f, atStart, 4);
        Assert.Equal(JoltCharacter.MassFor(big), bigger, 4);
        Assert.True(bigger > atStart);
    }

    // ------------------------------------------------------------------ 2. a push is impulse / mass, up to a ceiling

    // A flying avatar (no ground to fade the push) hit by an impulse east at 2 s; the speed it has in the first heartbeat
    // after, and its top speed.
    private (float First, float Top) FlyingPush(float impulse)
    {
        float first = 0f, top = 0f;
        double pushedAt = double.NaN;
        var sc = new Scenario
        {
            Name = "push-flying",
            DefaultDuration = _ => 4f,
            Setup = r => r.AddAvatarAt(new Vector3(60f, 128f, r.GroundAt(60f, 128f) + 30f), true),
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= 2.0)
                {
                    r.Actor.AddForce(new Vector3(impulse, 0f, 0f), true);
                    pushedAt = r.Now;
                    r.Stage = 1;
                }
                else if (r.Stage == 1 && r.Now > pushedAt)
                {
                    first = r.Actor.Velocity.X;
                    r.Stage = 2;
                }
                if (r.Stage >= 1)
                    top = MathF.Max(top, r.Actor.Velocity.X);
            },
        };
        Run(sc);
        return (first, top);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(20f)]
    [InlineData(100f)]
    public void A_push_changes_a_flying_avatars_velocity_by_impulse_over_mass(float impulse)
    {
        (float first, float top) = FlyingPush(impulse);
        float expected = impulse / 1.954922f;
        _out.WriteLine($"impulse {impulse}: expected {expected:0.00} m/s, the first heartbeat after {first:0.00}, top {top:0.00}");
        // The flying push fades with a second's time constant, so the heartbeat after it reads a little under the figure.
        Assert.InRange(top, expected * 0.9f, expected * 1.03f);
        Assert.InRange(first, expected * 0.8f, expected * 1.03f);
    }

    [Theory]
    [InlineData(1000f)]
    [InlineData(10000f)]
    public void A_push_beyond_the_ceiling_is_held_at_it(float impulse)
    {
        (_, float top) = FlyingPush(impulse);
        // The flying push fades within the heartbeat that reads it (93.55 read for a ceiling of 100).
        _out.WriteLine($"impulse {impulse}: top {top:0.00} m/s, ceiling {PhysicsBackendSettings.DefaultAvatarPushMaxSpeed}");
        Assert.InRange(top, PhysicsBackendSettings.DefaultAvatarPushMaxSpeed * 0.9f, PhysicsBackendSettings.DefaultAvatarPushMaxSpeed * 1.02f);
    }

    // A standing avatar on level ground, pushed east at 1 s; how far it went by the end.
    private float StandingPushDistance(float impulse, params (string key, string value)[] keys)
    {
        Vector3 start = default, end = default;
        var sc = new Scenario
        {
            Name = "push-standing",
            DefaultDuration = _ => 6f,
            Setup = r => r.AddAvatar(60f, 128f),
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= 1.0)
                {
                    start = r.Actor.Position;
                    r.Actor.AddForce(new Vector3(impulse, 0f, 0f), true);
                    r.Stage = 1;
                }
                if (r.Stage == 1)
                    end = r.Actor.Position;
            },
        };
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0, SimulatorDefaults = true };
        foreach ((string key, string value) in keys)
            o.Jolt[key] = value;
        Harness.Harness.Run(sc, o);
        return Level(end - start);
    }

    [Fact]
    public void A_stronger_push_throws_a_standing_avatar_further_until_it_reaches_the_ceiling()
    {
        float d10 = StandingPushDistance(10f), d100 = StandingPushDistance(100f), d1000 = StandingPushDistance(1000f), d10000 = StandingPushDistance(10000f);
        _out.WriteLine($"standing avatar pushed east: 10 -> {d10:0.000} m, 100 -> {d100:0.000} m, 1000 -> {d1000:0.000} m, 10000 -> {d10000:0.000} m");
        Assert.True(d100 > d10 * 5f, $"100 went {d100:0.000} m, 10 went {d10:0.000} m");
        Assert.True(d1000 > d100 * 1.5f, $"1000 went {d1000:0.000} m, 100 went {d100:0.000} m");
        // 10000 / 1.954922 is far over the ceiling, as 1000 is: both go as far as the ceiling takes them.
        Assert.True(d10000 >= d1000 * 0.98f, $"10000 went {d10000:0.000} m, 1000 went {d1000:0.000} m");
    }

    [Fact]
    public void The_ceiling_is_a_setting_and_a_lower_one_holds_a_strong_push_to_it()
    {
        float high = StandingPushDistance(1000f);
        float low = StandingPushDistance(1000f, ("AvatarPushMaxSpeed", "10"));
        float zero = StandingPushDistance(1000f, ("AvatarPushMaxSpeed", "0"));
        _out.WriteLine($"1000 pushed: ceiling 100 -> {high:0.000} m, ceiling 10 -> {low:0.000} m, ceiling 0 -> {zero:0.000} m");
        Assert.True(low < high * 0.3f);
        Assert.True(zero < 0.01f, $"{zero:0.000} m with pushes off");
    }

    // ------------------------------------------------------------------ 3. walking and running into a physical object

    // An avatar 8 m west of a physical cube of `side` metres (density 1000: 1.25 for 0.5 m, 80 for 2 m) walks or runs
    // east into it from 1 s. The cube's top level speed, and its speed one second after the avatar reached it.
    private (float Top, float AfterContact, float Avatar) IntoCube(float side, bool run)
    {
        PhysicsActor cube = null;
        float top = 0f, avatarTop = 0f, after = 0f;
        double touchedAt = double.NaN;
        var sc = new Scenario
        {
            Name = "avatar-into-cube",
            DefaultDuration = _ => 9f,
            Setup = r =>
            {
                cube = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(side, side, side),
                    new Vector3(X, Y, r.GroundAt(X, Y) + side * 0.5f + 0.01f), Quaternion.Identity, true, CubeId);
                r.AddAvatar(X - 8f, Y);
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= 1.0)
                {
                    r.Actor.SetAlwaysRun = run;
                    r.Actor.TargetVelocity = new Vector3(AvatarHitScenarios.WalkRequest, 0f, 0f);
                    r.Stage = 1;
                }
                float v = Level(cube.Velocity);
                if (double.IsNaN(touchedAt) && v > 0.05f)
                    touchedAt = r.Now;
                if (!double.IsNaN(touchedAt) && r.Now >= touchedAt + 1.0 && after == 0f)
                    after = v;
                top = MathF.Max(top, v);
                if (r.Stage == 1)
                    avatarTop = MathF.Max(avatarTop, Level(r.Actor.Velocity));
            },
        };
        Run(sc);
        return (top, after, avatarTop);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    public void An_avatar_running_into_a_physical_cube_pushes_it_harder_than_one_walking(float side)
    {
        (float walkTop, float walkAfter, float walkAvatar) = IntoCube(side, run: false);
        (float runTop, float runAfter, float runAvatar) = IntoCube(side, run: true);
        _out.WriteLine($"{side} m cube: walking (avatar {walkAvatar:0.00} m/s) cube top {walkTop:0.00} m/s, one second on {walkAfter:0.00}; "
            + $"running (avatar {runAvatar:0.00} m/s) cube top {runTop:0.00} m/s, one second on {runAfter:0.00}");
        Assert.True(walkTop > 0.5f, $"walking moved the {side} m cube at {walkTop:0.00} m/s");
        Assert.True(runAvatar > walkAvatar * 1.4f, $"the avatar ran at {runAvatar:0.00}, walked at {walkAvatar:0.00}");
        Assert.True(runTop > walkTop * 1.25f, $"the {side} m cube went {runTop:0.00} m/s when run into, {walkTop:0.00} when walked into");
        Assert.True(runAfter > walkAfter * 1.25f, $"one second after contact the cube went {runAfter:0.00} m/s when run into, {walkAfter:0.00} when walked into");
    }

    [Fact]
    public void An_avatars_push_on_an_object_is_held_to_the_maximum_force_setting()
    {
        // With the force held to nothing the 2 m cube (80, friction about 470 N) is not moved at all by a walk.
        PhysicsActor cube = null;
        float top = 0f;
        var sc = new Scenario
        {
            Name = "avatar-into-cube-no-force",
            DefaultDuration = _ => 8f,
            Setup = r =>
            {
                cube = r.AddPart(PrimitiveBaseShape.CreateBox(), new Vector3(2f, 2f, 2f),
                    new Vector3(X, Y, r.GroundAt(X, Y) + 1.01f), Quaternion.Identity, true, CubeId);
                r.AddAvatar(X - 8f, Y);
            },
            Input = r =>
            {
                if (r.Stage == 0 && r.Now >= 1.0)
                {
                    r.Actor.TargetVelocity = new Vector3(AvatarHitScenarios.WalkRequest, 0f, 0f);
                    r.Stage = 1;
                }
                top = MathF.Max(top, Level(cube.Velocity));
            },
        };
        var o = new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0, SimulatorDefaults = true };
        o.Jolt["AvatarObjectPushMaxForce"] = "0";
        Harness.Harness.Run(sc, o);
        _out.WriteLine($"2 m cube with the maximum force 0: top speed {top:0.000} m/s");
        Assert.True(top < 0.05f, $"{top:0.000} m/s");
    }
}
