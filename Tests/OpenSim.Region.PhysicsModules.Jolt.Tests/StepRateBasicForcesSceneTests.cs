/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using Rig = OpenSim.Region.PhysicsModules.Jolt.Tests.StepRateRig;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Move to target, hover, ground repel, buoyancy and the rotation target (llLookAt, llRotLookAt) of an ordinary physical prim
/// on a real backend, at 32, 45 and 64 Hz with 1 to 4 collision steps (the module keeps each solver step to 1/60 s or shorter,
/// so 32 and 45 Hz cannot run with one), under both <c>[Jolt] BasicForcesBehaviour</c> values. Each controller runs in every
/// collision step (BasicForcesStepProvider), so a second gives the same motion however it is cut. Where the answer is closed
/// form (the SecondLife spring, Halcyon's move to target) it is the reference; where Halcyon's code is the reference it is run
/// as written at its own 15 ms step (<see cref="HalcyonAsWritten"/>). The tolerance is 2% of the distance the object travels.
/// Figures go to the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class StepRateBasicForcesSceneTests
{
    private readonly ITestOutputHelper _out;
    public StepRateBasicForcesSceneTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = Rig.Heartbeat;
    private const float H0 = HalcyonBasicForces.StepSeconds;
    private const float G = 9.80665f;

    public static IEnumerable<object[]> Cells()
    {
        foreach (string behaviour in new[] { "SecondLife", "InWorldz" })
            foreach (double rate in new[] { 32.0, 45.0, 64.0 })
                for (int n = rate >= 60 ? 1 : 2; n <= 4; n++)
                    yield return new object[] { behaviour, rate, n };
    }

    public static IEnumerable<object[]> InWorldzCells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = rate >= 60 ? 1 : 2; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private static Rig Make(string behaviour, double rate, int n, Action<IConfig> more = null)
        => new Rig(rate, n, jolt =>
        {
            jolt.Set("PrimLinearDamping", "0");
            jolt.Set("PrimAngularDamping", "0");
            jolt.Set("BasicForcesBehaviour", behaviour);
            more?.Invoke(jolt);
        });

    // The value read after each heartbeat, against the physics time.
    private static List<(double t, double v)> Trace(Rig rig, double seconds, Func<double> read)
    {
        var trace = new List<(double, double)> { (rig.Time, read()) };
        while (rig.Time < seconds - 1e-9)
        {
            rig.Scene.Simulate(Heartbeat);
            trace.Add((rig.Time, read()));
        }
        Assert.True(rig.Backend.StepForceProviderFaults == 0, $"a step provider threw: {rig.Backend.LastStepForceProviderFault}");
        return trace;
    }

    // When the value first drops under `level` (linearly between samples), or -1.
    private static double FirstUnder(List<(double t, double v)> trace, double level)
    {
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].v < level && trace[i - 1].v >= level)
            {
                double f = (trace[i - 1].v - level) / (trace[i - 1].v - trace[i].v);
                return trace[i - 1].t + f * (trace[i].t - trace[i - 1].t);
            }
        return -1;
    }

    private double Check(string test, string behaviour, double rate, int n, double t, double got, double expected, double scale, double percent)
    {
        double error = Math.Abs(got - expected) / scale * 100.0;
        Say($"[rate-table] {test} | {behaviour} | {rate} Hz x {n} | t {t:0.000} | {got:0.0000} against {expected:0.0000} | {error:0.00}% of {scale}");
        Assert.True(error <= percent, $"{test} ({behaviour}) at {rate} Hz with {n} collision steps, t = {t:0.000}: {got:0.0000} against {expected:0.0000} (within {percent}% of {scale})");
        return error;
    }

    // ---- Move to target ----

    // The distance left after t: SecondLife's critically damped spring d (1 + t / tau) e^(-t / tau); Halcyon's exponential approach
    // d (1 - h0 / tau)^(t / h0), the velocity distance / tau in each step of 15 ms.
    private static double Left(string behaviour, double d, double tau, double t)
        => behaviour == "InWorldz" ? d * Math.Pow(1 - H0 / tau, t / H0) : d * (1 + t / tau) * Math.Exp(-t / tau);

    [Theory]
    [MemberData(nameof(Cells))]
    public void Move_to_target_arrives_alike_and_does_not_overshoot(string behaviour, double rate, int n)
    {
        foreach (float tau in new[] { 0.3f, 1f })
        {
            using Rig rig = Make(behaviour, rate, n);
            JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
            box.PIDTarget = new Vector3(110f, 128f, 100f);
            box.PIDTau = tau;
            box.PIDActive = true;
            var x = Trace(rig, 6.0, () => box.Position.X - 100.0);
            foreach (double t in new[] { 0.5, 1.0, 2.0 })
                Check($"move-to tau {tau} distance left", behaviour, rate, n, t, 10.0 - HalcyonAsWritten.At(x, t), Left(behaviour, 10.0, tau, t), 10.0, 2.0);

            double arrived = FirstUnder(x.Select(p => (p.t, 10.0 - p.v)).ToList(), 0.5);
            double expected = Enumerable.Range(1, 6000).Select(i => i * 0.001).First(t => Left(behaviour, 10.0, tau, t) < 0.5);
            Say($"[rate-table] move-to tau {tau} time to within 0.5 m | {behaviour} | {rate} Hz x {n} | {arrived:0.000} s against {expected:0.000} s");
            Assert.True(Math.Abs(arrived - expected) <= 0.03 * expected + 0.01, $"{arrived} against {expected}");
            double overshoot = x.Max(p => p.v) - 10.0;
            Say($"[rate-table] move-to tau {tau} overshoot | {behaviour} | {rate} Hz x {n} | {overshoot:0.0000} m");
            Assert.True(overshoot <= 0.01, $"overshoot {overshoot}");
        }
    }

    [Theory]
    [InlineData("InWorldz", true)]
    [InlineData("SecondLife", false)]
    public void A_target_more_than_65_m_away_acts_under_InWorldz_only(string behaviour, bool acts)
    {
        using Rig rig = Make(behaviour, 45.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(60f, 128f, 100f), vehicle: false);
        box.PIDTarget = new Vector3(200f, 128f, 100f);   // 140 m away
        box.PIDTau = 1f;
        box.PIDActive = true;
        rig.RunTo(1.0);
        double moved = box.Position.X - 60.0;
        Say($"[behaviour] move target 140 m away, tau 1 | {behaviour} | moved {moved:0.00} m in 1 s");
        if (acts) Assert.True(moved > 1.0, $"moved {moved}");
        else Assert.True(Math.Abs(moved) < 0.01, $"moved {moved}");
    }

    [Theory]
    [InlineData("InWorldz", false)]
    [InlineData("SecondLife", true)]
    public void A_tau_of_two_Halcyon_steps_or_less_does_nothing_under_InWorldz_and_acts_at_the_floor_under_SecondLife(string behaviour, bool holds)
    {
        using Rig rig = Make(behaviour, 45.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.Buoyancy = 0f;   // gravity on: a held object does not fall
        box.PIDTarget = new Vector3(100f, 128f, 100f);
        box.PIDTau = 0.02f;
        box.PIDActive = true;
        rig.RunTo(0.5);
        double fell = 100.0 - box.Position.Z;
        Say($"[behaviour] move to its own place, tau 0.02 | {behaviour} | fell {fell:0.000} m in 0.5 s");
        if (holds) Assert.True(fell < 0.01, $"fell {fell}");
        else Assert.True(fell > 0.9, $"fell {fell}");
    }

    // ---- Hover ----

    [Theory]
    [MemberData(nameof(Cells))]
    public void Hover_settles_to_the_same_height_alike(string behaviour, double rate, int n)
    {
        // tau 1, a hover height of 3 m over the ground (25 m), from 1 m over it: a 2 m rise.
        using Rig rig = Make(behaviour, rate, n);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, Rig.Ground + 1f), vehicle: false);
        box.Buoyancy = 0f;
        box.PIDHoverHeight = 3f;
        box.PIDHoverType = PIDHoverType.Ground;
        box.PIDHoverTau = 1f;
        box.PIDHoverActive = true;
        var z = Trace(rig, 3.0, () => box.Position.Z - Rig.Ground);
        List<(double t, double z)> reference = behaviour == "InWorldz" ? HalcyonAsWritten.HoverPath(H0, 3.0, 1f, 3f, false, true, 1f) : null;
        foreach (double t in new[] { 0.5, 1.0, 2.0, 3.0 })
        {
            double expected = behaviour == "InWorldz"
                ? HalcyonAsWritten.At(reference.Select(p => (p.t, p.z)).ToList(), t)
                : 3.0 - 2.0 * (1 + t / 1.0) * Math.Exp(-t / 1.0);
            Check("hover height", behaviour, rate, n, t, HalcyonAsWritten.At(z, t), expected, 2.0, 2.0);
        }
    }

    [Theory]
    [MemberData(nameof(InWorldzCells))]
    public void Ground_repel_holds_its_height_alike_and_never_pulls_down(double rate, int n)
    {
        // Dropped from 3.6 m over the ground with a repel height of 3 m, tau 1: it falls freely to the zone (a repel does not pull an
        // object down), is pushed up from under it, and holds about its height. Halcyon's code is the reference.
        using Rig rig = Make("InWorldz", rate, n);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, Rig.Ground + 3.6f), vehicle: false);
        box.Buoyancy = 0f;
        box.PIDHoverHeight = 3f;
        box.PIDHoverType = PIDHoverType.Ground;
        box.PIDHoverTau = 1f;
        box.PIDHoverRepel = true;
        box.PIDHoverActive = true;
        var z = Trace(rig, 8.0, () => box.Position.Z - Rig.Ground);
        var reference = HalcyonAsWritten.HoverPath(H0, 8.0, 1f, 3f, true, true, 3.6f).Select(p => (p.t, p.z)).ToList();
        Check("repel free fall", "InWorldz", rate, n, 0.1, HalcyonAsWritten.At(z, 0.1), 3.6 - 0.5 * G * 0.1 * 0.1, 3.0, 2.0);
        foreach (double t in new[] { 1.0, 2.0, 4.0, 8.0 })
            Check("repel height", "InWorldz", rate, n, t, HalcyonAsWritten.At(z, t), HalcyonAsWritten.At(reference, t), 3.0, 2.0);
    }

    [Theory]
    [MemberData(nameof(InWorldzCells))]
    public void Ground_repel_pushes_up_from_under_its_height_alike(double rate, int n)
    {
        using Rig rig = Make("InWorldz", rate, n);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, Rig.Ground + 1f), vehicle: false);
        box.Buoyancy = 0f;
        box.PIDHoverHeight = 3f;
        box.PIDHoverType = PIDHoverType.Ground;
        box.PIDHoverTau = 1f;
        box.PIDHoverRepel = true;
        box.PIDHoverActive = true;
        var z = Trace(rig, 3.0, () => box.Position.Z - Rig.Ground);
        var reference = HalcyonAsWritten.HoverPath(H0, 3.0, 1f, 3f, true, true, 1f).Select(p => (p.t, p.z)).ToList();
        foreach (double t in new[] { 0.5, 1.0, 2.0, 3.0 })
            Check("repel rise", "InWorldz", rate, n, t, HalcyonAsWritten.At(z, t), HalcyonAsWritten.At(reference, t), 2.0, 2.0);
    }

    [Fact]
    public void A_hover_that_ends_gives_gravity_back()
    {
        using Rig rig = Make("InWorldz", 45.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, Rig.Ground + 10f), vehicle: false);
        box.Buoyancy = 0f;
        box.PIDHoverHeight = 10f;
        box.PIDHoverType = PIDHoverType.Ground;
        box.PIDHoverTau = 1f;
        box.PIDHoverActive = true;
        rig.RunTo(1.0);
        Assert.True(Math.Abs(box.Position.Z - (Rig.Ground + 10f)) < 0.06, "the hover did not hold the height");
        Assert.Equal(1, rig.Backend.StepForceProviderCount);
        box.PIDHoverActive = false;
        double t0 = rig.Time;
        rig.RunTo(t0 + 1.0);
        double fell = t0 + 1.0 - rig.Time;
        Say($"[behaviour] hover ended | speed {box.Velocity.Z:0.000} after {rig.Time - t0:0.00} s");
        Assert.True(Math.Abs(box.Velocity.Z + G * (rig.Time - t0)) < 0.5, $"gravity did not come back: vz {box.Velocity.Z}");
        Assert.Equal(0, rig.Backend.StepForceProviderCount);   // no controller acts: the provider is off the body
    }

    // ---- Buoyancy ----

    [Theory]
    [MemberData(nameof(Cells))]
    public void Buoyancy_gives_the_same_rate_of_fall_or_rise(string behaviour, double rate, int n)
    {
        foreach (float buoyancy in new[] { 0.5f, 1.5f })
        {
            using Rig rig = Make(behaviour, rate, n);
            JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
            box.Buoyancy = buoyancy;
            foreach (double target in new[] { 1.0, 2.0 })
            {
                rig.RunTo(target);
                double t = rig.Time;
                Check($"buoyancy {buoyancy} vertical speed", behaviour, rate, n, t, box.Velocity.Z, -(1 - buoyancy) * G * t, G, 2.0);
            }
        }
    }

    [Theory]
    [InlineData("InWorldz", 0.98f, 1f, 0.0)]
    [InlineData("InWorldz", 0.977f, 1f, 0.0)]
    [InlineData("InWorldz", 0.976f, 0.976f, -0.024 * G)]
    [InlineData("SecondLife", 0.98f, 0.98f, -0.02 * G)]
    public void A_buoyancy_from_0_977_to_under_1_floats_the_object_under_InWorldz_only(string behaviour, float given, float kept, double speedAfterOneSecond)
    {
        using Rig rig = Make(behaviour, 45.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.Buoyancy = given;
        Assert.Equal(kept, box.Buoyancy);
        rig.RunTo(1.0);
        Say($"[behaviour] buoyancy {given} | {behaviour} | kept as {box.Buoyancy} | speed after 1 s {box.Velocity.Z:0.0000} against {speedAfterOneSecond:0.0000}");
        Assert.True(Math.Abs(box.Velocity.Z - speedAfterOneSecond) <= 0.02, $"{box.Velocity.Z} against {speedAfterOneSecond}");
    }

    // ---- Look-at and rot-look-at ----

    private static double Yaw(JoltPrim box)
    {
        Quaternion q = box.Orientation;
        return 2.0 * Math.Atan2(q.Z, q.W);
    }

    private static void LookAt(JoltPrim box, float strength, float damping, double angle)
    {
        box.APIDTarget = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)angle);
        box.APIDStrength = strength;
        box.APIDDamping = damping;
        box.APIDActive = true;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void The_rotation_target_turns_the_object_in_the_same_time_alike(string behaviour, double rate, int n)
    {
        // A quarter turn about z, strength 0.3, damping 0.5. SecondLife: the critically damped spring over max(strength,
        // min(damping, 1)) = 0.5 s. InWorldz: Halcyon's code as written at 15 ms (its step is sound at this strength).
        const double turn = Math.PI / 2;
        using Rig rig = Make(behaviour, rate, n);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        LookAt(box, 0.3f, 0.5f, turn);
        var yaw = Trace(rig, 4.5, () => Yaw(box));
        var reference = behaviour == "InWorldz" ? HalcyonAsWritten.LookAtPath(H0, 4.5, turn, 0.3f, 0.5f).Select(p => (p.t, p.angle)).ToList() : null;
        foreach (double t in new[] { 0.25, 0.5, 1.0, 2.0 })
        {
            double expected = behaviour == "InWorldz" ? HalcyonAsWritten.At(reference, t) : turn * (1 - (1 + t / 0.5) * Math.Exp(-t / 0.5));
            Check("look-at angle", behaviour, rate, n, t, HalcyonAsWritten.At(yaw, t), expected, turn, 2.0);
        }
        // The time to settle, to 5 degrees (the last degree is a tail: a hundredth of a radian moves it by a tenth of its time) and
        // to the 1 degree at which Halcyon's code stops the turn, which is only reported.
        foreach ((double level, bool assert) in new[] { (5 * HalcyonBasicForces.AtRotTolerance, true), (HalcyonBasicForces.AtRotTolerance, false) })
        {
            double settled = FirstUnder(yaw.Select(p => (p.t, turn - p.v)).ToList(), level);
            double settledRef = behaviour == "InWorldz"
                ? FirstUnder(reference.Select(p => (p.t, turn - p.angle)).ToList(), level)
                : Enumerable.Range(1, 6000).Select(i => i * 0.001).First(t => turn * (1 + t / 0.5) * Math.Exp(-t / 0.5) < level);
            Say($"[rate-table] look-at time to within {level / HalcyonBasicForces.AtRotTolerance:0} degrees | {behaviour} | {rate} Hz x {n} | {settled:0.000} s against {settledRef:0.000} s");
            if (assert)
                Assert.True(Math.Abs(settled - settledRef) <= 0.03 * settledRef + 0.03, $"{settled} against {settledRef}");
        }
        Assert.True(yaw.Max(p => p.v) - turn <= 0.02, "the object turned past its target");
    }

    [Theory]
    [MemberData(nameof(InWorldzCells))]
    public void Halcyons_look_at_at_full_strength_is_stable_at_every_step_and_the_same_alike(double rate, int n)
    {
        // At strength 1 Halcyon's drag is 1.5 per 15 ms step and over 2 above 20 ms: its code, run at the step of 32 Hz with
        // one collision step (31 ms), grows without limit (HalcyonBasicForcesTests). Here the object settles whatever the step.
        const double turn = Math.PI / 2;
        using Rig rig = Make("InWorldz", rate, n);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        LookAt(box, 1f, 0.1f, turn);
        var yaw = Trace(rig, 4.0, () => Yaw(box));
        var fine = FineLookAt(turn, 1f, 0.1f, 4.0);
        foreach (double t in new[] { 0.25, 0.5, 1.0, 2.0, 4.0 })
            Check("look-at angle at full strength", "InWorldz", rate, n, t, HalcyonAsWritten.At(yaw, t), HalcyonAsWritten.At(fine, t), turn, 2.0);
        Assert.True(yaw.Max(p => p.v) - turn <= 0.03, "the object turned past its target");
        Assert.True(Math.Abs(yaw[^1].v - turn) < 0.05, $"left {turn - yaw[^1].v} rad");
    }

    // The same law at a step of 2 ms: the path the step-independent form follows.
    private static List<(double t, double v)> FineLookAt(double turn, float strength, float damping, double seconds)
    {
        double h = 1.0 / 512, angle = 0, w = 0;
        var path = new List<(double, double)> { (0, 0) };
        for (int i = 1; i <= (int)(seconds / h); i++)
        {
            System.Numerics.Vector3 next = HalcyonBasicForces.RotLook(new System.Numerics.Vector3(0f, 0f, (float)(turn - angle)), new System.Numerics.Vector3(0f, 0f, (float)w), strength, damping, (float)h);
            w = next.Z;
            angle += w * h;
            path.Add((i * h, angle));
        }
        return path;
    }

    [Theory]
    [InlineData("InWorldz", true)]
    [InlineData("SecondLife", false)]
    public void A_strength_under_0_0445_turns_the_object_under_InWorldz_only(string behaviour, bool turns)
    {
        // The SL wiki: a strength under about 0.0445 has no effect. Halcyon holds the strength to 0.1..1 and acts on whatever it is given.
        using Rig rig = Make(behaviour, 45.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        LookAt(box, 0.02f, 0.5f, Math.PI / 2);
        rig.RunTo(1.0);
        Say($"[behaviour] look-at strength 0.02 | {behaviour} | turned {Yaw(box):0.000} rad in 1 s");
        if (turns) Assert.True(Yaw(box) > 0.3, $"turned {Yaw(box)}");
        else Assert.True(Math.Abs(Yaw(box)) < 0.01, $"turned {Yaw(box)}");
    }

    // ---- The step provider ----

    [Fact]
    public void The_provider_is_on_the_body_only_while_a_controller_acts()
    {
        using Rig rig = Make("InWorldz", 45.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        Assert.Equal(0, rig.Backend.StepForceProviderCount);
        box.PIDTarget = new Vector3(105f, 128f, 100f);
        box.PIDTau = 1f;
        box.PIDActive = true;
        rig.RunTo(0.5);
        Assert.Equal(1, rig.Backend.StepForceProviderCount);
        long calls = rig.Scene.BasicForcesOnStepCalls;
        Assert.True(calls >= 2 * 4, $"{calls} collision-step calls in 0.5 s");
        box.PIDActive = false;
        rig.RunTo(1.0);
        Assert.Equal(0, rig.Backend.StepForceProviderCount);
    }

    // The Update-rate path arms the provider; every lock a script thread, the Update-rate path or the scene's own sets could be
    // holding is then held by another thread while the engine steps. A collision step that took any of them would wait for it for
    // ever: the step has ten seconds to finish. (The backend's own region lock and pool gate are the step's own and are not among
    // them: the step takes them itself, before it runs a provider.)
    [Fact]
    public void A_collision_step_takes_none_of_the_locks_the_other_threads_hold()
    {
        using Rig rig = Make("InWorldz", 64.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.PIDTarget = new Vector3(110f, 128f, 100f);
        box.PIDTau = 1f;
        box.PIDActive = true;
        box.PIDHoverHeight = 3f;
        box.PIDHoverType = PIDHoverType.Absolute;
        box.PIDHoverTau = 1f;
        box.PIDHoverActive = true;
        LookAt(box, 0.3f, 0.5f, Math.PI / 2);
        for (int i = 0; i < 11; i++)
            rig.Scene.Simulate(Heartbeat);

        var locks = new List<object>();
        object Field(object owner, string name)
        {
            FieldInfo f = owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return f.GetValue(owner);
        }
        object plan = Field(box, "_basicForces");
        locks.Add(Field(box, "_targetLock"));
        locks.Add(Field(box, "_scriptForceLock"));
        locks.Add(Field(plan, "_gate"));
        locks.Add(Field(rig.Scene, "_targeted"));
        locks.Add(Field(rig.Scene, "_scriptForced"));
        locks.Add(Field(rig.Scene, "_vehicles"));

        // The Update-rate path, as JoltScene.Simulate runs it before the step.
        box.StepTargets(1f / 64f);
        long calls0 = rig.Scene.BasicForcesOnStepCalls;
        float VelocityX() { Assert.True(rig.Backend.TryGetBodyState(box.BodyHandle, out BodyState st)); return st.LinearVelocity.X; }
        float vx0 = VelocityX();

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            void Take(int i)
            {
                if (i == locks.Count)
                {
                    held.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    return;
                }
                lock (locks[i])
                    Take(i + 1);
            }
            Take(0);
        }) { IsBackground = true };
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)), "the holder thread could not take the locks");

        bool finished;
        try
        {
            Task step = Task.Run(() => rig.Backend.Step(1f / 64f, Span<BodyState>.Empty, Span<CharacterState>.Empty, Span<ContactReport>.Empty));
            // the test needs to know whether the step returned within a bound, so it waits on the task here
#pragma warning disable xUnit1031
            finished = step.Wait(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1031
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }
        Assert.True(finished, "the engine step did not finish while the locks were held: a collision step took one");
        Assert.Equal(calls0 + 2, rig.Scene.BasicForcesOnStepCalls);   // both collision steps ran the provider
        Assert.NotEqual(vx0, VelocityX());
    }
}
