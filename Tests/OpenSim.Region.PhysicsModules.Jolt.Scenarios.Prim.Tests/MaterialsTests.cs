/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Contact friction, restitution, combine rules, terrain, damping and the preset tables, through the harness's scene
/// (an 11 Hz heartbeat with 45 Hz physics steps): a vehicle keeps its parts' materials (its friction timescales add to the
/// contact friction while it slides, and act alone in the air), a sled's parts are frictionless, two surfaces combine by
/// [Jolt] FrictionCombine and RestitutionCombine, the terrain has its own friction and restitution, every physical object
/// is damped, and [Jolt] BasicForcesBehaviour picks the preset table. Where a law is checked in closed form the damping is
/// set to 0, as the law has none. Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class MaterialsTests
{
    private const float G = 9.80665f;
    private const double Heartbeat = 11.0;
    private const double PhysicsHz = 45.0;
    private static readonly (string, string) NoDamping = ("PrimLinearDamping", "0");
    private static readonly (string, string) NoAngularDamping = ("PrimAngularDamping", "0");

    private readonly ITestOutputHelper _out;
    public MaterialsTests(ITestOutputHelper output) => _out = output;

    private readonly record struct Point(double T, Vector3 Position, Vector3 Velocity, Vector3 Spin);

    private static List<Point> Run(float seconds, Action<Run> setup, Action<Run, double> act = null, params (string Key, string Value)[] jolt)
    {
        var trace = new List<Point>();
        var sc = new Scenario
        {
            Name = "materials",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r =>
            {
                PhysicsActor a = r.Actor;
                trace.Add(new Point(r.PhysicsTime, a.Position, a.Velocity, a.RotationalVelocity));
                act?.Invoke(r, r.PhysicsTime);
            },
        };
        var o = new HarnessOptions { RateHz = Heartbeat, PhysicsRateHz = PhysicsHz, UseDefaults = true };
        foreach ((string key, string value) in jolt)
            o.Jolt[key] = value;
        Harness.Harness.Run(sc, o);
        return trace;
    }

    private static Point At(List<Point> trace, double t) => trace.First(p => p.T >= t - 1e-9);

    // ---- reading a body's recorded materials ---------------------------------------------------------------------

    private static readonly FieldInfo SceneBackend = typeof(JoltScene).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo BackendRecords = typeof(JoltPhysicsBackend).GetField("_joltToRecord", BindingFlags.NonPublic | BindingFlags.Instance);

    private static JoltBodyRecord RecordOf(Run r, PhysicsActor a)
    {
        var backend = (JoltPhysicsBackend)SceneBackend.GetValue(r.PhysicsScene);
        uint handle = ((JoltPrim)a).BodyHandle.Value;
        foreach (JoltBodyRecord rec in ((IDictionary)BackendRecords.GetValue(backend)).Values)
            if (rec.Handle == handle)
                return rec;
        throw new InvalidOperationException("no body record for the actor");
    }

    private static float[] Sorted(float[] values) => values.OrderBy(v => v).ToArray();

    // ---- vehicle contact friction (sliding) ----------------------------------------------------------------------

    // A 1 x 1 x 0.5 m car on a fixed 60 x 8 x 1 m plate, both of friction 0.2 (so the combined friction is 0.2 whatever the
    // rule), set going at 10 m/s along its nose with no motor. Its forward friction timescale is T; the others are off.
    // Returns the trace and the time the speed was set.
    private static (List<Point> Trace, double Start) Slide(float timescale, params (string Key, string Value)[] jolt)
    {
        double start = double.NaN;
        var all = new List<(string, string)>(jolt) { NoDamping, NoAngularDamping };
        List<Point> trace = Run(4f, r =>
        {
            r.AddOtherBox(new Vector3(60f, 8f, 1f), new Vector3(128f, 128f, 80f), Quaternion.Identity, false).Friction = 0.2f;
            PhysicsActor car = r.AddBox(new Vector3(1f, 1f, 0.5f), new Vector3(101f, 128f, 80.755f), Quaternion.Identity);
            car.Friction = 0.2f;
            car.Restitution = 0f;
            car.VehicleType = (int)Vehicle.TYPE_CAR;
            car.VehicleVectorParam((int)Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(timescale, 1000f, 1000f));
            car.VehicleFloatParam((int)Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
        }, (r, now) =>
        {
            if (r.Heartbeats == 4)
            {
                r.Actor.Velocity = new Vector3(10f, 0f, 0f);
                start = now;
            }
        }, all.ToArray());
        return (trace, start);
    }

    // The speed of the slide `seconds` after it was set going.
    private static float SlideSpeed((List<Point> Trace, double Start) slide, double seconds, out double elapsed)
    {
        Point p = At(slide.Trace, slide.Start + seconds);
        elapsed = p.T - slide.Start;
        return p.Velocity.X;
    }

    [Fact]
    public void A_vehicle_sliding_in_contact_decays_by_the_contact_friction_plus_its_timescale_friction()
    {
        const float T = 10f, v0 = 10f, mu = 0.2f;
        var both = Slide(T);
        var contactOff = Slide(T, ("VehicleContactFriction", "0"));
        var timescaleOff = Slide(1000f);
        foreach (double t in new[] { 1.0, 2.0, 3.0 })
        {
            float v = SlideSpeed(both, t, out double e);
            float expected = (float)((v0 + mu * G * T) * Math.Exp(-e / T) - mu * G * T);
            float vContactOff = SlideSpeed(contactOff, t, out _);
            float vTimescaleOff = SlideSpeed(timescaleOff, t, out _);
            _out.WriteLine($"t={e:0.000}: both {v:0.000}, law {expected:0.000}, contact friction 0 {vContactOff:0.000}, timescale off {vTimescaleOff:0.000}");
            Assert.InRange(v, expected - 0.3f, expected + 0.3f);
            Assert.True(v < vContactOff - 0.5f, $"t={t}: not slower than a run with contact friction 0 ({v} against {vContactOff})");
            Assert.True(v < vTimescaleOff - 0.5f, $"t={t}: not slower than a run with the timescale friction off ({v} against {vTimescaleOff})");
        }
    }

    [Fact]
    public void A_vehicle_in_the_air_decays_by_its_timescale_friction_alone_whatever_its_contact_friction()
    {
        const float T = 10f, v0 = 10f;
        List<Point> Fly(string contactFriction)
        {
            return Run(4f, r =>
            {
                PhysicsActor car = r.AddBox(new Vector3(1f, 1f, 0.5f), new Vector3(100f, 128f, 150f), Quaternion.Identity);
                car.VehicleType = (int)Vehicle.TYPE_CAR;
                car.VehicleVectorParam((int)Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(T, 1000f, 1000f));
                car.VehicleFloatParam((int)Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
                car.VehicleFloatParam((int)Vehicle.BUOYANCY, 1f);   // no pull of gravity
            }, (r, now) =>
            {
                if (r.Heartbeats == 4)
                    r.Actor.Velocity = new Vector3(v0, 0f, 0f);
            }, NoDamping, NoAngularDamping, ("VehicleContactFriction", contactFriction));
        }
        List<Point> zero = Fly("0"), five = Fly("5");
        double t0 = zero.First(p => p.Velocity.X > v0 / 2).T;
        foreach (double t in new[] { 1.0, 2.0, 3.0 })
        {
            Point a = At(zero, t0 + t), b = At(five, t0 + t);
            double e = a.T - t0;
            double expected = v0 * Math.Exp(-e / T);
            _out.WriteLine($"t={e:0.000}: friction 0 {a.Velocity.X:0.000}, friction 5 {b.Velocity.X:0.000}, law {expected:0.000}");
            Assert.InRange(a.Velocity.X, expected - 0.1, expected + 0.1);
            Assert.Equal(a.Velocity.X, b.Velocity.X, 3);
        }
    }

    // ---- vehicle materials on the body ---------------------------------------------------------------------------

    // A two-part linkset (a root and one child) with a different friction, restitution and density in each part.
    private static PhysicsActor TwoParts(Run r, out PhysicsActor child)
    {
        PhysicsActor root = r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, 100f), Quaternion.Identity);
        root.Friction = 0.9f;
        root.Restitution = 0.2f;
        child = r.AddChildBox(new Vector3(1f, 1f, 1f), new Vector3(129f, 128f, 100f), Quaternion.Identity);
        child.Friction = 0.35f;
        child.Restitution = 0.7f;
        child.Density = 3000f;
        return root;
    }

    // Runs `stages` one per heartbeat on a two-part linkset (see TwoParts), the first at the third heartbeat, when the
    // linkset's body is built.
    private static void RunStages((string, string)[] jolt, params Action<Run, PhysicsActor, PhysicsActor>[] stages)
    {
        PhysicsActor root = null, child = null;
        int next = 0;
        Run(2f, r => root = TwoParts(r, out child), (r, now) =>
        {
            if (r.Heartbeats >= 3 && next < stages.Length)
                stages[next++](r, root, child);
        }, jolt);
        Assert.Equal(stages.Length, next);
    }

    private static void AssertParts(Run r, PhysicsActor root, float[] friction, float[] restitution)
    {
        JoltBodyRecord rec = RecordOf(r, root);
        Assert.NotNull(rec.PartFriction);
        Assert.Equal(Sorted(friction), Sorted(rec.PartFriction));
        Assert.Equal(Sorted(restitution), Sorted(rec.PartRestitution));
    }

    [Fact]
    public void A_vehicle_body_keeps_every_parts_material_friction_when_a_type_is_set_and_gets_it_back_on_none()
    {
        RunStages(Array.Empty<(string, string)>(),
            (r, root, child) =>
            {
                root.VehicleType = (int)Vehicle.TYPE_CAR;
                AssertParts(r, root, new[] { 0.9f, 0.35f }, new[] { 0.2f, 0.7f });   // kept, none zeroed
                Assert.Equal(0.9f, RecordOf(r, root).Friction);
                Assert.Equal(0.2f, RecordOf(r, root).Restitution);
            },
            (r, root, child) =>
            {
                child.Friction = 0.6f;   // a script changes a part while the vehicle runs: the body follows
                AssertParts(r, root, new[] { 0.9f, 0.6f }, new[] { 0.2f, 0.7f });
            },
            (r, root, child) =>
            {
                root.VehicleType = (int)Vehicle.TYPE_NONE;
                AssertParts(r, root, new[] { 0.9f, 0.6f }, new[] { 0.2f, 0.7f });
            });
    }

    [Fact]
    public void A_sleds_parts_all_have_zero_friction_and_keep_their_restitution_and_density()
    {
        RunStages(Array.Empty<(string, string)>(), (r, root, child) =>
        {
            float rootDensity = root.Density, childDensity = child.Density;
            root.VehicleType = (int)Vehicle.TYPE_SLED;
            AssertParts(r, root, new[] { 0f, 0f }, new[] { 0.2f, 0.7f });
            Assert.Equal(0f, RecordOf(r, root).Friction);
            Assert.Equal(rootDensity, root.Density);
            Assert.Equal(childDensity, child.Density);
            Assert.Equal(0.9f, root.Friction);   // the prims still report their own material
            Assert.Equal(0.35f, child.Friction);
        });
    }

    [Fact]
    public void A_sled_changed_to_a_car_gets_its_material_friction_back_on_every_part()
    {
        RunStages(Array.Empty<(string, string)>(),
            (r, root, child) =>
            {
                root.VehicleType = (int)Vehicle.TYPE_SLED;
                AssertParts(r, root, new[] { 0f, 0f }, new[] { 0.2f, 0.7f });
            },
            (r, root, child) =>
            {
                root.VehicleType = (int)Vehicle.TYPE_CAR;
                AssertParts(r, root, new[] { 0.9f, 0.35f }, new[] { 0.2f, 0.7f });
                root.VehicleType = (int)Vehicle.TYPE_SLED;
                AssertParts(r, root, new[] { 0f, 0f }, new[] { 0.2f, 0.7f });
                root.VehicleType = (int)Vehicle.TYPE_NONE;
                AssertParts(r, root, new[] { 0.9f, 0.35f }, new[] { 0.2f, 0.7f });
            });
    }

    [Fact]
    public void The_sled_and_vehicle_friction_keys_replace_every_parts_friction()
    {
        RunStages(new[] { ("SledContactFriction", "0.3"), ("VehicleContactFriction", "0.1") }, (r, root, child) =>
        {
            root.VehicleType = (int)Vehicle.TYPE_SLED;
            AssertParts(r, root, new[] { 0.3f, 0.3f }, new[] { 0.2f, 0.7f });
            root.VehicleType = (int)Vehicle.TYPE_CAR;
            AssertParts(r, root, new[] { 0.1f, 0.1f }, new[] { 0.2f, 0.7f });
        });
    }

    [Fact]
    public void A_vehicles_bounce_on_a_drop_above_the_cutoff_follows_its_material()
    {
        const float drop = 3f;   // arrives at 7.7 m/s, over the 1 m/s cutoff
        double Rise(bool vehicle)
        {
            double top = 80.5 + 0.25;
            List<Point> trace = Run(3f, r =>
            {
                r.AddOtherBox(new Vector3(20f, 20f, 1f), new Vector3(128f, 128f, 80f), Quaternion.Identity, false).Restitution = 0.5f;
                PhysicsActor a = r.AddBox(new Vector3(1f, 1f, 0.5f), new Vector3(128f, 128f, (float)(top + drop)), Quaternion.Identity);
                a.Restitution = 0.5f;
                if (vehicle)
                {
                    a.VehicleType = (int)Vehicle.TYPE_CAR;
                    a.VehicleVectorParam((int)Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
                    a.VehicleFloatParam((int)Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
                    // This is about the material reaching a vehicle. Under InWorldz a 7.7 m/s landing is an upward spike that
                    // Halcyon's cut takes the vertical speed off, so the cut is off for the comparison.
                    ((JoltPrim)a).VehicleControl.MitigatePhysxSpikes = false;
                }
            }, null, NoDamping, NoAngularDamping);
            int up = trace.FindIndex(p => p.Velocity.Z > 0.5f);
            Assert.True(up > 0, "the box never bounced");
            double apex = double.MinValue;
            for (int i = up; i < trace.Count && (i == up || trace[i].Velocity.Z > -0.5f); i++)
                apex = Math.Max(apex, trace[i].Position.Z);
            return apex - top;
        }
        double prim = Rise(false), car = Rise(true);
        _out.WriteLine($"bounce height: prim {prim:0.000}, vehicle {car:0.000}; restitution 0.5 gives {0.25 * drop:0.000}");
        Assert.InRange(prim, 0.25 * drop * 0.85, 0.25 * drop * 1.15);
        Assert.InRange(car, prim * 0.85, prim * 1.15);
    }

    // ---- combine rules -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MaterialCombine.Average, 0.5f)]
    [InlineData(MaterialCombine.GeometricMean, 0.4f)]
    [InlineData(MaterialCombine.Multiply, 0.16f)]
    [InlineData(MaterialCombine.Min, 0.2f)]
    [InlineData(MaterialCombine.Max, 0.8f)]
    public void Each_combine_rule_gives_its_value_for_a_known_pair(MaterialCombine rule, float expected)
    {
        Assert.Equal(expected, JoltPhysicsBackend.Combine(rule, 0.2f, 0.8f), 5);
        Assert.Equal(expected, JoltPhysicsBackend.Combine(rule, 0.8f, 0.2f), 5);
    }

    [Fact]
    public void The_combine_rules_default_to_average_and_are_read_from_Jolt()
    {
        var warnings = new List<string>();
        JoltConfig d = JoltConfig.FromConfig(new IniConfigSource(), warnings);
        Assert.Equal(MaterialCombine.Average, d.FrictionCombine);
        Assert.Equal(MaterialCombine.Average, d.RestitutionCombine);
        PhysicsBackendSettings s = d.ToBackendSettings(256, 256);
        Assert.Equal(MaterialCombine.Average, s.FrictionCombine);
        Assert.Equal(MaterialCombine.Average, s.RestitutionCombine);

        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        cfg.Set("FrictionCombine", "geometricmean");
        cfg.Set("RestitutionCombine", "Max");
        JoltConfig set = JoltConfig.FromConfig(src, warnings);
        Assert.Equal(MaterialCombine.GeometricMean, set.FrictionCombine);
        Assert.Equal(MaterialCombine.Max, set.RestitutionCombine);
        Assert.Equal(MaterialCombine.Max, set.ToBackendSettings(256, 256).RestitutionCombine);
        Assert.Empty(warnings);

        cfg.Set("FrictionCombine", "sum");
        JoltConfig bad = JoltConfig.FromConfig(src, warnings);
        Assert.Equal(MaterialCombine.Average, bad.FrictionCombine);
        Assert.Single(warnings);
    }

    // A box of friction 0.1 on a fixed plate of 0.5 tilted 25 degrees (tan 0.466): it slides at g (sin - mu cos), with mu the
    // combined friction, unless mu is over the tangent.
    private static float SlopeAcceleration(MaterialCombine rule, out float travelled)
    {
        const float deg = 25f;
        float a = deg * MathF.PI / 180f;
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitY, a);
        var down = new Vector3(MathF.Cos(a), 0f, -MathF.Sin(a));
        var normal = new Vector3(MathF.Sin(a), 0f, MathF.Cos(a));
        var plateAt = new Vector3(128f, 128f, 80f);
        List<Point> trace = Run(2.5f, r =>
        {
            r.AddOtherBox(new Vector3(40f, 8f, 1f), plateAt, tilt, false).Friction = 0.5f;
            r.AddBox(new Vector3(1f, 1f, 0.5f), plateAt + normal * 0.755f - down * 15f, tilt).Friction = 0.1f;
        }, null, NoDamping, NoAngularDamping, ("FrictionCombine", rule.ToString()));
        Point first = At(trace, 0.5), last = At(trace, 2.0);
        travelled = Vector3.Dot(last.Position - first.Position, down);
        return Vector3.Dot(last.Velocity - first.Velocity, down) / (float)(last.T - first.T);
    }

    [Theory]
    [InlineData(MaterialCombine.Average, 0.3f)]
    [InlineData(MaterialCombine.GeometricMean, 0.2236f)]
    [InlineData(MaterialCombine.Multiply, 0.05f)]
    [InlineData(MaterialCombine.Min, 0.1f)]
    public void A_box_slides_down_a_slope_at_the_acceleration_the_combined_friction_gives(MaterialCombine rule, float mu)
    {
        float a = SlopeAcceleration(rule, out _);
        float s = MathF.Sin(25f * MathF.PI / 180f), c = MathF.Cos(25f * MathF.PI / 180f);
        float expected = G * (s - mu * c);
        _out.WriteLine($"{rule}: {a:0.000} against {expected:0.000}");
        Assert.InRange(a, expected * 0.97f, expected * 1.03f);
    }

    [Fact]
    public void Under_the_maximum_rule_the_same_box_holds_on_the_slope()
    {
        SlopeAcceleration(MaterialCombine.Max, out float travelled);   // mu 0.5 is over tan 25 degrees
        Assert.InRange(travelled, -0.1f, 0.1f);
    }

    // A ball of restitution 0.9 dropped 4 m on a fixed plate of 0.3: it leaves at the combined restitution times its
    // arrival speed and so rises to that squared times the drop.
    [Theory]
    [InlineData(MaterialCombine.Average, 0.6f)]
    [InlineData(MaterialCombine.GeometricMean, 0.5196f)]
    [InlineData(MaterialCombine.Multiply, 0.27f)]
    [InlineData(MaterialCombine.Min, 0.3f)]
    [InlineData(MaterialCombine.Max, 0.9f)]
    public void A_ball_bounces_as_high_as_the_combined_restitution_gives(MaterialCombine rule, float combined)
    {
        const float drop = 4f, radius = 0.25f;
        double top = 80.5 + radius;
        List<Point> trace = Run(4f, r =>
        {
            r.AddOtherBox(new Vector3(20f, 20f, 1f), new Vector3(128f, 128f, 80f), Quaternion.Identity, false).Restitution = 0.3f;
            r.AddSphere(radius * 2f, new Vector3(128f, 128f, (float)(top + drop))).Restitution = 0.9f;
        }, null, NoDamping, NoAngularDamping, ("RestitutionCombine", rule.ToString()));
        double rise = Bounce(trace, top);
        double expected = combined * combined * drop;
        _out.WriteLine($"{rule}: rose {rise:0.000} against {expected:0.000}");
        Assert.InRange(rise, expected * 0.9 - 0.02, expected * 1.1 + 0.02);
    }

    // The height the body reaches above `rest` (its centre when resting) in its first bounce.
    private static double Bounce(List<Point> trace, double rest)
    {
        int up = trace.FindIndex(p => p.Velocity.Z > 0.3f);
        Assert.True(up > 0, "the body never bounced");
        double apex = double.MinValue;
        for (int i = up; i < trace.Count && (i == up || trace[i].Velocity.Z > -0.3f); i++)
            apex = Math.Max(apex, trace[i].Position.Z);
        return apex - rest;
    }

    // ---- terrain -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_vehicle_friction_keys_default_to_the_material_and_zero_and_are_read_from_Jolt()
    {
        var warnings = new List<string>();
        JoltConfig d = JoltConfig.FromConfig(new IniConfigSource(), warnings);
        Assert.Null(d.VehicleContactFriction);
        Assert.Null(d.ToVehicleSettings().ContactFriction);
        Assert.Equal(0f, d.SledContactFriction);
        Assert.Equal(0f, d.ToVehicleSettings().SledContactFriction);

        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        cfg.Set("VehicleContactFriction", "");
        Assert.Null(JoltConfig.FromConfig(src, warnings).VehicleContactFriction);
        cfg.Set("VehicleContactFriction", "0");
        Assert.Equal(0f, JoltConfig.FromConfig(src, warnings).ToVehicleSettings().ContactFriction);
        cfg.Set("VehicleContactFriction", "0.7");
        cfg.Set("SledContactFriction", "0.25");
        JoltConfig set = JoltConfig.FromConfig(src, warnings);
        Assert.Equal(0.7f, set.ToVehicleSettings().ContactFriction);
        Assert.Equal(0.25f, set.ToVehicleSettings().SledContactFriction);
        Assert.Empty(warnings);

        cfg.Set("VehicleContactFriction", "high");
        cfg.Set("SledContactFriction", "-1");
        JoltConfig bad = JoltConfig.FromConfig(src, warnings);
        Assert.Null(bad.VehicleContactFriction);
        Assert.Equal(0f, bad.SledContactFriction);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void The_terrain_settings_default_to_0_4_and_0_05_and_are_read_from_Jolt()
    {
        var warnings = new List<string>();
        JoltConfig d = JoltConfig.FromConfig(new IniConfigSource(), warnings);
        Assert.Equal(0.4f, d.TerrainFriction);
        Assert.Equal(0.05f, d.TerrainRestitution);
        PhysicsBackendSettings s = d.ToBackendSettings(256, 256);
        Assert.Equal(0.4f, s.TerrainFriction);
        Assert.Equal(0.05f, s.TerrainRestitution);

        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        cfg.Set("TerrainFriction", "0.8");
        cfg.Set("TerrainRestitution", "0.5");
        JoltConfig set = JoltConfig.FromConfig(src, warnings);
        Assert.Equal(0.8f, set.TerrainFriction);
        Assert.Equal(0.5f, set.TerrainRestitution);
        Assert.Empty(warnings);

        cfg.Set("TerrainRestitution", "2");
        Assert.Equal(0.05f, JoltConfig.FromConfig(src, warnings).TerrainRestitution);
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData(null, 0.475f)]    // terrain 0.05 and ball 0.9 average to 0.475
    [InlineData("0.5", 0.7f)]     // terrain 0.5 and ball 0.9 average to 0.7
    public void A_ball_bounces_on_the_terrain_to_the_height_its_restitution_and_the_terrains_give(string terrainRestitution, float combined)
    {
        const float drop = 4f, radius = 0.25f;
        float rest = 0f;
        var jolt = new List<(string, string)> { NoDamping, NoAngularDamping };
        if (terrainRestitution != null)
            jolt.Add(("TerrainRestitution", terrainRestitution));
        List<Point> trace = Run(4f, r =>
        {
            rest = r.GroundAt(128f, 128f) + radius;
            r.AddSphere(radius * 2f, new Vector3(128f, 128f, rest + drop)).Restitution = 0.9f;
        }, null, jolt.ToArray());
        double got = Bounce(trace, rest);
        double expected = combined * combined * drop;
        _out.WriteLine($"terrain {terrainRestitution ?? "default"}: rose {got:0.000} against {expected:0.000}");
        Assert.InRange(got, expected * 0.9 - 0.02, expected * 1.1 + 0.02);
    }

    [Theory]
    [InlineData(null, 0.4f)]     // a box of 0.4 on terrain 0.4
    [InlineData("0.8", 0.6f)]    // on terrain 0.8
    public void A_box_sliding_on_the_terrain_slows_at_the_combined_friction(string terrainFriction, float mu)
    {
        const float v0 = 5f;
        double start = double.NaN;
        // Enhanced edge removal keeps the box from catching the heightfield's triangle edges, which would stop it sooner.
        var jolt = new List<(string, string)> { NoDamping, NoAngularDamping, ("BodyEnhancedEdgeRemoval", "true") };
        if (terrainFriction != null)
            jolt.Add(("TerrainFriction", terrainFriction));
        List<Point> trace = Run(3f, r =>
        {
            float z = r.GroundAt(128f, 128f) + 0.25f + 0.002f;
            r.AddBox(new Vector3(1f, 1f, 0.5f), new Vector3(128f, 128f, z), Quaternion.Identity).Friction = 0.4f;
        }, (r, now) =>
        {
            if (r.Heartbeats == 1)
            {
                r.Actor.Velocity = new Vector3(v0, 0f, 0f);
                start = now;
            }
        }, jolt.ToArray());
        Point p = At(trace, start + 0.5);
        float expected = v0 - mu * G * (float)(p.T - start);
        _out.WriteLine($"terrain friction {terrainFriction ?? "default"}: {p.Velocity.X:0.000} against {expected:0.000}");
        Assert.InRange(p.Velocity.X, expected - 0.15f, expected + 0.15f);
    }

    // ---- damping -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_damping_defaults_are_0_02_linear_and_0_05_angular()
    {
        JoltConfig d = JoltConfig.FromConfig(new IniConfigSource(), new List<string>());
        Assert.Equal(0.02f, d.PrimLinearDamping);
        Assert.Equal(0.05f, d.PrimAngularDamping);
    }

    // A body in free space (no gravity, no friction but the damping): its speed and spin decay by the engine's damping.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_default_damping_reaches_prim_and_vehicle_bodies(bool vehicle)
    {
        double start = double.NaN;
        List<Point> trace = Run(5f, r =>
        {
            PhysicsActor a = r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 150f), Quaternion.Identity);
            if (vehicle)
            {
                a.VehicleType = (int)Vehicle.TYPE_CAR;
                a.VehicleVectorParam((int)Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
                a.VehicleVectorParam((int)Vehicle.ANGULAR_FRICTION_TIMESCALE, new Vector3(1000f, 1000f, 1000f));
                a.VehicleFloatParam((int)Vehicle.LINEAR_DEFLECTION_EFFICIENCY, 0f);
                a.VehicleFloatParam((int)Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, 0f);
                a.VehicleFloatParam((int)Vehicle.VERTICAL_ATTRACTION_EFFICIENCY, 0f);
                a.VehicleFloatParam((int)Vehicle.BUOYANCY, 1f);
            }
            else
            {
                a.GravModifier = 0f;
            }
        }, (r, now) =>
        {
            if (r.Heartbeats == 4)
            {
                r.Actor.Velocity = new Vector3(20f, 0f, 0f);
                r.Actor.RotationalVelocity = new Vector3(0f, 0f, 2f);
                start = now;
            }
        });
        Point p = At(trace, start + 3.0);
        double e = p.T - start;
        // Over e seconds a vehicle's own 1000 s timescale friction takes e/1000 of it too.
        double timescale = vehicle ? Math.Exp(-e / 1000.0) : 1.0;
        double linear = 20 * Math.Exp(-0.02 * e) * timescale, angular = 2 * Math.Exp(-0.05 * e) * timescale;
        _out.WriteLine($"{(vehicle ? "vehicle" : "prim")}: speed {p.Velocity.X:0.0000} against {linear:0.0000}, spin {p.Spin.Z:0.0000} against {angular:0.0000}");
        Assert.InRange(p.Velocity.X, linear * 0.995, linear * 1.005);
        Assert.InRange(p.Spin.Z, angular * 0.99, angular * 1.01);
    }

    // ---- BasicForcesBehaviour and the preset tables --------------------------------------------------------------

    private static readonly (float Friction, float Restitution)[] SecondLifeTable =
    {
        (0.8f, 0.4f), (0.3f, 0.4f), (0.2f, 0.7f), (0.6f, 0.5f), (0.9f, 0.3f), (0.4f, 0.7f), (0.9f, 0.9f), (0.6f, 0.5f),
    };

    private static readonly (float Friction, float Restitution)[] InWorldzTable =
    {
        (0.5f, 0.05f), (0.3f, 0.4f), (0.2f, 0.7f), (0.6f, 0.5f), (0.9f, 0.3f), (0.4f, 0.7f), (0.9f, 0.9f), (0.6f, 0.5f),
    };

    [Fact]
    public void BasicForcesBehaviour_defaults_to_InWorldz_and_is_read_from_Jolt()
    {
        var warnings = new List<string>();
        Assert.Equal(JoltConfig.BasicForces.InWorldz, JoltConfig.FromConfig(new IniConfigSource(), warnings).BasicForcesBehaviour);

        var src = new IniConfigSource();
        IConfig cfg = src.AddConfig("Jolt");
        cfg.Set("BasicForcesBehaviour", "SecondLife");
        Assert.Equal(JoltConfig.BasicForces.SecondLife, JoltConfig.FromConfig(src, warnings).BasicForcesBehaviour);
        Assert.Empty(warnings);

        cfg.Set("BasicForcesBehaviour", "Havok");
        Assert.Equal(JoltConfig.BasicForces.InWorldz, JoltConfig.FromConfig(src, warnings).BasicForcesBehaviour);
        Assert.Single(warnings);
    }

    [Fact]
    public void The_tables_are_the_Second_Life_wiki_and_InWorldz_ones_and_light_is_wood_under_InWorldz()
    {
        Assert.Equal(SecondLifeTable, JoltPrim.MaterialTableFor(JoltConfig.BasicForces.SecondLife));
        Assert.Equal(InWorldzTable, JoltPrim.MaterialTableFor(JoltConfig.BasicForces.InWorldz));
        Assert.Equal(JoltPrim.InWorldzMaterialTable[3], JoltPrim.InWorldzMaterialTable[7]);   // light is wood
    }

    [Theory]
    [InlineData(null)]
    [InlineData("InWorldz")]
    [InlineData("SecondLife")]
    public void PRIM_MATERIAL_gives_the_prim_the_selected_tables_friction_and_restitution(string behaviour)
    {
        var expected = behaviour == "SecondLife" ? SecondLifeTable : InWorldzTable;   // unset is InWorldz
        var seen = new List<(float, float)>();
        Run(1f, r =>
        {
            PhysicsActor a = r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, 100f), Quaternion.Identity);
            for (int m = 0; m < 8; m++)
            {
                a.SetMaterial(m);
                seen.Add((a.Friction, a.Restitution));
            }
            // llSetPhysicsMaterial's ranges are the same under both.
            a.Friction = 300f;
            Assert.Equal(255f, a.Friction);
            a.Restitution = 2f;
            Assert.Equal(1f, a.Restitution);
        }, null, behaviour == null ? Array.Empty<(string, string)>() : new[] { ("BasicForcesBehaviour", behaviour) });
        Assert.Equal(expected, seen);
    }

    // ---- a sled's friction combine ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("InWorldz", true)]
    [InlineData("SecondLife", false)]
    public void A_sleds_contacts_take_the_smaller_friction_under_InWorldz_only_and_a_changed_type_gives_that_up(string behaviour, bool min)
    {
        RunStages(new[] { ("VehicleBehaviour", behaviour) },
            (r, root, child) =>
            {
                Assert.False(RecordOf(r, root).FrictionMin);
                root.VehicleType = (int)Vehicle.TYPE_SLED;
                Assert.Equal(min, RecordOf(r, root).FrictionMin);
            },
            (r, root, child) =>
            {
                root.VehicleType = (int)Vehicle.TYPE_CAR;
                Assert.False(RecordOf(r, root).FrictionMin);
                root.VehicleType = (int)Vehicle.TYPE_SLED;
                Assert.Equal(min, RecordOf(r, root).FrictionMin);
                root.VehicleType = (int)Vehicle.TYPE_NONE;
                Assert.False(RecordOf(r, root).FrictionMin);
            });
    }

    // A sled of three parts (a 1 x 1 x 0.5 m root and a child 1 m ahead and one 1 m behind) let go at rest on a fixed plank
    // of friction 0.6 tilted 15 degrees (tan 15 = 0.268). A part's friction is 0, and 0 averaged with 0.6 is 0.3, above
    // tan 15, which holds a body still; the smaller of the two is 0, which lets it slide down. Under SecondLife the average
    // stays, as it was.
    private static double SledOnA15DegreePlank(string behaviour, out double secondsSliding)
    {
        const float tilt = 15f * MathF.PI / 180f;
        Quaternion lean = Quaternion.CreateFromAxisAngle(Vector3.UnitY, tilt);   // +x runs downhill
        Vector3 along = Vector3.Transform(Vector3.UnitX, lean), up = Vector3.Transform(Vector3.UnitZ, lean);
        var trace = new List<Point>();
        double typeSetAt = -1;
        var sc = new Scenario
        {
            Name = "sled-plank",
            DefaultDuration = _ => 6f,
            Setup = r =>
            {
                Vector3 centre = new(128f, 128f, r.GroundAt(128f, 128f) + 12f);
                r.AddOtherBox(new Vector3(40f, 10f, 1f), centre, lean, false).Friction = 0.6f;
                Vector3 at = centre - along * 8f + up * (0.5f + 0.25f + 0.02f);
                r.AddBox(new Vector3(1f, 1f, 0.5f), at, lean).Friction = 0.6f;
                r.AddChildBox(new Vector3(1f, 1f, 0.5f), at + along, lean, 41001).Friction = 0.6f;
                r.AddChildBox(new Vector3(1f, 1f, 0.5f), at - along, lean, 41002).Friction = 0.6f;
            },
            Input = r =>
            {
                if (r.Heartbeats == 4 && typeSetAt < 0)
                {
                    r.Actor.VehicleType = (int)Vehicle.TYPE_SLED;
                    typeSetAt = r.PhysicsTime;
                }
                trace.Add(new Point(r.PhysicsTime, r.Actor.Position, r.Actor.Velocity, r.Actor.RotationalVelocity));
            },
        };
        var o = new HarnessOptions { RateHz = Heartbeat, PhysicsRateHz = PhysicsHz, UseDefaults = true };
        o.Jolt["VehicleBehaviour"] = behaviour;
        Harness.Harness.Run(sc, o);
        Assert.True(typeSetAt > 0, "the sled was never made a vehicle");
        secondsSliding = 2.0;
        return At(trace, typeSetAt + secondsSliding).Velocity.Length();
    }

    [Fact]
    public void A_sled_of_three_parts_starts_and_slides_on_a_fifteen_degree_plank_under_InWorldz()
    {
        double speed = SledOnA15DegreePlank("InWorldz", out double t);
        _out.WriteLine($"SLED-PLANK InWorldz: {speed:0.000} m/s {t} s after it was let go");
        Assert.InRange(speed, 3.0, 6.0);   // frictionless: g sin 15 = 2.54 m/s^2, so about 5 m/s after 2 s
    }

    [Fact]
    public void A_sled_of_three_parts_stays_on_a_fifteen_degree_plank_under_SecondLife_as_it_did()
    {
        double speed = SledOnA15DegreePlank("SecondLife", out double t);
        _out.WriteLine($"SLED-PLANK SecondLife: {speed:0.000} m/s {t} s after it was let go");
        Assert.InRange(speed, 0.0, 0.3);
    }
}
