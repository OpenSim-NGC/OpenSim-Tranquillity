/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Numerics;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// joltc keeps every physics system in one process-wide map that a step listener's OnStep reads. So a region's physics
/// system is created and destroyed only with every job pool's gate held, and Dispose takes the gates before its own
/// _simLock (the order Step uses). These tests prove the order has no deadlock, that create and destroy wait for a
/// running Update, and that a region can be created and closed over and over while another region steps with a listener
/// registered.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PoolGateSystemMapTests
{
    private readonly ITestOutputHelper _out;
    public PoolGateSystemMapTests(ITestOutputHelper output) => _out = output;

    // Every wait below is a cap on something that should return, generous so that a stalled machine cannot reach it.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private sealed class Accelerate : IStepForceProvider
    {
        public void OnStep(in StepBody body, in StepInfo step) => body.AddLinearVelocity(new Vector3(0.1f, 0f, 0f) * step.DeltaTime);
    }

    private static PhysicsBackendSettings Region(string name, int collisionSteps = 2)
    {
        PhysicsBackendSettings s = JoltTestBackend.Settings(maxBodies: 4096, maxBodyPairs: 4096, maxContactConstraints: 1024);
        s.CollisionSteps = collisionSteps;
        s.RegionName = name;
        return s;
    }

    private static BodyId Floater(JoltTestBackend t, int i = 0)
    {
        BodyId box = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(20f + 2f * i, 20f, 100f));
        t.B.SetBodyGravityFactor(box, 0f);
        t.B.SetBodyLinearVelocity(box, new Vector3(0f, 0f, 1f));
        return box;
    }

    [Fact]
    public void Creating_and_closing_regions_while_another_region_steps_with_a_listener_registered_does_not_crash_or_hang()
    {
        using var steady = new JoltTestBackend(Region("Steady"));
        for (int i = 0; i < 20; i++)
            steady.B.AttachStepForceProvider(Floater(steady, i), new Accelerate());
        Assert.True(steady.B.HasStepListener);

        using var stop = new CancellationTokenSource();
        long updates = 0;
        var stepper = new Thread(() =>
        {
            var states = new BodyState[64];
            var chars = new CharacterState[4];
            var contacts = new ContactReport[64];
            while (!stop.IsCancellationRequested)
            {
                steady.B.Step(1f / 64f, states, chars, contacts);
                Interlocked.Increment(ref updates);
            }
        }) { IsBackground = true, Name = "steady-region-step" };

        // Two threads open and close regions at once, each with a provider attached to its own body and a step in between,
        // so the system map changes while the steady region is inside Update and calling OnStep.
        const int perThread = 25;
        int done = 0;
        Exception failure = null;
        var openers = new List<Thread>();
        for (int k = 0; k < 2; k++)
        {
            int id = k;
            openers.Add(new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        var t = new JoltTestBackend(Region($"Cycle{id}-{i}"));
                        BodyId box = Floater(t);
                        t.B.AttachStepForceProvider(box, new Accelerate());
                        t.B.Step(1f / 64f, new BodyState[8], new CharacterState[2], new ContactReport[8]);
                        if (i % 2 == 0)
                            t.B.RemoveBody(box);
                        t.Dispose();   // the attached provider and its listener go with the region
                        Interlocked.Increment(ref done);
                    }
                }
                catch (Exception e) { failure = e; }
            }) { IsBackground = true, Name = "region-cycle-" + k });
        }

        var sw = Stopwatch.StartNew();
        stepper.Start();
        foreach (Thread th in openers) th.Start();
        bool finished = true;
        foreach (Thread th in openers)
            finished &= th.Join(Limit);
        stop.Cancel();
        bool stepperStopped = stepper.Join(Limit);
        _out.WriteLine($"{Volatile.Read(ref done)} regions created and closed in {sw.ElapsedMilliseconds} ms while the steady region ran {Interlocked.Read(ref updates)} Updates with a listener");

        Assert.True(finished, "creating and closing regions did not finish within the limit - a deadlock between create/dispose and Step");
        Assert.True(stepperStopped, "the steady region's Step did not return");
        Assert.Null(failure);
        Assert.Equal(2 * perThread, done);
        Assert.True(Interlocked.Read(ref updates) > 0);
        Assert.Equal(0, steady.B.StepForceProviderFaults);
    }

    [Fact]
    public void Dispose_and_Initialize_wait_for_an_Update_in_flight_and_Dispose_does_not_hold_its_own_lock_while_it_waits()
    {
        using var holder = new JoltTestBackend(Region("Holder"));
        var b = new JoltTestBackend(Region("B"));
        BodyId box = Floater(b);

        // Hold the one pool's gate, as a running Update does.
        IDisposable hold = holder.B.HoldPoolGateForTest();
        var disposed = new ManualResetEventSlim(false);
        var disposer = new Thread(() => { b.Dispose(); disposed.Set(); }) { IsBackground = true, Name = "dispose-B" };
        var opened = new ManualResetEventSlim(false);
        JoltTestBackend c = null;
        var opener = new Thread(() => { c = new JoltTestBackend(Region("C")); opened.Set(); }) { IsBackground = true, Name = "create-C" };
        disposer.Start();
        opener.Start();
        try
        {
            // Not finishing while the gate is held is the thing to show, so this is a real wait.
            Assert.False(disposed.Wait(500), "Dispose destroyed the physics system while a pool gate was held (an Update in flight)");
            Assert.False(opened.Wait(10), "Initialize created a physics system while a pool gate was held");

            // Dispose is waiting for the gate, and has not taken B's _simLock: B's own scene-thread calls still return.
            bool bodyOpReturned = false;
            var op = new Thread(() => { b.B.IsBodyAwake(box); bodyOpReturned = true; }) { IsBackground = true, Name = "scene-call-B" };
            op.Start();
            Assert.True(op.Join(Limit), "a body call on B blocked while Dispose waited for the pool gate - Dispose took _simLock before the gates");
            Assert.True(bodyOpReturned);
        }
        finally
        {
            hold.Dispose();   // the thread that took it (Monitor)
        }

        Assert.True(disposed.Wait(Limit), "Dispose did not complete after the gate was released");
        Assert.True(opened.Wait(Limit), "Initialize did not complete after the gate was released");
        Assert.True(disposer.Join(Limit) && opener.Join(Limit));
        c.Dispose();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public void Taking_every_gate_in_pool_order_does_not_deadlock_against_steps_taking_one(int pools, bool fair)
    {
        using var alive = new JoltTestBackend(Region("Foundation"));   // keeps Foundation up for the stand-alone pools
        (bool completed, int violations) = JoltPhysicsBackend.AllGatesProbeForTest(pools, fair, rounds: 300, Limit);
        _out.WriteLine($"{pools} pools, fair handoff {fair}: completed {completed}, violations {violations}");
        Assert.True(completed, "the all-gates takers and the one-gate takers deadlocked");
        Assert.Equal(0, violations);
    }
}
