/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A constant force on an avatar: what an attachment's llSetForce sets on its wearer ("Used on an attachment, it will
/// apply the force to the avatar", SL wiki llSetForce). Core hands it to the wearer's actor as
/// PhysicsActor.SetConstantForce, in newtons, in region axes or the avatar's own (ScenePresence.SetConstantForce), and
/// hands it again to every new actor the avatar gets. The avatar accelerates by force / mass on every step, on top of
/// whatever else moves it, until the next call; zero ends it.
/// Every case runs at an 11 Hz heartbeat with one physics step per heartbeat and with [Jolt] PhysicsStepRate 45.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class AvatarConstantForceTests
{
    private const float G = 9.80665f;                     // [Jolt] Gravity default
    private const float X = 60f, Y = 60f;                 // over level ground (Course.Ground)
    private const float WalkRequest = AvatarForceScenarios.WalkRequest;
    private const float PushCap = 10f;   // [Jolt] AvatarPushMaxSpeed, set to 10 in these runs: the cap rule is what they check (the default is 100)
    private const float BodySpeedCap = PhysicsBackendSettings.JoltMaxLinearSpeed;
    private const float GroundFade = 0.25f;               // the push's fade on walkable ground (s)
    private const float FlyingFade = 1f;                  // the push's fade while flying (s)

    private readonly ITestOutputHelper _out;

    public AvatarConstantForceTests(ITestOutputHelper output) => _out = output;

    // The avatar where the physics step had it before each heartbeat, against the physics time it had been stepped to.
    private readonly record struct Point(double T, double PhysicsT, Vector3 Position, Vector3 Velocity, bool Colliding);

    // An avatar put this far above level ground, flying or not, then `setup` (the force, buoyancy, hover...), then `input`
    // before each heartbeat.
    private static Scenario At(string name, float height, bool flying, float duration, Action<Run> setup, Action<Run> input = null) => new()
    {
        Name = name,
        Description = name,
        DefaultDuration = _ => duration,
        Setup = r =>
        {
            r.AddAvatarAt(new Vector3(X, Y, r.GroundAt(X, Y) + height + r.AvatarStandHalf + (height == 0f ? 0.01f : 0f)), flying);
            setup(r);
        },
        Input = input ?? (_ => { }),
    };

    private static (RunResult Result, List<Point> Trace) Run(Scenario sc, double physicsRate, List<CharacterStepTrace> steps = null)
    {
        var trace = new List<Point>();
        Action<Run> input = sc.Input;
        Action<Run> setup = sc.Setup;
        var traced = new Scenario
        {
            Name = sc.Name, Description = sc.Description, Slopes = sc.Slopes, UsesSlope = sc.UsesSlope,
            DefaultDuration = sc.DefaultDuration, DefaultHold = sc.DefaultHold, World = sc.World,
            Setup = r =>
            {
                if (steps != null)
                    ((JoltPhysicsBackend)((JoltScene)r.PhysicsScene).Backend).CharacterStepped += t => steps.Add(t);
                setup(r);
            },
            Input = r =>
            {
                trace.Add(new Point(r.Now, r.PhysicsTime, r.Actor.Position, r.Actor.Velocity, r.Actor.IsColliding));
                input(r);
            },
        };
        RunResult result = Harness.Harness.Run(traced, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsRate, Jolt = { ["AvatarPushMaxSpeed"] = "10" } });
        return (result, trace);
    }

    private static Vector3 Force(Run r, float ax, float ay, float az) => new Vector3(ax, ay, az) * r.Actor.Mass;

    // The acceleration along one axis over the points whose physics time is in [from, to): a parabola fitted through them.
    private static double Accel(List<Point> trace, Func<Vector3, float> axis, double from, double to)
    {
        List<Point> pts = trace.Where(p => p.PhysicsT >= from - 1e-9 && p.PhysicsT < to - 1e-9).ToList();
        Assert.True(pts.Count >= 4, $"only {pts.Count} points in [{from}, {to})");
        double t0 = pts[0].PhysicsT;
        double[] s = new double[5], b = new double[3];
        foreach (Point p in pts)
        {
            double t = p.PhysicsT - t0, tk = 1;
            for (int k = 0; k < 5; k++, tk *= t) s[k] += tk;
            tk = 1;
            for (int k = 0; k < 3; k++, tk *= t) b[k] += axis(p.Position) * tk;
        }
        double[,] a = { { s[0], s[1], s[2] }, { s[1], s[2], s[3] }, { s[2], s[3], s[4] } };
        return 2.0 * Solve3(a, b)[2];
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

    // The velocity it moved at over the last `seconds` of the run, from where the physics step had it.
    private static Vector3 MovedOverLast(List<Point> trace, double seconds)
    {
        Point end = trace[^1];
        Point start = trace.Last(p => p.PhysicsT <= end.PhysicsT - seconds + 1e-9);
        return (end.Position - start.Position) / (float)(end.PhysicsT - start.PhysicsT);
    }

    private static void Within(double actual, double expected, double fraction, string what)
        => Assert.True(Math.Abs(actual - expected) <= Math.Abs(expected) * fraction,
            $"{what}: {actual:0.0000}, expected {expected:0.0000} within {fraction:P0}");

    // ------------------------------------------------------------------ in the air

    // An upward force equal to the avatar's weight holds it where it was put: its height changes by no more than 1 cm in
    // 10 s.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_upward_force_equal_to_the_weight_holds_an_avatar_in_the_air(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run(Harness.Harness.Find("avatar-force-weight"), physicsRate);
        float z0 = trace[0].Position.Z;
        Assert.True(trace[^1].PhysicsT >= 10.0, $"{r.Name}: the run ends at {trace[^1].PhysicsT:0.00} s");
        float most = trace.Where(p => p.PhysicsT <= 10.0 + 1e-9).Max(p => MathF.Abs(p.Position.Z - z0));
        _out.WriteLine($"{r.Name} p{physicsRate}: weight up, height moved at most {most * 1000f:0.000} mm in 10 s");
        Assert.True(most <= 0.01f, $"{r.Name}: the avatar moved {most:0.0000} m up or down");
        Assert.True(r.Samples.All(s => !s.Touching), $"{r.Name}: the avatar touched something");
    }

    // Twice the weight lifts it at g.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void An_upward_force_of_twice_the_weight_lifts_an_avatar_at_g(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run(At("force-twice-weight", 20f, false, 2f, r => r.Actor.SetConstantForce(Force(r, 0, 0, 2 * G), false)), physicsRate);
        double az = Accel(trace, p => p.Z, 0.1, 2.0);
        _out.WriteLine($"{r.Name} p{physicsRate}: twice the weight up, rises at {az:0.0000} m/s2 (g {G})");
        Within(az, G, 0.02, $"{r.Name} vertical acceleration");
    }

    // A level force on an avatar in the air accelerates it at force / mass, and it still falls at g; the velocity it
    // reports is the one it moves at.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_level_force_accelerates_an_avatar_in_the_air_at_force_over_mass(double physicsRate)
    {
        var steps = new List<CharacterStepTrace>();
        (RunResult r, List<Point> trace) = Run(Harness.Harness.Find("avatar-force-across"), physicsRate, steps);
        double ax = Accel(trace, p => p.X, 0.1, 4.0);
        double az = Accel(trace, p => p.Z, 0.1, 4.0);
        _out.WriteLine($"{r.Name} p{physicsRate}: east {ax:0.0000} m/s2 (asked {AvatarForceScenarios.ForceAcross}), falls {az:0.0000} m/s2");
        Within(ax, AvatarForceScenarios.ForceAcross, 0.02, $"{r.Name} level acceleration");
        Within(az, -G, 0.02, $"{r.Name} vertical acceleration");

        // What it reports in each update is the speed it moved at across (the general reported-speed test bounds it from
        // above; this bounds it from below too).
        foreach (CharacterStepTrace t in steps.Skip(1))
        {
            float moved = t.Displacement.X / t.Seconds;
            Assert.True(MathF.Abs(t.Reported.X - moved) <= 0.01f * MathF.Abs(moved) + 1e-3f,
                $"{r.Name}: an update moved {moved:0.000} m/s east and reported {t.Reported.X:0.000}");
        }
        float end = (float)trace[^1].PhysicsT;
        Within(r.Samples[^1].Velocity.X, AvatarForceScenarios.ForceAcross * end, 0.03, $"{r.Name} east velocity reported at the end");
    }

    // ------------------------------------------------------------------ local and region axes

    // A local force is in the avatar's own axes, turned by its orientation on every step: turned 90 degrees left, its
    // forward force pushes it north, and turned back, east. A region force pushes it east whichever way it faces.
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(45.0, true)]
    [InlineData(0.0, false)]
    [InlineData(45.0, false)]
    public void A_local_force_turns_with_the_avatar_and_a_region_force_does_not(double physicsRate, bool local)
    {
        const double Turn = 1.5, End = 3.0;
        Quaternion left = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        (RunResult r, List<Point> trace) = Run(At(local ? "force-local" : "force-region", 100f, false, (float)End, r =>
        {
            r.Actor.Orientation = left;
            r.Actor.SetConstantForce(Force(r, 2f, 0f, 0f), local);
        }, r =>
        {
            if (r.Stage == 0 && r.PhysicsTime >= Turn - 1e-9)
            {
                r.Actor.Orientation = Quaternion.Identity;
                r.Stage = 1;
            }
        }), physicsRate);

        double ax1 = Accel(trace, p => p.X, 0.1, Turn), ay1 = Accel(trace, p => p.Y, 0.1, Turn);
        double ax2 = Accel(trace, p => p.X, Turn + 0.1, End), ay2 = Accel(trace, p => p.Y, Turn + 0.1, End);
        _out.WriteLine($"{r.Name} p{physicsRate}: facing north ({ax1:0.0000}, {ay1:0.0000}) m/s2, facing east ({ax2:0.0000}, {ay2:0.0000})");
        if (local)
        {
            Within(ay1, 2.0, 0.02, $"{r.Name} north acceleration facing north");
            Assert.True(Math.Abs(ax1) < 0.04, $"{r.Name}: east acceleration facing north {ax1:0.0000}");
        }
        else
        {
            Within(ax1, 2.0, 0.02, $"{r.Name} east acceleration facing north");
            Assert.True(Math.Abs(ay1) < 0.04, $"{r.Name}: north acceleration facing north {ay1:0.0000}");
        }
        Within(ax2, 2.0, 0.02, $"{r.Name} east acceleration facing east");
        Assert.True(Math.Abs(ay2) < 0.04, $"{r.Name}: north acceleration facing east {ay2:0.0000}");
    }

    // ------------------------------------------------------------------ replace and end

    // Each call replaces the force: east for 1 s, then north for 1 s. Zero ends it: from then on the avatar's velocity
    // changes in every heartbeat exactly as that of an avatar that never had one (it keeps the speed it was given, and
    // falls). A force set and ended before any step leaves the avatar exactly as one that never had one.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_new_force_replaces_the_old_and_zero_ends_it(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run(At("force-replace", 100f, false, 3f,
            r => r.Actor.SetConstantForce(Force(r, 2f, 0f, 0f), false),
            r =>
            {
                if (r.Stage == 0 && r.PhysicsTime >= 1.0 - 1e-9) { r.Actor.SetConstantForce(Force(r, 0f, 2f, 0f), false); r.Stage = 1; }
                if (r.Stage == 1 && r.PhysicsTime >= 2.0 - 1e-9) { r.Actor.SetConstantForce(Vector3.Zero, false); r.Stage = 2; }
            }), physicsRate);
        (RunResult none, List<Point> noneTrace) = Run(At("force-none", 100f, false, 3f, _ => { }), physicsRate);

        double ax1 = Accel(trace, p => p.X, 0.1, 1.0), ay1 = Accel(trace, p => p.Y, 0.1, 1.0);
        double ax2 = Accel(trace, p => p.X, 1.1, 2.0), ay2 = Accel(trace, p => p.Y, 1.1, 2.0);
        double ax3 = Accel(trace, p => p.X, 2.1, 3.0), ay3 = Accel(trace, p => p.Y, 2.1, 3.0);
        _out.WriteLine($"{r.Name} p{physicsRate}: east ({ax1:0.0000}, {ay1:0.0000}), north ({ax2:0.0000}, {ay2:0.0000}), ended ({ax3:0.0000}, {ay3:0.0000}) m/s2");
        Within(ax1, 2.0, 0.02, $"{r.Name} east force, east acceleration");
        Assert.True(Math.Abs(ay1) < 0.04, $"{r.Name}: east force, north acceleration {ay1:0.0000}");
        Within(ay2, 2.0, 0.02, $"{r.Name} north force, north acceleration");
        Assert.True(Math.Abs(ax2) < 0.04, $"{r.Name}: north force, east acceleration {ax2:0.0000}");

        // After zero: the change of reported velocity in each heartbeat is the unforced avatar's.
        int from = trace.FindIndex(p => p.PhysicsT >= 2.0 + 0.2);
        Assert.True(from > 0 && trace.Count == noneTrace.Count, $"{r.Name}: {trace.Count} against {noneTrace.Count} heartbeats");
        for (int i = from + 1; i < trace.Count; i++)
        {
            Vector3 dv = trace[i].Velocity - trace[i - 1].Velocity, dvNone = noneTrace[i].Velocity - noneTrace[i - 1].Velocity;
            Assert.True((dv - dvNone).Length() <= 1e-3f,
                $"{r.Name}: heartbeat {i} changed the velocity by {dv}, one that never had a force by {dvNone}");
        }
        Assert.True(trace[^1].Velocity.X > 1.9f && trace[^1].Velocity.Y > 1.9f, $"{r.Name}: it kept {trace[^1].Velocity} after the force ended");

        // Set and ended before any step: the avatar moves exactly as one that never had a force.
        (RunResult ended, _) = Run(At("force-none", 100f, false, 3f, r =>
        {
            r.Actor.SetConstantForce(Force(r, 2f, 3f, 4f), true);
            r.Actor.SetConstantForce(Vector3.Zero, true);
        }), physicsRate);
        Assert.Equal(none.Samples.Count, ended.Samples.Count);
        for (int i = 0; i < none.Samples.Count; i++)
            Assert.True(none.Samples[i].Position == ended.Samples[i].Position && none.Samples[i].Velocity == ended.Samples[i].Velocity,
                $"heartbeat {i}: {ended.Samples[i].Position} / {ended.Samples[i].Velocity} against {none.Samples[i].Position} / {none.Samples[i].Velocity}");
    }

    // ------------------------------------------------------------------ refused

    // A force that is not a finite number is refused, as every other non-finite value on an actor is (NonFiniteGuard): the
    // force set before stays. One handed to a fresh avatar leaves it exactly as one that never had a force.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void A_force_that_is_not_a_finite_number_is_refused(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run(At("force-non-finite", 20f, false, 3f, r =>
        {
            r.Actor.SetConstantForce(Force(r, 0f, 0f, G), false);
            r.Actor.SetConstantForce(new Vector3(float.NaN, 0f, 0f), false);
            r.Actor.SetConstantForce(new Vector3(0f, float.PositiveInfinity, 0f), true);
            r.Actor.SetConstantForce(new Vector3(0f, 0f, float.NegativeInfinity), false);
        }), physicsRate);
        float most = trace.Max(p => (p.Position - trace[0].Position).Length());
        Assert.True(most <= 0.01f, $"{r.Name}: the avatar moved {most:0.0000} m; the refused forces replaced the weight");
        Assert.True(trace.All(p => float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y) && float.IsFinite(p.Position.Z)), $"{r.Name}: a position is not finite");

        (RunResult none, _) = Run(At("force-none", 20f, false, 3f, _ => { }), physicsRate);
        (RunResult nan, _) = Run(At("force-none", 20f, false, 3f, r => r.Actor.SetConstantForce(new Vector3(float.NaN, 1f, 1f), false)), physicsRate);
        for (int i = 0; i < none.Samples.Count; i++)
            Assert.True(none.Samples[i].Position == nan.Samples[i].Position, $"heartbeat {i}: {nan.Samples[i].Position} against {none.Samples[i].Position}");
    }

    // ------------------------------------------------------------------ a new backend character

    // The force survives the avatar's backend character being made again. Sitting and standing up (and a teleport within
    // the region) remove the avatar's actor and add a new one (ScenePresence.RemoveFromPhysicalScene, AddToPhysicalScene),
    // and core hands the new actor the force it holds (ScenePresence.PhysicsActor's setter, HandConstantForceTo): the new
    // character holds the avatar up as the first did. A new appearance gives the character a new shape, and the force goes
    // on.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_force_survives_the_avatars_backend_character_being_made_again(double physicsRate)
    {
        Vector3 held = Vector3.Zero;
        (RunResult r, List<Point> trace) = Run(At("force-remade", 20f, false, 4f, r =>
        {
            held = Force(r, 0f, 0f, G);
            r.Actor.SetConstantForce(held, false);
        }, r =>
        {
            if (r.Stage == 0 && r.PhysicsTime >= 1.0 - 1e-9)
            {
                Vector3 at = r.Actor.Position;
                r.PhysicsScene.RemoveAvatar(r.Actor);
                r.AddAvatarAt(at, false);
                r.Actor.SetConstantForce(held, false);   // HandConstantForceTo
                r.Stage = 1;
            }
            if (r.Stage == 1 && r.PhysicsTime >= 2.0 - 1e-9)
            {
                r.Actor.Size = new Vector3(0.5f, 0.7f, 2.3f);
                r.Stage = 2;
            }
        }), physicsRate);
        float z0 = trace[0].Position.Z;
        float most = trace.Max(p => MathF.Abs(p.Position.Z - z0));
        _out.WriteLine($"{r.Name} p{physicsRate}: made again at 1 s, reshaped at 2 s, height moved at most {most * 1000f:0.000} mm in 4 s");
        Assert.True(most <= 0.01f, $"{r.Name}: the avatar moved {most:0.0000} m up or down");
    }

    // ------------------------------------------------------------------ speed limits

    // The limits on an avatar's speed still hold under a force. A large force is capped by the engine's body speed cap
    // (PhysicsBackendSettings.MaxBodyLinearSpeed, 500 m/s by default), the cap a prim under llSetForce meets: 1e6 N east
    // (12500 m/s2) reaches it in 0.04 s and goes no faster. A push on an avatar under a force still adds no more than the
    // push cap. Hover's vertical speed stays within its 50 m/s.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_speed_limits_still_hold_under_a_force(double physicsRate)
    {
        (RunResult big, List<Point> _) = Run(At("force-large", 100f, false, 0.3f, r => r.Actor.SetConstantForce(new Vector3(1e6f, 0f, 0f), false)), physicsRate);
        float top = big.Samples.Max(s => s.Speed);
        _out.WriteLine($"{big.Name} p{physicsRate}: 1e6 N east, top speed {top:0.000} m/s, at the end {big.Samples[^1].Speed:0.000}");
        Assert.True(top <= BodySpeedCap * 1.01f, $"{big.Name}: {top:0.0} m/s, over the {BodySpeedCap} m/s body speed cap");
        Assert.True(big.Samples[^1].Velocity.X >= BodySpeedCap * 0.98f, $"{big.Name}: only {big.Samples[^1].Velocity.X:0.0} m/s east");

        // A flying avatar (its push is not shared with a fall's speed) under a force east, pushed east with far more than the
        // cap at 1 s: in the heartbeat that carries the push it goes faster than its twin by no more than the cap (less the
        // flying fade within that heartbeat).
        Scenario Flying(string name, bool push) => At(name, 30f, true, 1.5f, r => r.Actor.SetConstantForce(Force(r, 2f, 0f, 0f), false), r =>
        {
            if (push && r.Stage == 0 && r.PhysicsTime >= 1.0 - 1e-9) { r.Actor.AddForce(new Vector3(1e9f, 0f, 0f), true); r.Stage = 1; }
        });
        (RunResult forced, _) = Run(Flying("force-push-none", false), physicsRate);
        (RunResult pushed, _) = Run(Flying("force-push", true), physicsRate);
        int at = Enumerable.Range(0, pushed.Samples.Count).First(i => pushed.Samples[i].Velocity != forced.Samples[i].Velocity);
        float added = pushed.Samples[at].Velocity.X - forced.Samples[at].Velocity.X;
        float mostAdded = Enumerable.Range(0, pushed.Samples.Count).Max(i => pushed.Samples[i].Velocity.X - forced.Samples[i].Velocity.X);
        _out.WriteLine($"{pushed.Name} p{physicsRate}: a 1e9 push added {added:0.000} m/s in its heartbeat, at most {mostAdded:0.000}, to a flying avatar under a force (push cap {PushCap})");
        Assert.InRange(added, PushCap * 0.85f, PushCap * 1.02f);
        Assert.True(mostAdded <= PushCap * 1.02f, $"{pushed.Name}: the push added {mostAdded:0.000} m/s");

        (RunResult hover, _) = Run(At("force-hover-large", 0f, false, 2f, r =>
        {
            r.Actor.PIDHoverHeight = 3f;
            r.Actor.PIDHoverType = PIDHoverType.Ground;
            r.Actor.PIDHoverTau = 0.5f;
            r.Actor.PIDHoverActive = true;
            r.Actor.SetConstantForce(new Vector3(0f, 0f, 1e6f), false);
        }), physicsRate);
        float up = hover.Samples.Max(s => MathF.Abs(s.Velocity.Z));
        _out.WriteLine($"{hover.Name} p{physicsRate}: hovering with 1e6 N up, top vertical speed {up:0.000} m/s");
        Assert.True(up <= 50f * 1.02f, $"{hover.Name}: {up:0.0} m/s up or down, over hover's 50 m/s");
        Assert.True(up >= 45f, $"{hover.Name}: only {up:0.0} m/s up; the force did not act on the hovering avatar");
    }

    // ------------------------------------------------------------------ the cases of WHAT-SHOULD-HAPPEN

    // Standing on the ground. A downward force, or an upward one less than the weight: the ground holds the avatar and it
    // does not move. A level force: it drifts that way, settling at force / mass x 0.25 s (the ground's fade on a push's
    // speed): 2 m/s2 gives 0.5 m/s. When the force ends it stops, as after a push, and then stands exactly still.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void On_the_ground_a_force_below_the_weight_holds_and_a_level_force_drifts(double physicsRate)
    {
        foreach ((string name, Vector3 a) in new[] { ("force-ground-down", new Vector3(0f, 0f, -2f * G)), ("force-ground-up-half", new Vector3(0f, 0f, 0.5f * G)) })
        {
            (RunResult r, List<Point> trace) = Run(At(name, 0f, false, 3f, r => r.Actor.SetConstantForce(a * r.Actor.Mass, false)), physicsRate);
            Point stood = trace.First(p => p.PhysicsT >= 1.0 - 1e-9);   // put 1 cm above the ground, it has settled on it
            float moved = trace.Where(p => p.PhysicsT >= stood.PhysicsT).Max(p => (p.Position - stood.Position).Length());
            _out.WriteLine($"{name} p{physicsRate}: moved {moved * 1000f:0.000} mm from 1 s to 3 s");
            Assert.True(moved <= 0.001f, $"{name}: the standing avatar moved {moved:0.0000} m");
            Assert.True(r.Samples[^1].Touching, $"{name}: not on the ground at the end");
        }

        (RunResult d, List<Point> drift) = Run(At("force-ground-level", 0f, false, 7f, r => r.Actor.SetConstantForce(Force(r, 2f, 0f, 0f), false), r =>
        {
            if (r.Stage == 0 && r.PhysicsTime >= 4.0 - 1e-9) { r.Actor.SetConstantForce(Vector3.Zero, false); r.Stage = 1; }
        }), physicsRate);
        List<Point> before = drift.Where(p => p.PhysicsT <= 4.0 + 1e-9).ToList();
        Vector3 v = MovedOverLast(before, 1.0);
        Vector3 after = MovedOverLast(drift, 1.0);
        _out.WriteLine($"{d.Name} p{physicsRate}: 2 m/s2 east drifts at {v.X:0.0000} m/s (expected {2f * GroundFade}); after it ended, {after.Length():0.000000} m/s over the last 1 s (2 to 3 s after)");
        Within(v.X, 2f * GroundFade, 0.03, $"{d.Name} drift");
        Assert.True(after.Length() == 0f, $"{d.Name}: it still moves at {after} 2 to 3 s after the force ended");
        Assert.True(d.Samples[^1].Touching, $"{d.Name}: not on the ground");
    }

    // Walking and running: the force's drift is added to the walk or run speed (+0.5 m/s for 2 m/s2 along the way).
    [Theory]
    [InlineData(0.0, false)]
    [InlineData(45.0, false)]
    [InlineData(0.0, true)]
    [InlineData(45.0, true)]
    public void Walking_or_running_a_level_force_adds_its_drift_to_the_speed(double physicsRate, bool run)
    {
        Scenario Walk(string name, bool force) => At(name, 0f, false, 4f, r =>
        {
            r.Actor.SetAlwaysRun = run;
            if (force) r.Actor.SetConstantForce(Force(r, 2f, 0f, 0f), false);
        }, r =>
        {
            if (r.Stage == 0) { r.Actor.TargetVelocity = new Vector3(WalkRequest, 0f, 0f); r.Stage = 1; }
        });
        (RunResult free, List<Point> freeTrace) = Run(Walk(run ? "run" : "walk", false), physicsRate);
        (RunResult forced, List<Point> forcedTrace) = Run(Walk(run ? "force-run" : "force-walk", true), physicsRate);
        float vFree = MovedOverLast(freeTrace, 1.0).X, vForced = MovedOverLast(forcedTrace, 1.0).X;
        _out.WriteLine($"{forced.Name} p{physicsRate}: {vFree:0.0000} m/s without, {vForced:0.0000} with 2 m/s2 east (+{vForced - vFree:0.0000}, expected +{2f * GroundFade})");
        Within(vForced - vFree, 2f * GroundFade, 0.03, $"{forced.Name} added speed");
    }

    // Flying: gravity is off and the force adds to the flight, fading as a push does while flying (1 s): the weight up
    // lifts a flying avatar that asks for no movement, settling at g x 1 s = 9.81 m/s.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void Flying_an_upward_force_lifts_the_avatar(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run(At("force-fly-weight", 30f, true, 6f, r => r.Actor.SetConstantForce(Force(r, 0f, 0f, G), false)), physicsRate);
        double t = trace[^1].PhysicsT;
        Vector3 v = MovedOverLast(trace, 0.5);
        double expected = G * FlyingFade * (1 - Math.Exp(-(t - 0.25) / FlyingFade));
        _out.WriteLine($"{r.Name} p{physicsRate}: flying, weight up, rising at {v.Z:0.0000} m/s at {t:0.00} s (expected {expected:0.0000})");
        Within(v.Z, expected, 0.03, $"{r.Name} rise");
    }

    // Hovering: the hover's critically damped spring plus the force. A constant upward acceleration a moves the height it
    // settles at up by a x tau^2: the weight up, tau 0.5 s, gives 2.45 m above the 3 m asked.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void Hovering_an_upward_force_raises_the_height_it_settles_at(double physicsRate)
    {
        Scenario Hover(string name, bool force) => At(name, 0f, false, 8f, r =>
        {
            r.Actor.PIDHoverHeight = 3f;
            r.Actor.PIDHoverType = PIDHoverType.Ground;
            r.Actor.PIDHoverTau = 0.5f;
            r.Actor.PIDHoverActive = true;
            if (force) r.Actor.SetConstantForce(Force(r, 0f, 0f, G), false);
        });
        (RunResult free, _) = Run(Hover("hover", false), physicsRate);
        (RunResult forced, _) = Run(Hover("force-hover", true), physicsRate);
        float rise = forced.Samples[^1].Position.Z - free.Samples[^1].Position.Z;
        _out.WriteLine($"{forced.Name} p{physicsRate}: settles {rise:0.0000} m above the hover without the force (expected {G * 0.25f:0.0000})");
        Within(rise, G * 0.25f, 0.02, $"{forced.Name} rise");
    }

    // With buoyancy: both act. Buoyancy 0.5 leaves half of gravity, and an upward force of half the weight takes the rest:
    // it floats where it was put.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void With_buoyancy_the_two_add(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run(At("force-buoyancy", 20f, false, 5f, r =>
        {
            r.Actor.Buoyancy = 0.5f;
            r.Actor.SetConstantForce(Force(r, 0f, 0f, 0.5f * G), false);
        }), physicsRate);
        float most = trace.Max(p => MathF.Abs(p.Position.Z - trace[0].Position.Z));
        _out.WriteLine($"{r.Name} p{physicsRate}: buoyancy 0.5 and half the weight up, height moved at most {most * 1000f:0.000} mm in 5 s");
        Assert.True(most <= 0.01f, $"{r.Name}: the avatar moved {most:0.0000} m up or down");
    }

    // ------------------------------------------------------------------ what an attachment script asks for

    // An attachment's script that holds its wearer up asks for llGetMass() x 9.8 newtons, and llGetMass() in an attachment is
    // the wearer's actor's Mass, the mass the backend divides the force by. That force holds a walking avatar's height,
    // whatever the avatar's size (the actor's mass does not change with the box): over 8 s it sinks by no more than the
    // 9.8 against 9.80665 leaves (0.5 x 0.00665 x 64 = 0.21 m), and it never rises.
    [Theory]
    [InlineData(0.0, 1.4f)]
    [InlineData(45.0, 1.4f)]
    [InlineData(0.0, 1.9f)]
    [InlineData(45.0, 1.9f)]
    [InlineData(0.0, 2.6f)]
    [InlineData(45.0, 2.6f)]
    public void The_mass_the_actor_reports_times_gravity_holds_a_walking_avatar_of_any_size(double physicsRate, float height)
    {
        var box = new Vector3(0.45f, 0.6f, height);
        float mass = 0f;
        var sc = new Scenario
        {
            Name = $"force-reported-mass-{height:0.0}",
            Description = "an avatar of one size, not flying, 20 m up, with the force its attachment's script reads off its own mass",
            DefaultDuration = _ => 8f,
            Setup = r =>
            {
                PhysicsActor pa = r.PhysicsScene.AddAvatar(OpenSim.Region.PhysicsModules.Jolt.Harness.Run.ActorLocalId, "Test User", new Vector3(X, Y, r.GroundAt(X, Y) + 20f), box, 0f, false);
                r.Actor = pa;
                r.ActorSize = box;
                mass = pa.Mass;
                pa.SetConstantForce(new Vector3(0f, 0f, mass * 9.8f), false);
            },
            Input = _ => { },
        };
        (RunResult r, List<Point> trace) = Run(sc, physicsRate);
        float z0 = trace[0].Position.Z;
        float rose = trace.Max(p => p.Position.Z - z0), sank = trace.Max(p => z0 - p.Position.Z);
        _out.WriteLine($"{r.Name} p{physicsRate}: mass {mass}, rose at most {rose * 1000f:0.000} mm, sank at most {sank * 1000f:0.000} mm in {trace[^1].PhysicsT:0.0} s");
        Assert.True(rose <= 0.001f, $"{r.Name}: the avatar rose {rose:0.0000} m");
        Assert.True(sank <= 0.25f, $"{r.Name}: the avatar sank {sank:0.0000} m");
    }

    // The same force on an avatar that is flying lifts it, as designed (gravity is off, so the force is all there is), and
    // when the flight ends it goes on rising at the speed it had: 9.8 N per kg against gravity leaves nothing to speed it up
    // or slow it. This is the sequence a script saw when the simulator's autopilot (a walk to a point higher than the ground
    // plus an avatar's height puts the walker into flight, ScenePresence.MoveToTarget) flew the wearer for a short while
    // and then handed it back to walking.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public void The_weight_up_lifts_a_flying_avatar_and_it_keeps_its_speed_when_the_flight_ends(double physicsRate)
    {
        (RunResult r, List<Point> trace) = Run(At("force-weight-flight-then-walk", 20f, true, 7f, r => r.Actor.SetConstantForce(Force(r, 0f, 0f, 9.8f), false), r =>
        {
            if (r.Stage == 0 && r.PhysicsTime >= FlightEnds - 1e-9) { r.Actor.Flying = false; r.Stage = 1; }
        }), physicsRate);
        float z0 = trace[0].Position.Z;
        Point Near(double t) => trace.First(p => p.PhysicsT >= t - 1e-9);
        float before = Near(FlightEnds).Velocity.Z, after = Near(FlightEnds + 1.0).Velocity.Z;
        float rise = trace.Max(p => p.Position.Z) - z0;
        _out.WriteLine($"{r.Name} p{physicsRate}: rising at {before:0.000} m/s when the flight ends (after {FlightEnds} s), {after:0.000} m/s a second after; {rise:0.00} m in {trace[^1].PhysicsT:0.0} s");
        Assert.True(before > 2f, $"{r.Name}: it flew up at only {before:0.000} m/s");
        Within(after, before, 0.1, $"{r.Name} speed after the flight ends");
    }

    private const double FlightEnds = 0.5;               // when the autopilot's flight ended in that run (s)
}
