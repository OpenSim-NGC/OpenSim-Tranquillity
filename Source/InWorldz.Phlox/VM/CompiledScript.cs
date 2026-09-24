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

        private string _bytecodeIdentity;

        /// <summary>
        /// Identifies this compiled program: a SHA-256 over the bytecode, the constant pool, the
        /// event table and the global count, as hex. Two compiles with the same identity have the
        /// same code at the same addresses, so a saved execution position can be resumed in either.
        /// </summary>
        public string BytecodeIdentity
        {
            get
            {
                if (_bytecodeIdentity != null) return _bytecodeIdentity;
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(NumGlobals);
                    w.Write(ByteCode?.Length ?? 0);
                    if (ByteCode != null) w.Write(ByteCode);
                    w.Write(ConstPool?.Length ?? 0);
                    if (ConstPool != null)
                        foreach (object c in ConstPool)
                        {
                            w.Write(c?.GetType().FullName ?? "null");
                            w.Write(Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                        }
                    w.Write(StateEvents?.Length ?? 0);
                    if (StateEvents != null)
                        foreach (EventInfo[] events in StateEvents)
                        {
                            w.Write(events?.Length ?? 0);
                            if (events == null) continue;
                            foreach (EventInfo e in events)
                            {
                                if (e == null) { w.Write(-1); continue; }
                                w.Write(e.StateId); w.Write(e.EventName ?? string.Empty);
                                w.Write(e.NumberOfArguments); w.Write(e.NumberOfLocals); w.Write(e.Address);
                            }
                        }
                }
                _bytecodeIdentity = Convert.ToHexString(sha.ComputeHash(ms.ToArray()));
                return _bytecodeIdentity;
            }
        }

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
