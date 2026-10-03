/*
 * Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon ExecutionScheduler.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 by Legion Builds for OpenSim 0.9.3 .NET 8
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using Microsoft.Extensions.Logging;
using PhloxEventInfo = InWorldz.Phlox.VM.EventInfo;
using SysLinkedList = System.Collections.Generic.LinkedList<InWorldz.Phlox.VM.Interpreter>;
using SysLinkedListNode = System.Collections.Generic.LinkedListNode<InWorldz.Phlox.VM.Interpreter>;

namespace Phlox.ScriptEngine
{
    internal class PhloxExecutionScheduler
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // How many total Tick() calls per DoWork() pass
        private const int INSTRUCTION_FREQUENCY = 48;
        // Ticks per script per pass
        private const int SCRIPT_TIMESLICE = 8;
        // Slow timeslice warning threshold ms
        private const int SLOW_THRESH_MS = 250;
        // Touch repeat interval ms
        private const ulong TOUCH_INTERVAL = 100;
        // Maximum number of queued events per script — prevents listen/timer floods
        private const int MAX_EVENT_QUEUE_DEPTH = 64;

        private readonly WorkArrivedDelegate m_WorkArrived;
        private readonly PhloxEngine m_Engine;
        /// <summary>Scripts loaded with the item's Running flag off and never started; enabling one owes it a state_entry.</summary>
        private readonly HashSet<UUID> m_HeldFresh = new HashSet<UUID>();
        private readonly IWorldComm m_WorldComm;

        // All scripts regardless of run state
        private readonly System.Collections.Generic.Dictionary<UUID, Interpreter> m_AllScripts = new();
        // Guards m_AllScripts add/remove against the cross-thread stats snapshot
        // (SnapshotScriptStats, called from the estate-request thread).
        private readonly object m_AllScriptsLock = new();

        // Run queue — scripts ready to execute
        private readonly SysLinkedList m_RunQueue = new();
        private SysLinkedListNode m_NextScript;
        private readonly System.Collections.Generic.Dictionary<UUID, SysLinkedListNode> m_RunIndex = new();

        // Sleeping scripts priority queue
        private struct SleepEntry : IComparable<SleepEntry>
        {
            public UUID ItemId;
            public ulong ReadyOn;
            public WakeEvent Event;
            public enum WakeEvent { None, Timer, Touch, MinDelay }

            public int CompareTo(SleepEntry other)
                => ReadyOn < other.ReadyOn ? -1 : ReadyOn > other.ReadyOn ? 1 : 0;
        }

        private readonly C5.IntervalHeap<SleepEntry> m_SleepHeap = new();
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_StdSleepHandles = new();
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_TimerHandles = new();
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_TouchHandles = new();
        /// <summary>One pending llMinEventDelay wake per script.</summary>
        private readonly System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> m_MinDelayHandles = new();
        /// <summary>
        /// The milliseconds a parcel-paused script's timer had left at the pause (Halcyon's InjectScript keeps
        /// them). Scheduler-thread only, transient: never saved, dropped on resume, on a new timer and on unload.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<UUID, ulong> m_ParcelTimerLeft = new();

        // Pending events (posted from outside thread)
        private readonly Queue<PendingEvent> m_PendingEvents = new();
        private struct PendingEvent { public UUID ItemId; public PostedEvent Evt; public bool GrabUpdate; }

        // Enable/disable requests
        private readonly Queue<EnableDisableReq> m_EnableDisableQueue = new();
        private struct EnableDisableReq { public UUID ItemId; public bool Enable; }

        // Transient suspend/resume requests (posted from estate/console threads) and the
        // suspended set itself. The set is scheduler-thread-only (drained-queue pattern, like
        // enable/disable) so it needs no lock. Suspension is deliberately NOT persisted —
        // it lives in scheduler bookkeeping, so a region restart clears it (SL semantics:
        // suspend is a live-operations pause, not a durable state). It is also NOT
        // enable/disable: listens, timers and touch subscriptions stay registered and the
        // user-visible Running flag is untouched; the script just stops receiving timeslices.
        private readonly Queue<SuspendResumeReq> m_SuspendResumeQueue = new();
        private struct SuspendResumeReq { public UUID ItemId; public bool Suspend; }
        private readonly HashSet<UUID> m_Suspended = new();

        // Reset requests
        private readonly Queue<UUID> m_PendingResets = new();

        // No Scripts parcels enforced live. Scene events only ask for a check; the check and the
        // pause/resume it leads to run here, on the scheduler thread, like enable/disable. Nothing polls.
        private enum ParcelCheckKind { Item, Group, Parcel, ControlHolders }
        private struct ParcelCheckReq { public ParcelCheckKind Kind; public UUID ItemId; public SceneObjectGroup Group; public int ParcelLocalId; }
        private readonly Queue<ParcelCheckReq> m_ParcelChecks = new();
        /// <summary>Each script's API, for its host prim and its sensor. Scheduler thread only.</summary>
        private readonly System.Collections.Generic.Dictionary<UUID, LSLSystemAPI> m_Apis = new();
        /// <summary>Scripts whose last check allowed them only because they held taken controls - the ones to
        /// ask again when the core clears controls. The check itself always asks the avatar. Scheduler thread only.</summary>
        private readonly HashSet<UUID> m_ControlsExempt = new();

        // Permission ends the scene reports (the core's release of controls on an avatar still here, a new
        // owner). Queued from region threads; EndPermissions runs here, on the scheduler thread, like every script call.
        private struct PermsEndReq { public UUID ItemId; public UUID AgentId; public SceneObjectGroup Group; }
        private readonly Queue<PermsEndReq> m_PermsEnds = new();

        /// <summary>What the parcel checks have done, for tests and the cost report.</summary>
        internal struct ParcelStats { public long Scanned, Evaluated, Paused, Resumed; }
        private ParcelStats m_ParcelStats;
        internal ParcelStats ParcelCounters => m_ParcelStats;

        // Syscall returns
        private readonly Queue<SyscallReturn> m_SyscallReturns = new();
        /// <summary>Seq is the call's syscall sequence number (-1 = unsequenced, accepted while in Syscall);
        /// Fault is an exception from a deferred service call, re-raised inside the script's next tick.</summary>
        private struct SyscallReturn { public UUID ItemId; public object RetValue; public int Delay; public int Seq; public Exception Fault; }

        // ── The service lane ─────────────────────────────────────────────
        // Syscalls that can leave the process (user accounts, grid, assets, experience, groups,
        // teleport, ...) run here instead of inline on this region's scheduler thread, so a slow
        // service stalls only the script that asked. Dedicated threads, not pool threads: at most
        // ServiceCallThreads per region, started on demand and exiting after ServiceThreadIdleMs idle,
        // so an idle region holds none. A call that has not answered by its deadline resumes the script
        // with the function's failure value; the late answer is then dropped (sequence number).
        private readonly ConcurrentQueue<DeferredServiceCall> m_ServiceQueue = new();
        private readonly SemaphoreSlim m_ServiceSignal = new(0);
        private int m_ServiceThreadCount;
        private int m_ServiceThreadsIdle;
        private int m_ServiceThreadSeq;
        /// <summary>Calls handed to the lane and not yet swept; scheduler thread only.</summary>
        private readonly List<DeferredServiceCall> m_OutstandingServiceCalls = new();
        internal int ServiceCallTimeoutMs = 35000;
        internal int ServiceCallThreads = 4;
        internal int ServiceThreadIdleMs = 30000;

        // Async syscall dispatcher (for long-running syscalls like HTTP, dataserver, etc.).
        // Replaces the vendored SmartThreadPool ("Phlox Async", MinWorkerThreads=0,
        // MaxWorkerThreads=6, IdleTimeout=60s) with the system thread pool, preserving
        // its semantics: strict FIFO start order, at most MAX_ASYNC_WORKERS calls in
        // flight, and no dedicated threads held while idle (drain workers exit when the
        // queue empties, so idle reclamation is immediate rather than 60s).
        private const int MAX_ASYNC_WORKERS = 6;
        private readonly ConcurrentQueue<SyscallShim.LongRunSyscallDelegate> m_AsyncQueue = new();
        private int m_AsyncWorkers;

        // Deferred events for scripts not yet loaded. Only for an item whose Phlox load is in flight, at most
        // MaxDeferredEventsPerItem per item, and for at most DeferredEventLifetimeMs from the first one (Halcyon
        // DeferredEventManager: MAX_DEFERRED_EVENTS = 32, EXPIRATION_SECONDS = 60). Every other event for an item that is
        // not loaded is dropped at once and counted in m_DroppedForUnloaded. Scheduler thread only.
        internal const int MaxDeferredEventsPerItem = 32;
        internal const ulong DeferredEventLifetimeMs = 60_000;
        private sealed class DeferredEvents { public ulong ExpiresOn; public readonly List<PostedEvent> Events = new(); }
        private readonly System.Collections.Generic.Dictionary<UUID, DeferredEvents> m_DeferredEvents = new();
        private long m_DroppedForUnloaded;
        private ulong m_NextDeferredExpiry;

        private readonly System.Diagnostics.Stopwatch m_SliceWatch = new();

        public PhloxExecutionScheduler(WorkArrivedDelegate workArrived, PhloxEngine engine, IWorldComm worldComm)
        {
            m_WorkArrived = workArrived;
            m_Engine = engine;
            m_WorldComm = worldComm;

            // [InWorldz.Phlox] ServiceCallTimeoutMs (35000), ServiceCallThreads (4 per region).
            var cfg = engine?.Config;
            if (cfg != null)
            {
                ServiceCallTimeoutMs = Math.Max(1, cfg.GetInt("ServiceCallTimeoutMs", ServiceCallTimeoutMs));
                ServiceCallThreads = Math.Max(1, cfg.GetInt("ServiceCallThreads", ServiceCallThreads));
            }
        }

        // ── Called by ScriptLoader once a script is ready to run ──────────────

        internal void FinishedLoading(PhloxLoadRequest req, CompiledScript compiled)
        {
            if (m_AllScripts.ContainsKey(req.ItemID))
            {
                m_log.LogDebug("[PhloxExe]: Skipping duplicate OnRezScript for {0} (already loaded)", req.ItemID);
                return;
            }
            if (req.Prim == null)
            {
                m_log.LogError("[PhloxExe]: Prim is null for item {0}", req.ItemID);
                return;
            }

            SyscallShim shim = new SyscallShim(PerformAsyncCall);
            // Service-reaching calls go to the lane unless the operator turned deferral off.
            if (m_Engine?.ServiceCallDeferral != ServiceCallDeferralMode.Never)
                shim.DeferServiceCall = DeferServiceCall;
            LSLSystemAPI sysApi = new LSLSystemAPI(m_Engine, req.Prim, req.Prim.LocalId, req.ItemID);
            shim.SystemAPI = sysApi;

            Interpreter interp;
            bool freshStart;
            bool holdStateLoadFailed = false;
            bool restoredFromCarried = false;

            try
            {
                InWorldz.Phlox.Serialization.SerializedRuntimeState savedState = null;
                bool carried = false;
                try
                {
                    if (m_Engine.StateManager != null)
                        savedState = m_Engine.StateManager.LoadState(req.ItemID, compiled.AssetId, out carried);
                }
                catch (StateLoadFailedException e)
                {
                    // The row may be there and unreadable right now. A fresh start here would
                    // save over it at the next flush. Hold the script instead: loaded, visible in
                    // `phlox status` as StateLoadFailed, never run, never saved. A restart retries.
                    m_log.LogError("[PhloxExe]: Holding {0} DISABLED (state load failed, row kept): {1}", req.ItemID, e.Message);
                    holdStateLoadFailed = true;
                }
                if (savedState != null)
                {
                    try
                    {
                        // State that came with the object is input from outside this simulator: it must fit the
                        // compiled script before anything is built from it, and again once it is.
                        string misfit = carried ? RestoredStateFit.CheckSerialized(savedState) : null;
                        RuntimeState restoredRuntimeState = null;
                        string recompiledNote = null;
                        if (misfit == null)
                        {
                            // A state saved mid-event on bytecode that has since been recompiled
                            // comes back idle, with its globals, queue and timers.
                            restoredRuntimeState = savedState.ToRuntimeStateFor(compiled, req.ItemID, out recompiledNote);
                            if (carried) misfit = RestoredStateFit.Check(restoredRuntimeState, compiled) ?? FitCarriedStateHere(restoredRuntimeState);
                        }
                        if (misfit != null)
                        {
                            m_log.LogWarning("[PhloxExe]: The state {0} brought with its object does not fit its script ({1}); it starts fresh", req.ItemID, misfit);
                            interp = new Interpreter(compiled, shim);
                            freshStart = true;
                        }
                        else
                        {
                            if (recompiledNote != null) m_log.LogInformation(recompiledNote);
                            interp = new Interpreter(compiled, restoredRuntimeState, shim);
                            freshStart = false;
                            restoredFromCarried = carried;
                            m_log.LogDebug("[PhloxExe]: Restored state for {0}", req.ItemID);
                        }
                    }
                    catch (Exception e) when (carried)
                    {
                        // Carried state came with the object, so there is no row of ours to move aside.
                        m_log.LogWarning("[PhloxExe]: The state {0} brought with its object does not restore; it starts fresh: {1}", req.ItemID, e.Message);
                        interp = new Interpreter(compiled, shim);
                        freshStart = true;
                    }
                    catch (Exception e)
                    {
                        // Read but not restorable: bad data, not a busy database. Moved aside, and the script starts
                        // fresh, as Halcyon and YEngine reset a script whose state cannot be loaded.
                        m_log.LogWarning("[PhloxExe]: State restore failed for {0}: {1}", req.ItemID, e.Message);
                        m_Engine.StateManager?.RejectRow(req.ItemID, "the saved state does not restore: " + e.Message);
                        interp = new Interpreter(compiled, shim);
                        freshStart = true;
                    }
                }
                else
                {
                    interp = new Interpreter(compiled, shim);
                    freshStart = true;
                }
                interp.ItemId = req.ItemID;
                shim.Interpreter = interp;
                sysApi.Script = interp;
            }
            catch (VMException e)
            {
                m_log.LogError("[PhloxExe]: VM error starting {0}: {1}", req.ItemID, e);
                return;
            }

            interp.OnStateChg += OnStateChange;
            // A rez gives the script its start parameter. A region start or a crossing carries none, and a restored
            // script does not keep the one it was saved with: SL's llGetStartParameter "does not survive region
            // restarts (SVC-2251) or region change (SVC-3258, crossing or teleport)".
            interp.ScriptState.StartParameter = (freshStart || req.PostOnRez) ? req.StartParam : 0;
            interp.HostLocalId = req.Prim.LocalId;

            lock (m_AllScriptsLock)
                m_AllScripts[interp.ItemId] = interp;
            m_Apis[interp.ItemId] = sysApi;
            interp.SetScriptEventFlags();

            if (holdStateLoadFailed)
            {
                interp.ScriptState.LocalDisable |= RuntimeState.LocalDisableFlag.StateLoadFailed;
                interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                return;   // no state_entry, no run queue - and StateManager refuses to save it
            }

            if (freshStart)
            {
                // New script — fire state_entry to run the script's initialization.
                //
                // PHLOX-2d: this MUST be Waiting, not Running. ProcessEventQueue only starts an
                // event when the script is Waiting (:681); anything else is queued (:687), and the
                // queue is drained by TransitionToWait, which runs only when a script that is
                // already on the run queue finishes an event. A fresh script set to Running was
                // therefore never started by anything: its state_entry sat in the queue for ever,
                // with no error and no log line. The restored-state branch below has always set
                // Waiting, which is why scripts restored from state ran and freshly compiled ones
                // did not - the manhole script, and every new script.
                interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                var invItem = req.Prim.Inventory?.GetInventoryItem(req.ItemID);
                if (invItem != null && !invItem.ScriptRunning)
                {
                    // The item's Running flag is off - unticked in the viewer, llSetScriptState(FALSE), or a
                    // crash before this restart. Loaded and held: no state_entry until a reset or the checkbox.
                    interp.ScriptState.GeneralEnable = false;
                    lock (m_AllScriptsLock) m_HeldFresh.Add(req.ItemID);
                    m_log.LogInformation("[PhloxExe]: {0} loaded STOPPED (item Running flag off); no state_entry until reset or Running is ticked", req.ItemID);
                    EvaluateParcelRule(req.ItemID);   // So the owner starting it on disallowed land leaves it paused
                    return;
                }
                sysApi.OnFreshStart();   // Not a reset - the item's grant is the core's (CreateScriptInstance)
                PostEvent(req.ItemID, new PostedEvent
                {
                    EventType = SupportedEventList.Events.STATE_ENTRY,
                    Args = Array.Empty<object>()
                });
            }
           else
            {
                // Restored values decide what runs next; an exception from them stops this one script with its usual
                // error, and never reaches the loader or the scheduler loop.
                try
                {
                    sysApi.RestoreSavedGrant(restoredFromCarried);
                    sysApi.DropGrantRecordsWithoutGrant();
                    if (req.StateSource == (int)StateSource.RegionStart)
                    {
                        m_StartRestored++;
                        if (req.Prim.Inventory?.GetInventoryItem(req.ItemID)?.PermsMask is int mask && mask != 0)
                            m_StartRestoredGrants++;
                    }
                    // Resume where the script stopped, instead of forcing Waiting.
                    //
                    // The saved state carries RunState, Calls, TopFrame, RunningEvent, EventQueue and a
                    // relative NextWakeup, and all of it used to be restored and then thrown away by an
                    // unconditional `RunState = Waiting`. Nothing re-armed a sleep and nothing put a
                    // running script back on the run queue, so an interrupted handler simply never
                    // finished. Worse than never: the stale frame stays on the stack, so the next
                    // unrelated event pushes on top of it (RuntimeState.cs:335-336) and the interrupted
                    // handler resumes NESTED inside the new event, after it.
                    //
                    // The flush loop and StateManager.Stop() at shutdown save whatever is dirty (ScriptUnloaded
                    // is the OnRemoveScript path, not shutdown), so a script mid-llSleep when the
                    // region stopped was saved in exactly the state that never resumed.
                    // A script saved stopped (its Running flag off, or crashed) is restored frozen, as Halcyon's
                    // InjectScript did only `if (interp.ScriptState.Enabled)`: nothing goes on the run queue or the
                    // sleep heap and its timer is not armed. Its RunState, frame and timer's time left are kept for
                    // StartAfterStop, which carries on from them when Running is ticked.
                    bool enabled = interp.ScriptState.Enabled;
                    var restoredRunState = interp.ScriptState.RunState;
                    switch (restoredRunState)
                    {
                        case RuntimeState.Status.Running:
                            // Mid-event with time left on the clock. Put it back on the run
                            // queue and it continues from its own TopFrame.
                            if (enabled) AddToRunQueue(interp);
                            break;

                        case RuntimeState.Status.Sleeping:
                            // NextWakeup was already restored relative to now by
                            // SerializedRuntimeState.ToRuntimeState, so it is a tick value on this run's
                            // basis and can be tracked directly.
                            //
                            // This arm was once DELETED by an edit - the Running block was
                            // replaced by slicing from `case Running` to `case Syscall`, and Sleeping sat
                            // between them. A sleeping script then fell to `default` and was restored
                            // Waiting, so the script never resumed. The dispatch is
                            // on RunState ONLY; LastSyscallIndex is read inside the Syscall arm and
                            // nowhere else, whatever value it holds.
                            if (enabled) TrackSleep(interp, interp.ScriptState.NextWakeup);
                            break;

                        case RuntimeState.Status.Syscall:
                            // A syscall in flight when the region stopped has NO completion
                            // coming - whatever was going to call SysReturn died with the old process. So
                            // the only way back is to supply the return value ourselves, which is what
                            // LastSyscallIndex is persisted for. A stopped script gets the value now and
                            // waits as Running for its start.
                            ResumeFromSyscall(interp, req.ItemID, enabled);
                            break;

                        default:
                            interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                            break;
                    }

                    if (!interp.ScriptState.GeneralEnable)
                    {
                        // The row says stopped (a crash, or the checkbox) but the item came out of the region DB
                        // with its Running flag at the default, true - the DB never stores it. Push it off so the viewer's
                        // checkbox and `phlox status` agree with the state, and say why, as "loaded STOPPED" does.
                        m_Engine.SetItemRunningFlag(req.Prim.LocalId, req.ItemID, false);
                        m_log.LogInformation("[PhloxExe]: {0} restored STOPPED ({1}); no run until reset or Running is ticked",
                            req.ItemID, interp.ScriptState.TerminatedReason != null ? "terminated: " + interp.ScriptState.TerminatedReason : "Running flag off");
                    }

                    // A script saved while Waiting can still hold events on its OWN queue
                    // (ScriptState.EventQueue). ProcessEventQueue reads only m_PendingEvents, and that
                    // queue is drained by TransitionToWait - which runs only for a script already on the
                    // run queue. A restored script is on neither, so without this the queued events sit
                    // there for ever.
                    if (enabled && interp.ScriptState.RunState == RuntimeState.Status.Waiting)
                    {
                        bool hasQueued;
                        lock (interp.ScriptState.EventQueueLock)
                            hasQueued = interp.ScriptState.EventQueue != null && interp.ScriptState.EventQueue.Count > 0;
                        if (hasQueued)
                            DeliverNextQueuedEvent(interp);
                    }

                    // The timer keeps its phase: the next timer() comes after what was left of the interval when the
                    // state was captured, at once if that had run out (Halcyon InjectScript). A stopped script keeps
                    // that time for its start.
                    if (interp.ScriptState.TimerInterval > 0)
                    {
                        ulong left = RestoredTimerLeft(interp.ScriptState);
                        if (enabled) ResumeTimerWithTimeLeft(interp, left);
                        else m_StoppedTimerLeft[req.ItemID] = left;
                    }

                    // The listens the script held come back through this engine's listen manager with the handles the
                    // script was given (Halcyon OnScriptInjected -> Relisten). A stopped script keeps them registered,
                    // as a stop leaves them; what they hear is dropped until it starts.
                    RestoreListens(interp, req);

                    if (enabled)
                    {
                        bool fromCrossing = req.StateSource == (int)StateSource.PrimCrossing;
                        interp.OnScriptInjected(fromCrossing);
                    }
                }
                catch (Exception e)
                {
                    StopRestoredScript(interp, e);
                    return;
                }
            }

            // The start-up events, in SL's order: state_entry (a fresh script, posted above) or the restored queue,
            // then on_rez ("on_rez will be triggered prior to attach when attaching from inventory or during
            // login"), then attach, then the changed event the start brings (ArrivalChange), fresh or restored (YEngine
            // XMRInstCtor posts them in this order; Halcyon posts on_rez, then changed). A script restored in the middle
            // of an event or asleep gets them on its own queue, after that event.
            // An attachment worn from inventory gets attach from here only: the core starts its scripts with
            // AttachedRez and raises no OnAttach for it (AttachmentsModule.AttachObjectInternal).
            if (req.PostOnRez)
            {
                PostEvent(req.ItemID, new PostedEvent
                {
                    EventType = SupportedEventList.Events.ON_REZ,
                    Args = new object[] { interp.ScriptState.StartParameter }
                });
            }

            if (req.StateSource == (int)StateSource.AttachedRez &&
                req.Prim?.ParentGroup?.AttachedAvatar != UUID.Zero &&
                interp.Script.FindEvent(interp.ScriptState.LSLState,
                    (int)SupportedEventList.Events.ATTACH) != null)
            {
                PostEvent(req.ItemID, new PostedEvent
                {
                    EventType = SupportedEventList.Events.ATTACH,
                    Args = new object[] { req.Prim.ParentGroup.AttachedAvatar.ToString() }
                });
            }

            int arrivalChange = ArrivalChange(req);
            if (arrivalChange != 0 &&
                interp.Script.FindEvent(interp.ScriptState.LSLState,
                    (int)SupportedEventList.Events.CHANGED) != null)
            {
                SceneObjectGroup group = req.Prim.ParentGroup;
                if (req.StateSource == (int)StateSource.PrimCrossing && group != null && !group.IsAttachment
                    && GroupAwaitsRider(group, null))
                    m_HeldArrivals.Add(new HeldArrival
                    {
                        ItemId = req.ItemID,
                        Group = group,
                        Change = arrivalChange,
                        Deadline = InWorldz.Phlox.Util.Clock.Now + ArrivalRiderWaitMs
                    });
                else PostEvent(req.ItemID, new PostedEvent
                {
                    EventType = SupportedEventList.Events.CHANGED,
                    Args = new object[] { arrivalChange }
                });
            }

            // Inject any events that arrived before the script was loaded
            InjectDeferredEvents(interp);
            
            // Only add to run queue if fresh — restored scripts wait for events
            if (freshStart)
                AddToRunQueue(interp);

            // Every start (rez, region start, arrival, duplicate) is checked against the parcel. A script the
            // parcel does not allow starts paused - never refused - with everything above in place for its resume.
            EvaluateParcelRule(req.ItemID);

            m_WorkArrived();
        }

        /// <summary>
        /// The changed() bits a script's start brings, from the state source the core starts it with, or 0.
        /// <list type="bullet">
        /// <item>RegionStart: CHANGED_REGION_START, to every script ("The region this object is in has just come
        /// online").</item>
        /// <item>PrimCrossing (an object crossing, or a worn object crossing with its wearer) and Teleporting (worn objects
        /// arriving with a wearer who teleported): CHANGED_REGION, and CHANGED_TELEPORT for an attachment, as one event
        /// (YEngine XMRInstCtor: CHANGED_REGION | CHANGED_TELEPORT). SL wiki CHANGED_REGION: "The object has changed region
        /// by crossing a region boundary (or by teleporting, if attached). This event only occurs in the root prim of a
        /// linkset"; CHANGED_TELEPORT: "This event only occurs in the root prim of an attachment". So a script in a child
        /// prim gets neither. The teleport bit goes to an attachment only: the core also starts an unworn object's
        /// scripts as Teleporting when the object's owner is in the region and arrived there by teleport
        /// (EntityTransferModule.GetStateSource asks the owner's presence for any object).</item>
        /// </list>
        /// A teleport within a region restores nothing; the core posts its CHANGED_TELEPORT itself
        /// (EntityTransferModule.TeleportAgentWithinRegion).
        /// </summary>
        private static int ArrivalChange(PhloxLoadRequest req)
        {
            const int CHANGED_REGION = 0x100, CHANGED_TELEPORT = 0x200, CHANGED_REGION_START = 0x400;
            switch ((StateSource)req.StateSource)
            {
                case StateSource.RegionStart:
                    return CHANGED_REGION_START;

                case StateSource.PrimCrossing:
                case StateSource.Teleporting:
                    if (req.Prim == null || !req.Prim.IsRoot) return 0;
                    bool worn = req.Prim.ParentGroup?.IsAttachment == true;
                    return req.StateSource == (int)StateSource.Teleporting && worn
                        ? CHANGED_REGION | CHANGED_TELEPORT
                        : CHANGED_REGION;

                default:
                    return 0;
            }
        }

        // ── The region start's restore summary ─────────────────────────────────

        // Scripts the region's start restored from their saved state, and those of them given a permission grant back.
        // Scheduler thread only.
        private int m_StartRestored, m_StartRestoredGrants;

        /// <summary>
        /// One line once the region's start has loaded its scripts: how many came back with their saved state, and how
        /// many of those got a permission grant back (<see cref="LSLSystemAPI.RestoreSavedGrant"/>). Master scheduler
        /// thread, from the loader.
        /// </summary>
        internal void WriteStartSummary()
        {
            m_log.LogInformation("[PhloxExe]: Region start: {0} scripts restored, {1} with a permission grant put back",
                m_StartRestored, m_StartRestoredGrants);
        }

        // ── An arrival event that waits for riders ─────────────────────────────

        /// <summary>
        /// How long a crossing object's changed(CHANGED_REGION) waits for riders still to come, in milliseconds. Halcyon
        /// waits with no limit (Scene.cs AddSceneObjectFromOtherRegion posts it only when AvatarsToExpect is 0;
        /// ScenePresence.ContinueSitAsRootAgent posts it when the last rider arrives), so a rider who never arrives
        /// leaves the script with no event. Here the wait ends after this long: the core sends the riders after the
        /// object, a far crossing a second later (SceneObjectGroup.CrossAsync), and a rider is in the region only once
        /// the viewer has completed the move; ten seconds leaves room for a slow link while an object holding a grant
        /// from someone who is not coming gets its event late by no more than that.
        /// </summary>
        internal const ulong ArrivalRiderWaitMs = 10_000;

        private sealed class HeldArrival
        {
            public UUID ItemId;
            public SceneObjectGroup Group;
            public int Change;
            public ulong Deadline;
            public readonly HashSet<UUID> Arrived = new();
        }

        // Scheduler thread only (FinishedLoading and DoWork both run on it).
        private readonly List<HeldArrival> m_HeldArrivals = new();

        /// <summary>
        /// Does a script in <paramref name="group"/> hold a grant waiting for an avatar to arrive on the object (not one
        /// from an Experience, which needs no seat), from an avatar not in <paramref name="arrived"/>? The core crosses
        /// the object before its riders (SceneObjectGroup.CrossAsync) and tells the new region nothing of
        /// them, so such a grant is the sign that a rider is still to come.
        /// </summary>
        private bool GroupAwaitsRider(SceneObjectGroup group, HashSet<UUID> arrived)
        {
            foreach (SceneObjectPart part in group.Parts)
                foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                    if (m_Apis.TryGetValue(item.ItemID, out LSLSystemAPI api)
                        && api.WaitingSeatGranter is UUID granter && granter.IsNotZero()
                        && (arrived == null || !arrived.Contains(granter)))
                        return true;
            return false;
        }

        /// <summary>
        /// A crossing object's changed(CHANGED_REGION) waits until the riders it expects have arrived and their waiting
        /// grants are decided, so the script sees their grant inside the event, as Halcyon posts it once the last rider is
        /// seated (ScenePresence.ContinueSitAsRootAgent). An avatar's arrival counts once it has been handled
        /// (ProcessArrivedAvatars), seated on the object or not. The wait ends after <see cref="ArrivalRiderWaitMs"/>
        /// whatever has arrived; the event is posted once. The script runs its other events meanwhile.
        /// </summary>
        private void ProcessHeldArrivals()
        {
            if (m_HeldArrivals.Count == 0) return;
            ulong now = InWorldz.Phlox.Util.Clock.Now;
            for (int i = m_HeldArrivals.Count - 1; i >= 0; i--)
            {
                HeldArrival held = m_HeldArrivals[i];
                if (!m_AllScripts.ContainsKey(held.ItemId) || held.Group.IsDeleted)
                {
                    m_HeldArrivals.RemoveAt(i);
                    continue;
                }
                if (now < held.Deadline && GroupAwaitsRider(held.Group, held.Arrived)) continue;
                m_HeldArrivals.RemoveAt(i);
                PostEvent(held.ItemId, new PostedEvent
                {
                    EventType = SupportedEventList.Events.CHANGED,
                    Args = new object[] { held.Change }
                });
            }
        }

        private ulong EarliestHeldArrival()
        {
            ulong earliest = ulong.MaxValue;
            foreach (HeldArrival held in m_HeldArrivals) earliest = Math.Min(earliest, held.Deadline);
            return earliest;
        }

        // ── Event posting ──────────────────────────────────────────────────────

        public void PostEvent(UUID itemId, PostedEvent evt)
        {
            lock (m_PendingEvents)
                m_PendingEvents.Enqueue(new PendingEvent { ItemId = itemId, Evt = evt });
            m_WorkArrived();
        }

        /// <summary>
        /// A grab update (a touch() with the latest detect data). In order with the prim's other events, so it meets the
        /// touch_start before it; FoldGrabUpdate decides on the scheduler thread whether it is an event or only data.
        /// </summary>
        public void PostGrabUpdate(UUID itemId, PostedEvent evt)
        {
            lock (m_PendingEvents)
                m_PendingEvents.Enqueue(new PendingEvent { ItemId = itemId, Evt = evt, GrabUpdate = true });
            m_WorkArrived();
        }

        // ── Enable / disable / reset ───────────────────────────────────────────

        public void ChangeEnabledStatus(UUID itemId, bool enable)
        {
            lock (m_EnableDisableQueue)
                m_EnableDisableQueue.Enqueue(new EnableDisableReq { ItemId = itemId, Enable = enable });
            m_WorkArrived();
        }

        public void ResetScript(UUID itemId)
        {
            lock (m_PendingResets)
                m_PendingResets.Enqueue(itemId);
            m_WorkArrived();
        }

        // ── Transient suspend/resume (estate live-ops tool) ────────────────────

        /// <summary>
        /// Request a transient suspend. Returns false if this scheduler doesn't run the
        /// script (lets a multi-engine caller try the next engine). Applied on the
        /// scheduler thread in ProcessSuspendResume.
        /// </summary>
        public bool RequestSuspend(UUID itemId)
        {
            lock (m_AllScriptsLock)
                if (!m_AllScripts.ContainsKey(itemId)) return false;
            lock (m_SuspendResumeQueue)
                m_SuspendResumeQueue.Enqueue(new SuspendResumeReq { ItemId = itemId, Suspend = true });
            m_WorkArrived();
            return true;
        }

        /// <summary>
        /// Request a resume. Unknown/non-suspended scripts are a cheap no-op — callers
        /// like SceneObjectPartInventory.ResumeScripts() invoke this for every script on
        /// every rez/deed, so the known-script check here keeps that path from queueing.
        /// </summary>
        public void RequestResume(UUID itemId)
        {
            lock (m_AllScriptsLock)
                if (!m_AllScripts.ContainsKey(itemId)) return;
            lock (m_SuspendResumeQueue)
                m_SuspendResumeQueue.Enqueue(new SuspendResumeReq { ItemId = itemId, Suspend = false });
            m_WorkArrived();
        }

        public bool ResetNow(UUID itemId)
        {
            Interpreter script;
            if (!m_AllScripts.TryGetValue(itemId, out script)) return false;

            UnregisterFromNotifications(script);
            DropPendingEvents(itemId);   // SL llResetScript "The event queue is cleared" - posted, not yet queued, too
            m_Engine.StateManager?.DeleteState(itemId);
            bool wasCrashed = script.ScriptState.TerminatedReason != null;
            lock (m_AllScriptsLock) m_HeldFresh.Remove(itemId);   // A reset of a held script owes it nothing more
            script.Reset();
            if (wasCrashed)
            {
                // A reset is how a crashed script comes back - fresh, and running again
                script.ScriptState.TerminatedReason = null;
                script.ScriptState.GeneralEnable = true;
                m_Engine.SetItemRunningFlag(script.HostLocalId, itemId, true);
            }
            script.SetScriptEventFlags();

            PostEvent(itemId, new PostedEvent
            {
                EventType = SupportedEventList.Events.STATE_ENTRY,
                Args = Array.Empty<object>()
            });

            if (!m_RunIndex.ContainsKey(itemId) && script.ScriptState.Enabled)
            {
                // The reset throttle (LSLSystemAPI.OnScriptReset) may have put the fresh script to sleep; it
                // wakes and runs state_entry then. Inside the script's own slice CheckRunstateChange does the same.
                if (script.ScriptState.RunState == RuntimeState.Status.Sleeping)
                    TrackSleep(script, script.ScriptState.NextWakeup);
                else
                    AddToRunQueue(script);
            }

            return true;
        }

        /// <summary>Events posted to this item and not yet moved into its queue are dropped (reset).</summary>
        private void DropPendingEvents(UUID itemId)
        {
            m_HeldArrivals.RemoveAll(held => held.ItemId == itemId);
            lock (m_PendingEvents)
            {
                if (m_PendingEvents.Count == 0) return;
                var keep = new List<PendingEvent>(m_PendingEvents.Count);
                foreach (var pe in m_PendingEvents)
                {
                    if (pe.ItemId == itemId) pe.Evt.SignalCompleted();   // No waiter waits for a dropped event
                    else keep.Add(pe);
                }
                if (keep.Count == m_PendingEvents.Count) return;
                m_PendingEvents.Clear();
                foreach (var pe in keep) m_PendingEvents.Enqueue(pe);
            }
        }

        /// <summary>
        /// The Running checkbox and llGetScriptState: the owner's setting. A pause by the parcel is not the
        /// owner's, so it does not untick the box; any other hold (StateLoadFailed, CrossingWait) still does.
        /// </summary>
        public bool GetScriptRunning(UUID itemId)
        {
            Interpreter script;
            // Asked from region threads (the viewer's Running box, llGetScriptState) while this thread adds and
            // removes scripts: the read takes the lock the writes take.
            lock (m_AllScriptsLock)
                if (!m_AllScripts.TryGetValue(itemId, out script)) return false;
            var st = script.ScriptState;
            return st.GeneralEnable && (st.LocalDisable & ~RuntimeState.LocalDisableFlag.Parcel) == RuntimeState.LocalDisableFlag.None;
        }

        /// <summary>Is this script paused by the parcel rule?</summary>
        internal bool IsParcelPaused(UUID itemId)
        {
            lock (m_AllScriptsLock)
                return m_AllScripts.TryGetValue(itemId, out Interpreter s)
                       && (s.ScriptState.LocalDisable & RuntimeState.LocalDisableFlag.Parcel) != 0;
        }

        /// <summary>Is the script loaded here? Safe from any thread.</summary>
        internal bool IsLoaded(UUID itemId)
        {
            lock (m_AllScriptsLock) return m_AllScripts.ContainsKey(itemId);
        }

        /// <summary>Safe from any thread: takes the lock this thread's adds and removes take.</summary>
        public Interpreter FindScript(UUID itemId)
        {
            lock (m_AllScriptsLock)
            {
                m_AllScripts.TryGetValue(itemId, out var s);
                return s;
            }
        }

        // Per-script stats snapshot for the estate Top Scripts report. Taken under the
        // m_AllScripts lock so it is safe to enumerate from the estate-request thread.
        internal struct ScriptStatSnapshot
        {
            public uint HostLocalId;
            public double ExecMs;
            public int MemoryUsed;
            public bool Enabled;
        }

        internal List<ScriptStatSnapshot> SnapshotScriptStats()
        {
            List<ScriptStatSnapshot> list;
            lock (m_AllScriptsLock)
            {
                list = new List<ScriptStatSnapshot>(m_AllScripts.Count);
                foreach (Interpreter interp in m_AllScripts.Values)
                {
                    list.Add(new ScriptStatSnapshot
                    {
                        HostLocalId = interp.HostLocalId,
                        ExecMs = interp.GetExecutionTime(),
                        MemoryUsed = interp.ScriptState.MemInfo.MemoryUsed,
                        Enabled = interp.ScriptState.Enabled
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// A read-only view of one script for the console. Everything the last four
        /// sessions had to reach for with a debugger or infer from silence - what state the script
        /// is in, whether anything is queued for it, and what the region thinks it handles.
        /// </summary>
        internal struct ScriptStatus
        {
            public bool Found;
            public UUID ItemId;
            public uint HostLocalId;
            public string RunState;
            public bool Enabled;
            public bool GeneralEnable;
            public bool Suspended;
            public int QueuedEvents;
            public int LslState;
            public int TimerIntervalMs;
            public ulong EventMask;
            public string PendingSyscall;
            /// <summary>Why the simulator holds it, if it does (e.g. StateLoadFailed).</summary>
            public string LocalDisable;
            /// <summary>The error a crashed script stopped on, or null.</summary>
            public string TerminatedReason;
        }

        /// <summary>Test seam: is this script on the run queue right now?</summary>
        internal bool IsOnRunQueue(UUID itemId)
        {
            lock (m_AllScriptsLock) return m_RunIndex.ContainsKey(itemId);
        }

        internal ScriptStatus GetStatus(UUID itemId)
        {
            lock (m_AllScriptsLock)
            {
                if (!m_AllScripts.TryGetValue(itemId, out Interpreter interp))
                    return new ScriptStatus { Found = false, ItemId = itemId };

                var st = interp.ScriptState;
                int queued;
                lock (st.EventQueueLock) queued = st.EventQueue.Count;

                return new ScriptStatus
                {
                    Found = true,
                    ItemId = itemId,
                    HostLocalId = interp.HostLocalId,
                    RunState = st.RunState.ToString(),
                    Enabled = st.Enabled,
                    GeneralEnable = st.GeneralEnable,
                    Suspended = m_Suspended.Contains(itemId),
                    QueuedEvents = queued,
                    LslState = st.LSLState,
                    TimerIntervalMs = st.TimerInterval,
                    EventMask = 0,
                    // Which syscall the script is sitting in. One word, and it is the
                    // difference between "RunState=Syscall" telling you nothing and telling you
                    // everything - this is what made the manhole a half-hour trace instead of a
                    // five-minute one.
                    PendingSyscall = st.RunState == RuntimeState.Status.Syscall
                        ? DescribeCurrentSyscall(interp) : null,
                    LocalDisable = st.LocalDisable == RuntimeState.LocalDisableFlag.None ? null : st.LocalDisable.ToString(),
                    TerminatedReason = st.TerminatedReason,
                };
            }
        }

        /// <summary>Every item this scheduler holds, for a whole-object status listing.</summary>
        /// <summary>
        /// The built-in the script is currently inside, by name. The interpreter's instruction
        /// pointer sits just past the syscall opcode, so the operand it carries is the function's
        /// TableIndex; that is looked back up in the table it was emitted from.
        /// </summary>
        private static string DescribeCurrentSyscall(Interpreter interp)
        {
            try
            {
                int idx = interp.ScriptState.LastSyscallIndex;
                if (idx < 0) return "(unknown)";
                foreach (var sig in InWorldz.Phlox.Types.Defaults.AllMethods)
                    if (sig.TableIndex == idx) return sig.FunctionName;
                return "(index " + idx + ")";
            }
            catch { return "(unknown)"; }
        }

        internal List<UUID> AllItemIds()
        {
            lock (m_AllScriptsLock) return new List<UUID>(m_AllScripts.Keys);
        }

        // ── Syscall returns ────────────────────────────────────────────────────

        public void PostSyscallReturn(UUID itemId, object retValue, int delay)
            => PostSyscallReturn(itemId, retValue, delay, -1, null);

        /// <summary>A return carrying the call's sequence number, and a fault if the body threw.</summary>
        public void PostSyscallReturn(UUID itemId, object retValue, int delay, int seq, Exception fault)
        {
            lock (m_SyscallReturns)
                m_SyscallReturns.Enqueue(new SyscallReturn { ItemId = itemId, RetValue = retValue, Delay = delay, Seq = seq, Fault = fault });
            m_WorkArrived();
        }

        // ── Service lane ───────────────────────────────────────────────

        /// <summary>Called by a shim on this scheduler's thread, inside the script's tick.</summary>
        private void DeferServiceCall(DeferredServiceCall call)
        {
            call.Deadline = InWorldz.Phlox.Util.Clock.Now + (ulong)ServiceCallTimeoutMs;
            m_OutstandingServiceCalls.Add(call);
            m_ServiceQueue.Enqueue(call);
            EnsureServiceThread();
            m_ServiceSignal.Release();
        }

        private void EnsureServiceThread()
        {
            if (Volatile.Read(ref m_ServiceThreadsIdle) > 0) return;
            while (true)
            {
                int cur = Volatile.Read(ref m_ServiceThreadCount);
                if (cur >= ServiceCallThreads) return;
                if (Interlocked.CompareExchange(ref m_ServiceThreadCount, cur + 1, cur) != cur) continue;
                var t = new Thread(ServiceLoop)
                {
                    IsBackground = true,
                    Name = "Phlox service " + (m_Engine?.World?.RegionInfo?.RegionName ?? "?") + " #" + Interlocked.Increment(ref m_ServiceThreadSeq),
                };
                t.Start();
                return;
            }
        }

        private void ServiceLoop()
        {
            try
            {
                while (true)
                {
                    Interlocked.Increment(ref m_ServiceThreadsIdle);
                    bool signalled;
                    try { signalled = m_ServiceSignal.Wait(ServiceThreadIdleMs); }
                    finally { Interlocked.Decrement(ref m_ServiceThreadsIdle); }
                    if (!signalled)
                    {
                        if (m_ServiceQueue.IsEmpty) break;   // idle long enough: give the thread back
                        continue;
                    }
                    if (m_ServiceQueue.TryDequeue(out DeferredServiceCall call))
                        RunServiceCall(call);
                }
            }
            finally
            {
                Interlocked.Decrement(ref m_ServiceThreadCount);
                // A call can land between the empty check and the decrement; never strand it.
                if (!m_ServiceQueue.IsEmpty) EnsureServiceThread();
            }
        }

        private void RunServiceCall(DeferredServiceCall call)
        {
            // The deadline already answered the script: do not perform the call after the fact.
            if (call.IsFinished) return;

            var ctx = call.Context;
            object result = null;
            Exception fault = null;
            ctx.Enter();
            try { result = call.Body(); }
            catch (Exception e) { fault = e; }
            finally { SyscallContext.Exit(); }

            if (!call.TryFinish())
            {
                m_log.LogInformation("[PhloxExe]: {0} for {1} answered after its {2} ms deadline; the late result is dropped",
                    call.FunctionName, ctx.ItemId, ServiceCallTimeoutMs);
                return;
            }
            PostSyscallReturn(ctx.ItemId, fault == null ? result : null, ctx.DelayMs, ctx.Seq, fault);
        }

        /// <summary>Scheduler thread: answer every call past its deadline with its failure value.</summary>
        private void ProcessServiceDeadlines()
        {
            if (m_OutstandingServiceCalls.Count == 0) return;
            ulong now = InWorldz.Phlox.Util.Clock.Now;
            for (int i = m_OutstandingServiceCalls.Count - 1; i >= 0; i--)
            {
                var call = m_OutstandingServiceCalls[i];
                if (call.IsFinished) { m_OutstandingServiceCalls.RemoveAt(i); continue; }
                if (now < call.Deadline) continue;
                if (call.TryFinish())
                {
                    m_log.LogWarning("[PhloxExe]: {0} for {1} did not answer in {2} ms; the script resumes with the call's failure value and a late answer will be dropped",
                        call.FunctionName, call.Context.ItemId, ServiceCallTimeoutMs);
                    PostSyscallReturn(call.Context.ItemId, call.FailureValue, 0, call.Context.Seq, null);
                }
                m_OutstandingServiceCalls.RemoveAt(i);
            }
        }

        private ulong EarliestServiceDeadline()
        {
            ulong min = ulong.MaxValue;
            foreach (var c in m_OutstandingServiceCalls)
                if (!c.IsFinished && c.Deadline < min) min = c.Deadline;
            return min;
        }

        /// <summary>Diagnostics/tests: service threads alive right now.</summary>
        internal int ServiceThreadCount => Volatile.Read(ref m_ServiceThreadCount);

        // ── Timer ──────────────────────────────────────────────────────────────

        public void SetTimer(UUID itemId, float sec)
        {
            Interpreter script;
            if (!m_AllScripts.TryGetValue(itemId, out script)) return;

            script.ScriptState.TimerInterval = (int)(sec * 1000);
            m_ParcelTimerLeft.Remove(itemId);   // A new timer replaces the time a parcel pause kept

            // Remove any existing timer handle, and any timer event already queued. A timer that fired while the
            // script was busy has no handle any more (the wake removed it) but its event still waits in the queue;
            // Halcyon ScriptSetTimer removes that one when there is no handle. SL: "Setting sec to 0.0 stops the timer".
            C5.IPriorityQueueHandle<SleepEntry> existing;
            if (m_TimerHandles.TryGetValue(itemId, out existing))
            {
                m_TimerHandles.Remove(itemId);
                m_SleepHeap.Delete(existing);
            }
            script.ScriptState.RemovePendingTimerEvent();

            if (script.ScriptState.TimerInterval > 0)
            {
                ulong readyOn = InWorldz.Phlox.Util.Clock.Now + (ulong)script.ScriptState.TimerInterval;
                TrackTimer(script, readyOn, false);
            }
        }

        // ── Unload ────────────────────────────────────────────────────────────

        internal void DoUnload(UUID itemId)
        {
            Interpreter script;
            if (!m_AllScripts.TryGetValue(itemId, out script)) return;

            bool itemLeftPrim = ItemLeftItsPrim(itemId);
            RemoveFromRunQueue(itemId);
            m_Suspended.Remove(itemId);
            UnregisterFromNotifications(script);
            // Whatever the API's unload hook throws, the script still leaves the scheduler - a throw here used to
            // abort the unload and keep the script (and everything it held) loaded for the life of the region.
            try { script.OnUnload(ScriptUnloadReason.Unloaded, RuntimeState.LocalDisableFlag.None); }
            catch (Exception e) { m_log.LogError(e, "[PhloxExe]: unload hook of {0} failed; unloading it anyway", itemId); }
            // The row stays until state travels with objects (a derez, take or crossing may bring the item back with the
            // same id), except when the item itself left a prim that is still here: that row can never be restored.
            if (itemLeftPrim) m_Engine.StateManager?.QueueUnloadDelete(itemId);
            else m_Engine.StateManager?.QueueUnloadSave(script);
            lock (m_AllScriptsLock)
            {
                m_AllScripts.Remove(itemId);
                m_HeldFresh.Remove(itemId);
            }
            m_Apis.Remove(itemId);
            m_ControlsExempt.Remove(itemId);
        }

        /// <summary>
        /// The script item was taken out of its prim's inventory (deleted, moved, given away) and the prim is still in the
        /// scene: the core removes the item before it raises OnRemoveScript (SceneObjectPartInventory.RemoveInventoryItem).
        /// A derez or crossing leaves the item where it is, in a group that is being deleted.
        /// </summary>
        private bool ItemLeftItsPrim(UUID itemId)
        {
            if (!m_Apis.TryGetValue(itemId, out LSLSystemAPI api)) return false;
            SceneObjectPart part = api.HostPart;
            SceneObjectGroup group = part?.ParentGroup;
            if (group == null || group.IsDeleted || part.Inventory == null) return false;
            return part.Inventory.GetInventoryItem(itemId) == null;
        }

        // ── Main work loop ─────────────────────────────────────────────────────

        /// <summary>The managed thread id of whoever last drove DoWork - the script thread.
        /// A region-side wait on a script event must never block this thread.</summary>
        public int WorkerThreadId { get; private set; } = -1;

        private StateManager m_AttachedStateManager;

        /// <summary>The engine's [InWorldz.Phlox] section, for the parts built with this scheduler (the listen manager).</summary>
        internal Nini.Config.IConfig EngineConfig => m_Engine?.Config;

        // The script in a timeslice now, and whether the scheduler is halted: a stop that did not end the scheduler's thread
        // in time halts it, and a halted scheduler starts no further timeslice or pass.
        private readonly object m_SliceLock = new object();
        private Interpreter m_InSlice;
        private bool m_Halted;

        /// <summary>
        /// The scheduler runs no further script: no new pass, no new timeslice. Returns the script in a timeslice now, which
        /// keeps running until its slice ends, or null. For a final save whose scheduler thread did not stop.
        /// </summary>
        internal Interpreter Halt()
        {
            lock (m_SliceLock)
            {
                m_Halted = true;
                return m_InSlice;
            }
        }

        public WorkStatus DoWork()
        {
            lock (m_SliceLock)
                if (m_Halted) return new WorkStatus { NextWakeUpTime = ulong.MaxValue };
            WorkerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            // Script state is captured here, between timeslices, when the state thread asks for it (Halcyon
            // ExecutionScheduler.RequestStateData); the state thread only writes what it is handed.
            var stateManager = m_Engine?.StateManager;
            if (stateManager != null)
            {
                if (!ReferenceEquals(m_AttachedStateManager, stateManager))
                {
                    stateManager.AttachScheduler(() => m_WorkArrived?.Invoke());
                    m_AttachedStateManager = stateManager;
                }
                stateManager.CaptureRequestedStates();
            }
            ProcessObjectStateRequests();
            ProcessArrivedAvatars();
            ProcessExperienceLandChecks();
            ProcessHeldArrivals();   // After the arrivals, whose waiting grants it waits for
            CheckSleepingScripts();
            ProcessEventQueue();
            ExpireDeferredEvents();
            ProcessPermsEnds();      // Before the parcel checks it may call for
            ProcessParcelChecks();
            ProcessEnableDisable();
            ProcessSuspendResume();
            ProcessResets();
            ProcessServiceDeadlines();
            ProcessSyscallReturns();

            bool hadRunnable = m_NextScript != null;
            DoTimeslices();

            ulong nextWake = m_SleepHeap.Count > 0 ? m_SleepHeap.FindMin().ReadyOn : ulong.MaxValue;
            return new WorkStatus
            {
                WorkWasDone = hadRunnable,
                WorkIsPending = HasWork(),
                NextWakeUpTime = Math.Min(Math.Min(nextWake, EarliestServiceDeadline()), EarliestHeldArrival())
            };
        }

        // The service-lane threads need no stop - they are background threads that give
        // themselves back after ServiceThreadIdleMs with nothing to do.
        internal void Stop() { }

        // ── Private helpers ────────────────────────────────────────────────────

        private bool HasWork()
        {
            if (m_RunQueue.Count > 0) return true;
            lock (m_PendingEvents) if (m_PendingEvents.Count > 0) return true;
            lock (m_EnableDisableQueue) if (m_EnableDisableQueue.Count > 0) return true;
            lock (m_ParcelChecks) if (m_ParcelChecks.Count > 0) return true;
            lock (m_PermsEnds) if (m_PermsEnds.Count > 0) return true;
            lock (m_ObjectStateRequests) if (m_ObjectStateRequests.Count > 0) return true;
            lock (m_ArrivedAvatars) if (m_ArrivedAvatars.Count > 0) return true;
            lock (m_ExperienceLandChecks) if (m_ExperienceLandChecks.Count > 0) return true;
            lock (m_SuspendResumeQueue) if (m_SuspendResumeQueue.Count > 0) return true;
            lock (m_PendingResets) if (m_PendingResets.Count > 0) return true;
            lock (m_SyscallReturns) if (m_SyscallReturns.Count > 0) return true;
            return false;
        }

        private void DoTimeslices()
        {
            int iterations = 0;
            while (m_NextScript != null && iterations < INSTRUCTION_FREQUENCY)
            {
                var followingScript = m_NextScript.Next;
                var currentNode = m_NextScript;
                var current = currentNode.Value;
                int ticks = 0;
                bool terminated = false;
                lock (m_SliceLock)
                {
                    if (m_Halted) return;
                    m_InSlice = current;
                }
                try
                {
                    m_SliceWatch.Restart();
                    while (ticks < SCRIPT_TIMESLICE)
                    {
                        // A script put to sleep while it sat on the run queue (the reset throttle, on a reset from
                        // outside its own slice) goes to the sleep heap untouched - its first opcode would overwrite the sleep.
                        if (m_NextScript.Value.ScriptState.RunState == RuntimeState.Status.Sleeping)
                        {
                            CheckRunstateChange();
                            break;
                        }
                        try { m_NextScript.Value.Tick(); }
                        catch (Exception e)
                        {
                            // TerminateWithError marks the script Killed, but this path broke out of
                            // the timeslice WITHOUT the Killed arm of CheckRunstateChange, so the node stayed on
                            // the run queue and the next pass ticked the dead script again on a torn operand
                            // stack - the "Unable to cast" and "Stack empty" stops that followed every OSSL
                            // denial. Off the queue here, once.
                            TerminateWithError(m_NextScript.Value, e);
                            m_RunIndex.Remove(m_NextScript.Value.ItemId);
                            m_RunQueue.Remove(m_NextScript);
                            terminated = true;
                        }

                        iterations++;
                        ticks++;

                        if (terminated || CheckRunstateChange()) break;
                    }
                    m_SliceWatch.Stop();

                    // Every timeslice marks the script for saving (Halcyon RunNextScript: "tell our state manager that we
                    // changed"), so one in a long loop, an llSleep or a blocking call is saved as it is now. A crash marked
                    // itself in TerminateWithError.
                    if (!terminated) m_Engine?.StateManager?.ScriptChanged(current);

                    // Guard against null after termination/removal
                    if (!terminated && m_NextScript != null && m_NextScript == currentNode)
                    {
                        m_NextScript.Value.AddExecutionTime(m_SliceWatch.Elapsed.TotalMilliseconds);

                        if (m_SliceWatch.Elapsed.TotalMilliseconds >= SLOW_THRESH_MS)
                            m_log.LogWarning("[PhloxExe]: Slow timeslice for {0} ({1:F1}ms)",
                                m_NextScript.Value.Script.AssetId, m_SliceWatch.Elapsed.TotalMilliseconds);
                    }
                }
                finally
                {
                    lock (m_SliceLock) m_InSlice = null;
                }

                m_NextScript = followingScript ?? m_RunQueue.First;
            }
        }

        private bool CheckRunstateChange()
        {
            if (m_NextScript.Value.ScriptState.RunState == RuntimeState.Status.Running) return false;

            switch (m_NextScript.Value.ScriptState.RunState)
            {
                case RuntimeState.Status.Sleeping:
                    var sleeper = m_NextScript.Value;
                    RemoveFromRunQueue(sleeper.ItemId);
                    TrackSleep(sleeper, sleeper.ScriptState.NextWakeup);
                    break;

                case RuntimeState.Status.Waiting:
                    TransitionToWait();
                    break;

                case RuntimeState.Status.Killed:
                    m_RunIndex.Remove(m_NextScript.Value.ItemId);
                    m_RunQueue.Remove(m_NextScript);
                    break;

                case RuntimeState.Status.Syscall:
                    m_RunQueue.Remove(m_NextScript);
                    m_RunIndex.Remove(m_NextScript.Value.ItemId);
                    break;
            }
            return true;
        }

        private void TransitionToWait()
        {
            Interpreter script = m_NextScript.Value;
            script.ScriptState.RunningEvent?.SignalCompleted();
            script.ScriptState.RunningEvent = null;

            while (true)
            {
                PostedEvent nextEvt;
                lock (script.ScriptState.EventQueueLock)
                {
                    if (script.ScriptState.EventQueue.Count == 0) break;
                    nextEvt = script.ScriptState.EventQueue.Dequeue();
                }
                PhloxEventInfo info = FindEventHandler(nextEvt, script);
                CheckAndResetTouchWait(script, nextEvt);
                if (info != null)
                {
                    // The floor applies to a queued start as much as a fresh one. Put
                    // the event back at the front, arm the wake, and let the script go idle.
                    if (MinDelayHolds(script))
                    {
                        lock (script.ScriptState.EventQueueLock)
                            script.ScriptState.EventQueue.InsertFirst(nextEvt);
                        TrackMinDelayWake(script);
                        break;
                    }
                    try
                    {
                        ArmMinDelay(script);
                        script.ScriptState.DoEvent(info, nextEvt, nextEvt.Args);
                        CheckAndResetTimer(script, info);
                        return; // stay on run queue
                    }
                    catch (Exception e)   // not only VMException: restored values can be wrong in ways the VM does not check
                    {
                        TerminateWithError(script, e);
                        m_RunIndex.Remove(m_NextScript.Value.ItemId);
                        m_RunQueue.Remove(m_NextScript);
                        return;
                    }
                }
            }

			m_Engine.StateManager?.ScriptChanged(script);
            m_RunIndex.Remove(m_NextScript.Value.ItemId);
            m_RunQueue.Remove(m_NextScript);
        }

        private void CheckSleepingScripts()
        {
            ulong now = InWorldz.Phlox.Util.Clock.Now;
            while (m_SleepHeap.Count > 0)
            {
                SleepEntry s = m_SleepHeap.FindMin();
                if (now < s.ReadyOn) break;

                m_SleepHeap.DeleteMin();

                switch (s.Event)
                {
                    case SleepEntry.WakeEvent.None: m_StdSleepHandles.Remove(s.ItemId); break;
                    case SleepEntry.WakeEvent.Timer: m_TimerHandles.Remove(s.ItemId); break;
                    case SleepEntry.WakeEvent.Touch: m_TouchHandles.Remove(s.ItemId); break;
                    case SleepEntry.WakeEvent.MinDelay: m_MinDelayHandles.Remove(s.ItemId); break;
                }

                Interpreter script;
                if (!m_AllScripts.TryGetValue(s.ItemId, out script)) continue;
                if (!script.ScriptState.Enabled) continue;

                switch (s.Event)
                {
                    case SleepEntry.WakeEvent.None:
                        AddToRunQueue(script);
                        break;
                    case SleepEntry.WakeEvent.Timer:
                        PostEvent(s.ItemId, new PostedEvent
                        {
                            EventType = SupportedEventList.Events.TIMER,
                            Args = Array.Empty<object>()
                        });
                        break;
                    case SleepEntry.WakeEvent.MinDelay:
                        // The floor has elapsed; if the script is idle with events it was
                        // made to hold, start the next one now.
                        if (script.ScriptState.RunState == RuntimeState.Status.Waiting)
                        {
                            bool queued;
                            lock (script.ScriptState.EventQueueLock)
                                queued = script.ScriptState.EventQueue != null && script.ScriptState.EventQueue.Count > 0;
                            if (queued) DeliverNextQueuedEvent(script);
                        }
                        break;
                    case SleepEntry.WakeEvent.Touch:
                        if (script.ScriptState.TouchActive)
                            PostEvent(s.ItemId, new PostedEvent
                            {
                                EventType = SupportedEventList.Events.TOUCH,
                                Args = new object[] { 1 },
                                DetectVars = script.ScriptState.CurrentTouchDetectVars
                            });
                        break;
                }
            }
        }

        // ── touch() repeat while held ───────────────────────────────────────────
        //
        // SL touch(): "Triggered on touch start, each minimum event delay while held, and touch end." Halcyon's scheduler
        // (ExecutionScheduler.CheckAndResetTouchWait, ResetTouchInterval, ProcessTouchInfoUpdates): touch_start starts a
        // touch() every 100 ms for a script whose state has a touch() handler, carrying the latest detect data; a grab
        // update only replaces that data; touch_end stops it.

        private static bool HandlesTouch(Interpreter script)
            => script.Script.FindEvent(script.ScriptState.LSLState, (int)SupportedEventList.Events.TOUCH) != null;

        /// <summary>Called for every event a script is offered, before its handler is looked at, as Halcyon did.</summary>
        private void CheckAndResetTouchWait(Interpreter script, PostedEvent evt)
        {
            RuntimeState st = script.ScriptState;
            switch (evt.EventType)
            {
                case SupportedEventList.Events.TOUCH_START:
                    if (!HandlesTouch(script)) return;
                    st.TouchActive = true;
                    st.CurrentTouchDetectVars = evt.DetectVars;
                    ResetTouchInterval(script);
                    break;
                case SupportedEventList.Events.TOUCH_END:
                    if (!st.TouchActive) return;
                    st.TouchActive = false;
                    st.CurrentTouchDetectVars = null;
                    RemoveWake(m_TouchHandles, script.ItemId);
                    break;
                case SupportedEventList.Events.TOUCH:
                    if (!st.TouchActive) return;
                    if (!HandlesTouch(script))
                    {
                        // A state change took the handler away: nothing is left to repeat
                        st.TouchActive = false;
                        st.CurrentTouchDetectVars = null;
                        return;
                    }
                    evt.DetectVars = st.CurrentTouchDetectVars;
                    ResetTouchInterval(script);
                    break;
            }
        }

        /// <summary>
        /// The next repeat, 100 ms on. One wake per script, and none while a touch() already waits in the script's queue:
        /// a busy script gets the next repeat when it takes that one, not a backlog (Halcyon's own comment: one touch event
        /// on the queue at once).
        /// </summary>
        private void ResetTouchInterval(Interpreter script)
        {
            if (m_TouchHandles.ContainsKey(script.ItemId)) return;
            lock (script.ScriptState.EventQueueLock)
                if (script.ScriptState.IsEventQueued(SupportedEventList.Events.TOUCH)) return;
            C5.IPriorityQueueHandle<SleepEntry> h = null;
            m_SleepHeap.Add(ref h, new SleepEntry
            {
                ItemId = script.ItemId,
                ReadyOn = InWorldz.Phlox.Util.Clock.Now + TOUCH_INTERVAL,
                Event = SleepEntry.WakeEvent.Touch
            });
            m_TouchHandles[script.ItemId] = h;
        }

        /// <summary>
        /// A grab update for a script whose touch is active only refreshes the data the next repeat carries (Halcyon
        /// UpdateTouchData), and re-arms a repeat a reset or a parcel pause took away. True when it was taken that way.
        /// A script whose touch was never started stays on the region's grab updates: it gets this one as a touch().
        /// </summary>
        private bool FoldGrabUpdate(Interpreter script, PostedEvent evt)
        {
            if (!script.ScriptState.TouchActive || !HandlesTouch(script)) return false;
            script.ScriptState.CurrentTouchDetectVars = evt.DetectVars;
            ResetTouchInterval(script);
            return true;
        }

        private void ProcessEventQueue()
        {
            List<PendingEvent> events;
            lock (m_PendingEvents)
            {
                if (m_PendingEvents.Count == 0) return;
                events = new List<PendingEvent>(m_PendingEvents);
                m_PendingEvents.Clear();
            }

            foreach (var pe in events)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(pe.ItemId, out script))
                {
                    pe.Evt.SignalCompleted();   // A waiter must not wait for a script that is not here
                    // Held only while this item's Phlox load is in flight; anything else - a deleted or reset-away
                    // script's late sensor, HTTP or dataserver event, another engine's script, a failed compile - is dropped.
                    if (m_Engine != null && m_Engine.IsLoading(pe.ItemId)) AddDeferredEvent(pe.ItemId, pe.Evt);
                    else m_DroppedForUnloaded++;
                    continue;
                }

                // A script that is not enabled - stopped by its owner or paused by the parcel - keeps what belongs to
                // its own state change and runs it when it is started or resumed: Halcyon queues state_entry for a
                // disabled script and does not run it. Everything else is dropped below.
                if (!script.ScriptState.Enabled
                    && (pe.Evt.EventType == SupportedEventList.Events.STATE_ENTRY || pe.Evt.EventType == SupportedEventList.Events.STATE_EXIT))
                {
                    pe.Evt.SignalCompleted();
                    script.ScriptState.QueueEvent(pe.Evt);
                    continue;
                }

                // Halcyon (ExecutionScheduler, pending events): "killed and disabled scripts should no longer respond
                // to outside stimuli". That is also what a parcel pause does with an event that arrives.
                if (!script.ScriptState.Enabled)
                {
                    pe.Evt.SignalCompleted();
                    continue;
                }

                if (pe.GrabUpdate && FoldGrabUpdate(script, pe.Evt)) { pe.Evt.SignalCompleted(); continue; }

                PhloxEventInfo info = FindEventHandler(pe.Evt, script);
                CheckAndResetTouchWait(script, pe.Evt);
                if (info == null) { pe.Evt.SignalCompleted(); continue; }

                // Flood protection: drop events if the script's queue is full - but never on_rez, state_entry,
                // state_exit or timer, which RuntimeState.QueueEvent lets past the limit as Halcyon's did. A lost
                // state event leaves a state change half done, and a lost timer event stops the timer for good: it is
                // re-armed only when its event runs (CheckAndResetTimer).
                int queueDepth = script.ScriptState.EventQueue.Count;
                if (queueDepth >= MAX_EVENT_QUEUE_DEPTH && !OverflowsQueueLimit(pe.Evt.EventType))
                {
                    m_log.LogWarning("[PhloxExe]: Event queue full ({0} events) for script {1}, dropping {2} event",
                        queueDepth, pe.ItemId, pe.Evt.EventType);
                    pe.Evt.SignalCompleted();
                    continue;
                }

                // Suspended: accumulate instead of delivering (SL semantics — the event
                // queue keeps filling, bounded by the depth cap above, and drains on
                // resume). Note a fired llSetTimerEvent timer only re-arms when its TIMER
                // event is DELIVERED (CheckAndResetTimer), so a suspended repeating timer
                // accumulates exactly one pending TIMER event — no flood.
                if (m_Suspended.Contains(pe.ItemId))
                {
                    pe.Evt.SignalCompleted();   // It will run on resume, but nobody waits that long
                    script.ScriptState.QueueEvent(pe.Evt);
                    continue;
                }

                if (script.ScriptState.RunState == RuntimeState.Status.Waiting)
                {
                    // llMinEventDelay - a floor between handler STARTS. wiki: events
                    // inside the window are queued and processed after it, not dropped (YEngine
                    // drops some; the wiki is the parity rule here). Hold it and arm a wake.
                    if (MinDelayHolds(script))
                    {
                        script.ScriptState.QueueEvent(pe.Evt);
                        TrackMinDelayWake(script);
                    }
                    else
                        StartEvent(pe.Evt, script, info);
                }
                else if (IsNullChangeControl(pe.Evt) && ControlQueued(script))
                {
                    // Halcyon ExecutionScheduler: "sometimes the client will spam the server with control events";
                    // a control() that changes no key is not queued while a control() already waits.
                    pe.Evt.SignalCompleted();
                }
                else
                {
                    script.ScriptState.QueueEvent(pe.Evt);
                }
            }
        }

        /// <summary>The kinds RuntimeState.QueueEvent keeps past the queue limit (its OVERFLOWABLE_EVENTS).</summary>
        private static bool OverflowsQueueLimit(SupportedEventList.Events type)
            => type == SupportedEventList.Events.ON_REZ || type == SupportedEventList.Events.STATE_ENTRY
               || type == SupportedEventList.Events.STATE_EXIT || type == SupportedEventList.Events.TIMER;

        /// <summary>control(key id, integer level, integer edge) with edge 0: the viewer repeating keys held, nothing changed.</summary>
        private static bool IsNullChangeControl(PostedEvent evt)
            => evt.EventType == SupportedEventList.Events.CONTROL
               && evt.Args != null && evt.Args.Length > 2 && evt.Args[2] is int edge && edge == 0;

        private static bool ControlQueued(Interpreter script)
        {
            lock (script.ScriptState.EventQueueLock)
                return script.ScriptState.IsEventQueued(SupportedEventList.Events.CONTROL);
        }

        private void ProcessEnableDisable()
        {
            // Drain the entire queue in one pass — the previous one-per-pass
            // behaviour caused multi-script prims to take many DoWork cycles
            // before all their scripts entered the run queue, which compounded
            // with master-scheduler wakeup gaps to produce minute-long delays
            // between script state_entry events.
            List<EnableDisableReq> batch;
            lock (m_EnableDisableQueue)
            {
                if (m_EnableDisableQueue.Count == 0) return;
                batch = new List<EnableDisableReq>(m_EnableDisableQueue);
                m_EnableDisableQueue.Clear();
            }

            foreach (var req in batch)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(req.ItemId, out script)) continue;

                if (req.Enable)
                {
                    // Ticking Running on a crashed script starts it fresh, never from its dead frame
                    if (script.ScriptState.TerminatedReason != null) { ResetNow(req.ItemId); continue; }
                    bool wasOn = script.ScriptState.GeneralEnable;
                    script.ScriptState.GeneralEnable = true;
                    bool heldFresh;
                    lock (m_AllScriptsLock) heldFresh = m_HeldFresh.Remove(req.ItemId);
                    if (heldFresh)
                    {
                        // Loaded with the Running flag off and never started - its state_entry is owed now
                        PostEvent(req.ItemId, new PostedEvent { EventType = SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() });
                        if (!m_RunIndex.ContainsKey(req.ItemId))
                            AddToRunQueue(script);
                    }
                    else if (!wasOn)
                        StartAfterStop(script);
                }
                else if (script.ScriptState.GeneralEnable)
                {
                    bool wasEnabled = script.ScriptState.Enabled;
                    script.ScriptState.GeneralEnable = false;
                    RemoveFromRunQueue(req.ItemId);
                    ulong? timerLeft = wasEnabled ? TimerTimeLeft(script) : null;
                    UnregisterFromNotifications(script);
                    if (timerLeft.HasValue) m_StoppedTimerLeft[req.ItemId] = timerLeft.Value;
                    // Halcyon AfterDisable -> OnScriptUnloaded(GloballyDisabled): the async handlers stop and taken
                    // controls are let go. Their records stay, so a start takes them up again (StartAfterStop).
                    if (wasEnabled && m_Apis.TryGetValue(req.ItemId, out LSLSystemAPI api)) api.OnScriptStopped();
                    m_Engine.StateManager?.ScriptChanged(script);   // A stopped script never runs again to get itself saved
                }
                script.SetScriptEventFlags();
            }
        }

        /// <summary>
        /// The time a stopped script's timer had left when it was stopped, kept for its start. Halcyon kept the same in
        /// StateCapturedOn and TimerLastScheduledOn (AfterDisable, InjectScript).
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<UUID, ulong> m_StoppedTimerLeft = new();

        /// <summary>
        /// Milliseconds until the timer's next event, or null when the script has no timer. A timer whose event is
        /// already posted and not yet run has none left.
        /// </summary>
        private ulong? TimerTimeLeft(Interpreter script)
        {
            if (script.ScriptState.TimerInterval <= 0) return null;
            if (!m_TimerHandles.TryGetValue(script.ItemId, out var h)) return 0;
            ulong readyOn = m_SleepHeap[h].ReadyOn;
            ulong now = InWorldz.Phlox.Util.Clock.Now;
            return readyOn > now ? readyOn - now : 0;
        }

        /// <summary>
        /// A stopped script is started again (the Running checkbox, llSetScriptState TRUE): it carries on where it was,
        /// as Halcyon EnableScript -> InjectScript did. A running script goes back on the run queue, a sleeping one waits
        /// out the rest of its sleep, an idle one runs what was queued for it (a state_entry held while it was stopped),
        /// and one inside a blocking call waits for the call's answer, which queues it. The timer comes back with the
        /// time it had left, and OnScriptInjected takes up the sensor repeat and the controls the stop let go.
        /// A script the parcel still pauses stays paused; ResumeForParcel starts it.
        /// </summary>
        private void StartAfterStop(Interpreter script)
        {
            var st = script.ScriptState;
            bool hadLeft = m_StoppedTimerLeft.Remove(script.ItemId, out ulong left);
            if (!st.Enabled) return;

            if (st.TimerInterval > 0 && !m_TimerHandles.ContainsKey(script.ItemId))
            {
                if (hadLeft) ResumeTimerWithTimeLeft(script, left);
                else TrackTimer(script, InWorldz.Phlox.Util.Clock.Now + (ulong)st.TimerInterval, false);
            }

            switch (st.RunState)
            {
                case RuntimeState.Status.Running:
                    AddToRunQueue(script);
                    break;
                case RuntimeState.Status.Sleeping:
                    if (!m_StdSleepHandles.ContainsKey(script.ItemId))
                        TrackSleep(script, st.NextWakeup);
                    break;
                case RuntimeState.Status.Waiting:
                    bool queued;
                    lock (st.EventQueueLock) queued = st.EventQueue != null && st.EventQueue.Count > 0;
                    if (queued) DeliverNextQueuedEvent(script);
                    break;
                // Syscall: the return arrives as usual and queues it.
            }

            try { script.OnScriptInjected(false); }
            catch (Exception e) { m_log.LogError(e, "[PhloxExe]: {0} started again, but restoring its sensor and controls failed", script.ItemId); }
        }

        private void ProcessSuspendResume()
        {
            List<SuspendResumeReq> batch;
            lock (m_SuspendResumeQueue)
            {
                if (m_SuspendResumeQueue.Count == 0) return;
                batch = new List<SuspendResumeReq>(m_SuspendResumeQueue);
                m_SuspendResumeQueue.Clear();
            }

            foreach (var req in batch)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(req.ItemId, out script)) continue;

                if (req.Suspend)
                {
                    if (!m_Suspended.Add(req.ItemId)) continue;
                    // Park a runnable script: pull it from the run queue but leave
                    // RunState=Running — "runnable but not queued" is the parked marker
                    // the resume path re-queues. Sleep-heap entries, timers, touch and
                    // worldcomm listens all stay registered (the point of TRANSIENT
                    // suspend); their wake paths funnel through AddToRunQueue, which
                    // parks instead of queueing while suspended.
                    RemoveFromRunQueue(req.ItemId);
                }
                else
                {
                    if (!m_Suspended.Remove(req.ItemId)) continue;
                    if (!script.ScriptState.Enabled) continue; // disabled while suspended — enable path owns re-queueing

                    if (script.ScriptState.RunState == RuntimeState.Status.Running)
                    {
                        // Was mid-timeslice at suspend, or a sleep wake / syscall return
                        // parked it while suspended — continue execution where it left off.
                        AddToRunQueue(script);
                    }
                    else if (script.ScriptState.RunState == RuntimeState.Status.Waiting)
                    {
                        // Deliver the first event that accumulated during suspension (the
                        // rest drain normally via TransitionToWait once it runs).
                        DeliverNextQueuedEvent(script);
                    }
                    // Sleeping/Syscall: nothing to do — their normal completion paths
                    // re-queue through the (no longer gated) AddToRunQueue.
                }
            }
        }

        // Kick a Waiting script whose event queue filled while it was suspended. Mirrors
        // the TransitionToWait drain (dequeue -> find handler -> DoEvent -> re-arm timer)
        // but from outside the run queue, so it re-queues on success.
        private void DeliverNextQueuedEvent(Interpreter script)
        {
            while (true)
            {
                PostedEvent nextEvt;
                lock (script.ScriptState.EventQueueLock)
                {
                    if (script.ScriptState.EventQueue.Count == 0) return;
                    nextEvt = script.ScriptState.EventQueue.Dequeue();
                }
                PhloxEventInfo info = FindEventHandler(nextEvt, script);
                CheckAndResetTouchWait(script, nextEvt);
                if (info == null) continue;
                try
                {
                    script.ScriptState.DoEvent(info, nextEvt, nextEvt.Args);
                    CheckAndResetTimer(script, info);
                    AddToRunQueue(script);
                }
                catch (Exception e)   // not only VMException: restored values can be wrong in ways the VM does not check
                {
                    TerminateWithError(script, e);
                }
                return;
            }
        }

        // ── State that travels with objects ─────────────────────────────────────

        /// <summary>A capture asked for by a region thread (GetXMLState, SaveAllState), answered on this thread.</summary>
        private sealed class ObjectStateRequest
        {
            public UUID[] Items;         // null: save every loaded script (SaveAllState)
            public byte[][] Blobs;
            public UUID[] AssetIds;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }
        private readonly Queue<ObjectStateRequest> m_ObjectStateRequests = new();
        private readonly Queue<UUID> m_ArrivedAvatars = new();
        private readonly Queue<UUID> m_ExperienceLandChecks = new();

        // Captures taken for an object's other scripts, by item id, until the core asks for them (it asks one script at a
        // time, SceneObjectPartInventory.GetScriptStates). A null blob: the script travels without state.
        private readonly Dictionary<UUID, (byte[] Blob, UUID AssetId, long Taken)> m_CapturedForObject = new();

        /// <summary>
        /// The script's state for its object to carry, captured here: the caller is this scheduler's thread, or the thread
        /// is not running. False when the script is not loaded here, or is held because its saved row could not be read
        /// (its state is unknown, and the row stays the only copy).
        /// </summary>
        internal bool CaptureForObject(UUID itemId, out byte[] blob, out UUID assetId)
        {
            blob = null;
            assetId = UUID.Zero;
            Interpreter interp = FindScript(itemId);
            if (interp == null) return false;
            if ((interp.ScriptState.LocalDisable & RuntimeState.LocalDisableFlag.StateLoadFailed) != 0) return false;
            if (m_Apis.TryGetValue(itemId, out LSLSystemAPI api)) api.NoteGrantForCarry();
            try { blob = StateManager.CaptureBlob(interp); }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxExe]: Could not capture {0} for its object: {1}", itemId, e.Message);
                return false;
            }
            assetId = interp.Script.AssetId;
            return true;
        }

        /// <summary>
        /// As <see cref="CaptureForObject"/>, from a region thread while this scheduler's thread runs (Halcyon
        /// EngineInterface.GetXMLState: RequestStateData, then WaitForData). The first script asked for captures every
        /// script of <paramref name="objectItems"/> (its object's loaded scripts) in one pass on the scheduler thread, and
        /// the others are answered from that pass, so an object waits at most <paramref name="timeoutMs"/> however many
        /// scripts it holds. On a timeout none of them carries state: each starts fresh where it arrives, and one warning
        /// names the object.
        /// </summary>
        internal bool RequestCaptureForObject(UUID itemId, IReadOnlyList<UUID> objectItems, string objectName, int timeoutMs,
                                              out byte[] blob, out UUID assetId)
        {
            long now = Environment.TickCount64;
            lock (m_CapturedForObject)
            {
                foreach (var k in new List<UUID>(m_CapturedForObject.Keys))
                    if (now - m_CapturedForObject[k].Taken > Math.Max(timeoutMs, 1000) * 2L) m_CapturedForObject.Remove(k);
                if (m_CapturedForObject.Remove(itemId, out var got))
                {
                    blob = got.Blob;
                    assetId = got.AssetId;
                    return blob != null;
                }
            }

            var items = new List<UUID> { itemId };
            foreach (UUID other in objectItems) if (other != itemId) items.Add(other);
            var req = new ObjectStateRequest { Items = items.ToArray(), Blobs = new byte[items.Count][], AssetIds = new UUID[items.Count] };
            lock (m_ObjectStateRequests) m_ObjectStateRequests.Enqueue(req);
            m_WorkArrived?.Invoke();
            bool done = req.Done.Wait(timeoutMs);
            if (!done)
                m_log.LogWarning("[PhloxExe]: Timed out after {0} ms capturing the {1} script(s) of {2}; they travel without their state and start fresh where they arrive",
                    timeoutMs, items.Count, objectName);
            lock (m_CapturedForObject)
                for (int i = 1; i < items.Count; i++)
                    m_CapturedForObject[items[i]] = (done ? req.Blobs[i] : null, done ? req.AssetIds[i] : UUID.Zero, now);
            blob = done ? req.Blobs[0] : null;
            assetId = done ? req.AssetIds[0] : UUID.Zero;
            return blob != null;
        }

        /// <summary>Every loaded script saved to the state database now (SaveAllState), on the scheduler thread; waits for it.</summary>
        internal bool RequestSaveAll(int timeoutMs)
        {
            var req = new ObjectStateRequest();
            lock (m_ObjectStateRequests) m_ObjectStateRequests.Enqueue(req);
            m_WorkArrived?.Invoke();
            return req.Done.Wait(timeoutMs);
        }

        /// <summary>Save every loaded script now, on this thread (the caller is the scheduler thread, or it is not running).</summary>
        internal void SaveAllHere()
        {
            List<Interpreter> all;
            lock (m_AllScriptsLock) all = new List<Interpreter>(m_AllScripts.Values);
            m_Engine?.StateManager?.SaveNow(all);
        }

        private void ProcessObjectStateRequests()
        {
            List<ObjectStateRequest> batch;
            lock (m_ObjectStateRequests)
            {
                if (m_ObjectStateRequests.Count == 0) return;
                batch = new List<ObjectStateRequest>(m_ObjectStateRequests);
                m_ObjectStateRequests.Clear();
            }
            foreach (var req in batch)
            {
                try
                {
                    if (req.Items == null) SaveAllHere();
                    else
                        for (int i = 0; i < req.Items.Length; i++)
                            if (CaptureForObject(req.Items[i], out byte[] blob, out UUID assetId))
                            {
                                req.Blobs[i] = blob;
                                req.AssetIds[i] = assetId;
                            }
                }
                finally { req.Done.Set(); }
            }
        }

        /// <summary>
        /// An avatar became a root agent here (an arrival by crossing or teleport, a login). Scripts it granted
        /// TAKE_CONTROLS to, in the object it sits on or in its attachments, that hold a Control record and are not
        /// registered on it take their controls again (Halcyon EngineInterface.OnCrossedAvatarReady). A grant from an
        /// Experience waiting for this avatar is decided wherever its object is, as such a grant needs no seat.
        /// </summary>
        internal void RequestAvatarArrived(UUID agentId)
        {
            lock (m_ArrivedAvatars) m_ArrivedAvatars.Enqueue(agentId);
            m_WorkArrived?.Invoke();
        }

        private void ProcessArrivedAvatars()
        {
            List<UUID> batch;
            lock (m_ArrivedAvatars)
            {
                if (m_ArrivedAvatars.Count == 0) return;
                batch = new List<UUID>(m_ArrivedAvatars);
                m_ArrivedAvatars.Clear();
            }
            Scene world = m_Engine?.World;
            if (world == null) return;
            foreach (UUID agentId in batch)
            {
                foreach (HeldArrival held in m_HeldArrivals) held.Arrived.Add(agentId);
                ScenePresence sp = world.GetScenePresence(agentId);
                if (sp == null || sp.IsChildAgent || sp.IsDeleted) continue;
                var groups = new List<SceneObjectGroup>();
                SceneObjectGroup seat = sp.ParentPart?.ParentGroup;
                if (seat != null) groups.Add(seat);
                groups.AddRange(sp.GetAttachments());
                var visited = new HashSet<UUID>();
                foreach (SceneObjectGroup g in groups)
                {
                    if (g == null || g.IsDeleted) continue;
                    foreach (SceneObjectPart part in g.Parts)
                        foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                            if (m_Apis.TryGetValue(item.ItemID, out LSLSystemAPI api) && visited.Add(item.ItemID))
                                api.OnGroupCrossedAvatarReady(agentId);
                }
                var experienceClaims = new List<LSLSystemAPI>();
                foreach (KeyValuePair<UUID, LSLSystemAPI> kv in m_Apis)
                    if (!visited.Contains(kv.Key) && kv.Value.HasExperienceClaimFor(agentId)) experienceClaims.Add(kv.Value);
                foreach (LSLSystemAPI api in experienceClaims) api.OnGroupCrossedAvatarReady(agentId);
            }
        }

        /// <summary>
        /// An avatar entered a parcel here. Every script holding a grant this avatar gave from an Experience asks whether the
        /// Experience can still run for it (<see cref="LSLSystemAPI.ExperienceLandChanged"/>).
        /// </summary>
        internal void RequestExperienceLandCheck(UUID agentId)
        {
            lock (m_ExperienceLandChecks) m_ExperienceLandChecks.Enqueue(agentId);
            m_WorkArrived?.Invoke();
        }

        private void ProcessExperienceLandChecks()
        {
            List<UUID> batch;
            lock (m_ExperienceLandChecks)
            {
                if (m_ExperienceLandChecks.Count == 0) return;
                batch = new List<UUID>(m_ExperienceLandChecks);
                m_ExperienceLandChecks.Clear();
            }
            foreach (UUID agentId in batch)
            {
                var holders = new List<LSLSystemAPI>();
                foreach (KeyValuePair<UUID, LSLSystemAPI> kv in m_Apis)
                    if (kv.Value.HoldsExperienceGrantFrom(agentId)) holders.Add(kv.Value);
                foreach (LSLSystemAPI api in holders) api.ExperienceLandChanged(agentId);
            }
        }

        // ── Permission lifecycle ───────────────────────────────────────────────

        /// <summary>The core released this script's controls on an avatar still in the region (stand, Release Keys, detach, drop).</summary>
        internal void RequestControlsReleasedByCore(UUID itemId, UUID agentId) => EnqueuePermsEnd(new PermsEndReq { ItemId = itemId, AgentId = agentId });

        /// <summary>The object has a new owner.</summary>
        internal void RequestOwnerChanged(SceneObjectGroup group)
        {
            if (group != null) EnqueuePermsEnd(new PermsEndReq { Group = group });
        }

        private void EnqueuePermsEnd(PermsEndReq req)
        {
            lock (m_PermsEnds) m_PermsEnds.Enqueue(req);
            m_WorkArrived?.Invoke();
        }

        private void ProcessPermsEnds()
        {
            List<PermsEndReq> batch;
            lock (m_PermsEnds)
            {
                if (m_PermsEnds.Count == 0) return;
                batch = new List<PermsEndReq>(m_PermsEnds);
                m_PermsEnds.Clear();
            }
            foreach (var req in batch)
            {
                if (req.Group == null)
                {
                    if (m_Apis.TryGetValue(req.ItemId, out LSLSystemAPI api)) api.ControlsReleasedByCore(req.AgentId);
                    continue;
                }
                if (req.Group.IsDeleted) continue;
                foreach (SceneObjectPart part in req.Group.Parts)
                    foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                        if (m_Apis.TryGetValue(item.ItemID, out LSLSystemAPI owned)) owned.OwnerChanged();
            }
        }

        // ── No Scripts parcels ────────────────────────────────────────────────

        /// <summary>A script's parcel standing may have changed (it took or released controls).</summary>
        internal void RequestParcelCheckForItem(UUID itemId) => EnqueueParcelCheck(new ParcelCheckReq { Kind = ParcelCheckKind.Item, ItemId = itemId });

        /// <summary>An object moved to another parcel, changed owner or group, or was attached or dropped.</summary>
        internal void RequestParcelCheck(SceneObjectGroup group)
        {
            if (group == null) return;
            EnqueueParcelCheck(new ParcelCheckReq { Kind = ParcelCheckKind.Group, Group = group });
        }

        /// <summary>A parcel's flags, owner, group or shape changed (EventManager.OnLandObjectAdded).</summary>
        internal void RequestParcelCheckForParcel(int parcelLocalId)
            => EnqueueParcelCheck(new ParcelCheckReq { Kind = ParcelCheckKind.Parcel, ParcelLocalId = parcelLocalId });

        /// <summary>The core may have cleared an avatar's taken controls: ask again for the scripts running only by them.</summary>
        internal void RequestControlHoldersCheck() => EnqueueParcelCheck(new ParcelCheckReq { Kind = ParcelCheckKind.ControlHolders });

        private void EnqueueParcelCheck(ParcelCheckReq req)
        {
            lock (m_ParcelChecks) m_ParcelChecks.Enqueue(req);
            m_WorkArrived?.Invoke();
        }

        private void ProcessParcelChecks()
        {
            List<ParcelCheckReq> batch;
            lock (m_ParcelChecks)
            {
                if (m_ParcelChecks.Count == 0) return;
                batch = new List<ParcelCheckReq>(m_ParcelChecks);
                m_ParcelChecks.Clear();
            }

            var done = new HashSet<UUID>();   // one decision per script per pass
            foreach (var req in batch)
            {
                switch (req.Kind)
                {
                    case ParcelCheckKind.Item:
                        if (done.Add(req.ItemId)) EvaluateParcelRule(req.ItemId);
                        break;

                    case ParcelCheckKind.Group:
                        // the object's own scripts only
                        var group = req.Group;
                        if (group.IsDeleted || !group.ContainsScripts()) break;
                        foreach (SceneObjectPart part in group.Parts)
                            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                                if (m_AllScripts.ContainsKey(item.ItemID) && done.Add(item.ItemID))
                                    EvaluateParcelRule(item.ItemID);
                        break;

                    case ParcelCheckKind.Parcel:
                        // This engine's own scripts, one parcel lookup per object, and a decision only for the scripts
                        // whose object stands on the parcel that changed. Attachments are always allowed; skipped.
                        var onParcel = new System.Collections.Generic.Dictionary<UUID, bool>();
                        foreach (var kv in m_Apis)
                        {
                            SceneObjectGroup g = kv.Value.HostPart?.ParentGroup;
                            if (g == null || g.IsDeleted || g.IsAttachment) continue;
                            m_ParcelStats.Scanned++;
                            if (!onParcel.TryGetValue(g.UUID, out bool on))
                            {
                                on = m_Engine != null && m_Engine.ParcelLocalIdAt(g) == req.ParcelLocalId;
                                onParcel[g.UUID] = on;
                            }
                            if (on && done.Add(kv.Key)) EvaluateParcelRule(kv.Key);
                        }
                        break;

                    case ParcelCheckKind.ControlHolders:
                        foreach (UUID id in new List<UUID>(m_ControlsExempt))
                            if (done.Add(id)) EvaluateParcelRule(id);
                        break;
                }
            }
        }

        /// <summary>
        /// "May this script run here", asked of the engine (PhloxEngine.ScriptMayRunHere), and the pause or resume it
        /// calls for. Scheduler thread.
        /// </summary>
        private void EvaluateParcelRule(UUID itemId)
        {
            if (!m_AllScripts.TryGetValue(itemId, out Interpreter script)) return;
            if (!m_Apis.TryGetValue(itemId, out LSLSystemAPI api)) return;
            SceneObjectPart part = api.HostPart;
            if (part?.ParentGroup == null || part.ParentGroup.IsDeleted || m_Engine == null) return;

            m_ParcelStats.Evaluated++;
            bool allowed = m_Engine.ScriptMayRunHere(part, itemId, out bool onlyByControls);
            if (onlyByControls) m_ControlsExempt.Add(itemId); else m_ControlsExempt.Remove(itemId);

            if (allowed) ResumeForParcel(script, api);
            else PauseForParcel(script, api);
        }

        /// <summary>
        /// Pause, never stop or reset: RunState, the frame, globals and the script's own queued events stay as they are.
        /// Its wakes (sleep, timer, touch repeat, event-delay floor) come off the heap and its sensor repeat stops;
        /// listens stay registered and what they deliver is dropped (ProcessEventQueue).
        /// </summary>
        private void PauseForParcel(Interpreter script, LSLSystemAPI api)
        {
            var st = script.ScriptState;
            if ((st.LocalDisable & RuntimeState.LocalDisableFlag.Parcel) != 0) return;
            st.LocalDisable |= RuntimeState.LocalDisableFlag.Parcel;
            m_ParcelStats.Paused++;

            RemoveFromRunQueue(script.ItemId);
            RemoveWake(m_StdSleepHandles, script.ItemId);
            KeepTimerLeftForParcel(script);
            RemoveWake(m_TimerHandles, script.ItemId);
            RemoveWake(m_TouchHandles, script.ItemId);
            RemoveWake(m_MinDelayHandles, script.ItemId);
            api.PauseSensorForParcel();
            script.SetScriptEventFlags();   // no events while LocalDisable is set: the prim stops advertising touch etc.
            m_log.LogDebug("[PhloxExe]: {0} paused: the parcel does not allow it", script.ItemId);
        }

        /// <summary>
        /// Clear the parcel pause. A script its owner stopped stays stopped: nothing is re-armed unless it is enabled
        /// once the parcel bit is off.
        /// </summary>
        private void ResumeForParcel(Interpreter script, LSLSystemAPI api)
        {
            var st = script.ScriptState;
            if ((st.LocalDisable & RuntimeState.LocalDisableFlag.Parcel) == 0) return;
            st.LocalDisable &= ~RuntimeState.LocalDisableFlag.Parcel;
            m_ParcelStats.Resumed++;
            script.SetScriptEventFlags();
            bool hadLeft = m_ParcelTimerLeft.Remove(script.ItemId, out ulong left);
            if (!st.Enabled) return;

            if (st.TimerInterval > 0 && !m_TimerHandles.ContainsKey(script.ItemId))
            {
                if (hadLeft) ResumeTimerWithTimeLeft(script, left);
                else TrackTimer(script, InWorldz.Phlox.Util.Clock.Now + (ulong)st.TimerInterval, false);
            }
            api.RestoreSensorAfterParcel();

            switch (st.RunState)
            {
                case RuntimeState.Status.Running:
                    AddToRunQueue(script);
                    break;
                case RuntimeState.Status.Sleeping:
                    if (!m_StdSleepHandles.ContainsKey(script.ItemId))
                        TrackSleep(script, st.NextWakeup);
                    break;
                case RuntimeState.Status.Waiting:
                    bool queued;
                    lock (st.EventQueueLock) queued = st.EventQueue != null && st.EventQueue.Count > 0;
                    if (queued) DeliverNextQueuedEvent(script);
                    break;
                // Syscall: the return arrives as usual and queues it.
            }
            m_log.LogDebug("[PhloxExe]: {0} resumed: the parcel allows it", script.ItemId);
        }

        private void RemoveWake(System.Collections.Generic.Dictionary<UUID, C5.IPriorityQueueHandle<SleepEntry>> handles, UUID itemId)
        {
            if (handles.TryGetValue(itemId, out var h))
            {
                handles.Remove(itemId);
                m_SleepHeap.Delete(h);
            }
        }

        /// <summary>
        /// At a parcel pause, keep the time the timer has left. Halcyon (ExecutionScheduler.InjectScript):
        /// readyOn = now + (TimerInterval - (StateCapturedOn - TimerLastScheduledOn)), StateCapturedOn being the pause,
        /// so only the time waited before the pause counts and the pause's own length does not. A timer whose event is
        /// already posted and not yet delivered (no wake armed) has nothing left: it fires at once on resume, as
        /// Halcyon's remainder of zero or less does.
        /// </summary>
        private void KeepTimerLeftForParcel(Interpreter script)
        {
            if (script.ScriptState.TimerInterval <= 0) return;
            ulong left = 0;
            if (m_TimerHandles.TryGetValue(script.ItemId, out var h))
            {
                ulong readyOn = m_SleepHeap[h].ReadyOn;
                ulong now = InWorldz.Phlox.Util.Clock.Now;
                left = readyOn > now ? readyOn - now : 0;
            }
            m_ParcelTimerLeft[script.ItemId] = left;
        }

        /// <summary>
        /// The next timer event comes after the time that was left at the pause, then CheckAndResetTimer re-arms
        /// it at the full interval as usual. TimerLastScheduledOn is set as if the timer had been scheduled that long ago,
        /// so a save or a second pause measures from the same schedule (Halcyon left it at the old value, which counts
        /// the first pause again on a second one).
        /// </summary>
        private void ResumeTimerWithTimeLeft(Interpreter script, ulong left)
        {
            ulong interval = (ulong)script.ScriptState.TimerInterval;
            if (left > interval) left = interval;
            ulong now = InWorldz.Phlox.Util.Clock.Now;
            TrackTimer(script, now + left, true);
            ulong waited = interval - left;
            script.ScriptState.TimerLastScheduledOn = now > waited ? now - waited : 0;
        }

        private void ProcessResets()
        {
            // Drain the entire queue in one pass (see ProcessEnableDisable note).
            List<UUID> batch;
            lock (m_PendingResets)
            {
                if (m_PendingResets.Count == 0) return;
                batch = new List<UUID>(m_PendingResets);
                m_PendingResets.Clear();
            }
            foreach (var id in batch)
                ResetNow(id);
        }

        private void ProcessSyscallReturns()
        {
            List<SyscallReturn> returns;
            lock (m_SyscallReturns)
            {
                if (m_SyscallReturns.Count == 0) return;
                returns = new List<SyscallReturn>(m_SyscallReturns);
                m_SyscallReturns.Clear();
            }

            foreach (var ret in returns)
            {
                Interpreter script;
                if (!m_AllScripts.TryGetValue(ret.ItemId, out script)) continue;
                if (script.ScriptState.RunState != RuntimeState.Status.Syscall) continue;
                // A return for an earlier call (the script was reset, changed state, or the call
                // was already answered by its deadline and the script has since parked in another).
                if (ret.Seq >= 0 && ret.Seq != script.ScriptState.SyscallSeq) continue;

                // The call is over; the script is no longer parked in it (now on this thread).
                script.ScriptState.LastSyscallIndex = -1;

                if (ret.Fault != null)
                {
                    // The deferred body threw. Resume the script and re-raise the exception inside
                    // its next tick, where the inline call would have thrown it.
                    script.SetPendingFault(ret.Fault);
                    script.ScriptState.RunState = RuntimeState.Status.Running;
                    AddToRunQueue(script);
                    continue;
                }

                if (ret.RetValue != null)
                    script.ScriptState.Operands.Push(ret.RetValue);

                if (ret.Delay == 0)
                {
                    script.ScriptState.RunState = RuntimeState.Status.Running;
                    AddToRunQueue(script);
                }
                else
                {
                    script.ScriptState.RunState = RuntimeState.Status.Sleeping;
                    script.ScriptState.NextWakeup = InWorldz.Phlox.Util.Clock.Now + (ulong)ret.Delay;
                    TrackSleep(script, script.ScriptState.NextWakeup);
                }
            }
        }

        private void OnStateChange(Interpreter script, int newState)
        {
            UnregisterFromNotifications(script);
            script.ScriptState.StateChangePrep();
            // SL state: "All listens are released". The API drops them from the listen manager; the saved record goes
            // with them, so a restore cannot bring back a listen of the state the script left.
            script.ScriptState.ActiveListens?.Clear();

            lock (m_PendingEvents)
            {
                PostEvent(script.ItemId, new PostedEvent
                {
                    EventType = SupportedEventList.Events.STATE_EXIT,
                    Args = Array.Empty<object>()
                });
                PostEvent(script.ItemId, new PostedEvent
                {
                    EventType = SupportedEventList.Events.STATE_ENTRY,
                    Args = Array.Empty<object>(),
                    TransitionToState = newState
                });
            }
        }

		private PhloxEventInfo FindEventHandler(PostedEvent evt, Interpreter script)
		{
			if (evt.TransitionToState != PostedEvent.NO_TRANSITION)
			{
				script.ScriptState.LSLState = evt.TransitionToState;
				script.SetScriptEventFlags();
			}
			int state = script.ScriptState.LSLState;
			if (state < 0 || script.Script.StateEvents == null || state >= script.Script.StateEvents.Length)
				return null;
			if (evt.TransitionToState != PostedEvent.NO_TRANSITION && script.ScriptState.Enabled)
			{
				// SL llSetTimerEvent: "The timer persists across state changes". OnStateChange took the timer's wake
				// off the heap with the rest; the new state gets it back when it has a timer() to run, as Halcyon
				// DoStateTransitionAndFindEventHandler does. A stopped script is re-armed when it is started.
				PhloxEventInfo timer = script.Script.FindEvent(state, (int)SupportedEventList.Events.TIMER);
				if (timer != null) CheckAndResetTimer(script, timer);
			}
			return script.Script.FindEvent(state, (int)evt.EventType);
		}

        private void StartEvent(PostedEvent evt, Interpreter script, PhloxEventInfo info)
        {
            try
            {
                ArmMinDelay(script);
                script.ScriptState.SampleMemoryPeak();   // Event boundary
                script.ScriptState.DoEvent(info, evt, evt.Args);
                CheckAndResetTimer(script, info);
                AddToRunQueue(script);
            }
            catch (Exception e)   // not only VMException: restored values can be wrong in ways the VM does not check
            {
                TerminateWithError(script, e);
            }
        }

        // ── llMinEventDelay ─────────────────────────────────────────────────────

        /// <summary>True while this script's floor has not elapsed since its last handler start.</summary>
        private static bool MinDelayHolds(Interpreter script)
            => script.ScriptState.MinEventDelayMs > 0
               && InWorldz.Phlox.Util.Clock.Now < script.ScriptState.NextEventAllowedOn;

        /// <summary>A handler is starting now: the next may not start before now + floor.</summary>
        private static void ArmMinDelay(Interpreter script)
        {
            if (script.ScriptState.MinEventDelayMs > 0)
                script.ScriptState.NextEventAllowedOn = InWorldz.Phlox.Util.Clock.Now + (ulong)script.ScriptState.MinEventDelayMs;
        }

        /// <summary>Wake at the end of the floor so the held event is delivered without a poke.</summary>
        private void TrackMinDelayWake(Interpreter script)
        {
            if (m_MinDelayHandles.ContainsKey(script.ItemId)) return;
            var entry = new SleepEntry { ItemId = script.ItemId, ReadyOn = script.ScriptState.NextEventAllowedOn, Event = SleepEntry.WakeEvent.MinDelay };
            C5.IPriorityQueueHandle<SleepEntry> h = null;
            m_SleepHeap.Add(ref h, entry);
            m_MinDelayHandles[script.ItemId] = h;
            m_WorkArrived?.Invoke();
        }

        /// <summary>llMinEventDelay(delay): the floor, applied from the next handler start on.</summary>
        public void SetMinEventDelay(UUID itemId, float seconds)
        {
            if (!m_AllScripts.TryGetValue(itemId, out Interpreter script)) return;
            int ms = seconds <= 0f ? 0 : (int)(seconds * 1000f);
            script.ScriptState.MinEventDelayMs = ms;
            if (ms == 0) script.ScriptState.NextEventAllowedOn = 0;
        }

        private void CheckAndResetTimer(Interpreter script, PhloxEventInfo info)
        {
            if (info.EventType == (int)SupportedEventList.Events.TIMER &&
                script.ScriptState.TimerInterval > 0 &&
                !m_TimerHandles.ContainsKey(script.ItemId))
            {
                ulong readyOn = InWorldz.Phlox.Util.Clock.Now + (ulong)script.ScriptState.TimerInterval;
                TrackTimer(script, readyOn, false);
            }
        }

        private void AddToRunQueue(Interpreter script)
        {
            // A script paused by the parcel is never queued. RunState is left as it is (a syscall return has
            // already set Running), and ResumeForParcel queues it by that RunState. This one gate covers a syscall
            // return, a reset, the owner's Running checkbox and a queued-event delivery.
            if ((script.ScriptState.LocalDisable & RuntimeState.LocalDisableFlag.Parcel) != 0)
                return;

            // Suspended scripts never enter the run queue (removal at suspend + this gate
            // keeps DoTimeslices' hot path free of per-slice flag checks and keeps
            // HasWork() honest — a run queue holding only suspended scripts would make
            // the master scheduler busy-spin). Park as RunState=Running-but-not-queued:
            // that is the marker ProcessSuspendResume re-queues on resume, so sleep wakes
            // and syscall returns that land during suspension aren't lost.
            if (m_Suspended.Contains(script.ItemId))
            {
                script.ScriptState.RunState = RuntimeState.Status.Running;
                return;
            }

            if (m_RunIndex.ContainsKey(script.ItemId)) return;

            var node = m_RunQueue.AddLast(script);
            m_RunIndex[script.ItemId] = node;
            script.ScriptState.RunState = RuntimeState.Status.Running;

            if (m_NextScript == null)
                m_NextScript = node;
        }

        private void RemoveFromRunQueue(UUID itemId)
        {
            if (m_NextScript != null && m_NextScript.Value.ItemId == itemId)
            {
                m_NextScript = m_NextScript.Next ?? m_RunQueue.First;
                if (m_NextScript != null && m_NextScript.Value.ItemId == itemId)
                    m_NextScript = null;
            }

            SysLinkedListNode node;
            if (m_RunIndex.TryGetValue(itemId, out node))
            {
                m_RunIndex.Remove(itemId);
                m_RunQueue.Remove(node);
            }
        }

        private void UnregisterFromNotifications(Interpreter script)
        {
            m_ParcelTimerLeft.Remove(script.ItemId);
            m_StoppedTimerLeft.Remove(script.ItemId);
            C5.IPriorityQueueHandle<SleepEntry> h;
            if (m_StdSleepHandles.TryGetValue(script.ItemId, out h))
            {
                m_StdSleepHandles.Remove(script.ItemId);
                m_SleepHeap.Delete(h);
            }
            if (m_TimerHandles.TryGetValue(script.ItemId, out h))
            {
                m_TimerHandles.Remove(script.ItemId);
                m_SleepHeap.Delete(h);
            }
            if (m_TouchHandles.TryGetValue(script.ItemId, out h))
            {
                m_TouchHandles.Remove(script.ItemId);
                m_SleepHeap.Delete(h);
            }

            m_WorldComm.DeleteListener(script.ItemId);
        }

        /// <summary>
        /// Bring a script back that was captured mid-syscall.
        /// <para>
        /// The interrupted call cannot be re-issued and its completion will never arrive, so the
        /// function's return value is pushed here and the script continues from the instruction after
        /// the call. A Void function pushes nothing, which is exactly what its caller expects.
        /// </para>
        /// <para>
        /// A state saved before <c>LastSyscallIndex</c> existed has -1 and cannot be resumed - there is
        /// no way to know what value to push - so it falls back to the old behaviour and says so.
        /// </para>
        /// </summary>
        private void ResumeFromSyscall(Interpreter interp, UUID itemId, bool enqueue)
        {
            int index = interp.ScriptState.LastSyscallIndex;
            // FunctionSig is a struct, so "not found" needs its own flag.
            InWorldz.Phlox.Types.FunctionSig sig = default;
            bool found = false;
            if (index >= 0)
            {
                foreach (var m in InWorldz.Phlox.Types.Defaults.AllMethods)
                {
                    if (m.TableIndex == index) { sig = m; found = true; break; }
                }
            }

            if (!found)
            {
                m_log.LogWarning(
                    "[PhloxExe]: {Item} was saved mid-syscall with no recorded function (index {Index}); restored as Waiting - the interrupted call does not resume",
                    itemId, index);
                interp.ScriptState.RunState = RuntimeState.Status.Waiting;
                return;
            }

            if (sig.ReturnType != InWorldz.Phlox.Types.VarType.Void)
                interp.ScriptState.Operands.Push(DefaultValueFor(sig.ReturnType));

            m_log.LogInformation(
                "[PhloxExe]: {Item} was saved inside {Function}; resuming with that call's default return value",
                itemId, sig.FunctionName);

            if (enqueue) AddToRunQueue(interp);
            else interp.ScriptState.RunState = RuntimeState.Status.Running;   // stopped: StartAfterStop queues it
        }

        /// <summary>
        /// Milliseconds the restored timer had left: the interval less the time it had already waited when the state
        /// was captured (Halcyon InjectScript: TimerInterval - (StateCapturedOn - TimerLastScheduledOn)), never below
        /// 0 or above the interval. ToRuntimeState put both times on this run's clock. A row with no schedule time
        /// gets the whole interval.
        /// </summary>
        private static ulong RestoredTimerLeft(RuntimeState st)
        {
            ulong interval = (ulong)st.TimerInterval;
            if (st.TimerLastScheduledOn == 0 || st.StateCapturedOn == 0) return interval;
            long waited = (long)st.StateCapturedOn - (long)st.TimerLastScheduledOn;
            if (waited <= 0) return interval;
            return (ulong)waited >= interval ? 0 : interval - (ulong)waited;
        }

        /// <summary>
        /// Register the restored script's saved listens again, each with its own handle. A listen the manager cannot
        /// take back (the region's cap is full) is dropped from the state too, so the script's handles stay true.
        /// </summary>
        /// <summary>
        /// The limits a running script is held to, applied to state that came with its object: the timer as
        /// llSetTimerEvent sets it (none below 0, the region's MinTimerInterval floor), the event delay as llMinEventDelay
        /// sets it, and the records OnScriptInjected acts on in the shapes the script's own calls write them. Listens are
        /// held to the listen caps when they are registered again (PhloxListenManager.Restore). Null when it fits.
        /// </summary>
        private string FitCarriedStateHere(RuntimeState st)
        {
            if (st.TimerInterval < 0) st.TimerInterval = 0;
            int floorMs = (int)((m_Engine?.MinTimerInterval ?? 0f) * 1000f);
            if (st.TimerInterval > 0 && st.TimerInterval < floorMs) st.TimerInterval = floorMs;
            if (st.MinEventDelayMs < 0) st.MinEventDelayMs = 0;

            if (st.MiscAttributes == null) return null;
            foreach (var kvp in new List<KeyValuePair<int, object[]>>(st.MiscAttributes))
            {
                object[] v = kvp.Value;
                bool fits = (RuntimeState.MiscAttr)kvp.Key switch
                {
                    RuntimeState.MiscAttr.VolumeDetect or RuntimeState.MiscAttr.SilentEstateManagement
                        => v != null && v.Length == 1 && v[0] is int,
                    RuntimeState.MiscAttr.Control
                        => v != null && v.Length == 3 && v[0] is int && v[1] is int && v[2] is int,
                    RuntimeState.MiscAttr.SensorRepeat
                        => v != null && v.Length == 6 && v[0] is string && v[1] is string && v[2] is int &&
                           v[3] is float && v[4] is float && v[5] is float,
                    _ => false
                };
                if (!fits) return $"its record {kvp.Key} is not one a script writes";
            }
            return null;
        }

        /// <summary>
        /// Restored values threw while the script was being put back: it stops with its usual error, as a runtime error
        /// stops it, off the run queue and the sleep heap.
        /// </summary>
        private void StopRestoredScript(Interpreter interp, Exception e)
        {
            RemoveFromRunQueue(interp.ItemId);
            TerminateWithError(interp, e);
        }

        private void RestoreListens(Interpreter interp, PhloxLoadRequest req)
        {
            var saved = interp.ScriptState.ActiveListens;
            if (saved == null || saved.Count == 0) return;
            var listens = m_Engine?.ListenManager;
            foreach (var kvp in new List<KeyValuePair<int, ActiveListen>>(saved))
            {
                var l = kvp.Value;
                if (l == null) { saved.Remove(kvp.Key); continue; }
                UUID filterKey = UUID.Zero;
                if (!string.IsNullOrEmpty(l.Key)) UUID.TryParse(l.Key, out filterKey);
                int got = listens?.Restore(req.Prim.LocalId, req.ItemID, req.Prim.UUID, l.Handle, l.Channel,
                    l.Name ?? string.Empty, filterKey, l.Message ?? string.Empty) ?? -1;
                if (got != l.Handle) saved.Remove(kvp.Key);
            }
        }

        /// <summary>Test seam: when the script's timer wakes next, on the engine clock; null with no timer armed.</summary>
        internal ulong? TimerReadyOn(UUID itemId)
            => m_TimerHandles.TryGetValue(itemId, out var h) ? m_SleepHeap[h].ReadyOn : (ulong?)null;

        /// <summary>The value an interrupted call of this return type contributes.</summary>
        private static object DefaultValueFor(InWorldz.Phlox.Types.VarType type)
        {
            switch (type)
            {
                case InWorldz.Phlox.Types.VarType.Integer: return 0;
                case InWorldz.Phlox.Types.VarType.Float:   return 0.0f;
                case InWorldz.Phlox.Types.VarType.Vector:  return OpenMetaverse.Vector3.Zero;
                case InWorldz.Phlox.Types.VarType.Rotation:return OpenMetaverse.Quaternion.Identity;
                case InWorldz.Phlox.Types.VarType.List:    return new InWorldz.Phlox.Types.LSLList(new System.Collections.Generic.List<object>());
                case InWorldz.Phlox.Types.VarType.Key:     return OpenMetaverse.UUID.Zero.ToString();
                case InWorldz.Phlox.Types.VarType.String:  return string.Empty;
                default:                                   return null;
            }
        }
        private void TrackSleep(Interpreter script, ulong readyOn)
        {
            C5.IPriorityQueueHandle<SleepEntry> h = null;
            m_SleepHeap.Add(ref h, new SleepEntry
            {
                ItemId = script.ItemId,
                ReadyOn = readyOn,
                Event = SleepEntry.WakeEvent.None
            });
            m_StdSleepHandles[script.ItemId] = h;
            script.ScriptState.NextWakeup = readyOn;
        }

        private void TrackTimer(Interpreter script, ulong readyOn, bool fromRestore)
        {
            C5.IPriorityQueueHandle<SleepEntry> h = null;
            m_SleepHeap.Add(ref h, new SleepEntry
            {
                ItemId = script.ItemId,
                ReadyOn = readyOn,
                Event = SleepEntry.WakeEvent.Timer
            });
            m_TimerHandles[script.ItemId] = h;
            if (!fromRestore)
                script.ScriptState.TimerLastScheduledOn = InWorldz.Phlox.Util.Clock.Now;
            script.ScriptState.RemovePendingTimerEvent();
        }

        private void TerminateWithError(Interpreter script, Exception e)
        {
            script.ScriptState.RunningEvent?.SignalCompleted();
            script.ScriptState.RunState = RuntimeState.Status.Killed;
            script.ScriptState.LastSyscallIndex = -1;   // Not parked in anything any more
            // A crashed script stays stopped until reset. The Running flag goes off the way
            // llSetScriptState(FALSE) and the viewer's checkbox take it off - GeneralEnable in the state, which
            // is persisted, and the item's flag - so a restore holds it and `phlox status` says why.
            script.ScriptState.GeneralEnable = false;
            script.ScriptState.TerminatedReason = e.Message;
            UnregisterFromNotifications(script);
            m_Engine.SetItemRunningFlag(script.HostLocalId, script.ItemId, false);
            // The crash branch of RunNextScript returns before its ScriptChanged, so a script that died in
            // its first slice was never dirty - and a region stop calls StateManager.Stop(), which flushes the DIRTY
            // set only (no ScriptUnloaded at shutdown). The killed state never reached the row; the next start found
            // the previous asset's row, discarded it as stale, and ran state_entry again (item 9262c036).
            // The item's Running flag cannot carry it either: the region DB does not store it. Mark it dirty here.
            m_Engine.StateManager?.ScriptChanged(script);
            m_log.LogError("[PhloxExe]: Script {0} asset {1} terminated: {2}",
                script.ItemId, script.Script.AssetId, e);
            try
            {
                // Halcyon's wording (ExecutionScheduler.TerminateScriptWithError), which error-catcher scripts match
                script.ShoutError($"Script {script.Script.AssetId} encountered a problem and was stopped: {e.Message}");
            }
            catch { /* ignore errors during error reporting */ }
        }

        private void PerformAsyncCall(SyscallShim.LongRunSyscallDelegate call)
        {
            m_AsyncQueue.Enqueue(call);
            EnsureAsyncWorker();
        }

        private void EnsureAsyncWorker()
        {
            // Spawn a drain worker unless the concurrency cap is already saturated.
            // CAS loop so a burst of posts can never exceed MAX_ASYNC_WORKERS.
            while (!m_AsyncQueue.IsEmpty)
            {
                int current = Volatile.Read(ref m_AsyncWorkers);
                if (current >= MAX_ASYNC_WORKERS)
                    return;
                if (Interlocked.CompareExchange(ref m_AsyncWorkers, current + 1, current) == current)
                {
                    Task.Run(DrainAsyncQueue);
                    return;
                }
            }
        }

        private void DrainAsyncQueue()
        {
            try
            {
                while (m_AsyncQueue.TryDequeue(out SyscallShim.LongRunSyscallDelegate call))
                {
                    try { call(); }
                    catch (Exception e)
                    {
                        m_log.LogError("[PhloxExe]: Async syscall exception: {0}", e);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref m_AsyncWorkers);
                // An item may land between the failed TryDequeue and the decrement;
                // re-kick so it cannot strand in the queue with zero workers.
                EnsureAsyncWorker();
            }
        }

        private void InjectDeferredEvents(Interpreter script)
        {
            DeferredEvents deferred;
            if (!m_DeferredEvents.TryGetValue(script.ItemId, out deferred)) return;
            m_DeferredEvents.Remove(script.ItemId);
            foreach (var evt in deferred.Events)
                PostEvent(script.ItemId, evt);
        }

        private void AddDeferredEvent(UUID itemId, PostedEvent evt)
        {
            if (!m_DeferredEvents.TryGetValue(itemId, out var list))
            {
                list = new DeferredEvents { ExpiresOn = InWorldz.Phlox.Util.Clock.Now + DeferredEventLifetimeMs };
                m_DeferredEvents[itemId] = list;
            }
            if (list.Events.Count < MaxDeferredEventsPerItem) list.Events.Add(evt);   // Halcyon: the first 32 are kept
            else m_DroppedForUnloaded++;
        }

        /// <summary>Halcyon DeferredEventManager.DoExpirations - an item that has not loaded within 60 s loses its events. Checked once a second.</summary>
        private void ExpireDeferredEvents()
        {
            if (m_DeferredEvents.Count == 0) return;
            ulong now = InWorldz.Phlox.Util.Clock.Now;
            if (now < m_NextDeferredExpiry) return;
            m_NextDeferredExpiry = now + 1000;
            List<UUID> expired = null;
            foreach (var kvp in m_DeferredEvents)
                if (now >= kvp.Value.ExpiresOn) (expired ??= new List<UUID>()).Add(kvp.Key);
            if (expired == null) return;
            foreach (UUID id in expired)
            {
                m_DroppedForUnloaded += m_DeferredEvents[id].Events.Count;
                m_DeferredEvents.Remove(id);
            }
        }

        /// <summary>The item's load was cancelled (removed while compiling) - its held events go with it.</summary>
        internal void DropDeferred(UUID itemId)
        {
            if (m_DeferredEvents.Remove(itemId, out var list)) m_DroppedForUnloaded += list.Events.Count;
        }

        /// <summary>Held events for items not loaded, and events dropped because their item was not loaded (tests, leak check).</summary>
        internal (int Items, int Events, long Dropped) DeferredStats()
        {
            int events = 0;
            foreach (var kvp in m_DeferredEvents) events += kvp.Value.Events.Count;
            return (m_DeferredEvents.Count, events, m_DroppedForUnloaded);
        }

        /// <summary>Events posted and not yet taken by ProcessEventQueue (tests, leak check).</summary>
        internal int PendingEventCount { get { lock (m_PendingEvents) return m_PendingEvents.Count; } }
    }
}
