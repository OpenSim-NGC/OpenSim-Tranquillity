/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The physics material of a physical prim, through the harness's per-heartbeat step at the 11 Hz heartbeat, with [Jolt]
/// PhysicsStepRate 45 and with one physics step per heartbeat: what reaches the prim's PhysicsActor from
/// llSetPhysicsMaterial (Friction, Restitution, GravModifier, Density), PRIM_MATERIAL (SetMaterial) and the prims'
/// damping ([Jolt] PrimLinearDamping, PrimAngularDamping). The figures are the Second Life wiki's (llSetPhysicsMaterial's
/// ranges, PRIM_MATERIAL's table, mass as density times volume), and ubODE's where the wiki gives none: two touching
/// surfaces combine by the shipped rules (the average of the two, for friction and restitution), and gravity is (1 - buoyancy) x the multiplier.
/// Every scene here has the prim's damping in its prediction. Serial with the other native tests: every run steps a real
/// backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PhysicsMaterialTests
{
    private const float G = 9.80665f;
    private const double Heartbeat = 11.0;
    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private static readonly Vector3 High = new(128f, 128f, 150f);

    /// <summary>The prim's state before a heartbeat, at physics time T.</summary>
    private readonly record struct Point(double T, Vector3 Position, Vector3 Velocity, Vector3 Spin, int Awake);

    /// <summary>
    /// Runs <paramref name="setup"/>'s scene for <paramref name="seconds"/>; <paramref name="act"/> runs before every
    /// heartbeat, as a script would between heartbeats, with the physics time so far. [Jolt] keys in
    /// <paramref name="jolt"/>. Returns the prim's state before each heartbeat.
    /// </summary>
    private static List<Point> Run(double physicsHz, float seconds, Action<Run> setup, Action<Run, double> act = null,
        params (string Key, string Value)[] jolt)
    {
        var trace = new List<Point>();
        var sc = new Scenario
        {
            Name = "physics-material",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r =>
            {
                PhysicsActor a = r.Actor;
                trace.Add(new Point(r.PhysicsTime, a.Position, a.Velocity, a.RotationalVelocity, r.AwakeBodies));
                act?.Invoke(r, r.PhysicsTime);
            },
        };
        var o = new HarnessOptions { RateHz = Heartbeat, PhysicsRateHz = physicsHz }.SecondLife();
        // The module's shipped defaults, damping included.
        o.Jolt["PrimLinearDamping"] = JoltConfig.DefaultPrimLinearDamping.ToString(CultureInfo.InvariantCulture);
        o.Jolt["PrimAngularDamping"] = JoltConfig.DefaultPrimAngularDamping.ToString(CultureInfo.InvariantCulture);
        foreach ((string key, string value) in jolt)
            o.Jolt[key] = value;
        Harness.Harness.Run(sc, o);
        return trace;
    }

    private static Point At(List<Point> trace, double t) => trace.First(p => p.T >= t - 1e-9);

    private static void Within(float expected, float percent, float got)
    {
        float tol = MathF.Abs(expected) * percent / 100f;
        Assert.InRange(got, expected - tol, expected + tol);
    }

    // The length of the engine's integration step: the physics step split into the solver's collision steps ([Jolt]
    // CollisionSteps 6 with one step per heartbeat, PhysicsStepCollisionSteps 2 with PhysicsStepRate on, 3 at 22.5 Hz). Jolt adds
    // gravity to a body's velocity, then multiplies it by (1 - damping x h), then moves it, in each of these.
    private static double Substep(double physicsHz)
    {
        var c = new JoltConfig();
        return physicsHz > 0 ? 1.0 / physicsHz / c.CollisionStepsAt((float)physicsHz) : 1.0 / Heartbeat / c.CollisionSteps;
    }

    // The distance a body starting at rest falls in time t under g with damping c, stepped as the engine steps it.
    private static double FallDistance(double t, double c, double h, double g = G)
    {
        double v = 0, d = 0;
        int n = (int)Math.Round(t / h);
        for (int i = 0; i < n; i++)
        {
            v = (v + g * h) * Math.Max(0, 1 - c * h);
            d += v * h;
        }
        return d;
    }

    // ---- friction ------------------------------------------------------------------------------------------------

    // A 1 x 1 x 0.5 m box resting on a fixed 8 x 8 x 1 m plate tilted `slopeDeg` about y (its surface falls along +x).
    // The box is flat enough that it slides rather than tips at any slope used here (it would tip only with a friction
    // above 2). Returns the trace and the downslope direction.
    private static (List<Point> Trace, Vector3 Down) Slope(double physicsHz, float slopeDeg, Action<PhysicsActor> box,
        Action<PhysicsActor> plate, float seconds = 3f)
    {
        float a = slopeDeg * MathF.PI / 180f;
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitY, a);
        var down = new Vector3(MathF.Cos(a), 0f, -MathF.Sin(a));
        var normal = new Vector3(MathF.Sin(a), 0f, MathF.Cos(a));
        var plateAt = new Vector3(128f, 128f, 80f);
        List<Point> trace = Run(physicsHz, seconds, r =>
        {
            plate(r.AddOtherBox(new Vector3(8f, 8f, 1f), plateAt, tilt, false));
            box(r.AddBox(new Vector3(1f, 1f, 0.5f), plateAt + normal * (0.5f + 0.25f + 0.005f), tilt));
        });
        return (trace, down);
    }

    private static void SetFriction(PhysicsActor a, float f) => a.Friction = f;

    // The friction two touching surfaces give under the shipped default rule ([Jolt] FrictionCombine; the average).
    private static float CombinedFriction(float a, float b) => JoltPhysicsBackend.Combine(new JoltConfig().FrictionCombine, a, b);

    [Theory]
    [InlineData(45.0, 0.5f, 0.5f)]
    [InlineData(0.0, 0.5f, 0.5f)]
    [InlineData(45.0, 0.25f, 1.0f)]   // averaged: 0.625
    [InlineData(0.0, 0.25f, 1.0f)]
    public void A_box_holds_on_a_slope_below_its_friction(double physicsHz, float boxFriction, float plateFriction)
    {
        // tan 20 deg = 0.364, below the combined friction (0.5; 0.625 for 0.25 and 1).
        (List<Point> trace, Vector3 down) = Slope(physicsHz, 20f, b => SetFriction(b, boxFriction), p => SetFriction(p, plateFriction));
        float moved = Vector3.Dot(At(trace, 2.5).Position - At(trace, 0.5).Position, down);
        Assert.InRange(moved, -0.02f, 0.02f);
        Assert.InRange(Vector3.Dot(trace[^1].Velocity, down), -0.05f, 0.05f);
    }

    [Theory]
    [InlineData(45.0, 0.5f, 0.5f)]
    [InlineData(0.0, 0.5f, 0.5f)]
    [InlineData(45.0, 0.25f, 1.0f)]
    [InlineData(0.0, 0.25f, 1.0f)]
    public void A_box_slides_down_a_slope_above_its_friction_at_the_formulas_acceleration(double physicsHz, float boxFriction,
        float plateFriction)
    {
        // tan 40 deg = 0.839, above the combined friction (0.5; 0.625 for 0.25 and 1): a = g (sin 40 - mu cos 40), less the damping c x v.
        float mu = CombinedFriction(boxFriction, plateFriction);
        float th = 40f * MathF.PI / 180f;
        float a = G * (MathF.Sin(th) - mu * MathF.Cos(th));
        (List<Point> trace, Vector3 down) = Slope(physicsHz, 40f, b => SetFriction(b, boxFriction), p => SetFriction(p, plateFriction));
        Point p0 = At(trace, 0.5), p1 = At(trace, 1.5);
        float v0 = Vector3.Dot(p0.Velocity, down), v1 = Vector3.Dot(p1.Velocity, down);
        float measured = (v1 - v0) / (float)(p1.T - p0.T);
        float expected = a - JoltConfig.DefaultPrimLinearDamping * (v0 + v1) / 2f;
        Within(expected, 5f, measured);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Material_types_set_the_friction_a_box_feels_on_a_slope(double physicsHz)
    {
        // Glass (friction 0.2) on glass slides down 20 deg (tan 0.364); stone (0.8) on stone holds.
        float th = 20f * MathF.PI / 180f;
        (List<Point> glass, Vector3 down) = Slope(physicsHz, 20f, b => b.SetMaterial(2), p => p.SetMaterial(2));
        Point p0 = At(glass, 0.5), p1 = At(glass, 1.5);
        float v0 = Vector3.Dot(p0.Velocity, down), v1 = Vector3.Dot(p1.Velocity, down);
        float expected = G * (MathF.Sin(th) - 0.2f * MathF.Cos(th)) - JoltConfig.DefaultPrimLinearDamping * (v0 + v1) / 2f;
        Within(expected, 5f, (v1 - v0) / (float)(p1.T - p0.T));

        (List<Point> stone, _) = Slope(physicsHz, 20f, b => b.SetMaterial(0), p => p.SetMaterial(0));
        Assert.InRange(Vector3.Dot(At(stone, 2.5).Position - At(stone, 0.5).Position, down), -0.02f, 0.02f);
    }

    // ---- restitution ---------------------------------------------------------------------------------------------

    // The peak height (above `floorTop`) of the actor's bottom after its first bounce: from the first sample in free
    // flight after it (rising, one sample after the one that saw the bounce, so not still in contact), the height it
    // rises to from there at its upward speed with the prim's damping (RiseHeight). The 11 Hz samples are too far apart to
    // catch the apex of a low bounce itself.
    private static float ReboundPeak(List<Point> trace, float floorTop, float halfHeight)
    {
        int impact = -1;
        for (int i = 1; i < trace.Count; i++)
            if (trace[i - 1].Velocity.Z < -1f && trace[i].Velocity.Z > 0f) { impact = i; break; }
        Assert.True(impact > 0 && impact + 1 < trace.Count, "no bounce");
        Point p = trace[impact + 1];
        Assert.True(p.Velocity.Z > 0f, "no free flight after the bounce");
        return (float)(p.Position.Z - halfHeight - floorTop + RiseHeight(p.Velocity.Z, JoltConfig.DefaultPrimLinearDamping));
    }

    // The height a body rises to when it leaves at speed v under g with damping c (dv/dt = -g - c v).
    private static double RiseHeight(double v, double c)
    {
        double h = 1e-4, z = 0;
        while (v > 0) { v -= (G + c * v) * h; z += v * h; }
        return z;
    }

    // The speed a body reaches falling `height` from rest under g with damping c.
    private static double FallSpeed(double height, double c)
    {
        double h = 1e-4, v = 0, d = 0;
        while (d < height) { v += (G - c * v) * h; d += v * h; }
        return v;
    }

    [Theory]
    [InlineData(45.0, 0.5f)]
    [InlineData(45.0, 0.7f)]
    [InlineData(45.0, 0.9f)]
    [InlineData(0.0, 0.5f)]
    [InlineData(0.0, 0.7f)]
    [InlineData(0.0, 0.9f)]
    public void A_box_dropped_2_m_rebounds_to_the_height_its_restitution_gives(double physicsHz, float restitution)
    {
        // A 1 m box dropped with its base 2 m above a fixed plate of the same restitution: the combined restitution is that
        // value under any rule that gives r for r and r (the shipped one averages), and it leaves at restitution x its arrival speed. RestingContactAndBounceTests covers 0.1 to 0.9 at
        // every step rate and drop height, sampling every step.
        const float plateTop = 60f;
        float c = JoltConfig.DefaultPrimLinearDamping;
        List<Point> trace = Run(physicsHz, 3f, r =>
        {
            r.AddOtherBox(new Vector3(10f, 10f, 2f), new Vector3(128f, 128f, plateTop - 1f), Quaternion.Identity, false).Restitution = restitution;
            r.AddBox(Unit, new Vector3(128f, 128f, plateTop + 2f + 0.5f), Quaternion.Identity).Restitution = restitution;
        });
        double expected = RiseHeight(restitution * FallSpeed(2.0, c), c);
        Within((float)expected, 5f, ReboundPeak(trace, plateTop, 0.5f));
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_box_with_no_restitution_does_not_bounce(double physicsHz)
    {
        const float plateTop = 60f;
        List<Point> trace = Run(physicsHz, 3f, r =>
        {
            r.AddOtherBox(new Vector3(10f, 10f, 2f), new Vector3(128f, 128f, plateTop - 1f), Quaternion.Identity, false).Restitution = 0f;
            r.AddBox(Unit, new Vector3(128f, 128f, plateTop + 2f + 0.5f), Quaternion.Identity).Restitution = 0f;
        });
        Assert.All(trace.Where(p => p.T > 0.8), p => Assert.True(p.Position.Z < plateTop + 0.5f + 0.05f, $"rose to {p.Position.Z} at {p.T}"));
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_linkset_bounces_with_the_restitution_of_the_prim_that_strikes(double physicsHz)
    {
        // The root on top of its child; only the child strikes the plate. Child 0.8 and root 0 bounces as 0.8; the other
        // way round it does not bounce.
        const float plateTop = 60f;
        float c = JoltConfig.DefaultPrimLinearDamping;
        // The plate has the striking child's restitution, so the pair's is the child's under the shipped averaging.
        List<Point> Drop(float rootR, float childR) => Run(physicsHz, 3f, r =>
        {
            r.AddOtherBox(new Vector3(10f, 10f, 2f), new Vector3(128f, 128f, plateTop - 1f), Quaternion.Identity, false).Restitution = childR;
            r.AddBox(Unit, new Vector3(128f, 128f, plateTop + 2f + 1.5f), Quaternion.Identity).Restitution = rootR;
            r.AddChildBox(Unit, new Vector3(128f, 128f, plateTop + 2f + 0.5f), Quaternion.Identity).Restitution = childR;
        });

        double expected = RiseHeight(0.8 * FallSpeed(2.0, c), c);
        Within((float)expected, 5f, ReboundPeak(Drop(0f, 0.8f), plateTop, 1.5f));
        Assert.All(Drop(0.8f, 0f).Where(p => p.T > 0.8), p => Assert.True(p.Position.Z < plateTop + 1.5f + 0.05f, $"rose to {p.Position.Z}"));
    }

    // ---- gravity multiplier --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0, 0.5f)]
    [InlineData(45.0, 2f)]
    [InlineData(45.0, -1f)]
    [InlineData(0.0, 0.5f)]
    [InlineData(0.0, 2f)]
    [InlineData(0.0, -1f)]
    public void The_gravity_multiplier_scales_the_fall(double physicsHz, float multiplier)
    {
        List<Point> trace = Run(physicsHz, 1.2f, r => r.AddBox(Unit, High, Quaternion.Identity).GravModifier = multiplier);
        Point p0 = At(trace, 0.1), p1 = At(trace, 1.0);
        // dv/dt = -g m - c v, so the gravity part is the measured rate plus c x the mean speed.
        float rate = (p1.Velocity.Z - p0.Velocity.Z) / (float)(p1.T - p0.T)
                   + JoltConfig.DefaultPrimLinearDamping * (p0.Velocity.Z + p1.Velocity.Z) / 2f;
        Within(-G * multiplier, 2f, rate);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_gravity_multiplier_of_0_floats(double physicsHz)
    {
        List<Point> trace = Run(physicsHz, 2f, r => r.AddBox(Unit, High, Quaternion.Identity).GravModifier = 0f);
        Assert.InRange(trace[^1].Position.Z - High.Z, -0.001f, 0.001f);
        Assert.InRange(trace[^1].Velocity.Z, -0.001f, 0.001f);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_linkset_falls_by_its_roots_gravity_multiplier(double physicsHz)
    {
        // The root's 0 floats the linkset whatever the child's; a child's 0 under a root's 1 changes nothing.
        List<Point> rootZero = Run(physicsHz, 1.2f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity).GravModifier = 0f;
            r.AddChildBox(Unit, High + new Vector3(1f, 0f, 0f), Quaternion.Identity).GravModifier = 1f;
        });
        Assert.InRange(rootZero[^1].Position.Z - High.Z, -0.001f, 0.001f);

        List<Point> childZero = Run(physicsHz, 1.2f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity);
            r.AddChildBox(Unit, High + new Vector3(1f, 0f, 0f), Quaternion.Identity).GravModifier = 0f;
        });
        Point p0 = At(childZero, 0.2), p1 = At(childZero, 1.0);
        float rate = (p1.Velocity.Z - p0.Velocity.Z) / (float)(p1.T - p0.T)
                   + JoltConfig.DefaultPrimLinearDamping * (p0.Velocity.Z + p1.Velocity.Z) / 2f;
        Within(-G, 2f, rate);
    }

    // ---- density -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(500f, 5f)]
    [InlineData(1000f, 10f)]
    [InlineData(2000f, 20f)]
    [InlineData(22587f, 225.87f)]
    [InlineData(50000f, 225.87f)]   // clamped to the wiki's 22587
    [InlineData(0.5f, 0.01f)]       // clamped to the wiki's 1
    public void Mass_is_volume_times_density_in_lindograms(float density, float mass)
    {
        float got = 0f;
        Run(0.0, 0.3f, r => r.AddBox(Unit, High, Quaternion.Identity), (r, _) =>
        {
            r.Actor.Density = density;
            got = r.Actor.Mass;
        });
        Within(mass, 0.1f, got);
    }

    [Fact]
    public void A_linkset_weighs_each_prims_volume_times_its_own_density()
    {
        // A 1 m root at 1000 (10) and a 1 x 1 x 2 m child at 3000 (2 x 30 = 60): 70. The child alone at 1000: 30.
        float sum = 0f, sumAfter = 0f;
        PhysicsActor child = null;
        Run(0.0, 0.6f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity);
            child = r.AddChildBox(new Vector3(1f, 1f, 2f), High + new Vector3(1f, 0f, 0f), Quaternion.Identity);
            child.Density = 3000f;
        }, (r, now) =>
        {
            if (r.Heartbeats == 2) sum = r.Actor.Mass + child.Mass;
            if (r.Heartbeats == 3) child.Density = 1000f;
            if (r.Heartbeats == 5) sumAfter = r.Actor.Mass + child.Mass;
        });
        Within(70f, 0.1f, sum);
        Within(30f, 0.1f, sumAfter);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Density_changes_how_an_impulse_moves_the_body(double physicsHz)
    {
        // An impulse of 40 on a 1 m cube at density 4000 (mass 40) gives it 1 m/s.
        Vector3 before = Vector3.Zero, after = Vector3.Zero;
        Run(physicsHz, 1f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity);
            r.Actor.GravModifier = 0f;
            r.Actor.Density = 4000f;
        }, (r, _) =>
        {
            if (r.Heartbeats == 3) { before = r.Actor.Velocity; r.Actor.AddForce(new Vector3(40f, 0f, 0f), false); }
            if (r.Heartbeats == 5) after = r.Actor.Velocity;
        });
        Within(1f, 2f, after.X - before.X);
    }

    // ---- material types ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0.8f, 0.4f)]    // stone
    [InlineData(1, 0.3f, 0.4f)]    // metal
    [InlineData(2, 0.2f, 0.7f)]    // glass
    [InlineData(3, 0.6f, 0.5f)]    // wood
    [InlineData(4, 0.9f, 0.3f)]    // flesh
    [InlineData(5, 0.4f, 0.7f)]    // plastic
    [InlineData(6, 0.9f, 0.9f)]    // rubber
    [InlineData(7, 0.6f, 0.5f)]    // light
    public void Each_material_type_gives_the_wikis_friction_and_restitution(int material, float friction, float restitution)
    {
        PhysicsActor box = null;
        Run(0.0, 0.2f, r =>
        {
            box = r.AddBox(Unit, High, Quaternion.Identity);
            box.Friction = 0.123f;
            box.Restitution = 0.123f;
            box.SetMaterial(material);
        }, null, ("BasicForcesBehaviour", "SecondLife"));   // the wiki's table; MaterialsTests checks the default (InWorldz) table
        Assert.Equal(friction, box.Friction);
        Assert.Equal(restitution, box.Restitution);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Rubber_bounces_by_its_restitution(double physicsHz)
    {
        // PRIM_MATERIAL_RUBBER: 0.9 (the wiki's table) on a plate of 0.9.
        const float plateTop = 60f;
        float c = JoltConfig.DefaultPrimLinearDamping;
        List<Point> trace = Run(physicsHz, 3f, r =>
        {
            r.AddOtherBox(new Vector3(10f, 10f, 2f), new Vector3(128f, 128f, plateTop - 1f), Quaternion.Identity, false).Restitution = 0.9f;
            r.AddBox(Unit, new Vector3(128f, 128f, plateTop + 2f + 0.5f), Quaternion.Identity).SetMaterial(6);
        }, null, ("BasicForcesBehaviour", "SecondLife"));
        Within((float)RiseHeight(0.9 * FallSpeed(2.0, c), c), 5f, ReboundPeak(trace, plateTop, 0.5f));
    }

    [Fact]
    public void A_material_type_out_of_range_is_ignored()
    {
        PhysicsActor box = null;
        Run(0.0, 0.2f, r =>
        {
            box = r.AddBox(Unit, High, Quaternion.Identity);
            box.SetMaterial(6);
            box.SetMaterial(8);
            box.SetMaterial(-1);
        });
        Assert.Equal(0.9f, box.Friction);
        Assert.Equal(0.9f, box.Restitution);
    }

    // ---- ranges and non-numbers ----------------------------------------------------------------------------------

    [Fact]
    public void Values_are_clamped_to_the_wikis_ranges_and_non_numbers_refused()
    {
        PhysicsActor box = null;
        Run(0.0, 0.2f, r => box = r.AddBox(Unit, High, Quaternion.Identity));

        box.Friction = 300f; Assert.Equal(255f, box.Friction);
        box.Friction = -1f; Assert.Equal(0f, box.Friction);
        box.Restitution = 2f; Assert.Equal(1f, box.Restitution);
        box.Restitution = -0.5f; Assert.Equal(0f, box.Restitution);
        box.GravModifier = 50f; Assert.Equal(28f, box.GravModifier);
        box.GravModifier = -5f; Assert.Equal(-1f, box.GravModifier);
        box.Density = 1e9f; Assert.Equal(22587f, box.Density);
        box.Density = 0f; Assert.Equal(1f, box.Density);

        box.Friction = 0.4f; box.Restitution = 0.3f; box.GravModifier = 1.5f; box.Density = 700f;
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            box.Friction = bad;
            box.Restitution = bad;
            box.GravModifier = bad;
            box.Density = bad;
        }
        Assert.Equal(0.4f, box.Friction);
        Assert.Equal(0.3f, box.Restitution);
        Assert.Equal(1.5f, box.GravModifier);
        Assert.Equal(700f, box.Density);
    }

    // ---- damping -------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> DampingCases()
    {
        foreach (double hz in new[] { 45.0, 0.0 })
        {
            yield return new object[] { hz, null };     // the default, 0.05
            yield return new object[] { hz, 0.5f };     // set through [Jolt] PrimLinearDamping / PrimAngularDamping
        }
    }

    private static (string, string)[] DampingKeys(float? damping) => damping is float d
        ? new[] { ("PrimLinearDamping", d.ToString(CultureInfo.InvariantCulture)), ("PrimAngularDamping", d.ToString(CultureInfo.InvariantCulture)) }
        : Array.Empty<(string, string)>();

    [Theory]
    [MemberData(nameof(DampingCases))]
    public void A_3_s_free_fall_covers_the_distance_its_damping_gives(double physicsHz, float? damping)
    {
        double c = damping ?? JoltConfig.DefaultPrimLinearDamping;
        List<Point> trace = Run(physicsHz, 3.3f, r => r.AddBox(Unit, new Vector3(128f, 128f, 200f), Quaternion.Identity),
            null, DampingKeys(damping));
        Point end = At(trace, 3.0);
        double h = Substep(physicsHz);
        double expected = FallDistance(end.T, c, h);
        float fell = 200f - end.Position.Z;
        Within((float)expected, 1f, fell);
        // The 1 percent tells the damping apart: with none the box would fall more than 1 percent further.
        Assert.True(FallDistance(end.T, 0, h) > expected * 1.01);
    }

    [Theory]
    [MemberData(nameof(DampingCases))]
    public void A_coasting_box_loses_speed_and_spin_as_its_damping_gives(double physicsHz, float? damping)
    {
        double c = damping ?? JoltConfig.DefaultPrimLinearDamping;
        double ca = damping ?? JoltConfig.DefaultPrimAngularDamping;
        double h = Substep(physicsHz);
        double start = double.NaN;
        List<Point> trace = Run(physicsHz, 2.5f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity);
            r.Actor.GravModifier = 0f;
        }, (r, now) =>
        {
            if (r.Heartbeats == 2)
            {
                r.Actor.Velocity = new Vector3(5f, 0f, 0f);
                r.Actor.RotationalVelocity = new Vector3(0f, 0f, 2f);
                start = now;
            }
        }, DampingKeys(damping));
        Point end = At(trace, start + 2.0);
        double keep = Math.Pow(1 - c * h, Math.Round((end.T - start) / h));
        Within((float)(5 * keep), 1f, end.Velocity.X);
        Within((float)(2 * Math.Pow(1 - ca * h, Math.Round((end.T - start) / h))), 1f, end.Spin.Z);
    }

    [Fact]
    public void The_damping_settings_default_to_0_02_and_0_05_and_are_read_from_Jolt()
    {
        var warnings = new List<string>();
        JoltConfig d = JoltConfig.FromConfig(new Nini.Config.IniConfigSource(), warnings);
        Assert.Equal(0.02f, d.PrimLinearDamping);
        Assert.Equal(0.05f, d.PrimAngularDamping);

        var src = new Nini.Config.IniConfigSource();
        Nini.Config.IConfig cfg = src.AddConfig("Jolt");
        cfg.Set("PrimLinearDamping", "0.1001");
        cfg.Set("PrimAngularDamping", "0.025");
        JoltConfig set = JoltConfig.FromConfig(src, warnings);
        Assert.Equal(0.1001f, set.PrimLinearDamping);
        Assert.Equal(0.025f, set.PrimAngularDamping);
        Assert.Empty(warnings);

        cfg.Set("PrimLinearDamping", "-1");
        cfg.Set("PrimAngularDamping", "abc");
        JoltConfig bad = JoltConfig.FromConfig(src, warnings);
        Assert.Equal(0.02f, bad.PrimLinearDamping);
        Assert.Equal(0.05f, bad.PrimAngularDamping);
        Assert.Equal(2, warnings.Count);
    }

    // ---- waking --------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> WakeCases()
    {
        // A child's gravity multiplier has no effect (the root's applies to the linkset), so it wakes nothing.
        foreach (string change in new[] { "friction", "restitution", "gravity", "density", "material" })
            foreach (bool child in new[] { false, true })
                if (!(child && change == "gravity"))
                    yield return new object[] { change, child };
    }

    [Theory]
    [MemberData(nameof(WakeCases))]
    public void A_change_wakes_a_sleeping_object(string change, bool onChild)
    {
        // A box (with a child beside it for the linkset cases) settles on the ground and sleeps; one change wakes it.
        PhysicsActor child = null;
        int awakeBefore = -1, awakeAfter = -1;
        bool changed = false;
        Run(0.0, 8f, r =>
        {
            r.AddBox(Unit, new Vector3(128f, 128f, Course.Ground + 0.52f), Quaternion.Identity);
            if (onChild)
                child = r.AddChildBox(Unit, new Vector3(129f, 128f, Course.Ground + 0.52f), Quaternion.Identity);
        }, (r, now) =>
        {
            if (changed && awakeAfter < 0) { awakeAfter = r.AwakeBodies; return; }
            if (changed || now < 1.0 || r.AwakeBodies != 0) return;
            awakeBefore = r.AwakeBodies;
            PhysicsActor a = onChild ? child : r.Actor;
            switch (change)
            {
                case "friction": a.Friction = 0.9f; break;
                case "restitution": a.Restitution = 0.9f; break;
                case "gravity": a.GravModifier = 0.5f; break;
                case "density": a.Density = 2000f; break;
                default: a.SetMaterial(1); break;
            }
            changed = true;
        });
        Assert.Equal(0, awakeBefore);
        Assert.True(awakeAfter >= 1, $"still asleep after the {change} change");
    }

    // ---- vehicles ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("push-car", 45.0)]
    [InlineData("push-car", 0.0)]
    [InlineData("crash-wall", 45.0)]
    [InlineData("crash-wall", 0.0)]
    [InlineData("crash-drop", 45.0)]
    [InlineData("crash-drop", 0.0)]
    public void A_vehicle_ignores_the_gravity_multiplier(string scenario, double physicsHz)
    {
        // The same vehicle run, with a gravity multiplier of 2, gives the same trace to the bit: a vehicle's gravity is
        // the vehicle's own. (Its damping and contact materials are those of any prim; MaterialsTests checks them.)
        Scenario sc = Harness.Harness.Find(scenario);
        RunResult Once(bool changed)
        {
            var wrapped = new Scenario
            {
                Name = sc.Name, Description = sc.Description, Slopes = sc.Slopes, UsesSlope = sc.UsesSlope,
                DefaultDuration = sc.DefaultDuration, DefaultHold = sc.DefaultHold, World = sc.World, Input = sc.Input,
                Steady = sc.Steady, SteadyFromPath = sc.SteadyFromPath, Gap = sc.Gap, PassedThrough = sc.PassedThrough,
                Setup = r =>
                {
                    sc.Setup(r);
                    if (changed)
                        r.Actor.GravModifier = 2f;
                },
            };
            var o = new HarnessOptions { RateHz = Heartbeat, PhysicsRateHz = physicsHz }.SecondLife();
            return Harness.Harness.Run(wrapped, o);
        }
        RunResult plain = Once(false), changedRun = Once(true);
        Assert.Equal(plain.Samples.Count, changedRun.Samples.Count);
        for (int i = 0; i < plain.Samples.Count; i++)
        {
            Assert.Equal(plain.Samples[i].Position, changedRun.Samples[i].Position);
            Assert.Equal(plain.Samples[i].Velocity, changedRun.Samples[i].Velocity);
        }
    }

    // ---- phantom and volume-detect prims ---------------------------------------------------------------------------

    public enum Flag { Phantom, VolumeDetect }

    // As core's DoPhysicsPropertyUpdate sets the flags on an actor the part already has: a volume detector is also phantom.
    private static void SetFlag(PhysicsActor a, Flag flag, bool on)
    {
        a.Phantom = on;
        a.SetVolumeDetect(on && flag == Flag.VolumeDetect ? 1 : 0);
    }

    // A phantom or volume-detect prim keeps the friction, restitution and density set on it: set while the flag is on,
    // they read back unchanged, its mass is its volume x density, and once it is solid again it bounces by its
    // restitution as a prim that was never phantom does.
    [Theory]
    [InlineData(45.0, Flag.Phantom)]
    [InlineData(45.0, Flag.VolumeDetect)]
    [InlineData(0.0, Flag.Phantom)]
    [InlineData(0.0, Flag.VolumeDetect)]
    public void A_phantom_or_volume_detect_prim_keeps_its_bounce_and_density(double physicsHz, Flag flag)
    {
        const float plateTop = 60f, restitution = 0.7f;
        float c = JoltConfig.DefaultPrimLinearDamping;
        float friction = float.NaN, readRestitution = float.NaN, density = float.NaN, mass = float.NaN;
        List<Point> trace = Run(physicsHz, 3f, r =>
        {
            r.AddOtherBox(new Vector3(10f, 10f, 2f), new Vector3(128f, 128f, plateTop - 1f), Quaternion.Identity, false).Restitution = restitution;
            PhysicsActor box = r.AddBox(Unit, new Vector3(128f, 128f, plateTop + 2f + 0.5f), Quaternion.Identity);
            SetFlag(box, flag, true);
            box.Friction = 0.25f;
            box.Restitution = restitution;
            box.Density = 3000f;
        }, (r, _) =>
        {
            // Two heartbeats of falling with the flag on (0.16 m, far above the plate), then solid again.
            if (r.Heartbeats != 2)
                return;
            friction = r.Actor.Friction;
            readRestitution = r.Actor.Restitution;
            density = r.Actor.Density;
            mass = r.Actor.Mass;
            SetFlag(r.Actor, flag, false);
        });

        Assert.Equal(0.25f, friction);
        Assert.Equal(restitution, readRestitution);
        Assert.Equal(3000f, density);
        Assert.Equal(30f, mass, 2);   // a 1 m cube at 3000 kg/m3, in lindograms (1000 kg/m3 is 10)
        double expected = RiseHeight(restitution * FallSpeed(2.0, c), c);
        Within((float)expected, 5f, ReboundPeak(trace, plateTop, 0.5f));
    }

    // Density acts on the body while it is phantom or volume detect: an impulse of 40 on a 1 m cube at density 4000
    // (mass 40) gives it 1 m/s.
    [Theory]
    [InlineData(45.0, Flag.Phantom)]
    [InlineData(45.0, Flag.VolumeDetect)]
    [InlineData(0.0, Flag.Phantom)]
    [InlineData(0.0, Flag.VolumeDetect)]
    public void Density_moves_a_phantom_or_volume_detect_body_as_a_solid_one(double physicsHz, Flag flag)
    {
        Vector3 before = Vector3.Zero, after = Vector3.Zero;
        Run(physicsHz, 1f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity);
            SetFlag(r.Actor, flag, true);
            r.Actor.GravModifier = 0f;
            r.Actor.Density = 4000f;
        }, (r, _) =>
        {
            if (r.Heartbeats == 3) { before = r.Actor.Velocity; r.Actor.AddForce(new Vector3(40f, 0f, 0f), false); }
            if (r.Heartbeats == 5) after = r.Actor.Velocity;
        });
        Within(1f, 2f, after.X - before.X);
    }

    // Friction set on a phantom or volume-detect prim is the friction it slides with once it is solid: on a 40 degree
    // slope the box slides at g (sin 40 - mu cos 40), less its damping, as a prim that was never phantom does.
    [Theory]
    [InlineData(45.0, Flag.Phantom)]
    [InlineData(45.0, Flag.VolumeDetect)]
    [InlineData(0.0, Flag.Phantom)]
    [InlineData(0.0, Flag.VolumeDetect)]
    public void A_phantom_or_volume_detect_prim_keeps_its_friction(double physicsHz, Flag flag)
    {
        // The flag goes off before the first step: a phantom box on the plate would fall through it.
        float mu = CombinedFriction(0.25f, 1.0f);
        float th = 40f * MathF.PI / 180f;
        float a = G * (MathF.Sin(th) - mu * MathF.Cos(th));
        (List<Point> trace, Vector3 down) = Slope(physicsHz, 40f, b =>
        {
            SetFlag(b, flag, true);
            SetFriction(b, 0.25f);
            SetFlag(b, flag, false);
        }, p => SetFriction(p, 1.0f));
        Point p0 = At(trace, 0.5), p1 = At(trace, 1.5);
        float v0 = Vector3.Dot(p0.Velocity, down), v1 = Vector3.Dot(p1.Velocity, down);
        float measured = (v1 - v0) / (float)(p1.T - p0.T);
        float expected = a - JoltConfig.DefaultPrimLinearDamping * (v0 + v1) / 2f;
        Within(expected, 5f, measured);
    }
}
