/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The rotation target of a physical prim or linkset (llRotLookAt, llLookAt): what reaches the root prim's PhysicsActor
/// as APIDTarget, APIDStrength, APIDDamping and APIDActive, through the harness's per-heartbeat step at the 11 Hz
/// heartbeat, with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat. The rule tested is a critically
/// damped spring on the rotation, timescale max(strength, min(damping, 1)) seconds, which turns the body by the short way
/// and never passes its target; see Docs/JoltPhysics.md. Each run starts from rest. Serial with the other native tests:
/// every run steps a real backend on the shared job pool.
/// Set ANGULAR_TARGET_OUT to a file to have the figures runs append their rows to it.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials(BasicForces = "SecondLife")]
public class AngularTargetTests
{
    protected readonly ITestOutputHelper _output;
    public AngularTargetTests(ITestOutputHelper output) => _output = output;

    protected static readonly Vector3 Unit = new(1f, 1f, 1f);
    protected static readonly Vector3 High = new(128f, 128f, 150f);
    private const float X = 128f, Y = 128f;
    private const uint RootId = 1601, LeftId = 1602, RightId = 1603;

    protected readonly record struct Pt(double T, Quaternion Rot, Vector3 Spin, Vector3 Pos, int Awake);

    protected static Quaternion Turn(float degrees, Vector3 axis) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);
    protected static Quaternion TurnZ(float degrees) => Turn(degrees, Vector3.UnitZ);

    /// <summary>The angle between two rotations (degrees).</summary>
    protected static float Between(Quaternion a, Quaternion b)
    {
        float d = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        return 2f * MathF.Acos(MathF.Min(1f, d)) * 180f / MathF.PI;
    }

    protected static List<Pt> Run(double physicsHz, float seconds, Action<Run> setup, Action<Run, double> act = null)
    {
        var trace = new List<Pt>();
        var sc = new Scenario
        {
            Name = "angular-target",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r =>
            {
                PhysicsActor a = r.Actor;
                trace.Add(new Pt(r.PhysicsTime, a.Orientation, a.RotationalVelocity, a.Position, r.AwakeBodies));
                act?.Invoke(r, r.PhysicsTime);
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz, Jolt = { ["PrimLinearDamping"] = "0.05", ["PrimAngularDamping"] = "0.05" } });
        return trace;
    }

    protected static Pt At(List<Pt> trace, double t) => trace.First(p => p.T >= t - 1e-9);

    // As a physics engine is handed a look-at: the target, then strength and damping, then on.
    protected static void LookAt(PhysicsActor a, Quaternion target, float strength, float damping)
    {
        a.APIDTarget = target;
        a.APIDStrength = strength;
        a.APIDDamping = damping;
        a.APIDActive = true;
    }

    /// <summary>The cube standing clear of the ground, its weight cancelled (buoyancy 1), or resting on the ground.</summary>
    protected static PhysicsActor Cube(Run r, float size, float mass, bool air)
    {
        float z = air ? 150f : r.GroundAt(X, Y) + size * 0.5f + 0.02f;
        PhysicsActor a = r.AddBox(new Vector3(size, size, size), new Vector3(X, Y, z), Quaternion.Identity);
        if (air)
            a.Buoyancy = 1f;
        a.Density = mass * 100f / (size * size * size);
        return a;
    }

    /// <summary>Three 1 m cubes in a row along x, linked; the root is the middle one.</summary>
    protected static PhysicsActor Linkset(Run r, bool air, out PhysicsActor left)
    {
        float z = air ? 150f : r.GroundAt(X, Y) + 0.5f + 0.02f;
        PhysicsActor root = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X, Y, z), Quaternion.Identity, true, RootId);
        if (air)
            root.Buoyancy = 1f;
        r.ActorSize = Unit;
        left = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X - 1f, Y, z), Quaternion.Identity, true, LeftId, root);
        r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, new Vector3(X + 1f, Y, z), Quaternion.Identity, true, RightId, root);
        return root;
    }

    // The error left from rest by the critically damped spring: e0 (1 + x) e^(-x), x = t / T.
    protected static double Left(double e0, double timescale, double t) => e0 * (1 + t / timescale) * Math.Exp(-t / timescale);

    /// <summary>The turn from the start, in degrees, at each sample (0 = not turned).</summary>
    protected static float[] Turned(List<Pt> trace) => trace.Select(p => Between(trace[0].Rot, p.Rot)).ToList().ToArray();

    // ---- the turn --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0, false)]
    [InlineData(45.0, true)]
    [InlineData(0.0, false)]
    [InlineData(0.0, true)]
    public virtual void It_turns_90_degrees_by_the_spring_rule_without_passing_the_target_in_the_air_and_on_the_ground(double physicsHz, bool air)
    {
        const float T = 0.2f;
        List<Pt> trace = Run(physicsHz, 4f, r => LookAt(Cube(r, 1f, 1f, air), TurnZ(90f), T, T));
        float[] turned = Turned(trace);

        // The error left at one, two and four timescales: within 3 degrees of the rule at the sample's own time.
        foreach (double x in new[] { 1.0, 2.0, 4.0 })
        {
            int i = trace.FindIndex(p => p.T >= x * T - 1e-9);
            double expected = Left(90, T, trace[i].T);
            float left = 90f - turned[i];
            Assert.True(Math.Abs(left - expected) <= 3.0, $"air {air}: at t {trace[i].T:0.000} {left:0.0} degrees are left, the rule says {expected:0.0}");
        }
        // Never past the target, and never turning back.
        for (int i = 0; i < turned.Length; i++)
        {
            Assert.True(turned[i] <= 90.5f, $"passed the target: {turned[i]:0.0} degrees at t {trace[i].T:0.00}");
            if (i > 0)
                Assert.True(turned[i] >= turned[i - 1] - 0.3f, $"turned back at t {trace[i].T:0.00}");
        }
        Pt end = At(trace, 1.6);
        Assert.True(Between(end.Rot, TurnZ(90f)) < 2f, $"air {air}: {Between(end.Rot, TurnZ(90f)):0.0} degrees from the target at t {end.T:0.00}");
        foreach (Pt p in trace.Where(p => p.T >= end.T))
            Assert.True(Between(p.Rot, TurnZ(90f)) < 2f, $"left the target at t {p.T:0.00}");
    }

    [Theory]
    [InlineData(45.0, 1f, 1f, true)]
    [InlineData(45.0, 1f, 100f, true)]
    [InlineData(45.0, 4f, 100f, true)]
    [InlineData(45.0, 1f, 1f, false)]
    [InlineData(45.0, 1f, 100f, false)]
    [InlineData(45.0, 4f, 100f, false)]
    [InlineData(0.0, 4f, 100f, false)]
    public virtual void It_reaches_the_target_within_2_degrees_the_same_for_any_mass_or_size(double physicsHz, float size, float mass, bool air)
    {
        List<Pt> reference = Run(physicsHz, 2.5f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), 0.3f, 0.3f));
        List<Pt> trace = Run(physicsHz, 2.5f, r => LookAt(Cube(r, size, mass, air), TurnZ(90f), 0.3f, 0.3f));
        float[] turned = Turned(trace);
        Assert.True(turned.Max() <= 90.5f, $"passed the target: {turned.Max():0.0}");
        Pt end = At(trace, 2.0);
        Assert.True(Between(end.Rot, TurnZ(90f)) < 2f, $"size {size} mass {mass} air {air}: {Between(end.Rot, TurnZ(90f)):0.0} degrees from the target at t {end.T:0.00}");
        // The turn at 0.6 s is the 1 kg cube's in the air, to a degree and a half: the ground's friction costs it a little.
        float here = turned[trace.FindIndex(p => p.T >= 0.6 - 1e-9)];
        float ref0 = Turned(reference)[reference.FindIndex(p => p.T >= 0.6 - 1e-9)];
        Assert.True(Math.Abs(here - ref0) <= 1.5f, $"at 0.6 s it has turned {here:0.0}, the 1 kg cube in the air {ref0:0.0}");
    }

    [Theory]
    [InlineData(45.0, true)]
    [InlineData(45.0, false)]
    [InlineData(0.0, false)]
    public void It_turns_three_linked_cubes_as_one_body_and_takes_the_target_from_a_child(double physicsHz, bool air)
    {
        List<Pt> trace = Run(physicsHz, 2.5f, r => LookAt(Linkset(r, air, out _), TurnZ(90f), 0.3f, 0.3f));
        Pt end = At(trace, 2.0);
        Assert.True(Between(end.Rot, TurnZ(90f)) < 2f, $"{Between(end.Rot, TurnZ(90f)):0.0} degrees from the target");
        Assert.True(Turned(trace).Max() <= 90.5f);

        // A request that reaches a child is the root's.
        List<Pt> viaChild = Run(physicsHz, 2.5f, r => { Linkset(r, air, out PhysicsActor left); LookAt(left, TurnZ(90f), 0.3f, 0.3f); });
        Assert.True(Between(At(viaChild, 2.0).Rot, TurnZ(90f)) < 2f, "the target set on a child did not turn the linkset");
    }

    [Theory]
    [InlineData(45.0, 15f, true)]
    [InlineData(45.0, 30f, true)]
    [InlineData(45.0, 60f, true)]
    [InlineData(45.0, 30f, false)]
    [InlineData(0.0, 30f, true)]
    [InlineData(0.0, 60f, false)]
    public virtual void A_target_moved_many_times_a_second_is_followed_smoothly(double physicsHz, float updatesPerSecond, bool air)
    {
        List<Pt> trace = DragRun(physicsHz, updatesPerSecond, air, 0.1f, 1f, 1f, 1f);
        float[] turned = Turned(trace);
        Assert.True(turned.Max() <= 90.5f, $"passed the target: {turned.Max():0.0}");
        // It trails the cursor by about twice the timescale at the cursor's speed; at 2 x 0.1 s and at most 1.5 x 90 degrees a
        // second that is 27 degrees; it never turns back.
        // On the ground the allowance for friction can twitch the body back by under a degree as it settles.
        float backSlack = air ? 0.3f : 1.2f;
        float lag = 0f;
        for (int i = 0; i < trace.Count; i++)
        {
            lag = MathF.Max(lag, Ideal(trace[i].T, 1.0, 0.5, 90f) - turned[i]);
            if (i > 0)
                Assert.True(turned[i] >= turned[i - 1] - backSlack, $"turned back at t {trace[i].T:0.00}: {turned[i - 1]:0.00} then {turned[i]:0.00}");
        }
        Assert.True(lag < 35f, $"lagged {lag:0.0} degrees");
        Pt end = At(trace, 3.0);
        Assert.True(Between(end.Rot, TurnZ(90f)) < 2f, $"{Between(end.Rot, TurnZ(90f)):0.0} degrees from the target at t {end.T:0.00}");
        // Smooth: the spin changes by well under a quarter of its peak from one heartbeat to the next.
        // On the ground the spin read after a step has the contact friction's share taken out of it, which comes and goes
        // from step to step, so there the smooth thing is the turn made in each heartbeat.
        if (air)
        {
            float peak = trace.Max(p => p.Spin.Length());
            for (int i = 1; i < trace.Count; i++)
                Assert.True((trace[i].Spin - trace[i - 1].Spin).Length() <= 0.35f * peak + 0.05f, $"the spin jumped at t {trace[i].T:0.00}");
        }
        else
        {
            float peakStep = Enumerable.Range(1, turned.Length - 1).Max(i => MathF.Abs(turned[i] - turned[i - 1]));
            for (int i = 2; i < turned.Length; i++)
                Assert.True(MathF.Abs((turned[i] - turned[i - 1]) - (turned[i - 1] - turned[i - 2])) <= 0.5f * peakStep + 1f, $"the turn per heartbeat jumped at t {trace[i].T:0.00}");
        }
    }

    // The cursor's turn (degrees) at time t: a smooth start and stop of `total` degrees over `seconds` from `start`.
    protected static float Ideal(double t, double seconds, double start, float total)
    {
        double s = Math.Clamp((t - start) / seconds, 0.0, 1.0);
        return total * (float)(s * s * (3.0 - 2.0 * s));
    }

    protected static List<Pt> DragRun(double physicsHz, float updatesPerSecond, bool air, float timescale, float size, float mass, float seconds, bool linkset = false, float duration = 6f)
    {
        float last = float.NaN;
        return Run(physicsHz, duration,
            r =>
            {
                PhysicsActor a = linkset ? Linkset(r, air, out _) : Cube(r, size, mass, air);
                a.APIDStrength = timescale;
                a.APIDDamping = timescale;
            },
            (r, t) =>
            {
                // The viewer's last update at or before now (the earlier ones of the same heartbeat are overtaken).
                double tu = Math.Floor(t * updatesPerSecond + 1e-9) / updatesPerSecond;
                if (tu < 0.5)
                    return;
                float ideal = Ideal(tu, seconds, 0.5, 90f);
                if (ideal == last)
                    return;
                last = ideal;
                r.Actor.APIDTarget = TurnZ(ideal);
                r.Actor.APIDActive = true;
            });
    }

    [Fact]
    public virtual void A_target_held_still_holds_within_half_a_degree_and_the_body_sleeps_and_a_new_target_wakes_it()
    {
        PhysicsActor actor = null;
        bool second = false;
        List<Pt> trace = Run(45.0, 12f, r => LookAt(actor = Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f),
            (r, t) =>
            {
                if (t >= 8.0 && !second)
                {
                    second = true;
                    actor.APIDTarget = TurnZ(0f);
                }
            });
        // Held 3 to 8 s: within half a degree and not moving.
        foreach (Pt p in trace.Where(p => p.T >= 3.0 && p.T <= 8.0))
        {
            Assert.True(Between(p.Rot, TurnZ(90f)) < 0.5f, $"drifted to {Between(p.Rot, TurnZ(90f)):0.00} degrees off at t {p.T:0.00}");
            Assert.True(p.Spin.Length() < 0.02f, $"still spinning {p.Spin.Length():0.000} rad/s at t {p.T:0.00}");
        }
        // Asleep by 8 s; the new target wakes it and it turns back.
        Assert.Equal(0, At(trace, 7.9).Awake);
        Assert.True(trace.Where(p => p.T > 8.0 && p.T < 8.5).Any(p => p.Awake > 0), "a new target did not wake the body");
        Assert.True(Between(trace[^1].Rot, TurnZ(0f)) < 2f, $"{Between(trace[^1].Rot, TurnZ(0f)):0.0} degrees from the second target at the end");
    }

    [Theory]
    [InlineData(45.0, true)]
    [InlineData(0.0, true)]
    [InlineData(45.0, false)]
    public void Switching_it_off_mid_turn_ends_it_at_once_and_leaves_no_angular_velocity(double physicsHz, bool air)
    {
        PhysicsActor actor = null;
        bool off = false;
        double offAt = 0;
        List<Pt> trace = Run(physicsHz, 4f, r => LookAt(actor = Cube(r, 1f, 1f, air), TurnZ(90f), 0.5f, 0.5f),
            (r, t) =>
            {
                if (t >= 0.4 && !off)
                {
                    off = true;
                    offAt = t;
                    actor.APIDActive = false;
                }
            });
        Pt before = At(trace, offAt);
        Assert.True(before.Spin.Length() > 0.3f, $"it was not turning when switched off: {before.Spin.Length():0.00} rad/s");
        // The sample after the switch is the state after one step with it off: no angular velocity is left, and it goes no further.
        Pt after = trace.First(p => p.T > offAt + 1e-9);
        Assert.True(after.Spin.Length() < 1e-3f, $"left {after.Spin.Length():0.0000} rad/s");
        float stopped = Turned(trace)[trace.IndexOf(after)];
        Assert.True(Turned(trace)[^1] - stopped < 0.5f, "it turned on after being switched off");
        Assert.True(stopped < 85f, "it was already at the target");
    }

    // ---- strength and damping --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.2f, 0.2f, 0.2f)]
    [InlineData(0.2f, 0.8f, 0.8f)]      // the slower of the two sets the timescale
    [InlineData(0.8f, 0.2f, 0.8f)]
    [InlineData(0.5f, 0f, 0.5f)]        // no damping: the strength alone
    [InlineData(0.3f, 5f, 1f)]          // "damping seems to be capped at 1.0"
    public virtual void Strength_and_damping_set_the_timescale(float strength, float damping, float timescale)
    {
        List<Pt> trace = Run(45.0, 6f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), strength, damping));
        float[] turned = Turned(trace);
        foreach (double x in new[] { 1.0, 2.0, 3.0 })
        {
            int i = trace.FindIndex(p => p.T >= x * timescale - 1e-9);
            double expected = Left(90, timescale, trace[i].T);
            float left = 90f - turned[i];
            Assert.True(Math.Abs(left - expected) <= 3.0, $"strength {strength} damping {damping}: at t {trace[i].T:0.000} {left:0.0} degrees are left, the rule says {expected:0.0}");
        }
        Assert.True(turned.Max() <= 90.5f);
    }

    [Theory]
    [InlineData(0.04f)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public virtual void A_strength_under_the_minimum_does_nothing(float strength)
    {
        List<Pt> trace = Run(45.0, 2f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), strength, 0.5f));
        Assert.True(Turned(trace).Max() < 0.5f);
    }

    [Fact]
    public void The_smallest_working_strength_turns()
    {
        List<Pt> trace = Run(45.0, 3f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), 0.0445f, 0f));
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 2f);
    }

    // ---- the short way, any axis -----------------------------------------------------------------------------------

    [Fact]
    public virtual void It_takes_the_short_way_round_and_turns_about_any_axis()
    {
        // 270 degrees about Z is 90 the other way.
        List<Pt> trace = Run(45.0, 3f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(270f), 0.2f, 0.2f));
        Assert.True(Turned(trace).Max() <= 90.5f, $"went the long way: {Turned(trace).Max():0.0}");
        Quaternion qRaw = TurnZ(50f) * Turn(30f, Vector3.UnitX); Quaternion q = Quaternion.Normalize(in qRaw);
        trace = Run(45.0, 3f, r => LookAt(Cube(r, 1f, 1f, true), q, 0.2f, 0.2f));
        Assert.True(Between(trace[^1].Rot, q) < 2f, $"{Between(trace[^1].Rot, q):0.0} degrees from a target about a tilted axis");
        // A target given as the negative of the same rotation is the same target.
        var neg = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        trace = Run(45.0, 3f, r => LookAt(Cube(r, 1f, 1f, true), neg, 0.2f, 0.2f));
        Assert.True(Between(trace[^1].Rot, q) < 2f);
    }

    // ---- axis locks, vehicles, non-finite --------------------------------------------------------------------------

    [Fact]
    public void A_locked_axis_is_not_turned_about()
    {
        // Z locked (llSetStatus(STATUS_ROTATE_Z, FALSE): bit 0x08): a turn about Z does not happen.
        List<Pt> trace = Run(45.0, 3f, r => { PhysicsActor a = Cube(r, 1f, 1f, true); a.LockAngularMotion(0x08); LookAt(a, TurnZ(90f), 0.2f, 0.2f); });
        Assert.True(Turned(trace).Max() < 0.5f, $"turned {Turned(trace).Max():0.0} degrees about a locked axis");

        // X and Y locked: Z is free, and a target that is a tilt and a turn is reached as far as Z goes.
        Quaternion tiltedRaw = TurnZ(90f) * Turn(40f, Vector3.UnitX); Quaternion tilted = Quaternion.Normalize(in tiltedRaw);
        trace = Run(45.0, 3f, r => { PhysicsActor a = Cube(r, 1f, 1f, true); a.LockAngularMotion(0x06); LookAt(a, tilted, 0.2f, 0.2f); });
        Vector3 xAxis = Vector3.UnitX * trace[^1].Rot;
        Vector3 zAxis = Vector3.UnitZ * trace[^1].Rot;
        Assert.True(MathF.Abs(zAxis.Z - 1f) < 0.01f, $"the vertical axis tilted: {zAxis}");
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 2f, $"{Between(trace[^1].Rot, TurnZ(90f)):0.0} degrees from the turn about the free axis; x axis {xAxis}");

        // Only Z free and a locked ground-resting cube: same.
        trace = Run(45.0, 3f, r => { PhysicsActor a = Cube(r, 1f, 1f, false); a.LockAngularMotion(0x08); LookAt(a, TurnZ(90f), 0.2f, 0.2f); });
        Assert.True(Turned(trace).Max() < 0.5f);
    }

    [Fact]
    public void A_non_finite_target_strength_or_damping_is_ignored()
    {
        PhysicsActor actor = null;
        List<Pt> trace = Run(45.0, 3f, r => LookAt(actor = Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f),
            (r, t) =>
            {
                if (t < 0.1)
                    return;
                actor.APIDTarget = new Quaternion(float.NaN, 0f, 0f, 1f);
                actor.APIDStrength = float.NaN;
                actor.APIDDamping = float.PositiveInfinity;
            });
        float[] turned = Turned(trace);
        Assert.True(turned.All(float.IsFinite));
        Assert.True(trace.All(p => float.IsFinite(p.Rot.X + p.Rot.Y + p.Rot.Z + p.Rot.W)));
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 2f, "the target changed");
        // A target of no length is not a rotation either.
        trace = Run(45.0, 2f, r => LookAt(Cube(r, 1f, 1f, true), new Quaternion(0f, 0f, 0f, 0f), 0.2f, 0.2f));
        Assert.True(Turned(trace).Max() < 0.5f);
    }

    [Fact]
    public void An_active_vehicle_is_left_to_its_own_controller()
    {
        List<Pt> Go(bool target) => Run(45.0, 3f, r =>
        {
            PhysicsActor a = Cube(r, 1f, 10f, true);
            r.MakeVehicle(Vehicle.TYPE_AIRPLANE);
            if (target)
                LookAt(a, TurnZ(90f), 0.2f, 0.2f);
        });
        List<Pt> without = Go(false);
        List<Pt> with = Go(true);
        Assert.True(Between(with[^1].Rot, without[^1].Rot) < 0.5f, $"a vehicle was turned {Between(with[^1].Rot, without[^1].Rot):0.0} degrees by the target");
    }

    // ---- together with the other things a script sets ------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void With_a_move_to_target_both_arrive(double physicsHz)
    {
        Vector3 target = High + new Vector3(8f, 3f, 0f);
        List<Pt> trace = Run(physicsHz, 8f, r =>
        {
            PhysicsActor a = Cube(r, 1f, 1f, true);
            a.PIDTarget = target; a.PIDTau = 0.5f; a.PIDActive = true;
            LookAt(a, TurnZ(90f), 0.3f, 0.3f);
        });
        Assert.True((trace[^1].Pos - target).Length() < 0.05f, $"{(trace[^1].Pos - target).Length():0.000} m from the position target");
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 2f);
        Assert.True(Turned(trace).Max() <= 90.5f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void With_a_constant_force_it_still_reaches_the_target_and_the_force_still_moves_it(double physicsHz)
    {
        List<Pt> trace = Run(physicsHz, 3f, r =>
        {
            PhysicsActor a = Cube(r, 1f, 1f, true);
            a.Force = new Vector3(2f, 0f, 0f);   // 2 N on 1 kg
            LookAt(a, TurnZ(90f), 0.2f, 0.2f);
        });
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 2f, $"{Between(trace[^1].Rot, TurnZ(90f)):0.0} degrees from the target");
        float moved = trace[^1].Pos.X - trace[0].Pos.X;
        Assert.InRange(moved, 0.5f * 2f * 2.9f * 2.9f * 0.9f, 0.5f * 2f * 3.0f * 3.0f * 1.1f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void It_does_not_touch_gravity_or_buoyancy(double physicsHz)
    {
        // Buoyancy 0 falls as it does with no target; buoyancy 1 stays where it is.
        List<Pt> Fall(bool target) => Run(physicsHz, 1.5f, r =>
        {
            PhysicsActor a = Cube(r, 1f, 1f, true);
            a.Buoyancy = 0f;
            if (target) LookAt(a, TurnZ(90f), 0.2f, 0.2f);
        });
        List<Pt> free = Fall(false), turning = Fall(true);
        Assert.True(free[0].Pos.Z - free[^1].Pos.Z > 3f, "the free body did not fall");
        Assert.True(MathF.Abs(free[^1].Pos.Z - turning[^1].Pos.Z) < 0.05f, $"fell {free[0].Pos.Z - free[^1].Pos.Z:0.00} m, {turning[0].Pos.Z - turning[^1].Pos.Z:0.00} m while turning");
        List<Pt> hover = Run(physicsHz, 2f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f));
        Assert.True(MathF.Abs(hover[^1].Pos.Z - hover[0].Pos.Z) < 0.02f, $"drifted {hover[^1].Pos.Z - hover[0].Pos.Z:0.000} m at buoyancy 1");
    }

    [Fact]
    public virtual void An_angular_velocity_a_script_sets_is_a_starting_speed_the_spring_takes_to_the_target()
    {
        PhysicsActor actor = null;
        bool kicked = false;
        List<Pt> trace = Run(45.0, 6f, r => LookAt(actor = Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f),
            (r, t) =>
            {
                if (t >= 3.0 && !kicked)
                {
                    kicked = true;
                    actor.RotationalVelocity = new Vector3(0f, 0f, 2f);
                }
            });
        // From rest at the target, a speed of v swings it out at most v T / e = 2 x 0.2 / 2.718 rad = 8.4 degrees, then it returns.
        float swing = trace.Where(p => p.T >= 3.0).Max(p => Between(p.Rot, TurnZ(90f)));
        Assert.True(swing > 4f, $"the script's spin was ignored: swing {swing:0.0}");
        Assert.True(swing < 10f, $"swung {swing:0.0} degrees");
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 1f, "did not return");
    }

    [Fact]
    public void A_steady_torque_is_held_against()
    {
        List<Pt> trace = Run(45.0, 4f, r =>
        {
            PhysicsActor a = Cube(r, 1f, 1f, true);
            a.Torque = new Vector3(0f, 0f, 1f);
            LookAt(a, TurnZ(90f), 0.2f, 0.2f);
        });
        Assert.True(Between(trace[^1].Rot, TurnZ(90f)) < 2f, $"{Between(trace[^1].Rot, TurnZ(90f)):0.0} degrees from the target");
    }

    // ---- figures (a turn of 90 degrees in 1 s at several masses and sizes; asserted loosely, written to ANGULAR_TARGET_OUT when it is set) ----------------

    public static IEnumerable<object[]> FigureCases()
    {
        foreach (double hz in new[] { 0.0, 45.0 })
        {
            foreach (bool air in new[] { true, false })
            {
                yield return new object[] { "turn-90-1s-1kg", hz, air, 1f, 1f };
                if (!air)
                {
                    yield return new object[] { "turn-90-1s-100kg", hz, air, 1f, 100f };
                    yield return new object[] { "turn-90-1s-4m-cube", hz, air, 4f, 100f };
                    yield return new object[] { "turn-90-1s-linkset", hz, air, 1f, 30f };
                }
            }
            yield return new object[] { "turn-90-1s-100kg", hz, true, 1f, 100f };
            yield return new object[] { "turn-90-1s-4m-cube", hz, true, 4f, 100f };
            yield return new object[] { "turn-90-1s-linkset", hz, true, 1f, 30f };
        }
    }

    protected void Row(string run, List<Pt> trace, Func<double, float> ideal)
    {
        float[] turned = Turned(trace);
        float overshoot = MathF.Max(0f, turned.Max() - 90f);
        float lag = 0f;
        for (int i = 0; i < trace.Count; i++)
            lag = MathF.Max(lag, ideal(trace[i].T) - turned[i]);
        float endErr = Between(trace[^1].Rot, TurnZ(90f));
        double reach = double.NaN;
        for (int i = 0; i < trace.Count && double.IsNaN(reach); i++)
            if (trace.Skip(i).All(p => Between(p.Rot, TurnZ(90f)) < 2f))
                reach = trace[i].T;
        float peak = trace.Max(p => p.Spin.Length()) * 180f / MathF.PI;
        float endDps = trace[^1].Spin.Length() * 180f / MathF.PI;
        string row = string.Join(",", run, lag.ToString("0.0", CultureInfo.InvariantCulture), overshoot.ToString("0.0", CultureInfo.InvariantCulture),
                                 endErr.ToString("0.0", CultureInfo.InvariantCulture), double.IsNaN(reach) ? "" : reach.ToString("0.00", CultureInfo.InvariantCulture),
                                 peak.ToString("0.0", CultureInfo.InvariantCulture), endDps.ToString("0.00", CultureInfo.InvariantCulture), trace[^1].Awake.ToString(CultureInfo.InvariantCulture));
        _output.WriteLine("run,lag_deg,overshoot_deg,end_err_deg,within2_from_s,peak_dps,end_dps,awake_at_end");
        _output.WriteLine(row);
        string path = Environment.GetEnvironmentVariable("ANGULAR_TARGET_OUT");
        if (!string.IsNullOrEmpty(path))
        {
            lock (typeof(AngularTargetTests))
                File.AppendAllText(path, row + "\n");
        }
        CheckFigure(run, overshoot, endErr);
    }

    /// <summary>What a figure run must show: Second Life's spring never passes its target.</summary>
    protected virtual void CheckFigure(string run, float overshoot, float endErr)
    {
        Assert.True(overshoot <= 0.5f, $"{run}: passed the target by {overshoot:0.0} degrees");
        Assert.True(endErr < 2f, $"{run}: {endErr:0.0} degrees from the target at the end");
    }

    protected static string Tag(double hz, bool air) => (hz > 0 ? "45Hz" : "heartbeat") + (air ? "/air" : "/ground");

    /// <summary>A 90 degree turn asked for at once (strength and damping 0.2): the table's "1 s" turn, a step in the target.</summary>
    [Theory]
    [MemberData(nameof(FigureCases))]
    public virtual void Figures_a_90_degree_turn(string name, double hz, bool air, float size, float mass)
    {
        bool linkset = name.EndsWith("linkset");
        List<Pt> trace = Run(hz, 6f, r => LookAt(linkset ? Linkset(r, air, out _) : Cube(r, size, mass, air), TurnZ(90f), 0.2f, 0.2f));
        Row(name + "|" + Tag(hz, air), trace, t => 0f);
    }

    /// <summary>The cursor's turn of 90 degrees in 1 s, then still, the target moved at 15, 30 and 60 updates a second.</summary>
    [Theory]
    [InlineData(0.0, 15f, true)]
    [InlineData(0.0, 30f, true)]
    [InlineData(0.0, 60f, true)]
    [InlineData(45.0, 15f, true)]
    [InlineData(45.0, 30f, true)]
    [InlineData(45.0, 60f, true)]
    [InlineData(0.0, 15f, false)]
    [InlineData(0.0, 30f, false)]
    [InlineData(0.0, 60f, false)]
    [InlineData(45.0, 15f, false)]
    [InlineData(45.0, 30f, false)]
    [InlineData(45.0, 60f, false)]
    public void Figures_a_dragged_target(double hz, float updates, bool air)
    {
        List<Pt> trace = DragRun(hz, updates, air, 0.1f, 1f, 1f, 1f);
        Row($"drag-90-1s-{updates:0}upd|{Tag(hz, air)}", trace, t => Ideal(t, 1.0, 0.5, 90f));
    }

    [Theory]
    [InlineData(0.0, "100kg", false)]
    [InlineData(45.0, "100kg", false)]
    [InlineData(0.0, "4m-cube", false)]
    [InlineData(45.0, "4m-cube", false)]
    [InlineData(0.0, "linkset", false)]
    [InlineData(45.0, "linkset", false)]
    [InlineData(45.0, "linkset", true)]
    public void Figures_a_dragged_target_on_heavier_bodies(double hz, string body, bool air)
    {
        List<Pt> trace = body == "linkset" ? DragRun(hz, 30f, air, 0.1f, 1f, 1f, 1f, linkset: true)
            : DragRun(hz, 30f, air, 0.1f, body == "4m-cube" ? 4f : 1f, 100f, 1f);
        Row($"drag-90-1s-30upd-{body}|{Tag(hz, air)}", trace, t => Ideal(t, 1.0, 0.5, 90f));
    }

    /// <summary>A 90 degree turn over 6 s: the cursor's slow turn.</summary>
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(45.0, true)]
    [InlineData(45.0, false)]
    public void Figures_a_slow_dragged_target(double hz, bool air)
    {
        List<Pt> trace = DragRun(hz, 30f, air, 0.1f, 1f, 1f, 6f, duration: 9f);
        Row($"drag-90-6s-30upd|{Tag(hz, air)}", trace, t => Ideal(t, 6.0, 0.5, 90f));
    }

    /// <summary>The target held still: a turn made and then 5 s held, and off mid-turn.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    public virtual void Figures_held_and_switched_off(double hz)
    {
        List<Pt> held = Run(hz, 8f, r => LookAt(Cube(r, 1f, 1f, true), TurnZ(90f), 0.2f, 0.2f));
        Row($"held-5s|{Tag(hz, true)}", held, t => 0f);
        PhysicsActor a = null;
        bool off = false;
        List<Pt> cut = Run(hz, 4f, r => LookAt(a = Cube(r, 1f, 1f, true), TurnZ(90f), 0.5f, 0.5f), (r, t) => { if (t >= 0.4 && !off) { off = true; a.APIDActive = false; } });
        float[] turned = Turned(cut);
        string row = $"off-mid-turn|{Tag(hz, true)},stopped_at_deg,{turned[^1]:0.0},end_dps,{cut[^1].Spin.Length() * 180f / MathF.PI:0.00}";
        _output.WriteLine(row);
        string path = Environment.GetEnvironmentVariable("ANGULAR_TARGET_OUT");
        if (!string.IsNullOrEmpty(path))
            lock (typeof(AngularTargetTests))
                File.AppendAllText(path, row + "\n");
    }
}
