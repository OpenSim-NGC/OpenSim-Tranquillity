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
/// The backend's per-body force providers (IStepForceProvider): one step listener per physics system, registered when the
/// first provider attaches and removed when the last detaches, calling each provider before every collision step with that
/// step's own time. A provider changes velocities only. These tests use a constant-acceleration provider on a body with no
/// gravity and no damping, so the velocity after a time T is exactly a x T.
///
/// They run in the serial collection because a real backend steps on the process's shared job pool, and some assert on
/// the pool's gate.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class StepForceProviderTests
{
    private readonly ITestOutputHelper _out;
    public StepForceProviderTests(ITestOutputHelper output) => _out = output;

    /// <summary>Adds a constant acceleration as a velocity change and records each call.</summary>
    private sealed class ConstantAcceleration : IStepForceProvider
    {
        private readonly object _gate = new();
        public readonly Vector3 Acceleration;
        public readonly List<(float Dt, bool First, bool Last)> Calls = new();
        public ConstantAcceleration(Vector3 acceleration) => Acceleration = acceleration;

        public void OnStep(in StepBody body, in StepInfo step)
        {
            body.AddLinearVelocity(Acceleration * step.DeltaTime);
            lock (_gate) Calls.Add((step.DeltaTime, step.IsFirstStep, step.IsLastStep));
        }

        public int Count { get { lock (_gate) return Calls.Count; } }
    }

    private sealed class Throwing : IStepForceProvider
    {
        public int Calls;
        public void OnStep(in StepBody body, in StepInfo step)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("a provider that fails");
        }
    }

    private static PhysicsBackendSettings SettingsWith(int collisionSteps)
    {
        PhysicsBackendSettings s = JoltTestBackend.Settings();
        s.CollisionSteps = collisionSteps;
        return s;
    }

    /// <summary>A body floating in free space: no gravity, no damping.</summary>
    private static BodyId Floater(JoltTestBackend t, int index = 0, bool keepAwake = false)
    {
        // Spaced out so that bodies never touch.
        BodyId box = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f)),
            new Vector3(20f + 2f * (index % 100), 20f + 2f * (index / 100), 100f));
        if (keepAwake)
            t.B.SetBodyLinearVelocity(box, new Vector3(0f, 0f, 1f));   // moving, so it does not fall asleep
        t.B.SetBodyGravityFactor(box, 0f);
        t.B.SetBodyDamping(box, 0f, 0f);
        return box;
    }

    private static void Step(JoltPhysicsBackend b, float dt)
        => b.Step(dt, new BodyState[16], new CharacterState[4], new ContactReport[64]);

    private static Vector3 VelocityOf(JoltPhysicsBackend b, BodyId body)
    {
        Assert.True(b.TryGetBodyState(body, out BodyState state));
        return state.LinearVelocity;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_velocity_change_is_applied_once_per_collision_step_with_the_steps_own_time(int n)
    {
        const float dt = 1f / 45f;
        using var t = new JoltTestBackend(SettingsWith(n));
        BodyId box = Floater(t);
        var provider = new ConstantAcceleration(new Vector3(10f, 0f, 0f));
        using (t.B.AttachStepForceProvider(box, provider))
        {
            const int updates = 3;
            for (int i = 0; i < updates; i++)
                Step(t.B, dt);

            Assert.Equal(updates * n, provider.Count);
            for (int i = 0; i < provider.Calls.Count; i++)
            {
                Assert.Equal(dt / n, provider.Calls[i].Dt, 6);
                Assert.Equal(i % n == 0, provider.Calls[i].First);
                Assert.Equal(i % n == n - 1, provider.Calls[i].Last);
            }
            // Once per collision step, not once per Update and not repeated: a x (3 Updates) exactly.
            Assert.Equal(10f * updates * dt, VelocityOf(t.B, box).X, 3);
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(45)]
    [InlineData(64)]
    public void The_same_simulated_time_gives_the_same_velocity_at_every_update_rate_and_collision_step_count(int rate)
    {
        const float acceleration = 10f;
        const int updatesPerSecond = 1;   // one simulated second at each rate
        double reference = double.NaN;
        for (int n = 1; n <= 4; n++)
        {
            using var t = new JoltTestBackend(SettingsWith(n));
            BodyId box = Floater(t);
            var provider = new ConstantAcceleration(new Vector3(acceleration, 0f, 0f));
            using (t.B.AttachStepForceProvider(box, provider))
            {
                float dt = 1f / rate;
                for (int i = 0; i < rate * updatesPerSecond; i++)
                    Step(t.B, dt);
                float vx = VelocityOf(t.B, box).X;
                double expected = acceleration * rate * updatesPerSecond * dt;
                _out.WriteLine($"{rate} Hz, n = {n}: vx {vx:0.0000} m/s after {rate * updatesPerSecond} Updates ({provider.Count} calls), a x T = {expected:0.0000}");
                Assert.Equal(rate * updatesPerSecond * n, provider.Count);
                Assert.InRange(vx / expected, 0.99, 1.01);
                if (double.IsNaN(reference))
                    reference = vx;
                else
                    Assert.InRange(vx / reference, 0.99, 1.01);
            }
        }
    }

    [Fact]
    public void Providers_on_different_regions_do_not_interfere()
    {
        const float dt = 1f / 45f;
        using var a = new JoltTestBackend(SettingsWith(2));
        using var b = new JoltTestBackend(SettingsWith(3));
        BodyId boxA = Floater(a);
        BodyId boxB = Floater(b);
        var pa = new ConstantAcceleration(new Vector3(10f, 0f, 0f));
        var pb = new ConstantAcceleration(new Vector3(0f, -4f, 0f));
        using IDisposable ha = a.B.AttachStepForceProvider(boxA, pa);
        // Region A has a listener and B has none yet: stepping B must not call A's provider, nor change A's body.
        Assert.True(a.B.HasStepListener);
        Assert.False(b.B.HasStepListener);
        Step(b.B, dt);
        Assert.Equal(0, pa.Count);

        using IDisposable hb = b.B.AttachStepForceProvider(boxB, pb);
        for (int i = 0; i < 10; i++)
        {
            Step(a.B, dt);
            Step(b.B, dt);
        }
        Assert.Equal(10 * 2, pa.Count);
        Assert.Equal(10 * 3, pb.Count);
        Vector3 va = VelocityOf(a.B, boxA);
        Vector3 vb = VelocityOf(b.B, boxB);
        Assert.Equal(10f * 10 * dt, va.X, 3);
        Assert.Equal(0f, va.Y, 4);
        Assert.Equal(-4f * 10 * dt, vb.Y, 3);
        Assert.Equal(0f, vb.X, 4);

        // Detaching A's provider takes A's listener off and leaves B's.
        ha.Dispose();
        Assert.False(a.B.HasStepListener);
        Assert.True(b.B.HasStepListener);
        int before = pb.Count;
        Step(b.B, dt);
        Assert.Equal(before + 3, pb.Count);
    }

    [Fact]
    public void Detaching_the_last_provider_removes_the_listener()
    {
        const float dt = 1f / 45f;
        using var t = new JoltTestBackend(SettingsWith(2));
        BodyId one = Floater(t);
        BodyId two = Floater(t);
        Assert.False(t.B.HasStepListener);   // nothing attached, nothing registered

        var p1 = new ConstantAcceleration(new Vector3(10f, 0f, 0f));
        var p2 = new ConstantAcceleration(new Vector3(0f, 10f, 0f));
        IDisposable h1 = t.B.AttachStepForceProvider(one, p1);
        Assert.True(t.B.HasStepListener);
        IDisposable h2 = t.B.AttachStepForceProvider(two, p2);
        Assert.Equal(2, t.B.StepForceProviderCount);
        Step(t.B, dt);
        Assert.Equal(2, p1.Count);
        Assert.Equal(2, p2.Count);

        h1.Dispose();
        h1.Dispose();   // a second dispose does nothing
        Assert.True(t.B.HasStepListener);   // one provider left
        Assert.Equal(1, t.B.StepForceProviderCount);
        Step(t.B, dt);
        Assert.Equal(2, p1.Count);
        Assert.Equal(4, p2.Count);

        h2.Dispose();
        Assert.False(t.B.HasStepListener);
        Assert.Equal(0, t.B.StepForceProviderCount);
        Step(t.B, dt);
        Step(t.B, dt);
        Assert.Equal(2, p1.Count);
        Assert.Equal(4, p2.Count);
    }

    [Fact]
    public void Removing_a_body_detaches_its_providers_and_disposing_the_backend_with_a_provider_attached_is_clean()
    {
        const float dt = 1f / 45f;
        var t = new JoltTestBackend(SettingsWith(2));
        BodyId one = Floater(t);
        BodyId two = Floater(t);
        var p1 = new ConstantAcceleration(new Vector3(10f, 0f, 0f));
        var p2 = new ConstantAcceleration(new Vector3(0f, 10f, 0f));
        IDisposable h1 = t.B.AttachStepForceProvider(one, p1);
        IDisposable h2 = t.B.AttachStepForceProvider(two, p2);
        Step(t.B, dt);

        t.B.RemoveBody(one);
        Assert.Equal(1, t.B.StepForceProviderCount);
        Assert.True(t.B.HasStepListener);
        Step(t.B, dt);
        Assert.Equal(2, p1.Count);
        h1.Dispose();   // already gone: nothing happens

        t.B.RemoveBody(two);
        Assert.False(t.B.HasStepListener);

        BodyId three = Floater(t);
        IDisposable h3 = t.B.AttachStepForceProvider(three, new ConstantAcceleration(Vector3.UnitX));
        Step(t.B, dt);
        t.Dispose();   // with the provider and its listener still attached
        h3.Dispose();  // after the backend is gone: no fault
        h2.Dispose();
        Assert.Throws<ObjectDisposedException>(() => t.B.AttachStepForceProvider(three, p1));
    }

    [Fact]
    public void A_provider_that_throws_is_counted_and_does_not_stop_the_step_or_the_other_providers()
    {
        const float dt = 1f / 45f;
        using var t = new JoltTestBackend(SettingsWith(2));
        BodyId bad = Floater(t);
        BodyId good = Floater(t);
        var thrower = new Throwing();
        var ok = new ConstantAcceleration(new Vector3(10f, 0f, 0f));
        using IDisposable h1 = t.B.AttachStepForceProvider(bad, thrower);
        using IDisposable h2 = t.B.AttachStepForceProvider(good, ok);
        Step(t.B, dt);
        Step(t.B, dt);
        Assert.Equal(4, thrower.Calls);
        Assert.Equal(4, t.B.StepForceProviderFaults);
        Assert.IsType<InvalidOperationException>(t.B.LastStepForceProviderFault);
        Assert.Equal(4, ok.Count);
        Assert.Equal(10f * 2 * dt, VelocityOf(t.B, good).X, 3);
    }

    [Fact]
    public void A_body_that_is_asleep_is_not_called_and_an_invalid_body_is_refused()
    {
        const float dt = 1f / 45f;
        using var t = new JoltTestBackend(SettingsWith(2));
        BodyId asleep = t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(128f, 128f, 100f), startActive: false);
        t.B.SetBodyGravityFactor(asleep, 0f);
        var p = new ConstantAcceleration(new Vector3(10f, 0f, 0f));
        using IDisposable h = t.B.AttachStepForceProvider(asleep, p);
        Step(t.B, dt);
        Step(t.B, dt);
        Assert.Equal(0, p.Count);
        Assert.Throws<ArgumentException>(() => t.B.AttachStepForceProvider(BodyId.Invalid, p));
        Assert.Throws<ArgumentNullException>(() => t.B.AttachStepForceProvider(asleep, null!));
    }

    [Fact]
    public void Attaching_and_detaching_while_another_thread_steps_is_safe()
    {
        const float dt = 1f / 64f;
        using var t = new JoltTestBackend(SettingsWith(2));
        BodyId box = Floater(t);
        BodyId other = Floater(t);
        using var stop = new CancellationTokenSource();
        var stepper = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
                Step(t.B, dt);
        }) { IsBackground = true, Name = "region-step" };
        stepper.Start();
        var sw = Stopwatch.StartNew();
        int cycles = 0;
        try
        {
            // A listener is added and removed on every cycle, between Updates (the attach waits for the running one).
            while (sw.Elapsed < TimeSpan.FromSeconds(3))
            {
                var p = new ConstantAcceleration(new Vector3(0.01f, 0f, 0f));
                IDisposable h = t.B.AttachStepForceProvider(box, p);
                Thread.Sleep(2);
                h.Dispose();
                cycles++;
            }
        }
        finally
        {
            stop.Cancel();
            Assert.True(stepper.Join(TimeSpan.FromSeconds(60)), "the stepping thread did not stop");
        }
        _out.WriteLine($"{cycles} attach/detach cycles in 3 s while stepping at 64 Hz, faults {t.B.StepForceProviderFaults}");
        Assert.True(cycles > 5);
        Assert.Equal(0, t.B.StepForceProviderFaults);
        Assert.False(t.B.HasStepListener);
        GC.KeepAlive(other);
    }

    /// <summary>A provider that does the least a provider can: it reads nothing and changes nothing.</summary>
    private sealed class Trivial : IStepForceProvider
    {
        public void OnStep(in StepBody body, in StepInfo step) { }
    }

    // Cost of the listener at 64 Hz with 2 collision steps: the time spent in OnStep itself (timed inside the managed
    // callback), and the whole round trip as Update time per OnStep call against the same Updates with no listener at all
    // (the native-to-managed call, the barrier it adds to the step, and the provider loop). Printed, with a very loose bound:
    // the point is the number.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void Cost_of_an_OnStep_call_at_64_Hz_with_two_collision_steps(int providers)
    {
        const float dt = 1f / 64f;
        const int updates = 2000;
        const int n = 2;

        double baseline = MeasureUpdates(withListener: false, 0, dt, n, updates, out _, out _);
        double withListener = MeasureUpdates(withListener: true, providers, dt, n, updates, out long onStepTicks, out long calls);

        double insideUs = calls == 0 ? 0 : onStepTicks * 1e6 / Stopwatch.Frequency / calls;
        double roundTripUs = (withListener - baseline) * 1000.0 / (updates * n);
        _out.WriteLine($"providers {providers,3}: OnStep body {insideUs,8:0.00} us per call ({calls} calls); " +
                       $"Update {baseline / updates * 1000.0:0.0} us -> {withListener / updates * 1000.0:0.0} us, " +
                       $"added {roundTripUs:0.00} us per OnStep call (round trip, both steps of every Update)");
        Assert.Equal(updates * n, calls);
        Assert.True(roundTripUs < 5000, "an OnStep round trip took over 5 ms");
    }

    // Total Update time in milliseconds for `updates` Updates of a region with `providers` trivial providers (or none),
    // with a listener registered or not.
    private static double MeasureUpdates(bool withListener, int providers, float dt, int n, int updates, out long onStepTicks, out long calls)
    {
        using var t = new JoltTestBackend(SettingsWith(n));
        BodyId[] bodies = new BodyId[Math.Max(1, providers)];
        for (int i = 0; i < bodies.Length; i++)
            bodies[i] = Floater(t, i, keepAwake: true);
        var handles = new List<IDisposable>();
        if (withListener)
        {
            t.B.KeepStepListenerForTest();   // the empty-listener case keeps it registered with no provider
            for (int i = 0; i < providers; i++)
                handles.Add(t.B.AttachStepForceProvider(bodies[i], new Trivial()));
            t.B.TimeStepListenerForTest = true;
        }
        var states = new BodyState[256];
        var chars = new CharacterState[4];
        var contacts = new ContactReport[64];
        for (int i = 0; i < 200; i++)   // warm up
            t.B.Step(dt, states, chars, contacts);
        long t0 = t.B.OnStepTicksForTest, c0 = t.B.OnStepCallsForTest;
        double physicsMs = 0;
        for (int i = 0; i < updates; i++)
            physicsMs += t.B.Step(dt, states, chars, contacts).PhysicsMilliseconds;
        onStepTicks = t.B.OnStepTicksForTest - t0;
        calls = t.B.OnStepCallsForTest - c0;
        foreach (IDisposable h in handles) h.Dispose();
        return physicsMs;
    }
}
