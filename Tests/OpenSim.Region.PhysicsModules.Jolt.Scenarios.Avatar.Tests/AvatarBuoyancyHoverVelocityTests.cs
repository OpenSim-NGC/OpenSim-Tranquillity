/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// An attachment's llSetBuoyancy and llSetHoverHeight acting on its wearer, and the velocity an avatar reports (the
/// harness's avatar-buoyancy, avatar-ledge, avatar-hover, avatar-wall and avatar-moving-platform scenarios), at an 11 Hz
/// heartbeat with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. Core hands both calls to the
/// wearer's actor (SceneObjectGroup.SetBuoyancy, SetHoverHeight). The Second Life wiki: llSetBuoyancy, below 1.0 the
/// object sinks and at 1.0 it floats (wiki.secondlife.com/wiki/LlSetBuoyancy); llSetHoverHeight, a critically damped
/// approach to the height over tau seconds, its example an attached prim that floats its wearer in the air without
/// flying (wiki.secondlife.com/wiki/LlSetHoverHeight). The reported velocity is what core sends viewers, scripts' llGetVel and sensors, and the animator.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarBuoyancyHoverVelocityTests
{
    private const float G = 9.80665f;                     // [Jolt] Gravity default
    private const float WalkSpeed = 3.2f;                 // 4.096 m/s asked x [Jolt] AvatarWalkSpeedFactor default

    // The avatar where the physics step had it before each heartbeat, against the physics time it had been stepped to:
    // with PhysicsStepRate 45 a heartbeat holds four or five steps, so heartbeat times alone misplace it by a step.
    private readonly record struct Point(double T, double PhysicsT, Vector3 Position, Vector3 Velocity, bool Colliding);

    private static (RunResult Result, List<Point> Trace) Run(string scenario, double physicsRate, float? slope = null)
    {
        Scenario sc = Harness.Harness.Find(scenario);
        var trace = new List<Point>();
        Action<Run> input = sc.Input;
        var traced = new Scenario
        {
            Name = sc.Name, Description = sc.Description, Slopes = sc.Slopes, UsesSlope = sc.UsesSlope,
            DefaultDuration = sc.DefaultDuration, DefaultHold = sc.DefaultHold, World = sc.World, Setup = sc.Setup,
            Input = r =>
            {
                input(r);
                trace.Add(new Point(r.Now, r.PhysicsTime, r.Actor.Position, r.Actor.Velocity, r.Actor.IsColliding));
            },
        };
        RunResult result = Harness.Harness.Run(traced, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, SlopeDeg = slope });
        return (result, trace);
    }

    private static float Level(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // The first point at which the avatar, walking off the ledge, no longer stands on it.
    private static int LeftAt(List<Point> trace)
        => trace.FindIndex(p => p.T > AvatarForceScenarios.WalkAt && !p.Colliding);

    // The vertical acceleration of the fall from the ledge: a parabola fitted through the points in the air (after the
    // first, which may have left part way through its step) above the ground it lands on.
    private static double FallAcceleration(RunResult r, List<Point> trace, out int points)
    {
        int left = LeftAt(trace);
        Assert.True(left > 0, $"{r.Name}: the avatar never left the ledge");
        float landed = trace[0].Position.Z - 3f;   // standing on the ground 3 m below the platform it started on
        List<Point> air = trace.Skip(left + 1).TakeWhile(p => !p.Colliding && p.Position.Z > landed + 0.05f).ToList();
        points = air.Count;
        Assert.True(points >= 4, $"{r.Name}: only {points} points in the air");
        double t0 = air[0].PhysicsT;
        double[] s = new double[5], b = new double[3];
        foreach (Point p in air)
        {
            double t = p.PhysicsT - t0, tk = 1;
            for (int k = 0; k < 5; k++, tk *= t) s[k] += tk;
            tk = 1;
            for (int k = 0; k < 3; k++, tk *= t) b[k] += p.Position.Z * tk;
        }
        double[,] a = { { s[0], s[1], s[2] }, { s[1], s[2], s[3] }, { s[2], s[3], s[4] } };
        double[] x = Solve3(a, b);
        return 2.0 * x[2];
    }

    private static double[] Solve3(double[,] a, double[] b)
    {
        double Det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double d = Det(a);
        var x = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var m = (double[,])a.Clone();
            for (int row = 0; row < 3; row++) m[row, c] = b[row];
            x[c] = Det(m) / d;
        }
        return x;
    }

    // ------------------------------------------------------------------ buoyancy

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Buoyancy_one_holds_an_avatar_that_steps_off_a_ledge_at_its_height_for_ten_seconds(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run("avatar-buoyancy-1", physicsRate);
        float standing = trace[0].Position.Z;   // on the platform, before it walks
        int left = LeftAt(trace);
        Assert.True(left > 0, $"{r.Name}: the avatar never left the ledge");
        double from = trace[left].T;
        Assert.True(r.Samples[^1].T >= from + 10.0 - 1e-6, $"{r.Name}: the run ends {r.Samples[^1].T - from:0.00} s after it left the ledge");
        foreach (Sample s in r.Samples.Where(s => s.T >= from))
            Assert.True(MathF.Abs(s.Position.Z - standing) < 0.05f,
                $"{r.Name}: at {s.T:0.00} s the avatar is {s.Position.Z - standing:+0.000;-0.000} m from its height, {s.Position.X - AvatarForceScenarios.LedgeX:0.0} m east of the ledge's middle");
        // It walked on across the air: it is well past the ledge, not stuck on it.
        Assert.True(r.Samples[^1].Position.X > AvatarForceScenarios.LedgeX + 20f, $"{r.Name}: the avatar ended at x {r.Samples[^1].Position.X:0.0}");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Buoyancy_half_lets_an_avatar_fall_at_half_gravity_and_none_as_today(double physicsRate)
    {
        (RunResult half, List<Point> halfTrace) = Run("avatar-buoyancy-half", physicsRate);
        (RunResult zero, List<Point> zeroTrace) = Run("avatar-buoyancy-0", physicsRate);
        (RunResult none, List<Point> noneTrace) = Run("avatar-ledge", physicsRate);

        double aHalf = FallAcceleration(half, halfTrace, out int nHalf);
        double aZero = FallAcceleration(zero, zeroTrace, out int nZero);
        Assert.InRange(-aZero, G * 0.99, G * 1.01);
        Assert.True(Math.Abs(-aHalf - G * 0.5) <= G * 0.5 * 0.05, $"{half.Name}: falls at {-aHalf:0.000} m/s2 ({nHalf} points), half gravity is {G * 0.5:0.000}");

        // Buoyancy 0 is no buoyancy: the avatar goes exactly where one with none set goes.
        Assert.Equal(none.Samples.Count, zero.Samples.Count);
        for (int i = 0; i < none.Samples.Count; i++)
            Assert.True(none.Samples[i].Position == zero.Samples[i].Position,
                $"at {none.Samples[i].T:0.00} s buoyancy 0 has the avatar at {zero.Samples[i].Position}, none set at {none.Samples[i].Position}");
        // Both land and walk on.
        Assert.True(half.Samples[^1].Touching && zero.Samples[^1].Touching, $"{half.Name} / {zero.Name}: not on the ground at the end");
    }

    // ------------------------------------------------------------------ hover

    [Theory]
    [InlineData(45.0, 0f)]
    [InlineData(0.0, 0f)]
    [InlineData(45.0, 15f)]
    [InlineData(0.0, 15f)]
    public void Hover_from_an_attachment_holds_the_avatar_at_its_height_while_it_walks_and_the_stop_lets_it_fall(double physicsRate, float slope)
    {
        (RunResult r, List<Point> trace) = Run("avatar-hover", physicsRate, slope);
        float standHalf = JoltCharacter.StandHalfFor(Harness.Run.SimulatorAvatarSize);   // the harness's avatar is the simulator's box
        float Height(Sample s) => s.Height;

        // Standing until the hover is set.
        Assert.True(r.Samples.Where(s => s.T <= AvatarForceScenarios.HoverAt).All(s => MathF.Abs(Height(s) - standHalf) < 0.01f), $"{r.Name}: the avatar was not standing");

        // Risen to it by the time it starts walking, and held within 5 cm all the way across, up the slope as well.
        foreach (Sample s in r.Samples.Where(s => s.T >= AvatarForceScenarios.HoverWalkAt && s.T <= AvatarForceScenarios.HoverStopAt))
            Assert.True(MathF.Abs(Height(s) - AvatarForceScenarios.HoverHeight) < 0.05f,
                $"{r.Name}: at {s.T:0.00} s the avatar is {Height(s):0.000} m above the ground at ({s.Position.X:0.0}, {s.Position.Y:0.0}), not {AvatarForceScenarios.HoverHeight}");

        // It walked at its walk speed while hovering, along the ground and onto the slope.
        Sample walkFrom = r.Samples.First(s => s.T >= AvatarForceScenarios.HoverWalkAt + 1.0);
        Sample walkTo = r.Samples.Last(s => s.T <= AvatarForceScenarios.HoverStopAt);
        float walked = Level(walkTo.Position - walkFrom.Position) / (float)(walkTo.T - walkFrom.T);
        Assert.True(MathF.Abs(walked - WalkSpeed) < WalkSpeed * 0.03f, $"{r.Name}: walked at {walked:0.000} m/s while hovering");
        if (slope > 0f)
            Assert.True(walkTo.Position.Y > Course.RampBottomY + 15f, $"{r.Name}: only reached y {walkTo.Position.Y:0.0}");

        // After the stop call it falls back to the ground and stands.
        Assert.True(r.Samples.Any(s => s.T > AvatarForceScenarios.HoverStopAt && s.Velocity.Z < -3f), $"{r.Name}: did not fall after the stop");
        Sample end = r.Samples[^1];
        Assert.True(end.Touching && MathF.Abs(end.Height - standHalf) < 0.05f, $"{r.Name}: ended {end.Height:0.000} m above the ground, touching {end.Touching}");
    }

    // ------------------------------------------------------------------ the velocity it reports

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void An_avatar_walking_into_a_wall_reports_almost_no_speed(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run("avatar-wall", physicsRate);
        // At the wall: its face less the capsule's radius.
        Sample atWall = r.Samples.First(s => s.Position.X > AvatarForceScenarios.WallFaceX - 0.3f);
        foreach (Sample s in r.Samples.Where(s => s.T >= atWall.T + 0.25))
        {
            Assert.True(s.Position.X < AvatarForceScenarios.WallFaceX, $"{r.Name}: through the wall at {s.T:0.00} s");
            Assert.True(s.Speed < 0.1f, $"{r.Name}: at {s.T:0.00} s, held at the wall, it reports {s.Velocity} ({s.Speed:0.000} m/s)");
        }
        Assert.True(r.Samples[^1].T - atWall.T > 2.0, $"{r.Name}: reached the wall only at {atWall.T:0.00} s");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void An_avatar_walking_freely_reports_its_walk_speed(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run("avatar-wall", physicsRate);
        // Walking, before it is near the wall.
        foreach (Point p in trace.Where(p => p.T >= AvatarForceScenarios.WalkAt + 0.3 && p.Position.X < AvatarForceScenarios.WallFaceX - 1.0f))
            Assert.True(MathF.Abs(Level(p.Velocity) - WalkSpeed) < WalkSpeed * 0.02f && MathF.Abs(p.Velocity.Z) < 0.01f,
                $"{r.Name}: at {p.T:0.00} s walking freely it reports {p.Velocity}");
        // And that is how fast it really goes: physics time between two points well apart.
        Point a = trace.First(p => p.T >= AvatarForceScenarios.WalkAt + 0.3), b = trace.Last(p => p.Position.X < AvatarForceScenarios.WallFaceX - 1.0f);
        float moved = Level(b.Position - a.Position) / (float)(b.PhysicsT - a.PhysicsT);
        Assert.True(MathF.Abs(moved - WalkSpeed) < WalkSpeed * 0.02f, $"{r.Name}: it moved at {moved:0.000} m/s");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void An_avatar_standing_on_a_platform_moving_at_2_m_s_reports_2_m_s(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run("avatar-moving-platform", physicsRate);
        HarnessPart platform = r.Parts.Single(p => p.Name == "platform");
        foreach (Sample s in r.Samples.Where(s => s.T >= 1.0))
        {
            Assert.True(MathF.Abs(Level(s.Velocity) - AvatarForceScenarios.PlatformSpeed) < AvatarForceScenarios.PlatformSpeed * 0.05f,
                $"{r.Name}: at {s.T:0.00} s on the platform it reports {s.Velocity}");
            Assert.True(s.Touching, $"{r.Name}: off the platform at {s.T:0.00} s");
        }
        // It rode the platform: it is still over it at the end.
        Vector3 end = r.Samples[^1].Position, under = platform.EndPosition;
        Assert.True(MathF.Abs(end.X - under.X) < 2f && MathF.Abs(end.Y - under.Y) < 2f, $"{r.Name}: the avatar at {end}, the platform at {under}");
        Assert.True(end.X > 108f, $"{r.Name}: carried only to x {end.X:0.0}");
    }
}
