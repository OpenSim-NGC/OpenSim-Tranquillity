/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Turning a physical object with the mouse (Ctrl-Shift drag), through core's own spin path against a Jolt scene.
//
// What the viewer sends (LLToolGrabBase in the viewer's lltoolgrab.cpp): ObjectSpinStart when the spin begins, which starts
// the viewer's own rotation at the root's current rotation; then, only on a hover event where the mouse moved, one
// ObjectSpinUpdate carrying that rotation (the mouse's pixels, 0.01 rad each, composed onto it in the object's own frame),
// with no timer; and ObjectSpinStop when the keys or the button are let go.
// What core does: SceneObjectGroup.SpinMovement takes the rotation between two successive updates, its axis and its sign,
// and asks the root's actor for one angular force of (axis x the root part's mass x 0.1) with pushforce true, whatever the
// angle; SpinStart only marks the root as waiting for the first update, and Scene.ProcessSpinObjectStop does nothing.
//
// These scenarios call those handlers on a group whose root part holds the Jolt actor, so the call is core's own. Nothing
// is tuned: they record what happens today, and the tests built on them assert only that the run finishes and nothing is
// not-a-number. The figures a run gives are in <see cref="SpinMeasure"/>.
//
// Modelling choices, each of them a choice and not a measurement of the viewer:
// - The viewer's update rate is <see cref="ViewerHz"/> while the mouse moves (the viewer sends one per mouse event).
// - The cursor's turn follows a smooth start and stop (smoothstep) of <see cref="SpinPlan.TotalDegrees"/> about the object's
//   own vertical axis, taking <see cref="SpinPlan.DragSeconds"/>, starting at <see cref="SpinPlan.DragStart"/>.
// - After the turn the mouse is held still, so the viewer sends nothing (core would ignore an update that turned nothing).
// - A script's look-at is run once per heartbeat, before the update and the step: the region's frame runs it through
//   SceneObjectGroup.Update right after the physics step, so it overwrites the angular velocity the step left.

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>The cursor's turn, the release and the script's calls, for one spin run.</summary>
public sealed class SpinPlan
{
    public string Kind;
    public float TotalDegrees = 90f;
    public double DragStart = 0.5;
    public double DragSeconds = 1.0;
    /// <summary>The spin is let go at this time (NaN = the cursor stops and the run just goes on).</summary>
    public double ReleaseAt = double.NaN;
    /// <summary>The viewer keeps sending updates after the cursor stops (the same rotation again).</summary>
    public bool UpdatesWhileHeld;
    public float Mass;
    /// <summary>Where the angle the run is judged against ends: the cursor's turn, or the script's look-at target.</summary>
    public float ReferenceDegrees = 90f;
    /// <summary>A script's llRotLookAt (to rotation of <see cref="LookAtDegrees"/> about the vertical axis) from <see cref="ScriptAt"/>.</summary>
    public bool LookAt;
    public float LookAtDegrees;
    public float LookAtStrength = 1f;
    public double ScriptAt;
    /// <summary>A script's llTargetOmega about the vertical axis, at this rate (rad/s), from <see cref="ScriptAt"/>.</summary>
    public float Omega;
    /// <summary>No drag at all: the script's call alone.</summary>
    public bool NoDrag;
    /// <summary>The script's llRotLookAt is called on a child part of the linkset (core hands it on to the root).</summary>
    public bool ViaChild;
    /// <summary>The mouse's move drag runs with the rotate-drag: the cursor carries the object this many metres up, over the same seconds.</summary>
    public float LiftMetres;
    internal Vector3 LiftFrom;
    /// <summary>Not core's spin path: each update sets the root's look-at target (core's own llRotLookAt call) to the rotation the viewer
    /// sent, and the frame runs UpdateLookAt, to record what that controller does with the same cursor; the release stops it.</summary>
    public bool DragViaLookAt;
    public int Sent;
    /// <summary>Whether core's own look-at controller ever set a non-zero angular velocity on the root part during the run.</summary>
    public bool CoreWroteSpin;
    internal int NextUpdate;
    internal bool Started, Stopped, ScriptOn;
    internal CoreGrab Core;

    public double DragEnd => DragStart + DragSeconds;

    /// <summary>The cursor's turn at time t (degrees).</summary>
    public float Ideal(double t)
    {
        double s = Math.Clamp((t - DragStart) / DragSeconds, 0.0, 1.0);
        return TotalDegrees * (float)(s * s * (3.0 - 2.0 * s));
    }
}

/// <summary>What a spin run did, from its samples (the object's yaw and angular velocity at each heartbeat).</summary>
public sealed class SpinMeasure
{
    public string Run;
    public float Mass;
    public int Updates;
    /// <summary>The farthest the object was behind the cursor's turn while the cursor moved (degrees).</summary>
    public float MaxLag;
    /// <summary>How far past <see cref="SpinPlan.ReferenceDegrees"/> it got, at most (degrees; 0 = never past it).</summary>
    public float Overshoot;
    public float EndYaw, MaxYaw, MinYaw;
    public float EndError;
    /// <summary>After the cursor stopped: whether it came to rest within 2 degrees of the reference and stayed, and when.</summary>
    public bool Settled;
    public double SettleSeconds = double.NaN;
    /// <summary>Seconds after the cursor stopped at which the object first reached the reference (negative = before it stopped; NaN = never).</summary>
    public double ReachSeconds = double.NaN;
    public float TopDegPerSec, AtDragEndDegPerSec, EndDegPerSec;
    /// <summary>Release runs: the yaw and speed at the release, how far it turned after, and how long it took to drop under 6 degrees a second (NaN = still above at the end).</summary>
    public float YawAtRelease, DegPerSecAtRelease, TurnAfterRelease;
    public double SpinSecondsAfterRelease = double.NaN;
    public bool NotANumber;
    /// <summary>Whether core's own controller wrote an angular velocity on the root part (it does not when the engine has the target).</summary>
    public bool CoreWroteSpin;
    /// <summary>After the cursor stopped: the most the yaw went back from the highest it had reached (degrees), sampled at the heartbeat.</summary>
    public float Twitch;
    /// <summary>How far the object's centre ended above where it started (m).</summary>
    public float Rise;

    public static string Header => "run,updates,mass,lag_deg,overshoot_deg,end_yaw_deg,end_err_deg,max_yaw_deg,min_yaw_deg,settled,settle_s,top_dps,dps_at_drag_end,dps_end,yaw_at_release,dps_at_release,turn_after_release_deg,spin_s_after_release,reach_s_after_stop,nan,core_wrote_spin,twitch_deg,rise_m";

    public string Row()
    {
        string F(float v) => float.IsNaN(v) ? "" : v.ToString("0.0", CultureInfo.InvariantCulture);
        string D(double v) => double.IsNaN(v) ? "" : v.ToString("0.00", CultureInfo.InvariantCulture);
        return string.Join(",", Run, Updates.ToString(CultureInfo.InvariantCulture), Mass.ToString("0.0", CultureInfo.InvariantCulture), F(MaxLag), F(Overshoot), F(EndYaw), F(EndError), F(MaxYaw), F(MinYaw),
                           Settled ? "1" : "0", D(SettleSeconds), F(TopDegPerSec), F(AtDragEndDegPerSec), F(EndDegPerSec), F(YawAtRelease), F(DegPerSecAtRelease), F(TurnAfterRelease), D(SpinSecondsAfterRelease), D(ReachSeconds), NotANumber ? "1" : "0",
                           CoreWroteSpin ? "1" : "0", F(Twitch), F(Rise));
    }
}

public static class SpinScenarios
{
    /// <summary>How many ObjectSpinUpdate packets a second the viewer sends while the mouse moves.</summary>
    public static float ViewerHz = 30f;

    private const float X = 128f, Y = 128f;
    private const uint RootId = 1501, LeftId = 1502, RightId = 1503;

    [ThreadStatic] private static SpinPlan _last;

    public static SpinPlan PlanOf(Run r) => _last;

    /// <summary>An object standing clear of the ground is held where it is by buoyancy 1 (the weight cancelled), so that
    /// nothing but the spin acts on it; the same object on the ground is held by friction as well.</summary>
    private const float AirHeight = 20f;

    private static SpinPlan Begin(Run r, string kind, float size, float mass, bool air = false, float? friction = null, double dragSeconds = 1.0)
    {
        float z = r.GroundAt(X, Y) + size * 0.5f + 0.02f + (air ? AirHeight : 0f);
        var plan = new SpinPlan { Kind = kind, DragSeconds = dragSeconds };
        _last = plan;
        PhysicsActor a = r.AddBox(new Vector3(size, size, size), new Vector3(X, Y, z), Quaternion.Identity);
        if (air)
            a.Buoyancy = 1f;
        if (friction is float f)
            a.Friction = f;
        // A 1 m cube's mass is its density / 100 in the scene's units.
        a.Density = mass * 100f / (size * size * size);
        plan.Mass = a.Mass;
        plan.Core = new CoreGrab(a);
        plan.Core.UsePhysicsScene(r.PhysicsScene);
        r.Cleanups.Add(plan.Core.Release);
        return plan;
    }

    private static Quaternion Turn(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    private static void Feed(Run r)
    {
        SpinPlan p = PlanOf(r);
        CoreGrab core = p.Core;
        // The region's frame: a script's look-at, then the updates that arrived since, then the step.
        if (!p.ScriptOn && (p.LookAt || p.Omega != 0f) && r.Now >= p.ScriptAt - 1e-9)
        {
            p.ScriptOn = true;
            if (p.LookAt)
            {
                core.EnableScriptCalls();
                if (p.ViaChild)
                    core.Child(0).RotLookAt(Turn(p.LookAtDegrees), p.LookAtStrength, 0.5f);
                else
                    core.Group.RotLookAt(Turn(p.LookAtDegrees), p.LookAtStrength, 0.5f);
            }
            if (p.Omega != 0f)
            {
                // llTargetOmega on a physical prim ends in SceneObjectPart.UpdateAngularVelocity: during a rotate-drag the group
                // only remembers the spin, otherwise it sets the part's AngularVelocity (the root's actor takes it as its
                // rotational velocity) and schedules a terse update.
                core.Root.UpdateAngularVelocity(Vector3.UnitZ * p.Omega);
            }
        }
        // The region's frame runs the root's look-at pass for a look-at or a rotate-drag that is held; when the scene
        // says the engine has a rotation target, that pass hands the target to the actor.
        if (p.Started || (p.ScriptOn && p.LookAt))
            core.UpdateLookAt();
        if (core.CorePartAngularVelocity != Vector3.Zero)
            p.CoreWroteSpin = true;
        if (p.Omega != 0f)
        {
            // The region's terse update reads the part's angular velocity from the actor each frame (the getter refreshes the
            // part's own copy), and UpdateAngularVelocity writes only a value that differs from that copy.
            _ = core.Root.AngularVelocity;
        }
        if (p.NoDrag)
            return;
        double until = r.Now + 1e-9;
        while (true)
        {
            double t = p.NextUpdate / (double)ViewerHz;
            if (t > until)
                break;
            p.NextUpdate++;
            if (t < p.DragStart)
                continue;
            if (!double.IsNaN(p.ReleaseAt) && t > p.ReleaseAt)
            {
                if (!p.Stopped)
                {
                    p.Stopped = true;
                    if (p.DragViaLookAt)
                        core.Group.StopLookAt();
                    else
                        core.SpinStop();
                }
                continue;
            }
            if (!p.Started)
            {
                p.Started = true;
                if (p.DragViaLookAt)
                    core.EnableScriptCalls();
                else
                    core.SpinStart();
            }
            if (!p.UpdatesWhileHeld && t > p.DragEnd)
                continue;
            if (p.DragViaLookAt)
                core.Group.RotLookAt(Turn(p.Ideal(t)), p.LookAtStrength, 0.5f);
            else
            {
                if (p.LiftMetres != 0f)
                {
                    // the mouse's move drag, at the same update: the object's centre carried up with the cursor
                    float lift = p.LiftMetres * p.Ideal(t) / p.TotalDegrees;
                    core.Update(core.PartId(), p.LiftFrom + new Vector3(0f, 0f, lift));
                }
                core.SpinUpdate(Turn(p.Ideal(t)));
            }
            p.Sent++;
        }
    }

    private static Scenario Cube(string name, string description, float size, float mass, double release = double.NaN, bool held = false, bool air = false,
                                 float? friction = null, double dragSeconds = 1.0, bool viaLookAt = false, float strength = 1f)
        => new()
        {
            Name = air ? name + "-air" : name,
            Description = description + (air ? " Clear of the ground, buoyancy 1." : friction == 0f ? " Resting on the ground, friction 0." : " Resting on the ground."),
            DefaultDuration = _ => 7f,
            Setup = r =>
            {
                SpinPlan p = Begin(r, "box", size, mass, air, friction, dragSeconds);
                p.ReleaseAt = release;
                p.UpdatesWhileHeld = held;
                p.DragViaLookAt = viaLookAt;
                p.LookAtStrength = strength;
            },
            Input = Feed,
        };

    private static Scenario Linkset(bool air = false, bool viaLookAt = false) => new()
    {
        Name = (viaLookAt ? "spin-lookatdrag-linkset" : "spin-linkset") + (air ? "-air" : ""),
        Description = (viaLookAt ? "Not core's spin path: the root's look-at is set to the cursor's rotation at each update. " : "")
                      + "Three 1 m cubes in a row along x (10 kg each) turned 90 degrees in 1 s about the vertical axis, then the mouse still 5.5 s."
                      + (air ? " Clear of the ground, buoyancy 1." : " Resting on the ground."),
        DefaultDuration = _ => 7f,
        Setup = r => LinksetSetup(r, air, viaLookAt),
        Input = Feed,
    };

    private static void LinksetSetup(Run r, bool air, bool viaLookAt)
    {
        const float cube = 1f;
        float z = r.GroundAt(X, Y) + cube * 0.5f + 0.02f + (air ? AirHeight : 0f);
        var unit = new Vector3(cube, cube, cube);
        PhysicsActor root = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), unit, new Vector3(X, Y, z), Quaternion.Identity, true, RootId);
        if (air)
            root.Buoyancy = 1f;
        r.ActorSize = unit;
        PhysicsActor left = r.AddPart(PrimitiveBaseShape.CreateBox(), unit, new Vector3(X - 1f, Y, z), Quaternion.Identity, true, LeftId, root);
        PhysicsActor right = r.AddPart(PrimitiveBaseShape.CreateBox(), unit, new Vector3(X + 1f, Y, z), Quaternion.Identity, true, RightId, root);
        var plan = new SpinPlan { Kind = "linkset", DragViaLookAt = viaLookAt };
        _last = plan;
        plan.Mass = root.Mass;
        plan.Core = new CoreGrab(root, left, right);
        plan.Core.UsePhysicsScene(r.PhysicsScene);
        r.Cleanups.Add(plan.Core.Release);
    }

    /// <summary>A script's call on the same object as the drag.</summary>
    private static Scenario Script(string name, string description, Action<SpinPlan> script, bool noDrag = false, bool air = true)
        => new()
        {
            Name = name,
            Description = description + (air ? " Clear of the ground, buoyancy 1." : " Resting on the ground."),
            DefaultDuration = _ => 7f,
            Setup = r =>
            {
                SpinPlan p = Begin(r, "box", 1f, 1f, air: air);
                p.NoDrag = noDrag;
                script(p);
            },
            Input = Feed,
        };

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        Cube("spin-90-1kg", "A 1 kg cube turned 90 degrees in 1 s through core's spin path, then the mouse still 5.5 s.", 1f, 1f),
        Cube("spin-90-100kg", "A 100 kg cube turned 90 degrees in 1 s through core's spin path, then the mouse still 5.5 s.", 1f, 100f),
        Cube("spin-90-1kg-held", "As spin-90-1kg, but the viewer keeps sending the same rotation after the cursor stops.", 1f, 1f, held: true),
        Cube("spin-90-4m-cube", "A 100 kg cube 4 m on a side turned 90 degrees in 1 s through core's spin path, then the mouse still 5.5 s.", 4f, 100f),
        Cube("spin-90-1kg-nofriction", "A 1 kg cube with friction 0 turned 90 degrees in 1 s through core's spin path, then the mouse still 5.5 s.", 1f, 1f, friction: 0f),
        Cube("spin-90-1kg-held", "As spin-90-1kg, but the viewer keeps sending the same rotation after the cursor stops.", 1f, 1f, held: true, air: true),
        Cube("spin-90-slow3-1kg", "A 1 kg cube turned 90 degrees slowly, in 3 s, through core's spin path.", 1f, 1f, air: true, dragSeconds: 3.0),
        Cube("spin-90-slow6-1kg", "A 1 kg cube turned 90 degrees very slowly, in 6 s, through core's spin path.", 1f, 1f, air: true, dragSeconds: 6.0),
        Cube("spin-90-1kg", "A 1 kg cube turned 90 degrees in 1 s through core's spin path, then the mouse still 5.5 s.", 1f, 1f, air: true),
        Cube("spin-90-100kg", "A 100 kg cube turned 90 degrees in 1 s through core's spin path, then the mouse still 5.5 s.", 1f, 100f, air: true),
        Cube("spin-90-4m-cube", "A 100 kg cube 4 m on a side turned 90 degrees in 1 s through core's spin path, then the mouse still 5.5 s.", 4f, 100f, air: true),
        Cube("spin-release-1kg", "A 1 kg cube turned 90 degrees and let go (ObjectSpinStop) as the cursor stops, while still turning; 5 s watched.", 1f, 1f, release: 1.5, air: true),
        Cube("spin-release-mid-1kg", "A 1 kg cube let go (ObjectSpinStop) half way through the turn; 5.5 s watched.", 1f, 1f, release: 1.0, air: true),
        Cube("spin-release-mid-100kg", "A 100 kg cube let go (ObjectSpinStop) half way through the turn; 5.5 s watched.", 1f, 100f, release: 1.0, air: true),
        Cube("spin-90-slow6-1kg", "A 1 kg cube turned 90 degrees very slowly, in 6 s, through core's spin path.", 1f, 1f, dragSeconds: 6.0),
        Cube("spin-release-1kg", "A 1 kg cube turned 90 degrees and let go (ObjectSpinStop) as the cursor stops; 5 s watched.", 1f, 1f, release: 1.5),
        Cube("spin-release-mid-1kg", "A 1 kg cube let go (ObjectSpinStop) half way through the turn; 5.5 s watched.", 1f, 1f, release: 1.0),
        new Scenario
        {
            Name = "spin-with-grab",
            Description = "A 1 kg cube on the ground carried 2 m up by the mouse's move drag while the rotate-drag turns it 90 degrees in 1 s, then both held 5.5 s.",
            DefaultDuration = _ => 7f,
            Setup = r =>
            {
                SpinPlan p = Begin(r, "box", 1f, 1f);
                p.LiftMetres = 2f;
                p.LiftFrom = r.Actor.Position;
            },
            Input = Feed,
        },
        Linkset(),
        Linkset(air: true),
        Script("spin-lookat-s1", "A script's llRotLookAt (back to the start rotation, strength 1) held on the 1 kg cube while the cursor turns it 90 degrees.",
               p => { p.LookAt = true; p.LookAtDegrees = 0f; p.LookAtStrength = 1f; p.ReferenceDegrees = 0f; }),
        Script("spin-lookat-s5", "As spin-lookat-s1 with strength 5.",
               p => { p.LookAt = true; p.LookAtDegrees = 0f; p.LookAtStrength = 5f; p.ReferenceDegrees = 0f; }),
        Script("spin-lookat-alone", "A script's llRotLookAt to 90 degrees (strength 1) alone, with no drag.",
               p => { p.LookAt = true; p.LookAtDegrees = 90f; p.LookAtStrength = 1f; p.ReferenceDegrees = 90f; }, noDrag: true),
        Script("spin-lookat-alone-ground", "A script's llRotLookAt to 90 degrees (strength 1) alone, with no drag.",
               p => { p.LookAt = true; p.LookAtDegrees = 90f; p.LookAtStrength = 1f; p.ReferenceDegrees = 90f; }, noDrag: true, air: false),
        Cube("spin-lookatdrag-1kg", "Not core's spin path: the root's look-at (strength 1) is set to the cursor's rotation at each update, and stopped on the release. A 1 kg cube turned 90 degrees in 1 s, then the mouse still 5.5 s.",
             1f, 1f, air: true, viaLookAt: true),
        Cube("spin-lookatdrag-1kg", "As spin-lookatdrag-1kg.", 1f, 1f, viaLookAt: true),
        Cube("spin-lookatdrag-100kg", "As spin-lookatdrag-1kg, a 100 kg cube.", 1f, 100f, air: true, viaLookAt: true),
        Cube("spin-lookatdrag-4m-cube", "As spin-lookatdrag-1kg, a 100 kg cube 4 m on a side.", 4f, 100f, air: true, viaLookAt: true),
        Cube("spin-lookatdrag-s2-1kg", "As spin-lookatdrag-1kg with strength 2.", 1f, 1f, air: true, viaLookAt: true, strength: 2f),
        Cube("spin-lookatdrag-release-1kg", "As spin-lookatdrag-1kg, let go as the cursor stops; 5 s watched.", 1f, 1f, release: 1.5, air: true, viaLookAt: true),
        Cube("spin-lookatdrag-release-mid-1kg", "As spin-lookatdrag-1kg, let go half way through the turn; 5.5 s watched.", 1f, 1f, release: 1.0, air: true, viaLookAt: true),
        Cube("spin-lookatdrag-slow6-1kg", "As spin-lookatdrag-1kg, turned slowly, in 6 s.", 1f, 1f, air: true, viaLookAt: true, dragSeconds: 6.0),
        Linkset(air: true, viaLookAt: true),
        Linkset(viaLookAt: true),
        Script("spin-omega-ground", "A script's llTargetOmega of 1 rad/s about the vertical axis on the 1 kg cube on the ground while the cursor turns it 90 degrees.",
               p => { p.Omega = 1f; p.ReferenceDegrees = float.NaN; }, air: false),
        new Scenario
        {
            Name = "spin-lookat-linkset-child",
            Description = "Three 1 m cubes in a row along x (10 kg each) on the ground, a script's llRotLookAt to 90 degrees (strength 1) called on a child part, no drag.",
            DefaultDuration = _ => 7f,
            Setup = r =>
            {
                LinksetSetup(r, air: false, viaLookAt: false);
                SpinPlan p = _last;
                p.NoDrag = true;
                p.LookAt = true;
                p.LookAtDegrees = 90f;
                p.LookAtStrength = 1f;
                p.ReferenceDegrees = 90f;
                p.ViaChild = true;
            },
            Input = Feed,
        },
        Script("spin-omega-release", "A script's llTargetOmega of 1 rad/s about the vertical axis on the 1 kg cube while the cursor turns it 90 degrees, and the turn let go as the cursor stops: the spin is applied again.",
               p => { p.Omega = 1f; p.ReferenceDegrees = float.NaN; p.ReleaseAt = 1.5; }),
        Script("spin-omega-release-ground", "As spin-omega-release, resting on the ground.",
               p => { p.Omega = 1f; p.ReferenceDegrees = float.NaN; p.ReleaseAt = 1.5; }, air: false),
        Script("spin-omega", "A script's llTargetOmega of 1 rad/s about the vertical axis on the 1 kg cube while the cursor turns it 90 degrees.",
               p => { p.Omega = 1f; p.ReferenceDegrees = float.NaN; }),
        Script("spin-omega-alone", "A script's llTargetOmega of 1 rad/s about the vertical axis alone, with no drag.",
               p => { p.Omega = 1f; p.ReferenceDegrees = float.NaN; }, noDrag: true),
    };

    // The yaw of the object's own x axis in the region, unwrapped across samples (a turn can pass a full circle).
    private static float[] Yaws(List<Sample> ss)
    {
        var yaw = new float[ss.Count];
        float prev = 0f, total = 0f;
        for (int i = 0; i < ss.Count; i++)
        {
            Vector3 x = Vector3.UnitX * ss[i].Rotation;
            float raw = MathF.Atan2(x.Y, x.X) * 180f / MathF.PI;
            float step = raw - prev;
            while (step > 180f) step -= 360f;
            while (step < -180f) step += 360f;
            total += step;
            prev = raw;
            yaw[i] = total;
        }
        return yaw;
    }

    /// <summary>What the run did, from its samples.</summary>
    public static SpinMeasure Measure(RunResult res)
    {
        SpinPlan p = _last;
        List<Sample> ss = res.Samples;
        var m = new SpinMeasure { Run = $"{res.Scenario}@{res.RateHz:0.#}Hz/{ViewerHz:0.#}upd/{(res.PhysicsRateHz > 0 ? res.PhysicsRateHz.ToString("0.#") + "Hz" : "heartbeat")}", Mass = p.Mass, Updates = p.Sent };
        float[] yaw = Yaws(ss);
        float reference = p.ReferenceDegrees;
        double lastBad = double.NaN;
        double lastT = ss[^1].T;
        bool released = !double.IsNaN(p.ReleaseAt);
        int releaseIdx = -1;
        double lastFast = double.NaN;
        m.MaxYaw = float.MinValue;
        m.MinYaw = float.MaxValue;
        m.CoreWroteSpin = p.CoreWroteSpin;
        m.Rise = ss[^1].Position.Z - ss[0].Position.Z;
        float runningMax = float.MinValue;
        for (int i = 0; i < ss.Count; i++)
        {
            Sample s = ss[i];
            if (!float.IsFinite(s.Position.X) || !float.IsFinite(s.Rotation.X) || !float.IsFinite(s.Rotation.W) || !float.IsFinite(s.AngularVelocity.X) || !float.IsFinite(s.AngularVelocity.Z))
            {
                m.NotANumber = true;
                continue;
            }
            float dps = s.AngularVelocity.Length() * 180f / MathF.PI;
            m.TopDegPerSec = MathF.Max(m.TopDegPerSec, dps);
            m.MaxYaw = MathF.Max(m.MaxYaw, yaw[i]);
            m.MinYaw = MathF.Min(m.MinYaw, yaw[i]);
            if (!p.NoDrag && s.T > p.DragStart && s.T <= p.DragEnd + 1e-9)
                m.MaxLag = MathF.Max(m.MaxLag, p.Ideal(s.T) - yaw[i]);
            if (!float.IsNaN(reference))
                m.Overshoot = MathF.Max(m.Overshoot, yaw[i] - reference);
            if (s.T > p.DragEnd)
            {
                runningMax = MathF.Max(runningMax, yaw[i]);
                m.Twitch = MathF.Max(m.Twitch, runningMax - yaw[i]);
            }
            if (s.T >= p.DragEnd - 1e-9 && m.AtDragEndDegPerSec == 0f)
                m.AtDragEndDegPerSec = dps;
            if (!p.NoDrag && !float.IsNaN(reference) && double.IsNaN(m.ReachSeconds) && s.T > p.DragStart && yaw[i] >= reference - 2f)
                m.ReachSeconds = s.T - p.DragEnd;
            if (s.T > p.DragEnd && !float.IsNaN(reference))
            {
                bool away = MathF.Abs(yaw[i] - reference) > 2f || dps > 3f;
                if (away)
                    lastBad = s.T;
            }
            if (released && releaseIdx < 0 && s.T >= p.ReleaseAt)
            {
                releaseIdx = i;
                m.YawAtRelease = yaw[i];
                m.DegPerSecAtRelease = dps;
            }
            if (released && releaseIdx >= 0 && dps >= 6f)
                lastFast = s.T;
        }
        m.EndYaw = yaw[^1];
        m.EndDegPerSec = ss[^1].AngularVelocity.Length() * 180f / MathF.PI;
        if (!float.IsNaN(reference))
        {
            m.EndError = m.EndYaw - reference;
            m.Settled = double.IsNaN(lastBad) || lastBad < lastT - 1e-9;
            m.SettleSeconds = double.IsNaN(lastBad) ? 0.0 : (m.Settled ? lastBad - p.DragEnd : double.NaN);
        }
        if (released && releaseIdx >= 0)
        {
            m.TurnAfterRelease = m.EndYaw - m.YawAtRelease;
            m.SpinSecondsAfterRelease = lastFast >= lastT - 1e-9 ? double.NaN : (double.IsNaN(lastFast) ? 0.0 : lastFast - p.ReleaseAt);
        }
        return m;
    }
}
