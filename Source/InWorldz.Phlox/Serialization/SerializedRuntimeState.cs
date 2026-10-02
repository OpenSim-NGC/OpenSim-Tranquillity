using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    /// <summary>
    /// The state of a script as held on disk or on the wire
    /// </summary>
    [ProtoContract]
    public class SerializedRuntimeState
    {
        [ProtoMember(1, IsRequired=true)]
        public int IP;

        [ProtoMember(2, IsRequired = true)]
        public int LSLState;

        [ProtoMember(3)]
        public SerializedLSLPrimitive[] Globals;

        [ProtoMember(4)]
        public SerializedLSLPrimitive[] Operands;

        [ProtoMember(5)]
        public SerializedStackFrame[] Calls;

        [ProtoMember(6)]
        public SerializedStackFrame TopFrame;

        [ProtoMember(7)]
        public VM.MemoryInfo MemInfo;

        [ProtoMember(8)]
        public SerializedPostedEvent[] EventQueue;

        [ProtoMember(9, IsRequired = true)]
        public VM.RuntimeState.Status RunState;

        [ProtoMember(10)]
        public bool Enabled;

        [ProtoMember(11)]
        public DateTime NextWakeup;

        [ProtoMember(12)]
        public DateTime StateCapturedOn;

        [ProtoMember(13)]
        public DateTime TimerLastScheduledOn;

        [ProtoMember(14)]
        public int TimerInterval;

        [ProtoMember(15)]
        public SerializedPostedEvent RunningEvent;

        /// <summary>Tags 16 and 17: the grant saved with the state, with <see cref="PermsOwner"/>. Declared from the
        /// start but written only since tag 28; an earlier build's ToRuntimeState never reads them.</summary>
        [ProtoMember(16)]
        public string PermsGranter;

        [ProtoMember(17)]
        public int GrantedPermsMask;

        [ProtoMember(18)]
        public Dictionary<int, VM.ActiveListen> ActiveListens;

        [ProtoMember(19)]
        public int StartParameter;

        [ProtoMember(20)]
        public Dictionary<int, SerializedLSLPrimitive[]> MiscAttributes;

        /// <summary>
        /// Which syscall was in flight when the state was captured, as a
        /// <c>FunctionSig.TableIndex</c>, or -1 for none. Without it a script saved mid-syscall could
        /// not be resumed at all: the call has no completion coming, so the only way back is to push
        /// that function's return value ourselves, and that needs to know which function it was.
        /// <para>
        /// Tag 22 was the next free one. <b>Rows written before this field must still load</b>, and they
        /// do: protobuf-net leaves an absent field at its initialised value, which is why this is -1
        /// here and not 0 - 0 is a real table index.
        /// </para>
        /// </summary>
        [ProtoMember(22)]
        public int LastSyscallIndex = -1;

        /// <summary>Tags 23-25. Rows written before them load as 0 / false / 0, which is
        /// "no floor, not profiling, no peak" - exactly what an older script had.</summary>
        [ProtoMember(23)]
        public int MinEventDelayMs;
        [ProtoMember(24)]
        public bool ProfilingMemory;
        [ProtoMember(25)]
        public int PeakMemoryUsed;

        /// <summary>Tag 26: the error a crashed script stopped on; null for every row written before it and for a script that is not crashed.</summary>
        [ProtoMember(26)]
        public string TerminatedReason;

        [ProtoMember(21)]
        public float TotalRuntime;

        /// <summary>
        /// The CompiledScript.BytecodeIdentity the state was captured on (tag 27). Absent - null -
        /// in rows written before it; see <see cref="ToRuntimeStateFor"/>.
        /// </summary>
        [ProtoMember(27)]
        public string BytecodeIdentity;

        /// <summary>
        /// Tag 28: the object's owner when the grant in tags 16 and 17 was noted. Absent - null - in every row and carried
        /// state written before it, and then no grant is restored, whatever 16 and 17 hold. An earlier build skips the tag.
        /// </summary>
        [ProtoMember(28)]
        public string PermsOwner;

        /// <summary>
        /// Tag 29: the Experience the grant in tags 16 and 17 came from (llRequestExperiencePermissions). Absent - null - for
        /// any other grant and in every row and carried state written before it, which then restore as before. An earlier
        /// build skips the tag.
        /// </summary>
        [ProtoMember(29)]
        public string PermsExperience;

        public SerializedRuntimeState()
        {
        }



        public static SerializedRuntimeState FromRuntimeState(VM.RuntimeState state)
        {
            // Snapshot all mutable collections up front to avoid
            // "Collection was modified" if the execution thread touches
            // the RuntimeState while we are serializing.
            // Catch Exception (not just InvalidOperationException) because
            // C5.CollectionModifiedException does not inherit from InvalidOperationException.
            VM.StackFrame[] callsSnapshot;
            try { callsSnapshot = state.Calls.ToArray(); }
            catch { callsSnapshot = Array.Empty<VM.StackFrame>(); }

            VM.PostedEvent[] eventQueueSnapshot;
            lock (state.EventQueueLock)
            {
                try { eventQueueSnapshot = state.EventQueue.ToArray(); }
                catch { eventQueueSnapshot = Array.Empty<VM.PostedEvent>(); }
            }

            Dictionary<int, VM.ActiveListen> listensSnapshot;
            try { listensSnapshot = new Dictionary<int, VM.ActiveListen>(state.ActiveListens); }
            catch { listensSnapshot = new Dictionary<int, VM.ActiveListen>(); }

            KeyValuePair<int, object[]>[] miscSnapshot;
            try { miscSnapshot = state.MiscAttributes.ToArray(); }
            catch { miscSnapshot = Array.Empty<KeyValuePair<int, object[]>>(); }

            object[] globalsSnapshot;
            try { globalsSnapshot = (object[])state.Globals.Clone(); }
            catch { globalsSnapshot = state.Globals; }

            // The operand stack was the one live collection still walked in place
            // (FromPrimitiveStack enumerates it) while the script thread pushes and pops.
            Stack<object> operandsSnapshot;
            try { operandsSnapshot = new Stack<object>(new Stack<object>(state.Operands)); }
            catch { operandsSnapshot = new Stack<object>(); }

            SerializedRuntimeState serState = new SerializedRuntimeState();
            serState.IP = state.IP;
            serState.LSLState = state.LSLState;
            serState.Globals = SerializedLSLPrimitive.FromPrimitiveList(globalsSnapshot);
            serState.Operands = SerializedLSLPrimitive.FromPrimitiveStack(operandsSnapshot);

            serState.Calls = new SerializedStackFrame[callsSnapshot.Length];
            for (int i = 0; i < callsSnapshot.Length; i++)
            {
                serState.Calls[i] = SerializedStackFrame.FromStackFrame(callsSnapshot[i]);
            }

            serState.TopFrame = SerializedStackFrame.FromStackFrame(state.TopFrame);
            serState.MemInfo = state.MemInfo;

            serState.EventQueue = new SerializedPostedEvent[eventQueueSnapshot.Length];
            for (int i = 0; i < eventQueueSnapshot.Length; i++)
            {
                serState.EventQueue[i] = SerializedPostedEvent.FromPostedEvent(eventQueueSnapshot[i]);
            }

            serState.RunState = state.RunState;
            serState.Enabled = state.GeneralEnable;
            serState.TerminatedReason = state.TerminatedReason;

            UInt64 tickCountNow = Util.Clock.GetLongTickCount();
            serState.StateCapturedOn = DateTime.Now;
            //if the next wakeup is in the past, just filter it to be now equal to the state capture time
            //this prevents strange values from getting into the tickcounttodatetime calculation
            serState.LastSyscallIndex = state.LastSyscallIndex;
            serState.MinEventDelayMs = state.MinEventDelayMs;
            serState.ProfilingMemory = state.ProfilingMemory;
            serState.PeakMemoryUsed = state.PeakMemoryUsed;

            serState.NextWakeup = state.NextWakeup < tickCountNow ? serState.StateCapturedOn : Util.Clock.TickCountToDateTime(state.NextWakeup, tickCountNow);
            serState.TimerLastScheduledOn = Util.Clock.TickCountToDateTime(state.TimerLastScheduledOn, tickCountNow);
            serState.TimerInterval = state.TimerInterval;
            serState.RunningEvent = SerializedPostedEvent.FromPostedEvent(state.RunningEvent);
            serState.BytecodeIdentity = state.BytecodeIdentity;
            serState.PermsGranter = state.PermsGranter;
            serState.GrantedPermsMask = state.GrantedPermsMask;
            serState.PermsOwner = state.PermsOwner;
            serState.PermsExperience = state.PermsExperience;
            serState.ActiveListens = listensSnapshot;
            serState.StartParameter = state.StartParameter;

            serState.MiscAttributes = new Dictionary<int, SerializedLSLPrimitive[]>();
            foreach (KeyValuePair<int, object[]> kvp in miscSnapshot)
            {
                serState.MiscAttributes[kvp.Key] = SerializedLSLPrimitive.FromPrimitiveList(kvp.Value);
            }

            //calculate total runtime
            serState.TotalRuntime = state.TotalRuntime;
            return serState;
        }

        public VM.RuntimeState ToRuntimeState()
        {
            VM.RuntimeState state = new VM.RuntimeState();
            state.IP = this.IP;
            state.LSLState = this.LSLState;
            state.Globals = SerializedLSLPrimitive.ToPrimitiveList(this.Globals);
            state.Operands = SerializedLSLPrimitive.ToPrimitiveStack(this.Operands);

            if (this.Calls != null)
            {
                state.Calls = new Stack<VM.StackFrame>(this.Calls.Length);
                //calls is a stack, so again push them in reverse order
                for (int i = this.Calls.Length - 1; i >= 0; i--)
                {
                    state.Calls.Push(this.Calls[i].ToStackFrame());
                }
            }
            else
            {
                state.Calls = new Stack<VM.StackFrame>();
            }

            if (state.Calls.Count > 0)
            {
                //DO NOT USE THE SERIALIZED TOPFRAME HERE, IT IS A DIFFERENT REFERENCE THAN 
                //state.Calls.Peek!!!
                state.TopFrame = state.Calls.Peek();
            }
            else
            {
                state.TopFrame = null;
            }

            state.MemInfo = this.MemInfo;

            state.EventQueue = new C5.LinkedList<VM.PostedEvent>();
            if (this.EventQueue != null)
            {
                foreach (SerializedPostedEvent evt in this.EventQueue)
                {
                    state.EventQueue.Add(evt.ToPostedEvent());
                }
            }

            state.RunState = this.RunState;
            state.LastSyscallIndex = this.LastSyscallIndex;
            state.MinEventDelayMs = this.MinEventDelayMs;
            state.ProfilingMemory = this.ProfilingMemory;
            state.PeakMemoryUsed = this.PeakMemoryUsed;
            state.NextEventAllowedOn = 0;   // relative to the old process's clock; a restore starts allowed
            state.GeneralEnable = this.Enabled;
            state.TerminatedReason = this.TerminatedReason;

            UInt64 currentTickCount = Util.Clock.GetLongTickCount();

            state.StateCapturedOn = currentTickCount;

            Int64 relativeNextWakeup = (Int64)currentTickCount + (Int64)(this.NextWakeup - this.StateCapturedOn).TotalMilliseconds;
            if (relativeNextWakeup < 0) relativeNextWakeup = 0;

            state.NextWakeup = (UInt64)relativeNextWakeup;

            Int64 relativeTimerLastScheduledOn = (Int64)currentTickCount + (Int64)(this.TimerLastScheduledOn - this.StateCapturedOn).TotalMilliseconds;
            if (relativeTimerLastScheduledOn < 0) relativeTimerLastScheduledOn = 0;

            state.TimerLastScheduledOn = (UInt64)relativeTimerLastScheduledOn;
            state.TimerInterval = this.TimerInterval;

            if (this.RunningEvent != null)
            {
                state.RunningEvent = this.RunningEvent.ToPostedEvent();
            }

            if (this.ActiveListens != null)
            {
                state.ActiveListens = this.ActiveListens;
            }
            else
            {
                state.ActiveListens = new Dictionary<int, VM.ActiveListen>();
            }

            state.StartParameter = this.StartParameter;

            state.MiscAttributes = new Dictionary<int,object[]>();
            if (this.MiscAttributes != null)
            {
                foreach (KeyValuePair<int, SerializedLSLPrimitive[]> kvp in MiscAttributes)
                {
                    state.MiscAttributes[kvp.Key] = SerializedLSLPrimitive.ToPrimitiveList(kvp.Value);
                }
            }

            state.OtherRuntime = TotalRuntime;
            state.StartTimeOnSimulator = Util.Clock.GetLongTickCount();
            state.BytecodeIdentity = this.BytecodeIdentity;
            state.PermsGranter = this.PermsGranter;
            state.GrantedPermsMask = this.GrantedPermsMask;
            state.PermsOwner = this.PermsOwner;
            state.PermsExperience = this.PermsExperience;

            return state;
        }

        /// <summary>
        /// Restores this state to run on <paramref name="script"/>. The IP, call frames, operand
        /// stack and running event only mean something in the bytecode they were captured on. If
        /// the script has been recompiled since (its identity differs), or the row predates the
        /// identity, a state saved mid-event keeps its globals, LSL state, queued events, timers and
        /// listens, drops the in-progress event and resumes idle; <paramref name="recompiledNote"/>
        /// is then the line to log, otherwise null. Unchanged bytecode, and a state saved idle,
        /// restore exactly as <see cref="ToRuntimeState"/> does.
        /// </summary>
        public VM.RuntimeState ToRuntimeStateFor(VM.CompiledScript script, OpenMetaverse.UUID itemId, out string recompiledNote)
        {
            VM.RuntimeState state = ToRuntimeState();
            recompiledNote = null;

            bool sameBytecode = this.BytecodeIdentity != null && this.BytecodeIdentity == script.BytecodeIdentity;
            if (!sameBytecode && state.IsMidEvent)
            {
                state.DropExecutionPosition();
                recompiledNote = $"[PhloxState]: {itemId} recompiled since its state was saved; " +
                                 $"resumed idle in state {state.LSLState}, in-progress event dropped";
            }

            state.BytecodeIdentity = script.BytecodeIdentity;
            return state;
        }
    }
}
