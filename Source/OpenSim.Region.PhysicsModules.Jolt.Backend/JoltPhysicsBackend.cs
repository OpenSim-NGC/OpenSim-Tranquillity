/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Jolt implementation of IPhysicsBackend.
//
// ============================ READ THIS FIRST ============================
// Jolt binding: JoltPhysicsSharp 2.19.1,
// single precision (Foundation.Init(false); the native is loaded by JoltNative). The Jolt calls below
// are the 2.19.1 surface.
//
// The parts worth reading carefully are the ones that are easy to get wrong and
// expensive to discover later:
//   - broad phase / object layer filtering  (BroadPhase region + Initialize)
//   - DontActivate on insert                (CreateBody)
//   - ScaledShape wrapper                   (CreateScaledShape; a resized mesh prim wraps its asset's cooked shape)
//   - contact ring buffer                   (JoltContactListener)
//   - CharacterVirtual stepping order       (Step)
// =========================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using JoltPhysicsSharp;

// The test-only hooks (HoldPoolGateForTest) are internal.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Backend.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Vehicles.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Harness.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Scenarios.Avatar.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Scenarios.Prim.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpenSim.Region.PhysicsModules.Jolt.Scenarios.Vehicle.Tests")]

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    public sealed partial class JoltPhysicsBackend : IPhysicsBackend
    {
        public string Name => "Jolt";
        public string Version => "5.x";

        private PhysicsBackendSettings _settings;
        private readonly Stopwatch _stepTimer = new Stopwatch();

        // Handle tables. Jolt hands back its own ids; we keep our own dense
        // tables so a stale handle of ours can never index live Jolt memory.
        private readonly HandleTable<JoltBodyRecord> _bodies = new HandleTable<JoltBodyRecord>();
        private readonly HandleTable<JoltShapeRecord> _shapes = new HandleTable<JoltShapeRecord>();
        private readonly HandleTable<JoltCharacterRecord> _characters = new HandleTable<JoltCharacterRecord>();

        private JoltContactListener _contactListener = null!;

        // Native Jolt handles. Nullable + disposed in Dispose() in strict reverse
        // order: the PhysicsSystem retains the filter interfaces and the
        // job system for its lifetime, so the system MUST be torn down first.
        private PhysicsSystem? _system;
        // ONE shared, process-capped set of JobSystemThreadPools for ALL regions, created with Foundation
        // (first region in) and disposed with it (last region out). A per-region pool of ProcessorCount-1
        // threads makes N regions cost N*(cores-1) threads (measured ~36/region, 78 for two). InWorldz ran
        // ~1 thread/region with the default settings; a capped process-wide pool is the target shape. Sized once by the
        // first region's settings (ThreadCount / DeterministicMode) under s_foundationGate, then shared.
        //
        // ONE PHYSICS UPDATE PER POOL. A JobSystemThreadPool's queue is a fixed ring of 1024 slots
        // (JobSystemThreadPool.h:86) shared by every Update on the pool; its head is the minimum of the workers'
        // heads, a worker advances its own head only after its running job returns, and QueueJobInternal sleeps
        // 100 us and retries forever while the ring is full (JobSystemThreadPool.cpp 152-190). So a worker whose
        // job queues a follow-on job into a full ring waits on its own head. Concurrent Updates add their queue
        // traffic together - four regions x 300 boxes wedged every time, whatever maxJobs was. One Update at a
        // time is the configuration Jolt is built and tested for, so each pool admits ONE Update through its gate
        // and the process scales by running [Jolt] JobPools pools. Step takes the gate BEFORE this region's
        // _simLock, holds it for the whole step and releases it in a finally; nothing takes a gate while holding
        // any _simLock, so the order (gate, then _simLock) cannot invert; see Step. Pools, their count, their
        // size and how they hand over ([Jolt] JobPoolFairHandoff) are the first region's, like ThreadCount; each
        // region is assigned the pool with the fewest regions at Initialize.
        //
        // HANDOFF. By default the gate is a Monitor. A Monitor does not queue its waiters in order, and a region
        // running several physics steps in one heartbeat takes the gate again right after releasing it, usually
        // before a woken waiter runs; so a waiting region can sit through the rest of the holder's heartbeat. With
        // fair handoff the gate is a ticket lock: each Step takes the next ticket and runs when its number is served,
        // so a region that started waiting during the holder's step runs before the holder's next step.
        private sealed class JobPool
        {
            public readonly int Index;
            public readonly JobSystemThreadPool System;
            public readonly bool Fair;
            public readonly object Gate = new object();   // admits ONE Update; Monitor, so owner-checked (Fair = false)
            public int Regions;       // assigned regions; under s_foundationGate
            public int Inside;        // Updates inside the gate now; Interlocked
            public int PeakInside;    // the most ever inside at once; must stay 1

            // Which region a waiting step waited behind (metrics only), both Volatile. Enter writes both as soon as it
            // has the gate; Exit clears Holder before it lets the gate go. So a name read from Holder is the region
            // holding the gate at that moment, never one that has already let it go, and never the reader itself.
            // Holder is null for the instant between a step taking the gate and naming itself; a step that read null
            // then reports LastHolder as it finds it on taking the gate: the region the gate passed from to it.
            private string? _holder;
            private string? _lastHolder;

            // TEST-ONLY. ArrivedForTest runs when a step (or HoldPoolGateForTest) has found whether it must wait, with
            // its region's name and that answer; TakenForTest runs once it has the gate, before it names itself.
            public Action<string?, bool>? ArrivedForTest;
            public Action<string?>? TakenForTest;

            // Fair = true: a ticket lock. _nextTicket is the next ticket to hand out, _serving the ticket that may run.
            // A waiter blocks on _turn; Exit wakes waiters only when some are registered in _waiters. Waiter: under
            // _turn, Interlocked-increment _waiters, then read _serving. Exit: Interlocked-increment _serving, then read
            // _waiters. Both are full fences, so either the waiter sees its turn or Exit sees the waiter and takes _turn
            // to pulse it, which it can only do once the waiter is inside Monitor.Wait.
            private long _nextTicket;
            private long _serving;
            private int _waiters;
            private readonly object _turn = new object();

            public JobPool(int index, JobSystemThreadPool system, bool fair) { Index = index; System = system; Fair = fair; }

            /// <summary>Takes the gate for region <paramref name="name"/>. True when it had to wait (the gate was held
            /// when it arrived), with the Stopwatch ticks waited and the region it waited behind: the one holding the gate
            /// when it arrived, or, when that one had not named itself yet, the one the gate passed from to it.</summary>
            public bool Enter(string? name, out long waitTicks, out string? heldBy)
            {
                waitTicks = 0;
                heldBy = null;
                bool waited;
                if (!Fair)
                {
                    waited = !Monitor.TryEnter(Gate);
                    ArrivedForTest?.Invoke(name, waited);
                    if (waited)
                    {
                        heldBy = Volatile.Read(ref _holder);
                        long start = Stopwatch.GetTimestamp();
                        Monitor.Enter(Gate);
                        waitTicks = Stopwatch.GetTimestamp() - start;
                    }
                }
                else
                {
                    long ticket = Interlocked.Increment(ref _nextTicket) - 1;
                    waited = Interlocked.Read(ref _serving) != ticket;
                    ArrivedForTest?.Invoke(name, waited);
                    if (waited)
                    {
                        heldBy = Volatile.Read(ref _holder);
                        long waitStart = Stopwatch.GetTimestamp();
                        WaitForTurn(ticket);
                        waitTicks = Stopwatch.GetTimestamp() - waitStart;
                    }
                }

                TakenForTest?.Invoke(name);
                if (waited && heldBy == null)
                    heldBy = Volatile.Read(ref _lastHolder);
                Volatile.Write(ref _lastHolder, name);
                Volatile.Write(ref _holder, name);
                return waited;
            }

            // Blocks until `ticket` is served. A ticket is never abandoned: an interrupted waiter keeps waiting for its
            // turn, then passes the gate on and rethrows, so the steps queued behind it are not stranded.
            private void WaitForTurn(long ticket)
            {
                bool interrupted = false;
                lock (_turn)
                {
                    Interlocked.Increment(ref _waiters);
                    try
                    {
                        while (Interlocked.Read(ref _serving) != ticket)
                        {
                            try { Monitor.Wait(_turn); }
                            catch (ThreadInterruptedException) { interrupted = true; }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _waiters);
                    }
                }
                if (interrupted)
                {
                    Exit();
                    throw new ThreadInterruptedException();
                }
            }

            public void Exit()
            {
                Volatile.Write(ref _holder, null);
                if (!Fair)
                {
                    Monitor.Exit(Gate);
                    return;
                }
                Interlocked.Increment(ref _serving);
                if (Volatile.Read(ref _waiters) > 0)
                    lock (_turn)
                        Monitor.PulseAll(_turn);
            }

            /// <summary>Fair pools: steps holding or queued for the gate now (tickets handed out and not yet passed on).</summary>
            public long Queued => Interlocked.Read(ref _nextTicket) - Interlocked.Read(ref _serving);
        }

        // ALL POOL GATES, for creating and destroying a physics system.
        //
        // joltc keeps every physics system in one process-wide map (s_PhysicsSystems, joltc.cpp): Create inserts,
        // Destroy erases, and a step listener's OnStep reads it (joltc.cpp:5396) with no lock. An Update on any pool
        // can call OnStep, so a system may be created or destroyed only while NO pool is inside an Update: the
        // caller holds every pool's gate. Step holds one gate, so waiting for all of them waits for the Updates in
        // flight (one frame at most) and holds off every region's next step while the native call runs.
        //
        // LOCK ORDER, whole backend: pool gates in ascending pool index (all of them, or the one a Step takes),
        // then a region's _simLock, then _characterGate; s_systemMapGate and s_foundationGate are leaves taken
        // last, and nothing takes a gate while holding any of them. Step takes a single gate, so two takers of
        // all gates cannot invert, and a Step holding gate k never waits for a gate. Dispose takes the gates
        // BEFORE _simLock (the order Step uses); Initialize takes them for the system create only, and holds no
        // _simLock then. A fair gate is not re-entrant: do not create or dispose a region on a thread inside
        // its own Step.
        private readonly struct AllPoolGates : IDisposable
        {
            private readonly JobPool[] _held;
            private AllPoolGates(JobPool[] held) { _held = held; }

            public static AllPoolGates Take(JobPool[] pools, string? name)
            {
                int taken = 0;
                try
                {
                    for (; taken < pools.Length; taken++)
                        pools[taken].Enter(name, out _, out _);
                    return new AllPoolGates(pools);
                }
                catch
                {
                    for (int i = taken - 1; i >= 0; i--)
                        pools[i].Exit();
                    throw;
                }
            }

            public void Dispose()
            {
                if (_held == null)
                    return;   // default(AllPoolGates): nothing was taken
                for (int i = _held.Length - 1; i >= 0; i--)
                    _held[i].Exit();
            }
        }

        /// <summary>
        /// TEST-ONLY: the lock order of AllPoolGates on <paramref name="poolCount"/> stand-alone pools (the stock native
        /// runs one pool, so a real two-pool process cannot be built here). Two threads take every gate and
        /// <paramref name="poolCount"/> x 2 threads each take one gate as Step does, <paramref name="rounds"/> times; a
        /// holder of all gates must never find a Step inside, and a Step must never run while all gates are held. Returns
        /// false for Completed when the threads did not finish within <paramref name="limit"/> (a deadlock). Needs Foundation
        /// up, as any live backend has it.
        /// </summary>
        internal static (bool Completed, int Violations) AllGatesProbeForTest(int poolCount, bool fair, int rounds, TimeSpan limit)
        {
            var pools = new JobPool[poolCount];
            for (int i = 0; i < poolCount; i++)
                pools[i] = new JobPool(i, new JobSystemThreadPool(new JobSystemThreadPoolConfig
                    { maxJobs = JoltMaxJobs, maxBarriers = JoltMaxBarriers, numThreads = 1 }), fair);
            var inside = new int[poolCount];
            int allHeld = 0, violations = 0;
            var threads = new List<Thread>();
            for (int k = 0; k < 2; k++)
                threads.Add(new Thread(() =>
                {
                    for (int r = 0; r < rounds; r++)
                        using (AllPoolGates.Take(pools, "probe"))
                        {
                            Volatile.Write(ref allHeld, 1);
                            for (int p = 0; p < poolCount; p++)
                                if (Volatile.Read(ref inside[p]) != 0)
                                    Interlocked.Increment(ref violations);
                            Thread.SpinWait(200);
                            Volatile.Write(ref allHeld, 0);
                        }
                }) { IsBackground = true, Name = "probe-all-gates" });
            for (int k = 0; k < poolCount * 2; k++)
            {
                int p = k % poolCount;
                threads.Add(new Thread(() =>
                {
                    for (int r = 0; r < rounds * 2; r++)
                    {
                        pools[p].Enter("probe-step", out _, out _);
                        try
                        {
                            Interlocked.Increment(ref inside[p]);
                            if (Volatile.Read(ref allHeld) != 0)
                                Interlocked.Increment(ref violations);
                            Thread.SpinWait(100);
                            Interlocked.Decrement(ref inside[p]);
                        }
                        finally { pools[p].Exit(); }
                    }
                }) { IsBackground = true, Name = "probe-step" });
            }
            foreach (Thread th in threads) th.Start();
            var deadline = DateTime.UtcNow + limit;
            bool completed = true;
            foreach (Thread th in threads)
                completed &= th.Join(Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
            if (completed)
                foreach (JobPool p in pools)
                    DestroyJobSystem(p.System);
            return (completed, Volatile.Read(ref violations));
        }

        public const int MaxJobPools = 64;
        private const int JoltMaxJobs = 2048;     // PhysicsSettings.h cMaxPhysicsJobs (one Update per pool)
        private const int JoltMaxBarriers = 8;      // PhysicsSettings.h cMaxPhysicsBarriers
        private static JobPool[]? s_pools;
        private static int s_jobThreads;            // the resolved total the pools were sized from (capacity stat)
        private static int s_jobThreadsPerPool;
        private static int s_jobThreadSource;       // how that total was chosen (JobThreadSource)
        private static int s_jobPoolsRequested;     // the pools the first region asked for (resolved)
        private static string? s_jobPoolsLimitedBy; // why fewer pools run than were asked for; null when as asked

        // This region's pool (assigned at Initialize, under s_foundationGate) and its waits at the pool's gate:
        // written by Step under _simLock, read through Interlocked.
        private JobPool? _pool;
        private long _gateWaits;
        private long _gateWaitTicksTotal;
        private long _gateWaitTicksMax;
        // Step's waits for this region's own _simLock (held by a body change or query on another thread): written by
        // Step, read through Interlocked.
        private long _simLockWaits;
        private long _simLockWaitTicksTotal;
        private long _simLockWaitTicksMax;

        /// <summary>
        /// TEST-ONLY: run by Initialize right after the physics system is created and its events subscribed, so a
        /// test can make Initialize fail part-way by throwing here.
        /// </summary>
        internal Action? AfterSystemCreatedForTest;

        /// <summary>
        /// TEST-ONLY: hold this region's pool gate, as another region's Step would, until the returned
        /// object is disposed. With the default (Monitor) gate, dispose it on the thread that called this.
        /// </summary>
        internal IDisposable HoldPoolGateForTest()
        {
            JobPool pool = _pool ?? throw new InvalidOperationException("HoldPoolGateForTest: no pool assigned.");
            pool.Enter(_settings.RegionName, out _, out _);
            return new GateHold(pool);
        }

        /// <summary>TEST-ONLY: run by Step right after it has taken the pool's gate, before the step itself.</summary>
        internal Action<JoltPhysicsBackend>? GateTakenForTest;

        /// <summary>
        /// TEST-ONLY: set the hooks of this region's pool, which every region on the pool shares. <paramref name="arrived"/>
        /// runs when a step reaching the gate has found whether it must wait (the region's name, and true when it must);
        /// <paramref name="taken"/> runs once it has the gate, before it names itself as the holder. Null clears a hook.
        /// </summary>
        internal void SetPoolHooksForTest(Action<string?, bool>? arrived, Action<string?>? taken)
        {
            JobPool pool = _pool ?? throw new InvalidOperationException("SetPoolHooksForTest: no pool assigned.");
            pool.ArrivedForTest = arrived;
            pool.TakenForTest = taken;
        }

        /// <summary>TEST-ONLY: run by Step when it finds its region's lock held by another thread, before it waits.</summary>
        internal Action? RegionLockBusyForTest;

        /// <summary>TEST-ONLY: with fair handoff, the steps holding or queued for this region's pool now; -1 otherwise.</summary>
        internal long PoolQueuedForTest => _pool is { Fair: true } p ? p.Queued : -1;

        private sealed class GateHold : IDisposable
        {
            private JobPool? _pool;
            public GateHold(JobPool pool) { _pool = pool; }
            public void Dispose()
            {
                JobPool? p = Interlocked.Exchange(ref _pool, null);
                p?.Exit();
            }
        }

        /// <summary>JobPools for a settings struct: 0 (unset) = 1; otherwise clamped to [1, 64].</summary>
        public static int ResolveJobPools(int requested)
            => requested <= 0 ? 1 : Math.Clamp(requested, 1, MaxJobPools);

        /// <summary>
        /// The job pools that run on <paramref name="native"/>: <see cref="ResolveJobPools(int)"/>, or 1 when the native
        /// is not safe for more than one pool. On such a native (the stock joltc) every physics system draws on one
        /// process-wide TempAllocator, and two physics updates at once on two pools stop the process ("TempAllocator:
        /// Freeing in the wrong order"); one pool admits one update at a time, so its regions take turns on it.
        /// </summary>
        public static int ResolveJobPools(int requested, JoltNativeInfo native)
            => native.SafeForMultiplePools ? ResolveJobPools(requested) : 1;

        /// <summary>
        /// Why <paramref name="native"/> gets fewer pools than <paramref name="requested"/> asks for, in words; null when
        /// it gets what is asked.
        /// </summary>
        public static string? JobPoolLimitReason(int requested, JoltNativeInfo native)
        {
            if (ResolveJobPools(requested, native) == ResolveJobPools(requested))
                return null;
            string what = native.Build?.Source ?? "a build this module has no record of";
            return $"the loaded joltc ({what}) is not safe for more than one job pool: its physics systems share one " +
                   "scratch allocator, and two physics updates at once would stop the process";
        }

        /// <summary>
        /// Workers per pool when <paramref name="totalThreads"/> (the resolved ThreadCount) is split across
        /// <paramref name="jobPools"/> pools: max(1, total / pools). The remainder is not started, so the process
        /// never runs more workers than ThreadCount unless ThreadCount is smaller than JobPools.
        /// </summary>
        public static int ResolveThreadsPerPool(int totalThreads, int jobPools)
            => Math.Max(1, totalThreads / ResolveJobPools(jobPools));
        private ObjectLayerPairFilterTable? _objectLayerPairFilter;
        private BroadPhaseLayerInterfaceTable? _broadPhaseInterface;
        private ObjectVsBroadPhaseLayerFilterTable? _objectVsBroadPhaseFilter;
        // NOTE: 2.19.1 has NO TempAllocator - temp allocation is internal
        // to PhysicsSystem.Update. There is deliberately no _tempAllocator field.

        // Cached BodyInterface, valid for the PhysicsSystem's lifetime.
        //
        // This LOCKING BodyInterface is NOT "safe to call from any thread" in the sense that would let the
        // taint queue be dropped and Create/Remove/Set* run straight from the scene thread. Each of these
        // races is real:
        //   1. NarrowPhaseQuery races Update   -> TempAllocator abort (so queries take _simLock)
        //   2. Dispose races Step              -> use-after-free      (so Dispose takes _simLock + _disposed)
        //   3. body Create/Remove races Update -> TempAllocator abort on login with a physical object
        //      (so EVERY body op takes _simLock)
        //   4. Update races Update ACROSS REGIONS on a process-shared TempAllocator (stock joltc) -> abort
        //      (so every region's Update runs on one job pool - see _simLock's comment).
        // The per-body locking BodyInterface protects per-body DATA, but structural broadphase mutation
        // (add/remove) and the LIFO TempAllocator are NOT safe concurrent with _system.Update.
        //
        // THE RULE, no exceptions: every native call that touches this region's PhysicsSystem - Step, the
        // queries, and ALL body ops below - is serialised through this backend's _simLock.
        // Character ops use _characterGate (per-instance), always taken INSIDE _simLock; the CharacterVirtual
        // is serialised against the character step, and its ExtendedUpdate/TempAllocator use happens inside
        // Step which already holds _simLock. Shape Create* are the one exception: they build immutable
        // ref-counted Shapes independently of any live system (no broadphase/TempAllocator touch), so they
        // stay off _simLock to keep cooking off the hot lock. (A per-call entry trace of the cross-region
        // abort showed ONLY Step on three threads - no Create*/character op was the racer.)
        private BodyInterface _bodyInterface;

        // --- Active-body tracking ---
        // OnBodyActivated/OnBodyDeactivated fire from Jolt WORKER threads during Update(), and
        // activation can also flip from the SCENE thread (SetBodyTransform activate:true - no
        // taint queue). A plain shared HashSet would tear. So the event handlers only ENQUEUE;
        // the HashSet is owned SOLELY by the Step thread. Zero cross-thread set mutation; zero
        // per-frame allocation (the scratch collections are Clear()ed and refilled, not realloc'd;
        // foreach over a concrete HashSet/List uses a struct enumerator).
        private readonly ConcurrentQueue<ActivationDelta> _activationQueue = new ConcurrentQueue<ActivationDelta>();
        private readonly HashSet<uint> _activeBodies = new HashSet<uint>();   // step-thread only

        // Jolt's PhysicsSettings.mLinearCastThreshold (its default 0.75), read from the system once it exists: the share
        // of a shape's inner radius a LinearCast body must move in a collision step to be cast (UpdateCastBySpeed).
        private float _linearCastThreshold = 0.75f;
        private readonly HashSet<uint> _justActivated = new HashSet<uint>();  // scratch, per-step
        private readonly List<uint> _justDeactivated = new List<uint>();      // scratch, per-step
        private readonly List<uint> _staleActive = new List<uint>();          // scratch, per-step

        // A fair drain. Settle (JustDeactivated) states that do not fit the caller's buffer
        // wait here for the next Step instead of being dropped; active bodies are emitted round-robin from a
        // cursor that persists across Steps, so an overflowing region rotates through every active body rather
        // than starving the same tail. Step-thread only; reused, so no allocation after warm-up.
        private readonly Queue<uint> _pendingSettle = new Queue<uint>();
        private readonly List<uint> _activeSnapshot = new List<uint>();
        private int _activeCursor;
        private int _characterCursor;   // same rotation for the character drain (under _characterGate)

        // Reverse map: Jolt BodyID.ID -> our record. Written on Create/Remove (scene thread),
        // read from Step and from the contact/activation callbacks (worker threads).
        private readonly ConcurrentDictionary<uint, JoltBodyRecord> _joltToRecord =
            new ConcurrentDictionary<uint, JoltBodyRecord>();

        // The Jolt ids of the bodies removed since the avatars were last stepped (only kept while there is an avatar): an avatar
        // whose ground was one of them finds its ground again before its step (StepCharacter). Written under _simLock, read and
        // cleared by the step.
        private readonly ConcurrentDictionary<uint, byte> _removedBodyIds = new ConcurrentDictionary<uint, byte>();

        // Current terrain body (SetTerrain replaces it). BodyId.Invalid = none.
        private BodyId _terrainBody = BodyId.Invalid;

        // Mutator calls dropped for a non-finite argument. Interlocked; read by GetCapacityStats.
        private long _rejectedNonFinite;

        // Capacity failures, counted instead of discarded. The update-error counters are
        // written only by Step (under _simLock); BodyCreateFailures only by CreateBody (under _simLock). All are
        // read through Interlocked so GetCapacityStats is safe from any thread.
        private long _manifoldCacheFullSteps;
        private long _bodyPairCacheFullSteps;
        private long _contactConstraintsFullSteps;
        private int _lastUpdateError;   // PhysicsUpdateErrors
        private long _bodyCreateFailures;
        private bool _terrainBodyMissing;   // the last SetTerrain got no body (MaxBodies); Volatile

        // Region water plane height (metres, region-local Z). Stored for buoyancy and queries;
        // no water collision body in the solve yet - water is a force field, not a surface.
        private float _waterHeight;

        // ObjectLayerFilter objects (native callbacks) keyed by QueryFilter value, built lazily so each
        // distinct filter allocates its callback once. Disposed in Dispose.
        private readonly ConcurrentDictionary<QueryFilter, LayerQueryFilter> _queryFilters =
            new ConcurrentDictionary<QueryFilter, LayerQueryFilter>();

        // =====================================================================
        // _simLock - the per-backend gate serialising this region's native Jolt
        // calls. PER-INSTANCE: one lock per backend / region, so regions step in
        // parallel across cores.
        //
        // !!! CRITICAL DEPENDENCY: the scratch allocator !!!
        // Stock JoltPhysics.Native 1.0.4 joltc supplies ONE process-global
        // TempAllocatorImpl (a LIFO stack, NOT thread-safe) to every
        // JPH_PhysicsSystem_Update and all six JPH_CharacterVirtual_* scratch
        // consumers. With it shared, three regions' heartbeats stepping
        // concurrently produce "TempAllocator: Freeing in the wrong order" ->
        // std::abort(). With the STOCK DLL a per-instance
        // lock CANNOT protect it - two regions each holding their own _simLock
        // still hammer the one allocator. What protects it there is the job
        // pool's gate (one Step at a time per pool; see "The pool gate rule")
        // and ONE pool: ResolveJobPools(requested, native) gives one pool on a
        // native that JoltNative's record does not mark safe for more. Anyone
        // touching this lock's scope or the gate must keep every allocator use
        // inside the gate (ShapeAndAllocatorRuleTests).
        //
        // The stock joltc (amerkoleci/joltc @ 1715c5aab8, the source of
        // JoltPhysics.Native 1.0.4) has ONE process-wide
        // TempAllocatorImplWithMallocFallback(8MB), used at ALL SEVEN consuming
        // sites: PhysicsSystem_Update and CharacterVirtual Update /
        // ExtendedUpdate / RefreshContacts / WalkStairs / StickToFloor /
        // SetShape. All regions in the process therefore share one job pool,
        // and its gate keeps two regions' native calls from overlapping.
        // _simLock guards the INTRA-region races (this region's scene thread vs
        // its own heartbeat Step). A joltc with an allocator per physics system
        // (BulletSim's per-world scratch, PhysX's per-PxScene scratch) would
        // allow one pool per region; that needs a change in joltc itself.
        //
        // The races (all real, all still needed):
        //   1. NarrowPhaseQuery races Update   -> queries take _simLock
        //   2. Dispose races Step              -> Dispose takes _simLock + _disposed
        //   3. body Create/Remove races Update -> all body ops take _simLock
        //   4. Update races Update ACROSS REGIONS on the stock shared allocator
        //      -> the one job pool's gate covers it (a STATIC _simLock would
        //      too); this lock stays per-instance.
        //      1-3 are the races this lock guards.
        //
        // LOCK ORDER - always _simLock FIRST, then _characterGate. Both are
        // per-instance and _characterGate is only ever taken INSIDE _simLock, so
        // the order holds per backend, and no thread ever holds two backends'
        // locks at once - no cross-region inversion. Monitor is re-entrant per
        // thread, so a nested take of _simLock on the same thread is harmless.
        // _gate (HandleTable) is a leaf lock - it calls nothing - so it can be
        // taken under either.
        // =====================================================================
        private readonly object _simLock = new object();

        // =====================================================================
        // The allocator owner check.
        //
        // joltc hands its TempAllocator to exactly SEVEN native entry points
        // (joltc.cpp:1050 PhysicsSystem_Update, :8107/:8135/:8151/:8182/:8198/:8223 the CharacterVirtual
        // scratch users). TempAllocatorImpl is a LIFO stack with a non-atomic mTop whose ordering Jolt
        // guarantees "though job dependencies" (Jolt/Core/TempAllocator.h:11-13) - so two callers that are
        // not in one job graph corrupt it, and TempAllocatorImpl::Free answers with std::abort() (:83-84).
        // A native abort takes the process with it: no exception, no stack, no test failure - at most an
        // OS event-log entry and a console line.
        //
        // So the rule is made checkable. Every managed call site that reaches one of the seven calls
        // RequireSimLock first, which asks Monitor.IsEntered - "does THIS thread hold this backend's
        // _simLock" - and throws a managed, catchable exception naming the API when it does not. That
        // turns an unrecoverable native abort into a test failure, so an unlocked call site can be searched
        // for in tests instead of waited for in normal running.
        //
        // On by default in DEBUG. In RELEASE it costs a static bool read per call and is off unless a
        // test turns it on, so the shipped simulator pays nothing.
        // =====================================================================
        public static bool AllocatorOwnerCheck { get; set; } =
#if DEBUG
            true;
#else
            false;
#endif

        // ---------------------------------------------------------------------
        // The re-entry guard, and why the owner check cannot replace it.
        //
        // CharacterVirtual::ExtendedUpdate is the only one of the seven allocator entry points that calls BACK
        // into managed code while an allocator sequence is open: OnContactAdded/Persisted/Removed and
        // OnCharacterContactAdded/Persisted all fire from inside it. Monitor is re-entrant per thread, so a
        // handler that calls back into any of the seven takes _simLock again without blocking, and the owner
        // check - which only asks "does this thread hold the lock" - sees nothing wrong. The allocator does:
        // the inner call allocates on top of the outer sequence's frames and frees them out of order, which
        // TempAllocatorImpl::Free answers with std::abort().
        //
        // So this tracks the open site per THREAD, not per backend. Cross-backend re-entry on one thread is a
        // bug for the same reason - the inner call runs on another system's allocator while this one's sequence
        // is still open, and neither allocator's LIFO order survives if the callback does anything on either.
        // ---------------------------------------------------------------------
        [ThreadStatic] private static string? t_allocatorSiteInFlight;

        // ---------------------------------------------------------------------
        // The pool gate rule.
        //
        // Several regions can share one job pool, and with the stock joltc of the
        // JoltPhysics.Native package every PhysicsSystem draws on ONE process-wide TempAllocatorImpl. Two regions on one pool
        // are then safe only because every use of that allocator happens inside the pool's gate, which admits one
        // Step at a time: PhysicsSystem::Update and CharacterVirtual::ExtendedUpdate are called only from Step, under
        // the gate. CharacterVirtual::SetShape is the one call outside it, and it never reaches the allocator (see
        // SetShapeWithoutScratch). Nothing in the native checks this, so the allocator check does: Step records the
        // pool whose gate this thread holds, and a site that needs the gate fails when this thread does not hold
        // its region's. ShapeAndAllocatorRuleTests also lists every call into the seven allocator entry points.
        // ---------------------------------------------------------------------
        [ThreadStatic] private static JobPool? t_gateHeld;

        /// <summary>Marks one of the seven allocator entry points as open on this thread for its duration.</summary>
        private readonly struct AllocatorSite : IDisposable
        {
            private readonly string? m_previous;
            private readonly bool m_on;

            public AllocatorSite(JoltPhysicsBackend owner, string api, bool needsGate)
            {
                m_on = AllocatorOwnerCheck;
                m_previous = null;
                if (!m_on)
                    return;

                owner.RequireSimLock(api);
                if (needsGate && (owner._pool == null || !ReferenceEquals(t_gateHeld, owner._pool)))
                    throw new InvalidOperationException(
                        $"TempAllocator use outside the job pool gate: {api} reaches a TempAllocator but the calling thread "
                        + "does not hold this region's pool gate. On a joltc whose regions share one allocator, the gate is "
                        + "what keeps two regions' calls apart (Jolt/Core/TempAllocator.h:83-84).");

                string? open = t_allocatorSiteInFlight;
                if (open is not null)
                    throw new InvalidOperationException(
                        $"Re-entrant TempAllocator use: {api} entered while {open} is still open on this thread. Both draw on a "
                        + "PhysicsSystem's TempAllocator, which is a LIFO stack: the inner call's frames sit on "
                        + "top of the outer call's and are freed out of order, which Jolt answers with "
                        + "std::abort() (Jolt/Core/TempAllocator.h:83-84). _simLock does not catch this - Monitor "
                        + "is re-entrant, so the nested call holds it too.");

                t_allocatorSiteInFlight = api;
            }

            public void Dispose()
            {
                if (m_on) t_allocatorSiteInFlight = m_previous;
            }
        }

        /// <summary>Open an allocator site on this thread. Checks the lock, the pool gate and the re-entry rule together.</summary>
        private AllocatorSite Enter(string api) => new AllocatorSite(this, api, needsGate: true);

        /// <summary>The one allocator site outside the pool gate; see <see cref="SetShapeWithoutScratch"/>.</summary>
        private AllocatorSite EnterOutsideGate(string api) => new AllocatorSite(this, api, needsGate: false);

        /// <summary>TEST-ONLY: open the allocator check of a site that needs the pool gate, as Step's sites do.</summary>
        internal void EnterGatedAllocatorSiteForTest(string api)
        {
            lock (_simLock)
                using (Enter(api)) { }
        }

        private void RequireSimLock(string api)
        {
            if (!AllocatorOwnerCheck || Monitor.IsEntered(_simLock))
                return;
            throw new InvalidOperationException(
                $"Unlocked TempAllocator use: {api} reaches this PhysicsSystem's TempAllocator but the calling thread does not hold "
                + "_simLock. Concurrent use of that allocator is what aborts the process from native code "
                + "(Jolt/Core/TempAllocator.h:83-84). Take _simLock, then _characterGate.");
        }

        // Set true (under _simLock) by Dispose. Step and every native query check it under _simLock and
        // bail out, so a heartbeat Step that races region shutdown does NOTHING rather than touching a
        // freed PhysicsSystem / CharacterVirtual. Scene.Close only Sleep(500)s
        // to signal the heartbeat (no Join), so Dispose could free native memory mid-Step -> AccessViolation
        // -> process death -> every region AFTER the first lost its clean-shutdown Backup(true) flush.
        private volatile bool _disposed;

        // Foundation.Init / Foundation.Shutdown are PROCESS-GLOBAL Jolt lifecycle calls, but there is one
        // backend per region (INonSharedRegionModule). The FIRST region to dispose used to call
        // Foundation.Shutdown() and tear down the global allocator/registry out from under every OTHER
        // region's still-running heartbeat -> use-after-free in their ExtendedUpdate/Update. Ref-count it:
        // Init only on the 0->1 transition, Shutdown only on the 1->0 transition (the LAST region out).
        private static readonly object s_foundationGate = new object();
        private static int s_foundationRefCount;

        // Characters (CharacterVirtual) are NOT lock-free like BodyInterface, and they are stepped on
        // the Step thread OUTSIDE _system.Update. So all character create/remove/set/step operations are
        // serialised through this gate and the step-thread-owned list. (Abstraction friction vs the
        // taint-free body path.)
        // Always taken INSIDE _simLock when both are needed (see the lock-order note above).
        private readonly object _characterGate = new object();
        private readonly List<JoltCharacterRecord> _characterList = new List<JoltCharacterRecord>();

        // Shared avatar-vs-avatar collision. Every character is registered here so their capsules
        // collide (push/block) - Jolt's default matches SL's [BulletSim]AvatarToAvatarCollisionsByDefault
        // = true. It is not a config knob yet. Disposed after the characters.
        private CharacterVsCharacterCollisionSimple? _charVsChar;

        // Jolt's CapsuleShape axis is Y; a Z-up avatar capsule must stand along world Z. Rotate +90 deg
        // about X (Y -> Z), the same Z-up trick the heightfield wrapper uses. Shared, immutable.
        private static readonly Quaternion CapsuleYToZ = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

        // CharacterDesc.PushStrength is a relative scale (1.0 = normal); this is the newton value it
        // scales, chosen to equal Jolt's own MaxStrength default so PushStrength 1.0 = stock behaviour.
        private const float PushStrengthBaseNewtons = 100f;

        // Box convex radius is clamped to min(this, 0.1 * smallest half-extent) so Jolt never
        // asserts "convex radius larger than shape".
        private const float DefaultConvexRadius = 0.05f;

        // Restitution below this closing speed is dropped (Jolt's default 1.0 m/s). Used ONLY to feed
        // the contact-impulse estimator (EstimateCollisionResponse) with the same threshold the solver
        // will use, so the reported impulse matches what actually gets applied.
        private const float MinVelocityForRestitution = 1.0f;

        // RayCastAll coincident-hit collapse: the heightfield's two triangles meeting at the ray XY report
        // two hits at the same point on the same body. Two hits within this radius (1 mm), on the SAME body,
        // collapse to one - matching BulletSim's single terrain hit without touching legitimate multi-hit.
        private const float CoincidentEpsilonSq = 1e-6f;

        // Minimum heightfield sample count per side. joltc silently mis-cooks n<3 (asserts
        // compiled out; checked against JoltPhysicsSharp 2.18.6); the terrain feed passes region-derived (N+1) odd counts (257, 513, ...),
        // which a 257-sample test showed cook faithfully. 4 is a defensive floor above the 3 hard limit.
        private const int MinHeightFieldSampleCount = 4;

        private readonly struct ActivationDelta
        {
            public readonly uint BodyId;
            public readonly bool Activated;
            public ActivationDelta(uint bodyId, bool activated) { BodyId = bodyId; Activated = activated; }
        }

        // Number of ObjectLayers = number of PhysicsLayer members. Derived from the
        // enum so the filter tables never silently drift if a layer is added.
        private static readonly uint ObjectLayerCount = (uint)Enum.GetValues(typeof(PhysicsLayer)).Length;

        // =====================================================================
        // Broad phase / object layers
        //
        // Jolt has two filtering tiers and conflating them is the classic
        // first-integration mistake:
        //
        //   ObjectLayer     - fine grained, per body, decides "can A and B ever
        //                     collide". This is our PhysicsLayer, 1:1.
        //   BroadPhaseLayer - coarse buckets the AABB tree is partitioned by.
        //                     Keep this to 3. More buckets = more tree walks.
        //
        // The win from getting this right: NON_MOVING is a separate tree that is
        // never rebuilt during normal operation. A region with 50k static prims
        // and 200 physical ones only ever re-walks the small tree.
        // =====================================================================
        private static class BroadPhase
        {
            public const byte NonMoving = 0;  // Terrain, Static
            public const byte Moving = 1;     // Dynamic, Avatar, Debris
            public const byte Sensor = 2;     // Sensor
            public const uint Count = 3;
        }

        private static byte ToBroadPhase(PhysicsLayer layer) => layer switch
        {
            PhysicsLayer.Terrain => BroadPhase.NonMoving,
            PhysicsLayer.Static => BroadPhase.NonMoving,
            PhysicsLayer.Dynamic => BroadPhase.Moving,
            PhysicsLayer.Avatar => BroadPhase.Moving,
            PhysicsLayer.Debris => BroadPhase.Moving,
            PhysicsLayer.Sensor => BroadPhase.Sensor,
            PhysicsLayer.AvatarQuery => BroadPhase.Moving, // in the broadphase so queries find it; collides with nothing
            PhysicsLayer.Phantom => BroadPhase.Moving,
            PhysicsLayer.MovingStatic => BroadPhase.Moving, // a prim keyframed motion moves: kept out of the tree of fixed prims
            _ => BroadPhase.Moving,
        };

        /// <summary>
        /// The collision matrix. This table IS the behaviour contract - most
        /// "why does my object fall through X" bugs are a wrong cell here.
        /// Terrain/Static never test against each other: that alone removes the
        /// dominant pair count in a built-up region.
        /// </summary>
        private static bool ShouldCollide(PhysicsLayer a, PhysicsLayer b)
        {
            // The avatar query-marker layer NEVER collides in simulation. Keeping it out of every
            // collision pair is exactly what makes the marker inert - no push, no contacts (verified) -
            // so it can be findable by queries without ever entering the solve.
            if (a == PhysicsLayer.AvatarQuery || b == PhysicsLayer.AvatarQuery)
                return false;

            // A phantom prim touches the terrain only: objects and avatars pass through it, and a physical one
            // rests on the ground (llVolumeDetect's comparison of Phantom and VolumeDetect, wiki.secondlife.com).
            if (a == PhysicsLayer.Phantom || b == PhysicsLayer.Phantom)
                return a == PhysicsLayer.Terrain || b == PhysicsLayer.Terrain;

            // A prim that keyframed motion moves collides exactly as a fixed one does; only its broad phase differs.
            if (a == PhysicsLayer.MovingStatic) a = PhysicsLayer.Static;
            if (b == PhysicsLayer.MovingStatic) b = PhysicsLayer.Static;

            // Normalise so we only fill the lower triangle.
            if (a > b) (a, b) = (b, a);

            return (a, b) switch
            {
                (PhysicsLayer.Terrain, PhysicsLayer.Terrain) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Static) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Terrain, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Terrain, PhysicsLayer.Sensor) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Static, PhysicsLayer.Static) => false,
                (PhysicsLayer.Static, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Static, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Static, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Static, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Dynamic, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Avatar, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Avatar, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Avatar, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Sensor, PhysicsLayer.Sensor) => false,
                (PhysicsLayer.Sensor, PhysicsLayer.Debris) => false,

                // Debris vs Debris deliberately off - that is the whole point
                // of the tier. Turning it on silently reintroduces the O(n^2).
                (PhysicsLayer.Debris, PhysicsLayer.Debris) => false,

                _ => true,
            };
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>The most worker threads an automatic ThreadCount (0) gives one pool.</summary>
        public const int AutoMaxThreadsPerPool = 4;

        /// <summary>
        /// Workers per pool when ThreadCount is automatic (0): the pool's share of ProcessorCount - 1, at most
        /// <see cref="AutoMaxThreadsPerPool"/> and at least 1. One region's step is spread over its pool's workers, and
        /// past a few workers the hand-out and wake-up of each job costs more than the work it spreads: in the pool
        /// benchmark a pile of 1000 boxes stepped fastest on 4 workers, and a single moving body slowest on the most.
        /// </summary>
        public static int ResolveAutoThreadsPerPool(int jobPools, int processorCount)
            => Math.Clamp(Math.Max(1, processorCount - 1) / ResolveJobPools(jobPools), 1, AutoMaxThreadsPerPool);

        /// <summary>
        /// The total worker count a region's settings ask the shared job pools for, all pools together: a positive
        /// ThreadCount as given (split across the pools by <see cref="ResolveThreadsPerPool"/>); 0 (automatic) gives
        /// each pool <see cref="ResolveAutoThreadsPerPool"/> workers; DeterministicMode asks for 1. Only the FIRST
        /// region's request sizes the process-wide pools; the module compares its own request against
        /// <see cref="PhysicsCapacityStats.JobThreadCount"/> and warns.
        /// </summary>
        public static int ResolveThreadCount(int threadCount, bool deterministicMode, int jobPools)
            => ResolveThreadCount(threadCount, deterministicMode, jobPools, Environment.ProcessorCount);

        /// <summary>As <see cref="ResolveThreadCount(int, bool, int)"/>, for a given processor count.</summary>
        public static int ResolveThreadCount(int threadCount, bool deterministicMode, int jobPools, int processorCount)
        {
            if (deterministicMode)
                return 1;
            if (threadCount > 0)
                return threadCount;
            return ResolveJobPools(jobPools) * ResolveAutoThreadsPerPool(jobPools, processorCount);
        }

        /// <summary>How a region's settings choose the job thread count.</summary>
        public static JobThreadSource ResolveThreadSource(int threadCount, bool deterministicMode)
            => deterministicMode ? JobThreadSource.Deterministic : threadCount > 0 ? JobThreadSource.Set : JobThreadSource.Automatic;

        public void Initialize(in PhysicsBackendSettings settings)
        {
            _settings = settings;

            // Native boot. false => single precision.
            // PROCESS-GLOBAL and REF-COUNTED: only the first region to come up actually calls
            // Foundation.Init; Dispose only calls Foundation.Shutdown when the last region goes down (see
            // s_foundationRefCount). This stops one region's shutdown from tearing down Jolt under the
            // others (a multi-region AccessViolation).
            lock (s_foundationGate)
            {
                if (s_foundationRefCount == 0)
                {
                    // The joltc for this platform, from runtimes/<rid>/native/ (JoltNative). Throws a
                    // JoltNativeException naming the platform when there is no usable native.
                    JoltNativeInfo native = JoltNative.EnsureLoaded(settings.AllowUnrecordedNative);
                    if (!Foundation.Init(false))
                        throw new InvalidOperationException("Jolt Foundation.Init(false) failed.");

                    // Create the shared, process-capped job pools here (first region in),
                    // sized by THIS region's settings: [Jolt] JobPools pools splitting `threads`, each
                    // with Jolt's single-system limits (2048 jobs, 8 barriers) - right because each pool runs
                    // one Update at a time. A native that is not safe for more than one pool gets one pool,
                    // sized as [Jolt] JobPools = 1 would size it.
                    int pools = ResolveJobPools(settings.JobPools, native);
                    int threads = ResolveThreadCount(settings.ThreadCount, settings.DeterministicMode, pools);
                    int perPool = ResolveThreadsPerPool(threads, pools);
                    var created = new JobPool[pools];
                    for (int i = 0; i < pools; i++)
                        created[i] = new JobPool(i, new JobSystemThreadPool(new JobSystemThreadPoolConfig
                        {
                            maxJobs = JoltMaxJobs,
                            maxBarriers = JoltMaxBarriers,
                            numThreads = perPool,
                        }), settings.JobPoolFairHandoff);
                    s_pools = created;
                    s_jobThreads = threads;
                    s_jobThreadsPerPool = perPool;
                    s_jobThreadSource = (int)ResolveThreadSource(settings.ThreadCount, settings.DeterministicMode);
                    s_jobPoolsRequested = ResolveJobPools(settings.JobPools);
                    s_jobPoolsLimitedBy = JobPoolLimitReason(settings.JobPools, native);
                }
                s_foundationRefCount++;

                // This region's pool - the one with the fewest regions, ties to the lowest index.
                JobPool pick = s_pools![0];
                foreach (JobPool p in s_pools)
                    if (p.Regions < pick.Regions)
                        pick = p;
                pick.Regions++;
                _pool = pick;
            }

            // --- Object-layer collision matrix ---
            // ObjectLayerPairFilterTable starts with EVERY pair disabled; we turn on
            // exactly the ShouldCollide cells. Driving the table from ShouldCollide (rather
            // than hand-listing pairs) keeps the matrix the single source of truth AND
            // guarantees every one of the 21 unordered pairs is decided explicitly.
            // EnableCollision is symmetric, so we only walk the lower triangle (a <= b).
            _objectLayerPairFilter = new ObjectLayerPairFilterTable(ObjectLayerCount);
            for (uint a = 0; a < ObjectLayerCount; a++)
            {
                for (uint b = a; b < ObjectLayerCount; b++)
                {
                    if (ShouldCollide((PhysicsLayer)a, (PhysicsLayer)b))
                        _objectLayerPairFilter.EnableCollision(new ObjectLayer(a), new ObjectLayer(b));
                }
            }

            // --- ObjectLayer -> BroadPhaseLayer map ---
            _broadPhaseInterface = new BroadPhaseLayerInterfaceTable(ObjectLayerCount, BroadPhase.Count);
            for (uint a = 0; a < ObjectLayerCount; a++)
            {
                _broadPhaseInterface.MapObjectToBroadPhaseLayer(
                    new ObjectLayer(a),
                    new BroadPhaseLayer(ToBroadPhase((PhysicsLayer)a)));
            }

            // --- Object-vs-broadphase filter (the third table) ---
            // Built FROM the two tables above; it answers "can an object in layer X ever
            // touch broad-phase bucket Y" and is what actually prunes tree walks.
            _objectVsBroadPhaseFilter = new ObjectVsBroadPhaseLayerFilterTable(
                _broadPhaseInterface, BroadPhase.Count, _objectLayerPairFilter, ObjectLayerCount);

            var systemSettings = new PhysicsSystemSettings
            {
                MaxBodies = settings.MaxBodies,
                MaxBodyPairs = settings.MaxBodyPairs,
                MaxContactConstraints = settings.MaxContactConstraints,
                ObjectLayerPairFilter = _objectLayerPairFilter,
                BroadPhaseLayerInterface = _broadPhaseInterface,
                ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhaseFilter,
            };

            // JPH_PhysicsSystem_Create inserts into joltc's global map of systems, which an Update on any pool may
            // read; so no pool may be inside an Update (AllPoolGates). The reference this region took above keeps
            // s_pools alive until its own Dispose.
            using (AllPoolGates.Take(s_pools!, settings.RegionName))
            lock (s_systemMapGate)
            {
                _system = new PhysicsSystem(systemSettings);
                ClearContactValidateProc();
            }
            _system.Gravity = settings.Gravity;
            _linearCastThreshold = _system.Settings.LinearCastThreshold;
            _bodyInterface = _system.BodyInterface;
            _bodyInterfaceNoLock = _system.BodyInterfaceNoLock;
            // [Jolt] VelocityIterations and PositionIterations: the solver's velocity and position steps per collision step.
            // PhysicsBackendSettings.Default keeps Jolt's own 10 and 2; the module's [Jolt] default is 20 velocity steps, since a
            // tall stack needs more to come to rest: ten 0.5 m boxes do not fall asleep at 10 and do at 20.
            PhysicsSettings solver = _system.Settings;
            if (settings.VelocityIterations > 0)
                solver.NumVelocitySteps = (uint)settings.VelocityIterations;
            if (settings.PositionIterations > 0)
                solver.NumPositionSteps = (uint)settings.PositionIterations;
            if (settings.SpeculativeContactDistance is float speculative)
                solver.SpeculativeContactDistance = speculative;
            if (settings.SleepVelocityThreshold is float sleepThreshold)
                solver.PointVelocitySleepThreshold = sleepThreshold;
            _system.Settings = solver;
            _penetrationSlop = solver.PenetrationSlop;
            _positionShare = 1f - MathF.Pow(1f - solver.Baumgarte, Math.Max(1, solver.NumPositionSteps));

            // Determinism (for A/B parity runs): single-threaded ALONE is not enough - Jolt
            // also needs its DeterministicSimulation flag on to guarantee bit-identical re-runs. It
            // defaults true in Jolt's PhysicsSettings, but we set it EXPLICITLY when asked rather than lean on a default
            // that a future lib bump could flip. (Left untouched otherwise, to keep the fast path fast.)
            if (settings.DeterministicMode)
            {
                PhysicsSettings physicsSettings = _system.Settings;
                physicsSettings.DeterministicSimulation = true;
                _system.Settings = physicsSettings;
            }

            // Contacts + body activation arrive as C# EVENTS in 2.19.1, not a
            // listener object. The handlers ONLY enqueue / push into the ring - they never touch
            // scene state, never allocate, and never mutate the active set (see the field notes).
            _system.OnBodyActivated += HandleBodyActivated;
            _system.OnBodyDeactivated += HandleBodyDeactivated;
            _system.OnContactAdded += HandleContactAdded;
            _system.OnContactPersisted += HandleContactPersisted;
            _system.OnContactRemoved += HandleContactRemoved;

            AfterSystemCreatedForTest?.Invoke();

            // Worker pool (Update takes a JobSystem; no TempAllocator): shared, process-capped
            // pools created once under s_foundationGate above, NOT one per region; Update()
            // takes this region's _pool in Step. `threads` (above) sizes them on first region.

            // Avatar-vs-avatar collision registry (characters add themselves on create).
            _charVsChar = new CharacterVsCharacterCollisionSimple();

            // Contact ring; it is engine-agnostic. 2.19.1 exposes contacts as EVENTS on PhysicsSystem
            // (OnContactAdded/Persisted/Removed), NOT a SetContactListener object; the handlers
            // subscribed above push into this ring, and OnBodyActivated / OnBodyDeactivated keep
            // the O(active) set.
            _contactListener = new JoltContactListener(_settings.MaxContactConstraints * 2);
        }

        public void Dispose()
        {
            // Take _simLock for the WHOLE teardown so Dispose can never overlap an in-flight Step (Step
            // holds _simLock for its whole duration). Either Step runs to completion and THEN Dispose
            // proceeds, or Dispose gets in first, sets _disposed, and the next Step early-returns before
            // touching any now-freed native object. _disposed is set BEFORE any native free.
            // Deadlock-free: Step never blocks on anything Dispose holds, so a Dispose waiting on _simLock
            // waits at most one step, then proceeds. Lock order _simLock -> _characterGate is preserved.
            // Every pool gate first, then _simLock (see AllPoolGates): DisposeNative destroys the physics system,
            // which erases it from joltc's map while no Update may be reading it. A backend that never took a pool
            // (Initialize did not get that far) has no system to destroy and takes no gate.
            JobPool[]? pools = _pool != null ? s_pools : null;
            using (pools != null ? AllPoolGates.Take(pools, _settings.RegionName) : default)
            lock (_simLock)
            {
                if (_disposed)
                    return;        // idempotent
                _disposed = true;  // from here on Step + queries no-op

                DisposeNative();
            }

            // Foundation teardown is PROCESS-GLOBAL and ref-counted: only the LAST region out actually
            // shuts Jolt down. Done outside _simLock (different scope) but _disposed is already set, so this
            // instance's Step can't re-enter Jolt in the meantime. Other regions still up keep the count > 0.
            // Only a backend that took a reference gives one back: _pool is set in the same s_foundationGate block
            // as the increment, so a backend whose Initialize never ran, or failed before that block, must not
            // lower the count other regions hold.
            lock (s_foundationGate)
            {
                if (_pool == null)
                    return;
                _pool.Regions--;
                _pool = null;
                if (--s_foundationRefCount == 0)
                {
                    // Last region out: dispose the shared job pools BEFORE Foundation.
                    if (s_pools != null)
                        foreach (JobPool p in s_pools)
                            DestroyJobSystem(p.System);
                    s_pools = null;
                    Foundation.Shutdown();
                }
            }
        }

        // =====================================================================
        // The job pools' native job systems are destroyed by us.
        //
        // JoltPhysicsSharp 2.19.1's JobSystem sets its handle through the parameterless NativeObject constructor,
        // so NativeObject.OwnsHandle stays false and JobSystemThreadPool.Dispose() skips DisposeNative - i.e.
        // JPH_JobSystem_Destroy is never called and the pool's workers are never stopped or joined (the finalizer
        // path checks the same flag). Measured: +19 threads per create/dispose cycle (20 -> 116 over
        // five), still there after GC. Jolt's own destructor (~JobSystemThreadPool -> StopThreads) joins them,
        // so we call joltc's existing JPH_JobSystem_Destroy export ourselves, then dispose the wrapper (which,
        // OwnsHandle being false, destroys nothing a second time). If a future binding sets OwnsHandle, we skip
        // our destroy and its Dispose does it. No native change: the export ships in joltc.dll.
        // =====================================================================
        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_JobSystem_Destroy(IntPtr jobSystem);

        // NativeObject.OwnsHandle is `protected internal` in the binding - read it by reflection.
        private static readonly System.Reflection.PropertyInfo? s_ownsHandle = typeof(NativeObject).GetProperty(
            "OwnsHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        // =====================================================================
        // Shape settings are freed by us too.
        //
        // The same OwnsHandle defect: JoltPhysicsSharp 2.19.1 builds ConvexHullShapeSettings, MeshShapeSettings,
        // ScaledShapeSettings and RotatedTranslatedShapeSettings through the parameterless NativeObject constructor, so
        // their Dispose() frees nothing native. The native settings then outlive the cook for good, holding their copy
        // of the input (a mesh's triangles, a hull's points) and the shape Create() caches in them, which keeps that
        // shape alive as well: measured, about 236 KB for each 257-sample terrain cooked (the heightfield under the
        // Z-up wrapper) and 158 KB for each 3200-triangle mesh. joltc's create functions AddRef every settings object,
        // so JPH_ShapeSettings_Destroy (a Release) is what frees one. Settings the binding does own are left to its
        // Dispose; every settings object goes through here, so a binding that fixes the flag is handled the same way.
        // =====================================================================
        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_ShapeSettings_Destroy(IntPtr settings);

        internal static void FreeShapeSettings(ShapeSettings settings)
        {
            bool bindingDestroys = s_ownsHandle?.GetValue(settings) is true;
            if (!bindingDestroys && !settings.IsDisposed && settings.Handle != IntPtr.Zero)
                JPH_ShapeSettings_Destroy(settings.Handle);
            settings.Dispose();
        }

        // =====================================================================
        // Body creation settings are freed by us too.
        //
        // The same OwnsHandle defect: JoltPhysicsSharp 2.19.1 builds BodyCreationSettings through the parameterless
        // NativeObject constructor, so Dispose() frees nothing native. The native settings object keeps its reference on
        // the shape it was made with, so the shape outlives its last release and the destroy of the system that held the
        // body: measured, about 243 KB for every region start and stop (the terrain heightfield), and the shape of every
        // body ever created. The settings are only read by the call that makes the body, so they are destroyed right after.
        // =====================================================================
        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_BodyCreationSettings_Destroy(IntPtr settings);

        internal static void FreeBodyCreationSettings(BodyCreationSettings settings)
        {
            bool bindingDestroys = s_ownsHandle?.GetValue(settings) is true;
            if (!bindingDestroys && !settings.IsDisposed && settings.Handle != IntPtr.Zero)
                JPH_BodyCreationSettings_Destroy(settings.Handle);
            settings.Dispose();
        }

        private readonly struct ShapeSettingsScope : IDisposable
        {
            private readonly ShapeSettings _settings;
            public ShapeSettingsScope(ShapeSettings settings) => _settings = settings;
            public void Dispose() => FreeShapeSettings(_settings);
        }

        private static void DestroyJobSystem(JobSystemThreadPool pool)
        {
            // Unknown binding shape (no such property): assume it does NOT own the handle, as 2.19.1 does not.
            bool bindingDestroys = s_ownsHandle?.GetValue(pool) is true;
            if (!bindingDestroys && !pool.IsDisposed && pool.Handle != IntPtr.Zero)
                JPH_JobSystem_Destroy(pool.Handle);   // ~JobSystemThreadPool joins the workers
            pool.Dispose();
        }

        // =====================================================================
        // Each region's native physics system is destroyed by us too.
        //
        // The same OwnsHandle defect: the public PhysicsSystem(PhysicsSystemSettings) constructor assigns Handle through
        // the parameterless NativeObject constructor, so PhysicsSystem.Dispose() never runs its DisposeNative, which is
        // what calls JPH_PhysicsSystem_Destroy, destroys the contact and body-activation listeners it created, and frees
        // the GCHandle behind their userData. Without this every region teardown leaks the native system, its temp
        // allocator, the three layer-filter objects (JPH_PhysicsSystem_Destroy deletes those; joltc has no other
        // destroy for them), both listeners, and the GCHandle, which also keeps the managed wrapper alive.
        //
        // joltc keeps every system in a global map, s_PhysicsSystems: JPH_PhysicsSystem_Create inserts and
        // JPH_PhysicsSystem_Destroy erases. Regions are created and torn down on different threads. The native this
        // module runs on, the stock joltc of the JoltPhysics.Native package, has no lock on it. Both calls
        // are taken under s_systemMapGate, and with every pool's gate held (AllPoolGates), so no Update is running that
        // could read the map from a step listener's OnStep.
        // =====================================================================
        private static readonly object s_systemMapGate = new object();

        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_PhysicsSystem_Destroy(IntPtr system);

        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_ContactListener_Destroy(IntPtr listener);

        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_BodyActivationListener_Destroy(IntPtr listener);

        // The binding's private fields that hold the listeners and their userData (JoltPhysicsSharp 2.19.1, PhysicsSystem.cs).
        private static readonly System.Reflection.FieldInfo? s_contactListenerField = PhysicsSystemField("_contactListenerHandle");
        private static readonly System.Reflection.FieldInfo? s_activationListenerField = PhysicsSystemField("_bodyActivationListenerHandle");
        private static readonly System.Reflection.FieldInfo? s_listenerUserDataField = PhysicsSystemField("_listenerUserData");

        private static System.Reflection.FieldInfo? PhysicsSystemField(string name) => typeof(PhysicsSystem).GetField(
            name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>True when the binding's PhysicsSystem has the fields <see cref="DestroyPhysicsSystem"/> reads.</summary>
        internal static bool PhysicsSystemFieldsFound
            => s_contactListenerField != null && s_activationListenerField != null && s_listenerUserDataField != null;

        // Run with no Update of this system in flight (the caller holds _simLock with _disposed set). Safe on a
        // wrapper that is already disposed: it then does nothing.
        private static void DestroyPhysicsSystem(PhysicsSystem system)
        {
            bool bindingDestroys = s_ownsHandle?.GetValue(system) is true;
            if (bindingDestroys)
            {
                lock (s_systemMapGate)
                    system.Dispose();
                return;
            }
            if (system.IsDisposed || system.Handle == IntPtr.Zero)
                return;

            IntPtr contactListener = s_contactListenerField?.GetValue(system) is nint c ? c : IntPtr.Zero;
            IntPtr activationListener = s_activationListenerField?.GetValue(system) is nint a ? a : IntPtr.Zero;
            IntPtr userData = s_listenerUserDataField?.GetValue(system) is nint u ? u : IntPtr.Zero;

            // The system first: it points at both listeners, so they must outlive it.
            lock (s_systemMapGate)
                JPH_PhysicsSystem_Destroy(system.Handle);
            if (contactListener != IntPtr.Zero)
                JPH_ContactListener_Destroy(contactListener);
            if (activationListener != IntPtr.Zero)
                JPH_BodyActivationListener_Destroy(activationListener);
            if (userData != IntPtr.Zero)
                System.Runtime.InteropServices.GCHandle.FromIntPtr(userData).Free();

            // OwnsHandle is false, so this frees nothing native a second time; it clears the handle and unregisters it.
            system.Dispose();
        }

        // =====================================================================
        // No contact-validate callback.
        //
        // joltc's contact listener converts Jolt's CollideShapeResult for the OnContactValidate callback, and that
        // conversion mallocs both supporting-face arrays (joltc.cpp FromJolt, used by ManagedContactListener::
        // OnContactValidate at 1715c5a). joltc frees them only from joltc bc8a8002a on (upstream PR #76); the native
        // this module ships, and the stock JoltPhysics.Native 1.0.4, both predate it. The physics step collects faces
        // for every body pair it collides, and calls OnContactValidate once per colliding pair per step, so every
        // awake body touching something leaked 12 bytes per face vertex, plus the allocation overhead, every step:
        // about 17 KB per step for a kicked pile of 100 boxes.
        //
        // joltc calls a callback only when its slot in the process-wide procs table is set, and with the slot empty it
        // accepts every contact (AcceptAllContactsForThisBodyPair). JoltPhysicsSharp sets all four slots in
        // PhysicsSystem's static constructor, and its validate callback returns that same answer when no handler is
        // subscribed. The module never subscribes OnContactValidate, so joltc gets a copy of the binding's table with
        // the validate slot empty: the same answer for every contact, with no conversion and no allocation, on any
        // joltc. The copy lives in native memory for the life of the process, since joltc keeps the pointer.
        // =====================================================================
        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_ContactListener_SetProcs(IntPtr procs);

        // JPH_ContactListener_Procs (joltc.h): OnContactValidate, OnContactAdded, OnContactPersisted, OnContactRemoved.
        private const int ContactProcCount = 4;
        private static IntPtr s_contactProcs;

        /// <summary>True once joltc's contact listener has no validate callback (see ClearContactValidateProc).</summary>
        internal static bool ContactValidateProcCleared => s_contactProcs != IntPtr.Zero;

        // Under s_systemMapGate, after a PhysicsSystem exists (so the binding's static constructor has set its table).
        private static unsafe void ClearContactValidateProc()
        {
            if (s_contactProcs != IntPtr.Zero)
                return;
            System.Reflection.FieldInfo? field = typeof(PhysicsSystem).GetField(
                "_contactListener_Procs", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object? table = field?.GetValue(null);
            if (table == null || table.GetType().GetFields(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Length != ContactProcCount)
                return;   // not the table this code knows: leave the binding's in place (ContactValidateProcCleared stays false)

            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(table, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                IntPtr* source = (IntPtr*)pinned.AddrOfPinnedObject();
                for (int i = 0; i < ContactProcCount; i++)
                    if (source[i] == IntPtr.Zero)
                        return;
                IntPtr* copy = (IntPtr*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(ContactProcCount * IntPtr.Size));
                copy[0] = IntPtr.Zero;   // OnContactValidate
                for (int i = 1; i < ContactProcCount; i++)
                    copy[i] = source[i];
                JPH_ContactListener_SetProcs((IntPtr)copy);
                s_contactProcs = (IntPtr)copy;
            }
            finally { pinned.Free(); }
        }

        // The native + managed teardown, run under _simLock (see Dispose). Everything that frees a native
        // Jolt object lives here so it is serialised against Step by the caller's lock.
        private void DisposeNative()
        {
            // The step listener comes off first: it points at this backend, and the system is about to go.
            ReleaseStepProviders();

            // Characters own native CharacterVirtual objects + shapes and hold a ref to _system, so
            // dispose them BEFORE the system teardown below.
            lock (_characterGate)
            {
                foreach (JoltCharacterRecord rec in _characterList)
                {
                    rec.Character?.Dispose();
                    rec.Character = null;
                    rec.StandingShape?.Dispose();
                    rec.StandingShape = null;
                    rec.InnerCapsule?.Dispose();
                    rec.InnerCapsule = null;
                }
                _characterList.Clear();
                _charVsChar?.Dispose();
                _charVsChar = null;
            }

            // Query-filter callback objects.
            foreach (LayerQueryFilter f in _queryFilters.Values)
                f.Dispose();
            _queryFilters.Clear();

            // Our own handle tables first (pure managed bookkeeping).
            _characters.Clear();
            _bodies.Clear();
            // Shapes the callers never released. A body holds its own native reference, so letting go of ours here
            // is the same as ReleaseShape at the last count; left to finalizers they would outlive the system.
            foreach (JoltShapeRecord shapeRec in _shapes.TakeAll())
            {
                shapeRec.NativeShape?.Dispose();
                shapeRec.NativeShape = null;
                shapeRec.InnerShape?.Dispose();
                shapeRec.InnerShape = null;
            }
            _joltToRecord.Clear();
            _activeBodies.Clear();
            while (_activationQueue.TryDequeue(out _)) { }

            // Unsubscribe before teardown so no worker-thread callback fires into a half-disposed
            // backend during the final Update-drain window.
            if (_system != null)
            {
                _system.OnBodyActivated -= HandleBodyActivated;
                _system.OnBodyDeactivated -= HandleBodyDeactivated;
                _system.OnContactAdded -= HandleContactAdded;
                _system.OnContactPersisted -= HandleContactPersisted;
                _system.OnContactRemoved -= HandleContactRemoved;
            }

            // Native teardown order: system -> jobs -> filters -> Foundation.
            // The PhysicsSystem holds the filter interfaces and steps on the job system,
            // so it must go down first. Destroying it also frees the three layer-filter objects natively; the filter
            // wrappers below free nothing native.
            if (_system != null)
                DestroyPhysicsSystem(_system);
            _system = null;

            // Shared job pool is process-wide: disposed by the LAST region out in
            // Dispose()'s s_foundationGate block, NOT per-region here.

            _objectVsBroadPhaseFilter?.Dispose();
            _objectVsBroadPhaseFilter = null;
            _broadPhaseInterface?.Dispose();
            _broadPhaseInterface = null;
            _objectLayerPairFilter?.Dispose();
            _objectLayerPairFilter = null;

            // Foundation.Shutdown() is NOT here anymore - it is process-global + ref-counted in Dispose().
        }

        // =====================================================================
        // Jolt event callbacks. WORKER-THREAD context: enqueue / push only.
        // No allocation, no scene-state access, no mutation of _activeBodies.
        // =====================================================================

        private void HandleBodyActivated(PhysicsSystem system, in BodyID bodyID, ulong bodyUserData)
            => _activationQueue.Enqueue(new ActivationDelta(bodyID.ID, true));

        private void HandleBodyDeactivated(PhysicsSystem system, in BodyID bodyID, ulong bodyUserData)
            => _activationQueue.Enqueue(new ActivationDelta(bodyID.ID, false));

        private void HandleContactAdded(
            PhysicsSystem system, in Body body1, in Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
        {
            CombineMaterials(in body1, in body2, in manifold, ref settings);
            PushContact(in body1, in body2, in manifold, in settings, ContactPhase.Begin);
        }

        private void HandleContactPersisted(
            PhysicsSystem system, in Body body1, in Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
        {
            CombineMaterials(in body1, in body2, in manifold, ref settings);
            PushContact(in body1, in body2, in manifold, in settings, ContactPhase.Persist);
        }

        // WORKER-THREAD context, inside the step (which holds _simLock, so no setter changes a record meanwhile). The
        // friction and restitution of the two touching parts, each a linkset child's own where the body is a compound,
        // combined by [Jolt] FrictionCombine and RestitutionCombine (average by default, which is what PhysX does when no
        // combine mode is set, as in Halcyon's tree; the SL wiki gives no rule for two surfaces). Jolt's own rules, with
        // which it fills `settings`, are the geometric mean for friction and the larger value for restitution. A pair with
        // an avatar's query marker is left as Jolt made it.
        private void CombineMaterials(in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings settings)
        {
            _joltToRecord.TryGetValue(body1.ID.ID, out JoltBodyRecord? ra);
            _joltToRecord.TryGetValue(body2.ID.ID, out JoltBodyRecord? rb);
            if (ra == null || rb == null || ra.IsCharacterMarker || rb.IsCharacterMarker)
                return;
            PartMaterial(ra, manifold.SubShapeID1.Value, out float f1, out float r1);
            PartMaterial(rb, manifold.SubShapeID2.Value, out float f2, out float r2);
            settings.CombinedFriction = Combine(ra.FrictionMin || rb.FrictionMin ? MaterialCombine.Min : _settings.FrictionCombine, f1, f2);
            settings.CombinedRestitution = BounceFromTheSurface(in body1, in body2, ra, rb, in manifold,
                                                                Combine(_settings.RestitutionCombine, r1, r2));
        }

        /// <summary>The value two surfaces' <paramref name="a"/> and <paramref name="b"/> give a contact under <paramref name="rule"/>.</summary>
        internal static float Combine(MaterialCombine rule, float a, float b) => rule switch
        {
            MaterialCombine.GeometricMean => MathF.Sqrt(a * b),
            MaterialCombine.Multiply => a * b,
            MaterialCombine.Min => MathF.Min(a, b),
            MaterialCombine.Max => MathF.Max(a, b),
            _ => (a + b) * 0.5f,
        };

        // The length of one solver step of the current Update (the step's time over its collision steps). Written under
        // _simLock before the Update, read by the contact callbacks inside it.
        private float _collisionStepSeconds;

        /// <summary>The length of one solver step of the last Update (tests).</summary>
        internal float CollisionStepSecondsForTest => Volatile.Read(ref _collisionStepSeconds);

        // Jolt applies restitution in the solver step in which it first finds a closing contact, at the speed the body had
        // at the start of that step, and the body bounces "from its current position rather than from a position where it
        // is touching the other object" (Jolt, ContactConstraintManager.cpp, CalculateNonPenetrationConstraintProperties).
        // A body moving v metres a second is found anywhere from one step's travel above the surface (a speculative
        // contact) to one step's travel into it, so the bounce starts up to v x step from the surface and its height is off
        // by about that much: at 45 Hz a 1 m box dropped 2 m with restitution 0.3 rebounds from 18 percent low to 11 percent
        // high as the drop height changes by 7 cm. Where gravity pulls the two bodies together, this hands Jolt the
        // restitution that gives the bounce the height it would have had from the surface: the body struck the surface at
        // s (its speed now, less or more the gravity over the gap or the depth), left it at restitution x s, and so rises to
        // (restitution x s)^2 / 2g above it; Jolt sends it off from here at e' x its speed and then moves it one step, so e'
        // is solved from that. Bodies that gravity does not pull together (two falling bodies, or no gravity) keep the
        // combined value, as do contacts that close too slowly for Jolt to apply restitution at all.
        private float BounceFromTheSurface(in Body body1, in Body body2, JoltBodyRecord ra, JoltBodyRecord rb, in ContactManifold manifold,
                                           float restitution)
        {
            float dt = _collisionStepSeconds;
            if (restitution <= 0f || dt <= 0f || manifold.PointCount == 0)
                return restitution;
            // The normal points the way body 2 moves out of the contact. Gravity's pull of body 2 toward body 1 along it:
            Vector3 n = manifold.WorldSpaceNormal;
            float g1 = body1.IsDynamic ? ra.GravityFactor : 0f;
            float g2 = body2.IsDynamic ? rb.GravityFactor : 0f;
            float pull = -Vector3.Dot(_settings.Gravity * (g2 - g1), n);
            if (pull <= 1e-3f)
                return restitution;
            float damping = MathF.Max(body1.IsDynamic ? body1.MotionProperties.LinearDamping : 0f,
                                      body2.IsDynamic ? body2.MotionProperties.LinearDamping : 0f);

            Vector3 p1 = manifold.GetWorldSpaceContactPointOn1(0);
            Vector3 p2 = manifold.GetWorldSpaceContactPointOn2(0);
            float closing = -Vector3.Dot(body2.GetPointVelocity(p2) - body1.GetPointVelocity(p1), n);   // this step's gravity included
            float height = -manifold.PenetrationDepth;   // above the surface; below it when negative
            // Jolt applies restitution only to a contact closing faster than MinVelocityForRestitution that will close this step.
            if (closing <= MinVelocityForRestitution || closing * dt <= height)
                return restitution;
            float before = closing - pull * dt;          // the speed Jolt bounces: it takes this step's gravity back off
            if (before <= 0f)
                return restitution;
            // The height above the surface its speed would have taken it from, had it fallen there: the stepped fall loses
            // g dt^2 / 2 of it each step (a step adds dt x the new speed to the position), before x dt / 2 over a fall from rest.
            float fell = before * before / (2f * pull) + height + before * dt / 2f;
            if (fell <= 0f)
                return restitution;
            // It left the surface at restitution x the speed it struck it with, and its damping slows it on the way up
            // (dv/dt = -g - c v rises v0/c - g/c^2 ln(1 + c v0/g)): the bounce's height above the surface.
            float leave = restitution * MathF.Sqrt(2f * pull * fell);
            float rise = damping > 1e-6f
                ? leave / damping - pull / (damping * damping) * MathF.Log(1f + damping * leave / pull)
                : leave * leave / (2f * pull);
            if (rise <= height)
            {
                // Found above where the bounce should reach: no bounce from here is right. Not bouncing lands it on the
                // surface; the least bounce leaves it at this height. Take whichever is nearer the right height.
                return rise < height / 2f ? 0f : 1e-4f;
            }
            // The speed to leave at, found by halving: the highest point grows with it.
            float lo = 0f, hi = MathF.Sqrt(2f * pull * (rise - height)) + pull * dt + 1f;
            for (int i = 0; i < 24; i++)
            {
                float mid = (lo + hi) / 2f;
                if (Apex(height, mid, pull, damping, dt) < rise) lo = mid; else hi = mid;
            }
            float v = (lo + hi) / 2f;
            return MathF.Min(v / before, 2f);
        }

        // The highest point a body reaches leaving the surface region at v from `height` (below the surface when negative),
        // as Jolt steps it: each step moves it by the new speed x dt, its position solver then takes PositionShare of what
        // still lies deeper than the penetration slop off, and the next step takes g dt off the speed and then damps it.
        private float Apex(float height, float v, float pull, float damping, float dt)
        {
            float y = height;
            for (int i = 0; i < 1024 && v > 0f; i++)
            {
                y += v * dt;
                if (y < -_penetrationSlop)
                    y += _positionShare * (-_penetrationSlop - y);
                v = (v - pull * dt) * MathF.Max(0f, 1f - damping * dt);
            }
            return y;
        }

        // Jolt's penetration slop and the share of the penetration beyond it that its position solver removes in one step
        // (Baumgarte per position iteration, over NumPositionSteps iterations): read from the PhysicsSystem at creation.
        private float _penetrationSlop = 0.02f;
        private float _positionShare = 0.36f;

        // The struck part's friction and restitution: a compound child's own (SetBodyPartMaterial), or the body's.
        private void PartMaterial(JoltBodyRecord rec, uint subShapeId, out float friction, out float restitution)
        {
            friction = rec.Friction;
            restitution = rec.Restitution;
            float[]? f = rec.PartFriction;
            float[]? r = rec.PartRestitution;
            if (f == null || r == null || !_shapes.TryGet(rec.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.CompoundChildUserData == null)
                return;
            int bits = shapeRec.CompoundIndexBits;
            uint mask = bits >= 32 ? uint.MaxValue : (1u << bits) - 1u;
            int idx = (int)(subShapeId & mask);
            if (idx < f.Length && idx < r.Length)
            {
                friction = f[idx];
                restitution = r[idx];
            }
        }

        private void HandleContactRemoved(PhysicsSystem system, ref SubShapeIDPair pair)
        {
            // The pair separated (or a body was destroyed): no manifold, so no point/normal/impulse.
            // Still resolve both sides from the reverse map for the collision_end dispatch above. If
            // one body was just removed its record may already be gone -> that side reports Invalid,
            // which is correct (there is nothing left to name).
            _joltToRecord.TryGetValue(pair.Body1ID.ID, out JoltBodyRecord? ra);
            _joltToRecord.TryGetValue(pair.Body2ID.ID, out JoltBodyRecord? rb);
            // End carries the sub-shape pair, so name the struck child on each side (the module ignores the
            // End phase today - OpenSim derives ends from absence - but keep the identity correct for parity).
            _contactListener.Push(BuildContact(ra, rb, default, default, 0f, ContactPhase.End,
                ResolveStruckPart(ra, pair.SubShapeID1), ResolveStruckPart(rb, pair.SubShapeID2)));
        }

        // The STRUCK part's UserData: the compound child hit (ResolveChildUserData), or the body's own
        // UserData for a single-shape body. This is the per-contact link identity (llDetectedLinkNumber).
        private uint ResolveStruckPart(JoltBodyRecord? rec, uint subShapeId)
        {
            uint child = ResolveChildUserData(rec, subShapeId);
            return child != 0 ? child : (rec?.UserData ?? 0u);
        }

        // WORKER-THREAD context. Resolve both sides from the reverse map (never lock a body), apply the
        // Persist gate, estimate the impulse, and push into the ring. No allocation, no scene state.
        private void PushContact(
            in Body body1, in Body body2, in ContactManifold manifold,
            in ContactSettings settings, ContactPhase phase)
        {
            _joltToRecord.TryGetValue(body1.ID.ID, out JoltBodyRecord? ra);
            _joltToRecord.TryGetValue(body2.ID.ID, out JoltBodyRecord? rb);

            // A sensor pair the engine found in this update needs no help from the sweep of fast bodies (SweepFastBodiesForSensors).
            if (Volatile.Read(ref _sensorSweepActive) && (body1.IsSensor || body2.IsSensor))
                _sensorPairsReported[SensorPairKey(body1.ID.ID, body2.ID.ID)] = 0;

            // Stamp both bodies as touching in this step (BodyHadContact), before the Persist gate: Added or
            // Persisted fires for every touching pair in every step while either body is awake. A sensor or an
            // avatar's query marker touches nothing solid.
            if (!body1.IsSensor && !body2.IsSensor && !(ra?.IsCharacterMarker ?? false) && !(rb?.IsCharacterMarker ?? false))
            {
                long step = Volatile.Read(ref _contactStep);
                if (ra != null) Volatile.Write(ref ra.ContactStep, step);
                if (rb != null) Volatile.Write(ref rb.ContactStep, step);
            }

            // Persist gate. Persist fires every step for every touching pair; forward it
            // ONLY when a body in the pair wants contact events (has a collision handler). Begin/End are
            // cheap edge events and are never gated. Empirically Jolt STOPS firing Persist once a body
            // sleeps, so this gate only ever suppresses awake-but-touching pairs (e.g. an avatar
            // standing still). On by design.
            if (phase == ContactPhase.Persist &&
                !((ra?.WantsContactEvents ?? false) || (rb?.WantsContactEvents ?? false)))
                return;

            // The impulse estimate feeds collision sound / damage for a listener. Nobody subscribed
            // on either side -> nobody reads it, so skip the estimator (it ran for every Begin contact before).
            bool listening = (ra?.WantsContactEvents ?? false) || (rb?.WantsContactEvents ?? false);

            // Point on body 1, and the manifold normal. Jolt's WorldSpaceNormal points body1 -> body2,
            // which IS our A->B convention (A = body1) - verified on a box-on-ground drop (normal +Z,
            // ground=A -> box=B). No sign flip.
            Vector3 point = manifold.PointCount > 0 ? manifold.GetWorldSpaceContactPointOn1(0) : default;
            Vector3 normal = manifold.WorldSpaceNormal;

            // How fast they part along the normal where they touch, from the velocities the callback sees: before this
            // step's solve, so an impact reports the speed it struck at.
            float relativeSpeed = manifold.PointCount > 0
                ? Vector3.Dot(body2.GetPointVelocity(manifold.GetWorldSpaceContactPointOn2(0)) - body1.GetPointVelocity(point), normal)
                : 0f;

            // Impulse is a POST-solve quantity but Added/Persisted fire PRE-solve, so we use Jolt's own
            // in-callback estimator - the same helper its collision-sound sample uses. It reads only the
            // two bodies Jolt already handed us (NOT a lock we take) plus the manifold. It allocates no managed
            // memory, but joltc mallocs the per-point impulse array on every call (JPH_EstimateCollisionResponse)
            // and the binding never frees it, so it is freed here with joltc's own JPH_CollisionEstimationResult_FreeMembers
            // (joltc links its C runtime statically: only its own free may release its malloc). Sum the per-point
            // NORMAL impulses -> newton-seconds.
            float impulse = 0f;
            if (listening)
            {
                // Fully qualified: our own namespace is OpenSim.Region.PhysicsModules.Jolt.Backend, which would otherwise shadow
                // the JoltPhysicsSharp.Jolt static helper class.
                JoltPhysicsSharp.Jolt.EstimateCollisionResponse(
                    body1, body2, manifold, out CollisionEstimationResult response,
                    settings.CombinedFriction, settings.CombinedRestitution,
                    MinVelocityForRestitution, Math.Max(1, _settings.VelocityIterations));
                ReadOnlySpan<CollisionEstimationResult.Impulse> impulses = response.Impulses;
                for (int i = 0; i < impulses.Length; i++)
                    impulse += impulses[i].ContactImpulse;
                FreeEstimate(ref response);
            }

            // Name the struck part on each side from the contact sub-shape (child of a linkset, or the body
            // itself) - the per-child collision identity behind llDetectedLinkNumber.
            uint childA = ResolveStruckPart(ra, manifold.SubShapeID1.Value);
            uint childB = ResolveStruckPart(rb, manifold.SubShapeID2.Value);
            _contactListener.Push(BuildContact(ra, rb, point, normal, MathF.Max(0f, impulse), phase, childA, childB, relativeSpeed));
        }

        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern unsafe void JPH_CollisionEstimationResult_FreeMembers(CollisionEstimationResult* result);

        // Free the impulse array joltc allocated for an estimate (nothing when it has none). Counted for the tests.
        private static unsafe void FreeEstimate(ref CollisionEstimationResult result)
        {
            fixed (CollisionEstimationResult* r = &result)
                JPH_CollisionEstimationResult_FreeMembers(r);
            Interlocked.Increment(ref s_estimatesFreed);
        }

        private static long s_estimatesFreed;

        /// <summary>Collision estimates whose native impulse array has been freed, process-wide (tests).</summary>
        internal static long EstimatesFreed => Interlocked.Read(ref s_estimatesFreed);

        private static ContactReport BuildContact(
            JoltBodyRecord? ra, JoltBodyRecord? rb, Vector3 point, Vector3 normal, float impulse, ContactPhase phase,
            uint childUserDataA, uint childUserDataB, float relativeSpeed = 0f)
        {
            return new ContactReport
            {
                BodyA = ra != null ? new BodyId(ra.Handle) : BodyId.Invalid,
                BodyB = rb != null ? new BodyId(rb.Handle) : BodyId.Invalid,
                UserDataA = ra != null ? ra.UserData : 0u,
                UserDataB = rb != null ? rb.UserData : 0u,
                ChildUserDataA = childUserDataA,
                ChildUserDataB = childUserDataB,
                Point = point,
                Normal = normal,
                Impulse = impulse,
                RelativeSpeed = relativeSpeed,
                Phase = phase,
            };
        }

        // =====================================================================
        // Shapes
        // =====================================================================

        public ShapeId CreateBoxShape(Vector3 halfExtents)
        {
            float minHalf = MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z));
            float convexRadius = MathF.Max(0f, MathF.Min(DefaultConvexRadius, minHalf * 0.1f));
            var shape = new BoxShape(halfExtents, convexRadius);
            return RegisterShape(RequireCooked(shape, "CreateBoxShape"));
        }

        public ShapeId CreateSphereShape(float radius)
        {
            return RegisterShape(RequireCooked(new SphereShape(MathF.Max(0.001f, radius)), "CreateSphereShape"));
        }

        public ShapeId CreateCapsuleShape(float halfHeight, float radius)
        {
            return RegisterShape(RequireCooked(
                new CapsuleShape(MathF.Max(0.001f, halfHeight), MathF.Max(0.001f, radius)), "CreateCapsuleShape"));
        }

        public ShapeId CreateCylinderShape(float halfHeight, float radius)
        {
            float hh = MathF.Max(0.001f, halfHeight);
            float r = MathF.Max(0.001f, radius);
            // Jolt's CylinderShape axis is Y (like the capsule); prim orientation is the layer's job.
            // Convex radius must be <= min(radius, halfHeight) or Jolt asserts - clamp like the box path.
            float cr = MathF.Max(0f, MathF.Min(DefaultConvexRadius, MathF.Min(r, hh) * 0.1f));
            var settings = new CylinderShapeSettings(hh, r, cr);
            using var free = new ShapeSettingsScope(settings);
            return RegisterShape(RequireCooked(settings.Create(), "CreateCylinderShape"));
        }

        public ShapeId CreateConvexHullShape(ReadOnlySpan<Vector3> points)
        {
            if (points.Length < 4)
                throw new ArgumentException($"convex hull needs >= 4 points; got {points.Length}.");
            // A non-finite point never reaches the native hull builder.
            for (int i = 0; i < points.Length; i++)
                if (!IsFinite(points[i]))
                    throw new ArgumentException($"CreateConvexHullShape: point {i} is not finite ({points[i]}).");
            var settings = new ConvexHullShapeSettings(points, DefaultConvexRadius);
            using var free = new ShapeSettingsScope(settings);
            // Jolt's hull builder fails (joltc returns nullptr) on too few, coplanar, collinear or coincident
            // points, or a point error above 4x tolerance.
            return RegisterShape(RequireCooked(settings.Create(), "CreateConvexHullShape"));
        }

        public ShapeId CreateMeshShape(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
        {
            // This cooks the mesh it is given and keeps no cache: every caller that wants a shape shared between prims
            // has to hold the ShapeId itself. Cooking is the single most expensive operation here.
            // NOTE: a MeshShape reports Volume 0 (Jolt does not integrate triangle-soup volume), so a
            // DYNAMIC body on a mesh gets the clamped fallback mass - meshes are meant to be static.
            if (indices.Length % 3 != 0)
                throw new ArgumentException($"mesh index count {indices.Length} is not a multiple of 3.");
            // Sanitize's IndexedTriangle::IsDegenerate indexes the vertex list with no bounds
            // check, so an out-of-range index is a native out-of-bounds read. Validate before building settings.
            for (int i = 0; i < vertices.Length; i++)
                if (!IsFinite(vertices[i]))
                    throw new ArgumentException($"CreateMeshShape: vertex {i} is not finite ({vertices[i]}).");
            for (int i = 0; i < indices.Length; i++)
                if ((uint)indices[i] >= (uint)vertices.Length)
                    throw new ArgumentException($"CreateMeshShape: index {i} = {indices[i]} is outside [0, {vertices.Length}).");
            int triCount = indices.Length / 3;
            // Two-sided: each triangle is followed by its reverse (the same corners in the opposite order), whose front face
            // is the original's back face.
            int copies = _settings.TwoSidedMeshes ? 2 : 1;
            var tris = new IndexedTriangle[triCount * copies];
            for (int t = 0; t < triCount; t++)
            {
                tris[t * copies] = new IndexedTriangle(indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2], 0u, 0u);
                if (copies == 2)
                    tris[t * copies + 1] = new IndexedTriangle(indices[t * 3], indices[t * 3 + 2], indices[t * 3 + 1], 0u, 0u);
            }
            var verts = vertices.ToArray();
            var settings = new MeshShapeSettings(verts.AsSpan(), tris.AsSpan());
            using var free = new ShapeSettingsScope(settings);
            // No triangles left after Sanitize -> joltc returns nullptr.
            return RegisterShape(RequireCooked(settings.Create(), "CreateMeshShape"));
        }

        public ShapeId CreateCompoundShape(ReadOnlySpan<CompoundChild> children)
        {
            // Linksets. StaticCompoundShape (not mutable) builds a small internal tree and is markedly
            // faster to query - the right choice for a rigid linkset. Each child's UserData is stored in
            // order so a raycast/contact hit can name WHICH child prim was struck: Jolt encodes the
            // child index in the LOW SubShapeIDBitsRecursive bits of the hit's SubShapeID (verified),
            // which we decode in ResolveChildUserData.
            // Jolt's StaticCompoundShapeSettings.Create() ACCESS-VIOLATES with fewer than 2 sub-shapes.
            // A single-member set must use that member's shape directly, not a degenerate compound - which
            // is exactly what the linkset path does (a compound is only built for root + >=1 child = >=2
            // sub-shapes; a linkset down to one member reverts to the plain single-prim body). Guard here so
            // a stray 1-child call is a clear exception, never a native crash.
            if (children.Length < 2)
                throw new ArgumentException($"StaticCompoundShape requires >= 2 children (got {children.Length}); use the single member's shape directly.");

            var childUserData = new uint[children.Length];
            var settings = new StaticCompoundShapeSettings();
            using var free = new ShapeSettingsScope(settings);
            for (int i = 0; i < children.Length; i++)
            {
                CompoundChild c = children[i];
                if (!_shapes.TryGet(c.Shape.Value, out JoltShapeRecord childRec) || !IsLive(childRec))
                    throw new ArgumentException($"CreateCompoundShape: child {i} ({c.Shape}) is not a live shape.");
                // Create() AddRefs each child, so the child native survives via the compound even after
                // the caller releases the child's handle.
                settings.AddShape(c.Position, c.Orientation, childRec.NativeShape!, c.UserData);
                childUserData[i] = c.UserData;
            }

            // The compound's own index bits: smallest b with (1<<b) >= childCount (0 for a single child).
            int bits = 0;
            while ((1 << bits) < children.Length) bits++;

            var rec = new JoltShapeRecord
            {
                NativeShape = RequireCooked(settings.Create(), "CreateCompoundShape"),
                RefCount = 1,
                IsWrapper = true,
                CompoundChildUserData = childUserData,
                CompoundIndexBits = bits,
            };
            return new ShapeId(_shapes.Add(rec));
        }

        public ShapeId CreateHullCompoundShape(ReadOnlySpan<ShapeId> hulls)
        {
            // Same Jolt limit as CreateCompoundShape: a StaticCompoundShape with fewer than 2 sub-shapes access-violates.
            if (hulls.Length < 2)
                throw new ArgumentException($"a hull compound needs >= 2 hulls (got {hulls.Length}); use the single hull directly.");

            var settings = new StaticCompoundShapeSettings();
            using var free = new ShapeSettingsScope(settings);
            var handles = new IntPtr[hulls.Length];
            for (int i = 0; i < hulls.Length; i++)
            {
                if (!_shapes.TryGet(hulls[i].Value, out JoltShapeRecord hullRec) || !IsLive(hullRec))
                    throw new ArgumentException($"CreateHullCompoundShape: hull {i} ({hulls[i]}) is not a live shape.");
                if (hullRec.NativeShape!.Type != ShapeType.Convex)
                    throw new ArgumentException($"CreateHullCompoundShape: shape {i} ({hulls[i]}) is not a convex hull.");
                // Create() AddRefs each hull, so it lives on in the compound after the caller releases its handle.
                settings.AddShape(Vector3.Zero, Quaternion.Identity, hullRec.NativeShape, 0u);
                handles[i] = hullRec.NativeShape.Handle;
            }

            var rec = new JoltShapeRecord
            {
                NativeShape = RequireCooked(settings.Create(), "CreateHullCompoundShape"),
                RefCount = 1,
                IsWrapper = true,
                HullHandles = handles,   // no CompoundChildUserData: a hit on any hull is a hit on the body
            };
            return new ShapeId(_shapes.Add(rec));
        }

        public ShapeId CreateHeightFieldShape(
            ReadOnlySpan<float> heights, int sampleCountX, int sampleCountY, Vector3 scale)
        {
            // Jolt HeightFieldShape is SQUARE (one sample count) and Y-UP: a sample at grid
            // (col,row) sits at scale * (col, height, row) - the height axis is Jolt's Y and the
            // grid spans X and Z. OpenSim's world is Z-up (gravity -Z), so we HIDE the Jolt quirk
            // inside this method (nothing above IPhysicsBackend knows Jolt exists): cook the
            // Y-up field, then wrap it in a RotatedTranslatedShape and return the WRAPPER's handle,
            // which is already Z-up-correct and self-consistent for any caller/query. See below.
            //
            // Sample-count constraint (checked empirically against JoltPhysicsSharp 2.18.6):
            // the managed HeightFieldShapeSettings has BlockSize and BitsPerSample properties in 2.19.1;
            // this leaves both at the native defaults. joltc is a RELEASE build with Jolt's asserts compiled
            // out, so a bad count does NOT throw - it silently mis-cooks (n>=3 incl. odd/non-PoT all
            // return a non-null shape; only n<3 fails). A 257-sample test showed an odd/prime count
            // reproduces its input faithfully (block divisibility is a NON-constraint), and a var region
            // uses one (N+1)-square field per region (257 for a 256 m region, 513 for a
            // 512 m one). So the terrain feed hands ODD (N+1) counts, and this guard accepts
            // square + >= a sane floor - NOT power-of-two. Non-square is padded to square (edge
            // replication) by the terrain feed above the seam; we still reject it here defensively.
            if (sampleCountX != sampleCountY)
                throw new ArgumentException(
                    $"Jolt HeightFieldShape is square; got {sampleCountX}x{sampleCountY}. " +
                    "Non-square regions must be padded to square (edge replication) before cooking.");
            int n = sampleCountX;
            if (n < MinHeightFieldSampleCount)
                throw new ArgumentException(
                    $"HeightFieldShape sample count must be >= {MinHeightFieldSampleCount}; got {n} " +
                    "(n<3 silently mis-cooks in joltc).");
            if (heights.Length < n * n)
                throw new ArgumentException($"height buffer too small: need {n * n} samples, got {heights.Length}.");

            // The caller's `scale` is in OpenSim Z-up terms: (X spacing, Y spacing, height scale).
            // Jolt wants (X spacing, HEIGHT scale, Z spacing), so swap Y<->Z going in.
            Vector3 joltScale = new Vector3(scale.X, scale.Z, scale.Y);

            // Convention: heights[y*N + x] is the height at grid (x, y), and must land at world
            // (x, y). The RotatedTranslatedShape wrapper (below) maps Jolt grid-row r to world
            // Y = (N-1-r) - a north-south flip - so we ROW-REVERSE going in (input row y -> Jolt
            // row N-1-y) to cancel it. X is untouched (no X mirror). Checked by an
            // asymmetric per-quadrant test. (settings copies into native storage, so this cook-time
            // temp array is fine - once per terrain asset, not per frame.)
            float[] samples = new float[n * n];
            for (int jy = 0; jy < n; jy++)
                heights.Slice((n - 1 - jy) * n, n).CopyTo(samples.AsSpan(jy * n, n));
            Vector3 offset = Vector3.Zero;
            Shape inner;
            // 2.19.x: HeightFieldShapeSettings takes (float* samples, offset, scale, uint sampleCount)
            // (was float[]/int in 2.18.6). Pin the cook-time temp array; Create() copies into native storage.
            unsafe
            {
                fixed (float* pSamples = samples)
                {
                    var hfSettings = new HeightFieldShapeSettings(pSamples, offset, joltScale, (uint)n);
                    try { inner = RequireCooked(hfSettings.Create(), "CreateHeightFieldShape (inner)"); }
                    finally { FreeShapeSettings(hfSettings); }
                }
            }

            try
            {
                // R_x(+90) sends Jolt's +Y (height) to world +Z (up). A proper rotation can't also
                // keep the row axis on +Y (that swap is a reflection), so it lands on -Y; the
                // (N-1)*Yspacing translation lifts the field back into the +Y quadrant. Net: the
                // shape, placed at the origin, occupies X in [0,(N-1)*sx], Y in [0,(N-1)*sy], with
                // height along +Z. The -Y row flip this introduces is cancelled by the row-reverse
                // when building `samples` above, so input (x,y) lands at world (x,y) - no mirror.
                Vector3 posW = new Vector3(0f, (n - 1) * scale.Y, 0f);
                Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

                Shape wrapper;
                var wrapSettings = new RotatedTranslatedShapeSettings(posW, rot, inner);
                using (new ShapeSettingsScope(wrapSettings))
                    wrapper = RequireCooked(wrapSettings.Create(), "CreateHeightFieldShape (Z-up wrapper)");

                // The wrapper OWNS the inner shape (private, not caller-visible): both are disposed
                // together when this handle's RefCount hits 0.
                var rec = new JoltShapeRecord
                {
                    NativeShape = wrapper,
                    InnerShape = inner,
                    RefCount = 1,
                    IsWrapper = true,
                };
                return new ShapeId(_shapes.Add(rec));
            }
            catch
            {
                inner.Dispose();
                throw;
            }
        }

        public ShapeId CreateScaledShape(ShapeId baseShape, Vector3 scale)
        {
            // Wraps a cooked shape in a ScaledShape: cheap, shares the underlying geometry, no re-cook. A mesh prim that is
            // resized takes one of these over the shape cooked for its asset (JoltScene.CookMeshShape). Verified per-type
            // against JoltPhysicsSharp 2.18.6: box/hull/mesh accept ANY scale (incl. mirror/tri-non-uniform);
            // sphere/capsule reject non-uniform (MakeScaleValid uniform-ises to the mean); cylinder allows an axial
            // scale with UNIFORM radial only. We MakeScaleValid so we never cook a distorted/invalid
            // shape; the layer above can pre-check IsValidScale if it wants to degrade differently
            // (e.g. swap a non-uniformly-scaled sphere for an ellipsoid hull) rather than accept the clamp.
            if (!_shapes.TryGet(baseShape.Value, out JoltShapeRecord baseRec) || !IsLive(baseRec))
                throw new ArgumentException($"CreateScaledShape: {baseShape} is not a live shape.");

            Vector3 valid = baseRec.NativeShape!.MakeScaleValid(scale);
            var settings = new ScaledShapeSettings(baseRec.NativeShape, valid);
            using var free = new ShapeSettingsScope(settings);
            var rec = new JoltShapeRecord
            {
                NativeShape = RequireCooked(settings.Create(), "CreateScaledShape"),  // AddRefs the base; base survives via its own handle
                RefCount = 1,
                IsWrapper = true,
                BaseShape = baseShape,
            };
            return new ShapeId(_shapes.Add(rec));
        }

        public bool IsScaleValid(ShapeId shape, Vector3 scale)
        {
            if (!IsFinite(scale) || scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f)
                return false;
            return _shapes.TryGet(shape.Value, out JoltShapeRecord rec) && IsLive(rec) && rec.NativeShape!.IsValidScale(scale);
        }

        public void AddShapeRef(ShapeId shape)
        {
            if (_shapes.TryGet(shape.Value, out JoltShapeRecord rec))
                Interlocked.Increment(ref rec.RefCount);
        }

        public void ReleaseShape(ShapeId shape)
        {
            if (!_shapes.TryGet(shape.Value, out JoltShapeRecord rec))
                return;
            if (Interlocked.Decrement(ref rec.RefCount) <= 0)
            {
                // Last caller reference gone. Dispose our managed Shape wrapper (releases one
                // native ref). Any Body still using the shape holds its OWN native ref, so the
                // native RefTarget survives until that body is destroyed - no premature free.
                // A wrapper also owns its private inner shape (heightfield under the Z-up wrapper),
                // so dispose that too.
                rec.NativeShape?.Dispose();
                rec.NativeShape = null;
                rec.InnerShape?.Dispose();
                rec.InnerShape = null;
                _shapes.Remove(shape.Value);
            }
        }

        // joltc returns nullptr when a cook fails, and JoltPhysicsSharp wraps that in a live
        // Shape whose Handle is 0. Registered, it would reach CreateBody / SetShape / a compound as a null
        // native shape. Every native shape creation goes through this: a failed cook is disposed (safe -
        // NativeObject.Dispose skips the native destroy at Handle 0) and becomes a managed ArgumentException
        // the caller can fall back from.
        private static Shape RequireCooked(Shape? shape, string what)
        {
            if (shape != null && shape.Handle != IntPtr.Zero)
                return shape;
            shape?.Dispose();
            throw new ArgumentException($"{what}: native cook failed (Jolt returned no shape)");
        }

        // A shape record is usable for native work only while its managed wrapper exists AND wraps a real
        // native shape (Handle 0 = a failed cook; see RequireCooked).
        private static bool IsLive(JoltShapeRecord rec) => rec.NativeShape != null && rec.NativeShape.Handle != IntPtr.Zero;

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        // A quaternion is unusable if any component is non-finite or it has (near) zero length -
        // Jolt normalises nothing and a zero rotation divides by zero in every transform built from it.
        private const float MinQuaternionLengthSq = 1e-8f;
        private static bool IsUsable(Quaternion q)
            => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W)
               && q.LengthSquared() >= MinQuaternionLengthSq;

        // Mutator policy: a call carrying a non-finite value is DROPPED and counted, never passed to
        // Jolt (Release Jolt keeps a NaN velocity and it spreads through every contact). Surfaced through
        // GetCapacityStats().RejectedNonFinite. Checked before any lock, so no lock scope changes.
        private void CountRejectedNonFinite() => Interlocked.Increment(ref _rejectedNonFinite);

        // Registers a freshly-created Jolt shape, RefCount = 1 (the creator's reference).
        private ShapeId RegisterShape(Shape shape)
        {
            var rec = new JoltShapeRecord { NativeShape = shape, RefCount = 1 };
            return new ShapeId(_shapes.Add(rec));
        }

        // =====================================================================
        // Bodies
        // =====================================================================

        public BodyId CreateBody(in BodyDesc desc)
        {
            lock (_simLock)
            {
            if (_disposed) return BodyId.Invalid;
            if (_system == null)
                throw new InvalidOperationException("CreateBody before Initialize.");
            // Creator policy: throw, so the caller's existing accept-and-ignore path handles it.
            if (!IsFinite(desc.Position) || !IsUsable(desc.Orientation)
                || !IsFinite(desc.LinearVelocity) || !IsFinite(desc.AngularVelocity))
                throw new ArgumentException(
                    $"CreateBody: non-finite descriptor (position {desc.Position}, orientation {desc.Orientation}, " +
                    $"velocity {desc.LinearVelocity}, angular {desc.AngularVelocity}).");
            if (!_shapes.TryGet(desc.Shape.Value, out JoltShapeRecord shapeRec) || !IsLive(shapeRec))
                throw new ArgumentException($"CreateBody: {desc.Shape} is not a live shape handle.");

            MotionType joltMotion = ToJoltMotion(desc.MotionType);
            bool movable = desc.MotionType != BodyMotionType.Static;

            var objectLayer = new ObjectLayer((uint)desc.Layer);
            var bcs = new BodyCreationSettings(
                shapeRec.NativeShape!, desc.Position, desc.Orientation, joltMotion, objectLayer);
            float mass = 0f;
            try
            {
                bcs.Friction = desc.Friction;
                bcs.Restitution = desc.Restitution;
                bcs.IsSensor = desc.IsSensor;
                bcs.UserData = desc.UserData;
                if (_settings.BodyInternalEdgeRemoval && desc.MotionType == BodyMotionType.Dynamic)
                    bcs.EnhancedInternalEdgeRemoval = true;

                if (movable)
                {
                    // Velocities, damping, gravity factor and CCD only mean anything for a body that
                    // actually moves; a Static body has no MotionProperties to hold them. The motion quality (CCD) is
                    // set on the body once it exists, below.
                    bcs.LinearVelocity = desc.LinearVelocity;
                    bcs.AngularVelocity = desc.AngularVelocity;
                    bcs.LinearDamping = MathF.Max(0f, desc.LinearDamping);
                    bcs.AngularDamping = MathF.Max(0f, desc.AngularDamping);
                    bcs.GravityFactor = desc.GravityFactor;
                    if (_settings.MaxBodyLinearSpeed > 0f)
                        bcs.MaxLinearVelocity = _settings.MaxBodyLinearSpeed;
                    if (_settings.MaxBodyAngularSpeed > 0f)
                        bcs.MaxAngularVelocity = _settings.MaxBodyAngularSpeed;

                    // Let this body flip Dynamic<->Kinematic<->Static later (SetBodyMotionType). A body
                    // created Static deliberately does NOT get this: allocating MotionProperties for
                    // every one of a region's tens of thousands of non-physical prims is exactly the
                    // memory regression the DontActivate rule guards against. A prim that can
                    // go physical must therefore be CREATED movable, not created static and promoted.
                    bcs.AllowDynamicOrKinematic = true;
                }

                if (desc.MotionType == BodyMotionType.Dynamic)
                {
                    // Mass policy (BodyDesc): explicit Mass wins; else shape volume x
                    // Density. We ALWAYS override rather than trust the shape's baked density, because
                    // shapes are shared/refcounted across prims and carry Jolt's default 1000 kg/m^3 -
                    // the per-body Density lives in BodyDesc, not the shape. CalculateInertia keeps the
                    // inertia TENSOR derived from the real geometry, scaled to this mass (verified
                    // exact: asked 42 -> body mass 42.0000).
                    mass = ComputeMass(shapeRec, desc);
                    bcs.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
                    bcs.MassPropertiesOverride = new MassProperties { Mass = mass };
                }

                // The load-bearing line: do NOT wake on insert unless asked. A region
                // rezzing tens of thousands of prims with Activate is a pathological startup stall.
                Activation activation = desc.StartActive ? Activation.Activate : Activation.DontActivate;
                BodyID joltId = _bodyInterface.CreateAndAddBody(bcs, activation);

                // At MaxBodies Jolt hands back the invalid id (0xFFFFFFFF). Recording it would give
                // the caller a "live" handle to nothing. Record nothing, count it, and return Invalid - JoltPrim
                // already treats an invalid body as body-less, and the module logs the count (rate-limited).
                if (joltId.IsInvalid)
                {
                    Interlocked.Increment(ref _bodyCreateFailures);
                    return BodyId.Invalid;
                }

                // The motion quality goes through the body interface, not BodyCreationSettings: under JoltPhysicsSharp
                // 2.19.1, BodyCreationSettings.MotionQuality's setter stores a value that is neither Discrete nor
                // LinearCast whatever it is given (read back with its getter and BodyInterface.GetMotionQuality), so a
                // body created with it is never cast. Jolt reads the quality only as "is it LinearCast", so such a body
                // moved as a Discrete one. BodyInterface.SetMotionQuality stores the value it is given. A WhenFast body
                // starts Discrete; the step casts it when it is fast (UpdateCastBySpeed).
                ContinuousCollision ccd = movable ? desc.Ccd : ContinuousCollision.Off;
                if (movable)
                    _bodyInterface.SetMotionQuality(joltId, ccd == ContinuousCollision.On ? MotionQuality.LinearCast : MotionQuality.Discrete);

                var rec = new JoltBodyRecord
                {
                    NativeBodyId = joltId.ID,
                    Shape = desc.Shape,
                    Layer = desc.Layer,
                    MotionType = desc.MotionType,
                    UserData = desc.UserData,
                    WantsContactEvents = desc.WantsContactEvents,
                    Mass = mass,
                    AllowMotionChange = movable,
                    Friction = desc.Friction,
                    Restitution = desc.Restitution,
                    GravityFactor = desc.GravityFactor,
                    Ccd = ccd,
                    InnerRadius = shapeRec.NativeShape!.InnerRadius,
                };
                uint handle = _bodies.Add(rec);
                rec.Handle = handle;
                _joltToRecord[joltId.ID] = rec;
                if (desc.Layer == PhysicsLayer.Sensor)
                    Interlocked.Increment(ref _sensorBodies);
                return new BodyId(handle);
            }
            finally
            {
                // CreateAndAddBody copies the settings; the managed settings object is ours to free.
                FreeBodyCreationSettings(bcs);
            }
            }   // _simLock
        }

        private static MotionType ToJoltMotion(BodyMotionType t) => t switch
        {
            BodyMotionType.Static => MotionType.Static,
            BodyMotionType.Kinematic => MotionType.Kinematic,
            BodyMotionType.Dynamic => MotionType.Dynamic,
            _ => MotionType.Static,
        };

        // Explicit mass wins; otherwise shape volume x density. Clamped to a small positive so a
        // degenerate (zero-volume) shape can never yield a zero/negative-mass dynamic body, whose
        // inverse mass would be infinite acceleration.
        private static float ComputeMass(JoltShapeRecord shapeRec, in BodyDesc desc)
        {
            if (desc.Mass > 0f)
                return desc.Mass;
            float volume = shapeRec.NativeShape != null ? shapeRec.NativeShape.Volume : 0f;
            float density = desc.Density > 0f ? desc.Density : 1000f;
            return MathF.Max(volume * density, 1e-3f);
        }

        // Body-handle -> live Jolt id. Returns false (idempotent no-op for callers) on a stale/invalid
        // handle, matching RemoveBody's contract.
        private bool TryResolve(BodyId body, out JoltBodyRecord rec, out BodyID jid)
        {
            if (_bodies.TryGet(body.Value, out rec))
            {
                jid = new BodyID(rec.NativeBodyId);
                return true;
            }
            jid = default;
            return false;
        }

        // Force/impulse resolution: only DYNAMIC bodies respond. Static bodies have no MotionProperties
        // (Add* would dereference null natively); kinematic bodies are script/animation-driven and
        // ignore forces. This mirrors SL, where llApplyImpulse et al. only affect physical objects.
        private bool TryResolveDynamic(BodyId body, out BodyID jid)
        {
            if (_bodies.TryGet(body.Value, out JoltBodyRecord rec) && rec.MotionType == BodyMotionType.Dynamic)
            {
                jid = new BodyID(rec.NativeBodyId);
                return true;
            }
            jid = default;
            return false;
        }

        public void RemoveBody(BodyId body)
        {
            lock (_simLock)
            {
            if (_disposed) return;
            if (!_bodies.TryGet(body.Value, out JoltBodyRecord rec))
                return; // stale/invalid handle - idempotent no-op.
            if (rec.IsCharacterMarker)
                return; // an avatar query-marker is owned by its character; RemoveCharacter destroys it.

            var joltId = new BodyID(rec.NativeBodyId);
            DropProvidersOfBody(body.Value);   // a force provider never outlives its body
            _bodyInterface.RemoveAndDestroyBody(joltId);
            _joltToRecord.TryRemove(rec.NativeBodyId, out _);
            _bodies.Remove(body.Value); // bumps the generation so the stale handle fails validation.
            if (_characterList.Count > 0)
                _removedBodyIds[rec.NativeBodyId] = 0;   // an avatar that stood on it finds its ground again in its next step (StepCharacter)
            if (rec.Layer == PhysicsLayer.Sensor)
                Interlocked.Decrement(ref _sensorBodies);
            // _activeBodies is step-thread-owned; if this body happened to be active, the stale
            // id is self-healed at the top of Step (it no longer resolves via _joltToRecord).
            }   // _simLock
        }

        public bool IsBodyValid(BodyId body) => _bodies.IsValid(body.Value);

        public void SetBodyShape(BodyId body, ShapeId shape, bool recomputeMass)
        {
            lock (_simLock)
            {
            if (_disposed) return;
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            if (!_shapes.TryGet(shape.Value, out JoltShapeRecord shapeRec) || !IsLive(shapeRec))
                throw new ArgumentException($"SetBodyShape: {shape} is not a live shape handle.");
            // Do not wake the body just because its shape changed (activation stays the caller's call).
            _bodyInterface.SetShape(jid, shapeRec.NativeShape!, recomputeMass, Activation.DontActivate);
            rec.Shape = shape;
            rec.InnerRadius = shapeRec.NativeShape!.InnerRadius;
            }   // _simLock
        }

        // Map a hit's SubShapeID to the struck child's UserData for a compound (linkset) body. Jolt puts
        // the child index in the LOW CompoundIndexBits of the SubShapeID (root shape peels first, from
        // the low end - so even a mesh child's own sub-bits sit ABOVE these). Non-compound => 0.
        private uint ResolveChildUserData(JoltBodyRecord? bodyRec, uint subShapeId)
        {
            if (bodyRec == null)
                return 0u;
            if (!_shapes.TryGet(bodyRec.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.CompoundChildUserData == null)
                return 0u;
            uint[] list = shapeRec.CompoundChildUserData;
            int bits = shapeRec.CompoundIndexBits;
            uint mask = bits >= 32 ? uint.MaxValue : (1u << bits) - 1u;
            int idx = (int)(subShapeId & mask);
            return (idx >= 0 && idx < list.Length) ? list[idx] : 0u;
        }

        public void SetBodyMotionType(BodyId body, BodyMotionType motionType, bool activate)
        {
            lock (_simLock)
            {
            if (_disposed) return;
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            if (motionType != BodyMotionType.Static && !rec.AllowMotionChange)
                throw new InvalidOperationException(
                    "SetBodyMotionType to a movable type needs a body created Dynamic or Kinematic " +
                    "(a Static body has no MotionProperties to promote). Create it movable up front " +
                    "if it can ever go physical.");
            _bodyInterface.SetMotionType(jid, ToJoltMotion(motionType),
                activate ? Activation.Activate : Activation.DontActivate);
            rec.MotionType = motionType;
            }   // _simLock
        }

        // Phantom and volume detect switched on a live prim. Jolt changes the layer and the sensor flag of a body in
        // place (BodyInterface::SetObjectLayer, SetIsSensor), so the body keeps its id, transform and velocity; pairs
        // the new layer no longer allows end at the next step and report OnContactRemoved.
        public void SetBodyLayer(BodyId body, PhysicsLayer layer)
        {
            lock (_simLock)
            {
                if (_disposed) return;
                if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                    return;
                _bodyInterface.SetObjectLayer(jid, new ObjectLayer((uint)layer));
                if (rec.Layer == PhysicsLayer.Sensor && layer != PhysicsLayer.Sensor)
                    Interlocked.Decrement(ref _sensorBodies);
                else if (rec.Layer != PhysicsLayer.Sensor && layer == PhysicsLayer.Sensor)
                    Interlocked.Increment(ref _sensorBodies);
                rec.Layer = layer;
            }
        }

        public void SetBodySensor(BodyId body, bool isSensor)
        {
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out _, out BodyID jid))
                    _bodyInterface.SetIsSensor(jid, isSensor);
            }
        }

        // Move a body IN PLACE - Jolt's BodyInterface repositions the existing body (velocity, contacts,
        // BodyID all preserved); it does NOT destroy/recreate. This is the real reposition (JoltPhysicsSharp
        // 2.19.1 exposes SetPositionAndRotation - the earlier stub was deferred, not a native gap). A no-op
        // on an inert body just sets its transform; activate:true wakes it (used when a live/vehicle body is
        // repositioned so it keeps stepping). Resolve failure (destroyed body) is a safe no-op.
        public void SetBodyTransform(BodyId body, Vector3 position, Quaternion orientation, bool activate)
        {
            if (!IsFinite(position) || !IsUsable(orientation)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out _, out BodyID jid))
                    _bodyInterface.SetPositionAndRotation(jid, position, orientation,
                        activate ? Activation.Activate : Activation.DontActivate);
            }
        }

        // BodyInterface::MoveKinematic (Body::MoveKinematic): sets the linear and angular velocity that take the body to the
        // pose in `seconds`, and wakes it when that velocity is not zero. What stands on the body is carried by it: an
        // avatar takes the velocity of the body under its feet (CharacterVirtual ground velocity), a dynamic body by
        // contact friction. Only a kinematic body takes it (a static one has no motion properties).
        public void MoveKinematicBody(BodyId body, Vector3 position, Quaternion orientation, float seconds)
        {
            if (!IsFinite(position) || !IsUsable(orientation) || !float.IsFinite(seconds)) { CountRejectedNonFinite(); return; }
            if (seconds <= 0f)
                return;
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid) && rec.MotionType == BodyMotionType.Kinematic)
                    _bodyInterface.MoveKinematic(jid, position, orientation, seconds);
            }
        }

        // BodyInterface::SetPositionRotationAndVelocity: the pose and the velocities at once. The body is activated when a
        // velocity is not zero; with both zero a kinematic body stays where it is put and what rests on it is not flung.
        public void SetBodyPoseAndVelocity(BodyId body, Vector3 position, Quaternion orientation, Vector3 linearVelocity, Vector3 angularVelocity)
        {
            if (!IsFinite(position) || !IsUsable(orientation) || !IsFinite(linearVelocity) || !IsFinite(angularVelocity)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid) && rec.MotionType != BodyMotionType.Static)
                    _bodyInterface.SetPositionRotationAndVelocity(jid, position, orientation, linearVelocity, angularVelocity);
            }
        }

        public void SetBodyLinearVelocity(BodyId body, Vector3 velocity)
        {
            if (!IsFinite(velocity)) { CountRejectedNonFinite(); return; }
            // Jolt's BodyInterface.SetLinearVelocity wakes a sleeping body when the velocity is not near zero, so a
            // script's llSetVelocity moves a sleeping object at once (SelectionAndMoveTests checks it through the
            // prim actor at both step modes).
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out _, out BodyID jid))
                    _bodyInterface.SetLinearVelocity(jid, velocity);
            }
        }

        public void SetBodyAngularVelocity(BodyId body, Vector3 velocity)
        {
            if (!IsFinite(velocity)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out _, out BodyID jid))
                    _bodyInterface.SetAngularVelocity(jid, velocity);
            }
        }

        public void SetBodyMass(BodyId body, float mass)
        {
            if (!float.IsFinite(mass)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
            if (_disposed) return;
            if (mass <= 0f || !TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            rec.Mass = mass;
            if (rec.MotionType != BodyMotionType.Dynamic)
                return; // mass is inert for static/kinematic motion; recorded for a later flip to Dynamic.

            // No BodyInterface.SetMass in 2.19.1. Take the shape's geometry-correct mass properties,
            // scale them to the target mass (keeps the inertia tensor's SHAPE, changes only its
            // magnitude), and push them through a body write-lock.
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                    ApplyMassProperties(lockWrite.Body!, rec, mass);
            }
            finally { bli.UnlockWrite(lockWrite); }
            }   // _simLock
        }

        // Inertia about a locked axis, as a multiple of the unlocked tensor's trace: far above the others, so the principal
        // axes Jolt finds for the tensor include each locked axis, whose inverse is then set to exactly zero.
        private const float LockedInertiaScale = 1e6f;

        // The shape's geometry-correct mass properties scaled to `mass` (keeps the inertia tensor's shape, changes only its
        // magnitude), with the body's rotation locks (SetBodyRotationLocks) applied, pushed into its motion properties. A
        // locked axis is taken out of the tensor (its row and column zeroed) and given an infinite inertia (a zero inverse),
        // so no torque, impulse or contact can turn the body about it, and the free axes keep the inertia of the rest of
        // the tensor. The caller holds the body's write lock.
        private static void ApplyMassProperties(Body b, JoltBodyRecord rec, float mass)
        {
            MassProperties mp = b.Shape.MassProperties;
            mp.ScaleToMass(mass);
            MotionProperties motion = b.MotionProperties;
            byte locks = rec.RotationLocks;
            if (locks == 0)
            {
                motion.SetMassProperties(motion.AllowedDOFs, mp);
                return;
            }
            Matrix4x4 inertia = mp.Inertia;
            float big = LockedInertiaScale * MathF.Max(inertia.M11 + inertia.M22 + inertia.M33, 1e-6f);
            for (int axis = 0; axis < 3; axis++)
            {
                if ((locks & (1 << axis)) == 0)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    SetInertiaElement(ref inertia, axis, k, 0f);
                    SetInertiaElement(ref inertia, k, axis, 0f);
                }
                SetInertiaElement(ref inertia, axis, axis, big);
            }
            mp.Inertia = inertia;
            motion.SetMassProperties(motion.AllowedDOFs, mp);
            Vector3 inverse = motion.InverseInertiaDiagonal;
            float cut = 10f / big;
            if (inverse.X <= cut) inverse.X = 0f;
            if (inverse.Y <= cut) inverse.Y = 0f;
            if (inverse.Z <= cut) inverse.Z = 0f;
            motion.SetInverseInertia(inverse, motion.InertiaRotation);
        }

        private static void SetInertiaElement(ref Matrix4x4 m, int row, int column, float value)
        {
            switch (row * 3 + column)
            {
                case 0: m.M11 = value; break;
                case 1: m.M12 = value; break;
                case 2: m.M13 = value; break;
                case 3: m.M21 = value; break;
                case 4: m.M22 = value; break;
                case 5: m.M23 = value; break;
                case 6: m.M31 = value; break;
                case 7: m.M32 = value; break;
                default: m.M33 = value; break;
            }
        }

        public void SetBodyRotationLocks(BodyId body, bool lockX, bool lockY, bool lockZ)
        {
            byte locks = (byte)((lockX ? 1 : 0) | (lockY ? 2 : 0) | (lockZ ? 4 : 0));
            lock (_simLock)
            {
            if (_disposed) return;
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            if (rec.RotationLocks == locks)
                return;
            rec.RotationLocks = locks;
            if (rec.MotionType != BodyMotionType.Dynamic)
                return;   // recorded; a static body has no motion properties
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                    ApplyMassProperties(lockWrite.Body!, rec, rec.Mass);
            }
            finally { bli.UnlockWrite(lockWrite); }
            // ubODE stops the body's turning when it locks an axis (ODEPrim createAMotor): what it was turning about a
            // locked axis would otherwise carry on.
            if (locks != 0)
                _bodyInterface.SetAngularVelocity(jid, Vector3.Zero);
            }   // _simLock
        }

        // Read the recorded body mass (set at creation to explicit Mass or ComputeMass = Volume x Density,
        // and updated by SetBodyMass). Read-only - does not touch the simulation. Used for A/B mass parity.
        public float GetBodyMass(BodyId body)
        {
            return TryResolve(body, out JoltBodyRecord rec, out _) ? rec.Mass : 0f;
        }

        public bool GetBodyEnhancedEdgeRemoval(BodyId body)
        {
            lock (_simLock)
            {
                if (_disposed || !TryResolve(body, out _, out BodyID jid))
                    return false;
                BodyLockInterface bli = _system!.BodyLockInterface;
                bli.LockRead(jid, out BodyLockRead lockRead);
                try { return lockRead.Succeeded && lockRead.Body!.EnhancedInternalEdgeRemoval; }
                finally { bli.UnlockRead(lockRead); }
            }
        }

        public bool GetCharacterEnhancedEdgeRemoval(CharacterId character)
        {
            lock (_characterGate)
                return _characters.TryGet(character.Value, out JoltCharacterRecord rec) && rec.Character != null && rec.Character.EnhancedInternalEdgeRemoval;
        }

        // Local principal moments of inertia (diagonal), read from the live MotionProperties.
        // Jolt stores the INVERSE diagonal; invert per component (0 stays 0 - a locked/infinite axis).
        public Vector3 GetBodyInertiaDiagonal(BodyId body)
        {
            lock (_simLock)
            {
            if (_disposed) return Vector3.Zero;
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType != BodyMotionType.Dynamic)
                return Vector3.Zero;

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockRead(jid, out BodyLockRead lockRead);
            try
            {
                if (!lockRead.Succeeded)
                    return Vector3.Zero;
                Vector3 inv = lockRead.Body!.MotionProperties.InverseInertiaDiagonal;
                return new Vector3(
                    inv.X > 0f ? 1f / inv.X : 0f,
                    inv.Y > 0f ? 1f / inv.Y : 0f,
                    inv.Z > 0f ? 1f / inv.Z : 0f);
            }
            finally { bli.UnlockRead(lockRead); }
            }   // _simLock
        }

        // The linear damping Jolt applies to the body each collision step, v *= 1 - c dt (MotionProperties).
        public float GetBodyLinearDamping(BodyId body)
        {
            lock (_simLock)
            {
            if (_disposed) return 0f;
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType != BodyMotionType.Dynamic)
                return 0f;

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockRead(jid, out BodyLockRead lockRead);
            try
            {
                return lockRead.Succeeded ? lockRead.Body!.MotionProperties.LinearDamping : 0f;
            }
            finally { bli.UnlockRead(lockRead); }
            }   // _simLock
        }

        // Recompute the dynamic mass from the shape's geometric volume and a PHYSICAL density (kg/m^3),
        // then apply it via the same mass-property scaling path as SetBodyMass. Used so the module can
        // honour SceneObjectPart.Density (x DensityScaleFactor) for BulletSim mass parity.
        public void SetBodyDensity(BodyId body, float physicalDensity)
        {
            if (!float.IsFinite(physicalDensity)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
            if (_disposed) return;
            if (physicalDensity <= 0f || !TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                {
                    Body b = lockWrite.Body!;
                    float mass = MathF.Max(b.Shape.Volume * physicalDensity, 1e-3f);
                    rec.Mass = mass;
                    if (rec.MotionType == BodyMotionType.Dynamic)
                        ApplyMassProperties(b, rec, mass);
                }
            }
            finally { bli.UnlockWrite(lockWrite); }
            }   // _simLock
        }

        public void SetBodyFriction(BodyId body, float friction)
        {
            if (!float.IsFinite(friction)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                {
                    _bodyInterface.SetFriction(jid, friction);
                    rec.Friction = friction;
                    if (rec.PartFriction != null)
                        Array.Fill(rec.PartFriction, friction);
                }
            }
        }

        public void SetBodyRestitution(BodyId body, float restitution)
        {
            if (!float.IsFinite(restitution)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                {
                    _bodyInterface.SetRestitution(jid, restitution);
                    rec.Restitution = restitution;
                    if (rec.PartRestitution != null)
                        Array.Fill(rec.PartRestitution, restitution);
                }
            }
        }

        public void SetBodyFrictionMin(BodyId body, bool min)
        {
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out JoltBodyRecord rec, out BodyID _))
                    rec.FrictionMin = min;
            }
        }

        public void SetBodyPartMaterial(BodyId body, uint partUserData, float friction, float restitution)
        {
            if (!float.IsFinite(friction) || !float.IsFinite(restitution)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                    return;
                if (!_shapes.TryGet(rec.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.CompoundChildUserData == null)
                {
                    // One shape: the part is the body.
                    _bodyInterface.SetFriction(jid, friction);
                    _bodyInterface.SetRestitution(jid, restitution);
                    rec.Friction = friction;
                    rec.Restitution = restitution;
                    return;
                }
                int index = Array.IndexOf(shapeRec.CompoundChildUserData, partUserData);
                if (index < 0)
                    return;
                if (rec.PartFriction == null || rec.PartRestitution == null || rec.PartFriction.Length != shapeRec.CompoundChildUserData.Length)
                {
                    rec.PartFriction = new float[shapeRec.CompoundChildUserData.Length];
                    rec.PartRestitution = new float[shapeRec.CompoundChildUserData.Length];
                    Array.Fill(rec.PartFriction, rec.Friction);
                    Array.Fill(rec.PartRestitution, rec.Restitution);
                }
                rec.PartFriction[index] = friction;
                rec.PartRestitution[index] = restitution;
            }
        }

        public float GetShapeVolume(ShapeId shape)
        {
            lock (_simLock)
            {
                if (_disposed || !_shapes.TryGet(shape.Value, out JoltShapeRecord rec) || !IsLive(rec))
                    return 0f;
                return rec.NativeShape!.Volume;
            }
        }

        public Vector3 GetShapeCenterOfMass(ShapeId shape)
        {
            lock (_simLock)
            {
                if (_disposed || !_shapes.TryGet(shape.Value, out JoltShapeRecord rec) || !IsLive(rec))
                    return Vector3.Zero;
                return rec.NativeShape!.CenterOfMass;
            }
        }

        // joltc's ConvexShape density (ConvexShape::SetDensity / GetDensity), called on the shape's own handle: the binding's
        // managed wrapper of a cooked shape need not be its ConvexShape subclass, so the shape's type is checked instead.
        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void JPH_ConvexShape_SetDensity(IntPtr shape, float density);

        [System.Runtime.InteropServices.DllImport("joltc", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern float JPH_ConvexShape_GetDensity(IntPtr shape);

        public void SetShapeDensity(ShapeId shape, float density)
        {
            if (!float.IsFinite(density)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed || density <= 0f || !_shapes.TryGet(shape.Value, out JoltShapeRecord rec) || !IsLive(rec))
                    return;
                if (rec.HullHandles != null)
                {
                    // A hull compound (a mesh asset's hull list): every hull takes the density.
                    foreach (IntPtr hull in rec.HullHandles)
                        if (JPH_ConvexShape_GetDensity(hull) != density)
                            JPH_ConvexShape_SetDensity(hull, density);
                    return;
                }
                Shape native = rec.NativeShape!;
                if (native.Type != ShapeType.Convex)
                    return;
                if (JPH_ConvexShape_GetDensity(native.Handle) != density)
                    JPH_ConvexShape_SetDensity(native.Handle, density);
            }
        }

        public float GetShapeDensity(ShapeId shape)
        {
            lock (_simLock)
            {
                if (_disposed || !_shapes.TryGet(shape.Value, out JoltShapeRecord rec) || !IsLive(rec))
                    return 0f;
                if (rec.HullHandles != null)
                    return JPH_ConvexShape_GetDensity(rec.HullHandles[0]);
                Shape native = rec.NativeShape!;
                return native.Type == ShapeType.Convex ? JPH_ConvexShape_GetDensity(native.Handle) : 0f;
            }
        }

        public void SetBodyDamping(BodyId body, float linear, float angular)
        {
            if (!float.IsFinite(linear) || !float.IsFinite(angular)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
            if (_disposed) return;
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType == BodyMotionType.Static)
                return; // no MotionProperties on a static body.

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                {
                    MotionProperties motion = lockWrite.Body!.MotionProperties;
                    motion.LinearDamping = MathF.Max(0f, linear);
                    motion.AngularDamping = MathF.Max(0f, angular);
                }
            }
            finally { bli.UnlockWrite(lockWrite); }
            }   // _simLock
        }

        public void SetBodyGravityFactor(BodyId body, float factor)
        {
            if (!float.IsFinite(factor)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                    rec.MotionType == BodyMotionType.Static)
                    return; // static bodies never feel gravity; SetGravityFactor would touch null motion props.
                _bodyInterface.SetGravityFactor(jid, factor);
                rec.GravityFactor = factor;
            }
        }

        // Apply* only act on DYNAMIC bodies (see TryResolveDynamic). All of these auto-activate a
        // sleeping body - AddForce and AddImpulse were both verified to wake it - which matches SL's
        // wake-on-impulse behaviour. AddForce/AddTorque accumulate and are consumed by the next Step;
        // AddImpulse/AddAngularImpulse change velocity instantly (delta v = impulse / mass).
        public void ApplyForce(BodyId body, Vector3 force)
        {
            if (!IsFinite(force)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolveDynamic(body, out BodyID jid))
                    _bodyInterface.AddForce(jid, force);
            }
        }

        public void ApplyTorque(BodyId body, Vector3 torque)
        {
            if (!IsFinite(torque)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolveDynamic(body, out BodyID jid))
                    _bodyInterface.AddTorque(jid, torque);
            }
        }

        public void ApplyImpulse(BodyId body, Vector3 impulse)
        {
            if (!IsFinite(impulse)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolveDynamic(body, out BodyID jid))
                    _bodyInterface.AddImpulse(jid, impulse);
            }
        }

        public void ApplyImpulseAtPoint(BodyId body, Vector3 impulse, Vector3 worldPoint)
        {
            if (!IsFinite(impulse) || !IsFinite(worldPoint)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolveDynamic(body, out BodyID jid))
                    _bodyInterface.AddImpulse(jid, impulse, worldPoint);
            }
        }

        public void ApplyAngularImpulse(BodyId body, Vector3 angularImpulse)
        {
            if (!IsFinite(angularImpulse)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolveDynamic(body, out BodyID jid))
                    _bodyInterface.AddAngularImpulse(jid, angularImpulse);
            }
        }

        public void ApplyBuoyancy(
            BodyId body, float waterHeight, float buoyancy, float linearDrag, float angularDrag)
        {
            if (!float.IsFinite(waterHeight) || !float.IsFinite(buoyancy) || !float.IsFinite(linearDrag) || !float.IsFinite(angularDrag))
                { CountRejectedNonFinite(); return; }
            // A no-op, not an implementation: nothing in the module calls it (the vehicle buoyancy model does its own
            // lift), and Jolt's ApplyBuoyancyImpulse needs a step length and a fluid velocity this signature does not
            // carry, so there is no one mapping to write and nothing to test it against. A call that does nothing cannot
            // push a body the wrong way; a throw would only crash a caller that is not there yet.
        }

        public void ActivateBody(BodyId body)
        {
            lock (_simLock)
            {
                if (_disposed) return;
                // Static bodies are never active; skip so we don't touch a body with no MotionProperties.
                if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid) && rec.MotionType != BodyMotionType.Static)
                    _bodyInterface.ActivateBody(jid);
            }
        }

        public void DeactivateBody(BodyId body)
        {
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out _, out BodyID jid))
                    _bodyInterface.DeactivateBody(jid);
            }
        }

        public void SetBodyContinuousCollision(BodyId body, ContinuousCollision mode)
        {
            lock (_simLock)
            {
                if (_disposed) return;
                if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid) && rec.MotionType != BodyMotionType.Static)
                {
                    rec.Ccd = mode;
                    rec.CastingBySpeed = false;   // WhenFast: Discrete until a step finds the body fast
                    _bodyInterface.SetMotionQuality(jid, mode == ContinuousCollision.On ? MotionQuality.LinearCast : MotionQuality.Discrete);
                }
            }
        }

        // ContinuousCollision.WhenFast, before each update: a body is LinearCast for the update only if its velocity
        // would carry it further in one collision step than Jolt's cast threshold for it, the test Jolt itself makes
        // for a LinearCast body (PhysicsSystem::JobIntegrateVelocity: the step's travel against
        // mLinearCastThreshold x the shape's inner radius); Discrete otherwise. The bodies looked at are the awake ones
        // and those woken since the last step (still in the activation queue); a sleeping body does not move.
        private void UpdateCastBySpeed(float collisionStepSeconds)
        {
            foreach (uint joltId in _activeBodies)
                UpdateCastBySpeed(joltId, collisionStepSeconds);
            if (!_activationQueue.IsEmpty)
                foreach (ActivationDelta delta in _activationQueue)
                    if (delta.Activated)
                        UpdateCastBySpeed(delta.BodyId, collisionStepSeconds);
        }

        private void UpdateCastBySpeed(uint joltId, float collisionStepSeconds)
        {
            if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec) || rec.Ccd != ContinuousCollision.WhenFast)
                return;
            var jid = new BodyID(joltId);
            float threshold = _linearCastThreshold * rec.InnerRadius;
            float travel = _bodyInterface.GetLinearVelocity(jid).Length() * collisionStepSeconds;
            bool cast = travel > threshold;
            if (cast == rec.CastingBySpeed)
                return;
            _bodyInterface.SetMotionQuality(jid, cast ? MotionQuality.LinearCast : MotionQuality.Discrete);
            rec.CastingBySpeed = cast;
        }

        /// <summary>Tests: the body's Jolt motion quality as Jolt holds it (0 Discrete, 1 LinearCast), or -1 for no such
        /// body.</summary>
        internal int BodyMotionQualityForTest(BodyId body)
        {
            lock (_simLock)
                return !_disposed && TryResolve(body, out _, out BodyID jid) ? (int)_bodyInterface.GetMotionQuality(jid) : -1;
        }

        // Allow/forbid sleeping (vehicles forbid it while active - Bullet's DISABLE_DEACTIVATION).
        // Needs a body write-lock: AllowSleeping lives on the Body, not the BodyInterface.
        public void SetBodyAllowSleeping(BodyId body, bool allow)
        {
            lock (_simLock)
            {
            if (_disposed) return;
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType == BodyMotionType.Static)
                return; // static bodies have no MotionProperties and never sleep/wake.

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                    lockWrite.Body!.SetAllowSleeping(allow);
            }
            finally { bli.UnlockWrite(lockWrite); }
            }   // _simLock
        }

        // Toggle the Persist gate for a LIVE body (subscription happens after CreateBody). Begin/End always
        // report; Persist (the ongoing-touch stream that drives the script `collision` event) is forwarded
        // only when a body in the pair wants events. A prim's collision-script subscription flips this.
        public void SetBodyWantsContactEvents(BodyId body, bool wants)
        {
            if (_bodies.TryGet(body.Value, out JoltBodyRecord rec))
                rec.WantsContactEvents = wants;
        }

        // Step counter for BodyHadContact: advanced at the start of every step, stamped onto each body the
        // contact callbacks report during it. Written under _simLock, read from the solver's worker threads.
        private long _contactStep;

        // The engine reports no contact for a body asleep, which has not moved since it last touched what it rests on, so
        // a sleeping body keeps the state it went to sleep with: touching if it touched in the last step it was awake.
        public bool BodyHadContact(BodyId body)
        {
            if (!_bodies.TryGet(body.Value, out JoltBodyRecord rec))
                return false;
            long step = Volatile.Read(ref _contactStep);
            long last = Volatile.Read(ref rec.ContactStep);
            if (step != 0 && !Volatile.Read(ref rec.Awake) && rec.MotionType == BodyMotionType.Dynamic)
                return last != 0 && last >= Volatile.Read(ref rec.SleptStep);
            return step != 0 && last == step;
        }

        public bool IsBodyAwake(BodyId body)
            => _bodies.TryGet(body.Value, out JoltBodyRecord rec) && Volatile.Read(ref rec.Awake);

        public bool TryGetBodyUserData(BodyId body, out uint userData)
        {
            bool found = _bodies.TryGet(body.Value, out JoltBodyRecord rec);
            userData = found ? rec.UserData : 0u;
            return found;
        }

        public bool TryGetBodyState(BodyId body, out BodyState state)
        {
            lock (_simLock)
            {
            if (_disposed || !_bodies.TryGet(body.Value, out JoltBodyRecord rec))
            {
                state = default;
                return false;
            }
            var joltId = new BodyID(rec.NativeBodyId);
            state = new BodyState
            {
                Body = body,
                UserData = rec.UserData,
                Position = _bodyInterface.GetPosition(joltId),
                Orientation = _bodyInterface.GetRotation(joltId),
                LinearVelocity = _bodyInterface.GetLinearVelocity(joltId),
                AngularVelocity = _bodyInterface.GetAngularVelocity(joltId),
                Flags = _bodyInterface.IsActive(joltId) ? BodyStateFlags.Active : BodyStateFlags.None,
            };
            return true;
            }   // _simLock
        }

        // =====================================================================
        // Characters
        // =====================================================================

        public CharacterId CreateCharacter(in CharacterDesc desc)
        {
            // CharacterVirtual, not Character: no rigid body in the solve, stepped OUTSIDE _system.Update
            // (see Step), so the movement layer stays in control and stair-stepping / slope handling /
            // moving-platform support come from the controller rather than being rebuilt on a capsule.
            //
            // Z-up adaptation (Jolt's CharacterVirtual defaults are Y-up): Up = +Z, the capsule is
            // rotated to stand along Z, and SupportingVolume is a plane one radius below the centre so
            // the bottom hemisphere counts as ground. The Y-up ExtendedUpdateSettings are remapped in Step.
            if (_disposed)
                return CharacterId.Invalid;   // a region being torn down: refused, as CreateBody does
            if (_system == null)
                throw new InvalidOperationException("CreateCharacter before Initialize.");
            if (!IsFinite(desc.Position) || !IsUsable(desc.Orientation))
                throw new ArgumentException(
                    $"CreateCharacter: non-finite descriptor (position {desc.Position}, orientation {desc.Orientation}).");
            PhysicsSystem system = _system;

            (Shape wrapper, Shape inner) = BuildStandingCapsule(desc.CapsuleHalfHeight, desc.CapsuleRadius);
            var settings = MakeCharacterSettings(wrapper, desc.CapsuleRadius, desc.MaxSlopeAngle, desc.Mass, desc.PushStrength);
            settings.EnhancedInternalEdgeRemoval = _settings.AvatarInternalEdgeRemoval;

            // _simLock: AddCharacter runs on the LOGIN/TELEPORT thread and adds the query-marker body,
            // which mutates the broadphase Update is walking. Taken outside _characterGate per the
            // lock-order rule. (This is the avatar half of the teleport-crash path.)
            lock (_simLock)
            lock (_characterGate)
            {
                if (_disposed)
                {
                    // Disposed between the check above and the lock: the system is gone, so make nothing against it.
                    wrapper.Dispose();
                    inner.Dispose();
                    return CharacterId.Invalid;
                }
                var character = new CharacterVirtual(settings, desc.Position, desc.Orientation, desc.UserData, system);
                character.MaxSlopeAngle = desc.MaxSlopeAngle;
                character.UserData = desc.UserData;

                var rec = new JoltCharacterRecord
                {
                    Character = character,
                    StandingShape = wrapper,
                    InnerCapsule = inner,
                    UserData = desc.UserData,
                    WantsContactEvents = desc.WantsContactEvents,
                    CapsuleHalfHeight = desc.CapsuleHalfHeight,
                    CapsuleRadius = desc.CapsuleRadius,
                    SupportRadius = SupportRadiusOf(desc.CapsuleRadius),
                    MaxSlopeAngle = desc.MaxSlopeAngle,
                    StepHeight = desc.StepHeight,
                    PushStrength = desc.PushStrength,
                    JumpSpeed = desc.JumpSpeed,
                    PushAllowance = MathF.Max(0f, _settings.AvatarPushMaxSpeed),
                    Mass = desc.Mass,
                    Placed = true,
                };
                uint handle = _characters.Add(rec);
                rec.Handle = handle;

                // Avatar as a QUERY CITIZEN. A kinematic marker body on the inert
                // AvatarQuery layer (collides with NOTHING - no push, no contacts) carries the avatar's
                // shape + UserData so RayCast/Overlap/ShapeCast can find the avatar. It is synced to the
                // character's position each step (in Step, after ExtendedUpdate, before _system.Update).
                // Distinct from the rejected contact inner body: that failure (CollideKinematicVsNonDynamic
                // HANGS, solid presence changes push) was a SIMULATION-collision problem; a query-only
                // marker never enters the solve, so a query sees it regardless of the collision matrix.
                var markerBcs = new BodyCreationSettings(
                    wrapper, desc.Position, desc.Orientation, MotionType.Kinematic,
                    new ObjectLayer((uint)PhysicsLayer.AvatarQuery));
                markerBcs.UserData = desc.UserData;
                BodyID markerId;
                try { markerId = _bodyInterface.CreateAndAddBody(markerBcs, Activation.DontActivate); }
                finally { FreeBodyCreationSettings(markerBcs); }

                // CreateBody's invalid-id policy. At MaxBodies the avatar gets no query
                // marker (queries will not see it; it still walks and collides): count it, record nothing against
                // 0xFFFFFFFF, and leave MarkerBodyId 0 - "none" to Step and RemoveCharacter.
                if (markerId.IsInvalid)
                {
                    Interlocked.Increment(ref _bodyCreateFailures);
                }
                else
                {
                    var markerRec = new JoltBodyRecord
                    {
                        NativeBodyId = markerId.ID,
                        Shape = ShapeId.Invalid,
                        Layer = PhysicsLayer.AvatarQuery,
                        MotionType = BodyMotionType.Kinematic,
                        UserData = desc.UserData,
                        WantsContactEvents = false,
                        IsCharacterMarker = true,
                    };
                    uint markerHandle = _bodies.Add(markerRec);
                    markerRec.Handle = markerHandle;
                    _joltToRecord[markerId.ID] = markerRec;
                    rec.MarkerBodyId = markerId.ID;
                    rec.MarkerRecord = markerRec;
                }

                // Avatar as a COLLISION CITIZEN. Rather than an inner rigid body (which, checked
                // against JoltPhysicsSharp 2.18.6, cannot report kinematic-vs-static/terrain, whose
                // CollideKinematicVsNonDynamic fix HANGS the solver, and which as a solid body perturbs the avatar push behaviour), we
                // forward the CharacterVirtual's OWN contact events. They fire on THIS (step) thread
                // during ExtendedUpdate, cover terrain/static/dynamic/sensor, and - crucially - a
                // standing avatar re-reports its floor contact every step, which is the real thing the
                // Persist gate exists to suppress. Movement is untouched (these are observational).
                // A touching contact is noted and reported after the move (FinishCharacterContacts), when its relative speed
                // is known and a loose object the avatar touches has been dealt with.
                WireCharacter(rec, character);

                _characterList.Add(rec);
                return new CharacterId(handle);
            }
        }

        // The controller's contact events, and its entry in the avatar-vs-avatar registry. Runs under _simLock and
        // _characterGate, for a new controller and for the one that replaces it on a resize.
        private void WireCharacter(JoltCharacterRecord rec, CharacterVirtual character)
        {
            character.OnContactAdded += (CharacterVirtual cv, in BodyID b2, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                => NoteCharacterBodyContact(rec, b2.ID, ss.Value, ToVec(pos), normal, ContactPhase.Begin, ref s);
            character.OnContactPersisted += (CharacterVirtual cv, in BodyID b2, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                => NoteCharacterBodyContact(rec, b2.ID, ss.Value, ToVec(pos), normal, ContactPhase.Persist, ref s);
            character.OnContactRemoved += (CharacterVirtual cv, in BodyID b2, SubShapeID ss)
                => PushCharacterBodyContact(rec, b2.ID, ss.Value, default, default, ContactPhase.End);

            // Avatar-avatar: register in the shared collision so capsules push/block, and report
            // the contact. otherCharacter.UserData gives the other avatar's id directly.
            if (_charVsChar != null)
            {
                _charVsChar.Add(character);
                character.SetCharacterVsCharacterCollision(_charVsChar);
            }
            character.OnCharacterContactAdded += (CharacterVirtual cv, CharacterVirtual other, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                => PushCharacterCharacterContact(rec, other, ToVec(pos), normal, ContactPhase.Begin);
            character.OnCharacterContactPersisted += (CharacterVirtual cv, CharacterVirtual other, SubShapeID ss, in RVector3 pos, in Vector3 normal, ref CharacterContactSettings s)
                => PushCharacterCharacterContact(rec, other, ToVec(pos), normal, ContactPhase.Persist);
        }

        // The supporting plane is one radius below the capsule's centre, and CharacterVirtual takes it only at creation
        // (joltc has no call to change it), so a radius change has to build a new controller. Clamped as the capsule is.
        private static float SupportRadiusOf(float capsuleRadius) => MathF.Max(0.01f, capsuleRadius);

        private static CharacterVirtualSettings MakeCharacterSettings(Shape wrapper, float capsuleRadius, float maxSlopeAngle, float mass, float pushStrength)
            => new CharacterVirtualSettings
            {
                Shape = wrapper,
                Up = Vector3.UnitZ,
                SupportingVolume = new Plane(Vector3.UnitZ, -SupportRadiusOf(capsuleRadius)),
                MaxSlopeAngle = maxSlopeAngle,
                Mass = mass,
                MaxStrength = MathF.Max(0f, pushStrength) * PushStrengthBaseNewtons,
                // NOTE: PredictiveContactDistance / PenetrationRecoverySpeed are left at Jolt's defaults
                // (0.1 / 1.0). Raising PredictiveContactDistance to 0.15 (to reduce the feet dip) was
                // tried and REVERTED: on a cone's incline the early look-ahead lifted the capsule off the ground
                // (visible hover) and fought penetration recovery frame-to-frame (a walking hop/bob). The
                // clean-hold walk (defaults) is "mostly SL-like" with only a MINOR feet-dip on transitions,
                // which is preferable to hover+hop. Do not raise the look-ahead without a slope walk-test.
            };

        private static Vector3 ToVec(in RVector3 d) => new Vector3((float)d.X, (float)d.Y, (float)d.Z);   // 2.19.x renamed Double3 -> RVector3

        // Push an avatar-vs-BODY contact into the ring. Fires on the step thread during ExtendedUpdate.
        // Side A is the avatar (no BodyId - it is not a solver body; UserData carries the avatar id);
        // side B is the touched body, resolved via the reverse map. Persist is gated exactly like body
        // contacts: forwarded only if the avatar or the other body wants events.
        private void PushCharacterBodyContact(JoltCharacterRecord ch, uint otherJoltId, uint otherSubShape, Vector3 point, Vector3 normal, ContactPhase phase,
                                              float relativeSpeed = 0f)
        {
            _joltToRecord.TryGetValue(otherJoltId, out JoltBodyRecord? other);
            bool wants = ch.WantsContactEvents || (other?.WantsContactEvents ?? false);
            if (phase == ContactPhase.Persist && !wants)
                return;
            _contactListener.Push(new ContactReport
            {
                BodyA = BodyId.Invalid,                 // the avatar is not a rigid body
                BodyB = other != null ? new BodyId(other.Handle) : BodyId.Invalid,
                UserDataA = ch.UserData,
                UserDataB = other?.UserData ?? 0u,
                ChildUserDataA = ch.UserData,           // the avatar has no sub-shapes; itself is the struck part
                ChildUserDataB = ResolveStruckPart(other, otherSubShape),   // the linkset child the avatar touched
                Point = point,
                Normal = normal,                        // character-contact normal (points from the character into the body)
                Impulse = 0f,                           // controller-resolved contact; no solver impulse available
                RelativeSpeed = relativeSpeed,
                Phase = phase,
            });
        }

        // A contact of the character's own update with a body, noted for FinishCharacterContacts. A loose object (one the
        // simulation moves, not under the avatar's feet) is left to FinishCharacterContacts: Jolt's own character push does
        // not move it, and it does not move the avatar on first touch. Jolt's push moved a light object faster than the
        // avatar walked and could launch the avatar onto it, and a heavy object thrown at an avatar moved it at the object's
        // speed, with no regard to the push limits. What is under the avatar's feet is left to Jolt, so it stands on it.
        private void NoteCharacterBodyContact(JoltCharacterRecord ch, uint otherJoltId, uint otherSubShape, Vector3 point, Vector3 normal,
                                              ContactPhase phase, ref CharacterContactSettings settings)
        {
            _joltToRecord.TryGetValue(otherJoltId, out JoltBodyRecord? other);
            // What the avatar last stood on is never loose, at any edge it touches: the avatar rides it (adopting its
            // velocity), so pushing it as well would feed back into the avatar's own speed. It rides only ground it can walk
            // on (StepCharacter); a steep face, such as the side of a post a flying avatar flies into, is still loose, else
            // Jolt pushed it with the avatar's whole push force in one step, faster than the avatar moved.
            bool loose = other != null && other.MotionType == BodyMotionType.Dynamic && other.Layer == PhysicsLayer.Dynamic
                         && normal.Z >= -CharacterFeetNormalZ && ch.Character != null
                         && !(ch.Character.GroundBodyId == otherJoltId && ch.Character.GroundState == GroundState.OnGround);
            if (loose)
            {
                // Jolt moves the avatar out of the way of a body at the body's own speed. Not on first touch, before
                // FinishCharacterContacts has slowed the body to the speed the avatar takes from it, unless the body has
                // already struck the avatar and been slowed so, giving it a push (Strike): denied, Jolt's character held
                // the avatar where it was, the push was lost as blocked, and a fast body struck before this update went on
                // into the avatar and out past it. Never for an object on top of the avatar: with the ground below, Jolt's
                // character took the two for opposing walls and could not move at all, so an avatar with a box on its head
                // could not walk out from under it. An avatar the caller has just put in place is set out of the object in
                // that update, as out of a fixed one (StepCharacter): held back a step, it was set out in the next, at
                // once, reported as speed (38 m/s), while Jolt took the object for what it stood on and pushed it aside.
                settings.CanPushCharacter = (phase != ContactPhase.Begin || ch.StruckBy.Contains(otherJoltId) || ch.Placed)
                                            && normal.Z <= CharacterFeetNormalZ;
                settings.CanReceiveImpulses = false;
            }
            ch.Contacts.Add(new CharacterBodyContact(otherJoltId, otherSubShape, point, normal, phase, loose));
        }

        // Whether a loose object (a body the simulation moves, other than what the avatar stands on) lies where the avatar's
        // feet go this step, below its step height: the box the capsule's lower part sweeps, with Jolt's predictive contact
        // distance (0.1 m) ahead. Only for an avatar walking on the ground.
        private bool LooseObjectInStep(JoltCharacterRecord rec, CharacterVirtual ch, Vector3 velocity, float dt)
        {
            Vector3 level = new Vector3(velocity.X, velocity.Y, 0f);
            float speed = level.Length();
            if (rec.Flying || speed < 0.01f || ch.GroundState != GroundState.OnGround)
                return false;
            Vector3 dir = level / speed;
            float reach = speed * dt + 0.1f;
            Vector3 pos = ch.Position;
            float feet = pos.Z - rec.CapsuleHalfHeight - rec.CapsuleRadius;
            float r = rec.CapsuleRadius;
            Vector3 from = new Vector3(pos.X, pos.Y, 0f), to = from + dir * reach;
            var center = new Vector3((from.X + to.X) * 0.5f, (from.Y + to.Y) * 0.5f, feet + 0.05f + rec.StepHeight * 0.5f);
            var half = new Vector3(MathF.Abs(to.X - from.X) * 0.5f + r, MathF.Abs(to.Y - from.Y) * 0.5f + r, rec.StepHeight * 0.5f);
            Span<BodyId> found = stackalloc BodyId[8];
            int n = OverlapBox(center, half, Quaternion.Identity, QueryFilter.Dynamic, found);
            uint ground = ch.GroundBodyId;
            for (int i = 0; i < n; i++)
                if (_bodies.TryGet(found[i].Value, out JoltBodyRecord rb) && rb.NativeBodyId != ground
                    && rb.MotionType == BodyMotionType.Dynamic && rb.Layer == PhysicsLayer.Dynamic)
                    return true;
            return false;
        }

        // A loose object found inside the avatar's capsule is moved back out along the contact normal when it struck the
        // avatar, rests on an avatar that stands on the ground, or is lighter than the avatar (the lighter gives way). Else
        // Jolt would move the avatar out of it on its next update, by all the object had gone in during one step, at
        // whatever speed that took.
        private void MoveOutOfCapsule(JoltCharacterRecord rec, CharacterVirtual ch, BodyID jid, Vector3 point, Vector3 normal, Vector3 lead, bool wake)
        {
            Vector3 pos = ch.Position;
            float z = Math.Clamp(point.Z, pos.Z - rec.CapsuleHalfHeight, pos.Z + rec.CapsuleHalfHeight);
            // Out past the character's padding too: a character that starts its move within its padding of a body is held by
            // it, and an avatar with a box resting on its head could not walk out from under it.
            float clear = rec.CapsuleRadius + ch.CharacterPadding + 0.005f;
            float depth = MathF.Max(0f, clear - Vector3.Distance(point, new Vector3(pos.X, pos.Y, z)));
            Vector3 move = normal * depth + lead;
            if (move.LengthSquared() < 1e-8f)
                return;
            Vector3 at = ToVec(_bodyInterface.GetPosition(jid));
            _bodyInterface.SetPosition(jid, at + move, wake ? Activation.Activate : Activation.DontActivate);
        }

        // An object let fall asleep on the avatar (FinishCharacterContacts) is woken, and falls, once the avatar's update no
        // longer touches it: the avatar moved off, or it was taken away.
        private void WakeWhatNoLongerRests(JoltCharacterRecord rec, List<CharacterBodyContact> contacts)
        {
            rec.Resting.RemoveWhere(id =>
            {
                for (int i = 0; i < contacts.Count; i++)
                    if (contacts[i].BodyJoltId == id)
                        return false;
                if (_joltToRecord.TryGetValue(id, out JoltBodyRecord? gone) && gone.MotionType == BodyMotionType.Dynamic)
                    _bodyInterface.ActivateBody(new BodyID(id));
                return true;
            });
        }

        // A body's velocity at a world point: its centre of mass's, and its turning about it.
        private Vector3 PointVelocity(BodyID jid, Vector3 point)
        {
            Vector3 com = ToVec(_bodyInterface.GetCenterOfMassPosition(jid));
            return _bodyInterface.GetLinearVelocity(jid) + Vector3.Cross(_bodyInterface.GetAngularVelocity(jid), point - com);
        }

        // A loose object coming at the avatar faster than this (m/s) strikes it; slower, it is only touching (the avatar may
        // be pushing it). A resting object's own jitter is far below it.
        private const float StrikeSpeed = 0.1f;

        // A character contact whose normal (from the avatar into what it touches) points down by more than this is under
        // its feet: a surface tilted less than 60 degrees from level (the module's AvatarFeetNormalZ).
        internal const float CharacterFeetNormalZ = 0.5f;

        // After the character's move: each contact it noted is reported with its relative speed, and each loose object it
        // touches is dealt with. The speeds are those before this: the avatar's step velocity, and the body's as the last
        // physics step left it.
        //
        // A loose object coming at the avatar strikes it as two bodies meeting with no bounce (ubODE's avatar contacts have
        // bounce 0, ODEScene's near callback): both go on along the contact normal at their common speed, weighted by mass
        // with the avatar's mass. So the object never goes on into the avatar. The avatar takes its share as a push, held to
        // the push limits ([Jolt] AvatarPushMaxSpeed); an avatar on the ground is not pushed down into it, so an object
        // that lands on it stops there. An avatar moving into a loose object pushes it along, level, with a force of its mass
        // times its speed into the object times [Jolt] AvatarObjectPushPerMomentum (held to AvatarObjectPushMaxForce, and
        // scaled by its PushStrength), and never faster than the avatar moves that way: a light object goes along at the
        // avatar's pace, a heavy one is accelerated by the force less its friction, so a run pushes it harder than a walk.
        private void FinishCharacterContacts(JoltCharacterRecord rec, CharacterVirtual ch, float dt)
        {
            List<CharacterBodyContact> contacts = rec.Contacts;
            rec.LooseAtSide = false;
            rec.StruckBy.Clear();   // read by this update's contacts; the strikes from here on are for the next
            if (rec.Resting.Count > 0)
                WakeWhatNoLongerRests(rec, contacts);
            if (contacts.Count == 0)
                return;
            Vector3 vChar = rec.StepVelocity;
            bool supported = ch.GroundState == GroundState.OnGround;
            float pushScale = MathF.Max(0f, rec.PushStrength);
            for (int i = 0; i < contacts.Count; i++)
            {
                CharacterBodyContact c = contacts[i];
                _joltToRecord.TryGetValue(c.BodyJoltId, out JoltBodyRecord? other);
                var jid = new BodyID(c.BodyJoltId);
                Vector3 vBody = other != null && other.MotionType != BodyMotionType.Static
                    ? PointVelocity(jid, c.Point)
                    : Vector3.Zero;
                float relativeSpeed = Vector3.Dot(vBody - vChar, c.Normal);

                bool held = false, struck = false, letSleep = false;
                Vector3 lead = Vector3.Zero;
                if (c.Loose && other != null && rec.Mass > 0f && other.Mass > 0f)
                {
                    Vector3 n = c.Normal;
                    if (MathF.Abs(n.Z) < CharacterFeetNormalZ)
                        rec.LooseAtSide = true;
                    float a = Vector3.Dot(vChar, n), b = Vector3.Dot(vBody, n);
                    // An avatar on the ground does not give under an object on it or coming down on it.
                    held = supported && n.Z > CharacterFeetNormalZ;
                    if (Strikes(a, b, held))
                    {
                        struck = true;
                        lead = Strike(rec, jid, other, n, a, b, held, supported, dt);
                        // Come to rest on an avatar standing still, it is let fall asleep there: held up each step against
                        // the step's gravity it would sink and be lifted again, never still.
                        letSleep = held && vChar.LengthSquared() < 0.0025f && b > -2f * MathF.Abs(_settings.Gravity.Z) * dt;
                    }
                    else
                    {
                        Vector3 level = new Vector3(n.X, n.Y, 0f);
                        float len = level.Length();
                        if (len > 1e-3f)
                        {
                            level /= len;
                            float ah = Vector3.Dot(vChar, level), bh = Vector3.Dot(vBody, level);
                            // Only an avatar moving into the object pushes it. One moving away (ah below zero) with an object
                            // coming at it from the side, a falling box against its rounded foot, would get a negative force
                            // and so a push into the avatar of the force's size.
                            if (ah > 0f && ah > bh)
                            {
                                // The force is the avatar's mass x its speed into the object x the per-momentum factor, held to the
                                // maximum: running (faster) pushes harder than walking, and a heavier avatar harder than a light one.
                                float pushForce = pushScale * MathF.Min(_settings.AvatarObjectPushMaxForce, rec.Mass * ah * _settings.AvatarObjectPushPerMomentum);
                                float target = MathF.Min(ah, bh + pushForce * dt / other.Mass);
                                _bodyInterface.AddLinearVelocity(jid, level * (target - bh));
                            }
                        }
                    }
                }

                if (c.Loose && other != null && (other.Mass < rec.Mass || held || struck))
                    MoveOutOfCapsule(rec, ch, jid, c.Point, c.Normal, lead, !letSleep);
                if (letSleep)
                {
                    _bodyInterface.DeactivateBody(jid);
                    rec.Resting.Add(c.BodyJoltId);
                }
                PushCharacterBodyContact(rec, c.BodyJoltId, c.SubShape, c.Point, c.Normal, c.Phase, relativeSpeed);
            }
            contacts.Clear();
        }

        // Whether a loose object strikes the avatar: it comes at it faster than StrikeSpeed and faster than the avatar moves
        // that way, or it comes down on an avatar that holds it up. `a` and `b` are the avatar's and the object's speeds
        // along the contact normal (from the avatar into the object).
        private static bool Strikes(float a, float b, bool held) => (b < -StrikeSpeed && a > b) || (held && b < 0f);

        // A loose object striking the avatar, by the rule FinishCharacterContacts describes: both go on along `n` at their
        // common speed by mass (or the object stops on an avatar that holds it up), the avatar's share held to its push
        // allowance. Changes the object's velocity and gives the avatar its push. Returns how far the object is to be held
        // back for this step, since the avatar takes its share only on its next one.
        private Vector3 Strike(JoltCharacterRecord rec, BodyID jid, JoltBodyRecord other, Vector3 n, float a, float b, bool held, bool supported, float dt)
        {
            float common = held ? a : (rec.Mass * a + other.Mass * b) / (rec.Mass + other.Mass);
            if (!held)
            {
                // The avatar's share is held to its push allowance; what it cannot take, the object loses, so it
                // goes on no faster than the avatar does and never into it.
                Vector3 give = n * (common - a);
                if (supported && give.Z < 0f)
                    give.Z = 0f;
                float wanted = give.Length();
                float taken = MathF.Min(wanted, MathF.Max(0f, rec.PushAllowance));
                if (taken > 0f)
                    AddCharacterImpulse(new CharacterId(rec.Handle), give * (taken / wanted));
                if (taken > StrikeSpeed)
                    rec.StruckBy.Add(jid.ID);
                common = a - (wanted > 0f ? taken / wanted : 0f) * (a - common);
            }
            _bodyInterface.AddLinearVelocity(jid, n * (common - b));
            // The avatar takes its share on its next step, so for this one the object is held back by what it
            // would gain on the avatar meanwhile: else it goes that far into it.
            return n * ((a - common) * dt);
        }

        // ---------------------------------------------------------------------
        // Fast bodies and volume-detect prims. Jolt finds a sensor's contact only where a body overlaps it at the start of a
        // collision step: its cast of a fast body ignores sensors, and a sensor is not cast itself. A body that is wholly on
        // one side of a thin detector at one step and wholly on the other at the next is never found, however the heartbeat is
        // divided, and the detector raises nothing. Second Life documents that a detector "triggers collision_start and
        // collision_end events when interpenetrating" and no speed beyond which that stops.
        //
        // So, in an update, when the region has a sensor: each awake dynamic body that would go further than
        // SensorSweepMinTravel is noted before it (its centre of mass and its turn), and afterwards, from where it was to
        // where the update left it, its shape is cast against the sensors. A sensor the path passed through, that the
        // engine did not report in this update, and that the body does not overlap where it ended (the engine finds that one
        // in the next update, as it finds every sensor contact), gets a begin contact, which the scene turns into one
        // collision_start; the next heartbeat has no contact, which is the collision_end. A sensor the path meets on the way
        // to a stop short of it is not passed through: the path cast is the one the update made (to where it ended), not the
        // one it would have made.
        // ---------------------------------------------------------------------

        private int _sensorBodies;                       // bodies on the Sensor layer now
        private bool _sensorSweepActive;                 // volatile reads: the contact callbacks run on the engine's job threads
        private const float SensorSweepMinTravel = 0.1f; // m an update must move a body for it to be swept

        private readonly struct SweptBody
        {
            public readonly uint JoltId;
            public readonly Vector3 From;
            public readonly Quaternion Rotation;
            public SweptBody(uint id, Vector3 from, Quaternion rotation) { JoltId = id; From = from; Rotation = rotation; }
        }

        private readonly List<SweptBody> _sensorSwept = new List<SweptBody>();                                   // step-thread only
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _sensorPairsReported = new System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>();
        private readonly List<ShapeCastResult> _sensorCastHits = new List<ShapeCastResult>();
        private readonly List<CollideShapeResult> _sensorEndOverlaps = new List<CollideShapeResult>();
        private readonly HashSet<uint> _sensorsOverlappedAtEnd = new HashSet<uint>();

        /// <summary>Begin contacts the sweep of fast bodies against sensors has raised that the engine did not (tests).</summary>
        internal long SensorCrossingsRaised => Interlocked.Read(ref _sensorCrossingsRaised);
        private long _sensorCrossingsRaised;

        private static ulong SensorPairKey(uint a, uint b) => a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

        private void BeginSensorSweep(float deltaTime)
        {
            _sensorSwept.Clear();
            _sensorPairsReported.Clear();
            if (Volatile.Read(ref _sensorBodies) <= 0)
            {
                Volatile.Write(ref _sensorSweepActive, false);
                return;
            }
            _sweptBodies.Clear();
            _sweptBodies.UnionWith(_activeBodies);
            if (!_activationQueue.IsEmpty)
                foreach (ActivationDelta delta in _activationQueue)
                    if (delta.Activated)
                        _sweptBodies.Add(delta.BodyId);
            foreach (uint joltId in _sweptBodies)
            {
                if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec) || rec.IsCharacterMarker
                    || rec.MotionType != BodyMotionType.Dynamic || rec.Layer != PhysicsLayer.Dynamic)
                    continue;
                var jid = new BodyID(joltId);
                if (_bodyInterface.GetLinearVelocity(jid).Length() * deltaTime <= SensorSweepMinTravel)
                    continue;
                _sensorSwept.Add(new SweptBody(joltId, ToVec(_bodyInterface.GetCenterOfMassPosition(jid)), _bodyInterface.GetRotation(jid)));
            }
            Volatile.Write(ref _sensorSweepActive, _sensorSwept.Count > 0);
        }

        private void FinishSensorSweep()
        {
            if (!Volatile.Read(ref _sensorSweepActive))
                return;
            Volatile.Write(ref _sensorSweepActive, false);
            for (int i = 0; i < _sensorSwept.Count; i++)
            {
                SweptBody swept = _sensorSwept[i];
                if (!_joltToRecord.TryGetValue(swept.JoltId, out JoltBodyRecord? rec) || !_shapes.TryGet(rec.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                    continue;
                var jid = new BodyID(swept.JoltId);
                Vector3 to = ToVec(_bodyInterface.GetCenterOfMassPosition(jid));
                Vector3 path = to - swept.From;
                if (!(path.Length() > SensorSweepMinTravel))
                    continue;

                Matrix4x4 start = Matrix4x4.CreateFromQuaternion(swept.Rotation);
                start.Translation = swept.From;
                _sensorCastHits.Clear();
                _system!.NarrowPhaseQuery.CastShape(shapeRec.NativeShape, Matrix4x4.Transpose(start), path, DefaultCastSettings(), Vector3.Zero,
                    CollisionCollectorType.AllHit, _sensorCastHits, null, FilterFor(QueryFilter.Sensor), null, null);
                if (_sensorCastHits.Count == 0)
                    continue;

                // The sensors the body overlaps where it is now: the engine reports those in its next update.
                Matrix4x4 end = Matrix4x4.CreateFromQuaternion(_bodyInterface.GetRotation(jid));
                end.Translation = to;
                _sensorEndOverlaps.Clear();
                _sensorsOverlappedAtEnd.Clear();
                _system.NarrowPhaseQuery.CollideShape(shapeRec.NativeShape, Vector3.One, Matrix4x4.Transpose(end), DefaultCollideSettings(), Vector3.Zero,
                    CollisionCollectorType.AllHit, _sensorEndOverlaps, null, FilterFor(QueryFilter.Sensor), null, null);
                for (int k = 0; k < _sensorEndOverlaps.Count; k++)
                    _sensorsOverlappedAtEnd.Add(_sensorEndOverlaps[k].BodyID2.ID);

                for (int h = 0; h < _sensorCastHits.Count; h++)
                {
                    ShapeCastResult hit = _sensorCastHits[h];
                    uint sensorId = hit.BodyID2.ID;
                    if (sensorId == swept.JoltId || _sensorsOverlappedAtEnd.Contains(sensorId)
                        || _sensorPairsReported.ContainsKey(SensorPairKey(swept.JoltId, sensorId))
                        || !_joltToRecord.TryGetValue(sensorId, out JoltBodyRecord? sensorRec) || sensorRec.Layer != PhysicsLayer.Sensor)
                        continue;
                    // One begin for the pair, however many of the body's parts or the detector's the path crossed.
                    _sensorPairsReported[SensorPairKey(swept.JoltId, sensorId)] = 0;
                    Interlocked.Increment(ref _sensorCrossingsRaised);
                    float axis = hit.PenetrationAxis.Length();
                    Vector3 normal = axis > 1e-6f ? -hit.PenetrationAxis / axis : Vector3.UnitZ;
                    Vector3 v = _bodyInterface.GetLinearVelocity(jid);
                    _contactListener.Push(BuildContact(rec, sensorRec, hit.ContactPointOn2, normal, 0f, ContactPhase.Begin,
                        ResolveStruckPart(rec, hit.SubShapeID1.Value), ResolveStruckPart(sensorRec, hit.SubShapeID2.Value), Vector3.Dot(v, -normal)));
                }
            }
            _sensorSwept.Clear();
        }

        // The same for an avatar. Its own update (CharacterVirtual::ExtendedUpdate) reports the sensors it overlaps along the
        // move it can make in a step, and an avatar that goes through a thin detector faster than its capsule is wide in one
        // step (flying at 48 m/s at 11 Hz with one step per heartbeat) is wholly on one side of it at one step and wholly on
        // the other at the next, so the detector raised nothing. After the avatar's move, when the region has a sensor and the
        // avatar went further than SensorSweepMinTravel, its capsule is cast from where it was to where it is against the sensors;
        // a sensor the path crossed, that its update did not report and that it does not overlap where it ended (the next update
        // finds that one), gets a begin contact among the update's, so the detector's collision_start is raised and the next
        // heartbeat's absence is the collision_end, as for a body.
        private void SweepCharacterAgainstSensors(JoltCharacterRecord rec, CharacterVirtual ch, Vector3 from)
        {
            if (Volatile.Read(ref _sensorBodies) <= 0 || rec.StandingShape == null || _system == null)
                return;
            Vector3 to = ch.Position;
            Vector3 path = to - from;
            if (!(path.Length() > SensorSweepMinTravel))
                return;
            Matrix4x4 start = Matrix4x4.CreateFromQuaternion(ch.Rotation);
            start.Translation = from;
            _sensorCastHits.Clear();
            _system.NarrowPhaseQuery.CastShape(rec.StandingShape, Matrix4x4.Transpose(start), path, DefaultCastSettings(), Vector3.Zero,
                CollisionCollectorType.AllHit, _sensorCastHits, null, FilterFor(QueryFilter.Sensor), null, null);
            if (_sensorCastHits.Count == 0)
                return;

            Matrix4x4 end = Matrix4x4.CreateFromQuaternion(ch.Rotation);
            end.Translation = to;
            _sensorEndOverlaps.Clear();
            _sensorsOverlappedAtEnd.Clear();
            _system.NarrowPhaseQuery.CollideShape(rec.StandingShape, Vector3.One, Matrix4x4.Transpose(end), DefaultCollideSettings(), Vector3.Zero,
                CollisionCollectorType.AllHit, _sensorEndOverlaps, null, FilterFor(QueryFilter.Sensor), null, null);
            for (int k = 0; k < _sensorEndOverlaps.Count; k++)
                _sensorsOverlappedAtEnd.Add(_sensorEndOverlaps[k].BodyID2.ID);

            for (int h = 0; h < _sensorCastHits.Count; h++)
            {
                ShapeCastResult hit = _sensorCastHits[h];
                uint sensorId = hit.BodyID2.ID;
                if (_sensorsOverlappedAtEnd.Contains(sensorId) || !(hit.Fraction > 0f)
                    || !_joltToRecord.TryGetValue(sensorId, out JoltBodyRecord? sensorRec) || sensorRec.Layer != PhysicsLayer.Sensor)
                    continue;
                bool reported = false;
                for (int c = 0; c < rec.Contacts.Count && !reported; c++)
                    reported = rec.Contacts[c].BodyJoltId == sensorId;
                if (reported)
                    continue;   // the update found it, or an earlier hit of this cast was one
                float axis = hit.PenetrationAxis.Length();
                Vector3 normal = axis > 1e-6f ? hit.PenetrationAxis / axis : Vector3.UnitZ;
                rec.Contacts.Add(new CharacterBodyContact(sensorId, hit.SubShapeID2.Value, hit.ContactPointOn2, normal, ContactPhase.Begin, false));
            }
        }

        // An avatar meets bodies only in its own update, once per step before the simulation's update
        // (NoteCharacterBodyContact), and Jolt's cast of a fast body does not see it: its query marker is on the AvatarQuery
        // layer, which collides with nothing. So a body fast enough to cross an avatar within one update would go through
        // it, or be found deep inside it and put out on the far side. Before the update, each awake body that would go on
        // more than the avatar's radius past where it first touches an avatar in this update, with nothing nearer in its way,
        // strikes the avatar now: it is moved to that touch and the avatar and it meet by the rule of Strike, and both are
        // told of the contact with the speed they closed at. Slower bodies are left to the avatar's own update, as before.
        private void StrikeAvatarsInTheWay(float deltaTime)
        {
            float thinnest = float.MaxValue;
            for (int i = 0; i < _characterList.Count; i++)
                if (_characterList[i].MarkerBodyId != 0 && _characterList[i].Character != null)
                    thinnest = MathF.Min(thinnest, _characterList[i].CapsuleRadius);
            if (thinnest == float.MaxValue)
                return;
            _sweptBodies.Clear();
            _sweptBodies.UnionWith(_activeBodies);
            if (!_activationQueue.IsEmpty)
                foreach (ActivationDelta delta in _activationQueue)
                    if (delta.Activated)
                        _sweptBodies.Add(delta.BodyId);
            foreach (uint joltId in _sweptBodies)
                StrikeAvatarInTheWay(joltId, deltaTime, thinnest);
        }

        private readonly HashSet<uint> _sweptBodies = new HashSet<uint>();            // step-thread only
        private readonly List<ShapeCastResult> _avatarCastHits = new List<ShapeCastResult>();
        private readonly List<ShapeCastResult> _wayCastHits = new List<ShapeCastResult>();

        private void StrikeAvatarInTheWay(uint joltId, float dt, float thinnest)
        {
            if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec) || rec.IsCharacterMarker
                || rec.MotionType != BodyMotionType.Dynamic || rec.Layer != PhysicsLayer.Dynamic || rec.Mass <= 0f)
                return;
            var jid = new BodyID(joltId);
            Vector3 v = _bodyInterface.GetLinearVelocity(jid);
            float speed = v.Length();
            float travel = speed * dt;
            if (!(travel > thinnest) || !_shapes.TryGet(rec.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                return;
            Vector3 dir = v / speed;
            Vector3 from = ToVec(_bodyInterface.GetPosition(jid));
            Matrix4x4 at = Matrix4x4.CreateFromQuaternion(_bodyInterface.GetRotation(jid));
            at.Translation = from;
            Matrix4x4 cast = Matrix4x4.Transpose(at);   // the convention ShapeCast passes the transform in
            _avatarCastHits.Clear();
            _system!.NarrowPhaseQuery.CastShape(shapeRec.NativeShape, cast, v * dt, DefaultCastSettings(), Vector3.Zero,
                CollisionCollectorType.ClosestHit, _avatarCastHits, null, FilterFor(QueryFilter.Avatar), null, null);
            if (_avatarCastHits.Count == 0)
                return;
            ShapeCastResult hit = _avatarCastHits[0];
            JoltCharacterRecord? ch = null;
            for (int i = 0; i < _characterList.Count; i++)
                if (_characterList[i].MarkerBodyId == hit.BodyID2.ID)
                    ch = _characterList[i];
            float reach = hit.Fraction * travel;
            if (ch?.Character == null || ch.Mass <= 0f || !(travel - reach > ch.CapsuleRadius))
                return;
            // PenetrationAxis points from the cast body into the avatar; the normal from the avatar into the body is its negation.
            float axisLength = hit.PenetrationAxis.Length();
            if (!(axisLength > 1e-6f))
                return;
            Vector3 n = -hit.PenetrationAxis / axisLength;
            Vector3 vChar = ch.StepVelocity;
            float a = Vector3.Dot(vChar, n), b = Vector3.Dot(v, n);
            bool supported = ch.Character.GroundState == GroundState.OnGround;
            bool held = supported && n.Z > CharacterFeetNormalZ;
            if (!Strikes(a, b, held) || SomethingNearer(shapeRec.NativeShape, cast, v * dt, joltId, dir, travel, reach))
                return;

            Vector3 lead = Strike(ch, jid, rec, n, a, b, held, supported, dt);
            _bodyInterface.SetPosition(jid, from + dir * reach + lead, Activation.Activate);
            PushCharacterBodyContact(ch, joltId, hit.SubShapeID1.Value, hit.ContactPointOn2, n, ContactPhase.Begin, b - a);
        }

        // Whether a surface facing the body's motion lies in its way nearer than `reach`: then Jolt meets that first. What
        // the body only slides along (the ground under a ball rolling at an avatar) is not in its way.
        private bool SomethingNearer(Shape shape, Matrix4x4 cast, Vector3 motion, uint self, Vector3 dir, float travel, float reach)
        {
            _wayCastHits.Clear();
            _system!.NarrowPhaseQuery.CastShape(shape, cast, motion, DefaultCastSettings(), Vector3.Zero,
                CollisionCollectorType.AllHit, _wayCastHits, null, FilterFor(QueryFilter.Terrain | QueryFilter.Static | QueryFilter.Dynamic), null, null);
            for (int i = 0; i < _wayCastHits.Count; i++)
            {
                ShapeCastResult r = _wayCastHits[i];
                if (r.BodyID2.ID == self || r.Fraction * travel >= reach)
                    continue;
                float len = r.PenetrationAxis.Length();
                if (len > 1e-6f && Vector3.Dot(r.PenetrationAxis / len, dir) > 0.1f)
                    return true;
            }
            return false;
        }

        // Push an avatar-vs-AVATAR contact. Both sides are avatars (no BodyId); UserData on each.
        private void PushCharacterCharacterContact(JoltCharacterRecord ch, CharacterVirtual other, Vector3 point, Vector3 normal, ContactPhase phase)
        {
            uint otherUserData = other != null ? (uint)other.UserData : 0u;
            if (phase == ContactPhase.Persist && !ch.WantsContactEvents)
                return; // gate on this avatar's flag (the other avatar reports its own side symmetrically)
            // This avatar moves with the velocity of its step; the other with the one it last stepped with.
            float relativeSpeed = other != null ? Vector3.Dot(other.LinearVelocity - ch.StepVelocity, normal) : 0f;
            _contactListener.Push(new ContactReport
            {
                BodyA = BodyId.Invalid,
                BodyB = BodyId.Invalid,
                UserDataA = ch.UserData,
                UserDataB = otherUserData,
                ChildUserDataA = ch.UserData,           // avatars have no sub-shapes; each side is its own part
                ChildUserDataB = otherUserData,
                Point = point,
                Normal = normal,
                Impulse = 0f,
                RelativeSpeed = relativeSpeed,
                Phase = phase,
            });
        }

        // Cook a Z-up standing capsule: Jolt's CapsuleShape axis is Y, so wrap it in a
        // RotatedTranslatedShape rotated Y->Z. Returns (wrapper, inner); the wrapper holds a native ref
        // to the inner, and BOTH are disposed together when the character is removed.
        private static (Shape wrapper, Shape inner) BuildStandingCapsule(float halfHeight, float radius)
        {
            Shape capsule = RequireCooked(new CapsuleShape(MathF.Max(0.01f, halfHeight), MathF.Max(0.01f, radius)), "avatar capsule");
            try
            {
                var rt = new RotatedTranslatedShapeSettings(Vector3.Zero, CapsuleYToZ, capsule);
                using var free = new ShapeSettingsScope(rt);
                return (RequireCooked(rt.Create(), "avatar capsule (Z-up wrapper)"), capsule);
            }
            catch
            {
                capsule.Dispose();
                throw;
            }
        }

        public void RemoveCharacter(CharacterId character)
        {
            // _simLock: logout/teleport-out destroys the marker body (broadphase mutation) off the
            // heartbeat thread. Same ordering rule as AddCharacter.
            lock (_simLock)
            lock (_characterGate)
            {
                if (!_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    return;
                _characterList.Remove(rec);
                if (_charVsChar != null && rec.Character != null)
                    _charVsChar.Remove(rec.Character);

                // Destroy the query marker body first (it native-refs the shared wrapper shape).
                if (rec.MarkerBodyId != 0)
                {
                    _bodyInterface.RemoveAndDestroyBody(new BodyID(rec.MarkerBodyId));
                    _joltToRecord.TryRemove(rec.MarkerBodyId, out _);
                    if (rec.MarkerRecord != null)
                        _bodies.Remove(rec.MarkerRecord.Handle);
                    rec.MarkerBodyId = 0;
                    rec.MarkerRecord = null;
                }

                // Character next (it holds a ref to _system), then the shapes it referenced.
                rec.Character?.Dispose();
                rec.Character = null;
                rec.StandingShape?.Dispose();
                rec.StandingShape = null;
                rec.InnerCapsule?.Dispose();
                rec.InnerCapsule = null;
                _characters.Remove(character.Value);
            }
        }

        public void SetCharacterTransform(CharacterId character, Vector3 position, Quaternion orientation)
        {
            if (!IsFinite(position) || !IsUsable(orientation)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec) && rec.Character != null)
                {
                    rec.Character.Position = position;
                    rec.Character.Rotation = orientation;
                    rec.Placed = true;
                }
            }
        }

        // The avatar's Persist gate (see PushCharacterBodyContact). Read on the step thread under _characterGate.
        public void SetCharacterWantsContactEvents(CharacterId character, bool wants)
        {
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    rec.WantsContactEvents = wants;
            }
        }

        public void ReGroundCharacter(CharacterId character, Vector3 position)
        {
            if (!IsFinite(position)) { CountRejectedNonFinite(); return; }
            // Same gate StepCharacter runs under, so the position + velocity write is atomic against the
            // per-step CharacterVirtual update (no half-applied state, no race). Zeroing LinearVelocity is
            // what stops a just-lifted avatar from carrying its accumulated downward fall speed into the
            // next step (which would sink it back into the surface for a frame).
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec) && rec.Character != null)
                {
                    rec.Character.Position = position;
                    rec.Character.LinearVelocity = Vector3.Zero;
                    rec.MovedVelocity = Vector3.Zero;
                    rec.HoverCarry = 0f;
                    rec.HoverRise = 0f;
                    rec.Placed = true;
                }
            }
        }

        // This takes _simLock as well as _characterGate. CharacterVirtual::SetShape is one of the seven
        // TempAllocator consumers in joltc (joltc.cpp:8223).
        // Step holds _simLock for the whole step but releases _characterGate after phase 1, so with
        // _characterGate alone, for the whole of _system.Update (joltc.cpp:1050, the SAME allocator) this method
        // could acquire _characterGate freely and allocate into that stack behind Update's back. TempAllocator is
        // a LIFO stack with a non-atomic mTop and no locking - "allocations and frees can take place from
        // different threads, but the order is guaranteed though job dependencies" (Jolt/Core/TempAllocator.h:11-13)
        // - and two independent callers have no job dependency, so the free comes back out of order and
        // TempAllocatorImpl::Free aborts (:83-84).
        //
        // The trigger is ordinary: an inter-region teleport creates the character and the scene thread then
        // applies the avatar's size (PhysicsActor.Size in JoltCharacter) while that region's heartbeat is inside
        // Update. The process dies with "TempAllocator: Freeing in the wrong order" on the console.
        //
        // The rule at the top of this file says character ops take _characterGate INSIDE _simLock. Taking both,
        // in that order, is the whole fix - no allocator change, no native change.
        // The maximum penetration depth rule. CharacterVirtual::SetShape draws on the TempAllocator only to test the new
        // shape for penetration, which it does only when inMaxPenetrationDepth is below FLT_MAX (Jolt
        // CharacterVirtual.cpp:1504, v5.4.0); at FLT_MAX it swaps the shape and touches no allocator. That is what
        // lets this call run outside the pool gate (see the pool gate rule) on a joltc whose regions share one
        // allocator. Every SetShape goes through here, with the depth fixed: ShapeAndAllocatorRuleTests fails if
        // another caller appears or the depth changes.
        internal const float CharacterShapeMaxPenetrationDepth = float.MaxValue;

        private static bool SetShapeWithoutScratch(CharacterVirtual character, Shape shape, PhysicsSystem system)
            => character.SetShape(0f, shape, CharacterShapeMaxPenetrationDepth, new ObjectLayer((uint)PhysicsLayer.Avatar), system, null, null);

        public void SetCharacterShape(CharacterId character, float capsuleHalfHeight, float capsuleRadius)
        {
            if (!float.IsFinite(capsuleHalfHeight) || !float.IsFinite(capsuleRadius)) { CountRejectedNonFinite(); return; }
            lock (_simLock)
            lock (_characterGate)
            {
                if (_disposed || _system == null || !_characters.TryGet(character.Value, out JoltCharacterRecord rec) || rec.Character == null)
                    return;

                (Shape wrapper, Shape inner) = BuildStandingCapsule(capsuleHalfHeight, capsuleRadius);
                // Force the swap (maxPenetrationDepth = MaxValue) - callers resize deliberately; we do not
                // want a silent no-op if the new capsule momentarily overlaps the floor.
                bool ok;
                using (EnterOutsideGate("CharacterVirtual::SetShape (joltc.cpp:8223)"))
                    ok = SetShapeWithoutScratch(rec.Character, wrapper, _system);
                if (ok)
                {
                    rec.StandingShape?.Dispose();
                    rec.InnerCapsule?.Dispose();
                    rec.StandingShape = wrapper;
                    rec.InnerCapsule = inner;
                    rec.CapsuleHalfHeight = capsuleHalfHeight;
                    rec.CapsuleRadius = capsuleRadius;
                    // The query marker carries the capsule too: queries find the avatar at its new size.
                    if (rec.MarkerBodyId != 0)
                        _bodyInterface.SetShape(new BodyID(rec.MarkerBodyId), wrapper, false, Activation.DontActivate);
                    if (SupportRadiusOf(capsuleRadius) != rec.SupportRadius)
                        ReplaceCharacterForSupportRadius(rec);
                }
                else
                {
                    wrapper.Dispose();
                    inner.Dispose();
                }
            }
        }

        public void SetCharacterMass(CharacterId character, float mass)
        {
            if (!float.IsFinite(mass) || !(mass > 0f)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    rec.Mass = mass;   // the controller takes it on its next step (StepCharacter)
            }
        }

        // Builds the avatar's controller again from its current state, with the supporting plane for its present radius.
        // The new one is made before the old is let go, so a failure leaves the avatar as it was. Under _simLock and
        // _characterGate.
        private void ReplaceCharacterForSupportRadius(JoltCharacterRecord rec)
        {
            CharacterVirtual old = rec.Character!;
            CharacterVirtualSettings settings = MakeCharacterSettings(rec.StandingShape!, rec.CapsuleRadius, rec.MaxSlopeAngle, rec.Mass, rec.PushStrength);
            settings.EnhancedInternalEdgeRemoval = _settings.AvatarInternalEdgeRemoval;
            var replacement = new CharacterVirtual(settings, old.Position, old.Rotation, rec.UserData, _system!);
            replacement.MaxSlopeAngle = rec.MaxSlopeAngle;
            replacement.UserData = rec.UserData;
            replacement.LinearVelocity = old.LinearVelocity;

            _charVsChar?.Remove(old);
            old.Dispose();
            rec.Character = replacement;
            rec.SupportRadius = SupportRadiusOf(rec.CapsuleRadius);
            WireCharacter(rec, replacement);
        }

        /// <summary>
        /// Test control ONLY. Does what <see cref="SetCharacterShape"/> would do without <c>_simLock</c> -
        /// takes <c>_characterGate</c> and not <c>_simLock</c> - so a test can show the owner check
        /// actually catches an unlocked allocator call. A check that reproduces nothing shows nothing unless
        /// it can be shown to catch something. Never called by the simulator.
        /// </summary>
        public void SetCharacterShapeUnlockedForTest(CharacterId character, float capsuleHalfHeight, float capsuleRadius)
        {
            lock (_characterGate)
            {
                if (_disposed || _system == null || !_characters.TryGet(character.Value, out JoltCharacterRecord rec) || rec.Character == null)
                    return;
                RequireSimLock("CharacterVirtual::SetShape (joltc.cpp:8223)");
                (Shape wrapper, Shape inner) = BuildStandingCapsule(capsuleHalfHeight, capsuleRadius);
                SetShapeWithoutScratch(rec.Character, wrapper, _system);
                wrapper.Dispose();
                inner.Dispose();
            }
        }

        public void SetCharacterMovement(CharacterId character, Vector3 desiredVelocity, bool jump, bool flying)
        {
            if (!IsFinite(desiredVelocity)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                {
                    rec.DesiredVelocity = desiredVelocity;
                    rec.JumpRequested = jump;
                    rec.Flying = flying;
                }
            }
        }

        public void AddCharacterImpulse(CharacterId character, Vector3 velocityChange)
        {
            if (!IsFinite(velocityChange)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
            {
                if (!_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    return;
                // Direction and size, without overflow for a push whose length is beyond a float.
                float largest = MathF.Max(MathF.Abs(velocityChange.X), MathF.Max(MathF.Abs(velocityChange.Y), MathF.Abs(velocityChange.Z)));
                if (!(largest > 0f))
                    return;
                Vector3 scaled = velocityChange / largest;
                float size = scaled.Length() * largest;   // may be infinite: then the whole allowance is spent
                float allowed = MathF.Min(size, rec.PushAllowance);
                if (!(allowed > 0f))
                    return;
                rec.PushAllowance -= allowed;
                rec.PendingPush += Vector3.Normalize(scaled) * allowed;
            }
        }

        public void SetCharacterCarry(CharacterId character, Vector3 velocity)
        {
            if (!IsFinite(velocity)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
            {
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    rec.Carry = new Vector3(velocity.X, velocity.Y, 0f);
            }
        }

        /// <summary>
        /// How fast the velocity an avatar arrived with gives way, on the ground, to the velocity its controls ask for (s). An
        /// avatar that lands from a jump with its key let go stops within a step, so this is short: long enough to cover the
        /// moments before the first control input reaches a region the avatar has just entered.
        /// </summary>
        internal const float ArrivalCarryFadeSeconds = 0.1f;

        // The velocity an avatar arrived with (SetCharacterCarry), taken to the step's walking intent: it stays as it was in the
        // air, and on walkable ground, and while flying, the part of it beyond the intent fades. Returns the horizontal velocity to move with.
        private static Vector3 RelaxArrivalCarry(JoltCharacterRecord rec, Vector3 intent, bool onWalkable, float dt)
        {
            Vector3 rel = rec.Carry - intent;
            if (onWalkable)
                rel *= MathF.Exp(-dt / ArrivalCarryFadeSeconds);
            if (rel.LengthSquared() < 1e-4f)
            {
                rec.Carry = Vector3.Zero;
                return intent;
            }
            rec.Carry = intent + rel;
            return rec.Carry;
        }

        // How fast an avatar's push speed fades: on ground it can walk on (s), and flying. In the air it keeps it.
        internal const float PushFadeOnGroundSeconds = 0.25f;
        internal const float PushFadeFlyingSeconds = 1f;

        // The pushes an avatar received since its last step, folded into the velocity this step moves it with.
        // `push` is the push speed it still carries: horizontal (its vertical speed is the controller's own, which
        // gravity acts on), or all three axes when flying. Pushes may add speed up to AvatarPushMaxSpeed, never past it,
        // and never past the speed the avatar already had: an avatar already faster than that (falling) can be slowed
        // by a push, not sped up. Returns the velocity with the pushes in it.
        private Vector3 ApplyPushes(JoltCharacterRecord rec, Vector3 vel, float dt)
        {
            float cap = _settings.AvatarPushMaxSpeed;
            if (!(cap > 0f))
            {
                rec.Push = Vector3.Zero;
                rec.PendingPush = Vector3.Zero;
                return vel;
            }
            rec.PushAllowance = MathF.Min(cap, rec.PushAllowance + MathF.Max(0f, _settings.AvatarPushRecovery) * dt);

            Vector3 before = rec.Flying ? rec.Push : new Vector3(rec.Push.X, rec.Push.Y, vel.Z);
            Vector3 add = rec.PendingPush;
            rec.PendingPush = Vector3.Zero;
            Vector3 after = before + add;
            float limit = MathF.Max(before.Length(), cap);
            if (after.LengthSquared() > limit * limit)
            {
                // The part of this step's pushes that takes the speed to the limit: |before + t add| = limit.
                float a = add.LengthSquared(), b = 2f * Vector3.Dot(before, add), c = before.LengthSquared() - limit * limit;
                float t = a > 0f ? (-b + MathF.Sqrt(MathF.Max(0f, b * b - 4f * a * c))) / (2f * a) : 0f;
                after = before + add * Math.Clamp(t, 0f, 1f);
            }

            if (rec.Flying)
            {
                rec.Push = after;
                return vel + after;
            }
            rec.Push = new Vector3(after.X, after.Y, 0f);
            return new Vector3(vel.X + after.X, vel.Y + after.Y, after.Z);
        }

        public bool TryGetCharacterState(CharacterId character, out CharacterState state)
        {
            lock (_characterGate)
            {
                if (!_characters.TryGet(character.Value, out JoltCharacterRecord rec) || rec.Character == null)
                {
                    state = default;
                    return false;
                }
                state = BuildCharacterState(rec);
                return true;
            }
        }

        // Snapshot the controller's current kinematic + ground state. Caller holds _characterGate.
        private CharacterState BuildCharacterState(JoltCharacterRecord rec)
        {
            CharacterVirtual ch = rec.Character!;
            GroundState gs = ch.GroundState;
            _joltToRecord.TryGetValue(ch.GroundBodyId, out JoltBodyRecord? groundRec);
            return new CharacterState
            {
                Character = new CharacterId(rec.Handle),
                UserData = rec.UserData,
                Position = ch.Position,
                LinearVelocity = rec.MovedVelocity,
                GroundNormal = ch.GroundNormal,
                GroundBody = groundRec != null ? new BodyId(groundRec.Handle) : BodyId.Invalid,
                GroundIsTerrain = groundRec != null && groundRec.Layer == PhysicsLayer.Terrain,
                IsSupported = ch.IsSupported,
                IsSliding = gs == GroundState.OnSteepGround,
            };
        }

        // Advance one CharacterVirtual. Caller holds _characterGate. Runs BEFORE _system.Update so the
        // controller sees the world at frame start. This is the canonical CharacterVirtual
        // velocity model: keep vertical + integrate gravity, adopt ground velocity to ride moving
        // platforms, jump from solid ground, then collide-and-slide via ExtendedUpdate.
        private void StepCharacter(JoltCharacterRecord rec, float dt)
        {
            CharacterVirtual? ch = rec.Character;
            if (ch == null || _system == null)
                return;

            // The body the avatar stands on was removed since its last step, as a prim's body is when the prim becomes a kinematic
            // one (a fixed body cannot): the character still holds the removed body as its ground and finds no ground velocity
            // in it, so a platform that began to move in this step would take the avatar along only from the next one. Finding
            // the ground again, as after a teleport, takes the new body's velocity at once.
            if (!_removedBodyIds.IsEmpty && ch.GroundBodyId != 0 && _removedBodyIds.ContainsKey(ch.GroundBodyId))
                using (Enter("CharacterVirtual::RefreshContacts (joltc.cpp:8151)"))
                    ch.RefreshContacts(new ObjectLayer((uint)PhysicsLayer.Avatar), _system, null, null);

            // Buoyancy scales gravity as it does on a prim: 1 floats, 0.5 falls at half gravity, above 1 rises. As in ubODE
            // (ODECharacter.MoveCharacter), it does nothing while the avatar flies or hovers. With none set, gz * 1 is gz.
            float gz = _settings.Gravity.Z * (1f - rec.Buoyancy);
            Vector3 desired = rec.DesiredVelocity;
            Vector3 newVel;
            bool falling = false;
            bool hovering = rec.HoverAt != null;
            if (hovering)
                rec.Carry = Vector3.Zero;   // a hover is driven by its controls; the arrival velocity is for a walk or a flight

            // A constant force (an attachment's llSetForce): its acceleration, and the velocity it has given. Up and down while
            // walking or in the air it adds to gravity, which the controller's own vertical velocity carries; across, and in
            // all three axes while flying, its velocity is carried here (ForceCarry). While hovering, the spring takes the
            // vertical part. The velocity fades as a push's does: on walkable ground, and while flying.
            Vector3 force = rec.ForceAcceleration;
            bool forced = force != Vector3.Zero || rec.ForceCarry != Vector3.Zero;
            Vector3 forceStep = Vector3.Zero;   // the acceleration ForceCarry takes this step
            float forceTau = 0f;
            if (forced && !hovering && !rec.Flying && force.Z != 0f)
                gz += force.Z;

            if (hovering)
            {
                // Hover from an attachment: the walk or flight across, the spring up and down; no gravity, no jump.
                newVel = HoverVelocity(rec, ch, desired, dt, forced ? force.Z : 0f);
                if (forced)
                {
                    forceStep = new Vector3(force.X, force.Y, 0f);
                    forceTau = ch.GroundState == GroundState.OnGround ? PushFadeOnGroundSeconds : 0f;
                    rec.ForceCarry.Z = 0f;
                }
            }
            else if (rec.Flying)
            {
                // Flying: full 3D control, ground gravity disabled. The horizontal velocity the avatar arrived with fades to what its
                // controls ask for as it does on the ground, so a flight that crossed a border alone with no key held comes to rest
                // where a flight stops when the key is let go; its climb and descent are its controls' alone.
                newVel = desired;
                if (rec.Carry != Vector3.Zero)
                {
                    Vector3 across = RelaxArrivalCarry(rec, new Vector3(desired.X, desired.Y, 0f), true, dt);
                    newVel = new Vector3(across.X, across.Y, desired.Z);
                }
                if (forced)
                {
                    forceStep = force;
                    forceTau = PushFadeFlyingSeconds;
                }
            }
            else
            {
                ch.UpdateGroundVelocity(); // refresh GroundVelocity from the (possibly moving) ground body
                GroundState gs = ch.GroundState;
                bool onWalkable = gs == GroundState.OnGround;   // OnGround = slope within MaxSlopeAngle
                float vz = ch.LinearVelocity.Z;
                if (forced)
                {
                    // A vertical velocity the force gave while flying is already in the controller's own (the flying step
                    // set the controller's velocity to it), so when the avatar stops flying it is not added again.
                    rec.ForceCarry.Z = 0f;
                    forceStep = new Vector3(force.X, force.Y, 0f);
                    forceTau = onWalkable ? PushFadeOnGroundSeconds : 0f;
                }

                // On walkable ground and not moving up: adopt the ground's vertical velocity (moving
                // platform) rather than the accumulated fall speed.
                if (onWalkable && vz <= 0f)
                    vz = ch.GroundVelocity.Z;

                // Jump only from walkable ground.
                if (rec.JumpRequested && onWalkable)
                    vz = rec.JumpSpeed;

                // Ground hold / friction: a character SUPPORTED on a
                // WALKABLE slope must NOT slide - SL avatars stand still on inclines within MaxSlopeAngle.
                // We hold by NOT accumulating gravity while firmly on walkable ground (and not jumping):
                // with no downward velocity, ExtendedUpdate's collide-and-slide has nothing to redirect
                // down the slope. Gravity resumes the instant the character is airborne (InAir) or on
                // ground too steep to hold (OnSteepGround) - so ledges still drop and over-steep slopes
                // still slide (IsSliding). A frictionless "gravity every frame" lets a no-input avatar
                // creep down a walkable slope; flat-ground tests never show that downslope component.
                // A buoyancy above 1 lifts the avatar off the ground it stands on.
                bool heldByGround = onWalkable && !rec.JumpRequested && gz <= 0f;
                // Gravity off the ground. The move below uses the velocity it sets, so it gets half a step of
                // gravity now (the step's average vertical velocity, which moves the character exactly as constant
                // gravity does over the step) and the other half after the move. A whole step before the move, as
                // before, lost v0 * dt / 2 of a jump's rise: 0.644 m instead of 0.816 at 11 Hz for 4 m/s.
                if (!heldByGround)
                {
                    vz += gz * dt * 0.5f;
                    falling = true;
                }

                // Horizontal = intent, plus the ground's horizontal velocity so we ride a platform that
                // is being pushed sideways. With no input this is zero on static ground - no residual
                // slide velocity carries over frame to frame.
                Vector3 horiz = new Vector3(desired.X, desired.Y, 0f);
                if (rec.Carry != Vector3.Zero)
                    horiz = RelaxArrivalCarry(rec, horiz, onWalkable, dt);
                if (onWalkable)
                    horiz += new Vector3(ch.GroundVelocity.X, ch.GroundVelocity.Y, 0f);

                newVel = new Vector3(horiz.X, horiz.Y, vz);
            }

            // The force's velocity half way through the step, which is how far it moves the avatar over the step.
            if (forced)
                newVel += AdvanceForceCarry(rec.ForceCarry, forceStep, forceTau, dt * 0.5f);

            bool pushed = rec.Push != Vector3.Zero || rec.PendingPush != Vector3.Zero;
            newVel = ApplyPushes(rec, newVel, dt);
            if (forced)
                newVel = CapLength(newVel, ForceSpeedCap);
            if (!rec.Flying && !hovering && !falling && newVel.Z > 0f)
            {
                // Pushed up off the ground: from now it flies as a jump does, half a step of gravity before the move.
                newVel.Z += gz * dt * 0.5f;
                falling = true;
            }
            Vector3 startPos = ch.Position;

            ch.LinearVelocity = newVel;
            rec.StepVelocity = newVel;
            rec.JumpRequested = false;
            rec.Contacts.Clear();

            // Z-up remap of the (Y-up-defaulted) stair/stick settings. Step-up height = the avatar's
            // StepHeight; stick-to-floor pulls straight down so it tracks steps/ramps without floating.
            // No step up onto a loose object in the avatar's way: it pushes it instead. Stepping up onto a light box sent the
            // avatar up and over it at three times its walking speed.
            bool stepUp = !rec.LooseAtSide && !LooseObjectInStep(rec, ch, newVel, dt);
            // Nothing pulls a floating or hovering avatar down to the floor: buoyancy 1 holds it level as it walks off an
            // edge, where sticking would draw it down round the corner, and hover holds its own height.
            bool stick = !hovering && (rec.Flying || gz < 0f);
            var ext = new ExtendedUpdateSettings
            {
                WalkStairsStepUp = new Vector3(0f, 0f, stepUp ? MathF.Max(0f, rec.StepHeight) : 0f),
                StickToFloorStepDown = stick ? new Vector3(0f, 0f, -MathF.Max(0.05f, rec.StepHeight)) : Vector3.Zero,
            };
            // Draws on the TempAllocator: only from Step, inside the pool gate (the pool gate rule).
            // Jolt's character presses what holds it up with its weight (its mass times gravity, each update). A flying
            // avatar has no weight to give: brushing a light object lying on the ground, it pressed it with the 80 kg of a
            // standing one (71 m/s into the ground in one 11 Hz step for 1 kg) and the object was thrown up again. Its mass
            // for meeting objects stays rec.Mass (Strike).
            float characterMass = rec.Flying ? FlyingCharacterMass : rec.Mass;
            if (ch.Mass != characterMass)
                ch.Mass = characterMass;
            using (Enter("CharacterVirtual::ExtendedUpdate (joltc.cpp:8135)"))
                ch.ExtendedUpdate(dt, ext, new ObjectLayer((uint)PhysicsLayer.Avatar), _system, null, null);

            // The second half of the step's gravity, while the character is still in the air after the move (on
            // landing the ground takes over its vertical velocity next step).
            float late = 0f;
            if (falling && ch.GroundState == GroundState.InAir)
            {
                late = gz * dt * 0.5f;
                ch.LinearVelocity += new Vector3(0f, 0f, late);
            }

            // What the avatar reports is how it really moved this step, plus the gravity it gained after the move, not
            // the velocity it was asked to move with: walking into a wall it was asked for its walk speed and moved none
            // of it. ubODE reports its body's velocity, which the wall stops likewise.
            // Jolt's character also moves the avatar out of anything it overlaps, all of it in this one update, and that
            // is not speed. An avatar the caller has just put inside a prim (ScenePresence.StandUp places one with no
            // clearance check) was set out 1.245 m of a 2 m cube in one update and reported 13.7 m/s; it reports no speed
            // for that update. A body Jolt lets push the avatar carries it at the body's speed, and a body that went into
            // it before it might push (a heavy linkset catching a flying avatar up as its push fades) is set out besides:
            // 0.31 m in 1/45 s, reported as 14.05 m/s while the linkset carried it at 8.69. The avatar reports no more
            // than the speed such bodies carry it at.
            Vector3 displacement = ch.Position - startPos;
            Vector3 moved = displacement / dt;
            bool setClear = false;
            if (rec.Placed && displacement.Length() > newVel.Length() * dt + PlacedSetOutSlack)
            {
                moved = Vector3.Zero;
                setClear = SetClear(ch, displacement - newVel * dt);
            }
            else if (CarriedVelocity(rec, newVel) is Vector3 carried && moved.Length() > carried.Length() + CarriedSpeedSlack)
                moved *= carried.Length() / moved.Length();
            rec.MovedVelocity = new Vector3(moved.X, moved.Y, moved.Z + late);
            CharacterStepped?.Invoke(new CharacterStepTrace(rec.UserData, ch.Position - startPos, newVel, rec.MovedVelocity, dt, rec.Placed));
            rec.Placed = false;
            if (hovering && MathF.Abs(displacement.Z / dt - newVel.Z) > 0.01f + 0.1f * MathF.Abs(newVel.Z))
            {
                rec.HoverCarry = 0f;   // held up or down by something: the spring starts again from how it moved
                rec.HoverRise = 0f;
            }

            if (pushed)
                FadePush(rec, ch, newVel, startPos, dt);
            if (forced)
                FinishForceCarry(rec, ch, forceStep, forceTau, newVel, startPos, dt);

            SweepCharacterAgainstSensors(rec, ch, startPos);
            FinishCharacterContacts(rec, ch, dt);

            // Where it stands and what it touches are found again where it was left, after this update's contacts were
            // reported: Jolt's "after a character has teleported" (CharacterVirtual::RefreshContacts). Else the next
            // update began from this one's ground, the face, and an avatar left in the air off it was snapped down to
            // the floor by stick-to-floor (4.75 cm in 1/45 s, 2.14 m/s) instead of falling to it.
            if (setClear)
                using (Enter("CharacterVirtual::RefreshContacts (joltc.cpp:8151)"))
                    ch.RefreshContacts(new ObjectLayer((uint)PhysicsLayer.Avatar), _system, null, null);
        }

        // How much further than its velocity takes it (m) an avatar may move in the update after it was put in place before
        // it counts as set out of something it was put inside: the snap of stick-to-floor and rounding stay well below.
        private const float PlacedSetOutSlack = 0.01f;

        // How far (m) past Jolt's character padding an avatar set out sideways is left from what it was set out of.
        private const float SetOutClearance = 0.01f;

        // Jolt sets an avatar out to its character padding from what it overlapped, where the contact still counts as
        // touching (within Jolt's collision tolerance, 1 mm; CharacterVirtual::UpdateSupportingContact). Set out of the
        // side of an object with its feet a few millimetres above the ground, it then had that side face for its ground:
        // touching only a steep face, Jolt counts it as standing when a move down at 0.6 m/s over the update would be
        // stopped short, and names that face its ground. It hung there, its gravity held off, and took up the object's
        // velocity, so a light object it had been set out of carried it up as it tipped (1.15 m/s). Out sideways, it is
        // left SetOutClearance further on, clear of the face. Returns whether it was moved.
        private static bool SetClear(CharacterVirtual ch, Vector3 setOut)
        {
            var level = new Vector3(setOut.X, setOut.Y, 0f);
            float across = level.Length();
            if (!(across > PlacedSetOutSlack))
                return false;
            ch.Position += level * (SetOutClearance / across);
            return true;
        }

        // How much faster (m/s) than the bodies pushing it carry it an avatar may move before the rest counts as set out.
        private const float CarriedSpeedSlack = 0.01f;

        // The velocity the bodies Jolt let push the avatar in this update carry it at, starting from the velocity it moved
        // with: along each such contact's normal it moves away no slower than a body coming at it. Null when no body pushed
        // it. A body may push it on the terms NoteCharacterBodyContact gives Jolt: a loose object, not under its feet, that
        // touched it before or has struck it.
        private Vector3? CarriedVelocity(JoltCharacterRecord rec, Vector3 velocity)
        {
            Vector3? carried = null;
            List<CharacterBodyContact> contacts = rec.Contacts;
            for (int i = 0; i < contacts.Count; i++)
            {
                CharacterBodyContact c = contacts[i];
                if (!c.Loose || c.Normal.Z > CharacterFeetNormalZ || (c.Phase == ContactPhase.Begin && !rec.StruckBy.Contains(c.BodyJoltId)))
                    continue;
                float b = Vector3.Dot(PointVelocity(new BodyID(c.BodyJoltId), c.Point), c.Normal);
                if (!(b < 0f))
                    continue;   // not coming at it: the avatar is pushing it, or they part
                Vector3 v = carried ?? velocity;
                float a = Vector3.Dot(v, c.Normal);
                carried = a > b ? v + c.Normal * (b - a) : v;
            }
            return carried;
        }

        /// <summary>
        /// Test control ONLY: called on the step thread after each avatar's update, with how far the update moved it and the
        /// velocity it reports for it. Never set by the simulator.
        /// </summary>
        internal Action<CharacterStepTrace>? CharacterStepped;

        /// <summary>The mass (kg) Jolt's character is given while the avatar flies: next to none, so it presses nothing it
        /// touches with its weight (StepCharacter).</summary>
        internal const float FlyingCharacterMass = 0.001f;

        /// <summary>The smallest tau llMoveToTarget acts on is two physics frames, 2/45 s (wiki.secondlife.com/wiki/LlMoveToTarget). A
        /// smaller hover tau acts as this one, as on a prim (JoltPrim.MinTau).</summary>
        internal const float MinHoverTau = 2f / 45f;

        /// <summary>The most vertical speed hover gives an avatar (m/s): ubODE's limit (ODECharacter.MoveCharacter).</summary>
        internal const float MaxHoverSpeed = 50f;

        // The velocity a hovering avatar moves with this step. Across: what it is asked for, walking or flying, plus the
        // ground's velocity on ground it can walk on. Up and down: the critically damped spring the SL wiki describes for
        // llSetHoverHeight, reaching the height over tau seconds (wiki.secondlife.com/wiki/LlSetHoverHeight), the one a prim's
        // hover uses (JoltPrim.StepHover), with tau as its timescale. On the error e from the height,
        //   e'' = -e / tau^2 - 2 e' / tau        so from rest   e(t) = e0 (1 + t / tau) e^(-t / tau)
        // The height follows the ground along the avatar's path, so it rises with the ground as the avatar moves on.
        // A constant force's vertical acceleration a acts on the spring too: e'' = -e / tau^2 - 2 e' / tau + a settles at
        // e = a tau^2, so the spring is solved about the height moved by that much.
        private static Vector3 HoverVelocity(JoltCharacterRecord rec, CharacterVirtual ch, Vector3 desired, float dt, float forceZ)
        {
            Vector3 across = new Vector3(desired.X, desired.Y, 0f);
            ch.UpdateGroundVelocity();
            if (ch.GroundState == GroundState.OnGround)
                across += new Vector3(ch.GroundVelocity.X, ch.GroundVelocity.Y, 0f);

            Vector3 pos = ch.Position;
            Func<float, float, float> heightAt = rec.HoverAt!;
            float now = heightAt(pos.X, pos.Y);
            float rise = (heightAt(pos.X + across.X * dt, pos.Y + across.Y * dt) - now) / dt;
            double w = 1.0 / MathF.Max(rec.HoverTau, MinHoverTau);
            double e0 = pos.Z - now;
            if (forceZ != 0f)
                e0 -= forceZ / (w * w);
            // The error's rate is how the avatar moved less the ground's rise it moved with then: a change of slope under
            // the avatar is followed at once, as its walk is, and is not an error for the spring to take up.
            double v0 = rec.MovedVelocity.Z + rec.HoverCarry - rec.HoverRise;
            double c = v0 + w * e0, x = Math.Exp(-w * dt);
            double e1 = (e0 + c * dt) * x, v1 = (v0 - w * c * dt) * x;
            double move = (e1 - e0) / dt;
            float vz = rise + (float)move;
            rec.HoverCarry = (float)(v1 - move);
            rec.HoverRise = rise;
            if (MathF.Abs(vz) > MaxHoverSpeed)
            {
                vz = MathF.CopySign(MaxHoverSpeed, vz);
                rec.HoverCarry = 0f;
            }
            return new Vector3(across.X, across.Y, vz);
        }

        public void SetCharacterBuoyancy(CharacterId character, float buoyancy)
        {
            if (!float.IsFinite(buoyancy)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    rec.Buoyancy = buoyancy;
        }

        public void SetCharacterHover(CharacterId character, Func<float, float, float>? heightAt, float tau)
        {
            if (!float.IsFinite(tau)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
            {
                if (!_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    return;
                if (rec.HoverAt == null || heightAt == null)
                {
                    rec.HoverCarry = 0f;
                    rec.HoverRise = 0f;
                }
                rec.HoverAt = heightAt;
                rec.HoverTau = tau;
            }
        }

        public void SetCharacterConstantForce(CharacterId character, Vector3 acceleration)
        {
            if (!IsFinite(acceleration)) { CountRejectedNonFinite(); return; }
            lock (_characterGate)
                if (_characters.TryGet(character.Value, out JoltCharacterRecord rec))
                    rec.ForceAcceleration = acceleration;
        }

        // The velocity a constant force has given, h seconds on: v' = a - v / tau, solved exactly, so it is the same at any
        // step rate. With no fade (tau 0) it is v + a h; with one it settles at a x tau.
        private static Vector3 AdvanceForceCarry(Vector3 v, Vector3 a, float tau, float h)
        {
            if (!(tau > 0f))
                return v + a * h;
            Vector3 steady = a * tau;
            return steady + (v - steady) * MathF.Exp(-h / tau);
        }

        // The most speed a constant force may give an avatar: the engine's body speed cap, which a prim under llSetForce meets.
        private float ForceSpeedCap => _settings.MaxBodyLinearSpeed > 0f ? _settings.MaxBodyLinearSpeed : PhysicsBackendSettings.JoltMaxLinearSpeed;

        private static Vector3 CapLength(Vector3 v, float cap)
        {
            float len = v.Length();
            return len > cap ? v * (cap / len) : v;
        }

        // After the move: the force's velocity on to the end of the step, less what blocked the avatar took, as a push's is
        // (FadePush): across by the share of its level speed the move made, and while flying up or down likewise.
        private void FinishForceCarry(JoltCharacterRecord rec, CharacterVirtual ch, Vector3 accel, float tau, Vector3 commanded, Vector3 startPos, float dt)
        {
            Vector3 carry = AdvanceForceCarry(rec.ForceCarry, accel, tau, dt);
            Vector3 moved = (ch.Position - startPos) / dt;
            float wanted = new Vector2(commanded.X, commanded.Y).Length();
            float made = new Vector2(moved.X, moved.Y).Length();
            if (wanted > 0.01f && made < wanted)
            {
                carry.X *= made / wanted;
                carry.Y *= made / wanted;
            }
            if (MathF.Abs(commanded.Z) > 0.01f && carry.Z != 0f)
            {
                float share = moved.Z / commanded.Z;
                if (share < 1f)
                    carry.Z *= MathF.Max(0f, share);
            }
            carry = CapLength(carry, ForceSpeedCap);
            // With the force ended, a velocity faded to nothing is none (as a push's is), and the avatar is as one never pushed.
            if (accel == Vector3.Zero && carry.LengthSquared() < 1e-6f)
                carry = Vector3.Zero;
            rec.ForceCarry = carry;
        }

        // After the move: what blocked the avatar takes the push speed it blocked (a wall stops it), then the push
        // speed fades on walkable ground and when flying.
        private static void FadePush(JoltCharacterRecord rec, CharacterVirtual ch, Vector3 commanded, Vector3 startPos, float dt)
        {
            Vector3 moved = (ch.Position - startPos) / dt;
            float wanted = new Vector2(commanded.X, commanded.Y).Length();
            float made = new Vector2(moved.X, moved.Y).Length();
            if (wanted > 0.01f && made < wanted)
                rec.Push = new Vector3(rec.Push.X * made / wanted, rec.Push.Y * made / wanted, rec.Push.Z);

            float fade = rec.Flying ? PushFadeFlyingSeconds
                       : ch.GroundState == GroundState.OnGround ? PushFadeOnGroundSeconds : 0f;
            if (fade > 0f)
                rec.Push *= MathF.Exp(-dt / fade);
            if (rec.Push.LengthSquared() < 1e-6f)
                rec.Push = Vector3.Zero;
        }

        // =====================================================================
        // World
        // =====================================================================

        public void SetGravity(Vector3 gravity)
        {
            if (!IsFinite(gravity)) { CountRejectedNonFinite(); return; }
            // Under _simLock like every other write to the system: Step runs _system.Update under it, and Dispose frees it.
            lock (_simLock)
            {
                if (_disposed)
                    return;
                if (_system != null)
                    _system.Gravity = gravity;
                _settings.Gravity = gravity;
            }
        }

        public void SetTerrain(ShapeId heightFieldShape, Vector3 position)
        {
            // _simLock: SetTerrain swaps the terrain BODY (RemoveBody + a direct CreateAndAddBody), a
            // broadphase mutation. It runs on the SCENE thread on a live terrain edit, so it must not race
            // the heartbeat's Update. RemoveBody re-enters _simLock (re-entrant Monitor - fine).
            lock (_simLock)
            {
            if (_disposed) return;
            if (_system == null)
                throw new InvalidOperationException("SetTerrain before Initialize.");
            if (!_shapes.TryGet(heightFieldShape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                throw new ArgumentException($"SetTerrain: {heightFieldShape} is not a live shape handle.");

            // Replace any existing terrain.
            if (_terrainBody.IsValid)
            {
                RemoveBody(_terrainBody);
                _terrainBody = BodyId.Invalid;
            }

            // Static body in the Terrain layer. The shape is already Z-up-correct (the
            // RotatedTranslatedShape wrapper from CreateHeightFieldShape), so no rotation here.
            var objectLayer = new ObjectLayer((uint)PhysicsLayer.Terrain);
            var bcs = new BodyCreationSettings(
                shapeRec.NativeShape, position, Quaternion.Identity, MotionType.Static, objectLayer);
            try
            {
                bcs.Friction = _settings.TerrainFriction;
                bcs.Restitution = _settings.TerrainRestitution;
                BodyID joltId = _bodyInterface.CreateAndAddBody(bcs, Activation.DontActivate);

                // CreateBody's invalid-id policy. At MaxBodies there is no terrain body:
                // record nothing against 0xFFFFFFFF, count it, and flag it - the module logs an error, because a
                // region with no terrain collision is broken.
                if (joltId.IsInvalid)
                {
                    Interlocked.Increment(ref _bodyCreateFailures);
                    Volatile.Write(ref _terrainBodyMissing, true);
                    return;
                }
                Volatile.Write(ref _terrainBodyMissing, false);

                var rec = new JoltBodyRecord
                {
                    NativeBodyId = joltId.ID,
                    Shape = heightFieldShape,
                    Layer = PhysicsLayer.Terrain,
                    MotionType = BodyMotionType.Static,
                    UserData = 0u,
                    WantsContactEvents = false,
                    Friction = bcs.Friction,
                    Restitution = bcs.Restitution,
                };
                uint handle = _bodies.Add(rec);
                rec.Handle = handle;
                _joltToRecord[joltId.ID] = rec;
                _terrainBody = new BodyId(handle);
            }
            finally { FreeBodyCreationSettings(bcs); }
            }   // _simLock
        }

        public void SetWaterHeight(float height)
        {
            if (!float.IsFinite(height)) { CountRejectedNonFinite(); return; }
            _waterHeight = height;
        }

        // =====================================================================
        // Queries
        //
        // EVERY query below MUST hold _simLock for the whole call. They are NOT
        // safe concurrent with Step: NarrowPhaseQuery walks the same broadphase
        // Update mutates, and both draw on the PhysicsSystem's internal LIFO
        // TempAllocator, whose out-of-order free aborts the process (see the
        // _simLock note at the top of this file).
        //
        // Cost: a query issued while the step is running blocks for the remainder
        // of that step (single-digit ms). That is the same trade BulletSim makes,
        // and it is strictly better than a hard crash.
        //
        // Callers are off-thread by nature - llCastRay runs on script threads and
        // avatar setup on the login/teleport thread - so this lock is load-bearing,
        // not defensive.
        // =====================================================================

        public bool RayCast(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, out RayHit hit)
        {
            hit = default;
            if (_system == null)
                return false;
            // Query policy: non-finite input returns no hits (measured: without this check, a NaN ray
            // origin reports a hit).
            if (!IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(maxDistance))
                return false;

            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return false;

            // Jolt encodes the ray LENGTH in the direction vector's magnitude (not normalized).
            Vector3 rayDir = direction / len * maxDistance;
            var ray = new Ray(origin, rayDir);

            // QueryFilter is now honoured via a per-layer ObjectLayerFilter (cached per filter value).
            // _simLock spans SurfaceNormalOf too - that takes a body lock and reads shape geometry,
            // which is equally unsafe against a concurrent Update.
            lock (_simLock)
            {
                if (_disposed) return false;   // backend torn down (shutdown race) - no native call
                if (!_system.NarrowPhaseQuery.CastRay(ray, out RayCastResult result, null, FilterFor(filter), null))
                    return false;

                Vector3 point = origin + rayDir * result.Fraction;
                _joltToRecord.TryGetValue(result.BodyID.ID, out JoltBodyRecord? rec);
                hit = new RayHit
                {
                    Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                    UserData = rec != null ? rec.UserData : 0u,
                    ChildUserData = ResolveChildUserData(rec, result.subShapeID2),
                    Point = point,
                    Normal = SurfaceNormalOf(result.BodyID, result.subShapeID2, point),
                    Distance = maxDistance * result.Fraction,
                };
                return true;
            }
        }

        public int RayCastAll(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, Span<RayHit> hits)
        {
            if (_system == null)
                return 0;
            if (!IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(maxDistance))
                return 0;
            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return 0;

            Vector3 rayDir = direction / len * maxDistance;
            var ray = new Ray(origin, rayDir);
            // AllHitSorted = every hit along the ray, sorted by distance, no duplicates. The collector
            // needs an ICollection; this List is the one query-path allocation (queries run at script
            // rate, not per frame - a thread-local pool would be a later optimisation).
            var results = new List<RayCastResult>();
            lock (_simLock)
            {
            if (_disposed) return 0;   // backend torn down (shutdown race) - no native call
            _system.NarrowPhaseQuery.CastRay(
                ray, new RayCastSettings(), CollisionCollectorType.AllHitSorted, results, null, FilterFor(filter), null, null);

            // Collapse COINCIDENT duplicates: the heightfield's two triangles meeting at the ray XY report
            // two hits at the SAME point on the SAME body - which BulletSim (closest-hit) never produces and
            // scripts counting llCastRay hits do not expect. Drop a hit only when it is the same body AND the
            // same point (within CoincidentEpsilon) as the previous KEPT hit, so a stack of prims (different
            // bodies / different points) or terrain-then-prim (different bodies) is preserved in full,
            // distance-ordered. Results are distance-sorted, so any coincident pair is adjacent.
            int n = 0;
            uint prevBodyId = 0;
            Vector3 prevPoint = default;
            bool havePrev = false;
            for (int i = 0; i < results.Count && n < hits.Length; i++)
            {
                RayCastResult r = results[i];
                Vector3 point = origin + rayDir * r.Fraction;
                if (havePrev && r.BodyID.ID == prevBodyId && Vector3.DistanceSquared(prevPoint, point) < CoincidentEpsilonSq)
                    continue;
                _joltToRecord.TryGetValue(r.BodyID.ID, out JoltBodyRecord? rec);
                hits[n++] = new RayHit
                {
                    Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                    UserData = rec != null ? rec.UserData : 0u,
                    ChildUserData = ResolveChildUserData(rec, r.subShapeID2),
                    Point = point,
                    Normal = SurfaceNormalOf(r.BodyID, r.subShapeID2, point),
                    Distance = maxDistance * r.Fraction,
                };
                prevBodyId = r.BodyID.ID;
                prevPoint = point;
                havePrev = true;
            }
            return n;
            }   // _simLock (spans SurfaceNormalOf in the loop above - also unsafe vs a live Update)
        }

        // JoltPhysicsSharp 2.19.x query adaptation (two changes vs 2.18.6; RayCast unaffected):
        //  (1) CollideShape/CastShape now read the COM transform COLUMN-major. System.Numerics builds it
        //      row-major (translation in the last ROW); 2.18.6's wrapper transposed internally, 2.19.x does
        //      NOT - so an un-transposed transform collapses the query shape to ~origin (it then only hits
        //      terrain, never the target). Fix: pass Matrix4x4.Transpose(com). Transposing a row-major matrix
        //      is the equivalent column-major transform for ANY rotation, so this is exact, not identity-only.
        //  (2) The no-settings overloads now pass a ZERO-initialized settings struct (CollisionTolerance=0,
        //      PenetrationTolerance=0), degenerating GJK/EPA. 2.18.6 seeded Jolt's real defaults; restore below.
        private const float JoltCollisionTolerance = 1.0e-4f;   // Jolt cDefaultCollisionTolerance
        private const float JoltPenetrationTolerance = 1.0e-4f; // Jolt cDefaultPenetrationTolerance

        private static CollideShapeSettings DefaultCollideSettings() => new CollideShapeSettings
        {
            CollisionTolerance = JoltCollisionTolerance,
            PenetrationTolerance = JoltPenetrationTolerance,
            MaxSeparationDistance = 0f,
            ActiveEdgeMode = ActiveEdgeMode.CollideOnlyWithActive,  // Jolt's real CollideShapeSettings default
            BackFaceMode = BackFaceMode.IgnoreBackFaces,            // Jolt's real default
        };

        private static ShapeCastSettings DefaultCastSettings() => new ShapeCastSettings
        {
            CollisionTolerance = JoltCollisionTolerance,
            PenetrationTolerance = JoltPenetrationTolerance,
            ActiveEdgeMode = ActiveEdgeMode.CollideWithAll,
            BackFaceModeTriangles = BackFaceMode.IgnoreBackFaces, // a sweep enters through the FRONT face
            BackFaceModeConvex = BackFaceMode.IgnoreBackFaces,
            ReturnDeepestPoint = false,
            UseShrunkenShapeAndConvexRadius = false,
        };

        public int OverlapSphere(Vector3 center, float radius, QueryFilter filter, Span<BodyId> results)
        {
            if (_system == null)
                return 0;
            if (!IsFinite(center) || !float.IsFinite(radius))
                return 0;
            using var sphere = new SphereShape(MathF.Max(0.001f, radius));
            var found = new List<CollideShapeResult>();
            var cs = DefaultCollideSettings();
            lock (_simLock)
            {
                if (_disposed) return 0;   // backend torn down (shutdown race) - no native call
                _system.NarrowPhaseQuery.CollideShape(
                    sphere, Vector3.One, Matrix4x4.Transpose(Matrix4x4.CreateTranslation(center)), cs, Vector3.Zero,
                    CollisionCollectorType.AllHit, found, null, FilterFor(filter), null, null);
            }
            return CollectUniqueBodies(found, results);
        }

        public int OverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, QueryFilter filter, Span<BodyId> results)
        {
            if (_system == null)
                return 0;
            if (!IsFinite(center) || !IsFinite(halfExtents) || !IsUsable(orientation))
                return 0;
            float minHalf = MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z));
            float cr = MathF.Max(0f, MathF.Min(DefaultConvexRadius, minHalf * 0.1f));
            using var box = new BoxShape(halfExtents, cr);
            // A box's centre of mass IS its centre, so the COM transform is just rotate-then-translate.
            Matrix4x4 com = Matrix4x4.CreateFromQuaternion(orientation);
            com.Translation = center;
            var found = new List<CollideShapeResult>();
            var cs = DefaultCollideSettings();
            lock (_simLock)
            {
                if (_disposed) return 0;   // backend torn down (shutdown race) - no native call
                _system.NarrowPhaseQuery.CollideShape(
                    box, Vector3.One, Matrix4x4.Transpose(com), cs, Vector3.Zero,
                    CollisionCollectorType.AllHit, found, null, FilterFor(filter), null, null);
            }
            return CollectUniqueBodies(found, results);
        }

        public bool BodyPenetratesOthers(BodyId body, float tolerance)
        {
            if (_system == null || !float.IsFinite(tolerance))
                return false;
            var found = new List<CollideShapeResult>();
            uint self;
            lock (_simLock)
            {
                if (_disposed) return false;   // backend torn down (shutdown race) - no native call
                if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                    return false;
                self = jid.ID;
                if (!_shapes.TryGet(rec.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                    return false;
                // the shape sits at the body's centre of mass, as in the sensor sweep above
                Matrix4x4 com = Matrix4x4.CreateFromQuaternion(_bodyInterface.GetRotation(jid));
                com.Translation = ToVec(_bodyInterface.GetCenterOfMassPosition(jid));
                _system.NarrowPhaseQuery.CollideShape(shapeRec.NativeShape, Vector3.One, Matrix4x4.Transpose(com), DefaultCollideSettings(),
                    Vector3.Zero, CollisionCollectorType.AllHit, found, null, FilterFor(QueryFilter.Static | QueryFilter.Dynamic), null, null);
            }
            for (int i = 0; i < found.Count; i++)
            {
                // the length of the penetration axis is the depth Jolt reports
                if (found[i].BodyID2.ID != self && found[i].PenetrationAxis.Length() > tolerance)
                    return true;
            }
            return false;
        }

        public bool ShapeCast(ShapeId shape, Vector3 origin, Quaternion orientation, Vector3 direction, float maxDistance, QueryFilter filter, out RayHit hit)
        {
            hit = default;
            if (_system == null)
                return false;
            if (!_shapes.TryGet(shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                return false;
            if (!IsFinite(origin) || !IsUsable(orientation) || !IsFinite(direction) || !float.IsFinite(maxDistance))
                return false;
            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return false;

            Vector3 castVec = direction / len * maxDistance; // Jolt encodes cast length in the vector magnitude.
            Matrix4x4 com = Matrix4x4.CreateFromQuaternion(orientation);
            com.Translation = origin;
            var results = new List<ShapeCastResult>();
            var scs = DefaultCastSettings();
            lock (_simLock)
            {
                if (_disposed) return false;   // backend torn down (shutdown race) - no native call
                _system.NarrowPhaseQuery.CastShape(
                    shapeRec.NativeShape, Matrix4x4.Transpose(com), castVec, scs, Vector3.Zero,
                    CollisionCollectorType.ClosestHit, results, null, FilterFor(filter), null, null);
            }
            if (results.Count == 0)
                return false;

            ShapeCastResult r = results[0];
            _joltToRecord.TryGetValue(r.BodyID2.ID, out JoltBodyRecord? rec);
            // PenetrationAxis points from the cast shape into the hit body; the surface normal the caller
            // wants (pointing back out of the struck surface) is its negation, normalised.
            Vector3 axis = r.PenetrationAxis;
            float axisLen = axis.Length();
            Vector3 normal = axisLen > 1e-12f ? -axis / axisLen : default;
            hit = new RayHit
            {
                Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                UserData = rec != null ? rec.UserData : 0u,
                ChildUserData = ResolveChildUserData(rec, r.SubShapeID2.Value),
                Point = r.ContactPointOn2,           // first-contact point on the struck body
                Normal = normal,
                Distance = maxDistance * r.Fraction,
            };
            return true;
        }

        // Surface normal at a hit needs a read-lock on the body (results carry only id/fraction/subshape).
        private Vector3 SurfaceNormalOf(BodyID bodyId, uint subShapeId, Vector3 worldPoint)
        {
            Vector3 normal = default;
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockRead(bodyId, out BodyLockRead lockRead);
            try
            {
                Body? body = lockRead.Succeeded ? lockRead.Body : null;
                if (body != null)
                    normal = body.GetWorldSpaceSurfaceNormal(new SubShapeID(subShapeId), worldPoint);
            }
            finally { bli.UnlockRead(lockRead); }
            return normal;
        }

        // Flatten CollideShape results (one per touching sub-shape/face - a compound yields several) into
        // a de-duplicated list of our BodyIds, stopping at the caller's buffer capacity.
        private int CollectUniqueBodies(List<CollideShapeResult> found, Span<BodyId> results)
        {
            int n = 0;
            for (int i = 0; i < found.Count && n < results.Length; i++)
            {
                if (!_joltToRecord.TryGetValue(found[i].BodyID2.ID, out JoltBodyRecord? rec))
                    continue;
                var id = new BodyId(rec.Handle);
                bool dup = false;
                for (int j = 0; j < n; j++)
                    if (results[j].Equals(id)) { dup = true; break; }
                if (!dup)
                    results[n++] = id;
            }
            return n;
        }

        // ObjectLayerFilter that honours a QueryFilter bitmask. Cached per filter value (below) so we do
        // not allocate a native callback object per query.
        private sealed class LayerQueryFilter : ObjectLayerFilter
        {
            private readonly QueryFilter _filter;
            public LayerQueryFilter(QueryFilter filter) { _filter = filter; }
            protected override bool ShouldCollide(ObjectLayer layer) => QueryFilterAllows(_filter, (PhysicsLayer)layer.Value);
        }

        private static bool QueryFilterAllows(QueryFilter filter, PhysicsLayer layer) => layer switch
        {
            PhysicsLayer.Terrain => (filter & QueryFilter.Terrain) != 0,
            PhysicsLayer.Static => (filter & QueryFilter.Static) != 0,
            // A prim keyframed motion moves is a fixed prim to every query.
            PhysicsLayer.MovingStatic => (filter & QueryFilter.Static) != 0,
            PhysicsLayer.Dynamic => (filter & QueryFilter.Dynamic) != 0,
            PhysicsLayer.Avatar => (filter & QueryFilter.Avatar) != 0,
            PhysicsLayer.Sensor => (filter & QueryFilter.Sensor) != 0,
            // Phantom prims are found with volume detectors: llCastRay's RC_DETECT_PHANTOM finds both.
            PhysicsLayer.Phantom => (filter & QueryFilter.Sensor) != 0,
            // The avatar query-marker is found by exactly the filters that name Avatar (llSensor/
            // sit-target). filter=Static/Dynamic/Terrain do NOT return it. This is the ONLY way an
            // avatar surfaces to the query family.
            PhysicsLayer.AvatarQuery => (filter & QueryFilter.Avatar) != 0,
            // Debris has no QueryFilter bit - detection queries (llCastRay/llSensor) never return particle
            // debris, so it is excluded from every filter, INCLUDING All.
            PhysicsLayer.Debris => false,
            _ => false,
        };

        // Resolve (and cache) the ObjectLayerFilter for a QueryFilter value. We always use a filter (never
        // null) so Debris is consistently excluded even for QueryFilter.All.
        private ObjectLayerFilter FilterFor(QueryFilter filter)
            => _queryFilters.GetOrAdd(filter, f => new LayerQueryFilter(f));

        // =====================================================================
        // Health
        // =====================================================================

        public PhysicsCapacityStats GetCapacityStats()
        {
            var s = new PhysicsCapacityStats
            {
                RejectedNonFinite = Interlocked.Read(ref _rejectedNonFinite),
                ManifoldCacheFullSteps = Interlocked.Read(ref _manifoldCacheFullSteps),
                BodyPairCacheFullSteps = Interlocked.Read(ref _bodyPairCacheFullSteps),
                ContactConstraintsFullSteps = Interlocked.Read(ref _contactConstraintsFullSteps),
                LastUpdateError = (PhysicsUpdateErrors)Volatile.Read(ref _lastUpdateError),
                BodyCreateFailures = Interlocked.Read(ref _bodyCreateFailures),
                TerrainBodyMissing = Volatile.Read(ref _terrainBodyMissing),
                MaxBodies = _settings.MaxBodies,
                MaxBodyPairs = _settings.MaxBodyPairs,
                MaxContactConstraints = _settings.MaxContactConstraints,
                ContactRingCapacity = _contactListener?.Capacity ?? 0,
                DroppedContacts = _contactListener?.DroppedTotal ?? 0,
                JobThreadCount = Volatile.Read(ref s_jobThreads),
                JobPools = Volatile.Read(ref s_pools)?.Length ?? 0,
                JobThreadsPerPool = Volatile.Read(ref s_jobThreadsPerPool),
                JobThreadSource = (JobThreadSource)Volatile.Read(ref s_jobThreadSource),
                JobPoolsRequested = Volatile.Read(ref s_jobPoolsRequested),
                JobPoolsLimitedBy = Volatile.Read(ref s_jobPoolsLimitedBy),
                UpdateGateWaits = Interlocked.Read(ref _gateWaits),
                UpdateGateWaitMsTotal = TicksToMs(Interlocked.Read(ref _gateWaitTicksTotal)),
                UpdateGateWaitMsMax = TicksToMs(Interlocked.Read(ref _gateWaitTicksMax)),
                RegionLockWaits = Interlocked.Read(ref _simLockWaits),
                RegionLockWaitMsTotal = TicksToMs(Interlocked.Read(ref _simLockWaitTicksTotal)),
                RegionLockWaitMsMax = TicksToMs(Interlocked.Read(ref _simLockWaitTicksMax)),
            };
            FillRayCastStats(ref s);
            JobPool? pool = Volatile.Read(ref _pool);
            if (pool != null)
            {
                s.PoolIndex = pool.Index;
                s.PoolPeakInside = Volatile.Read(ref pool.PeakInside);
                s.JobPoolFairHandoff = pool.Fair;
            }

            // The live counts are native reads; take the locks every other native read takes, in order.
            lock (_simLock)
            {
                if (_disposed || _system == null)
                    return s;
                s.LiveBodyCount = (int)_system.BodiesCount;
                s.LiveShapeCount = _shapes.Count;
                s.ActiveBodyCount = (int)_system.GetNumActiveBodies(BodyType.Rigid);
                lock (_characterGate)
                    s.CharacterCount = _characterList.Count;
            }
            return s;
        }

        // This region waited `ticks` (Stopwatch ticks) at the update gate.
        private void RecordGateWait(long ticks)
        {
            Interlocked.Increment(ref _gateWaits);
            Interlocked.Add(ref _gateWaitTicksTotal, ticks);
            long max;
            while (ticks > (max = Interlocked.Read(ref _gateWaitTicksMax))
                   && Interlocked.CompareExchange(ref _gateWaitTicksMax, ticks, max) != max) { }
        }

        // A step waited `ticks` (Stopwatch ticks) for this region's _simLock.
        private void RecordSimLockWait(long ticks)
        {
            Interlocked.Increment(ref _simLockWaits);
            Interlocked.Add(ref _simLockWaitTicksTotal, ticks);
            long max;
            while (ticks > (max = Interlocked.Read(ref _simLockWaitTicksMax))
                   && Interlocked.CompareExchange(ref _simLockWaitTicksMax, ticks, max) != max) { }
        }

        // `inside` callers are in this pool's Update right now; keep the pool's high-water mark.
        private static void RecordInside(JobPool pool, int inside)
        {
            int peak;
            while (inside > (peak = Volatile.Read(ref pool.PeakInside))
                   && Interlocked.CompareExchange(ref pool.PeakInside, inside, peak) != peak) { }
        }

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // Fold one update's error flags into the counters. Step thread, under _simLock.
        private void RecordUpdateError(PhysicsUpdateError err)
        {
            if (err == PhysicsUpdateError.None)
                return;
            if ((err & PhysicsUpdateError.ManifoldCacheFull) != 0) Interlocked.Increment(ref _manifoldCacheFullSteps);
            if ((err & PhysicsUpdateError.BodyPairCacheFull) != 0) Interlocked.Increment(ref _bodyPairCacheFullSteps);
            if ((err & PhysicsUpdateError.ContactConstraintsFull) != 0) Interlocked.Increment(ref _contactConstraintsFullSteps);
            Volatile.Write(ref _lastUpdateError, (int)(PhysicsUpdateErrors)(byte)err);
        }

        // =====================================================================
        // Step
        // =====================================================================

        public StepResult Step(
            float deltaTime,
            Span<BodyState> bodyUpdates,
            Span<CharacterState> characterUpdates,
            Span<ContactReport> contacts)
        {
            // LOCK ORDER pool gate -> _simLock. The pool's gate (ONE Update at a time per pool)
            // is taken BEFORE this region's _simLock and held for the whole step, so a region waiting for its
            // pool does not hold _simLock - its scene-thread body ops and queries run meanwhile (with the gate
            // inside _simLock, a multi-region crossing test went from under 5 s to ~2 min). Only Step (one gate) and
            // the physics-system create and destroy (all gates, in pool order; see AllPoolGates) take a gate,
            // and nothing takes a gate while holding any _simLock, so the order cannot invert. If Dispose runs
            // while we wait, StepLocked's _disposed check returns once we have the gate and _simLock.
            JobPool? pool = _pool;
            bool poolWaited = false;
            long poolWaitTicks = 0;
            string? poolHeldBy = null;
            if (pool != null)
            {
                poolWaited = pool.Enter(_settings.RegionName, out poolWaitTicks, out poolHeldBy);
                if (poolWaited)
                    RecordGateWait(poolWaitTicks);
            }
            JobPool? gateBefore = t_gateHeld;
            try
            {
                t_gateHeld = pool;   // the pool gate rule: this thread holds this pool's gate until the finally
                GateTakenForTest?.Invoke(this);
                return StepLocked(pool, deltaTime, bodyUpdates, characterUpdates, contacts, poolWaited, poolWaitTicks, poolHeldBy);
            }
            finally
            {
                t_gateHeld = gateBefore;
                pool?.Exit();
            }
        }

        // Step's body, under _simLock. `pool` is the pool whose gate the caller holds (null: none assigned); the
        // caller's wait for it is reported in the result.
        private StepResult StepLocked(
            JobPool? pool,
            float deltaTime,
            Span<BodyState> bodyUpdates,
            Span<CharacterState> characterUpdates,
            Span<ContactReport> contacts,
            bool poolWaited,
            long poolWaitTicks,
            string? poolHeldBy)
        {
            _stepTimer.Restart();

            // _simLock spans the WHOLE step, not just _system.Update: CharacterVirtual.ExtendedUpdate
            // (phase 1 below) draws on the SAME internal TempAllocator as Update, so a query landing
            // between the two would corrupt it just as surely. Held here, released on exit - every
            // off-thread query blocks for the step duration (single-digit ms) and then proceeds.
            // Order is _simLock -> _characterGate, matching the rule at the top of this file.
            // Taken as `lock` takes it, with any wait for it (a body change or query on another thread holding it) timed
            // on its own. That wait is still inside the step's physics time, as before, and the job pool is held through it.
            bool simLockWaited = false;
            long simLockWaitTicks = 0;
            if (!Monitor.TryEnter(_simLock))
            {
                RegionLockBusyForTest?.Invoke();
                long lockWaitStart = Stopwatch.GetTimestamp();
                Monitor.Enter(_simLock);
                simLockWaitTicks = Stopwatch.GetTimestamp() - lockWaitStart;
                simLockWaited = true;
                RecordSimLockWait(simLockWaitTicks);
            }
            try
            {
            // Shutdown guard: if Dispose has run (or is mid-teardown having already set _disposed under
            // this same lock), do NOTHing - the PhysicsSystem / CharacterVirtuals are freed or about to be.
            // This is the heartbeat-vs-Dispose race fix: a Step that loses the race to Dispose returns an
            // empty result instead of calling ExtendedUpdate/Update on freed native memory (a shutdown
            // AccessViolation). _system is also null after teardown, so this doubles as a null guard.
            if (_disposed)
                return default;
            Volatile.Write(ref _contactStep, _contactStep + 1);

            // 1. Step every CharacterVirtual BEFORE the physics update. They are not part of the
            //    solve, so they must see the world as it was at the start of the frame or avatars
            //    jitter against moving prims.
            lock (_characterGate)
            {
                for (int i = 0; i < _characterList.Count; i++)
                {
                    JoltCharacterRecord crec = _characterList[i];
                    StepCharacter(crec, deltaTime);
                    // Sync the query marker to the JUST-stepped position, before _system.Update, so a
                    // query running mid-frame sees the avatar where it now is. DontActivate keeps the
                    // marker out of the active set (it never simulates) - it is only a query target.
                    if (crec.MarkerBodyId != 0 && crec.Character != null)
                        _bodyInterface.SetPositionAndRotation(
                            new BodyID(crec.MarkerBodyId), crec.Character.Position, crec.Character.Rotation, Activation.DontActivate);
                }
                _removedBodyIds.Clear();   // every avatar has been stepped since
            }

            // 2. Advance the simulation (3-arg Update, temp allocation internal).
            //    Uses this region's shared, process-capped job pool, not a per-region one.
            if (_system != null && pool != null)
            {
                int collisionSteps = Math.Max(1, _settings.CollisionSteps);
                _collisionStepSeconds = deltaTime / collisionSteps;   // the length of one solver step, for the bounce correction
                if (_characterList.Count > 0)
                    lock (_characterGate)
                        StrikeAvatarsInTheWay(deltaTime);
                UpdateCastBySpeed(deltaTime / collisionSteps);
                BeginSensorSweep(deltaTime);
                // The update's capacity error is counted, not discarded.
                PhysicsUpdateError updateError;
                // ONE Update at a time on this region's pool - Step holds the pool's gate.
                try
                {
                    RecordInside(pool, Interlocked.Increment(ref pool.Inside));
                    // Draws on the TempAllocator: only here, inside the pool gate (the pool gate rule).
                    using (Enter("PhysicsSystem::Update (joltc.cpp:1050)"))
                        updateError = _system.Update(deltaTime, collisionSteps, pool.System);
                }
                finally
                {
                    Interlocked.Decrement(ref pool.Inside);
                }
                RecordUpdateError(updateError);
                FinishSensorSweep();
            }

            // 3. Fold this frame's queued activation deltas into the step-thread-owned active
            //    set. This is the ONLY place _activeBodies is mutated. Ordered drain so an
            //    activate-then-deactivate within one frame nets out correctly.
            _justActivated.Clear();
            _justDeactivated.Clear();
            _staleActive.Clear();
            while (_activationQueue.TryDequeue(out ActivationDelta delta))
            {
                if (delta.Activated)
                {
                    if (_activeBodies.Add(delta.BodyId))
                        _justActivated.Add(delta.BodyId);
                }
                else
                {
                    _activeBodies.Remove(delta.BodyId);
                    _justActivated.Remove(delta.BodyId);
                    _justDeactivated.Add(delta.BodyId);
                }
                if (_joltToRecord.TryGetValue(delta.BodyId, out JoltBodyRecord? woken))
                {
                    if (!delta.Activated)
                        Volatile.Write(ref woken.SleptStep, _contactStep);
                    Volatile.Write(ref woken.Awake, delta.Activated);
                }
            }

            int bodyCount = 0;
            bool bodyOverflow = false;

            // Settle states FIRST. Bodies that slept this step get one final state with
            // JustDeactivated set - without it the viewer keeps interpolating and settled objects visibly drift.
            // They are never skipped when the active drain overflows; any that do not fit wait
            // in _pendingSettle (FIFO, oldest first) for the next Step. A body that woke again in the meantime is
            // no longer settled, so its stale settle state is discarded.
            for (int i = 0; i < _justDeactivated.Count; i++)
                _pendingSettle.Enqueue(_justDeactivated[i]);
            while (_pendingSettle.Count > 0)
            {
                uint joltId = _pendingSettle.Peek();
                if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec) || _activeBodies.Contains(joltId))
                {
                    _pendingSettle.Dequeue();   // removed, or awake again - nothing to settle
                    continue;
                }
                if (bodyCount >= bodyUpdates.Length) { bodyOverflow = true; break; }
                _pendingSettle.Dequeue();

                var jid = new BodyID(joltId);
                bodyUpdates[bodyCount++] = new BodyState
                {
                    Body = new BodyId(rec.Handle),
                    UserData = rec.UserData,
                    Position = _bodyInterface.GetPosition(jid),
                    Orientation = _bodyInterface.GetRotation(jid),
                    LinearVelocity = _bodyInterface.GetLinearVelocity(jid),
                    AngularVelocity = _bodyInterface.GetAngularVelocity(jid),
                    Flags = BodyStateFlags.JustDeactivated,
                };
            }

            // Then the ACTIVE set: O(active), NOT O(total), round-robin. Snapshot the step-thread-owned set
            // into a reused list (List.AddRange over a HashSet copies, no allocation once warm) and start where the
            // last overflowing Step stopped, so every active body is emitted within ceil(active / buffer) Steps.
            _activeSnapshot.Clear();
            _activeSnapshot.AddRange(_activeBodies);
            int activeN = _activeSnapshot.Count;
            int start = activeN > 0 ? _activeCursor % activeN : 0;
            for (int k = 0; k < activeN; k++)
            {
                int idx = (start + k) % activeN;
                uint joltId = _activeSnapshot[idx];
                if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec))
                {
                    _staleActive.Add(joltId); // removed out from under us; clean up after the loop
                    continue;
                }
                if (bodyCount >= bodyUpdates.Length)
                {
                    bodyOverflow = true;
                    _activeCursor = idx;      // resume here next Step
                    break;
                }

                var jid = new BodyID(joltId);
                BodyStateFlags flags = BodyStateFlags.Active;
                if (_justActivated.Contains(joltId)) flags |= BodyStateFlags.JustActivated;
                bodyUpdates[bodyCount++] = new BodyState
                {
                    Body = new BodyId(rec.Handle),
                    UserData = rec.UserData,
                    Position = _bodyInterface.GetPosition(jid),
                    Orientation = _bodyInterface.GetRotation(jid),
                    LinearVelocity = _bodyInterface.GetLinearVelocity(jid),
                    AngularVelocity = _bodyInterface.GetAngularVelocity(jid),
                    Flags = flags,
                };
            }
            for (int i = 0; i < _staleActive.Count; i++)
                _activeBodies.Remove(_staleActive[i]);

            // 4. Drain character state (post-ExtendedUpdate position + the ground each one found). Round-robin
            //    like the bodies: characters that do not fit are first in line next Step.
            int charCount = 0;
            lock (_characterGate)
            {
                int charN = _characterList.Count;
                int charStart = charN > 0 ? _characterCursor % charN : 0;
                for (int k = 0; k < charN; k++)
                {
                    int idx = (charStart + k) % charN;
                    if (_characterList[idx].Character == null)
                        continue;
                    if (charCount >= characterUpdates.Length)
                    {
                        _characterCursor = idx;
                        break;
                    }
                    characterUpdates[charCount++] = BuildCharacterState(_characterList[idx]);
                }
            }

            // 5. Drain contacts from the listener's ring buffer. Fed by the OnContact* handlers
            //    and the character contact callbacks.
            int contactCount = _contactListener.Drain(contacts, out bool contactOverflow);

            _stepTimer.Stop();

            return new StepResult(
                bodyCount,
                charCount,
                contactCount,
                bodyOverflow,
                contactOverflow,
                activeBodyCount: _activeBodies.Count,
                physicsMs: (float)_stepTimer.Elapsed.TotalMilliseconds,
                new StepWaits(poolWaited, TicksToMs(poolWaitTicks), poolHeldBy, simLockWaited, TicksToMs(simLockWaitTicks)));
            }
            finally
            {
                Monitor.Exit(_simLock);
            }
        }
    }

    // =========================================================================
    // Contact listener
    //
    // Jolt fires contact callbacks FROM WORKER THREADS, mid-solve. Two rules:
    //   - never touch scene state here
    //   - never allocate here
    // Write into a preallocated ring and drain on the step thread. This is the
    // most likely place for a first integration to deadlock or tear.
    // =========================================================================
    internal sealed class JoltContactListener
    {
        private readonly ContactReport[] _ring;
        private int _writeIndex;
        private int _dropped;
        private long _droppedTotal;   // cumulative ring drops, for GetCapacityStats

        public JoltContactListener(int capacity) => _ring = new ContactReport[Math.Max(1, capacity)];

        internal int Capacity => _ring.Length;
        internal long DroppedTotal => Interlocked.Read(ref _droppedTotal);

        // OnContactAdded  -> ContactPhase.Begin    -> LSL collision_start
        // OnContactPersisted -> ContactPhase.Persist -> LSL collision
        // OnContactRemoved -> ContactPhase.End     -> LSL collision_end
        //
        // Persist fires EVERY step for every touching pair. Filter here, not
        // above: a single avatar standing on a floor otherwise generates 45
        // events per second forever. Only forward Persist for pairs whose
        // owning object actually has a collision handler registered.
        internal void Push(in ContactReport report)
        {
            int index = Interlocked.Increment(ref _writeIndex) - 1;
            if (index >= _ring.Length)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _ring[index] = report;
        }

        internal int Drain(Span<ContactReport> destination, out bool overflowed)
        {
            int written = Math.Min(Volatile.Read(ref _writeIndex), _ring.Length);
            int count = Math.Min(written, destination.Length);

            _ring.AsSpan(0, count).CopyTo(destination);

            int dropped = Volatile.Read(ref _dropped);
            if (dropped > 0)
                Interlocked.Add(ref _droppedTotal, dropped);
            overflowed = dropped > 0 || written > destination.Length;
            Volatile.Write(ref _writeIndex, 0);
            Volatile.Write(ref _dropped, 0);
            return count;
        }
    }

    // =========================================================================
    // Handle table
    //
    // Generation-tagged slots. The low 24 bits index, the high 8 bits are a
    // generation counter bumped on free. A handle to a destroyed body fails
    // validation instead of silently addressing whatever got allocated in its
    // place - which is precisely the class of bug that makes physics crashes
    // impossible to reproduce.
    // =========================================================================
    internal sealed class HandleTable<T> where T : class
    {
        private const int IndexBits = 24;
        private const uint IndexMask = (1u << IndexBits) - 1u;

        private readonly object _gate = new object();
        private T?[] _slots = new T?[1024];
        private byte[] _generations = new byte[1024];
        private readonly ConcurrentQueue<int> _free = new ConcurrentQueue<int>();
        private int _highWater;
        private int _count;

        public uint Add(T item)
        {
            lock (_gate)
            {
                if (!_free.TryDequeue(out int slot))
                {
                    if (_highWater == _slots.Length)
                    {
                        Array.Resize(ref _slots, _slots.Length * 2);
                        Array.Resize(ref _generations, _generations.Length * 2);
                    }
                    slot = _highWater++;
                }

                _slots[slot] = item;
                _count++;
                // Generation 0 is reserved so a zeroed handle is never valid.
                if (_generations[slot] == 0) _generations[slot] = 1;
                return ((uint)_generations[slot] << IndexBits) | (uint)slot;
            }
        }

        public bool TryGet(uint handle, out T item)
        {
            int slot = (int)(handle & IndexMask);
            byte generation = (byte)(handle >> IndexBits);

            if (generation == 0 || slot >= _slots.Length || _generations[slot] != generation)
            {
                item = null!;
                return false;
            }

            T? candidate = _slots[slot];
            item = candidate!;
            return candidate != null;
        }

        public bool IsValid(uint handle) => TryGet(handle, out _);


        public bool Remove(uint handle)
        {
            lock (_gate)
            {
                if (!TryGet(handle, out _)) return false;

                int slot = (int)(handle & IndexMask);
                _slots[slot] = null;
                _count--;
                _generations[slot] = (byte)(_generations[slot] == 255 ? 1 : _generations[slot] + 1);
                _free.Enqueue(slot);
                return true;
            }
        }

        /// <summary>Live entries.</summary>
        public int Count
        {
            get { lock (_gate) return _count; }
        }

        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_slots, 0, _slots.Length);
                _highWater = 0;
                _count = 0;
                while (_free.TryDequeue(out _)) { }
            }
        }

        /// <summary>Empties the table and returns what was live in it, so the caller can release what the entries own.</summary>
        public List<T> TakeAll()
        {
            lock (_gate)
            {
                var live = new List<T>(_count);
                for (int i = 0; i < _highWater; i++)
                    if (_slots[i] is T item)
                        live.Add(item);
                Clear();
                return live;
            }
        }
    }

    // Records hold the native handles plus whatever module-side bookkeeping the
    // engine will not remember for us.
    internal sealed class JoltBodyRecord
    {
        public uint Handle;               // our HandleTable handle (for jolt-id -> BodyId)
        public uint NativeBodyId;         // Jolt BodyID.ID
        public ShapeId Shape;
        public PhysicsLayer Layer;
        public BodyMotionType MotionType;
        public uint UserData;
        public bool WantsContactEvents;   // gates Persist forwarding
        public float Mass;                // explicit or Volume x Density; 0 where mass is unused (static)
        public bool AllowMotionChange;    // created movable (AllowDynamicOrKinematic) -> may flip motion type
        public bool IsCharacterMarker;    // a query-only avatar marker (owned by its character; not a real prim)
        public long SleptStep;            // the step in which the engine last put the body to sleep (BodyHadContact)
        public long ContactStep;          // the last step in which the solver had this body touching another body (BodyHadContact)
        public byte RotationLocks;        // body-local axes it cannot turn about: 1 = x, 2 = y, 4 = z (SetBodyRotationLocks)
        public float Friction;            // the body's contact friction and restitution (SetBodyFriction, SetBodyRestitution)
        public float Restitution;
        public float[]? PartFriction;     // a compound's per-child values, in its child order (SetBodyPartMaterial); null = the body's
        public float[]? PartRestitution;
        public bool FrictionMin;          // SetBodyFrictionMin: this body's contacts combine friction with the smaller value
        public float GravityFactor;       // the body's gravity factor as last set (SetBodyGravityFactor); the bounce correction reads it
        public bool Awake;                // the engine had the body awake at the end of the last step (IsBodyAwake)
        public ContinuousCollision Ccd;   // SetBodyContinuousCollision; Off for a static body
        public bool CastingBySpeed;       // WhenFast: LinearCast for the coming update (UpdateCastBySpeed)
        public float InnerRadius;         // the body's shape's inner radius, for Jolt's cast threshold
    }

    internal sealed class JoltShapeRecord
    {
        public Shape? NativeShape;        // the shape this handle represents; disposed at RefCount 0
        public Shape? InnerShape;         // private inner shape OWNED by this wrapper (e.g. the Y-up
                                          // heightfield under a Z-up RotatedTranslatedShape); disposed with it
        public int RefCount;
        public bool IsWrapper;            // decorator wrapper (rotated/translated/scaled) or compound over other shapes
        public ShapeId BaseShape;         // caller-visible wrapped shape (CreateScaledShape); Invalid otherwise
        public uint[]? CompoundChildUserData; // ordered child UserData for a StaticCompound; null otherwise
        public int CompoundIndexBits;     // low bits of a hit SubShapeID that encode the compound child index
        public IntPtr[]? HullHandles;     // the native hulls of a hull compound (CreateHullCompoundShape), for SetShapeDensity; null otherwise
    }

    internal sealed class JoltCharacterRecord
    {
        public uint Handle;                   // our HandleTable handle
        public CharacterVirtual? Character;    // the Jolt controller, stepped outside _system.Update
        public Shape? StandingShape;           // Z-up rotated-capsule wrapper we own (disposed on remove)
        public Shape? InnerCapsule;            // the Y-up capsule the wrapper references (disposed with it)
        public uint UserData;
        public bool WantsContactEvents;        // gates Persist forwarding for this avatar's contacts
        public uint MarkerBodyId;              // Jolt BodyID.ID of the query-visible marker (0 = none)
        public JoltBodyRecord? MarkerRecord;   // the marker's body record (in _bodies + _joltToRecord)

        // Tuning knobs captured from CharacterDesc.
        public float CapsuleHalfHeight;
        public float CapsuleRadius;
        public float SupportRadius;            // the radius the supporting plane was built from (CharacterVirtual has no setter for it)
        public float MaxSlopeAngle;
        public float StepHeight;
        public float PushStrength;
        public float JumpSpeed;

        // Per-frame movement intent (set by SetCharacterMovement, consumed by StepCharacter).
        public Vector3 DesiredVelocity;
        public bool JumpRequested;
        public bool Flying;

        // Pushes (AddCharacterImpulse): received since the last step; the push speed still carried; and what is left
        // of the push allowance (starts full).
        public Vector3 PendingPush;
        public Vector3 Push;
        public float PushAllowance;
        public Vector3 Carry;                  // the horizontal velocity the avatar arrived with that its controls have not yet replaced (SetCharacterCarry)

        public float Mass;                     // CharacterDesc.Mass: what a loose object striking the avatar meets
        public Vector3 StepVelocity;           // the velocity the avatar moved with in its last step
        public Vector3 MovedVelocity;          // the velocity it was seen to move at in its last step, what it reports

        // An attachment's llSetBuoyancy and llSetHoverHeight on the wearer (SetCharacterBuoyancy, SetCharacterHover).
        public float Buoyancy;
        public Func<float, float, float>? HoverAt;   // where hover holds the capsule centre over (x, y); null = off
        public float HoverTau;
        public float HoverCarry;               // the spring's velocity this step's move did not use, for the next step
        public float HoverRise;                // the ground's rise under the avatar's path in its last hovering step (m/s)
        // An attachment's llSetForce on the wearer (SetCharacterConstantForce): the acceleration it gives (region axes), and
        // the velocity it has given that the controller does not keep itself (across, or all three axes while flying).
        public Vector3 ForceAcceleration;
        public Vector3 ForceCarry;
        public bool LooseAtSide;              // its last step touched a loose object from the side (no stepping up onto it)
        // The contacts its last update noted, reported after the move (FinishCharacterContacts). Step thread only.
        public readonly List<CharacterBodyContact> Contacts = new();
        public readonly HashSet<uint> Resting = new();   // bodies (Jolt ids) let fall asleep resting on it
        // Bodies (Jolt ids) that struck it since its last update giving it a push (Strike), and so have been slowed to the
        // speed it takes from them: these may push it on first touch. Step thread only.
        public readonly HashSet<uint> StruckBy = new();
        // Put where it is by the caller (added, or its position set) since its last update. Step thread clears it.
        public bool Placed;
    }

    /// <summary>One avatar update, for <see cref="JoltPhysicsBackend.CharacterStepped"/>: the avatar's id, how far the update
    /// moved it, the velocity it was moved with, the velocity it reports for the update, the update's length (s), and whether
    /// it had been put where it was by the caller since its last update.</summary>
    internal readonly record struct CharacterStepTrace(uint UserData, Vector3 Displacement, Vector3 Asked, Vector3 Reported, float Seconds, bool Placed);

    internal readonly record struct CharacterBodyContact(uint BodyJoltId, uint SubShape, Vector3 Point, Vector3 Normal, ContactPhase Phase, bool Loose);
}
