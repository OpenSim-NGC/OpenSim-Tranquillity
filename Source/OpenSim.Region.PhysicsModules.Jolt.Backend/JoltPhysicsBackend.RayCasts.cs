/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Ray casts from outside the physics step at a bounded cost (IPhysicsBackend.RayCastLimited).
//
// RayCastAll asks Jolt for every hit along the ray (AllHitSorted) and keeps the closest few. Its cost grows with
// everything the ray crosses: a long ray through a pile of prims or a dense mesh collects every triangle it passes,
// and all of it runs under _simLock, which the region's physics step also takes, while holding its job pool. Script
// casts come from strangers' scripts at script rate, so this path bounds three things:
//   - the hits looked at: a collector that keeps only the closest N (N = the hits the caller asked for) and tells
//     Jolt, once it has N, to stop looking past the furthest of them (the early-out fraction), so the engine skips
//     whatever lies beyond;
//   - one cast: it is cut short when the engine has reported RayCastMaxTestedHits hits to it, or when it runs past
//     what is left of the region's ray cast time;
//   - the region: the time its casts may take in one heartbeat, in two budgets drawn separately: casts known to be a
//     script's (RayCastBudgetMs) and every other cast (RayCastSimulatorBudgetMs), so a script flood that spends the
//     first cannot refuse the simulator's own casts (rez placement, landing). Past a budget, the casts charged to it
//     are refused until the next heartbeat (BeginRayCastBudget).
// What a script gets for a refused or cut-short cast is the caller's to say (JoltScene: Second Life's
// RCERR_CAST_TIME_EXCEEDED).
//
// The binding has no collector with an early-out callback, so this calls joltc's JPH_NarrowPhaseQuery_CastRay2
// directly (a CastRayCollector whose AddHit hands each hit to a callback and takes the callback's return as the new
// early-out fraction, joltc.cpp CastRayCollectorCallback). The callback runs on the calling thread, inside the
// native call, so the collector's state is per thread.

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using JoltPhysicsSharp;

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    public sealed partial class JoltPhysicsBackend
    {
        // joltc's JPH_RayCastResult.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRayCastResult
        {
            public uint BodyId;
            public float Fraction;
            public uint SubShapeId2;
        }

        [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern unsafe bool JPH_NarrowPhaseQuery_CastRay2(
            IntPtr query, Vector3* origin, Vector3* direction, RayCastSettings* settings,
            delegate* unmanaged[Cdecl]<IntPtr, NativeRayCastResult*, float> callback, IntPtr userData,
            IntPtr broadPhaseLayerFilter, IntPtr objectLayerFilter, IntPtr bodyFilter, IntPtr shapeFilter);

        // Jolt's CastRayCollector starts at 1 + FLT_EPSILON (a hit at the very end of the ray counts); returning it
        // leaves the early-out where it was.
        private const float InitialEarlyOut = 1f + 1.1920929e-7f;

        // The closest hits seen so far, at most Want of them, coincident duplicates collapsed as RayCastAll does.
        private sealed class ClosestHits
        {
            public NativeRayCastResult[] Kept = new NativeRayCastResult[16];
            public int Count;
            public int Want;
            public int Tested;
            public int MaxTested;
            public long Deadline;
            public TimeProvider Clock = TimeProvider.System;
            public bool CutShort;
            public float EarlyOut;
            public Vector3 Origin;
            public Vector3 RayDir;

            public void Reset(int want, int maxTested, TimeProvider clock, long deadline, Vector3 origin, Vector3 rayDir)
            {
                if (Kept.Length < want)
                    Kept = new NativeRayCastResult[want];
                Count = 0;
                Want = want;
                Tested = 0;
                MaxTested = maxTested;
                Clock = clock;
                Deadline = deadline;
                CutShort = false;
                EarlyOut = InitialEarlyOut;
                Origin = origin;
                RayDir = rayDir;
            }

            // Returns the new early-out fraction: never above the last one (Jolt requires it to only shrink). The deadline
            // is checked at every hit, so a cast runs past it by at most the engine's work up to the next hit and the
            // hits it then hands back.
            public float Add(in NativeRayCastResult r)
            {
                if (++Tested > MaxTested || Clock.GetTimestamp() > Deadline)
                {
                    CutShort = true;
                    return EarlyOut = -float.MaxValue;   // Jolt tests nothing more: no fraction is below this
                }

                // The same body at the same point (the heightfield's two triangles meeting under the ray): one hit,
                // the nearer.
                Vector3 point = Origin + RayDir * r.Fraction;
                for (int i = 0; i < Count; i++)
                {
                    if (Kept[i].BodyId == r.BodyId
                        && Vector3.DistanceSquared(Origin + RayDir * Kept[i].Fraction, point) < CoincidentEpsilonSq)
                    {
                        if (r.Fraction < Kept[i].Fraction)
                            Kept[i] = r;
                        return EarlyOut;
                    }
                }

                if (Count < Want)
                {
                    Kept[Count++] = r;
                }
                else
                {
                    int far = Furthest();
                    if (!(r.Fraction < Kept[far].Fraction))
                        return EarlyOut;
                    Kept[far] = r;
                }
                if (Count == Want)
                    EarlyOut = Math.Min(EarlyOut, Kept[Furthest()].Fraction);
                return EarlyOut;
            }

            private int Furthest()
            {
                int far = 0;
                for (int i = 1; i < Count; i++)
                    if (Kept[i].Fraction > Kept[far].Fraction)
                        far = i;
                return far;
            }
        }

        [ThreadStatic] private static ClosestHits? t_closest;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe float OnRayHit(IntPtr userData, NativeRayCastResult* result)
        {
            // Nothing may throw out of a native callback.
            try
            {
                ClosestHits? c = t_closest;
                return c == null ? -float.MaxValue : c.Add(*result);
            }
            catch
            {
                return -float.MaxValue;
            }
        }

        // One budget's time spent this heartbeat, and its counters GetCapacityStats reports. Interlocked throughout.
        private sealed class RayBudgetState
        {
            public long UsedTicks;
            public long Casts;
            public long Refused;
            public long CutShort;
            public long TicksTotal;
        }

        // Indexed by RayCastBudget.
        private readonly RayBudgetState[] _rayBudgets = { new RayBudgetState(), new RayBudgetState() };
        // Both budgets' time this heartbeat together, and the most one heartbeat has spent.
        private long _rayUsedTicksAll;
        private long _rayTicksMaxHeartbeat;

        // Every time the budgets read or charge is on this clock (PhysicsBackendSettings.RayCastClock).
        private TimeProvider RayClock => _settings.RayCastClock ?? TimeProvider.System;

        private double RayTicksToMs(long ticks) => ticks * 1000.0 / RayClock.TimestampFrequency;

        private long RayBudgetTicks(RayCastBudget budget)
        {
            float ms = budget == RayCastBudget.Script
                ? (_settings.RayCastBudgetMs > 0f ? _settings.RayCastBudgetMs : PhysicsBackendSettings.DefaultRayCastBudgetMs)
                : (_settings.RayCastSimulatorBudgetMs > 0f ? _settings.RayCastSimulatorBudgetMs : PhysicsBackendSettings.DefaultRayCastSimulatorBudgetMs);
            return (long)(ms * RayClock.TimestampFrequency / 1000.0);
        }

        private int RayMaxTested => _settings.RayCastMaxTestedHits > 0 ? _settings.RayCastMaxTestedHits : PhysicsBackendSettings.DefaultRayCastMaxTestedHits;

        public void BeginRayCastBudget()
        {
            // Under the lock every cast holds, so no cast is in flight across the new heartbeat's start: one that were
            // could be counted in both heartbeats' total, or charge the old heartbeat's time to the new one.
            lock (_simLock)
            {
                foreach (RayBudgetState b in _rayBudgets)
                    Interlocked.Exchange(ref b.UsedTicks, 0);
                Interlocked.Exchange(ref _rayUsedTicksAll, 0);
            }
        }

        public long RayCastsRefused(RayCastBudget budget)
            => budget == RayCastBudget.None ? 0 : Interlocked.Read(ref BudgetState(budget).Refused);

        private RayBudgetState BudgetState(RayCastBudget budget)
            => _rayBudgets[budget == RayCastBudget.Script ? 0 : 1];

        public unsafe int RayCastLimited(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, RayCastBudget budget,
                                         Span<RayHit> hits, out RayCastStatus status)
        {
            status = RayCastStatus.Ok;
            if (_system == null || hits.Length == 0)
                return 0;
            if (!IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(maxDistance))
                return 0;
            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return 0;

            Vector3 rayDir = direction / len * maxDistance;
            lock (_simLock)
            {
                if (_disposed) return 0;   // backend torn down (shutdown race) - no native call
                // RayCastBudget.None (the module's warm-up) is charged and counted nowhere.
                RayBudgetState? b = budget == RayCastBudget.None ? null : BudgetState(budget);
                long allowed = RayBudgetTicks(b == null ? RayCastBudget.Simulator : budget);
                long used = 0;
                if (b != null)
                {
                    Interlocked.Increment(ref b.Casts);
                    used = Interlocked.Read(ref b.UsedTicks);
                    if (used >= allowed)
                    {
                        Interlocked.Increment(ref b.Refused);
                        status = RayCastStatus.Refused;
                        return 0;
                    }
                }

                TimeProvider clock = RayClock;
                long start = clock.GetTimestamp();
                ClosestHits c = t_closest ??= new ClosestHits();
                c.Reset(hits.Length, RayMaxTested, clock, start + (allowed - used), origin, rayDir);

                // The same faces are tested as RayCastAll tests, except that a convex shape (a box, the avatar's capsule) is not
                // solid to a ray that starts inside it: a cast does not report a prim its line starts inside
                // (wiki.secondlife.com/wiki/LlCastRay), so the cast goes on to what lies beyond instead of hitting it at distance 0.
                var settings = new RayCastSettings { TreatConvexAsSolid = false };
                Vector3 o = origin, d = rayDir;
                IntPtr objectFilter = FilterFor(filter).Handle;
                JPH_NarrowPhaseQuery_CastRay2(_system.NarrowPhaseQuery.Handle, &o, &d, &settings,
                    &OnRayHit, IntPtr.Zero, IntPtr.Zero, objectFilter, IntPtr.Zero, IntPtr.Zero);

                int n = 0;
                if (c.CutShort)
                {
                    if (b != null)
                        Interlocked.Increment(ref b.CutShort);
                    status = RayCastStatus.CutShort;
                }
                else
                {
                    Span<NativeRayCastResult> kept = c.Kept.AsSpan(0, c.Count);
                    SortByFraction(kept);
                    for (; n < kept.Length; n++)
                    {
                        NativeRayCastResult r = kept[n];
                        var bodyId = new BodyID(r.BodyId);
                        Vector3 point = origin + rayDir * r.Fraction;
                        _joltToRecord.TryGetValue(r.BodyId, out JoltBodyRecord? rec);
                        hits[n] = new RayHit
                        {
                            Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                            UserData = rec != null ? rec.UserData : 0u,
                            ChildUserData = ResolveChildUserData(rec, r.SubShapeId2),
                            Point = point,
                            Normal = SurfaceNormalOf(bodyId, r.SubShapeId2, point),
                            Distance = maxDistance * r.Fraction,
                        };
                    }
                }

                if (b == null)
                {
                    // The budget lookup a counted cast makes, and a sort: one of fewer than two hits never calls the
                    // comparer, so the warm-up sorts two of its own.
                    BudgetState(RayCastBudget.Simulator);
                    Span<NativeRayCastResult> two = stackalloc NativeRayCastResult[2];
                    two[0].Fraction = 1f;
                    SortByFraction(two);
                    return n;
                }

                long spent = clock.GetTimestamp() - start;
                Interlocked.Add(ref b.UsedTicks, spent);
                Interlocked.Add(ref b.TicksTotal, spent);
                long nowUsed = Interlocked.Add(ref _rayUsedTicksAll, spent);
                long max;
                while (nowUsed > (max = Interlocked.Read(ref _rayTicksMaxHeartbeat))
                       && Interlocked.CompareExchange(ref _rayTicksMaxHeartbeat, nowUsed, max) != max) { }
                return n;
            }
        }

        private static void SortByFraction(Span<NativeRayCastResult> kept)
            => kept.Sort(static (a, b) => a.Fraction.CompareTo(b.Fraction));

        private void FillRayCastStats(ref PhysicsCapacityStats s)
        {
            s.ScriptRayCasts = BudgetStats(BudgetState(RayCastBudget.Script));
            s.SimulatorRayCasts = BudgetStats(BudgetState(RayCastBudget.Simulator));
            s.RayCasts = s.ScriptRayCasts.Casts + s.SimulatorRayCasts.Casts;
            s.RayCastsRefused = s.ScriptRayCasts.Refused + s.SimulatorRayCasts.Refused;
            s.RayCastsCutShort = s.ScriptRayCasts.CutShort + s.SimulatorRayCasts.CutShort;
            s.RayCastMsTotal = s.ScriptRayCasts.MsTotal + s.SimulatorRayCasts.MsTotal;
            s.RayCastMsMaxHeartbeat = RayTicksToMs(Interlocked.Read(ref _rayTicksMaxHeartbeat));
        }

        private RayCastBudgetStats BudgetStats(RayBudgetState b) => new RayCastBudgetStats
        {
            Casts = Interlocked.Read(ref b.Casts),
            Refused = Interlocked.Read(ref b.Refused),
            CutShort = Interlocked.Read(ref b.CutShort),
            MsTotal = RayTicksToMs(Interlocked.Read(ref b.TicksTotal)),
        };
    }
}
