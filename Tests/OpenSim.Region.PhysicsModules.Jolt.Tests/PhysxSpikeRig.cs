/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Reflection;
using JoltPhysicsSharp;
using Nini.Config;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using JVehicle = OpenSim.Region.PhysicsModules.Jolt.Vehicles.Vehicle;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Omv = OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

// The rig the spike tests (PhysxSpikeTests) run on: a real scene from StepRateRig, a Jolt step listener that records one
// row of a body's state before every collision step, and the metrics worked out from those rows. A one-step spike is
// therefore not averaged away by the 11 Hz heartbeat. The listener only reads (through the no-lock interface, which is all
// OnStep may use); it changes nothing.

/// <summary>A body's state at the start of one collision step.</summary>
internal readonly record struct StepRow(double T, float Dt, Vector3 P, Vector3 V, Vector3 W, Quaternion Q);

internal sealed class StepTrace : PhysicsStepListener
{
    private readonly PhysicsSystem _system;
    private readonly BodyID _id;
    private double _t;
    private readonly object _gate = new();
    public readonly List<StepRow> Rows = new();

    public StepTrace(PhysicsSystem system, BodyID id, double startTime) { _system = system; _id = id; _t = startTime; }

    protected override void OnStep(in PhysicsStepListenerContext context)
    {
        BodyInterface bi = context.System.BodyInterfaceNoLock;
        var row = new StepRow(_t, context.DeltaTime, bi.GetPosition(_id), bi.GetLinearVelocity(_id), bi.GetAngularVelocity(_id), bi.GetRotation(_id));
        lock (_gate) Rows.Add(row);
        _t += context.DeltaTime;
    }

    public void Detach()
    {
        _system.RemoveStepListener(this);
        Dispose();
    }
}

/// <summary>What the rows of a window say. Accelerations are per collision step; energy is the kinetic energy of the body.</summary>
internal readonly record struct SpikeMetrics(
    int Steps,
    double MaxDvz,          // largest |change of vz| / step time, m/s^2
    double MaxDwy,          // largest |change of the y angular velocity| / step time, rad/s^2
    double MaxDw,           // the same for the whole angular velocity vector
    double MaxVz,           // largest |vz|
    double MaxInjection,    // largest energy the contacts put in during one step: change of kinetic energy minus the work of gravity and the applied force, J
    double MaxInjectionShare, // the same against the kinetic energy at the start of the step, floored at the cruising energy
    double MaxSpeed,
    double MaxDvz15,        // largest |change of vz| over a window of 15 ms (one PhysX step of the vehicle code), / the window, m/s^2
    double MaxUp15,         // the same, upward changes only (the vehicle code's linear check looks only at those)
    double MaxDwy15,        // largest |change of the y angular velocity| over 15 ms, rad/s^2
    double MaxTiltDegrees); // largest angle the body turned about y away from its start orientation

internal static class SpikeMath
{
    // Kinetic energy of a box body: translation, plus rotation through the body-frame inertia diagonal of a box.
    public static double Kinetic(StepRow r, double mass, Vector3 size)
    {
        double ke = 0.5 * mass * r.V.LengthSquared();
        Vector3 wl = Vector3.Transform(r.W, Quaternion.Inverse(r.Q));
        double ix = mass / 12.0 * (size.Y * size.Y + size.Z * size.Z);
        double iy = mass / 12.0 * (size.X * size.X + size.Z * size.Z);
        double iz = mass / 12.0 * (size.X * size.X + size.Y * size.Y);
        return ke + 0.5 * (ix * wl.X * wl.X + iy * wl.Y * wl.Y + iz * wl.Z * wl.Z);
    }

    // `fromT` .. `toT` is the window. `gravity` is the body's own gravity acceleration (g times its multiplier); `force`
    // the world-frame force a script was applying at a time. Both do work that the contacts must not be credited for.
    public static SpikeMetrics Measure(List<StepRow> rows, double fromT, double toT, double mass, Vector3 size, Vector3 gravity, Func<double, Vector3> forceAt, double floorEnergy)
    {
        int steps = 0;
        double dvz = 0, dwy = 0, dw = 0, vz = 0, inj = 0, share = 0, speed = 0, dvz15 = 0, up15 = 0, dwy15 = 0, tilt = 0;
        Quaternion q0 = default;
        bool q0Set = false;
        for (int i = 0; i + 1 < rows.Count; i++)
        {
            StepRow a = rows[i], b = rows[i + 1];
            if (a.T < fromT - 1e-9 || b.T > toT + 1e-9) continue;
            if (!q0Set) { q0 = a.Q; q0Set = true; }
            Quaternion rel = Quaternion.Concatenate(Quaternion.Inverse(q0), a.Q);
            tilt = Math.Max(tilt, 2.0 * Math.Asin(Math.Min(1.0, Math.Abs(rel.Y))) * 180.0 / Math.PI);
            // The window: the first later row at least 15 ms on.
            for (int j = i + 1; j < rows.Count && rows[j].T <= toT + 1e-9; j++)
            {
                double span = rows[j].T - a.T;
                if (span < 0.015 - 1e-9) continue;
                double dz = (rows[j].V.Z - a.V.Z) / span;
                dvz15 = Math.Max(dvz15, Math.Abs(dz));
                up15 = Math.Max(up15, dz);
                dwy15 = Math.Max(dwy15, Math.Abs(rows[j].W.Y - a.W.Y) / span);
                break;
            }
            double dt = a.Dt;
            steps++;
            dvz = Math.Max(dvz, Math.Abs(b.V.Z - a.V.Z) / dt);
            dwy = Math.Max(dwy, Math.Abs(b.W.Y - a.W.Y) / dt);
            dw = Math.Max(dw, (b.W - a.W).Length() / dt);
            vz = Math.Max(vz, Math.Abs(a.V.Z));
            speed = Math.Max(speed, a.V.Length());
            double e0 = Kinetic(a, mass, size), e1 = Kinetic(b, mass, size);
            double work = Vector3.Dot(forceAt(a.T) + (float)mass * gravity, b.P - a.P);
            double injected = e1 - e0 - work;
            inj = Math.Max(inj, injected);
            share = Math.Max(share, injected / Math.Max(e0, floorEnergy));
        }
        return new SpikeMetrics(steps, dvz, dwy, dw, vz, inj, share, speed, dvz15, up15, dwy15, tilt);
    }
}

/// <summary>A scene, its step traces and the helpers the spike tests share.</summary>
internal sealed class SpikeProbe : IDisposable
{
    public const float G = 9.80665f;
    private static readonly FieldInfo SystemField = typeof(JoltPhysicsBackend).GetField("_system", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo RecordsField = typeof(JoltPhysicsBackend).GetField("_joltToRecord", BindingFlags.NonPublic | BindingFlags.Instance);

    // The speculative contact distances the seam, rough-ground, tunnelling, overlap and gravity tests run with. PHYSX_SPECS="0;0.01" (the word "default" is the engine's own)
    // runs them with other values, to measure a candidate default; unset it is the configured default (null, 0.02), 0.0025 and 0.
    public static readonly float?[] Specs = ReadSpecs();

    /// <summary>The one value PHYSX_SPECS names (null if it names none or several): what the rough-ground drives run with.</summary>
    public static float? EnvSpec => Specs.Length == 1 ? Specs[0] : null;

    /// <summary>What the rough-ground drives run with: the configured default, which their bounds were set at, and 0.0025.</summary>
    public static float?[] T2Specs => Specs.Where(s => s != 0f).ToArray();

    private static float?[] ReadSpecs()
    {
        string env = Environment.GetEnvironmentVariable("PHYSX_SPECS");
        if (string.IsNullOrWhiteSpace(env)) return new float?[] { null, 0.0025f, 0f };
        return env.Split(';', StringSplitOptions.RemoveEmptyEntries)
                  .Select(t => t.Trim() == "default" ? (float?)null : float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    public readonly StepRateRig Rig;
    private readonly List<StepTrace> _traces = new();
    private uint _nextId = 1000;

    public SpikeProbe(double rate, int collisionSteps, Action<IConfig> more = null, float[] terrain = null, float? speculativeDistance = null)
    {
        Rig = new StepRateRig(rate, collisionSteps, more);
        if (speculativeDistance is float d) Tune(ps => { ps.SpeculativeContactDistance = d; return ps; });
        if (terrain != null)
        {
            Rig.Scene.SetTerrain(terrain);
            Rig.Scene.Simulate(StepRateRig.Heartbeat);
        }
    }

    public JoltScene Scene => Rig.Scene;

    /// <summary>Changes the engine's solver settings (a test of what a setting does to a spike).</summary>
    public void Tune(Func<PhysicsSettings, PhysicsSettings> change)
    {
        var system = (PhysicsSystem)SystemField.GetValue(Rig.Backend);
        system.Settings = change(system.Settings);
    }

    public double Time => Rig.Time;

    /// <summary>A static box (a prim that is not physical).</summary>
    public PhysicsActor Static(Omv.Vector3 size, Omv.Vector3 position, Omv.Quaternion? rotation = null)
        => Scene.AddPrimShape("static", OpenSim.Framework.PrimitiveBaseShape.CreateBox(), position, size, rotation ?? Omv.Quaternion.Identity, false, _nextId++);

    /// <summary>A physical box with gravity on and the module's default material.</summary>
    public JoltPrim Box(Omv.Vector3 size, Omv.Vector3 position, Omv.Quaternion? rotation = null, bool physical = true)
    {
        var prim = Assert.IsType<JoltPrim>(Scene.AddPrimShape("box", OpenSim.Framework.PrimitiveBaseShape.CreateBox(), position, size, rotation ?? Omv.Quaternion.Identity, physical, _nextId++));
        prim.Density = 1000f;
        return prim;
    }

    /// <summary>A car-type vehicle (no attractor, banking or deflection) that rolls on the ground; <see cref="Drive"/> sets its speed.</summary>
    public JoltPrim MakeVehicle(Omv.Vector3 size, Omv.Vector3 position)
    {
        JoltPrim v = Rig.AddBox(size, position, vehicle: true);
        Rig.Float(JVehicle.BUOYANCY, 0f);
        Rig.Float(JVehicle.LINEAR_MOTOR_TIMESCALE, 0.4f);
        Rig.Float(JVehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 120f);
        return v;
    }

    public void Drive(JoltPrim vehicle, float speed)
    {
        Assert.Same(vehicle, Rig.Prim);
        Rig.Vector(JVehicle.LINEAR_MOTOR_DIRECTION, new Omv.Vector3(speed, 0f, 0f));
    }

    public StepTrace Trace(JoltPrim prim)
    {
        var records = (System.Collections.IDictionary)RecordsField.GetValue(Rig.Backend);
        BodyID id = default;
        bool found = false;
        foreach (System.Collections.DictionaryEntry e in records)
        {
            object rec = e.Value;
            if ((uint)rec.GetType().GetField("Handle").GetValue(rec) == prim.BodyHandle.Value)
            {
                id = new BodyID((uint)rec.GetType().GetField("NativeBodyId").GetValue(rec));
                found = true;
            }
        }
        Assert.True(found, "the prim has no body in the backend");
        var system = (PhysicsSystem)SystemField.GetValue(Rig.Backend);
        var t = new StepTrace(system, id, Rig.Time);
        system.AddStepListener(t);
        _traces.Add(t);
        return t;
    }

    /// <summary>Heartbeats until the physics time reaches `seconds`; `act` runs before each, with the time about to run.</summary>
    public void RunTo(double seconds, Action<double> act = null)
    {
        while (Rig.Time < seconds - 1e-9)
        {
            act?.Invoke(Rig.Time);
            Rig.Scene.Simulate(StepRateRig.Heartbeat);
        }
        Assert.True(Rig.Backend.StepForceProviderFaults == 0, $"a step provider threw: {Rig.Backend.LastStepForceProviderFault}");
    }

    public void Dispose()
    {
        foreach (StepTrace t in _traces)
            t.Detach();
        Rig.Dispose();
    }

    /// <summary>The cells every spike test runs: 32, 45 and 64 Hz with 1 to 4 collision steps. The module keeps each solver
    /// step to 1/60 s or shorter, so 32 and 45 Hz cannot run with one (StepRateRig).</summary>
    public static IEnumerable<object[]> Cells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = rate >= 60 ? 1 : 2; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    public static float[] Terrain(Func<int, int, float> height)
    {
        var h = new float[StepRateRig.Size * StepRateRig.Size];
        for (int y = 0; y < StepRateRig.Size; y++)
            for (int x = 0; x < StepRateRig.Size; x++)
                h[y * StepRateRig.Size + x] = height(x, y);
        return h;
    }

    // A fixed pseudo-random value in [-1, 1) per cell, so a terrain is the same on every run.
    public static float Noise(int x, int y)
    {
        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xFFFF) / 32768f - 1f;
    }
}
