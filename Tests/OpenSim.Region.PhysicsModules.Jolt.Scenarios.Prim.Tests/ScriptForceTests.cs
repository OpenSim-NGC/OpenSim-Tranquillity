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

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Script forces on one physical prim or linkset, through the harness's per-heartbeat step at the 11 Hz heartbeat, with
/// [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat: what reaches the prim's PhysicsActor from
/// llSetForce and llSetTorque (Force, Torque), llApplyImpulse and llApplyRotationalImpulse (AddForce and AddAngularForce,
/// not a push), llSetBuoyancy (Buoyancy), llSetStatus STATUS_ROTATE_* (LockAngularMotion) and llGetMass (Mass). The
/// targets are the Second Life wiki's (llGetMass in lindograms: volume x 1000 kg/m^3 / 100 at the default density) and
/// ubODE's where the wiki says nothing. Core turns a local vector into region axes with the prim's rotation before it
/// reaches the actor (SceneObjectPart.ApplyImpulse, ApplyAngularImpulse), and the local cases below do the same.
/// Accelerations are measured over the first 0.27 s of motion, where the prims' linear and angular damping
/// ([Jolt] PrimLinearDamping and PrimAngularDamping, 0.05 per second by default) costs under 1 percent. Serial with the
/// other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[PinnedMaterials]
public class ScriptForceTests
{
    private const float G = 9.80665f;
    private const float CubeMass = 10f;                    // a 1 m cube: 1 m^3 x 1000 kg/m^3 / 100
    private const float CubeInertia = CubeMass * 2f / 12f; // about any face axis of a solid cube: m (1 + 1) / 12
    private const double Early = 0.27;                     // the window accelerations are measured over (s)
    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private static readonly Vector3 High = new(128f, 128f, 150f);
    // Turned a quarter about the vertical: the prim's own x points along region +y.
    private static readonly Quaternion Turned = Quaternion.CreateFromEulers(0f, 0f, MathF.PI / 2f);

    /// <summary>The prim's state before a heartbeat, at physics time T.</summary>
    private readonly record struct Point(double T, Vector3 Position, Vector3 Velocity, Vector3 Spin, Quaternion Rotation, int Awake);

    /// <summary>
    /// Runs <paramref name="setup"/>'s scene for <paramref name="seconds"/>. <paramref name="act"/> runs before every
    /// heartbeat, as a script would between heartbeats, with the physics time stepped so far. Returns the prim's state
    /// before each heartbeat.
    /// </summary>
    private static List<Point> Run(double physicsHz, float seconds, Action<Run> setup, Action<Run, double> act = null)
    {
        var trace = new List<Point>();
        var sc = new Scenario
        {
            Name = "script-forces",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r =>
            {
                PhysicsActor a = r.Actor;
                trace.Add(new Point(r.PhysicsTime, a.Position, a.Velocity, a.RotationalVelocity, a.Orientation, r.AwakeBodies));
                act?.Invoke(r, r.PhysicsTime);
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        return trace;
    }

    private static Point At(List<Point> trace, double t) => trace.First(p => p.T >= t - 1e-9);

    /// <summary>Mean rate of change of <paramref name="of"/> from the first point at or after t0 to the first at or after t1.</summary>
    private static float Rate(List<Point> trace, double t0, double t1, Func<Point, float> of)
    {
        Point a = At(trace, t0), b = At(trace, t1);
        return (float)((of(b) - of(a)) / (b.T - a.T));
    }

    private static void Within(float expected, float percent, float got)
    {
        float tol = MathF.Abs(expected) * percent / 100f;
        Assert.InRange(got, expected - tol, expected + tol);
    }

    // The angle (rad) of the turn from one rotation to the other.
    private static float AngleBetween(Quaternion a, Quaternion b)
    {
        float dot = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        return 2f * MathF.Acos(Math.Clamp(dot, 0f, 1f));
    }

    // ---- llSetForce ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_set_force_accelerates_at_force_over_mass_until_cleared(double physicsHz)
    {
        var force = new Vector3(20f, 0f, 0f);
        float mass = 0f;
        double clearedAt = double.NaN;
        List<Point> trace = Run(physicsHz, 2.5f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.Force = force; },
            (r, now) =>
            {
                mass = r.Actor.Mass;
                if (now >= 1.0 && double.IsNaN(clearedAt)) { r.Actor.Force = Vector3.Zero; clearedAt = now; }
            });

        Within(CubeMass, 0.1f, mass);
        Within(force.X / mass, 2f, Rate(trace, 0.0, Early, p => p.Velocity.X));
        Assert.InRange(Rate(trace, 0.0, Early, p => p.Velocity.Y), -0.01f, 0.01f);

        // Held: still accelerating at the end of the first second.
        Assert.True(Rate(trace, 0.7, 1.0, p => p.Velocity.X) > 0.9f * force.X / mass);

        // Cleared: the force's acceleration is gone. All that is left is the damping, c x v (c = 0.05 per second),
        // which slows it a little.
        float after = Rate(trace, clearedAt + 0.1, clearedAt + 1.0, p => p.Velocity.X);
        float v = At(trace, clearedAt).Velocity.X;
        Assert.InRange(after, -(JoltConfig.DefaultPrimLinearDamping * v + 0.02f * force.X / mass), 0.02f * force.X / mass);
    }

    // ---- llApplyImpulse ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0, false)]
    [InlineData(45.0, true)]
    [InlineData(0.0, false)]
    [InlineData(0.0, true)]
    public void An_impulse_changes_speed_by_impulse_over_mass_along_the_right_axis(double physicsHz, bool local)
    {
        var impulse = new Vector3(15f, 0f, 0f);
        float mass = 0f;
        Vector3 before = Vector3.Zero, after = Vector3.Zero;
        Run(physicsHz, 1.2f,
            r => r.AddBox(Unit, High, Turned),
            (r, now) =>
            {
                if (r.Heartbeats == 5)
                {
                    mass = r.Actor.Mass;
                    before = r.Actor.Velocity;
                    // SceneObjectPart.ApplyImpulse: a local impulse is turned into region axes with the prim's rotation;
                    // SceneObjectGroup.applyImpulse hands it to the root's actor as a non-push force.
                    r.Actor.AddForce(local ? impulse * r.Actor.Orientation : impulse, false);
                }
                else if (r.Heartbeats == 6)
                    after = r.Actor.Velocity;
            });

        Vector3 dv = after - before;
        float expected = impulse.X / mass;
        // Region axes: along region x. Local: along the prim's own x, which the quarter turn points along region +y.
        Within(expected, 2f, local ? dv.Y : dv.X);
        Assert.InRange(local ? dv.X : dv.Y, -0.02f * expected, 0.02f * expected);
    }

    // ---- llSetTorque -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_set_torque_turns_about_its_axis_and_keeps_accelerating_until_cleared(double physicsHz)
    {
        var torque = new Vector3(0f, 0f, 2f);
        float alpha = torque.Z / CubeInertia;
        double clearedAt = double.NaN;
        List<Point> trace = Run(physicsHz, 2.5f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.Torque = torque; },
            (r, now) =>
            {
                if (now >= 1.0 && double.IsNaN(clearedAt)) { r.Actor.Torque = Vector3.Zero; clearedAt = now; }
            });

        Within(alpha, 2f, Rate(trace, 0.0, Early, p => p.Spin.Z));
        Point early = At(trace, Early);
        Assert.InRange(MathF.Abs(early.Spin.X) + MathF.Abs(early.Spin.Y), 0f, 0.01f * early.Spin.Z);

        // Held: still accelerating at the end of the first second.
        Assert.True(Rate(trace, 0.7, 1.0, p => p.Spin.Z) > 0.9f * alpha);

        // Cleared: no more angular acceleration; the angular damping (0.05 per second) slows it a little.
        float after = Rate(trace, clearedAt + 0.1, clearedAt + 1.0, p => p.Spin.Z);
        float w = At(trace, clearedAt).Spin.Z;
        Assert.InRange(after, -(JoltConfig.DefaultPrimAngularDamping * w + 0.02f * alpha), 0.02f * alpha);
    }

    // ---- llApplyRotationalImpulse ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0, false)]
    [InlineData(45.0, true)]
    [InlineData(0.0, false)]
    [InlineData(0.0, true)]
    public void A_rotational_impulse_changes_spin_by_impulse_over_inertia_about_the_right_axis(double physicsHz, bool local)
    {
        // About region z, or about the prim's own x, which the quarter turn points along region +y.
        var impulse = local ? new Vector3(CubeInertia, 0f, 0f) : new Vector3(0f, 0f, CubeInertia);   // 1 rad/s
        Vector3 before = Vector3.Zero, after = Vector3.Zero;
        Run(physicsHz, 1.2f,
            r => r.AddBox(Unit, High, Turned),
            (r, now) =>
            {
                if (r.Heartbeats == 5)
                {
                    before = r.Actor.RotationalVelocity;
                    // SceneObjectPart.ApplyAngularImpulse turns a local impulse into region axes with the prim's rotation;
                    // SceneObjectGroup.ApplyAngularImpulse hands it to the root's actor as a non-push angular force.
                    r.Actor.AddAngularForce(local ? impulse * r.Actor.Orientation : impulse, false);
                }
                else if (r.Heartbeats == 6)
                    after = r.Actor.RotationalVelocity;
            });

        Vector3 dw = after - before;
        Within(1f, 2f, local ? dw.Y : dw.Z);
        Assert.InRange(MathF.Abs(dw.X) + MathF.Abs(local ? dw.Z : dw.Y), 0f, 0.02f);
    }

    // ---- llSetBuoyancy -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Buoyancy_one_holds_its_height(double physicsHz)
    {
        List<Point> trace = Run(physicsHz, 10.2f, r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.Buoyancy = 1f; });
        Assert.True(trace[^1].T >= 10.0);
        foreach (Point p in trace)
            Assert.InRange(p.Position.Z, High.Z - 0.01f, High.Z + 0.01f);
    }

    [Theory]
    [InlineData(45.0, 0f)]
    [InlineData(0.0, 0f)]
    [InlineData(45.0, 0.5f)]
    [InlineData(0.0, 0.5f)]
    [InlineData(45.0, 2f)]
    [InlineData(0.0, 2f)]
    public void Buoyancy_sets_the_share_of_gravity(double physicsHz, float buoyancy)
    {
        List<Point> trace = Run(physicsHz, 1f, r => { r.AddBox(Unit, High, Quaternion.Identity); r.Actor.Buoyancy = buoyancy; });
        // The wiki: under 1 it sinks, 1 floats, over 1 rises; ubODE's gravity share is (1 - buoyancy).
        Within(-G * (1f - buoyancy), 2f, Rate(trace, 0.0, Early, p => p.Velocity.Z));
    }

    // ---- llSetStatus STATUS_ROTATE_X / _Y / _Z -----------------------------------------------------------------------

    private static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
    private static float On(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static byte LockBit(int axis) => (byte)(0x02 << axis);   // SceneObjectGroup.axisSelect: 0x02 X, 0x04 Y, 0x08 Z

    [Theory]
    [InlineData(45.0, 0)]
    [InlineData(45.0, 1)]
    [InlineData(45.0, 2)]
    [InlineData(0.0, 0)]
    [InlineData(0.0, 1)]
    [InlineData(0.0, 2)]
    public void A_torque_about_a_locked_axis_does_not_turn_it(double physicsHz, int axis)
    {
        List<Point> trace = Run(physicsHz, 2f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity);
            r.Actor.Buoyancy = 1f;
            r.Actor.LockAngularMotion(LockBit(axis));
            r.Actor.Torque = Axis(axis) * 2f;
        });
        foreach (Point p in trace)
        {
            Assert.InRange(p.Spin.Length(), 0f, 1e-4f);
            Assert.InRange(AngleBetween(Quaternion.Identity, p.Rotation), 0f, 1e-3f);
        }
    }

    [Theory]
    [InlineData(45.0, 0)]
    [InlineData(45.0, 1)]
    [InlineData(45.0, 2)]
    [InlineData(0.0, 0)]
    [InlineData(0.0, 1)]
    [InlineData(0.0, 2)]
    public void A_torque_about_a_free_axis_still_turns_it(double physicsHz, int locked)
    {
        int free = (locked + 1) % 3;
        List<Point> trace = Run(physicsHz, 1f, r =>
        {
            r.AddBox(Unit, High, Quaternion.Identity);
            r.Actor.Buoyancy = 1f;
            r.Actor.LockAngularMotion(LockBit(locked));
            r.Actor.Torque = Axis(free) * 2f;
        });
        Within(2f / CubeInertia, 2f, Rate(trace, 0.0, Early, p => On(p.Spin, free)));
        foreach (Point p in trace)
            Assert.InRange(MathF.Abs(On(p.Spin, locked)), 0f, 1e-4f);
    }

    // A box tilted on two axes, dropped 2 m onto level ground: free, the landing turns it; with all three axes locked
    // it lands without turning.
    [Theory]
    [InlineData(45.0, true)]
    [InlineData(45.0, false)]
    [InlineData(0.0, true)]
    [InlineData(0.0, false)]
    public void All_three_locked_a_collision_does_not_turn_it(double physicsHz, bool locked)
    {
        Quaternion tilt = Quaternion.CreateFromEulers(0.4f, 0.3f, 0f);
        float ground = 0f;
        List<Point> trace = Run(physicsHz, 3f, r =>
        {
            ground = r.GroundAt(128f, 128f);
            r.AddBox(Unit, new Vector3(128f, 128f, ground + 2.5f), tilt);
            if (locked)
                r.Actor.LockAngularMotion(0x0E);
        });
        Point end = trace[^1];
        Assert.InRange(end.Position.Z, ground, ground + 1.2f);   // it landed
        float turned = AngleBetween(tilt, end.Rotation);
        if (locked)
            Assert.InRange(turned, 0f, 1e-3f);
        else
            Assert.True(turned > 0.1f, $"free, the landing should turn it (turned {turned} rad)");
    }

    // ---- llGetMass ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Mass_is_volume_times_density_in_lindograms(double physicsHz)
    {
        float cube = 0f, sphere = 0f, linkset = 0f, linksetWelded = 0f, resized = 0f;
        Run(physicsHz, 0.2f, r => { r.AddBox(Unit, High, Quaternion.Identity); cube = r.Actor.Mass; });
        Run(physicsHz, 0.2f, r => { r.AddSphere(0.5f, High); sphere = r.Actor.Mass; });
        PhysicsActor child = null;
        Run(physicsHz, 0.3f,
            r =>
            {
                r.AddBox(Unit, High, Quaternion.Identity);
                child = r.AddChildBox(Unit, High + new Vector3(1f, 0f, 0f), Quaternion.Identity);
                linkset = r.Actor.Mass + child.Mass;   // SceneObjectGroup.GetMass sums its parts' actors
            },
            (r, now) => { if (r.Heartbeats == 2) linksetWelded = r.Actor.Mass + child.Mass; });
        Run(physicsHz, 0.3f,
            r => r.AddBox(Unit, High, Quaternion.Identity),
            (r, now) =>
            {
                if (r.Heartbeats == 1) r.Actor.Size = new Vector3(2f, 2f, 2f);
                if (r.Heartbeats == 2) resized = r.Actor.Mass;
            });

        Within(10f, 0.1f, cube);
        Within(4f / 3f * MathF.PI * 0.25f * 0.25f * 0.25f * 10f, 0.1f, sphere);   // 0.6545
        Within(20f, 0.1f, linkset);
        Within(20f, 0.1f, linksetWelded);
        Within(80f, 0.1f, resized);
    }

    // ---- a call from a child prim ------------------------------------------------------------------------------------

    // Core hands these to the root's actor; one that reaches a linked child's actor acts on the whole linkset too (the
    // wiki, llSetBuoyancy: "The most recent call of llSetBuoyancy in any child prim appears to set the global buoyancy
    // level for the object.").
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_call_on_a_child_prim_acts_on_the_linkset(double physicsHz)
    {
        var force = new Vector3(20f, 0f, 0f);
        PhysicsActor child = null;
        float mass = 0f;
        Vector3 rootForce = Vector3.Zero;
        float rootBuoyancy = 0f;
        List<Point> trace = Run(physicsHz, 1f,
            r =>
            {
                r.AddBox(Unit, High, Quaternion.Identity);
                child = r.AddChildBox(Unit, High + new Vector3(0f, 1f, 0f), Quaternion.Identity);
                child.Buoyancy = 1f;
                child.Force = force;
            },
            (r, now) => { mass = r.Actor.Mass + child.Mass; rootForce = r.Actor.Force; rootBuoyancy = r.Actor.Buoyancy; });

        Within(20f, 0.1f, mass);
        Assert.Equal(force, rootForce);
        Assert.Equal(1f, rootBuoyancy);
        Within(force.X / mass, 2f, Rate(trace, 0.0, Early, p => p.Velocity.X));
        Assert.InRange(Rate(trace, 0.0, Early, p => p.Velocity.Z), -0.02f * G, 0.02f * G);   // floats
    }

    // ---- sleeping bodies ---------------------------------------------------------------------------------------------

    public enum Call { Force, Torque, Impulse, RotationalImpulse, Buoyancy }

    // A box resting on the ground, left until the engine puts it to sleep; then one call.
    [Theory]
    [InlineData(45.0, Call.Force)]
    [InlineData(45.0, Call.Torque)]
    [InlineData(45.0, Call.Impulse)]
    [InlineData(45.0, Call.RotationalImpulse)]
    [InlineData(45.0, Call.Buoyancy)]
    [InlineData(0.0, Call.Force)]
    [InlineData(0.0, Call.Torque)]
    [InlineData(0.0, Call.Impulse)]
    [InlineData(0.0, Call.RotationalImpulse)]
    [InlineData(0.0, Call.Buoyancy)]
    public void A_sleeping_body_wakes_for_the_call(double physicsHz, Call call)
    {
        int calledAt = -1;
        List<Point> trace = Run(physicsHz, 10f,
            r => r.AddBoxOnGround(Unit, 128f, 128f, Quaternion.Identity),
            (r, now) =>
            {
                if (calledAt >= 0 || r.Heartbeats < 5 || r.AwakeBodies != 0)
                    return;
                calledAt = r.Heartbeats;
                PhysicsActor a = r.Actor;
                switch (call)
                {
                    case Call.Force: a.Force = new Vector3(0f, 0f, 300f); break;                 // more than its weight
                    case Call.Torque: a.Torque = new Vector3(0f, 0f, 200f); break;               // more than friction holds
                    case Call.Impulse: a.AddForce(new Vector3(0f, 0f, 20f), false); break;
                    case Call.RotationalImpulse: a.AddAngularForce(new Vector3(0f, 0f, 10f), false); break;
                    case Call.Buoyancy: a.Buoyancy = 2f; break;
                }
            });

        Assert.True(calledAt > 0, "the box never went to sleep");
        Point asleep = trace[calledAt], next = trace[calledAt + 1];
        Assert.Equal(0, asleep.Awake);
        Assert.True(next.Awake > 0, "the call did not wake it");
        if (call is Call.Torque or Call.RotationalImpulse)
            Assert.True(next.Spin.Z > 0.1f, $"it did not turn (spin {next.Spin})");
        else
            Assert.True(next.Velocity.Z > 0.1f, $"it did not rise (velocity {next.Velocity})");
    }

    // ---- guards ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void Non_finite_values_are_refused_and_absurd_ones_stay_finite(double physicsHz)
    {
        var nan = new Vector3(float.NaN, 0f, 0f);
        var inf = new Vector3(0f, float.PositiveInfinity, 0f);
        List<Point> trace = Run(physicsHz, 1f,
            r =>
            {
                r.AddBox(Unit, High, Quaternion.Identity);
                PhysicsActor a = r.Actor;
                a.Force = new Vector3(1f, 0f, 0f);
                a.Force = nan;
                a.Torque = inf;
                a.Buoyancy = float.NaN;
                Assert.Equal(new Vector3(1f, 0f, 0f), a.Force);
                Assert.Equal(Vector3.Zero, a.Torque);
                Assert.Equal(0f, a.Buoyancy);
            },
            (r, now) =>
            {
                if (r.Heartbeats != 2)
                    return;
                PhysicsActor a = r.Actor;
                a.Force = new Vector3(1e30f, 0f, 0f);
                a.Torque = new Vector3(0f, 0f, 1e30f);
                a.Buoyancy = -1e30f;
                a.AddAngularForce(new Vector3(1e30f, 0f, 0f), false);
            });

        foreach (Point p in trace)
        {
            Assert.True(float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y) && float.IsFinite(p.Position.Z), $"position {p.Position}");
            Assert.True(float.IsFinite(p.Velocity.Length()) && float.IsFinite(p.Spin.Length()), $"velocity {p.Velocity} spin {p.Spin}");
            Assert.InRange(p.Velocity.Length(), 0f, PhysicsBackendSettings.JoltMaxLinearSpeed * 1.01f);
            Assert.InRange(p.Spin.Length(), 0f, PhysicsBackendSettings.JoltMaxAngularSpeed * 1.01f);
        }
    }

    // ---- vehicles ----------------------------------------------------------------------------------------------------

    // A vehicle keeps its own forces: a set force, torque and buoyancy leave its motion exactly as without them (ubODE
    // hands a vehicle to its controller before it adds them, ODEPrim.Move).
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_vehicle_is_not_moved_by_set_forces_or_buoyancy(double physicsHz)
    {
        List<Point> Drive(bool withForces) => Run(physicsHz, 2f,
            r =>
            {
                r.AddBoxOnGround(new Vector3(2f, 1f, 0.5f), 128f, 128f, Quaternion.Identity);
                r.Actor.VehicleType = (int)Vehicle.TYPE_CAR;
                r.Actor.VehicleVectorParam((int)Vehicle.LINEAR_MOTOR_DIRECTION, new Vector3(5f, 0f, 0f));
                if (withForces)
                {
                    r.Actor.Force = new Vector3(0f, 500f, 500f);
                    r.Actor.Torque = new Vector3(0f, 0f, 100f);
                    r.Actor.Buoyancy = 1f;
                }
            });

        List<Point> plain = Drive(false), forced = Drive(true);
        Assert.Equal(plain.Count, forced.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].Position, forced[i].Position);
            Assert.Equal(plain[i].Rotation, forced[i].Rotation);
        }
        Assert.True(plain[^1].Position.X > 128.5f, "the car should have driven");
    }
}
