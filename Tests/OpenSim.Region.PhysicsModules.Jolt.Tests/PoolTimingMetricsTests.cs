/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The per-interval timing figures (a heartbeat's waits gathered over its steps, a region's interval, the `jolt capacity`
/// read-out) and the [Jolt] JobPoolFairHandoff key. Pure: no backend, no process-wide state, so these run in parallel.
/// </summary>
public class PoolTimingMetricsTests
{
    private static StepResult Step(float physicsMs, bool poolWaited = false, double poolMs = 0, string heldBy = null,
                                   bool lockWaited = false, double lockMs = 0)
        => new StepResult(0, 0, 0, false, false, 0, physicsMs, new StepWaits(poolWaited, poolMs, heldBy, lockWaited, lockMs));

    [Fact]
    public void A_heartbeat_gathers_its_steps_waits_and_longest_step()
    {
        var t = new HeartbeatTiming();
        t.Add(Step(1.5f));
        t.Add(Step(4.0f, poolWaited: true, poolMs: 2.0, heldBy: "Region A", lockWaited: true, lockMs: 0.5));
        t.Add(Step(2.0f, poolWaited: true, poolMs: 7.0, heldBy: "Region B"));
        t.Add(Step(0.5f, poolWaited: true, poolMs: 3.0, heldBy: "Region C", lockWaited: true, lockMs: 0.25));

        Assert.Equal(4.0, t.StepMaxMs);
        Assert.Equal(3, t.PoolWaits);
        Assert.Equal(12.0, t.PoolWaitMs);
        Assert.Equal(7.0, t.PoolWaitMaxMs);
        Assert.Equal("Region B", t.PoolHeldByAtMax);
        Assert.Equal(2, t.LockWaits);
        Assert.Equal(0.75, t.LockWaitMs);
        Assert.Equal(0.5, t.LockWaitMaxMs);
    }

    [Fact]
    public void An_interval_keeps_the_worst_figures_and_starts_again_from_zero()
    {
        var iv = new RegionInterval();
        Assert.Null(iv.Last);

        var quiet = new HeartbeatTiming();
        quiet.Add(Step(1.0f));
        var waited = new HeartbeatTiming();
        waited.Add(Step(3.0f, poolWaited: true, poolMs: 9.9, heldBy: "Heavy Region"));
        waited.Add(Step(2.0f, poolWaited: true, poolMs: 0.1, heldBy: "Other Region", lockWaited: true, lockMs: 0.4));

        iv.Record(1.0, in quiet, gapMs: 90.9, frameMs: 90.9);
        iv.Record(5.0, in waited, gapMs: 191.0, frameMs: 90.9);
        iv.Record(0.5, in quiet, gapMs: 0, frameMs: 90.9);

        string text = iv.Take();
        Assert.Equal("heartbeats=3, heartbeat max=5.00ms, step max=3.00ms, pool waits=2 total=10.0ms max=9.90ms held by Heavy Region, " +
                     "lock waits=1 total=0.4ms max=0.40ms, heartbeat gap max=191.0ms (frame 90.9ms), ray casts refused script=0 simulator=0", text);
        Assert.Equal(text, iv.Last);

        // The next interval starts from zero; the frame time carries over.
        Assert.Equal("heartbeats=0, heartbeat max=0.00ms, step max=0.00ms, pool waits=0 total=0.0ms max=0.00ms, " +
                     "lock waits=0 total=0.0ms max=0.00ms, heartbeat gap max=0.0ms (frame 90.9ms), ray casts refused script=0 simulator=0", iv.Take());
    }

    // The interval line gives the ray casts each budget refused during that interval, from the backend's cumulative
    // counts as the heartbeats saw them.
    [Fact]
    public void An_interval_gives_the_ray_casts_each_budget_refused_in_it()
    {
        var iv = new RegionInterval();
        var t = new HeartbeatTiming();
        t.Add(Step(1.0f));

        iv.Record(1.0, in t, 90.9, 90.9);
        iv.NoteRayCastsRefused(script: 120, simulator: 0);
        iv.Record(1.0, in t, 90.9, 90.9);
        iv.NoteRayCastsRefused(script: 8206, simulator: 3);
        Assert.EndsWith(", ray casts refused script=8206 simulator=3", iv.Take());

        iv.Record(1.0, in t, 90.9, 90.9);
        iv.NoteRayCastsRefused(script: 8300, simulator: 3);
        Assert.EndsWith(", ray casts refused script=94 simulator=0", iv.Take());

        // No heartbeat in an interval: nothing new to count.
        Assert.EndsWith(", ray casts refused script=0 simulator=0", iv.Take());
    }

    [Fact]
    public void Recording_allocates_nothing()
    {
        var iv = new RegionInterval();
        var r = Step(2.0f, poolWaited: true, poolMs: 1.0, heldBy: "Region A", lockWaited: true, lockMs: 0.1);
        void Heartbeat()
        {
            var t = new HeartbeatTiming();
            t.Add(in r);
            t.Add(in r);
            iv.Record(4.0, in t, 90.9, 90.9);
            iv.NoteRayCastsRefused(10, 20);
        }
        Heartbeat();   // JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            Heartbeat();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Capacity_keeps_its_lines_and_adds_the_handoff_the_region_lock_and_the_last_interval()
    {
        var s = new PhysicsCapacityStats
        {
            JobPools = 2, JobThreadsPerPool = 4, JobThreadCount = 8, PoolIndex = 1,
            UpdateGateWaits = 12, UpdateGateWaitMsTotal = 15.2, UpdateGateWaitMsMax = 9.9, PoolPeakInside = 1,
            RegionLockWaits = 3, RegionLockWaitMsTotal = 0.6, RegionLockWaitMsMax = 0.3,
        };
        string before = CapacityReport.Render("Test Region", s, 1, 0, 1, 0, 1, 0);
        Assert.Contains("  job pools         JobPools=2 threadsPerPool=4 (ThreadCount 8; process-wide)", before);
        Assert.Contains("  this region       pool=1 waits=12 waitMs total=15.2 max=9.9; pool peakInside=1", before);
        Assert.Contains("  pool handoff      default lock ([Jolt] JobPoolFairHandoff = false)", before);
        Assert.Contains("  region lock       waits=3 waitMs total=0.6 max=0.3", before);
        Assert.Contains("  last interval     none finished yet", before);
        Assert.Contains("  script ray casts  made=0 refused=0 cutShort=0 ms total=0.0 ([Jolt] RayCastBudgetMs)", before);
        Assert.Contains("  other ray casts   made=0 refused=0 cutShort=0 ms total=0.0 ([Jolt] RayCastSimulatorBudgetMs: rez placement, landing, YEngine llCastRay)", before);

        s.JobPoolFairHandoff = true;
        string after = CapacityReport.Render("Test Region", s, 1, 0, 1, 0, 1, 0, null, "heartbeats=330, heartbeat max=4.21ms");
        Assert.Contains("  pool handoff      first come, first served ([Jolt] JobPoolFairHandoff = true)", after);
        Assert.Contains("  last interval     heartbeats=330, heartbeat max=4.21ms", after);
    }

    private static JoltConfig Parse(string value, List<string> warnings)
    {
        var src = new IniConfigSource();
        IConfig jolt = src.AddConfig("Jolt");
        if (value != null)
            jolt.Set("JobPoolFairHandoff", value);
        return JoltConfig.FromConfig(src, warnings);
    }

    [Fact]
    public void The_fair_handoff_key_defaults_off_and_reaches_the_backend()
    {
        var warnings = new List<string>();
        Assert.False(Parse(null, warnings).JobPoolFairHandoff);
        Assert.False(Parse(null, warnings).ToBackendSettings(256, 256).JobPoolFairHandoff);
        Assert.True(Parse("true", warnings).JobPoolFairHandoff);
        Assert.True(Parse("true", warnings).ToBackendSettings(256, 256).JobPoolFairHandoff);
        Assert.Empty(warnings);

        Assert.False(Parse("sometimes", warnings).JobPoolFairHandoff);
        Assert.Single(warnings);
        Assert.Contains("JobPoolFairHandoff", warnings[0]);
    }
}
