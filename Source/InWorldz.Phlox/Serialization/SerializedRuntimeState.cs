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

        [ProtoMember(21)]
        public float TotalRuntime;

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

            SerializedRuntimeState serState = new SerializedRuntimeState();
            serState.IP = state.IP;
            serState.LSLState = state.LSLState;
            serState.Globals = SerializedLSLPrimitive.FromPrimitiveList(globalsSnapshot);
            serState.Operands = SerializedLSLPrimitive.FromPrimitiveStack(state.Operands);

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

            UInt64 tickCountNow = Util.Clock.GetLongTickCount();
            serState.StateCapturedOn = DateTime.Now;
            //if the next wakeup is in the past, just filter it to be now equal to the state capture time
            //this prevents strange values from getting into the tickcounttodatetime calculation
            serState.NextWakeup = state.NextWakeup < tickCountNow ? serState.StateCapturedOn : Util.Clock.TickCountToDateTime(state.NextWakeup, tickCountNow);
            serState.TimerLastScheduledOn = Util.Clock.TickCountToDateTime(state.TimerLastScheduledOn, tickCountNow);
            serState.TimerInterval = state.TimerInterval;
            serState.RunningEvent = SerializedPostedEvent.FromPostedEvent(state.RunningEvent);
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
            state.GeneralEnable = this.Enabled;

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

            return state;
        }
    }
}
