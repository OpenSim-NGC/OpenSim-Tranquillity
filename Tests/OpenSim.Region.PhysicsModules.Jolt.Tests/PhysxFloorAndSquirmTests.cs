/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;
using Omv = OpenMetaverse;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Two more of the PhysX workarounds in the Halcyon vehicle and physics code, asked of Jolt before anything is ported:
/// the floor under tiny forces (a force under 0.001 is raised to 0.001, or a torque of that size zeroed: "Stay above PhysX
/// NOP limits", "Avoid sending in too small values, causes physx to do strange things"; PhysxPrim.pid.cs MIN_PHYSX_FORCE,
/// VehicleDynamics.cs VehicleLimits.MinPhysicsForce) and the squirm thresholds (the step-unit dead zones "tuned to be above
/// the squirm point", under which "the object squirms continuously"; VehicleDynamics.cs VehicleLimits). Each test runs at the
/// module's two step rates, 45 Hz and 11 Hz (with the six collision steps that 11 Hz needs to keep each solver step to 1/60 s
/// or shorter), with Jolt's enhanced edge removal off and on. Figures go to the test output with the prefix [physx]. Serial
/// with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PhysxFloorAndSquirmTests
{
    private readonly ITestOutputHelper _out;
    public PhysxFloorAndSquirmTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    /// <summary>45 Hz with 2 collision steps and 11 Hz with 6, each with edge removal off and on.</summary>
    public static IEnumerable<object[]> Cells()
    {
        foreach (bool edge in new[] { false, true })
        {
            yield return new object[] { 45.0, 2, edge };
            yield return new object[] { 11.0, 6, edge };
        }
    }

    private static Action<Nini.Config.IConfig> Edge(bool on) => jolt => jolt.Set("BodyEnhancedEdgeRemoval", on ? "true" : "false");

    // The tiny-force tests read the force itself, so the module's per-second damping of every physical prim (0.02 linear, 0.05
    // angular, which takes about 2% off a speed in 2 s whatever the force) is switched off for them.
    private static Action<Nini.Config.IConfig> Undamped(bool edge) => jolt =>
    {
        Edge(edge)(jolt);
        jolt.Set("PrimLinearDamping", "0");
        jolt.Set("PrimAngularDamping", "0");
    };

    // The side of a cube of the module's density that weighs 1 kg.
    private const float KiloCube = 0.4642f;

    private static bool Asleep(SpikeProbe p, JoltPrim prim)
        => p.Rig.Backend.TryGetBodyState(prim.BodyHandle, out BodyState s) && (s.Flags & BodyStateFlags.Active) == 0;

    // ---- Tiny forces ---------------------------------------------------------------------------------------------

    private static readonly double[] TinyForces = { 1e-4, 5e-4, 1e-3, 5e-3 };

    private const double Tolerance = 0.01;   // 1% of the velocity F/m gives

    // A weightless 1 kg box (a cube of KiloCube metres) at rest, given an impulse of `j` N s along x in one call. The velocity
    // it ends with against j / m. The box has been at rest long enough to be asleep, so the push also has to wake it.
    private double ImpulseRatio(double rate, int n, bool edge, double j, bool angular)
    {
        using var p = new SpikeProbe(rate, n, Undamped(edge));
        JoltPrim box = p.Rig.AddBox(new Omv.Vector3(KiloCube, KiloCube, KiloCube), new Omv.Vector3(128f, 128f, StepRateRig.Ground + 20f), false);
        float mass = box.Mass;
        Assert.InRange(mass, 0.99f, 1.01f);
        StepTrace trace = p.Trace(box);
        p.RunTo(3.0);
        // Whether the engine has put it to sleep by now is a matter of the step rate; the push has to work either way.
        Say($"[physx] before the push: {rate} Hz x {n}, edge removal {(edge ? "on" : "off")}, box asleep {Asleep(p, box)}");
        if (angular) box.AddAngularForce(new Omv.Vector3((float)j, 0f, 0f), false);
        else box.AddForce(new Omv.Vector3((float)j, 0f, 0f), false);
        p.RunTo(p.Time + 0.3);
        StepRow last = trace.Rows[^1];
        if (!angular)
            return last.V.X / (j / mass);
        // Angular velocity against the impulse over the inertia of a cube about its centre: m a^2 / 6.
        double inertia = mass * KiloCube * KiloCube / 6.0;
        return last.W.X / (j / inertia);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void a_tiny_impulse_gives_the_velocity_it_should(double rate, int n, bool edge)
    {
        var failures = new List<string>();
        foreach (double j in TinyForces)
        {
            double r = ImpulseRatio(rate, n, edge, j, false);
            Say($"[physx] impulse {j:0.0e+0} N s | {rate} Hz x {n} | edge removal {(edge ? "on" : "off")} | velocity gained / (j/m) = {r:0.0000}");
            if (Math.Abs(r - 1.0) > Tolerance) failures.Add($"{j:0.0e+0}: {r:0.0000}");
        }
        Assert.True(failures.Count == 0, $"{rate} Hz x {n}: a tiny impulse gave the wrong velocity: " + string.Join("; ", failures));
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void a_tiny_angular_impulse_gives_the_spin_it_should(double rate, int n, bool edge)
    {
        var failures = new List<string>();
        foreach (double j in TinyForces)
        {
            double r = ImpulseRatio(rate, n, edge, j, true);
            Say($"[physx] angular impulse {j:0.0e+0} N m s | {rate} Hz x {n} | edge removal {(edge ? "on" : "off")} | spin gained / (j/I) = {r:0.0000}");
            if (Math.Abs(r - 1.0) > Tolerance) failures.Add($"{j:0.0e+0}: {r:0.0000}");
        }
        Assert.True(failures.Count == 0, $"{rate} Hz x {n}: a tiny angular impulse gave the wrong spin: " + string.Join("; ", failures));
    }

    // A constant force `f` N on the same box (llSetForce), from rest: the acceleration between 0.2 s and 0.4 s (before a
    // body at rest could fall asleep) and between 0.2 s and 2 s (the body is woken by the force at every step) against f / m.
    [Theory]
    [MemberData(nameof(Cells))]
    public void a_tiny_constant_force_accelerates_the_box_as_it_should(double rate, int n, bool edge)
    {
        var failures = new List<string>();
        foreach (double f in TinyForces)
        {
            using var p = new SpikeProbe(rate, n, Undamped(edge));
            JoltPrim box = p.Rig.AddBox(new Omv.Vector3(KiloCube, KiloCube, KiloCube), new Omv.Vector3(128f, 128f, StepRateRig.Ground + 20f), false);
            float mass = box.Mass;
            StepTrace trace = p.Trace(box);
            p.RunTo(1.5);
            double t0 = p.Time;
            box.Force = new Omv.Vector3((float)f, 0f, 0f);
            p.RunTo(t0 + 2.2);
            double expected = f / mass;
            double Accel(double a, double b)
            {
                StepRow ra = trace.Rows.Last(r => r.T <= t0 + a + 1e-9), rb = trace.Rows.Last(r => r.T <= t0 + b + 1e-9);
                return (rb.V.X - ra.V.X) / (rb.T - ra.T) / expected;
            }
            double early = Accel(0.2, 0.4), full = Accel(0.2, 2.0);
            Say($"[physx] force {f:0.0e+0} N | {rate} Hz x {n} | edge removal {(edge ? "on" : "off")} | acceleration / (f/m): 0.2-0.4 s {early:0.0000}, 0.2-2 s {full:0.0000}");
            if (Math.Abs(early - 1.0) > Tolerance) failures.Add($"{f:0.0e+0} early: {early:0.0000}");
            if (Math.Abs(full - 1.0) > Tolerance) failures.Add($"{f:0.0e+0} to 2 s: {full:0.0000}");
        }
        Assert.True(failures.Count == 0, $"{rate} Hz x {n}: a tiny constant force accelerated the box wrongly: " + string.Join("; ", failures));
    }

    // ---- Squirm --------------------------------------------------------------------------------------------------

    private readonly record struct Rest(double SleepAt, double CreepSpeed, double Drift, bool Asleep);

    private static float[] Slope(double degrees)
    {
        double rise = Math.Tan(degrees * Math.PI / 180.0);
        return SpikeProbe.Terrain((x, y) => (float)(StepRateRig.Ground + x * rise));
    }

    private static float SlopeZ(double degrees, float x) => (float)(StepRateRig.Ground + x * Math.Tan(degrees * Math.PI / 180.0));

    // A 1 m box set down on terrain tilted by `degrees` (flat ground at 0): when it falls asleep (seconds from the release,
    // or -1), the largest speed it has after `from` seconds, and how far it moves between `from` and the end of the run.
    private Rest BoxOnSlope(double rate, int n, bool edge, double degrees, double from, double end)
    {
        using var p = new SpikeProbe(rate, n, Edge(edge), Slope(degrees));
        float x = 128f;
        float top = SlopeZ(degrees, x);
        // The box is turned to lie flat on the slope, then released 2 cm above it.
        var tilt = Omv.Quaternion.CreateFromAxisAngle(Omv.Vector3.UnitY, (float)(-degrees * Math.PI / 180.0));
        JoltPrim box = p.Box(new Omv.Vector3(1f, 1f, 1f), new Omv.Vector3(x, 128f, top + 0.5f / MathF.Cos((float)(degrees * Math.PI / 180.0)) + 0.02f), tilt);
        StepTrace trace = p.Trace(box);
        double t0 = p.Time;
        double sleepAt = -1;
        p.RunTo(t0 + end, _ => { });
        // Falling asleep is read from the trace: the first step from which the box stays at rest.
        List<StepRow> rows = trace.Rows.Where(r => r.T >= t0 - 1e-9).ToList();
        double speed = rows.Where(r => r.T >= t0 + from).Select(r => r.V.Length()).DefaultIfEmpty(0).Max();
        StepRow a = rows.First(r => r.T >= t0 + from), b = rows[^1];
        double drift = (b.P - a.P).Length();
        for (int i = 0; i < rows.Count; i++)
            if (rows.Skip(i).All(r => r.V.Length() < 1e-4)) { sleepAt = rows[i].T - t0; break; }
        return new Rest(sleepAt, speed, drift, Asleep(p, box));
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void a_box_on_a_slope_settles_and_stays_put(double rate, int n, bool edge)
    {
        var failures = new List<string>();
        foreach (double degrees in new[] { 0.0, 5.0, 20.0 })
        {
            Rest r = BoxOnSlope(rate, n, edge, degrees, 3.0, 20.0);
            Say($"[physx] box on {degrees} degrees | {rate} Hz x {n} | edge removal {(edge ? "on" : "off")} | still from {r.SleepAt:0.00} s, asleep at the end {r.Asleep} | after 3 s: top speed {r.CreepSpeed:0.00000} m/s, moved {r.Drift:0.00000} m in 17 s");
            if (r.SleepAt < 0 || r.SleepAt > 3.0) failures.Add($"{degrees} degrees: still from {r.SleepAt:0.00} s");
            if (!r.Asleep) failures.Add($"{degrees} degrees: not asleep at the end");
            if (r.CreepSpeed > 1e-3 || r.Drift > 1e-3) failures.Add($"{degrees} degrees: creeps at {r.CreepSpeed:0.00000} m/s, {r.Drift:0.00000} m");
        }
        Assert.True(failures.Count == 0, $"{rate} Hz x {n}: " + string.Join("; ", failures));
    }

    // A car-type vehicle on the ground with a small linear motor that decays (a script asks for `speed` m/s; the motor decays
    // with timescale `decay`). Once the motor has ended (the linear motor threshold, 0.005 m/s, ThresholdLinearMotorDeltaV)
    // the vehicle should stop and sleep within 3 s and not creep.
    private (double MotorEnd, Rest Settled, double Peak) DecayingMotor(double rate, int n, bool edge, double degrees, float speed, float decay)
    {
        using var p = new SpikeProbe(rate, n, Edge(edge), degrees == 0 ? null : Slope(degrees),
                                     null);
        float x = 128f;
        var size = new Omv.Vector3(2f, 1f, 0.5f);
        float top = SlopeZ(degrees, x);
        var tilt = Omv.Quaternion.CreateFromAxisAngle(Omv.Vector3.UnitY, (float)(-degrees * Math.PI / 180.0));
        JoltPrim car = p.MakeVehicle(size, new Omv.Vector3(x, 128f, top + 0.25f / MathF.Cos((float)(degrees * Math.PI / 180.0)) + 0.02f));
        car.Orientation = tilt;
        p.Rig.Float(Vehicles.Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, decay);
        StepTrace trace = p.Trace(car);
        p.RunTo(1.0);
        double t0 = p.Time;
        p.Drive(car, speed);
        // The motor's own figure: speed x e^(-t / decay) falls under the threshold at decay x ln(speed / 0.005). A motor that is
        // 'engaged' for a further second (ThresholdLinearMotorEngaged) is counted in as well.
        double motorEnd = decay * Math.Log(speed / 0.005);
        p.RunTo(t0 + motorEnd + 25.0);
        List<StepRow> rows = trace.Rows.Where(r => r.T >= t0 - 1e-9).ToList();
        double peak = rows.Select(r => r.V.Length()).Max();
        double from = motorEnd + 3.0;
        double top2 = rows.Where(r => r.T >= t0 + from).Select(r => r.V.Length()).DefaultIfEmpty(0).Max();
        StepRow a = rows.First(r => r.T >= t0 + from), b = rows[^1];
        double sleepAt = -1;
        for (int i = 0; i < rows.Count; i++)
            if (rows.Skip(i).All(r => r.V.Length() < 1e-4)) { sleepAt = rows[i].T - t0; break; }
        return (motorEnd, new Rest(sleepAt, top2, (b.P - a.P).Length(), Asleep(p, car)), peak);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void a_vehicle_with_a_small_decaying_motor_comes_to_rest_and_sleeps(double rate, int n, bool edge)
    {
        var failures = new List<string>();
        foreach (double degrees in new[] { 0.0, 5.0 })
        {
            var (motorEnd, r, peak) = DecayingMotor(rate, n, edge, degrees, 0.3f, 0.5f);
            Say($"[physx] vehicle on {degrees} degrees | {rate} Hz x {n} | edge removal {(edge ? "on" : "off")} | peak {peak:0.000} m/s, motor ends at {motorEnd:0.00} s, still from {r.SleepAt:0.00} s, asleep at the end {r.Asleep} | 3 s after the end: top speed {r.CreepSpeed:0.00000} m/s, moved {r.Drift:0.00000} m in 25 s");
            if (r.SleepAt < 0 || r.SleepAt > motorEnd + 3.0) failures.Add($"{degrees} degrees: still from {r.SleepAt:0.00} s, motor end {motorEnd:0.00} s");
            if (!r.Asleep) failures.Add($"{degrees} degrees: not asleep at the end");
            if (r.CreepSpeed > 1e-3 || r.Drift > 1e-3) failures.Add($"{degrees} degrees: creeps at {r.CreepSpeed:0.00000} m/s, {r.Drift:0.00000} m");
        }
        Assert.True(failures.Count == 0, $"{rate} Hz x {n}: " + string.Join("; ", failures));
    }
}
