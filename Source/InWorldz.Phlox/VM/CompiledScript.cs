using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using OpenMetaverse;
using InWorldz.Phlox.Util;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// Represents a script after it has been compiled by the byte compiler
    /// </summary>
    public class CompiledScript
    {
        private int _version = 1;
        public int Version
        {
            get
            {
                return _version;
            }

            set
            {
                _version = value;
            }
        }

        public byte[] ByteCode;
        public object[] ConstPool;
        public EventInfo[][] StateEvents;
        
        public int NumGlobals;

        public UUID AssetId;

        /// <summary>
        /// Calculates the base memory size for this script from the
        /// const pool size and bytecode size
        /// </summary>
        /// <returns></returns>
        public int CalcBaseMemorySize()
        {
            int sz = ByteCode.Length;
            sz += MemoryCalc.CalcSizeOf(ConstPool);

            return sz;
        }

        /// <summary>
        /// Finds the event with the given name for the given state
        /// </summary>
        /// <param name="state">The state id</param>
        /// <param name="eventType">The id of the given event</param>
        /// <returns>The event found or null</returns>
        public EventInfo FindEvent(int state, int eventType)
        {
            EventInfo[] evtList = StateEvents[state];
            foreach (EventInfo evt in evtList)
            {
                if (evt.EventType == eventType)
                {
                    return evt;
                }
            }

            return null;
        }
    }
}
