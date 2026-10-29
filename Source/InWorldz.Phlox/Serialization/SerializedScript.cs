using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;
using OpenMetaverse;

namespace InWorldz.Phlox.Serialization
{
    /// <summary>
    /// A script in a format that can be saved to disk and sent 
    /// over the wire via protocol buffers
    /// </summary>
    [ProtoContract]
    public class SerializedScript
    {
        [ProtoMember(1)]
        public int Version;

        [ProtoMember(2)]
        public byte[] ByteCode;

        [ProtoMember(3)]
        SerializedLSLPrimitive[] ConstPool;

        [ProtoMember(4)]
        Dictionary<int, VM.EventInfo[]> StateEvents;

        [ProtoMember(5)]
        int NumGlobals;

        /// <summary>
        /// Deprecated. Do not use. Retained for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(6, IsRequired=false)]
        public string AssetIdPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                AssetId = UUID.Parse(value);
            }
        }

        [ProtoMember(7)]
        private Guid SerializableAssetId
        {
            get
            {
                return AssetId.Guid;
            }

            set
            {
                AssetId = new UUID(value);
            }
        }

        public UUID AssetId;

        /// <summary>
        /// Creates a new SerializedScript from a CompiledScript
        /// </summary>
        /// <param name="script"></param>
        /// <returns></returns>
        public static SerializedScript FromCompiledScript(VM.CompiledScript script)
        {
            SerializedScript serScript = new SerializedScript();
            serScript.Version = script.Version;
            serScript.ByteCode = script.ByteCode;

            serScript.ConstPool = SerializedLSLPrimitive.FromPrimitiveList(script.ConstPool);

            serScript.StateEvents = new Dictionary<int, VM.EventInfo[]>();
            for (int i = 0; i < script.StateEvents.Length; i++)
            {
                serScript.StateEvents.Add(i, script.StateEvents[i]);
            }

            serScript.NumGlobals = script.NumGlobals;
            serScript.AssetId = script.AssetId;

            return serScript;
        }

        public SerializedScript()
        {
        }

        public VM.CompiledScript ToCompiledScript()
        {
            VM.CompiledScript compScript = new VM.CompiledScript();
            compScript.Version = this.Version;
            compScript.ByteCode = this.ByteCode;

            //const pool, only type that needs changing is the list type
            compScript.ConstPool = SerializedLSLPrimitive.ToPrimitiveList(this.ConstPool);

            if (this.StateEvents != null)
            {
                compScript.StateEvents = new VM.EventInfo[this.StateEvents.Count][];

                for (int i = 0; i < this.StateEvents.Count; i++)
                {
                    compScript.StateEvents[i] = this.StateEvents[i];
                }
            }
            else
            {
                compScript.StateEvents = new VM.EventInfo[0][];
            }
            
            

            compScript.NumGlobals = this.NumGlobals;
            compScript.AssetId = this.AssetId;

            return compScript;
        }
    }
}
