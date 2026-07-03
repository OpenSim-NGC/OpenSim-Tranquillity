using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Util
{
    public class Preloader
    {
        public static void Preload()
        {
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(Serialization.SerializedLSLList)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(Serialization.SerializedLSLPrimitive)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(Serialization.SerializedPostedEvent)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(Serialization.SerializedRuntimeState)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(Serialization.SerializedScript)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(Serialization.SerializedStackFrame)].CompileInPlace();

            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(VM.ActiveListen)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(VM.DetectVariables)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(VM.EventInfo)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(VM.FunctionInfo)].CompileInPlace();
            ProtoBuf.Meta.RuntimeTypeModel.Default[typeof(VM.MemoryInfo)].CompileInPlace();
        }
    }
}
