/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// INSTRUMENTATION for measuring how the module scales with region count and thread-pool size.
// Kept in its own file so it is additive and does not touch the physics logic beyond three one-line hooks
// (AddRegion init, StepOnce, and a `jolt metrics` console subcommand).
//
// Provides the scaling metrics that are obtainable from managed code:
//   - per-region RSS delta at backend init (8 MB TempAllocator + MaxBodies preallocation + job pool)
//   - per-region step time (EMA + last) and active-body count
//   - whole-process step-time sum and total process THREAD COUNT (a per-region
//     JobSystemThreadPool of ProcessorCount-1 -> N*(cores-1) threads; this is how we watch it)
//   - a throttled process-wide summary emitted to the LOG (~30 s) so the metrics are captured without
//     needing console interaction, followed by a second line with each region's figures for that interval: its
//     longest heartbeat and longest physics step, its waits for its job pool (and which region held the pool at
//     the longest) and for its own region lock, the longest gap between heartbeats against the frame time, and the
//     ray casts refused in each ray cast budget (script, simulator).
//
// NOT YET obtainable here: TempAllocator high-water / malloc-fallback rate. The native
// TempAllocatorImplWithMallocFallback tracks that internally but the joltc C API does not export it.
// Surface it here if joltc ever exports it.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal static class JoltMetrics
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[JOLT METRICS]";


        private sealed class RegionStat
        {
            public long Steps;
            public double LastMs;
            public double EmaMs;           // exponential moving average of per-step physics ms
            public int ActiveBodies;
            public double InitRssDeltaMB;  // process RSS growth across this region's backend Initialize
            public readonly RegionInterval Interval = new RegionInterval();
        }

        private static readonly ConcurrentDictionary<string, RegionStat> s_regions =
            new ConcurrentDictionary<string, RegionStat>();
        private static long s_lastLogTick;   // throttle the periodic process summary (~30 s)
        private static long s_intervalStartTick;   // when the interval being recorded began (TickCount64)
        private static string s_lastIntervalLine;  // the last interval line logged

        public static void RecordRegionInit(string region, long rssDeltaBytes)
        {
            RegionStat st = s_regions.GetOrAdd(region, _ => new RegionStat());
            st.InitRssDeltaMB = rssDeltaBytes / (1024.0 * 1024.0);
            Interlocked.CompareExchange(ref s_intervalStartTick, Environment.TickCount64, 0);
            m_log.LogInformation($"{LogHeader} region '{region}': backend-init RSS delta = {st.InitRssDeltaMB:0.0} MB; process threads now = {ThreadCount()}.");
        }

        /// <summary>
        /// One heartbeat's physics: its time (all its steps, without pool waits), what its steps waited for, the time
        /// since the previous heartbeat's physics call (0: not known), the configured frame time, and the region's ray
        /// casts refused so far in each budget (cumulative).
        /// </summary>
        public static void RecordStep(string region, float physicsMs, int activeBodies, in HeartbeatTiming timing, double gapMs, double frameMs,
                                      long scriptRaysRefused = 0, long simulatorRaysRefused = 0)
        {
            RegionStat st = s_regions.GetOrAdd(region, _ => new RegionStat());
            st.Steps++;
            st.LastMs = physicsMs;
            st.EmaMs = st.EmaMs <= 0 ? physicsMs : st.EmaMs * 0.98 + physicsMs * 0.02;
            st.ActiveBodies = activeBodies;
            st.Interval.Record(physicsMs, in timing, gapMs, frameMs);
            st.Interval.NoteRayCastsRefused(scriptRaysRefused, simulatorRaysRefused);

            // Throttled process-wide summary to the log so the metrics are captured without
            // console interaction. Single-writer via CompareExchange so only one region logs per window.
            // TickCount64 (monotonic, non-wrapping) — plain Environment.TickCount goes negative past
            // ~24.9 days uptime, which silently disables the throttle.
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref s_lastLogTick);
            if (now - last > 30000 &&
                Interlocked.CompareExchange(ref s_lastLogTick, now, last) == last)
            {
                m_log.LogInformation(Report());
                m_log.LogInformation(EndInterval(now));
            }
        }

        /// <summary>
        /// Close the interval being recorded: take every region's figures for it (zeroing them for the next), keep them
        /// as that region's last interval, and return the line the periodic log prints after the summary line.
        /// </summary>
        private static string EndInterval(long nowTick)
        {
            long start = Interlocked.Exchange(ref s_intervalStartTick, nowTick);
            double seconds = start == 0 ? 0 : (nowTick - start) / 1000.0;
            var sb = new StringBuilder();
            sb.Append($"{LogHeader} last {seconds:0.0}s");
            foreach (var kv in s_regions)
                sb.Append($" | {kv.Key}: {kv.Value.Interval.Take()}");
            string line = sb.ToString();
            Volatile.Write(ref s_lastIntervalLine, line);
            return line;
        }

        /// <summary>This region's last finished interval as the periodic log printed it, with no region name; null before
        /// the first.</summary>
        internal static string LastIntervalOf(string region)
            => s_regions.TryGetValue(region, out RegionStat st) ? st.Interval.Last : null;

        /// <summary>The last interval line the periodic log printed; null before the first.</summary>
        internal static string LastIntervalLine() => Volatile.Read(ref s_lastIntervalLine);


        public static int ThreadCount()
        {
            using Process p = Process.GetCurrentProcess();
            return p.Threads.Count;
        }

        public static string Report()
        {
            using Process p = Process.GetCurrentProcess();
            double rssMB = p.WorkingSet64 / (1024.0 * 1024.0);
            int threads = p.Threads.Count;
            double sumMs = 0;

            StringBuilder sb = new StringBuilder();
            sb.Append($"{LogHeader} process RSS={rssMB:0} MB, threads={threads}, regions={s_regions.Count}");
            foreach (var kv in s_regions)
            {
                sumMs += kv.Value.EmaMs;
                sb.Append($" | {kv.Key}: step~{kv.Value.EmaMs:0.00}ms (last {kv.Value.LastMs:0.00}), active={kv.Value.ActiveBodies}, initRSSd={kv.Value.InitRssDeltaMB:0.0}MB, steps={kv.Value.Steps}");
            }
            sb.Append($" | whole-process step-sum~{sumMs:0.00}ms");
            sb.Append(" | TempAllocator high-water/malloc-fallback: N/A (needs native counter)");
            return sb.ToString();
        }
    }

    /// <summary>
    /// One heartbeat's waits and longest step, gathered over its physics steps (one, unless [Jolt] PhysicsStepRate is
    /// on). A value type kept on the heartbeat's stack.
    /// </summary>
    internal struct HeartbeatTiming
    {
        public double StepMaxMs;
        public int PoolWaits;
        public double PoolWaitMs;
        public double PoolWaitMaxMs;
        public string PoolHeldByAtMax;
        public int LockWaits;
        public double LockWaitMs;
        public double LockWaitMaxMs;

        public void Add(in StepResult r)
        {
            if (r.PhysicsMilliseconds > StepMaxMs)
                StepMaxMs = r.PhysicsMilliseconds;
            StepWaits w = r.Waits;
            if (w.PoolWaited)
            {
                PoolWaits++;
                PoolWaitMs += w.PoolWaitMs;
                if (w.PoolWaitMs >= PoolWaitMaxMs)
                {
                    PoolWaitMaxMs = w.PoolWaitMs;
                    PoolHeldByAtMax = w.PoolHeldBy;
                }
            }
            if (w.RegionLockWaited)
            {
                LockWaits++;
                LockWaitMs += w.RegionLockWaitMs;
                if (w.RegionLockWaitMs > LockWaitMaxMs)
                    LockWaitMaxMs = w.RegionLockWaitMs;
            }
        }
    }

    /// <summary>
    /// One region's timing over a metrics interval. <see cref="Record"/> runs on the region's heartbeat and
    /// <see cref="Take"/> on whichever thread closes the interval; both work field by field with Interlocked, so neither
    /// takes a lock and recording allocates nothing.
    /// </summary>
    internal sealed class RegionInterval
    {
        private long _heartbeats;
        private double _heartbeatMaxMs;
        private double _stepMaxMs;
        private long _poolWaits;
        private double _poolWaitMs;
        private double _poolWaitMaxMs;
        private string _poolHeldBy;
        private long _lockWaits;
        private double _lockWaitMs;
        private double _lockWaitMaxMs;
        private double _gapMaxMs;
        private double _frameMs;
        private string _last;
        // Ray casts refused, cumulative: the latest count the heartbeat saw, and the count when the last interval closed.
        private long _scriptRaysRefused;
        private long _simulatorRaysRefused;
        private long _scriptRaysRefusedAtTake;
        private long _simulatorRaysRefusedAtTake;

        /// <summary>The last interval <see cref="Take"/> closed; null before the first.</summary>
        public string Last => Volatile.Read(ref _last);

        public void Record(double heartbeatMs, in HeartbeatTiming timing, double gapMs, double frameMs)
        {
            Interlocked.Increment(ref _heartbeats);
            Max(ref _heartbeatMaxMs, heartbeatMs);
            Max(ref _stepMaxMs, timing.StepMaxMs);
            if (timing.PoolWaits > 0)
            {
                Interlocked.Add(ref _poolWaits, timing.PoolWaits);
                Add(ref _poolWaitMs, timing.PoolWaitMs);
                if (Max(ref _poolWaitMaxMs, timing.PoolWaitMaxMs) || Volatile.Read(ref _poolHeldBy) == null)
                    Volatile.Write(ref _poolHeldBy, timing.PoolHeldByAtMax);
            }
            if (timing.LockWaits > 0)
            {
                Interlocked.Add(ref _lockWaits, timing.LockWaits);
                Add(ref _lockWaitMs, timing.LockWaitMs);
                Max(ref _lockWaitMaxMs, timing.LockWaitMaxMs);
            }
            Max(ref _gapMaxMs, gapMs);
            if (frameMs > 0)
                Volatile.Write(ref _frameMs, frameMs);
        }

        /// <summary>The region's ray casts refused so far in each budget, cumulative (the backend's counters). The
        /// interval reports how many of them came after the last interval closed.</summary>
        public void NoteRayCastsRefused(long script, long simulator)
        {
            Volatile.Write(ref _scriptRaysRefused, script);
            Volatile.Write(ref _simulatorRaysRefused, simulator);
        }

        /// <summary>Close the interval: return its figures as text (also kept as <see cref="Last"/>) and start the next
        /// from zero. The frame time carries over. One thread at a time (the metrics log's throttle picks it).</summary>
        public string Take()
        {
            long poolWaits = Interlocked.Exchange(ref _poolWaits, 0);
            string heldBy = Interlocked.Exchange(ref _poolHeldBy, null);
            long scriptRefused = Volatile.Read(ref _scriptRaysRefused), simulatorRefused = Volatile.Read(ref _simulatorRaysRefused);
            long scriptRefusedHere = scriptRefused - _scriptRaysRefusedAtTake;
            long simulatorRefusedHere = simulatorRefused - _simulatorRaysRefusedAtTake;
            _scriptRaysRefusedAtTake = scriptRefused;
            _simulatorRaysRefusedAtTake = simulatorRefused;
            string text =
                $"heartbeats={Interlocked.Exchange(ref _heartbeats, 0)}, " +
                $"heartbeat max={Interlocked.Exchange(ref _heartbeatMaxMs, 0):0.00}ms, " +
                $"step max={Interlocked.Exchange(ref _stepMaxMs, 0):0.00}ms, " +
                $"pool waits={poolWaits} total={Interlocked.Exchange(ref _poolWaitMs, 0):0.0}ms " +
                $"max={Interlocked.Exchange(ref _poolWaitMaxMs, 0):0.00}ms" + (poolWaits > 0 ? $" held by {heldBy ?? "?"}" : "") + ", " +
                $"lock waits={Interlocked.Exchange(ref _lockWaits, 0)} total={Interlocked.Exchange(ref _lockWaitMs, 0):0.0}ms " +
                $"max={Interlocked.Exchange(ref _lockWaitMaxMs, 0):0.00}ms, " +
                $"heartbeat gap max={Interlocked.Exchange(ref _gapMaxMs, 0):0.0}ms (frame {Volatile.Read(ref _frameMs):0.0}ms), " +
                $"ray casts refused script={scriptRefusedHere} simulator={simulatorRefusedHere}";
            Volatile.Write(ref _last, text);
            return text;
        }

        // Raise `location` to `value` if it is higher; true when it did.
        private static bool Max(ref double location, double value)
        {
            double seen;
            while (value > (seen = Volatile.Read(ref location)))
                if (Interlocked.CompareExchange(ref location, value, seen) == seen)
                    return true;
            return false;
        }

        private static void Add(ref double location, double value)
        {
            double seen;
            do seen = Volatile.Read(ref location);
            while (Interlocked.CompareExchange(ref location, seen + value, seen) != seen);
        }
    }
}
