using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;
using OpenMetaverse;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// Stores information about an event that is contained in a script.
    /// This class is directly serializable by protobuf
    /// </summary>
    [ProtoContract]
    public class EventInfo
    {
        //an approximation of the size of this struct
        public const int MemSize = 48;

        [ProtoMember(1)]
        public int StateId;
        
        [ProtoMember(2)]
        public string EventName;

        [ProtoMember(3)]
        public int NumberOfArguments;

        [ProtoMember(4)]
        public int NumberOfLocals;

        [ProtoMember(5)]
        public int Address;

        [ProtoMember(6)]
        public int EventType;



        public EventInfo()
        {
        }

        public EventInfo(int stateId, string eventName, int numArgs, int numLocals, 
            int address, int eventType)
        {
            StateId = stateId;
            EventName = eventName;
            NumberOfArguments = numArgs;
            NumberOfLocals = numLocals;
            Address = address;
            EventType = eventType;
        }

        public FunctionInfo ToFunctionInfo()
        {
            return new FunctionInfo
            {
                Address = this.Address,
                Name = this.EventName,
                NumberOfArguments = this.NumberOfArguments,
                NumberOfLocals = this.NumberOfLocals
            };
        }
    }
}
