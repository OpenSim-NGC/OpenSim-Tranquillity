using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    /// <summary>
    /// Serialized version of a VM stackframe
    /// </summary>
    [ProtoContract]
    public class SerializedStackFrame
    {
        [ProtoMember(1)]
        public VM.FunctionInfo FunctionInfo;

        [ProtoMember(2)]
        public int ReturnAddress;

        [ProtoMember(3)]
        public SerializedLSLPrimitive[] Locals;

        [ProtoMember(4)]
        public SerializedClosure Closure; // set when this frame is executing a closure (upvalue access)

        [ProtoMember(5)]
        public int Wanted = -1; // value-call result adjustment (-1 = named-call/event)

        [ProtoMember(6)]
        public int OperandBase;

        public SerializedStackFrame()
        {
        }

        public static SerializedStackFrame FromStackFrame(VM.StackFrame frame)
        {
            if (frame == null) return null;

            SerializedStackFrame serFrame = new SerializedStackFrame();
            serFrame.FunctionInfo = frame.FunctionInfo;

            serFrame.ReturnAddress = frame.ReturnAddress;

            serFrame.Locals = new SerializedLSLPrimitive[frame.Locals.Length];
            for (int i = 0; i < serFrame.Locals.Length; i++)
            {
                serFrame.Locals[i] = SerializedLSLPrimitive.FromPrimitive(frame.Locals[i]);
            }

            serFrame.Closure = (frame.Closure != null) ? SerializedClosure.From(frame.Closure) : null;
            serFrame.Wanted = frame.Wanted;
            serFrame.OperandBase = frame.OperandBase;

            return serFrame;
        }

        public VM.StackFrame ToStackFrame()
        {
            VM.StackFrame frame = new VM.StackFrame(this.FunctionInfo, this.ReturnAddress);
            frame.Locals = SerializedLSLPrimitive.ToPrimitiveList(this.Locals);
            frame.Closure = (this.Closure != null) ? this.Closure.ToClosure() : null;
            frame.Wanted = this.Wanted;
            frame.OperandBase = this.OperandBase;

            return frame;
        }
    }
}
