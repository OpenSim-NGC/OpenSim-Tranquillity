using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    [ProtoContract]
    public class SerializedPostedEvent
    {
        [ProtoMember(1, IsRequired=true)]
        public Types.SupportedEventList.Events EventType;

        [ProtoMember(2)]
        public SerializedLSLPrimitive[] Args;

        [ProtoMember(3)]
        public VM.DetectVariables[] DetectVars;

        [ProtoMember(4)]
        public int TransitionToState;


        public SerializedPostedEvent()
        {
        }

        /// <summary>
        /// Creates a new serialized event from a posted event
        /// </summary>
        /// <param name="evt"></param>
        /// <returns></returns>
        public static SerializedPostedEvent FromPostedEvent(VM.PostedEvent evt)
        {
            if (evt == null) return null;

            SerializedPostedEvent serEvent = new SerializedPostedEvent();
            serEvent.EventType = evt.EventType;

            serEvent.Args = SerializedLSLPrimitive.FromPrimitiveList(evt.Args);

            serEvent.DetectVars = evt.DetectVars;

            serEvent.TransitionToState = evt.TransitionToState;

            return serEvent;
        }

        internal VM.PostedEvent ToPostedEvent()
        {
            VM.PostedEvent evt = new VM.PostedEvent();
            evt.EventType = this.EventType;

            evt.Args = SerializedLSLPrimitive.ToPrimitiveList(this.Args);

            evt.DetectVars = this.DetectVars;

            evt.TransitionToState = this.TransitionToState;

            return evt;
        }
    }
}
