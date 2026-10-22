/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The job pool's handoff ([Jolt] JobPoolFairHandoff) and the waits a step reports. Several backends share one pool,
/// one of them heavy. With the key off the gate is the Monitor it has always been; with it on, a ticket lock serves
/// steps in the order they arrived. In both, only one step runs on a pool at a time, and teardown neither strands a
/// waiter nor loses a step. Serial: the job pools are process-wide, and the first backend created decides their mode.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class PoolHandoffTests
{
    private readonly ITestOutputHelper _out;
    public PoolHandoffTests(ITestOutputHelper output) { _out = output; }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private static JoltTestBackend Region(string name, bool fair, int boxes = 0)
    {
        var s = JoltTestBackend.Settings();
        s.JobPools = 1;
        s.JobPoolFairHandoff = fair;
        s.RegionName = name;
        var t = new JoltTestBackend(s);
        t.Ground();
        if (boxes > 0)
        {
            var box = t.B.CreateBoxShape(new Vector3(0.4f));
            var side = (int)Math.Ceiling(Math.Sqrt(boxes / 3.0));
            for (var i = 0; i < boxes; i++)
            {
                int x = i % side, y = (i / side) % side, z = i / (side * side);
                t.Dynamic(box, new Vector3(120f + x * 0.85f + (z % 2) * 0.3f, 120f + y * 0.85f + (z % 2) * 0.3f, 1f + z * 0.9f), (uint)(1 + i));
            }
        }
        return t;
    }

    /// <summary>A heavy region and two light ones, created on fresh pools in the given mode.</summary>
    private static List<JoltTestBackend> Scenes(bool fair, int heavyBoxes = 300)
    {
        var list = new List<JoltTestBackend> { Region("Heavy", fair, heavyBoxes), Region("Light 1", fair), Region("Light 2", fair) };
        var s = list[0].B.GetCapacityStats();
        Assert.Equal(1, s.JobPools);                  // no earlier backend sized the pools
        Assert.Equal(fair, s.JobPoolFairHandoff);
        return list;
    }

    private static void DisposeAll(IEnumerable<JoltTestBackend> list)
    {
        foreach (var t in list)
            t.Dispose();
    }

    private static Thread Start(string name, Action body)
    {
        var th = new Thread(() => body()) { IsBackground = true, Name = name };
        th.Start();
        return th;
    }

    private static void Join(Thread th, string what)
        => Assert.True(th.Join(Limit), $"{what} did not finish within {Limit.TotalSeconds:0} s");

    private static void WaitUntil(Func<bool> condition, string what)
        => Assert.True(SpinWait.SpinUntil(condition, Limit), $"timed out waiting until {what}");

    /// <summary>
    /// Set when region <paramref name="name"/>'s step reaches the pool's gate and finds it must wait. From then on that
    /// step waits for the gate whatever the scheduler does, so the test can go on without guessing from thread states.
    /// The hook is the pool's: <paramref name="onPool"/> is any region on it.
    /// </summary>
    private static ManualResetEventSlim WaitsAtGate(JoltTestBackend onPool, string name)
    {
        var waits = new ManualResetEventSlim(false);
        onPool.B.SetPoolHooksForTest((region, mustWait) => { if (mustWait && region == name) waits.Set(); }, null);
        return waits;
    }

    private static StepResult Step(JoltTestBackend t) => t.Step(bodyBuf: 4096, contactBuf: 16384);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_steps_never_run_at_once_on_one_pool(bool fair)
    {
        var scenes = Scenes(fair);
        const int frames = 40;
        var taken = new ConcurrentDictionary<string, int>();
        var inside = 0;
        var overlapped = 0;
        foreach (var t in scenes)
            t.B.GateTakenForTest = b =>
            {
                taken.AddOrUpdate(Name(b), 1, (_, n) => n + 1);
                if (Interlocked.Increment(ref inside) != 1)
                    Interlocked.Increment(ref overlapped);
                Thread.Yield();   // widen the window a second step would need
                Interlocked.Decrement(ref inside);
            };
        try
        {
            using var go = new ManualResetEventSlim(false);
            var threads = scenes.Select(t => Start("region-step", () =>
            {
                go.Wait();
                for (var f = 0; f < frames; f++)
                    Step(t);
            })).ToList();
            go.Set();
            foreach (var th in threads)
                Join(th, "a region's steps");

            foreach (var t in scenes)
            {
                var s = t.B.GetCapacityStats();
                Assert.Equal(1, s.PoolPeakInside);
                Assert.Equal(frames, taken[Name(t.B)]);
            }
            Assert.Equal(0, overlapped);
            _out.WriteLine($"fair={fair}: " + string.Join("; ", scenes.Select(t =>
            {
                var s = t.B.GetCapacityStats();
                return $"{Name(t.B)} waits={s.UpdateGateWaits} total={s.UpdateGateWaitMsTotal:0.0}ms max={s.UpdateGateWaitMsMax:0.00}ms";
            })));
        }
        finally
        {
            DisposeAll(scenes);
        }
    }

    [Fact]
    public void Fair_handoff_serves_waiters_in_arrival_order()
    {
        var holder = Region("Holder", fair: true);
        var waiters = new[] { Region("W0", true), Region("W1", true), Region("W2", true), Region("W3", true) };
        try
        {
            var order = new ConcurrentQueue<string>();
            foreach (var w in waiters)
                w.B.GateTakenForTest = b => order.Enqueue(Name(b));
            var rng = new Random(7);
            for (var round = 0; round < 12; round++)
            {
                var arrival = waiters.OrderBy(_ => rng.Next()).ToArray();
                order.Clear();
                var hold = holder.B.HoldPoolGateForTest();
                Assert.Equal(1, holder.B.PoolQueuedForTest);
                var threads = new List<Thread>();
                foreach (var w in arrival)
                {
                    var queuedBefore = holder.B.PoolQueuedForTest;
                    threads.Add(Start("waiter-step", () => Step(w)));
                    WaitUntil(() => holder.B.PoolQueuedForTest == queuedBefore + 1, $"{Name(w.B)} queued");
                }
                hold.Dispose();
                foreach (var th in threads)
                    Join(th, "a waiting step");
                Assert.Equal(arrival.Select(w => Name(w.B)), order.ToArray());
            }
            Assert.Equal(0, holder.B.PoolQueuedForTest);
        }
        finally
        {
            DisposeAll(waiters);
            holder.Dispose();
        }
    }

    /// <summary>
    /// A heavy region steps back to back, as it does through a heartbeat of several physics steps, while a light region
    /// steps now and then. With fair handoff the light region runs before the heavy region's next step: at most the step
    /// the heavy region had already queued for when the light one arrived goes first. The default lock gives no such
    /// bound; its figure is printed, not asserted. The light region arrives when its step reaches the pool (with fair
    /// handoff, when it has drawn its ticket), read by the pool's own hook: a stall of the test thread between deciding
    /// to step and reaching the pool moves the arrival with it, so it cannot count the heavy steps taken meanwhile.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_waiter_gets_the_pool_at_the_holders_next_step(bool fair)
    {
        var heavy = Region("Heavy", fair, boxes: 300);
        var light = Region("Light", fair);
        try
        {
            var heavySteps = 0;
            heavy.B.GateTakenForTest = _ => Interlocked.Increment(ref heavySteps);
            var arrivedAt = 0;
            var worst = 0;
            light.B.SetPoolHooksForTest((region, _) => { if (region == "Light") arrivedAt = Volatile.Read(ref heavySteps); }, null);
            light.B.GateTakenForTest = _ => worst = Math.Max(worst, Volatile.Read(ref heavySteps) - arrivedAt);
            var done = false;
            // Bounded, so that with the default lock, which promises no order, a light step kept waiting still ends.
            const int heavyMax = 3000;
            var heavyThread = Start("heavy-step", () =>
            {
                for (var k = 0; k < heavyMax && !Volatile.Read(ref done); k++)
                    Step(heavy);
            });
            const int lightSteps = 15;
            var lightTaken = 0;
            for (var i = 0; i < lightSteps && heavyThread.IsAlive; i++)
            {
                // Arrive while the heavy region is mid-run.
                var seen = Volatile.Read(ref heavySteps);
                WaitUntil(() => Volatile.Read(ref heavySteps) > seen || !heavyThread.IsAlive, "the heavy region stepped");
                Step(light);
                lightTaken++;
            }
            Volatile.Write(ref done, true);
            Join(heavyThread, "the heavy region's steps");
            var s = light.B.GetCapacityStats();
            _out.WriteLine($"fair={fair}: heavy steps taken between the light region's arrival and its step, worst of {lightTaken}: {worst}; " +
                           $"light waits={s.UpdateGateWaits} total={s.UpdateGateWaitMsTotal:0.0}ms max={s.UpdateGateWaitMsMax:0.00}ms");
            if (fair)
            {
                Assert.Equal(lightSteps, lightTaken);
                Assert.InRange(worst, 0, 1);
            }
        }
        finally
        {
            light.Dispose();
            heavy.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_create_step_and_teardown_neither_deadlocks_nor_loses_a_step(bool fair)
    {
        const int rounds = 10, frames = 10;
        for (var round = 0; round < rounds; round++)
        {
            var scenes = Scenes(fair, heavyBoxes: 100);
            var taken = new int[scenes.Count];
            for (var i = 0; i < scenes.Count; i++)
            {
                var k = i;
                scenes[i].B.GateTakenForTest = _ => Interlocked.Increment(ref taken[k]);
            }
            using var go = new ManualResetEventSlim(false);
            var threads = scenes.Select(t => Start("region-step", () =>
            {
                go.Wait();
                for (var f = 0; f < frames; f++)
                    Step(t);
            })).ToList();
            // One light region is removed part way through its steps, as a region shut down while the others run.
            var removed = scenes[2];
            var remover = Start("region-remove", () =>
            {
                go.Wait();
                SpinWait.SpinUntil(() => Volatile.Read(ref taken[2]) >= frames / 2, Limit);
                removed.Dispose();
            });
            go.Set();
            foreach (var th in threads)
                Join(th, $"round {round}: a region's steps");
            Join(remover, $"round {round}: the region's removal");
            for (var i = 0; i < scenes.Count; i++)
                Assert.Equal(frames, taken[i]);   // every Step got past the pool and returned, the removed region's too
            Assert.Equal(1, scenes[0].B.GetCapacityStats().PoolPeakInside);
            if (fair)
                Assert.Equal(0, scenes[0].B.PoolQueuedForTest);
            scenes[0].Dispose();
            scenes[1].Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_region_removed_while_it_waits_for_the_pool(bool fair)
    {
        var holder = Region("Holder", fair);
        var waiter = Region("Waiter", fair);
        var other = Region("Other", fair);
        try
        {
            StepResult waiterResult = default;
            var waits = WaitsAtGate(holder, "Waiter");
            var hold = holder.B.HoldPoolGateForTest();
            var stepper = Start("waiter-step", () => waiterResult = Step(waiter));
            Assert.True(waits.Wait(Limit), "the waiter did not reach the pool");
            // Removing a region destroys its physics system, which waits for every pool gate (an Update in flight may be
            // reading joltc's map of systems), so the removal waits behind the holder like the step does. The waiter holds
            // no region lock while it waits, so its scene-thread calls would still return.
            var removed = new ManualResetEventSlim(false);
            var remover = Start("waiter-remove", () => { waiter.Dispose(); removed.Set(); });
            Assert.False(removed.Wait(500), "the region was removed while the pool gate was held");
            hold.Dispose();
            Join(stepper, "the removed region's waiting step");
            Join(remover, "removing the waiting region");
            // Its step was ahead of the removal in the queue (or got the pool first), so it ran or found the backend gone.
            Assert.True(waiterResult.PhysicsMilliseconds >= 0f);

            // The pool was passed on: the next region steps.
            var next = Start("other-step", () => Step(other));
            Join(next, "the next region's step");
            if (fair)
                Assert.Equal(0, holder.B.PoolQueuedForTest);
        }
        finally
        {
            other.Dispose();
            holder.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_region_removed_while_it_holds_the_pool(bool fair)
    {
        var holder = Region("Holder", fair);
        var waiter = Region("Waiter", fair);
        try
        {
            using var inside = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            holder.B.GateTakenForTest = _ =>
            {
                inside.Set();
                release.Wait(Limit);
            };
            StepResult holderResult = default;
            var holding = Start("holder-step", () => holderResult = Step(holder));
            Assert.True(inside.Wait(Limit), "the holder did not take the pool");
            var waits = WaitsAtGate(waiter, "Waiter");
            var waiting = Start("waiter-step", () => Step(waiter));
            Assert.True(waits.Wait(Limit), "the second region did not reach the pool");

            // The holder has the pool but not yet its region lock: removing it waits for the pool like any create or destroy
            // of a physics system, so it completes only after the holder's step has ended and passed the pool on.
            var removed = new ManualResetEventSlim(false);
            var remover = Start("holder-remove", () => { holder.Dispose(); removed.Set(); });
            Assert.False(removed.Wait(500), "the region was removed while its own step held the pool");
            release.Set();
            Join(holding, "the removed region's step");
            Join(waiting, "the waiting region's step");
            Join(remover, "removing the region that holds the pool");
            Assert.True(holderResult.PhysicsMilliseconds >= 0f);
            if (fair)
                Assert.Equal(0, waiter.B.PoolQueuedForTest);
        }
        finally
        {
            waiter.Dispose();
            holder.Dispose();
        }
    }

    [Fact]
    public void The_default_lock_is_unchanged_with_the_key_off()
    {
        Assert.False(PhysicsBackendSettings.Default.JobPoolFairHandoff);
        Assert.False(new JoltConfig().JobPoolFairHandoff);
        var t = Region("Only", fair: false);
        try
        {
            Assert.False(t.B.GetCapacityStats().JobPoolFairHandoff);
            Assert.Equal(-1, t.B.PoolQueuedForTest);
            // The default gate is a Monitor: held by this thread, it is re-entrant here and owned by no other thread.
            using (t.B.HoldPoolGateForTest())
                Assert.True(Monitor.IsEntered(PoolGate(t.B)));
            Assert.False(Monitor.IsEntered(PoolGate(t.B)));
        }
        finally
        {
            t.Dispose();
        }
    }

    [Fact]
    public void A_step_reports_its_pool_wait_and_who_held_the_pool()
    {
        var holder = Region("Holder", fair: false);
        var waiter = Region("Waiter", fair: false);
        try
        {
            var first = Step(waiter);
            Assert.False(first.Waits.PoolWaited);
            Assert.Equal(0.0, first.Waits.PoolWaitMs);

            StepResult r = default;
            var waits = WaitsAtGate(holder, "Waiter");
            var hold = holder.B.HoldPoolGateForTest();
            var stepper = Start("waiter-step", () => r = Step(waiter));
            Assert.True(waits.Wait(Limit), "the waiter did not reach the pool");
            hold.Dispose();
            Join(stepper, "the waiting step");
            Assert.True(r.Waits.PoolWaited);
            Assert.True(r.Waits.PoolWaitMs > 0);
            Assert.Equal("Holder", r.Waits.PoolHeldBy);
            Assert.False(r.Waits.RegionLockWaited);
            var s = waiter.B.GetCapacityStats();
            Assert.Equal(1, s.UpdateGateWaits);
            Assert.Equal(0, s.RegionLockWaits);
        }
        finally
        {
            waiter.Dispose();
            holder.Dispose();
        }
    }

    /// <summary>
    /// A region that steps again while the pool is passing to another region (taken, but its taker has not yet named
    /// itself) waited behind that other region, and must say so. It had itself taken the pool last; the pool names the
    /// region holding it, not the one that last took it, so a region is never reported as waiting on itself.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_step_that_waits_while_the_pool_changes_hands_names_the_new_holder_not_itself(bool fair)
    {
        var a = Region("Region A", fair);
        var b = Region("Region B", fair);
        try
        {
            Assert.False(Step(a).Waits.PoolWaited);   // Region A took the pool last
            using var bTook = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var aWaits = new ManualResetEventSlim(false);
            a.B.SetPoolHooksForTest(
                (region, mustWait) => { if (mustWait && region == "Region A") aWaits.Set(); },
                region =>
                {
                    if (region != "Region B")
                        return;
                    bTook.Set();
                    release.Wait(Limit);   // Region B has the pool and has not named itself yet
                });
            var bStep = Start("region-b-step", () => Step(b));
            Assert.True(bTook.Wait(Limit), "Region B did not take the pool");
            StepResult r = default;
            var aStep = Start("region-a-step", () => r = Step(a));
            Assert.True(aWaits.Wait(Limit), "Region A did not reach the pool");
            release.Set();
            Join(bStep, "Region B's step");
            Join(aStep, "Region A's step");
            a.B.SetPoolHooksForTest(null, null);

            Assert.True(r.Waits.PoolWaited);
            Assert.Equal("Region B", r.Waits.PoolHeldBy);
        }
        finally
        {
            b.Dispose();
            a.Dispose();
        }
    }

    [Fact]
    public void A_step_reports_its_wait_for_the_region_lock_apart_from_the_pool()
    {
        var t = Region("Locked", fair: false);
        try
        {
            var simLock = typeof(JoltPhysicsBackend).GetField("_simLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(t.B)!;
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            // Another thread holds the region's lock, as a body change or query does.
            var locker = Start("region-lock-holder", () =>
            {
                lock (simLock)
                {
                    held.Set();
                    release.Wait(Limit);
                }
            });
            Assert.True(held.Wait(Limit));
            // Set by the step itself when it finds the lock held, so the lock is released only once the step must wait.
            var busy = new ManualResetEventSlim(false);
            t.B.RegionLockBusyForTest = () => busy.Set();
            StepResult r = default;
            var stepper = Start("locked-step", () => r = Step(t));
            Assert.True(busy.Wait(Limit), "the step did not reach the region lock");
            release.Set();
            Join(stepper, "the step");
            Join(locker, "the lock holder");

            Assert.True(r.Waits.RegionLockWaited);
            Assert.True(r.Waits.RegionLockWaitMs > 0);
            Assert.False(r.Waits.PoolWaited);
            Assert.True(r.PhysicsMilliseconds >= r.Waits.RegionLockWaitMs);   // the wait stays inside the step's time, as before
            var s = t.B.GetCapacityStats();
            Assert.Equal(1, s.RegionLockWaits);
            Assert.Equal(0, s.UpdateGateWaits);
            Assert.True(s.RegionLockWaitMsMax > 0);
        }
        finally
        {
            t.Dispose();
        }
    }

    private static string Name(JoltPhysicsBackend b)
        => ((PhysicsBackendSettings)typeof(JoltPhysicsBackend).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(b)!).RegionName;

    private static object PoolGate(JoltPhysicsBackend b)
    {
        var pool = typeof(JoltPhysicsBackend).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(b)!;
        return pool.GetType().GetField("Gate")!.GetValue(pool)!;
    }
}
