using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.Util;
using ProtoBuf;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// Tracks per script memory usage
    /// </summary>
    [ProtoContract]
    public class MemoryInfo
    {
        /// <summary>
        /// Maximum memory in bytes allowed for all allocations
        /// </summary>
        public const int MAX_MEMORY = 0x20000;

        [ProtoMember(1)]
        public int MemoryUsed;

        public int MemoryFree
        {
            get
            {
                return MAX_MEMORY - MemoryUsed;
            }
        }

        public void UseMemory(int sz)
        {
            MemoryUsed += sz;

            if (MemoryUsed > MAX_MEMORY)
            {
                throw new VMException("Out of memory");
            }
        }

        public void OperandStackPush(object obj)
        {
            MemoryUsed += MemoryCalc.CalcSizeOf(obj);

            if (MemoryUsed > MAX_MEMORY)
            {
                throw new VMException("Out of memory");
            }
        }

        public void ReplaceStored(object lOld, object lNew)
        {
            MemoryUsed += MemoryCalc.CalcSizeOf(lNew) - MemoryCalc.CalcSizeOf(lOld);

            if (MemoryUsed > MAX_MEMORY)
            {
                throw new VMException("Out of memory");
            }
        }

        public void AddCall(StackFrame f)
        {
            MemoryUsed += MemoryCalc.CalcSizeOf(f.Locals);
            MemoryUsed += StackFrame.MemSize;

            if (MemoryUsed > MAX_MEMORY)
            {
                throw new VMException("Out of memory");
            }
        }

        public void CompleteCall(StackFrame f)
        {
            MemoryUsed -= MemoryCalc.CalcSizeOf(f.Locals);
            MemoryUsed -= StackFrame.MemSize;
        }
    }
}
