/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// llMoveToTarget and llSetHoverHeight on a physical prim or linkset, through the harness's per-heartbeat step at the
/// 11 Hz heartbeat, with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat: what reaches the root prim's
/// PhysicsActor from them (PIDTarget, PIDTau, PIDActive; PIDHoverHeight, PIDHoverType, PIDHoverTau, PIDHoverActive), set
/// as SceneObjectGroup.MoveToTarget and SetHoverHeight set them. The Second Life wiki says each "critically damps" to
/// its target "in tau seconds"; the rule tested is the critically damped spring with tau as its timescale, from rest
/// e(t) = e0 (1 + t / tau) e^(-t / tau), which never passes its target. These are the closed-form laws of the Second
/// Life page, so every run pins [Jolt] BasicForcesBehaviour to SecondLife (the shipped default is InWorldz). Serial with the other native tests: every run
/// steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class MoveToTargetAndHoverTests
{
    private const float G = 9.80665f;
    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private static readonly Vector3 High = new(128f, 128f, 150f);

    /// <summary>The prim's state before a heartbeat, at physics time T.</summary>
    private readonly record struct Point(double T, Vector3 Position, Vector3 Velocity, Vector3 Spin, float Ground, int Awake);

    /// <summary>
    /// Runs <paramref name="setup"/>'s scene for <paramref name="seconds"/> over <paramref name="world"/> (the harness
    /// course by default: level ground at 25 m, water at 20 m). <paramref name="act"/> runs before every heartbeat, as a
    /// script would between heartbeats, with the physics time stepped so far.
    /// </summary>
    private static List<Point> Run(double physicsHz, float seconds, Action<Run> setup, Action<Run, double> act = null,
                                   Func<float, (float[] heights, float water)> world = null)
    {
        var trace = new List<Point>();
        var sc = new Scenario
        {
            Name = "move-to-target",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r =>
            {
                PhysicsActor a = r.Actor;
                Vector3 p = a.Position;
                trace.Add(new Point(r.PhysicsTime, p, a.Velocity, a.RotationalVelocity, r.GroundAt(p.X, p.Y), r.AwakeBodies));
                act?.Invoke(r, r.PhysicsTime);
            },
        };
        if (world != null)
            sc.World = world;
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz, Jolt = { ["PrimLinearDamping"] = "0.05", ["PrimAngularDamping"] = "0.05", ["BasicForcesBehaviour"] = "SecondLife" } });
        return trace;
    }

    private static Point At(List<Point> trace, double t) => trace.First(p => p.T >= t - 1e-9);

    // As SceneObjectGroup.MoveToTarget sets them for tau > 0, and StopMoveToTarget.
    private static void MoveToTarget(PhysicsActor a, Vector3 target, float tau)
    {
        a.PIDTarget = target;
        a.PIDTau = tau;
        a.PIDActive = true;
    }

    // As SceneObjectGroup.SetHoverHeight sets them for a height that is not zero.
    private static void Hover(PhysicsActor a, float height, PIDHoverType type, float tau)
    {
        a.PIDHoverHeight = height;
        a.PIDHoverType = type;
        a.PIDHoverTau = tau;
        a.PIDHoverActive = true;
    }

    // The distance left, from rest at distance d0: d0 (1 + x) e^(-x), x = t / tau.
    private static double Left(double d0, double tau, double t) => d0 * (1 + t / tau) * Math.Exp(-t / tau);

    // When the rule's distance from d0 drops under `left`.
    private static double ArrivalTime(double d0, double tau, double left)
    {
        double lo = 0, hi = 100 * tau;
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Left(d0, tau, mid) > left) lo = mid; else hi = mid;
        }
        return hi;
    }

    // ---- llMoveToTarget ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0, 0.3f)]
    [InlineData(45.0, 1f)]
    [InlineData(45.0, 2f)]
    [InlineData(0.0, 0.3f)]
    [InlineData(0.0, 1f)]
    [InlineData(0.0, 2f)]
    public void Move_to_target_follows_the_critically_damped_rule_arrives_and_holds(double physicsHz, float tau)
    {
        const float d0 = 10f;
        Vector3 target = High + new Vector3(d0, 0f, 0f);
        double arrive = ArrivalTime(d0, tau, 0.005);
        float seconds = (float)(arrive + 10.5);
        List<Point> trace = Run(physicsHz, seconds,
            r => { r.AddBox(Unit, High, Quaternion.Identity); MoveToTarget(r.Actor, target, tau); });

        // The distance left at one, two and four tau: within 10 percent of the rule at the sample's own time.
        foreach (double x in new[] { 1.0, 2.0, 4.0 })
        {
            Point p = At(trace, x * tau);
            double expected = Left(d0, tau, p.T);
            double left = (target - p.Position).Length();
            Assert.True(Math.Abs(left - expected) <= 0.1 * expected,
                $"tau {tau}: at t {p.T:0.000} the distance left is {left:0.0000}, the rule says {expected:0.0000}");
        }

        // It never passes the target (the rule from rest does not), and it stays on the line to it.
        foreach (Point p in trace)
        {
            Assert.True(p.Position.X <= target.X + 0.01f, $"overshot to x {p.Position.X} at t {p.T:0.000}");
            Assert.InRange(p.Position.Y, High.Y - 0.01f, High.Y + 0.01f);
            Assert.InRange(p.Position.Z, High.Z - 0.02f, High.Z + 0.01f);
        }

        // Arrived by the rule's time (to 1 cm, with a step's slack), then held within 1 cm for 10 s.
        Point arrived = At(trace, arrive + 0.2);
        Assert.True((target - arrived.Position).Length() < 0.01f, $"not arrived at t {arrived.T:0.00}: {arrived.Position}");
        foreach (Point p in trace.Where(p => p.T >= arrived.T && p.T <= arrived.T + 10.0))
            Assert.True((target - p.Position).Length() < 0.01f, $"left the target at t {p.T:0.00}: {p.Position}");
        Assert.True(trace[^1].T >= arrived.T + 10.0 - 0.1, "the run did not cover 10 s of holding");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Stop_move_to_target_lets_it_fall_from_where_it_was_held(double physicsHz)
    {
        Vector3 target = High + new Vector3(0f, 0f, 1f);
        double stoppedAt = double.NaN;
        Vector3 heldAt = Vector3.Zero;
        List<Point> trace = Run(physicsHz, 6f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); MoveToTarget(r.Actor, target, 0.3f); },
            (r, now) =>
            {
                if (now >= 4.0 && double.IsNaN(stoppedAt))
                {
                    heldAt = r.Actor.Position;
                    r.Actor.PIDActive = false;   // SceneObjectGroup.StopMoveToTarget
                    stoppedAt = now;
                }
            });

        Assert.True((target - heldAt).Length() < 0.01f, $"not held at the target before the stop: {heldAt}");
        Point a = At(trace, stoppedAt + 0.1), b = At(trace, stoppedAt + 0.4);
        float accel = (b.Velocity.Z - a.Velocity.Z) / (float)(b.T - a.T);
        Assert.InRange(accel, -G * 1.03f, -G * 0.95f);
        // It fell from where it was held: from rest there, z = z0 - g t^2 / 2 (a step's slack).
        double t = b.T - stoppedAt;
        Assert.InRange(b.Position.Z, heldAt.Z - 0.5f * G * (float)(t * t) - 0.15f, heldAt.Z - 0.5f * G * (float)(t * t) + 0.15f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_target_65_m_or_more_away_does_nothing(double physicsHz)
    {
        // "must be less than 65, or no movement will occur."
        List<Point> trace = Run(physicsHz, 1.5f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); MoveToTarget(r.Actor, High + new Vector3(65f, 0f, 0f), 1f); });
        Point end = trace[^1];
        Assert.InRange(end.Position.X, High.X - 0.01f, High.X + 0.01f);
        Assert.True(end.Velocity.Z < -0.9f * G * (float)end.T, $"it should fall freely (vz {end.Velocity.Z} at t {end.T:0.00})");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_target_just_inside_65_m_is_reached(double physicsHz)
    {
        Vector3 target = High + new Vector3(64f, 0f, 0f);
        List<Point> trace = Run(physicsHz, 8f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); MoveToTarget(r.Actor, target, 0.5f); });
        Assert.True((target - trace[^1].Position).Length() < 0.01f, $"ended at {trace[^1].Position}");
    }

    // ---- llSetHoverHeight ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Hover_over_flat_ground_holds_the_height(double physicsHz)
    {
        const float height = 2f;
        List<Point> trace = Run(physicsHz, 8f,
            r => { r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 0.6f), Quaternion.Identity); Hover(r.Actor, height, PIDHoverType.Ground, 0.4f); });
        foreach (Point p in trace.Where(p => p.T >= 4.0))
            Assert.InRange(p.Position.Z - p.Ground, height - 0.02f, height + 0.02f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Hover_over_a_slope_holds_the_height_while_pushed_along_it(double physicsHz)
    {
        const float height = 2f, slope = 10f;
        List<Point> trace = Run(physicsHz, 12f,
            r =>
            {
                float y = 45f;
                r.AddBox(Unit, new Vector3(128f, y, Course.HeightAt(y, slope) + height), Quaternion.Identity);
                Hover(r.Actor, height, PIDHoverType.Ground, 0.4f);
            },
            (r, now) =>
            {
                if (r.Heartbeats == 22)
                    r.Actor.AddForce(new Vector3(0f, 30f, 0f), false);   // an impulse up the slope: 3 m/s for the 10 kg cube
            },
            world: s => (Course.Heightmap(slope), Course.Water));

        Point last = trace[^1];
        Assert.True(last.Position.Y > 65f, $"it should have moved up the slope (y {last.Position.Y})");
        Assert.True(last.Ground > Course.Ground + 4f, $"it should be over higher ground (ground {last.Ground})");
        // The push starts the ground rising under it at u tan(slope): from its height, the spring's error peaks at
        // u tan(slope) tau / e (e(t) = v0 t e^(-t / tau)). The push acts inside the step after it is given, so it is a step
        // late; allow 40 percent over the peak for that.
        float peak = 3f * MathF.Tan(slope * MathF.PI / 180f) * 0.4f / MathF.E;
        float transient = trace.Where(p => p.T >= 2.0 && p.T < 3.5).Max(p => MathF.Abs(p.Position.Z - p.Ground - height));
        Assert.True(transient <= 1.4f * peak, $"the push moved it {transient:0.000} m off its height; the spring's peak is {peak:0.000} m");
        // Settled before the push and again after it, all the way up the slope.
        foreach (Point p in trace.Where(p => (p.T >= 1.5 && p.T < 2.0) || (p.T >= 3.5 && p.Position.Y < Course.RampTopY - 2f)))
            Assert.True(MathF.Abs(p.Position.Z - p.Ground - height) <= 0.02f,
                $"at t {p.T:0.00}, y {p.Position.Y:0.00}: {p.Position.Z - p.Ground:0.0000} m above the ground");
    }

    [Theory]
    [InlineData(45.0, PIDHoverType.GroundAndWater, 21.5f)]   // llSetHoverHeight water TRUE (YEngine): the water is higher
    [InlineData(45.0, PIDHoverType.Water, 21.5f)]            // water TRUE as Phlox passes it
    [InlineData(45.0, PIDHoverType.Ground, 11.5f)]           // water FALSE: "ignore water like it isn't there"
    [InlineData(0.0, PIDHoverType.GroundAndWater, 21.5f)]
    [InlineData(0.0, PIDHoverType.Water, 21.5f)]
    [InlineData(0.0, PIDHoverType.Ground, 11.5f)]
    public void Hover_over_water_holds_above_the_water_when_asked(double physicsHz, PIDHoverType type, float expectedZ)
    {
        // Ground at 10 m under water at 20 m.
        List<Point> trace = Run(physicsHz, 10f,
            r => { r.AddBox(Unit, new Vector3(128f, 128f, 18f), Quaternion.Identity); Hover(r.Actor, 1.5f, type, 0.4f); },
            world: _ => (Course.Heightmap(0f, 10f), Course.Water));
        foreach (Point p in trace.Where(p => p.T >= 6.0))
            Assert.InRange(p.Position.Z, expectedZ - 0.02f, expectedZ + 0.02f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_negative_height_does_not_take_it_under_the_ground(double physicsHz)
    {
        // "Unless volume detect is enabled, negative height values when water is FALSE will not move the object below
        // the ground level". The cube rests on the ground.
        List<Point> trace = Run(physicsHz, 5f,
            r => { r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 2f), Quaternion.Identity); Hover(r.Actor, -3f, PIDHoverType.Ground, 0.4f); });
        foreach (Point p in trace.Where(p => p.T >= 3.0))
            Assert.InRange(p.Position.Z - p.Ground, 0.45f, 0.55f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Stop_hover_lets_it_fall(double physicsHz)
    {
        double stoppedAt = double.NaN;
        List<Point> trace = Run(physicsHz, 5f,
            r => { r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 5f), Quaternion.Identity); Hover(r.Actor, 5f, PIDHoverType.Ground, 0.3f); },
            (r, now) =>
            {
                if (now >= 3.0 && double.IsNaN(stoppedAt))
                {
                    r.Actor.PIDHoverActive = false;   // llStopHover: SetHoverHeight(0, ...) turns it off
                    stoppedAt = now;
                }
            });
        Point held = At(trace, stoppedAt);
        Assert.InRange(held.Position.Z - held.Ground, 4.98f, 5.02f);
        Point a = At(trace, stoppedAt + 0.1), b = At(trace, stoppedAt + 0.4);
        float accel = (b.Velocity.Z - a.Velocity.Z) / (float)(b.T - a.T);
        Assert.InRange(accel, -G * 1.03f, -G * 0.95f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_height_of_zero_stops_hover(double physicsHz)
    {
        // A height of zero has the same effect as llStopHover (wiki.secondlife.com/wiki/LlSetHoverHeight).
        List<Point> trace = Run(physicsHz, 3f,
            r => { r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 5f), Quaternion.Identity); Hover(r.Actor, 5f, PIDHoverType.Ground, 0.3f); },
            (r, now) => { if (r.Heartbeats == 11) r.Actor.PIDHoverHeight = 0f; });
        Assert.False(trace.Count == 0);
        Point a = At(trace, 1.1), b = At(trace, 1.4);
        Assert.InRange((b.Velocity.Z - a.Velocity.Z) / (float)(b.T - a.T), -G * 1.03f, -G * 0.95f);
    }

    // ---- both, together with the forces ----------------------------------------------------------------------------

    public enum Call { MoveToTarget, Hover, StopMoveToTarget, StopHover }

    [Theory]
    [InlineData(45.0, Call.MoveToTarget)]
    [InlineData(45.0, Call.Hover)]
    [InlineData(0.0, Call.MoveToTarget)]
    [InlineData(0.0, Call.Hover)]
    public void A_sleeping_object_wakes_for_the_call(double physicsHz, Call call)
    {
        int calledAt = -1;
        float groundZ = 0f;
        List<Point> trace = Run(physicsHz, 8f,
            r => r.AddBoxOnGround(Unit, 128f, 128f, Quaternion.Identity),
            (r, now) =>
            {
                if (calledAt >= 0 || r.Heartbeats < 5 || r.AwakeBodies != 0)
                    return;
                calledAt = r.Heartbeats;
                groundZ = r.Actor.Position.Z;
                if (call == Call.MoveToTarget)
                    MoveToTarget(r.Actor, r.Actor.Position + new Vector3(0f, 0f, 2f), 0.3f);
                else
                    Hover(r.Actor, 2.5f, PIDHoverType.Ground, 0.3f);
            });

        Assert.True(calledAt > 0, "the box never went to sleep");
        Assert.Equal(0, trace[calledAt].Awake);
        Assert.True(trace[calledAt + 1].Awake > 0, "the call did not wake it");
        float expectedZ = call == Call.MoveToTarget ? groundZ + 2f : Course.Ground + 2.5f;
        Assert.InRange(trace[^1].Position.Z, expectedZ - 0.01f, expectedZ + 0.01f);
    }

    [Theory]
    [InlineData(45.0, Call.StopMoveToTarget)]
    [InlineData(45.0, Call.StopHover)]
    [InlineData(0.0, Call.StopMoveToTarget)]
    [InlineData(0.0, Call.StopHover)]
    public void A_held_object_that_went_to_sleep_falls_when_let_go(double physicsHz, Call call)
    {
        int stoppedAt = -1;
        List<Point> trace = Run(physicsHz, 20f,
            r =>
            {
                r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 3f), Quaternion.Identity);
                if (call == Call.StopMoveToTarget)
                    MoveToTarget(r.Actor, new Vector3(128f, 128f, Course.Ground + 3f), 0.3f);
                else
                    Hover(r.Actor, 3f, PIDHoverType.Ground, 0.3f);
            },
            (r, now) =>
            {
                if (stoppedAt >= 0 || r.Heartbeats < 5 || (r.AwakeBodies != 0 && now < 15.0))
                    return;
                stoppedAt = r.Heartbeats;
                if (call == Call.StopMoveToTarget) r.Actor.PIDActive = false;
                else r.Actor.PIDHoverActive = false;
            });

        Assert.True(stoppedAt > 0);
        Assert.InRange(trace[stoppedAt].Position.Z, Course.Ground + 2.99f, Course.Ground + 3.01f);
        Assert.True(trace[stoppedAt + 2].Awake > 0, "the stop did not wake it");
        Assert.True(trace[stoppedAt + 5].Position.Z < Course.Ground + 2.8f, $"it did not fall (z {trace[stoppedAt + 5].Position.Z})");
    }

    [Theory]
    [InlineData(45.0, false)]
    [InlineData(45.0, true)]
    [InlineData(0.0, false)]
    [InlineData(0.0, true)]
    public void A_three_prim_linkset_moves_as_one_object(double physicsHz, bool callOnChild)
    {
        const float tau = 0.5f;
        Vector3 target = High + new Vector3(0f, 6f, 0f);
        PhysicsActor child = null, child2 = null;
        List<Point> trace = Run(physicsHz, 8f,
            r =>
            {
                r.AddBox(Unit, High, Quaternion.Identity);
                child = r.AddChildBox(Unit, High + new Vector3(1f, 0f, 0f), Quaternion.Identity);
                child2 = r.AddChildBox(Unit, High + new Vector3(-1f, 0f, 0f), Quaternion.Identity, 1003);
            },
            (r, now) =>
            {
                if (r.Heartbeats == 3)
                    MoveToTarget(callOnChild ? child : r.Actor, target, tau);
            });

        Point start = trace[3];
        Point p = At(trace, start.T + 2 * tau);
        double expected = Left(6, tau, p.T - start.T);
        double left = (target - p.Position).Length();
        Assert.True(Math.Abs(left - expected) <= 0.1 * expected, $"at {p.T - start.T:0.000} s the distance left is {left:0.000}, the rule says {expected:0.000}");
        Assert.True((target - trace[^1].Position).Length() < 0.01f, $"the root ended at {trace[^1].Position}");
        // Every prim of the linkset reports the root's target as its own.
        Assert.True(child.PIDActive && child2.PIDActive, "the children should report the root's target");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_linkset_hovers_as_one_object(double physicsHz)
    {
        List<Point> trace = Run(physicsHz, 6f,
            r =>
            {
                r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 1f), Quaternion.Identity);
                r.AddChildBox(Unit, new Vector3(129f, 128f, Course.Ground + 1f), Quaternion.Identity);
                r.AddChildBox(Unit, new Vector3(127f, 128f, Course.Ground + 1f), Quaternion.Identity, 1003);
                Hover(r.Actor, 3f, PIDHoverType.Ground, 0.4f);
            });
        foreach (Point p in trace.Where(p => p.T >= 4.0))
            Assert.InRange(p.Position.Z - p.Ground, 2.98f, 3.02f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Buoyancy_changes_nothing_while_held(double physicsHz)
    {
        Vector3 target = High + new Vector3(2f, 0f, 0f);
        List<Point> trace = Run(physicsHz, 5f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.Buoyancy = 3f; MoveToTarget(r.Actor, target, 0.3f); },
            (r, now) => { if (r.Heartbeats == 30) r.Actor.Buoyancy = -2f; });
        foreach (Point p in trace.Where(p => p.T >= 2.5))
            Assert.True((target - p.Position).Length() < 0.01f, $"at t {p.T:0.00}: {p.Position}");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_set_torque_still_turns_a_held_object(double physicsHz)
    {
        Vector3 target = High + new Vector3(0f, 0f, 1f);
        List<Point> trace = Run(physicsHz, 4f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.Torque = new Vector3(0f, 0f, 2f); MoveToTarget(r.Actor, target, 0.3f); });
        Point end = trace[^1];
        Assert.True(end.Spin.Z > 1f, $"it should spin (spin {end.Spin})");
        Assert.True((target - end.Position).Length() < 0.02f, $"it should stay at its target: {end.Position}");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_set_force_holds_it_force_tau_squared_over_mass_from_its_target(double physicsHz)
    {
        // The spring balances a steady force F where e / tau^2 = F / m: e = F tau^2 / m.
        const float tau = 1f, force = 5f;
        Vector3 target = High;
        float mass = 0f;
        List<Point> trace = Run(physicsHz, 14f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.Force = new Vector3(force, 0f, 0f); MoveToTarget(r.Actor, target, tau); },
            (r, now) => mass = r.Actor.Mass);
        float expected = force * tau * tau / mass;
        Point end = trace[^1];
        float offset = end.Position.X - target.X;
        Assert.True(offset >= 0.9f * expected && offset <= 1.1f * expected, $"held {offset:0.0000} m from its target, the rule says {expected:0.0000}");
        Assert.InRange(end.Position.Z, target.Z - 0.01f, target.Z + 0.01f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_vehicle_keeps_its_own_hover_and_motion(double physicsHz)
    {
        List<Point> Fly(bool withTargets) => Run(physicsHz, 4f,
            r =>
            {
                r.AddBox(new Vector3(2f, 1f, 0.5f), new Vector3(128f, 128f, Course.Ground + 2f), Quaternion.Identity);
                r.Actor.VehicleType = (int)Vehicle.TYPE_BALLOON;
                r.Actor.VehicleFloatParam((int)Vehicle.HOVER_HEIGHT, 4f);
                r.Actor.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(2f, 0f, 0f));
                if (withTargets)
                {
                    MoveToTarget(r.Actor, new Vector3(100f, 100f, 60f), 0.5f);
                    Hover(r.Actor, 10f, PIDHoverType.Ground, 0.5f);
                }
            });

        List<Point> plain = Fly(false), targeted = Fly(true);
        Assert.Equal(plain.Count, targeted.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].Position, targeted[i].Position);
            Assert.Equal(plain[i].Velocity, targeted[i].Velocity);
        }
    }

    // ---- what is refused, and a non-physical object ----------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Non_finite_values_are_refused_and_keep_the_last_good_one(double physicsHz)
    {
        Vector3 target = High + new Vector3(3f, 0f, 0f);
        List<Point> trace = Run(physicsHz, 5f,
            r =>
            {
                r.AddBox(Unit, High, Quaternion.Identity);
                MoveToTarget(r.Actor, target, 0.3f);
                r.Actor.PIDTarget = new Vector3(float.NaN, 0f, 0f);
                r.Actor.PIDTau = float.PositiveInfinity;
                r.Actor.PIDHoverHeight = float.NaN;
                r.Actor.PIDHoverTau = float.NegativeInfinity;
            });
        foreach (Point p in trace)
            Assert.True(float.IsFinite(p.Position.Length()) && float.IsFinite(p.Velocity.Length()), $"{p.Position} {p.Velocity}");
        Assert.True((target - trace[^1].Position).Length() < 0.01f, $"ended at {trace[^1].Position}");
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_tau_of_zero_or_less_does_nothing(double physicsHz)
    {
        // A call with a tau of 0.0 or less silently fails and does nothing (wiki.secondlife.com/wiki/LlMoveToTarget). The hover's is refused
        // the same way.
        foreach (bool hover in new[] { false, true })
        {
            List<Point> trace = Run(physicsHz, 1f,
                r =>
                {
                    r.AddBox(Unit, High, Quaternion.Identity);
                    if (hover) Hover(r.Actor, 2f, PIDHoverType.Absolute, -1f);
                    else MoveToTarget(r.Actor, High + new Vector3(5f, 0f, 0f), 0f);
                });
            Point end = trace[^1];
            Assert.InRange(end.Position.X, High.X - 0.01f, High.X + 0.01f);
            Assert.True(end.Velocity.Z < -0.9f * G * (float)end.T, $"it should fall freely (vz {end.Velocity.Z})");
        }
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_tau_below_two_physics_frames_acts_as_two_frames(double physicsHz)
    {
        // "The smallest functional tau is 0.044444444 (two physics frames, 2/45)."
        Vector3 target = High + new Vector3(1f, 0f, 0f);
        List<Point> tiny = Run(physicsHz, 1f, r => { r.AddBox(Unit, High, Quaternion.Identity); MoveToTarget(r.Actor, target, 0.001f); });
        List<Point> min = Run(physicsHz, 1f, r => { r.AddBox(Unit, High, Quaternion.Identity); MoveToTarget(r.Actor, target, JoltPrim.MinTau); });
        Assert.Equal(min.Count, tiny.Count);
        for (int i = 0; i < min.Count; i++)
            Assert.Equal(min[i].Position, tiny[i].Position);
        Assert.True((target - tiny[^1].Position).Length() < 0.01f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_non_physical_object_keeps_the_target_until_it_is_physical(double physicsHz)
    {
        // llMoveToTarget works on attachments and physical objects only, yet a call seems to persist after physics is
        // turned off (wiki.secondlife.com/wiki/LlMoveToTarget).
        Vector3 target = High + new Vector3(2f, 0f, 0f);
        Vector3 whileStatic = Vector3.Zero;
        List<Point> trace = Run(physicsHz, 4f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.IsPhysical = false; MoveToTarget(r.Actor, target, 0.2f); },
            (r, now) =>
            {
                if (r.Heartbeats == 11)
                {
                    whileStatic = r.Actor.Position;
                    r.Actor.IsPhysical = true;
                }
            });
        Assert.Equal(High, whileStatic);
        Assert.True((target - trace[^1].Position).Length() < 0.01f, $"ended at {trace[^1].Position}");
    }

    // ---- in the build tool, and with the gravity multiplier ------------------------------------------------------------

    // How far the controller still has to take the object at point p: to the target, or to the hover height over the
    // ground under it.
    private static float LeftAt(Call call, Point p, Vector3 target, float height)
        => call == Call.MoveToTarget ? (target - p.Position).Length() : MathF.Abs(p.Position.Z - (p.Ground + height));

    // An object selected while it is on its way stops where it is and stays there: the build tool's hold wins over both
    // controllers, as ubODE's Move skips a selected prim. The request is kept, and let go, the object goes on to its
    // target from rest where it was held, by the same rule.
    [Theory]
    [InlineData(45.0, Call.MoveToTarget)]
    [InlineData(45.0, Call.Hover)]
    [InlineData(0.0, Call.MoveToTarget)]
    [InlineData(0.0, Call.Hover)]
    public void A_selected_object_ignores_its_target_and_takes_it_up_again_when_let_go(double physicsHz, Call call)
    {
        const double SelectAt = 0.3, ReleaseAt = 2.3;
        const float tau = 0.5f, height = 4f;
        Vector3 start = call == Call.MoveToTarget ? High : new Vector3(128f, 128f, Course.Ground + 0.6f);
        Vector3 target = High + new Vector3(6f, 0f, 0f);
        double selectedT = double.NaN, releasedT = double.NaN;
        Vector3 heldAt = Vector3.Zero;
        var held = new List<Point>();
        List<Point> trace = Run(physicsHz, 8f,
            r =>
            {
                r.AddBox(Unit, start, Quaternion.Identity);
                if (call == Call.MoveToTarget) MoveToTarget(r.Actor, target, tau);
                else Hover(r.Actor, height, PIDHoverType.Ground, tau);
            },
            (r, now) =>
            {
                PhysicsActor a = r.Actor;
                if (double.IsNaN(selectedT))
                {
                    if (now >= SelectAt)
                    {
                        a.Selected = true;
                        selectedT = now;
                        heldAt = a.Position;
                    }
                    return;
                }
                if (!double.IsNaN(releasedT))
                    return;
                held.Add(new Point(now, a.Position, a.Velocity, a.RotationalVelocity, r.GroundAt(a.Position.X, a.Position.Y), r.AwakeBodies));
                if (now >= ReleaseAt)
                {
                    a.Selected = false;
                    releasedT = now;
                }
            });

        Assert.False(double.IsNaN(releasedT), "the run never let the object go");
        Assert.True(Vector3.Distance(heldAt, start) > 0.5f, $"it had not started moving when selected ({heldAt})");
        Assert.True(held.Count > 15, $"too few held samples ({held.Count})");
        foreach (Point p in held)
        {
            Assert.True(Vector3.Distance(p.Position, heldAt) < 0.001f, $"moved while selected at t={p.T:0.00}: {p.Position} (held at {heldAt})");
            Assert.True(p.Velocity.Length() < 1e-4f, $"reported moving while selected at t={p.T:0.00}: {p.Velocity}");
        }

        // Let go: from rest where it was held, the distance left at one and two tau is within 10 percent of the rule.
        double d0 = LeftAt(call, At(trace, releasedT), target, height);
        Assert.True(d0 > 0.5, $"held too close to the target to test ({d0:0.000} m)");
        foreach (double x in new[] { 1.0, 2.0 })
        {
            Point p = At(trace, releasedT + x * tau);
            double expected = Left(d0, tau, p.T - releasedT);
            double left = LeftAt(call, p, target, height);
            Assert.True(Math.Abs(left - expected) <= 0.1 * expected,
                $"at {p.T - releasedT:0.000} s after release the distance left is {left:0.0000}, the rule says {expected:0.0000}");
        }
        Assert.True(LeftAt(call, trace[^1], target, height) < 0.02f, $"did not arrive: {trace[^1].Position}");
    }

    // A target or hover stopped while the object is selected: let go, it falls from where it was held.
    [Theory]
    [InlineData(45.0, Call.StopMoveToTarget)]
    [InlineData(45.0, Call.StopHover)]
    [InlineData(0.0, Call.StopMoveToTarget)]
    [InlineData(0.0, Call.StopHover)]
    public void A_target_stopped_while_selected_lets_it_fall_when_let_go(double physicsHz, Call call)
    {
        double releasedT = double.NaN;
        Vector3 heldAt = Vector3.Zero, beforeRelease = Vector3.Zero;
        List<Point> trace = Run(physicsHz, 4.5f,
            r =>
            {
                r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 3f), Quaternion.Identity);
                if (call == Call.StopMoveToTarget)
                    MoveToTarget(r.Actor, new Vector3(128f, 128f, Course.Ground + 3f), 0.3f);
                else
                    Hover(r.Actor, 3f, PIDHoverType.Ground, 0.3f);
            },
            (r, now) =>
            {
                PhysicsActor a = r.Actor;
                if (r.Heartbeats == 22)
                {
                    a.Selected = true;
                    heldAt = a.Position;
                }
                if (r.Heartbeats == 28)
                {
                    if (call == Call.StopMoveToTarget) a.PIDActive = false;
                    else a.PIDHoverActive = false;
                }
                if (r.Heartbeats == 33)
                {
                    beforeRelease = a.Position;
                    a.Selected = false;
                    releasedT = now;
                }
            });

        Assert.InRange(heldAt.Z, Course.Ground + 2.99f, Course.Ground + 3.01f);
        Assert.True(Vector3.Distance(beforeRelease, heldAt) < 0.001f, $"moved while selected: {beforeRelease} (held at {heldAt})");
        Point a = At(trace, releasedT + 0.1), b = At(trace, releasedT + 0.4);
        float accel = (b.Velocity.Z - a.Velocity.Z) / (float)(b.T - a.T);
        Assert.InRange(accel, -G * 1.03f, -G * 0.95f);
    }

    // llSetPhysicsMaterial GRAVITY_MULTIPLIER does not change either controller: gravity is off while one acts, so the
    // object arrives by the rule and holds there; stopped, it falls (or rises) by the multiplier's gravity.
    [Theory]
    [InlineData(45.0, Call.MoveToTarget, 3f)]
    [InlineData(45.0, Call.Hover, 3f)]
    [InlineData(45.0, Call.MoveToTarget, -1f)]
    [InlineData(45.0, Call.Hover, -1f)]
    [InlineData(0.0, Call.MoveToTarget, 3f)]
    [InlineData(0.0, Call.Hover, 3f)]
    [InlineData(0.0, Call.MoveToTarget, -1f)]
    [InlineData(0.0, Call.Hover, -1f)]
    public void Move_to_target_and_hover_work_with_the_gravity_multiplier_set(double physicsHz, Call call, float multiplier)
    {
        const float tau = 0.3f, height = 8f;   // high enough that 0.4 s of falling at 3 g stays clear of the ground
        const double StopAt = 4.0;
        Vector3 start = call == Call.MoveToTarget ? High : new Vector3(128f, 128f, Course.Ground + 0.6f);
        Vector3 target = High + new Vector3(2f, 0f, 1f);
        double stoppedAt = double.NaN;
        List<Point> trace = Run(physicsHz, 4.6f,
            r =>
            {
                r.AddBox(Unit, start, Quaternion.Identity);
                r.Actor.GravModifier = multiplier;
                if (call == Call.MoveToTarget) MoveToTarget(r.Actor, target, tau);
                else Hover(r.Actor, height, PIDHoverType.Ground, tau);
            },
            (r, now) =>
            {
                if (now >= StopAt && double.IsNaN(stoppedAt))
                {
                    if (call == Call.MoveToTarget) r.Actor.PIDActive = false;
                    else r.Actor.PIDHoverActive = false;
                    stoppedAt = now;
                }
            });

        if (call == Call.MoveToTarget)
        {
            double d0 = (target - start).Length();
            Point p = At(trace, 2 * tau);
            double expected = Left(d0, tau, p.T);
            double left = (target - p.Position).Length();
            Assert.True(Math.Abs(left - expected) <= 0.1 * expected, $"at t {p.T:0.000} the distance left is {left:0.0000}, the rule says {expected:0.0000}");
        }
        foreach (Point p in trace.Where(p => p.T >= 2.5 && p.T < StopAt))
            Assert.True(LeftAt(call, p, target, height) < 0.02f, $"not held at t {p.T:0.00}: {p.Position}");

        Point a = At(trace, stoppedAt + 0.1), b = At(trace, stoppedAt + 0.4);
        float accel = (b.Velocity.Z - a.Velocity.Z) / (float)(b.T - a.T);
        float g = -G * multiplier;
        Assert.InRange(accel, MathF.Min(g * 0.95f, g * 1.03f), MathF.Max(g * 0.95f, g * 1.03f));
    }
}
