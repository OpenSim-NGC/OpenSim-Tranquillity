/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The laws of <see cref="HalcyonBasicForces"/> on their own, with no engine: each is compared with a literal transcription of
/// the Halcyon code it was ported from (<see cref="HalcyonAsWritten"/>) at Halcyon's own step, and at the other step lengths
/// the collision steps of 32, 45 and 64 Hz with 1 to 4 steps give (4.9 to 31 ms). Parallel with the other classes: it touches
/// no process-wide state.
/// </summary>
public class HalcyonBasicForcesTests
{
    private readonly ITestOutputHelper _out;
    public HalcyonBasicForcesTests(ITestOutputHelper output) { _out = output; }
    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float H0 = HalcyonBasicForces.StepSeconds;

    /// <summary>The collision step lengths of the rates the module runs, with n = 1 to 4 (32 and 45 Hz with one step included).</summary>
    public static IEnumerable<object[]> Cells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = 1; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private static double Step(double rate, int n) => 1.0 / rate / n;

    // ---- Buoyancy ----

    [Theory]
    [InlineData(0.9f, 0.9f)]
    [InlineData(0.976f, 0.976f)]
    [InlineData(0.977f, 1f)]
    [InlineData(0.98f, 1f)]
    [InlineData(0.999f, 1f)]
    [InlineData(1f, 1f)]
    [InlineData(1.2f, 1.2f)]
    [InlineData(0f, 0f)]
    [InlineData(-0.5f, -0.5f)]
    public void A_buoyancy_from_0_977_to_under_1_is_taken_as_1(float given, float expected)
        => Assert.Equal(expected, HalcyonBasicForces.LegacyBuoyancy(given));

    // ---- The second-order step ----

    [Theory]
    [InlineData(1f, 2f)]
    [InlineData(33f, 2f)]
    [InlineData(1000f, 50f)]
    public void The_second_order_step_is_Halcyons_own_at_Halcyons_step(float kappa, float c)
    {
        // Halcyon adds -kappa h x - c h v to the velocity in each step: alpha = -kappa h0, beta = 1 - c h0.
        (float alpha, float beta) = HalcyonBasicForces.SecondOrder(kappa, c, H0);
        Assert.Equal(-kappa * H0, alpha, 4);
        Assert.Equal(1f - c * H0, beta, 5);
    }

    [Theory]
    [InlineData(1f, 2f)]
    [InlineData(33f, 2f)]
    [InlineData(100f, 100f)]
    [InlineData(1000f, 30f)]
    public void The_second_order_step_gives_the_same_motion_at_every_step(float kappa, float c)
    {
        // 1 m from the target, at rest: the position after 1 s is the same whichever way the second is cut into steps.
        double reference = Simulate(kappa, c, H0, 1.0);
        foreach (double h in new[] { 1.0 / 128, 1.0 / 90, 1.0 / 64, 1.0 / 45, 1.0 / 32 })
        {
            double got = Simulate(kappa, c, h, 1.0);
            Say($"[rate-table] second order kappa {kappa} c {c} | step {h * 1000:0.0} ms | {got:0.0000} against {reference:0.0000}");
            Assert.True(Math.Abs(got - reference) <= 0.02, $"step {h}: {got} against {reference}");
        }
    }

    // x after t seconds from x = 1, v = 0, with the map the provider uses for a step of h (v' = alpha x + beta v, x' = x + h v').
    private static double Simulate(float kappa, float c, double h, double t)
    {
        (float alpha, float beta) = HalcyonBasicForces.SecondOrder(kappa, c, (float)h);
        double x = 1, v = 0;
        int steps = (int)Math.Round(t / h);
        for (int i = 0; i < steps; i++)
        {
            v = alpha * x + beta * v;
            x += h * v;
        }
        return x;
    }

    // ---- Move to target ----

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.5f)]
    [InlineData(2f)]
    public void Move_to_at_Halcyons_step_is_distance_over_tau(float tau)
    {
        var d = new Vector3(3f, -4f, 1f);
        Vector3 v = HalcyonBasicForces.MoveToVelocity(d, tau, H0);
        Assert.Equal(d.X / tau, v.X, 3);
        Assert.Equal(d.Y / tau, v.Y, 3);
        Assert.Equal(d.Z / tau, v.Z, 3);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Move_to_covers_the_same_distance_in_the_same_time_at_every_step(double rate, int n)
    {
        // Halcyon's distance left after k steps of H0 is d (1 - H0/tau)^k. After 1 s at the step the engine takes it is the same.
        foreach (float tau in new[] { 0.1f, 0.5f, 2f })
        {
            double h = Step(rate, n);
            double x = 10.0;
            int steps = (int)Math.Round(1.0 / h);
            for (int i = 0; i < steps; i++)
                x -= HalcyonBasicForces.MoveToVelocity(new Vector3((float)x, 0f, 0f), tau, (float)h).X * h;
            double expected = 10.0 * Math.Pow(1.0 - H0 / tau, steps * h / H0);
            Say($"[rate-table] move-to tau {tau} | {rate} Hz x {n} | left after 1 s {x:0.0000} against {expected:0.0000}");
            Assert.True(Math.Abs(x - expected) <= 0.002 * 10.0, $"tau {tau}: left {x} against {expected}");
        }
    }

    // ---- Hover and ground repel against the Halcyon code as written ----

    [Fact]
    public void Hover_at_Halcyons_step_is_Halcyons_own_for_every_branch()
    {
        var rng = new Random(7);
        int compared = 0;
        for (int i = 0; i < 4000; i++)
        {
            float tau = 0.03f + (float)rng.NextDouble() * 5f;
            float height = 0.2f + (float)rng.NextDouble() * 20f;
            float above = height + (float)(rng.NextDouble() * 2 - 1) * (rng.Next(3) == 0 ? 0.1f : 6f);
            float vz = (float)(rng.NextDouble() * 2 - 1) * (rng.Next(3) == 0 ? 0.02f : 8f);
            bool repel = rng.Next(2) == 0;
            HalcyonBasicForces.HoverResult got = HalcyonBasicForces.Hover(above, height, tau, repel, vz, H0);
            (float want, bool gravityOff, bool acts) = HalcyonAsWritten.Hover(above, height, tau, repel, vz, H0);
            Assert.Equal(gravityOff, got.GravityOff);
            Assert.Equal(acts, got.Acts);
            if (acts)
                Assert.True(Math.Abs(want - got.VerticalVelocity) <= 1e-4f * Math.Max(1f, Math.Abs(want)),
                    $"above {above} height {height} tau {tau} repel {repel} vz {vz}: {got.VerticalVelocity} against {want}");
            compared++;
        }
        Assert.Equal(4000, compared);
    }

    [Fact]
    public void Inside_the_butter_zone_only_the_vertical_drag_acts_and_gravity_is_off()
    {
        // 3.02 m above ground with a hover height of 3: inside 0.05 m. A fast vertical speed loses 2 a second; a slow one (under 0.01) stops.
        HalcyonBasicForces.HoverResult r = HalcyonBasicForces.Hover(3.02f, 3f, 1f, false, 1f, H0);
        Assert.True(r.GravityOff);
        Assert.Equal(1f - 2f * H0, r.VerticalVelocity, 4);
        r = HalcyonBasicForces.Hover(3.02f, 3f, 1f, false, 0.005f, H0);
        Assert.Equal(0f, r.VerticalVelocity, 6);
    }

    [Fact]
    public void A_repel_above_its_height_leaves_gravity_on_and_a_hover_there_pulls_down()
    {
        HalcyonBasicForces.HoverResult repel = HalcyonBasicForces.Hover(5f, 3f, 1f, true, 0f, H0);
        Assert.False(repel.GravityOff);
        Assert.False(repel.Acts);
        HalcyonBasicForces.HoverResult hover = HalcyonBasicForces.Hover(5f, 3f, 1f, false, 0f, H0);
        Assert.True(hover.GravityOff);
        Assert.True(hover.VerticalVelocity < 0f);   // a hover pulls an object down to its height
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-3f)]
    public void A_repel_below_its_height_only_pushes_up_and_cancels_a_fall(float vz)
    {
        HalcyonBasicForces.HoverResult r = HalcyonBasicForces.Hover(1f, 3f, 1f, true, vz, H0);
        Assert.True(r.GravityOff);
        Assert.True(r.VerticalVelocity > 0f, $"vz {vz} gave {r.VerticalVelocity}");
        if (vz < 0f)
            Assert.True(r.VerticalVelocity < 0.2f);   // the fall is cancelled; only the push of one step is left
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Hover_settles_to_the_same_height_and_time_at_every_step(double rate, int n)
    {
        // Tau 1, a hover height of 3 m: from 2 m below it (a 2 m rise) and from 4 m above it, at rest. The path of Halcyon's code
        // at 15 ms, and ours at the step this rate and number of collision steps give.
        double h = Step(rate, n);
        foreach (float start in new[] { 1f, 7f })
        {
            (double zRef, double tRef) = RunHover(H0, true, 1f, 3f, start);
            (double z, double t) = RunHover(h, false, 1f, 3f, start);
            double scale = Math.Abs(start - 3f);
            Say($"[rate-table] hover from {start} to 3 | {rate} Hz x {n} | height at 3 s {z:0.0000} against {zRef:0.0000} | first inside the zone {t:0.000} s against {tRef:0.000} s");
            Assert.True(Math.Abs(z - zRef) <= 0.02 * scale, $"height {z} against {zRef}");
            Assert.True((t < 0) == (tRef < 0), $"entered the zone {t} against {tRef}");
            if (tRef >= 0)
                Assert.True(Math.Abs(t - tRef) <= 0.02 * Math.Max(tRef, 1.0) + 0.02, $"time {t} against {tRef}");
        }
    }

    // Hover (or repel): the height above the ground after 3 s and when it first came inside the butter zone, from
    // `start` m above the ground. asWritten runs the literal Halcyon code with the step h.
    private static (double z, double t) RunHover(double h, bool asWritten, float tau, float height, float start)
    {
        double z = start, v = 0, entered = -1;
        int steps = (int)Math.Round(3.0 / h);
        for (int i = 0; i < steps; i++)
        {
            double time = i * h;
            float newV;
            if (asWritten)
                newV = HalcyonAsWritten.Hover((float)z, height, tau, false, (float)v, (float)h).vz;
            else
            {
                HalcyonBasicForces.HoverResult r = HalcyonBasicForces.Hover((float)z, height, tau, false, (float)v, (float)h);
                newV = r.Acts ? r.VerticalVelocity : (float)v;
            }
            v = newV;
            z += v * h;
            if (entered < 0 && Math.Abs(z - height) < HalcyonBasicForces.AtHoverHeightTolerance)
                entered = time + h;
        }
        return (z, entered);
    }

    // ---- Rotation look-at at a long step ----

    [Theory]
    [InlineData(0.1f, 0.1f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.6f, 0.1f)]
    [InlineData(0.3f, 5f)]
    public void Look_at_at_Halcyons_step_is_Halcyons_own_where_Halcyons_step_is_sound(float strength, float damping)
    {
        // Halcyon's drag (100 strength per second times 15 ms) is under 1 for a strength under 0.667: the step is its own.
        var rng = new Random(11);
        for (int i = 0; i < 500; i++)
        {
            var turn = new Vector3((float)rng.NextDouble() * 3 - 1.5f, (float)rng.NextDouble() * 3 - 1.5f, (float)rng.NextDouble() * 3 - 1.5f);
            var w = new Vector3((float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2);
            Vector3 got = HalcyonBasicForces.RotLook(turn, w, strength, damping, H0);
            Vector3 want = HalcyonAsWritten.RotLook(turn, w, strength, damping, H0);
            Assert.True((got - want).Length() <= 1e-3f * Math.Max(1f, want.Length()), $"turn {turn} w {w}: {got} against {want}");
        }
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(1f, 0.1f)]
    [InlineData(0.8f, 0.5f)]
    public void Halcyons_look_at_grows_without_limit_above_20_ms_and_ours_does_not(float strength, float damping)
    {
        // Halcyon's damping coefficient is 100 strength dt; at strength 1 it passes 2 (the velocity grows each step) above 20 ms.
        double asWrittenAt31 = LookAtSettleAngle(Step(32, 1), true, strength, damping, 3.0, out bool blewUp);
        Say($"[look-at] strength {strength} damping {damping} | Halcyon's code as written at 31 ms: angle left after 3 s {asWrittenAt31:0.0000e+0}, grew without limit: {blewUp}");
        if (strength * HalcyonBasicForces.RotLookAtScale * Step(32, 1) > 2.0)
            Assert.True(blewUp, "the code as written should be unstable at 31 ms");
        foreach ((double rate, int n) in new[] { (32.0, 1), (45.0, 1), (32.0, 2), (64.0, 1), (45.0, 2) })
        {
            double left = LookAtSettleAngle(Step(rate, n), false, strength, damping, 3.0, out bool bad);
            Say($"[look-at] strength {strength} damping {damping} | ours at {rate} Hz x {n} ({Step(rate, n) * 1000:0.0} ms): angle left after 3 s {left:0.0000e+0}");
            double fine = LookAtSettleAngle(1.0 / 512, false, strength, damping, 3.0, out _);
            Assert.False(bad);
            Assert.True(Math.Abs(left - fine) <= 0.02, $"{rate} Hz x {n}: {left} rad left against {fine} at 2 ms");
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Look_at_settles_in_the_same_time_at_every_step(double rate, int n)
    {
        foreach ((float strength, float damping) in new[] { (0.3f, 0.5f), (0.5f, 1f), (1f, 1f), (1f, 0.1f) })
        {
            double h = Step(rate, n);
            double tOurs = SettleTime(h, false, strength, damping);
            // Halcyon's code at its own step is the reference where that step is sound (the drag 100 strength x 15 ms is under 1);
            // where it is not (strength over 0.667: it flips the velocity's sign each step) there is no path to follow, and the
            // reference is the same law at a step of 2 ms.
            bool sound = strength * HalcyonBasicForces.RotLookAtScale * H0 < 1f;
            double tRef = sound ? SettleTime(H0, true, strength, damping) : SettleTime(1.0 / 512, false, strength, damping);
            Say($"[rate-table] look-at S {strength} D {damping} | {rate} Hz x {n} | within a degree after {tOurs:0.000} s against {tRef:0.000} s ({(sound ? "Halcyon's code at 15 ms" : "the same law at 2 ms")})");
            Assert.True(Math.Abs(tOurs - tRef) <= 0.02 * Math.Max(tRef, 1.0) + 0.03, $"{tOurs} against {tRef}");
        }
    }

    // Angle left (rad) after t seconds from a quarter turn at rest; blewUp when it passed a full turn at any step.
    private static double LookAtSettleAngle(double h, bool asWritten, float strength, float damping, double t, out bool blewUp)
    {
        double angle = Math.PI / 2, w = 0;
        blewUp = false;
        int steps = (int)Math.Round(t / h);
        for (int i = 0; i < steps; i++)
        {
            // `turn` takes the object to the target: positive while the angle left is positive.
            var turn = new Vector3(0f, 0f, (float)angle);
            var cur = new Vector3(0f, 0f, (float)w);
            Vector3 next = asWritten
                ? HalcyonAsWritten.RotLook(turn, cur, strength, damping, (float)h)
                : HalcyonBasicForces.RotLook(turn, cur, strength, damping, (float)h);
            w = next.Z;
            angle -= w * h;
            if (Math.Abs(angle) > 2 * Math.PI || double.IsNaN(angle))
            {
                blewUp = true;
                return Math.Abs(angle);
            }
        }
        return Math.Abs(angle);
    }

    // When the angle left first drops under a degree and stays there for the rest of 6 s.
    private static double SettleTime(double h, bool asWritten, float strength, double damping)
    {
        double angle = Math.PI / 2, w = 0, lastOut = 0;
        int steps = (int)Math.Round(6.0 / h);
        for (int i = 0; i < steps; i++)
        {
            Vector3 turn = new Vector3(0f, 0f, (float)angle);
            Vector3 cur = new Vector3(0f, 0f, (float)w);
            // `turn` is what takes the object to the target: positive while the angle left is positive.
            Vector3 next = asWritten
                ? HalcyonAsWritten.RotLook(turn, cur, strength, (float)damping, (float)h)
                : HalcyonBasicForces.RotLook(turn, cur, strength, (float)damping, (float)h);
            w = next.Z;
            angle -= w * h;
            if (Math.Abs(angle) > HalcyonBasicForces.AtRotTolerance)
                lastOut = (i + 1) * h;
            if (Math.Abs(angle) > 10) return 99;
        }
        return lastOut;
    }
}

/// <summary>
/// A literal transcription of the Halcyon code the laws are ported from (InWorldz.PhysxPhysics/PhysxPrim.pid.cs at
/// OpenSim-NGC/OpenSim-HalcyonGrid develop 317788374b), with the step as an argument: DoHoverTarget, DoHover, DoGroundRepel,
/// DoHoverDamping and DoRotLookTarget, in single precision, one step. The result is the velocity after the impulses and velocity
/// changes Halcyon queues have been applied. Test code: it is the reference the ported laws are compared with.
/// </summary>
internal static class HalcyonAsWritten
{
    public static (float vz, bool gravityOff, bool acts) Hover(float groundHeight, float hoverHeight, float hoverTau, bool repel, float vel, float timeStep)
    {
        const float damping = 1f;
        float dv = 0f;
        float targetDiff = Math.Abs(groundHeight - hoverHeight);
        if (targetDiff < (0.05f * damping))
            return (vel + DoHoverDamping(vel, damping, timeStep), true, true);

        if (repel)
        {
            if (groundHeight > hoverHeight + 0.05f)
                return (vel, false, false);
            float diff = hoverHeight - groundHeight;
            if (vel < 0.0f)
                dv += -vel;
            dv += diff * (1.0f / hoverTau) * timeStep;          // _mass * timeStep impulse over mass
            dv += DoHoverDamping(vel, 1.0f, timeStep);
            return (vel + dv, true, true);
        }
        else
        {
            float diff = hoverHeight - groundHeight;
            if (Math.Sign(vel) != Math.Sign(diff))
            {
                // Halcyon's branch is kept as written, although damping is 1 here and the second arm never runs
#pragma warning disable CS0162
                if (damping > 0.75f) dv += -vel;
                else dv += -vel * timeStep;
#pragma warning restore CS0162
            }
            dv += diff * (1.0f / hoverTau) * timeStep;
            dv += DoHoverDamping(vel, damping, timeStep);
            return (vel + dv, true, true);
        }
    }

    private static float DoHoverDamping(float vel, float hoverDamping, float timeStep)
    {
        if (vel != 0)
        {
            if (Math.Abs(vel) <= (0.01f * hoverDamping))
                return -vel * hoverDamping;
            return -vel * timeStep * 2.0f * hoverDamping;
        }
        return 0f;
    }

    /// <summary>
    /// The height above the ground of an object under the code above in steps of <paramref name="h"/>, every <paramref name="h"/>
    /// seconds from t = 0 to <paramref name="total"/>: Halcyon's velocity change at the start of the step, then the engine's
    /// gravity (when the hover has not turned it off) and its move by the new velocity.
    /// </summary>
    public static List<(double t, double z)> HoverPath(double h, double total, float tau, float height, bool repel, bool gravity, float z0, float g = 9.80665f)
    {
        var path = new List<(double, double)> { (0, z0) };
        double z = z0, v = 0;
        bool gravityOn = gravity;
        int steps = (int)Math.Round(total / h);
        for (int i = 1; i <= steps; i++)
        {
            (float vz, bool off, bool acts) = Hover((float)z, height, tau, repel, (float)v, (float)h);
            if (acts)
            {
                v = vz;
                gravityOn = gravity && !off;
            }
            else
                gravityOn = gravity;
            if (gravityOn)
                v -= g * h;
            z += v * h;
            path.Add((i * h, z));
        }
        return path;
    }

    /// <summary>The angle (rad) an object has turned toward a target <paramref name="total"/> radians away, every h seconds, under the look-at code above.</summary>
    public static List<(double t, double angle)> LookAtPath(double h, double seconds, double total, float strength, float damping)
    {
        var path = new List<(double, double)> { (0, 0) };
        double turned = 0, w = 0;
        int steps = (int)Math.Round(seconds / h);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 next = RotLook(new Vector3(0f, 0f, (float)(total - turned)), new Vector3(0f, 0f, (float)w), strength, damping, (float)h);
            w = next.Z;
            turned += w * h;
            path.Add((i * h, turned));
        }
        return path;
    }

    /// <summary>The value of a path at t, between its points linearly.</summary>
    public static double At(List<(double t, double v)> path, double t)
    {
        for (int i = 1; i < path.Count; i++)
            if (path[i].t >= t)
            {
                double f = (t - path[i - 1].t) / (path[i].t - path[i - 1].t);
                return path[i - 1].v + f * (path[i].v - path[i - 1].v);
            }
        return path[^1].v;
    }

    /// <summary>DoRotLookTarget with the torque turned into the angular velocity change it makes: the inertia cancels.</summary>
    public static Vector3 RotLook(Vector3 turn, Vector3 currAngVel, float strength, float damping, float timeStep)
    {
        strength = Math.Clamp(strength, 0.1f, 1.0f);
        damping = Math.Clamp(damping, 0.1f, 100.0f);
        if (turn.Length() <= (float)Math.PI / 180.0f)
            return Vector3.Zero;
        Vector3 targetAngVel = (turn / damping) * 100.0f;
        Vector3 velDamping = -currAngVel * strength * 100.0f;
        return currAngVel + targetAngVel * timeStep + velDamping * timeStep;
    }
}
