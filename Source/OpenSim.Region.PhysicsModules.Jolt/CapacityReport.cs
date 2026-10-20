/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Capacity surfacing for the Jolt module.
//
// The backend now counts every PhysicsUpdateError flag and every CreateBody that MaxBodies refused. This turns two
// snapshots of those counters into the one warning line an operator can act on (which [Jolt] key to raise), and
// renders the `jolt capacity` read-out. Pure: no scene, no backend, no logger - so it is tested directly.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    internal static class CapacityReport
    {
        /// <summary>
        /// The warning for what went wrong between <paramref name="prev"/> and <paramref name="cur"/>, or null when
        /// nothing did. Example: "[JOLT SCENE] Region A: collisions dropped (ContactConstraintsFull x37 in 10s) - raise
        /// [Jolt] MaxContactConstraints".
        /// </summary>
        internal static string Warning(string region, in PhysicsCapacityStats prev, in PhysicsCapacityStats cur, double seconds)
        {
            var dropped = new List<string>();
            var keys = new List<string>();
            void Flag(string name, long delta, string key)
            {
                if (delta <= 0) return;
                dropped.Add($"{name} x{delta}");
                if (!keys.Contains(key)) keys.Add(key);
            }
            Flag("ContactConstraintsFull", cur.ContactConstraintsFullSteps - prev.ContactConstraintsFullSteps, "MaxContactConstraints");
            Flag("BodyPairCacheFull", cur.BodyPairCacheFullSteps - prev.BodyPairCacheFullSteps, "MaxBodyPairs");
            // Jolt sizes the manifold cache from both caps.
            Flag("ManifoldCacheFull", cur.ManifoldCacheFullSteps - prev.ManifoldCacheFullSteps, "MaxBodyPairs");
            if (cur.ManifoldCacheFullSteps > prev.ManifoldCacheFullSteps && !keys.Contains("MaxContactConstraints"))
                keys.Add("MaxContactConstraints");

            long refused = cur.BodyCreateFailures - prev.BodyCreateFailures;
            if (dropped.Count == 0 && refused <= 0)
                return null;

            string span = $"{seconds:0}s";
            var sb = new StringBuilder($"{JoltScene.LogHeader} {region}:");
            if (dropped.Count > 0)
                sb.Append($" collisions dropped ({string.Join(", ", dropped)} in {span}) - raise [Jolt] {string.Join(", ", keys)}");
            if (refused > 0)
                sb.Append(dropped.Count > 0 ? ";" : "")
                  .Append($" bodies refused (MaxBodies {cur.MaxBodies} reached x{refused} in {span}; those prims have no physics) - raise [Jolt] MaxBodies");
            return sb.ToString();
        }

        /// <summary>The share of frame time above which waiting at the job pool's update gate is worth a warning.</summary>
        internal const double GateWaitWarnFraction = 0.20;

        /// <summary>
        /// The warning when this region spent more than 20% of its frame time over the last interval waiting
        /// for its job pool (which runs one region's physics update at a time), or null when it did not.
        /// </summary>
        internal static string GateWarning(string region, double waitMs, double frameMs, int poolIndex, int jobPools)
        {
            if (frameMs <= 0 || waitMs <= GateWaitWarnFraction * frameMs)
                return null;
            return $"{JoltScene.LogHeader} {region}: waited {waitMs:0} ms of {frameMs:0} ms frame time ({waitMs / frameMs:0%}) " +
                   $"for Jolt job pool {poolIndex} of {jobPools} (one physics update at a time per pool) - raise [Jolt] JobPools";
        }

        /// <summary>How the job thread count was chosen, for the startup log and `jolt capacity`.</summary>
        internal static string JobThreadsHow(in PhysicsCapacityStats s) => s.JobThreadSource switch
        {
            JobThreadSource.Set => $"set by [Jolt] ThreadCount = {s.JobThreadCount}, all pools together",
            JobThreadSource.Deterministic => "one thread, [Jolt] DeterministicMode",
            _ => $"automatic: each pool's share of the processors less one, at most {JoltPhysicsBackend.AutoMaxThreadsPerPool} per pool",
        };

        /// <summary>The startup log's job pool line: pools, threads per pool, and whether the count is automatic or set.</summary>
        internal static string JobPoolsStartup(in PhysicsCapacityStats s)
            => $"{s.JobPools} pool{(s.JobPools == 1 ? "" : "s")} x {s.JobThreadsPerPool} worker thread{(s.JobThreadsPerPool == 1 ? "" : "s")} " +
               $"({JobThreadsHow(in s)}); one physics update at a time per pool (process-wide).";

        /// <summary>The `jolt capacity` line on the pool count in use: as [Jolt] JobPools asked, or fewer and why.</summary>
        internal static string PoolsInUse(in PhysicsCapacityStats s)
            => s.JobPoolsLimitedBy == null
                ? $"{s.JobPools}, as [Jolt] JobPools asks"
                : $"{s.JobPools} of the {s.JobPoolsRequested} [Jolt] JobPools asks for: {s.JobPoolsLimitedBy}";

        /// <summary>
        /// The start-up warning when the loaded native runs fewer job pools than [Jolt] JobPools asks for: the setting,
        /// the reason and the remedy. Null when it runs what is asked.
        /// </summary>
        internal static string JobPoolLimitWarning(int requested, JoltNativeInfo native)
        {
            string reason = JoltPhysicsBackend.JobPoolLimitReason(requested, native);
            if (reason == null)
                return null;
            const string remedy = "More than one pool needs a change in joltc itself; set [Jolt] JobPools = 1.";
            return $"[Jolt] JobPools = {JoltPhysicsBackend.ResolveJobPools(requested)}, but the module runs ONE job pool: {reason}. " +
                   $"Regions take turns on that pool. {remedy}";
        }

        /// <summary>The `jolt capacity` read-out: backend stats plus the scene's own buffers.</summary>
        internal static string Render(string region, in PhysicsCapacityStats s,
            int bodyBuf, long bodyOverflowFrames, int charBuf, long charFullFrames, int contactBuf, long contactOverflowFrames,
            SubstepAccumulator substeps = null, string lastInterval = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{JoltScene.LogHeader} capacity for '{region}':");
            sb.AppendLine($"  bodies            live={s.LiveBodyCount} active={s.ActiveBodyCount} MaxBodies={s.MaxBodies} refused={s.BodyCreateFailures}");
            sb.AppendLine($"  caps              MaxBodyPairs={s.MaxBodyPairs} MaxContactConstraints={s.MaxContactConstraints}");
            sb.AppendLine($"  update errors     last={s.LastUpdateError} steps: BodyPairCacheFull={s.BodyPairCacheFullSteps} ManifoldCacheFull={s.ManifoldCacheFullSteps} ContactConstraintsFull={s.ContactConstraintsFullSteps}");
            sb.AppendLine($"  characters        {s.CharacterCount}");
            sb.AppendLine($"  contact ring      capacity={s.ContactRingCapacity} dropped={s.DroppedContacts} (cumulative)");
            sb.AppendLine($"  job pools         JobPools={s.JobPools} threadsPerPool={s.JobThreadsPerPool} (ThreadCount {s.JobThreadCount}; process-wide); {JobThreadsHow(in s)}");
            sb.AppendLine($"  pools in use      {PoolsInUse(in s)}");
            sb.AppendLine($"  this region       pool={s.PoolIndex} waits={s.UpdateGateWaits} waitMs total={s.UpdateGateWaitMsTotal:0.0} max={s.UpdateGateWaitMsMax:0.0}; pool peakInside={s.PoolPeakInside}");
            sb.AppendLine($"  pool handoff      {(s.JobPoolFairHandoff ? "first come, first served ([Jolt] JobPoolFairHandoff = true)" : "default lock ([Jolt] JobPoolFairHandoff = false)")}");
            sb.AppendLine($"  region lock       waits={s.RegionLockWaits} waitMs total={s.RegionLockWaitMsTotal:0.0} max={s.RegionLockWaitMsMax:0.0} (steps that waited for this region's own lock, cumulative)");
            sb.AppendLine($"  last interval     {lastInterval ?? "none finished yet (the metrics log closes one about every 30 s)"}");
            sb.AppendLine($"  rejected non-finite  {s.RejectedNonFinite}");
            sb.AppendLine($"  script ray casts  {RayBudget(in s.ScriptRayCasts)} ([Jolt] RayCastBudgetMs)");
            sb.AppendLine($"  other ray casts   {RayBudget(in s.SimulatorRayCasts)} ([Jolt] RayCastSimulatorBudgetMs: rez placement, landing, YEngine llCastRay)");
            sb.AppendLine($"  ray casts, both   made={s.RayCasts} refused={s.RayCastsRefused} cutShort={s.RayCastsCutShort} ms total={s.RayCastMsTotal:0.0} most in one heartbeat={s.RayCastMsMaxHeartbeat:0.00}");
            sb.Append($"  scene buffers     bodies={bodyBuf} (overflowed {bodyOverflowFrames} steps) characters={charBuf} (full {charFullFrames} steps) contacts={contactBuf} (overflowed {contactOverflowFrames} steps)");
            sb.AppendLine();
            if (substeps == null)
                sb.Append("  physics steps     one per heartbeat ([Jolt] PhysicsStepRate = 0)");
            else
                sb.Append($"  physics steps     {substeps.RateHz:0.##} Hz, {substeps.Steps} taken; heartbeats capped at {SubstepAccumulator.MaxStepsPerFrame} steps: {substeps.CappedFrames}");
            return sb.ToString();
        }

        private static string RayBudget(in RayCastBudgetStats b)
            => $"made={b.Casts} refused={b.Refused} cutShort={b.CutShort} ms total={b.MsTotal:0.0}";
    }
}
