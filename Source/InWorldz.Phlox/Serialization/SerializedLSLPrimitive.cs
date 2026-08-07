using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;
using OpenMetaverse;

namespace InWorldz.Phlox.Serialization
{
    [ProtoContract]
    public class SerializedLSLPrimitive
    {
        public object Value;

        private T? Get<T>() where T : struct
        {
            return (Value != null && Value is T) ? (T?)Value : (T?)null;
        }

        [ProtoMember(1)]
        private int? ValueInt
        {
            get { return Get<int>(); }
            set { Value = value; }
        }

        [ProtoMember(2)]
        private float? ValueFloat
        {
            get { return Get<float>(); }
            set { Value = value; }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [ProtoMember(3, IsRequired=false)]
        [Obsolete]
        private string ValueVectorPbuf1Deprecated
        {
            get 
            { 
                return null; 
            }
            set 
            {
                if (value != null)
                {
                    Value = Vector3.Parse(value);
                }
            }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [ProtoMember(4, IsRequired=false)]
        [Obsolete]
        private string ValueRotationPbuf1Deprecated
        {
            get 
            { 
                return null; 
            }
            set 
            {
                if (value != null)
                {
                    Value = Quaternion.Parse(value);
                }
            }
        }

        [ProtoMember(5)]
        private string ValueString
        {
            get { return (Value != null && Value is string) ? (string)Value : (string)null; }
            set { Value = value; }
        }

        [ProtoMember(6)]
        private SerializedLSLList ValueList
        {
            get { return (Value != null && Value is SerializedLSLList) ? (SerializedLSLList)Value : (SerializedLSLList)null; }
            set { Value = value; }
        }

        [ProtoMember(7)]
        private VM.FunctionInfo ValueFunction
        {
            get { return (Value != null && Value is VM.FunctionInfo) ? (VM.FunctionInfo)Value : (VM.FunctionInfo)null; }
            set { Value = value; }
        }

        [ProtoMember(10)]
        private SerializedLSLTable ValueTable
        {
            get { return (Value != null && Value is SerializedLSLTable) ? (SerializedLSLTable)Value : (SerializedLSLTable)null; }
            set { Value = value; }
        }

        [ProtoMember(11)]
        private bool ValueNil
        {
            get { return Value is Types.LuaNil; }
            set { if (value) Value = Types.LuaNil.Instance; }
        }

        [ProtoMember(12)]
        private bool? ValueBool
        {
            get { return (Value is bool b) ? (bool?)b : null; }
            set { if (value.HasValue) Value = value.Value; }
        }

        [ProtoMember(13)]
        private SerializedLuaGmatch ValueGmatch
        {
            get { return (Value is Types.LuaGmatch g) ? new SerializedLuaGmatch(g) : null; }
            set { if (value != null) Value = value.ToGmatch(); }
        }

        [ProtoMember(14)]
        private SerializedClosure ValueClosure
        {
            get { return (Value is Types.LuaClosure c) ? SerializedClosure.From(c) : null; }
            set { if (value != null) Value = value.ToClosure(); }
        }

        [ProtoMember(15)]
        private SerializedCell ValueCell
        {
            get { return (Value is Types.UpvalCell c) ? SerializedCell.From(c) : null; }
            set { if (value != null) Value = value.ToCell(); }
        }

        // Metatable self-reference marker (e.g. the standard OOP idiom `T.__index = T`): the value is
        // the very table that owns this entry. Resolved back to that table by SerializedLSLTable.ToTable,
        // which is the only place the owning table is known. Lets the common idiom round-trip a cycle.
        [ProtoMember(16)]
        public bool TableSelfRef { get; set; }

		[ProtoMember(8)]
        private SerializedVector3 ValueVector
        {
            get { return (Value != null && Value is Vector3 v) ? new SerializedVector3(v) : null; }
            set { if (value != null) Value = value.ToVector3(); }
        }
        [ProtoMember(9)]
        private SerializedQuaternion ValueRotation
        {
            get { return (Value != null && Value is Quaternion q) ? new SerializedQuaternion(q) : null; }
            set { if (value != null) Value = value.ToQuaternion(); }
        }
        /*
        [ProtoMember(8)]
        private Types.Sentinel ValueSentinel
        {
            get { return (Value != null && Value is Types.Sentinel) ? (Types.Sentinel)Value : (Types.Sentinel)null; }
            set { Value = value; }
        }*/

        public SerializedLSLPrimitive()
        {
        }

        public static SerializedLSLPrimitive FromPrimitive(object obj)
        {
            SerializedLSLPrimitive primitive = new SerializedLSLPrimitive();
            if (obj is Types.LSLList list)
                primitive.Value = SerializedLSLList.FromList(list);
            else if (obj is Types.LSLTable table)
                primitive.Value = SerializedLSLTable.FromTable(table);
            else
                primitive.Value = obj;
            return primitive;
        }

        /// <summary>
        /// Reconstruct a runtime value from a (possibly wrapped) serialized value: SerializedLSLList
        /// -> LSLList, SerializedLSLTable -> LSLTable (recursively), anything else passes through.
        /// </summary>
        public static object ResolveValue(object value)
        {
            if (value is SerializedLSLList sl) return sl.ToList();
            if (value is SerializedLSLTable st) return st.ToTable();
            return value;
        }

        public static object[] ToPrimitiveList(SerializedLSLPrimitive[] serPrimList)
        {
            if (serPrimList == null)
            {
                return new object[0];
            }

            object[] primitiveList = new object[serPrimList.Length];

            for (int i = 0; i < serPrimList.Length; i++)
            {
                SerializedLSLPrimitive obj = serPrimList[i];
                primitiveList[i] = ResolveValue(obj.Value);
            }

            return primitiveList;
        }

        public static SerializedLSLPrimitive[] FromPrimitiveList(object[] primList)
        {
            SerializedLSLPrimitive[] serPrimList = new SerializedLSLPrimitive[primList.Length];

            for (int i = 0; i < primList.Length; i++)
            {
                object obj = primList[i];
                serPrimList[i] = SerializedLSLPrimitive.FromPrimitive(obj);

                /*if (validate)
                {
                    if (!serPrimList[i].IsValid())
                    {
                        throw new SerializationException(
                            String.Format(
                                "FromPrimitiveList: Unable to serialize object to SerializedLSLPrimitive: Type: {0} Value: {1}",
                                obj != null ? obj.GetType().FullName : "null", obj));

                    }
                }*/
            }

            return serPrimList;
        }

        public static SerializedLSLPrimitive[] FromPrimitiveStack(Stack<object> primStack)
        {
            SerializedLSLPrimitive[] serPrimList = new SerializedLSLPrimitive[primStack.Count];

            int i = 0;
            foreach (object obj in primStack)
            {
                serPrimList[i] = SerializedLSLPrimitive.FromPrimitive(obj);
                /*if (validate)
                {
                    if (!serPrimList[i].IsValid())
                    {
                        throw new SerializationException(
                            String.Format(
                                "FromPrimitiveStack: Unable to serialize object to SerializedLSLPrimitive: Type: {0} Value: {1}",
                                obj != null ? obj.GetType().FullName : "null", obj));

                    }
                }*/

                i++;
            }

            return serPrimList;
        }

        public static Stack<object> ToPrimitiveStack(SerializedLSLPrimitive[] serializedLSLPrimitive)
        {
            if (serializedLSLPrimitive == null)
            {
                return new Stack<object>();
            }

            //push the primitives back onto the stack in reverse order
            Stack<object> primStack = new Stack<object>(serializedLSLPrimitive.Length);
            for (int i = serializedLSLPrimitive.Length - 1; i >= 0; i--)
            {
                SerializedLSLPrimitive obj = serializedLSLPrimitive[i];
                primStack.Push(ResolveValue(obj.Value));
            }

            return primStack;
        }

        public bool IsValid()
        {
            if (Value == null)
                return false;

            if (Value is int)
                return true;

            if (Value is float)
                return true;

            if (Value is Vector3)
                return true;

            if (Value is Quaternion)
                return true;

            if (Value is string)
                return true;

            if (Value is SerializedLSLList)
                return true;

            if (Value is SerializedLSLTable)
                return true;

            if (Value is bool)
                return true;

            if (Value is Types.LuaNil)
                return true;

            if (Value is Types.LuaGmatch)
                return true;

            if (Value is Types.LuaClosure)
                return true;

            if (Value is Types.UpvalCell)
                return true;

			if (Value is VM.FunctionInfo)
				return true;
            if (Value is SerializedStackFrame)
                return true;
            /*
            if (Value is Types.Sentinel)
                return true;
            */
            return false;
        }
    }

    // First-class function value: code ref + captured upvalue cell VALUES (by value). Cross-closure
    // cell SHARING is not preserved across a serialize boundary (flagged); single-closure capture
    // (the common case: a returned closure whose defining frame has exited) round-trips correctly.
    [ProtoContract]
    public class SerializedClosure
    {
        [ProtoMember(1)] public VM.FunctionInfo Fn;
        [ProtoMember(2)] public SerializedLSLPrimitive[] Upvals;
        public SerializedClosure() { }
        public static SerializedClosure From(Types.LuaClosure c)
        {
            var s = new SerializedClosure { Fn = c.Fn };
            int n = (c.Upvals != null) ? c.Upvals.Length : 0;
            s.Upvals = new SerializedLSLPrimitive[n];
            for (int i = 0; i < n; i++) s.Upvals[i] = SerializedLSLPrimitive.FromPrimitive(c.Upvals[i].Value);
            return s;
        }
        public Types.LuaClosure ToClosure()
        {
            int n = (Upvals != null) ? Upvals.Length : 0;
            var cells = new Types.UpvalCell[n];
            for (int i = 0; i < n; i++) cells[i] = new Types.UpvalCell(SerializedLSLPrimitive.ResolveValue(Upvals[i].Value));
            return new Types.LuaClosure(Fn, cells);
        }
    }

    [ProtoContract]
    public class SerializedCell
    {
        [ProtoMember(1)] public SerializedLSLPrimitive Inner;
        public SerializedCell() { }
        public static SerializedCell From(Types.UpvalCell c) { return new SerializedCell { Inner = SerializedLSLPrimitive.FromPrimitive(c.Value) }; }
        public Types.UpvalCell ToCell() { return new Types.UpvalCell(SerializedLSLPrimitive.ResolveValue(Inner.Value)); }
    }

    [ProtoContract]
    public class SerializedLuaGmatch
    {
        [ProtoMember(1)] public string Src;
        [ProtoMember(2)] public string Pat;
        [ProtoMember(3)] public int Pos;
        public SerializedLuaGmatch() { }
        public SerializedLuaGmatch(Types.LuaGmatch g) { Src = g.Src; Pat = g.Pat; Pos = g.Pos; }
        public Types.LuaGmatch ToGmatch() { return new Types.LuaGmatch { Src = Src, Pat = Pat, Pos = Pos }; }
    }

    [ProtoContract]
    public class SerializedVector3
    {
        [ProtoMember(1)] public float X;
        [ProtoMember(2)] public float Y;
        [ProtoMember(3)] public float Z;
        public SerializedVector3() { }
        public SerializedVector3(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }
        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    [ProtoContract]
    public class SerializedQuaternion
    {
        [ProtoMember(1)] public float X;
        [ProtoMember(2)] public float Y;
        [ProtoMember(3)] public float Z;
        [ProtoMember(4)] public float W;
        public SerializedQuaternion() { }
        public SerializedQuaternion(Quaternion q) { X = q.X; Y = q.Y; Z = q.Z; W = q.W; }
        public Quaternion ToQuaternion() => new Quaternion(X, Y, Z, W);
    }
}