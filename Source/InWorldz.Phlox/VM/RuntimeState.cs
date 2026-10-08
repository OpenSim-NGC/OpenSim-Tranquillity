using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// This reprensents the runtime state of a script. From the current IP to the callstack    
    /// </summary>
    public class RuntimeState
    {
        /// <summary>
        /// Maximum number of elements allowed in the event queue before we drop events
        /// </summary>
        public const int MAX_EVENT_QUEUE_SIZE = 64;

        public int IP;

        /// <summary>
        /// The LSL state id this script is currently in
        /// </summary>
        public int LSLState;

        /// <summary>
        /// Global variables
        /// </summary>
        public object[] Globals;

        /// <summary>
        /// The operand stack
        /// </summary>
        public Stack<object> Operands;

        /// <summary>
        /// The callstack
        /// </summary>
        public Stack<StackFrame> Calls;

        /// <summary>
        /// The top frame on the callstack
        /// </summary>
        public StackFrame TopFrame;

        /// <summary>
        /// Memory information for this running script
        /// </summary>
        public MemoryInfo MemInfo;

        /// <summary>
        /// A queue of events waiting to be processed
        /// </summary>
        public C5.LinkedList<PostedEvent> EventQueue;

        /// <summary>
        /// Guards EventQueue against concurrent access between executor and serialization threads
        /// </summary>
        public readonly object EventQueueLock = new object();

        /// <summary>
        /// The runtime state of a script
        /// </summary>
        public enum Status
        {
            /// <summary>
            /// The script is running and will continue execution on the next tick
            /// </summary>
            Running,

            /// <summary>
            /// The script is waiting after a HALT. This usually means it has been initialized
            /// and is waiting for events or has finished running an event. Scripts waiting for
            /// a timer to fire are also in this state
            /// </summary>
            Waiting,

            /// <summary>
            /// The script is sleeping and will be started again at it's wakeup time. This is used
            /// for calls to LLsleep and event delays
            /// </summary>
            Sleeping,

            /// <summary>
            /// The script is in a long running system call. This is a special status for system
            /// calls that make RPCs on the backend. This script will be kept on the run queue but
            /// will not receive any more clock ticks until this status changes
            /// </summary>
            Syscall,

            /// <summary>
            /// The script has been killed and should be removed at the next tick
            /// </summary>
            Killed
        }

        /// <summary>
        /// The runstate for the script
        /// </summary>
        public Status RunState;

        /// <summary>
        /// Regardless of the state, a script can also be disabled and 
        /// frozen in it's current runstate
        /// </summary>
        public bool GeneralEnable;

        /// <summary>
        /// The TableIndex of the syscall most recently dispatched, so a script parked in
        /// Status.Syscall can say WHICH call it is parked in. Diagnostic only - never persisted,
        /// never read by the VM.
        /// </summary>
        public int LastSyscallIndex = -1;

        /// <summary>
        /// The sequence number of the syscall this script most recently parked in
        /// Status.Syscall. Every off-thread call takes a process-wide unique number
        /// (SyscallContext.NextSeq, so a reset's fresh state cannot reuse one); a return is applied only if
        /// it carries the current one, so a result that arrives after a reset, a state change or a
        /// timeout cannot be pushed into a later call. Not persisted: a restored script has no
        /// off-thread call outstanding.
        /// </summary>
        public int SyscallSeq;

        /// <summary>llMinEventDelay: the floor, in ms, between event handler starts for
        /// this script. 0 = none. Persisted (tag 23); old rows load as 0.</summary>
        public int MinEventDelayMs = 0;

        /// <summary>The Clock tick before which the next handler may not start. Not
        /// persisted - it is relative to this process's clock and a restore starts allowed.</summary>
        public ulong NextEventAllowedOn = 0;

        /// <summary>llScriptProfiler(PROFILE_SCRIPT_MEMORY) is on (tag 24).</summary>
        public bool ProfilingMemory = false;

        /// <summary>High-water mark of MemInfo.MemoryUsed since profiling last started,
        /// sampled at event boundaries and at the two API reads (tag 25).</summary>
        public int PeakMemoryUsed = 0;

        /// <summary>Fold the current usage into the peak while profiling.</summary>
        public void SampleMemoryPeak()
        {
            if (ProfilingMemory && MemInfo != null && MemInfo.MemoryUsed > PeakMemoryUsed)
                PeakMemoryUsed = MemInfo.MemoryUsed;
        }

        /// <summary>
        /// The next time this script should be woken up from a sleep
        /// </summary>
        public UInt64 NextWakeup;

        /// <summary>
        /// The date and time the state of this script was captured
        /// </summary>
        public UInt64 StateCapturedOn;

        /// <summary>
        /// The last time a timer was scheduled
        /// </summary>
        public UInt64 TimerLastScheduledOn;

        /// <summary>
        /// Interval of the set timer in ms
        /// </summary>
        public int TimerInterval;

        /// <summary>
        /// The event that is currently running
        /// </summary>
        public PostedEvent RunningEvent;

        /// <summary>
        /// The <see cref="CompiledScript.BytecodeIdentity"/> of the bytecode this state runs on. It
        /// is saved with the state so a restore can tell whether the IP, call frames and operand
        /// stack still belong to the bytecode they are restored into. Null for a state saved before
        /// the identity was recorded.
        /// </summary>
        public string BytecodeIdentity;

        /// <summary>
        /// True while an event handler is in progress: running, asleep in llSleep, or parked in a
        /// syscall. A Waiting script holds no execution position; a Killed one is left alone.
        /// </summary>
        public bool IsMidEvent =>
            RunState == Status.Running || RunState == Status.Sleeping || RunState == Status.Syscall;

        /// <summary>
        /// Drops the in-progress event (running event, call frames, IP, operand stack and any
        /// sleep or pending syscall) and leaves the script Waiting. Globals, the LSL state, queued
        /// events, timers and listens are kept.
        /// </summary>
        public void DropExecutionPosition()
        {
            RunningEvent = null;
            Calls.Clear();
            TopFrame = null;
            Operands.Clear();
            IP = 0;
            NextWakeup = 0;
            LastSyscallIndex = -1;   // No parked syscall for the scheduler to resume
            RunState = Status.Waiting;
        }

        /// <summary>
        /// The UUID of the agent that has granted the latest perms
        /// </summary>
        public string PermsGranter; 

        /// <summary>
        /// Permissions that have been granted by the grantor
        /// </summary>
        public int GrantedPermsMask;

        /// <summary>
        /// The object's owner when <see cref="PermsGranter"/> and <see cref="GrantedPermsMask"/> were noted. These three
        /// are the grant saved with the state: the script item's grant at capture, or a grant restored from carried state
        /// that is still waiting for its granter. A restore gives a grant back only while the owner is still this one.
        /// </summary>
        public string PermsOwner;

        /// <summary>
        /// The Experience the saved grant came from (llRequestExperiencePermissions), noted with <see cref="PermsGranter"/>;
        /// null for any other grant. A grant from an Experience is restored from carried state only as that Experience
        /// would grant it again with no dialog.
        /// </summary>
        public string PermsExperience;

        /// <summary>
        /// The saved grant is a claim from carried state that was still waiting for its granter when it was noted, not a
        /// grant the script item held. Every restore decides it as carried state is decided, never whole.
        /// </summary>
        public bool PermsUnverified;

        /// <summary>
        /// The Experience whose grant the script item holds now, and that grant's granter; null when the item's grant did
        /// not come from an Experience. Not saved itself: a capture notes it as <see cref="PermsExperience"/> while the
        /// item's granter is still <see cref="ExperienceGranter"/>.
        /// </summary>
        public string ExperienceGrant;
        public string ExperienceGranter;

        /// <summary>
        /// Active listens this script has open
        /// </summary>
        public Dictionary<int, ActiveListen> ActiveListens;

        /// <summary>
        /// The start parameter that was passed to this script
        /// </summary>
        public int StartParameter;

        public enum MiscAttr
        {
            //true/false
            VolumeDetect,

            //string name, key id, integer type, float range, float arc, float rate
            SensorRepeat,

            //int controls, int accept, int pass_on
            Control,

            // PERMISSION_SILENT_ESTATE_MANAGEMENT persistence
            SilentEstateManagement
        }

        /// <summary>
        /// Miscellaneous persisted attributes for each script such as volume detect
        /// </summary>
        public Dictionary<int, object[]> MiscAttributes;

        /// <summary>
        /// The time this script was started on this simulator since
        /// the last llResetTime call
        /// 
        /// This is a transient property and not persisted
        /// </summary>
        public UInt64 StartTimeOnSimulator;

        /// <summary>
        /// The total runtime of this script on OTHER simulators or during previous
        /// sessions since the last reset of llResetTime call. This field is only set
        /// by state restores
        /// </summary>
        public float OtherRuntime;

        /// <summary>
        /// Calculates the total time this script has been running including all other sessions
        /// </summary>
        public float TotalRuntime
        {
            get
            {
                return OtherRuntime + ((Util.Clock.GetLongTickCount() - StartTimeOnSimulator) / 1000.0f);
            }
        }

        [Flags]
        public enum LocalDisableFlag
        {
            /// <summary>
            /// Script is not locally disabled on the simulator
            /// </summary>
            None            = 0,

            /// <summary>
            /// Script has been disabled due to a parcel level constraint
            /// </summary>
            Parcel          = 1,

            /// <summary>
            /// Script has been disabled until the avatar is crossed into the region
            /// </summary>
            CrossingWait    = (1 << 1),

            /// <summary>
            /// The script's saved state row could not be read at load; the script is held
            /// here, with a fresh interpreter that must never run or save, so the row survives for
            /// the next process to try again.
            /// </summary>
            StateLoadFailed = (1 << 2)
        }

        /// <summary>
        /// The current simulator local enabled state
        /// 
        /// This is a transient property and not persisted
        /// </summary>
        public LocalDisableFlag LocalDisable;

        /// <summary>
        /// Why TerminateWithError stopped this script, or null. Persisted (SerializedRuntimeState tag 26)
        /// so a crashed script restores stopped and `phlox status` can say why; cleared by a reset, which is how
        /// it comes back.
        /// </summary>
        public string TerminatedReason;

        /// <summary>
        /// Combines the persisted disabled flag with the local simulator flag
        /// </summary>
        public bool Enabled
        {
            get
            {
                return GeneralEnable && LocalDisable == LocalDisableFlag.None;
            }
        }

        /// <summary>
        /// Determines if an avatar currently has a mouse down on this script
        /// 
        /// This is a transient property and not persisted
        /// </summary>
        public bool TouchActive;

        /// <summary>
        /// The detect variables received to fill the latest touch() event
        /// 
        /// This is a transient property and not persisted
        /// </summary>
        public DetectVariables[] CurrentTouchDetectVars;


        /// <summary>
        /// For serialization
        /// </summary>
        public RuntimeState()
        {
            LocalDisable = LocalDisableFlag.None;
        }

        /// <summary>
        /// Standard ctor for new runtime state
        /// </summary>
        /// <param name="numGlobals">Number of global variables in the associated script</param>
        public RuntimeState(int numGlobals)
        {
            // A brand-new script is ENABLED. Only Reset() used to set this, so a fresh
            // instance was born with GeneralEnable false, and PhloxExecutionScheduler.FinishedLoading
            // computes the script's event mask (:184) BEFORE the freshStart branch resets it (:190).
            // LSLSystemAPI.SetScriptEventFlags gates the whole mask on GeneralEnable
            // (LSLSystemAPI.cs:96), so the mask went to the part as ZERO: the region never learned
            // the prim was touchable, the viewer showed no touch cursor, and touch_start could never
            // fire. state_entry still ran, because ProcessEventQueue lets STATE_ENTRY past a
            // disabled script (:664) - which is exactly what was seen in world.
            GeneralEnable = true;
            MemInfo = new MemoryInfo();
            Globals = new object[numGlobals];


            Operands = new Stack<object>(64);
            Calls = new Stack<StackFrame>(64);
            EventQueue = new C5.LinkedList<PostedEvent>();
            ActiveListens = new Dictionary<int, ActiveListen>();
            MiscAttributes = new Dictionary<int, object[]>();
            LocalDisable = LocalDisableFlag.None;
            this.Reset();
        }

        /// <summary>
        /// Resets the runtime state of this script putting IP back 
        /// at the init state and clearing out the call and operand stack
        /// </summary>
        public void Reset()
        {
            MemInfo.MemoryUsed = 0;
            RunState = Status.Running;
            GeneralEnable = true;
            IP = 0;
            LSLState = 0;
            TopFrame = null;
            Calls.Clear();
            Operands.Clear();
            lock (EventQueueLock)   // Every mutation under the lock the saver snapshots under
            {
                foreach (PostedEvent evt in EventQueue) evt.SignalCompleted();   // no waiter waits for a cleared event
                EventQueue.Clear();
            }
            NextWakeup = 0;
            StateCapturedOn = 0;
            TimerLastScheduledOn = 0;
            TimerInterval = 0;
            RunningEvent = null;
            PermsGranter = String.Empty;
            GrantedPermsMask = 0;
            PermsOwner = null;
            PermsExperience = null;
            PermsUnverified = false;
            ExperienceGrant = null;
            ExperienceGranter = null;
            ActiveListens.Clear();
            StartParameter = 0;

            //null out all globals
            for (int i = 0; i < Globals.Length; i++)
            {
                Globals[i] = null;
            }

            this.ResetRuntime();
        }

        /// <summary>
        /// Pushes the given args onto the stack and positions the function pointer
        /// </summary>
        /// <param name="info"></param>
        /// <param name="args"></param>
        public void DoEvent(EventInfo info, PostedEvent triggeringEvent, IList<object> args)
        {
            TopFrame = new StackFrame(info.ToFunctionInfo(), 0);
            Calls.Push(TopFrame);
            
            IP = info.Address;

            Debug.Assert(args.Count == info.NumberOfArguments);
            for (int i = 0; i < args.Count; i++)
            {
                TopFrame.Locals[i] = args[i];
            }

            RunState = Status.Running;
            RunningEvent = triggeringEvent;

            MemInfo.AddCall(TopFrame);
        }

        /// <summary>
        /// Queues an event if the event queue is below the maximum size
        /// </summary>
        /// <param name="postedEvent"></param>
        public void QueueEvent(PostedEvent postedEvent)
        {
            lock (EventQueueLock)
            {
                if (EventQueue.Count < MAX_EVENT_QUEUE_SIZE || AllowOverflow(postedEvent))
                    EventQueue.Enqueue(postedEvent);
            }
        }

        private static readonly C5.HashSet<Types.SupportedEventList.Events> OVERFLOWABLE_EVENTS = 
            new C5.HashSet<Types.SupportedEventList.Events>
            {
                Types.SupportedEventList.Events.ON_REZ,
                Types.SupportedEventList.Events.STATE_ENTRY,
                Types.SupportedEventList.Events.STATE_EXIT,
                Types.SupportedEventList.Events.TIMER
            };
        private bool AllowOverflow(PostedEvent postedEvent)
        {
            if (OVERFLOWABLE_EVENTS.Contains(postedEvent.EventType))
            {
                return true;
            }

            return false;
        }

        public bool IsEventQueued(InWorldz.Phlox.Types.SupportedEventList.Events evtType)
        {
            foreach (PostedEvent evt in EventQueue)
            {
                if (evt.EventType == evtType)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Clears the callstack, updates memory accounting, halts the script
        /// </summary>
        public void StateChangePrep()
        {
            //update memory accounting, we must deallocate the memory 
            //for calls
            foreach (StackFrame frame in Calls)
            {
                MemInfo.CompleteCall(frame);
            }

            RunState = Status.Waiting;
            TopFrame = null;
            Calls.Clear();
            Operands.Clear();
            lock (EventQueueLock)
            {
                foreach (PostedEvent evt in EventQueue) evt.SignalCompleted();   // no waiter waits for a cleared event
                EventQueue.Clear();
            }
        }

        public DetectVariables GetDetectVariables(int index)
        {
            if (RunningEvent != null && RunningEvent.DetectVars != null && RunningEvent.DetectVars.Length > index)
            {
                return RunningEvent.DetectVars[index];
            }
            else
            {
                return null;
            }
        }

        public void RemoveListen(int handle)
        {
            ActiveListens.Remove(handle);
        }

        public void AddActiveListen(ActiveListen listen)
        {
            ActiveListens[listen.Handle] = listen;
        }

        /// <summary>
        /// Resets the run time value for this script;. This is used in llGetTime
        /// </summary>
        public void ResetRuntime()
        {
            OtherRuntime = 0.0f;
            StartTimeOnSimulator = Util.Clock.GetLongTickCount();
        }

        /// <summary>
        /// Removes any pending timer events on this script's event queue
        /// </summary>
        public void RemovePendingTimerEvent()
        {
            PostedEvent foundEvt;

            lock (EventQueueLock)
            {
                if (EventQueue.Find(
                    delegate (PostedEvent evt) {
                        if (evt.EventType == Types.SupportedEventList.Events.TIMER) return true;
                        return false;
                    },
                    out foundEvt))
                {
                    EventQueue.Remove(foundEvt);
                }
            }
        }
    }
}
