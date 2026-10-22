/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Reflection;
using JoltPhysicsSharp;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What a Jolt PhysicsStepListener does on the native this module ships (the stock joltc of JoltPhysics.Native 1.0.4 with
/// JoltPhysicsSharp 2.19.1), as the forces plan relies on it. Jolt calls OnStep before each collision step of every
/// Update (PhysicsSystem.cpp, JobStepListeners), with every body locked, so a listener reads and writes bodies through the
/// no-lock interfaces and takes no region lock. Jolt's accumulated force is applied in every collision step and cleared only
/// after the last (PhysicsSystem.cpp, ApplyGravity and the sleep check), so a force added from OnStep is applied again in
/// each later step of the same Update; a velocity change is not.
///
/// The tests register the listener on the PhysicsSystem of a real backend, between steps, and take it off before the backend
/// is disposed. They run in the serial collection because a real backend steps on the process's shared job pool.
/// </summary>
[Xunit.Collection(JoltNativeSerial.Name)]
public class StepListenerTests
{
    private const float Dt = 1f / 45f;
    private readonly ITestOutputHelper _out;

    public StepListenerTests(ITestOutputHelper output) => _out = output;

    /// <summary>The listener under test: counts its calls and keeps what each one saw. It does not change a body.</summary>
    private sealed class CountingListener : PhysicsStepListener
    {
        private readonly object _gate = new();
        public readonly List<(float Dt, bool First, bool Last, int Thread)> Calls = new();
        public Action<PhysicsStepListenerContext> Extra;
        public bool SawSystem = true;
        public PhysicsSystem Expected;

        protected override void OnStep(in PhysicsStepListenerContext context)
        {
            lock (_gate)
            {
                Calls.Add((context.DeltaTime, context.IsFirstStep, context.IsLastStep, Environment.CurrentManagedThreadId));
                SawSystem &= ReferenceEquals(context.System, Expected);
            }
            Extra?.Invoke(context);
        }
    }

    private static PhysicsSystem SystemOf(JoltPhysicsBackend b)
        => (PhysicsSystem)typeof(JoltPhysicsBackend).GetField("_system", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b);

    private static BodyID NativeId(JoltPhysicsBackend b, BodyId body)
    {
        var records = (System.Collections.IDictionary)typeof(JoltPhysicsBackend).GetField("_joltToRecord", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b);
        foreach (System.Collections.DictionaryEntry e in records)
        {
            object rec = e.Value;
            if ((uint)rec.GetType().GetField("Handle").GetValue(rec) == body.Value)
                return new BodyID((uint)rec.GetType().GetField("NativeBodyId").GetValue(rec));
        }
        throw new InvalidOperationException("the body is not in the backend's records");
    }

    private static PhysicsBackendSettings SettingsWith(int collisionSteps)
    {
        PhysicsBackendSettings s = JoltTestBackend.Settings();
        s.CollisionSteps = collisionSteps;
        return s;
    }

    private static void Step(JoltPhysicsBackend b)
        => b.Step(Dt, new BodyState[16], new CharacterState[4], new ContactReport[64]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OnStep_runs_before_each_collision_step_with_the_steps_own_time(int n)
    {
        using var t = new JoltTestBackend(SettingsWith(n));
        BodyId box = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f)), new Vector3(128f, 128f, 50f));
        PhysicsSystem system = SystemOf(t.B);
        var listener = new CountingListener { Expected = system };
        system.AddStepListener(listener);
        try
        {
            const int updates = 3;
            for (int i = 0; i < updates; i++)
                Step(t.B);

            Assert.Equal(updates * n, listener.Calls.Count);
            for (int i = 0; i < listener.Calls.Count; i++)
            {
                (float dt, bool first, bool last, _) = listener.Calls[i];
                Assert.Equal(Dt / n, dt, 6);
                Assert.Equal(i % n == 0, first);
                Assert.Equal(i % n == n - 1, last);
            }
            Assert.True(listener.SawSystem, "the context's System is the system the listener is registered on");
            _out.WriteLine($"n={n}: {listener.Calls.Count} calls, step {listener.Calls[0].Dt * 1000f:0.000} ms, " +
                           $"threads {string.Join(",", listener.Calls.Select(c => c.Thread).Distinct())}, stepping thread {Environment.CurrentManagedThreadId}");
        }
        finally
        {
            system.RemoveStepListener(listener);
            listener.Dispose();
        }
    }

    [Fact]
    public void A_registered_listener_makes_an_Update_with_nothing_in_the_world_run()
    {
        using var t = new JoltTestBackend(SettingsWith(3));
        PhysicsSystem system = SystemOf(t.B);
        var listener = new CountingListener { Expected = system };
        Step(t.B);   // no listener yet
        system.AddStepListener(listener);
        try
        {
            Step(t.B);
            Assert.Equal(3, listener.Calls.Count);
        }
        finally
        {
            system.RemoveStepListener(listener);
            listener.Dispose();
        }
    }

    [Fact]
    public void A_removed_listener_is_not_called_again_and_the_backend_disposes_cleanly()
    {
        var t = new JoltTestBackend(SettingsWith(2));
        t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f)), new Vector3(128f, 128f, 50f));
        PhysicsSystem system = SystemOf(t.B);
        var listener = new CountingListener { Expected = system };
        system.AddStepListener(listener);
        Step(t.B);
        Assert.Equal(2, listener.Calls.Count);

        system.RemoveStepListener(listener);
        listener.Dispose();
        Step(t.B);
        Step(t.B);
        Assert.Equal(2, listener.Calls.Count);

        t.Dispose();
    }

    // The rule the forces plan builds on, on the real native: a body with no gravity, a force of 10 N on a 1 kg body for one
    // Update of 4 collision steps. Added once before the Update it gives F dt / m. Added from OnStep at every step it is
    // still in the accumulator at the next step, so step k applies k forces: 1 + 2 + 3 + 4 = 10 steps' worth instead of 4.
    // A velocity change written from OnStep at every step gives exactly F dt / m.
    [Theory]
    [InlineData("before the Update", 1.0)]
    [InlineData("AddForce from OnStep", 2.5)]
    [InlineData("velocity change from OnStep", 1.0)]
    public void A_force_added_from_OnStep_is_applied_again_in_every_later_collision_step_but_a_velocity_change_is_not(string how, double expectedShare)
    {
        const int n = 4;
        const float force = 10f;
        using var t = new JoltTestBackend(SettingsWith(n));
        BodyId box = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f)), new Vector3(128f, 128f, 50f));
        t.B.SetBodyGravityFactor(box, 0f);
        BodyID id = NativeId(t.B, box);
        PhysicsSystem system = SystemOf(t.B);
        float mass = t.B.GetBodyMass(box);
        var f = new Vector3(force, 0f, 0f);
        var listener = new CountingListener { Expected = system };
        if (how == "AddForce from OnStep")
            listener.Extra = c => system.BodyInterfaceNoLock.AddForce(id, f);
        else if (how == "velocity change from OnStep")
            listener.Extra = c => system.BodyInterfaceNoLock.SetLinearVelocity(id, system.BodyInterfaceNoLock.GetLinearVelocity(id) + f / mass * c.DeltaTime);
        system.AddStepListener(listener);
        try
        {
            if (how == "before the Update")
                t.B.ApplyForce(box, f);
            Step(t.B);
            Assert.Equal(n, listener.Calls.Count);
            float vx = system.BodyInterfaceNoLock.GetLinearVelocity(id).X;
            double intended = force / mass * Dt;
            _out.WriteLine($"{how}: mass {mass:0.###} kg, vx {vx:0.#####} m/s, F dt / m {intended:0.#####}, share {vx / intended:0.###}");
            Assert.Equal(expectedShare, vx / intended, 2);
        }
        finally
        {
            system.RemoveStepListener(listener);
            listener.Dispose();
        }
    }
}
