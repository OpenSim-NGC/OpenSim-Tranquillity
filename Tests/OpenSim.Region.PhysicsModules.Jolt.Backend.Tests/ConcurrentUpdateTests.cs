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
/// Every region's Step runs PhysicsSystem::Update on a JobSystemThreadPool. A pool's job queue
/// is a fixed 1024-slot ring shared by every Update on it, and a worker whose running job queues into a full ring
/// waits forever on its own head - so concurrent Updates on one pool wedge (4 regions x 300 boxes, every time,
/// whatever maxJobs was). The fix: one Update at a time per pool, [Jolt] JobPools pools.
///
/// <para>Pools are process-wide and sized by the first backend, so every test here starts with no backend alive
/// (the serial collection runs alone) and asserts the pool count it asked for actually took effect.</para>
///
/// <para>Stepping threads are background threads: when a run wedges, the test fails on its own time limit and the
/// host can still exit (the wedged backends are deliberately not disposed - Dispose would wait on their _simLock
/// forever). --blame-hang-timeout is the backstop.</para>
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ConcurrentUpdateTests
{
    private readonly ITestOutputHelper _out;
    public ConcurrentUpdateTests(ITestOutputHelper output) { _out = output; }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private static PhysicsBackendSettings Pools(PhysicsBackendSettings s, int jobPools)
    {
        s.JobPools = jobPools;
        return s;
    }

    /// <summary>
    /// <paramref name="regions"/> backends, each a ground box plus <paramref name="boxes"/> dynamic boxes dropped
    /// into a pile, each stepping <paramref name="frames"/> frames (CollisionSteps 6) on its own thread, all
    /// released by one start gate so their Steps overlap. <paramref name="beforeStart"/> runs on the backends before the
    /// gate opens. Returns the backends (disposed only on success).
    /// </summary>
    private List<JoltTestBackend> RunRegions(int regions, int boxes, int frames, int jobPools, out TimeSpan elapsed,
                                             Action<List<JoltTestBackend>> beforeStart = null)
    {
        var backends = new List<JoltTestBackend>();
        for (var r = 0; r < regions; r++)
        {
            var t = new JoltTestBackend(Pools(JoltTestBackend.Settings(), jobPools));
            t.Ground();
            var box = t.B.CreateBoxShape(new Vector3(0.4f));
            var side = (int)Math.Ceiling(Math.Sqrt(boxes / 3.0));
            for (var i = 0; i < boxes; i++)
            {
                int x = i % side, y = (i / side) % side, z = i / (side * side);
                // A loose lattice a little above the ground, slightly staggered per layer, so it collapses into a pile.
                t.Dynamic(box, new Vector3(120f + x * 0.85f + (z % 2) * 0.3f, 120f + y * 0.85f + (z % 2) * 0.3f, 1f + z * 0.9f), (uint)(1 + i));
            }
            backends.Add(t);
        }
        // No earlier backend sized the pools: they are what this run asked for, or one on a native not safe for more.
        var first = backends[0].B.GetCapacityStats();
        Assert.Equal(jobPools, first.JobPoolsRequested);
        Assert.Equal(PoolsOnThisNative(jobPools), first.JobPools);

        using var gate = new ManualResetEventSlim(false);
        var threads = new List<Thread>();
        foreach (var t in backends)
        {
            var th = new Thread(() =>
            {
                var bodies = new BodyState[4096];
                var chars = new CharacterState[8];
                var contacts = new ContactReport[16384];
                gate.Wait();
                for (var f = 0; f < frames; f++)
                    t.B.Step(1f / 11f, bodies, chars, contacts);
            }) { IsBackground = true, Name = "region-step" };
            th.Start();
            threads.Add(th);
        }

        beforeStart?.Invoke(backends);
        var sw = Stopwatch.StartNew();
        gate.Set();
        foreach (var th in threads)
        {
            var left = Limit - sw.Elapsed;
            if (left < TimeSpan.Zero || !th.Join(left))
            {
                _out.WriteLine($"WEDGED: {regions} regions x {boxes} boxes on {jobPools} pool(s), {threads.Count(x => x.IsAlive)} still inside Step after {sw.Elapsed.TotalSeconds:0.0}s");
                Assert.Fail($"{regions} concurrent regions x {boxes} boxes on {jobPools} pool(s) did not finish {frames} frames within {Limit.TotalSeconds:0}s - a job pool wedged");
            }
        }
        elapsed = sw.Elapsed;
        _out.WriteLine($"{regions} regions x {boxes} boxes x {frames} frames on {jobPools} pool(s) finished in {elapsed.TotalSeconds:0.00}s");
        return backends;
    }

    private void Report(List<JoltTestBackend> regions)
    {
        foreach (var t in regions)
        {
            var s = t.B.GetCapacityStats();
            _out.WriteLine($"  pool={s.PoolIndex}/{s.JobPools} threadsPerPool={s.JobThreadsPerPool} peakInside={s.PoolPeakInside} " +
                           $"waits={s.UpdateGateWaits} waitMs total={s.UpdateGateWaitMsTotal:0.0} max={s.UpdateGateWaitMsMax:0.0}");
        }
    }

    private static void DisposeAll(List<JoltTestBackend> regions)
    {
        foreach (var t in regions)
            t.Dispose();
    }

    [Fact]
    public void Concurrent_regions_do_not_wedge_the_shared_pool()
    {
        // The default: one pool, so the four regions' Updates take turns.
        var regions = RunRegions(regions: 4, boxes: 300, frames: 200, jobPools: PhysicsBackendSettings.Default.JobPools, out _);
        Report(regions);
        Assert.All(regions, t => Assert.Equal(1, t.B.GetCapacityStats().PoolPeakInside));
        DisposeAll(regions);
    }

    private static bool NativeAllowsPools => JoltNative.EnsureLoaded(allowUnrecorded: false).SafeForMultiplePools;

    // The pools a run asking for `jobPools` must get on the native this process loaded: all of them on one the record
    // marks safe for more than one pool, else one (every recorded build is one: the stock package native). Worked out here, not
    // with the backend's own resolver, so a backend that skipped the fallback fails this check before it steps.
    private static int PoolsOnThisNative(int jobPools) => NativeAllowsPools ? jobPools : 1;

    // On a native that runs one pool, a run that asked for more: one pool, the count asked for, and the reason.
    private static void AssertOnePoolFallback(List<JoltTestBackend> regions, int asked)
    {
        Assert.All(regions.Select(t => t.B.GetCapacityStats()), s =>
        {
            Assert.Equal(1, s.JobPools);
            Assert.Equal(asked, s.JobPoolsRequested);
            Assert.Equal(0, s.PoolIndex);
            Assert.Contains("is not safe for more than one job pool", s.JobPoolsLimitedBy);
        });
    }

    // The multi-pool halves run only on a native the record marks safe for more than one pool; on the stock native the
    // one-pool fallback is checked instead.
    [Fact]
    public void Heavy_regions_one_update_per_pool()
    {
        foreach (var pools in new[] { 1, 2 })
        {
            var regions = RunRegions(regions: 4, boxes: 1000, frames: 100, jobPools: pools, out var took);
            Report(regions);
            _out.WriteLine($"HEAVY JobPools={pools}: {took.TotalSeconds:0.00}s");
            Assert.All(regions, t => Assert.Equal(1, t.B.GetCapacityStats().PoolPeakInside));
            if (pools > 1 && !NativeAllowsPools)
                AssertOnePoolFallback(regions, pools);
            DisposeAll(regions);
        }
    }

    [Fact]
    public void Ten_regions_complete()
    {
        foreach (var pools in new[] { 1, 3 })
        {
            var regions = RunRegions(regions: 10, boxes: 100, frames: 100, jobPools: pools, out _);
            Report(regions);
            var stats = regions.Select(t => t.B.GetCapacityStats()).ToList();
            int inUse = PoolsOnThisNative(pools);
            Assert.All(stats, s => Assert.Equal(inUse, s.JobPools));
            Assert.All(stats, s => Assert.Equal(1, s.PoolPeakInside));
            // Fewest regions first, ties to the lowest index: 10 over 3 pools is 4/3/3; over 1 pool, all 10.
            var spread = Enumerable.Range(0, inUse).Select(p => stats.Count(s => s.PoolIndex == p)).ToArray();
            _out.WriteLine($"JobPools={pools} ({inUse} in use) spread: {string.Join("/", spread)}");
            Assert.Equal(inUse == 3 ? new[] { 4, 3, 3 } : new[] { 10 }, spread);
            if (pools > 1 && !NativeAllowsPools)
                AssertOnePoolFallback(regions, pools);
            DisposeAll(regions);
        }
    }

    [Fact]
    public void Gate_wait_is_counted()
    {
        // The first step to take the pool keeps it until the other region's step has reached the gate and found it held,
        // so the two overlap however the threads are scheduled: on a stalled machine one region could otherwise run all
        // its frames before the other starts.
        using var otherWaits = new ManualResetEventSlim(false);
        var first = 0;
        var overlapped = false;
        var regions = RunRegions(regions: 2, boxes: 200, frames: 60, jobPools: 1, out _, beforeStart: list =>
        {
            list[0].B.SetPoolHooksForTest((_, mustWait) => { if (mustWait) otherWaits.Set(); }, null);
            foreach (var t in list)
                t.B.GateTakenForTest = _ =>
                {
                    if (Interlocked.Exchange(ref first, 1) == 0)
                        Volatile.Write(ref overlapped, otherWaits.Wait(Limit));
                };
        });
        regions[0].B.SetPoolHooksForTest(null, null);
        Assert.True(Volatile.Read(ref overlapped), "the other region's step never reached the pool while the first held it");
        Report(regions);
        var stats = regions.Select(r => r.B.GetCapacityStats()).ToList();
        Assert.All(stats, s => Assert.Equal(0, s.PoolIndex));
        Assert.All(stats, s => Assert.Equal(1, s.PoolPeakInside));
        Assert.True(stats.Sum(s => s.UpdateGateWaits) > 0, "two regions on one pool must have waited at its gate");
        Assert.True(stats.Sum(s => s.UpdateGateWaitMsTotal) > 0);
        Assert.True(stats.Max(s => s.UpdateGateWaitMsMax) > 0);
        DisposeAll(regions);
    }
}
