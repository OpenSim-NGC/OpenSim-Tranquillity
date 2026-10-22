/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Vehicles;
using Xunit;
using Xunit.Abstractions;
using Rig = OpenSim.Region.PhysicsModules.Jolt.Tests.StepRateRig;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Script forces in the object's own axes on a real backend, at 32, 45 and 64 Hz with 1 to 4 collision steps: a spinning
/// object with a constant force or torque given in its own axes follows the same path (within 1% for the force, 3% of the
/// small torque), and a constant force in the world's axes gives the same velocity after the same time. Also when the
/// provider that turns a local force with the body is attached. The figures are written to the test output. Serial with the
/// other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class StepRateLocalForceSceneTests
{
    private readonly ITestOutputHelper _out;
    public StepRateLocalForceSceneTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Heartbeat = Rig.Heartbeat;

    public static IEnumerable<object[]> Cells()
    {
        foreach (double rate in new[] { 32.0, 45.0, 64.0 })
            for (int n = rate >= 60 ? 1 : 2; n <= 4; n++)
                yield return new object[] { rate, n };
    }

    private double Check(string test, double rate, int n, double t, double got, double expected, double scale, double percent)
    {
        double error = Math.Abs(got - expected) / scale * 100.0;
        Say($"[rate-table] {test} | {rate} Hz x {n} | t {t:0.000} | {got:0.0000} against {expected:0.0000} | {error:0.00}% of {scale}");
        Assert.True(error <= percent, $"{test} at {rate} Hz with {n} collision steps, t = {t:0.000}: {got:0.0000} against {expected:0.0000} (within {percent}% of {scale})");
        return error;
    }

    // ---- Script forces in the object's own axes ----

    // A spinning object, spun about z at 1 rad/s, with a force of 1 m/s^2 along its own x axis: the force turns with it, so its
    // velocity is (a/w)(sin wt, 1 - cos wt). A force fixed in the world would give 2 m/s after 2 s along x.
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_local_force_on_a_spinning_object_follows_the_same_path_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n, jolt => { jolt.Set("PrimLinearDamping", "0"); jolt.Set("PrimAngularDamping", "0"); });
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.RotationalVelocity = new Vector3(0f, 0f, 1f);
        float a = 1f;
        box.SetScriptForce(new Vector3(a * box.Mass, 0f, 0f), local: true);

        foreach (double target in new[] { 1.0, 2.0 })
        {
            rig.RunTo(target);
            double t = rig.Time;
            Vector3 v = box.Velocity;
            Check("local force vx", rate, n, t, v.X, a * Math.Sin(t), a, 1.0);
            Check("local force vy", rate, n, t, v.Y, a * (1 - Math.Cos(t)), a, 1.0);
        }
        Assert.Equal(1, rig.Backend.StepForceProviderCount);
    }

    // The control: the same force and spin with the force fixed in the world's axes (what a local force was before it turned
    // with the body: the vector was turned once, at the call). It reads a x t along x, not (a/w) sin wt: 16% more at t = 1 s,
    // so the test above tells the two apart.
    [Fact]
    public void A_force_fixed_in_the_world_is_not_the_local_force_answer()
    {
        using var rig = new Rig(64.0, 2, jolt => { jolt.Set("PrimLinearDamping", "0"); jolt.Set("PrimAngularDamping", "0"); });
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.RotationalVelocity = new Vector3(0f, 0f, 1f);
        box.SetScriptForce(new Vector3(box.Mass, 0f, 0f), local: false);
        rig.RunTo(1.0);
        double t = rig.Time;
        Say($"[rate-table] control: world force on a spinning object | vx {box.Velocity.X:0.0000} against local answer {Math.Sin(t):0.0000}");
        Assert.Equal(t, box.Velocity.X, 2);
        Assert.True(Math.Abs(box.Velocity.X - Math.Sin(t)) > 0.1);
    }

    // A torque of 0.02 rad/s^2 about the object's own x axis on a cube spinning about z at 1 rad/s: its horizontal angular
    // velocity is (alpha/w)(sin wt, 1 - cos wt), to the second order in the small tilt it makes.
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_local_torque_on_a_spinning_object_follows_the_same_path_alike(double rate, int n)
    {
        using var rig = new Rig(rate, n, jolt => { jolt.Set("PrimLinearDamping", "0"); jolt.Set("PrimAngularDamping", "0"); });
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        box.RotationalVelocity = new Vector3(0f, 0f, 1f);
        float alpha = 0.02f;
        float inertia = rig.Backend.GetBodyInertiaDiagonal(box.BodyHandle).X;
        Assert.True(inertia > 0f);
        box.SetScriptTorque(new Vector3(alpha * inertia, 0f, 0f), local: true);

        foreach (double target in new[] { 1.0, 2.0 })
        {
            rig.RunTo(target);
            double t = rig.Time;
            Vector3 w = box.RotationalVelocity;
            Check("local torque wx", rate, n, t, w.X, alpha * Math.Sin(t), alpha, 3.0);
            Check("local torque wy", rate, n, t, w.Y, alpha * (1 - Math.Cos(t)), alpha, 3.0);
        }
    }

    // A force in the world's axes needs no provider: Jolt applies it in every collision step, so the same force gives the same
    // velocity after the same time at every rate.
    [Theory]
    [MemberData(nameof(Cells))]
    public void A_constant_world_force_gives_the_same_velocity_after_the_same_time(double rate, int n)
    {
        using var rig = new Rig(rate, n, jolt => { jolt.Set("PrimLinearDamping", "0"); jolt.Set("PrimAngularDamping", "0"); });
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        float a = 1f;
        box.Force = new Vector3(a * box.Mass, 0f, 0f);

        foreach (double target in new[] { 1.0, 2.0 })
        {
            rig.RunTo(target);
            double t = rig.Time;
            Check("world force", rate, n, t, box.Velocity.X, a * t, a * t, 1.0);
        }
        Assert.Equal(0, rig.Backend.StepForceProviderCount);   // no provider: it is Jolt's own force
    }

    [Fact]
    public void The_local_force_provider_is_there_only_while_a_local_force_is_set()
    {
        using var rig = new Rig(64.0, 2);
        JoltPrim box = rig.AddBox(new Vector3(1f, 1f, 1f), new Vector3(100f, 128f, 100f), vehicle: false);
        Assert.Equal(0, rig.Backend.StepForceProviderCount);

        box.SetScriptForce(new Vector3(box.Mass, 0f, 0f), local: true);
        rig.Scene.Simulate(Heartbeat);
        Assert.Equal(1, rig.Backend.StepForceProviderCount);

        box.SetScriptForce(new Vector3(box.Mass, 0f, 0f), local: false);   // a new call replaces it: now in the world's axes
        rig.Scene.Simulate(Heartbeat);
        Assert.Equal(0, rig.Backend.StepForceProviderCount);

        box.SetScriptForce(new Vector3(box.Mass, 0f, 0f), local: true);
        rig.Scene.Simulate(Heartbeat);
        Assert.Equal(1, rig.Backend.StepForceProviderCount);
        box.SetScriptForce(Vector3.Zero, local: true);   // zero ends it
        rig.Scene.Simulate(Heartbeat);
        Assert.Equal(0, rig.Backend.StepForceProviderCount);

        box.SetScriptTorque(new Vector3(0f, 0f, 1f), local: true);
        rig.Scene.Simulate(Heartbeat);
        Assert.Equal(1, rig.Backend.StepForceProviderCount);
        box.Torque = Vector3.Zero;   // the plain setter is a world-frame torque
        rig.Scene.Simulate(Heartbeat);
        Assert.Equal(0, rig.Backend.StepForceProviderCount);
        Assert.Equal(0, rig.Backend.StepForceProviderFaults);
    }
}
