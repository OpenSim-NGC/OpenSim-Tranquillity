/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical object selected in the build tool, and one moved or turned while it sleeps, through the harness's
/// per-heartbeat step at the 11 Hz heartbeat, with [Jolt] PhysicsStepRate 45 and with one physics step per heartbeat.
/// Core hands the selection to the root's actor and every part's (SceneObjectGroup.IsSelected), and a build-tool move or
/// llSetPos to the root's Position, a turn to its Orientation (SceneObjectPart.GroupPosition and RotationOffset). The
/// target is ubODE's: a selected object stops where it is, nothing moves it, and it carries on from rest when let go
/// (ODEPrim.DoSelectedStatus, Move); a moved or turned sleeping body wakes (ODEPrim.changePosition, changeOrientation).
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class SelectionAndMoveTests
{
    private static readonly Vector3 Unit = new(1f, 1f, 1f);
    private static readonly Vector3 High = new(128f, 128f, 150f);
    private const uint SecondId = 1101, ChildA = 1102, ChildB = 1103;

    /// <summary>The state of an actor before a heartbeat, at physics time T.</summary>
    private readonly record struct Point(double T, Vector3 Position, Vector3 Velocity, Quaternion Rotation, int Awake);

    private static List<Point> Run(double physicsHz, float seconds, Action<Run> setup, Action<Run, double> act,
                                   Func<Run, PhysicsActor> traced = null)
    {
        var trace = new List<Point>();
        var sc = new Scenario
        {
            Name = "selection-and-move",
            DefaultDuration = _ => seconds,
            Setup = setup,
            Input = r =>
            {
                PhysicsActor a = traced?.Invoke(r) ?? r.Actor;
                trace.Add(new Point(r.PhysicsTime, a.Position, a.Velocity, a.Orientation, r.AwakeBodies));
                act(r, r.PhysicsTime);
            },
        };
        Harness.Harness.Run(sc, new HarnessOptions { RateHz = 11.0, PhysicsRateHz = physicsHz });
        return trace;
    }

    private static void Select(bool selected, params PhysicsActor[] parts)
    {
        foreach (PhysicsActor p in parts)
            p.Selected = selected;   // SceneObjectGroup.IsSelected: the root's actor, then every part's
    }

    // ---- selection -------------------------------------------------------------------------------------------------

    // A falling box is selected: it stops where it is and stays there for 5 s. Gravity, a push, an impulse, a set force
    // and a set velocity do not move it, and a second box dropped on it lands on it and does not move it. Let go, it
    // falls again from rest.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_selected_falling_box_stops_holds_and_falls_again_when_let_go(double physicsHz)
    {
        const double SelectAt = 0.5, DropAt = 1.0, ReleaseAt = SelectAt + 5.0;
        PhysicsActor second = null;
        Vector3 heldAt = Vector3.Zero, secondAtRelease = Vector3.Zero;
        double selectedT = double.NaN, releasedT = double.NaN;
        var held = new List<Point>();
        List<Point> trace = Run(physicsHz, 7.5f,
            r => r.AddBox(Unit, High, Quaternion.Identity),
            (r, now) =>
            {
                PhysicsActor a = r.Actor;
                if (double.IsNaN(selectedT) && now >= SelectAt)
                {
                    Select(true, a);
                    selectedT = now;
                    heldAt = a.Position;
                    return;
                }
                if (double.IsNaN(selectedT))
                    return;
                if (double.IsNaN(releasedT))
                {
                    held.Add(new Point(now, a.Position, a.Velocity, a.Orientation, r.AwakeBodies));
                    if (second == null && now >= DropAt)
                    {
                        // Script calls on the held box, and a second box dropped onto it from 3 m above.
                        a.AddForce(new Vector3(0f, 0f, 50f), true);
                        a.AddForce(new Vector3(30f, 0f, 0f), false);
                        a.AddAngularForce(new Vector3(0f, 0f, 20f), false);
                        a.Force = new Vector3(0f, 40f, 0f);
                        a.Velocity = new Vector3(5f, 0f, 0f);
                        a.RotationalVelocity = new Vector3(0f, 0f, 3f);
                        second = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, heldAt + new Vector3(0f, 0f, 3f),
                                           Quaternion.Identity, true, SecondId);
                    }
                    if (now >= ReleaseAt)
                    {
                        a.Force = Vector3.Zero;
                        secondAtRelease = second.Position;
                        Select(false, a);
                        releasedT = now;
                    }
                }
            });

        Assert.False(double.IsNaN(releasedT), "the run never let the box go");
        Assert.True(held.Count > 50, $"too few held samples ({held.Count})");
        Assert.True(held[^1].T - held[0].T >= 4.9, "not held for 5 s");
        foreach (Point p in held)
        {
            Assert.True(Vector3.Distance(p.Position, heldAt) < 0.001f, $"moved while selected at t={p.T:0.00}: {p.Position} (held at {heldAt})");
            Assert.True(p.Velocity.Length() < 1e-4f, $"reported moving while selected at t={p.T:0.00}: {p.Velocity}");
        }
        // The second box came to rest on top of it: it struck the held box and did not push it.
        Assert.InRange(secondAtRelease.Z, heldAt.Z + 0.95f, heldAt.Z + 1.05f);
        Assert.True(MathF.Abs(secondAtRelease.X - heldAt.X) < 0.3f && MathF.Abs(secondAtRelease.Y - heldAt.Y) < 0.3f,
                    $"the second box is not on the held one ({secondAtRelease})");

        // Let go: it falls again, from rest. Free fall from rest covers g t^2 / 2, 4.9 m in the first second.
        Point at = trace.First(p => p.T >= releasedT + 1.0 - 1e-9);
        float fell = heldAt.Z - at.Position.Z;
        Assert.InRange(fell, 4.0f, 5.2f);
        Assert.True(at.Velocity.Z < -8f, $"not falling a second after being let go (velocity {at.Velocity})");
        Assert.True(MathF.Abs(at.Position.X - heldAt.X) < 0.05f, $"the set velocity or impulse came back after release ({at.Position})");
    }

    // A falling three-prim linkset is selected as core selects one, every part's actor: the whole object stops and holds,
    // and a box dropped on a child part does not move it. Let go, it falls as one.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_selected_three_prim_linkset_holds_and_falls_as_one(double physicsHz)
    {
        const double SelectAt = 0.5, DropAt = 0.8, ReleaseAt = SelectAt + 3.0;
        PhysicsActor a = null, b = null, dropped = null;
        Vector3 heldAt = Vector3.Zero;
        double selectedT = double.NaN, releasedT = double.NaN;
        var held = new List<Point>();
        float massHeld = 0f, massAfter = 0f;
        List<Point> trace = Run(physicsHz, 5.5f,
            r =>
            {
                r.AddBox(Unit, High, Quaternion.Identity);
                a = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, High + new Vector3(1f, 0f, 0f), Quaternion.Identity, true, ChildA, r.Actor);
                b = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, High + new Vector3(2f, 0f, 0f), Quaternion.Identity, true, ChildB, r.Actor);
            },
            (r, now) =>
            {
                if (double.IsNaN(selectedT) && now >= SelectAt)
                {
                    Select(true, r.Actor, a, b);
                    selectedT = now;
                    heldAt = r.Actor.Position;
                    return;
                }
                if (!double.IsNaN(releasedT))
                {
                    massAfter = r.Actor.Mass + a.Mass + b.Mass;
                    return;
                }
                if (double.IsNaN(selectedT))
                    return;
                held.Add(new Point(now, r.Actor.Position, r.Actor.Velocity, r.Actor.Orientation, r.AwakeBodies));
                massHeld = r.Actor.Mass + a.Mass + b.Mass;
                if (dropped == null && now >= DropAt)
                    dropped = r.AddPart(PrimitiveBaseShape.CreateBox(), Unit, heldAt + new Vector3(2f, 0f, 3f), Quaternion.Identity, true, SecondId);
                if (now >= ReleaseAt)
                {
                    Select(false, r.Actor, a, b);
                    releasedT = now;
                }
            });

        Assert.False(double.IsNaN(releasedT), "the run never let the linkset go");
        foreach (Point p in held)
        {
            Assert.True(Vector3.Distance(p.Position, heldAt) < 0.001f, $"moved while selected at t={p.T:0.00}: {p.Position}");
            Assert.True(Vector3.Distance(Vector3.UnitX * p.Rotation, Vector3.UnitX * held[0].Rotation) < 1e-4f,
                        $"turned while selected at t={p.T:0.00}: {p.Rotation}");   // struck at one end, it did not turn
        }
        Assert.Equal(30f, massHeld, 2);

        Point at = trace.First(p => p.T >= releasedT + 1.0 - 1e-9);
        Assert.InRange(heldAt.Z - at.Position.Z, 4.0f, 5.2f);
        // Still one object: the linkset's mass is unchanged, and it fell without turning.
        Assert.Equal(30f, massAfter, 2);
        Assert.True(at.Awake <= 2, $"{at.Awake} awake bodies: a part has a body of its own");   // the linkset and the dropped box
        Assert.True(Vector3.Distance(Vector3.UnitX * at.Rotation, Vector3.UnitX) < 0.05f, $"turned as it fell ({at.Rotation})");
    }

    // ---- moving and turning a sleeping body --------------------------------------------------------------------------

    // A box asleep on the ground is moved 2 m up (a build-tool move or llSetPos: the root actor's Position). It wakes and
    // falls back to the ground.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_sleeping_box_moved_up_wakes_and_falls_back(double physicsHz)
    {
        int movedAt = -1;
        Vector3 rest = Vector3.Zero;
        List<Point> trace = Run(physicsHz, 8f,
            r => r.AddBoxOnGround(Unit, 128f, 128f, Quaternion.Identity),
            (r, now) =>
            {
                if (movedAt >= 0 || r.Heartbeats < 5 || r.AwakeBodies != 0)
                    return;
                movedAt = r.Heartbeats;
                rest = r.Actor.Position;
                r.Actor.Position = rest + new Vector3(0f, 0f, 2f);
            });

        Assert.True(movedAt > 0, "the box never went to sleep");
        Assert.Equal(0, trace[movedAt].Awake);
        Point next = trace[movedAt + 1];
        Assert.True(next.Awake > 0, "the move did not wake it");
        Assert.True(next.Position.Z < rest.Z + 2f, $"it did not start falling ({next.Position})");
        // Back on the ground. After a 2 m drop it may come to rest up to Jolt's penetration slop (PhysicsSettings
        // mPenetrationSlop, 0.02 m, not changed by the module) into the ground, where it was put down gently before.
        Point end = trace[^1];
        Assert.True(MathF.Abs(end.Position.Z - rest.Z) < 0.025f, $"it did not fall back to the ground (z {end.Position.Z}, rest {rest.Z})");
    }

    // A box asleep on the ground is turned 40 degrees about a horizontal axis (llSetRot, or a build-tool turn: the root
    // actor's Orientation). It wakes and settles flat on the ground again. Not 45: a cube turned exactly 45 degrees stands
    // balanced on an edge, an unstable equilibrium it leaves along a path that starts at zero speed, so whether the engine
    // tips it or (the speed staying under its sleep threshold for half a second) puts it to sleep on the edge depends on
    // float rounding. It tipped on x64 and Arm64 Windows and Linux and slept on its edge, 0.19 m high, on macOS.
    [Theory]
    [InlineData(45.0)]
    [InlineData(0.0)]
    public void A_sleeping_box_turned_45_degrees_wakes_and_settles(double physicsHz)
    {
        int turnedAt = -1;
        Vector3 rest = Vector3.Zero;
        List<Point> trace = Run(physicsHz, 10f,
            r => r.AddBoxOnGround(Unit, 128f, 128f, Quaternion.Identity),
            (r, now) =>
            {
                if (turnedAt >= 0 || r.Heartbeats < 5 || r.AwakeBodies != 0)
                    return;
                turnedAt = r.Heartbeats;
                rest = r.Actor.Position;
                r.Actor.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 40f * MathF.PI / 180f);
            });

        Assert.True(turnedAt > 0, "the box never went to sleep");
        Assert.Equal(0, trace[turnedAt].Awake);
        Assert.True(trace[turnedAt + 1].Awake > 0, "the turn did not wake it");
        Point end = trace[^1];
        Assert.Equal(0, end.Awake);   // settled, and the engine put it to sleep again
        Assert.True(MathF.Abs(end.Position.Z - rest.Z) < 0.02f, $"not resting on the ground (z {end.Position.Z}, rest {rest.Z})");
        // Flat: one of its faces is down, so one of its axes points straight up or down.
        float up = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }.Max(axis => MathF.Abs((axis * end.Rotation).Z));
        Assert.True(up > MathF.Cos(2f * MathF.PI / 180f), $"not flat on a face (rotation {end.Rotation})");
    }

    // ---- a selected phantom or volume-detect object -------------------------------------------------------------------

    public enum Flag { Phantom, VolumeDetect }

    // As core's DoPhysicsPropertyUpdate sets the flags on an actor the part already has: a volume detector is also phantom.
    private static void SetFlag(PhysicsActor a, Flag flag, bool on)
    {
        a.Phantom = on;
        a.SetVolumeDetect(on && flag == Flag.VolumeDetect ? 1 : 0);
    }

    // A falling phantom or volume-detect box is selected: it stops where it is and stays there, also while the flag is
    // switched off and on again (the body changes layer in place, and a physical body is woken for that). Let go, it falls
    // again from rest.
    [Theory]
    [InlineData(45.0, Flag.Phantom)]
    [InlineData(45.0, Flag.VolumeDetect)]
    [InlineData(0.0, Flag.Phantom)]
    [InlineData(0.0, Flag.VolumeDetect)]
    public void A_selected_phantom_or_volume_detect_box_holds_still(double physicsHz, Flag flag)
    {
        const double SelectAt = 0.5, OffAt = 1.5, OnAt = 2.0, ReleaseAt = 3.5;
        Vector3 heldAt = Vector3.Zero;
        double selectedT = double.NaN, releasedT = double.NaN;
        bool switchedOff = false, switchedOn = false;
        var held = new List<Point>();
        List<Point> trace = Run(physicsHz, 5f,
            r => { r.AddBox(Unit, High, Quaternion.Identity); SetFlag(r.Actor, flag, true); },
            (r, now) =>
            {
                PhysicsActor a = r.Actor;
                if (double.IsNaN(selectedT))
                {
                    if (now >= SelectAt)
                    {
                        Select(true, a);
                        selectedT = now;
                        heldAt = a.Position;
                    }
                    return;
                }
                if (!double.IsNaN(releasedT))
                    return;
                held.Add(new Point(now, a.Position, a.Velocity, a.Orientation, r.AwakeBodies));
                if (!switchedOff && now >= OffAt) { SetFlag(a, flag, false); switchedOff = true; }
                if (!switchedOn && now >= OnAt) { SetFlag(a, flag, true); switchedOn = true; }
                if (now >= ReleaseAt)
                {
                    Select(false, a);
                    releasedT = now;
                }
            });

        Assert.False(double.IsNaN(releasedT), "the run never let the box go");
        Assert.True(switchedOff && switchedOn, "the flag was not switched while selected");
        Assert.True(held[^1].T - held[0].T >= 2.9, "not held for 3 s");
        foreach (Point p in held)
        {
            Assert.True(Vector3.Distance(p.Position, heldAt) < 0.001f, $"moved while selected at t={p.T:0.00}: {p.Position} (held at {heldAt})");
            Assert.True(p.Velocity.Length() < 1e-4f, $"reported moving while selected at t={p.T:0.00}: {p.Velocity}");
        }

        // Let go: it falls again from rest, g t^2 / 2 = 4.9 m in the first second.
        Point at = trace.First(p => p.T >= releasedT + 1.0 - 1e-9);
        Assert.InRange(heldAt.Z - at.Position.Z, 4.0f, 5.2f);
        Assert.True(at.Velocity.Z < -8f, $"not falling a second after being let go (velocity {at.Velocity})");
    }

    // ---- a velocity set on a sleeping body ---------------------------------------------------------------------------

    public enum VelocityCall { Linear, Angular }

    // A box asleep on the ground is given a velocity: llSetVelocity (SceneObjectPart.SetVelocity, then the root actor's
    // Velocity) or llSetAngularVelocity (the root actor's RotationalVelocity). It wakes and moves at once, as ubODE's
    // changevelocity and changeangvelocity enable a disabled body, and settles and sleeps again.
    [Theory]
    [InlineData(45.0, VelocityCall.Linear)]
    [InlineData(45.0, VelocityCall.Angular)]
    [InlineData(0.0, VelocityCall.Linear)]
    [InlineData(0.0, VelocityCall.Angular)]
    public void A_sleeping_box_given_a_velocity_wakes_and_moves(double physicsHz, VelocityCall call)
    {
        int setAt = -1;
        Vector3 rest = Vector3.Zero;
        List<Point> trace = Run(physicsHz, 8f,
            r => r.AddBoxOnGround(Unit, 128f, 128f, Quaternion.Identity),
            (r, now) =>
            {
                if (setAt >= 0 || r.Heartbeats < 5 || r.AwakeBodies != 0)
                    return;
                setAt = r.Heartbeats;
                rest = r.Actor.Position;
                if (call == VelocityCall.Linear)
                    r.Actor.Velocity = new Vector3(3f, 0f, 0f);
                else
                    r.Actor.RotationalVelocity = new Vector3(0f, 0f, 3f);
            });

        Assert.True(setAt > 0, "the box never went to sleep");
        Assert.Equal(0, trace[setAt].Awake);
        Point next = trace[setAt + 1];
        Assert.True(next.Awake > 0, "the velocity did not wake it");
        if (call == VelocityCall.Linear)
            Assert.True(next.Position.X - rest.X > 0.1f, $"it did not move along x in the heartbeat after the set ({next.Position}, rest {rest})");
        else
        {
            // A turn about z: its x axis has swung away from where it rested.
            float turned = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitX * next.Rotation, Vector3.UnitX), -1f, 1f));
            Assert.True(turned > 0.1f, $"it did not turn in the heartbeat after the set ({turned:0.000} rad)");
        }
        Point end = trace[^1];
        Assert.Equal(0, end.Awake);   // friction stopped it, and the engine put it to sleep again
        Assert.True(MathF.Abs(end.Position.Z - rest.Z) < 0.02f, $"not resting on the ground (z {end.Position.Z}, rest {rest.Z})");
    }
}
