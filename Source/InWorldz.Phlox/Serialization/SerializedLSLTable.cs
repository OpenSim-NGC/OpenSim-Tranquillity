using System.Collections.Generic;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    /// <summary>
    /// Protobuf serialization wrapper for <see cref="Types.LSLTable"/>, modeled on
    /// <see cref="SerializedLSLList"/>. Stores entries as ordered parallel key/value lists so the
    /// table's iteration order is preserved across a round-trip (deterministic resume mid-pairs).
    ///
    /// Unlike SerializedLSLList (whose elements are flat LSL primitives that never nest), table
    /// values can be tables or lists, so each key and value is wrapped via
    /// <see cref="SerializedLSLPrimitive.FromPrimitive"/> and rebuilt via
    /// <see cref="SerializedLSLPrimitive.ResolveValue"/> — giving full recursive nesting.
    /// </summary>
    [ProtoContract]
    public class SerializedLSLTable
    {
        [ProtoMember(1)]
        public List<SerializedLSLPrimitive> Keys;

        [ProtoMember(2)]
        public List<SerializedLSLPrimitive> Values;

        // The table->metatable link, serialized by value (the metatable is itself a table). Cross-
        // instance metatable SHARING is not preserved across a serialize boundary (each instance
        // restores its own metatable copy) -- flagged; method lookup still works after restore.
        [ProtoMember(3)]
        public SerializedLSLTable Metatable;

        // The metatable IS this table (setmetatable(t, t)); resolved on restore without recursing.
        [ProtoMember(4)]
        public bool MetatableSelf;

        public SerializedLSLTable()
        {
        }

        /// <summary>
        /// How deeply tables may nest in a saved state, counting the outermost table as 1. Each level is a call of
        /// <see cref="FromTable(Types.LSLTable, HashSet{Types.LSLTable}, int)"/> on the thread taking the capture, so
        /// without a bound a script's values could nest deep enough to overflow that thread's stack, which ends the
        /// process. A capture of values nested deeper fails with a <see cref="SerializationException"/>: the script runs
        /// on, and its state is not saved, as for any capture that fails. A saved state nested deeper does not restore.
        /// The decoder's own limit (protobuf-net TypeModel.MaxDepth, 512 messages, two to a table level) is a little
        /// above this, so every table nesting a capture writes can be read back.
        /// </summary>
        public const int MaxNesting = 200;

        public static SerializedLSLTable FromTable(Types.LSLTable table)
        {
            // 'active' tracks tables currently being serialized in this tree, so cycles never recurse
            // forever. The common idiom T.__index = T (and setmetatable(t,t)) is captured precisely via
            // self markers; any deeper multi-table cycle is broken to nil (flagged limitation).
            return FromTable(table, new HashSet<Types.LSLTable>(), 1);
        }

        private static SerializedLSLTable FromTable(Types.LSLTable table, HashSet<Types.LSLTable> active, int depth)
        {
            if (depth > MaxNesting)
                throw new SerializationException($"tables are nested more than {MaxNesting} deep");

            SerializedLSLTable s = new SerializedLSLTable();
            s.Keys = new List<SerializedLSLPrimitive>(table.Count);
            s.Values = new List<SerializedLSLPrimitive>(table.Count);

            active.Add(table);
            foreach (object k in table.OrderedKeys)
            {
                // Keys follow the rule values follow: the table itself (t[t]) is a self marker, and an enclosing
                // table further up an active cycle drops the entry, as such a value drops to nil.
                if (k is Types.LSLTable kt && !ReferenceEquals(kt, table) && active.Contains(kt)) continue;
                s.Keys.Add(FromValue(k, table, active, depth));
                s.Values.Add(FromValue(table.Get(k), table, active, depth));
            }

            if (table.Metatable != null)
            {
                if (ReferenceEquals(table.Metatable, table)) s.MetatableSelf = true;
                else if (!active.Contains(table.Metatable)) s.Metatable = FromTable(table.Metatable, active, depth + 1);
                // else: metatable is an enclosing table in an active cycle -> dropped (flagged)
            }
            active.Remove(table);

            return s;
        }

        // Serialize one key or value of 'owner' (at nesting 'depth'), with cycle awareness for table-typed values.
        private static SerializedLSLPrimitive FromValue(object v, Types.LSLTable owner, HashSet<Types.LSLTable> active, int depth)
        {
            if (v is Types.LSLTable vt)
            {
                if (ReferenceEquals(vt, owner))                                          // self-reference (T.__index = T)
                    return new SerializedLSLPrimitive { TableSelfRef = true };
                if (active.Contains(vt))                                                 // deeper cycle -> break to nil (flagged)
                    return new SerializedLSLPrimitive { Value = Types.LuaNil.Instance };
                return new SerializedLSLPrimitive { Value = FromTable(vt, active, depth + 1) };   // inline nested table
            }
            return SerializedLSLPrimitive.FromPrimitive(v);
        }

        public Types.LSLTable ToTable()
        {
            return ToTable(1);
        }

        /// <summary>Rebuilds this table at nesting <paramref name="depth"/>; deeper than <see cref="MaxNesting"/> is refused.</summary>
        internal Types.LSLTable ToTable(int depth)
        {
            if (depth > MaxNesting)
                throw new SerializationException($"tables are nested more than {MaxNesting} deep");

            Types.LSLTable t = new Types.LSLTable();
            int n = (Keys != null) ? Keys.Count : 0;

            for (int i = 0; i < n; i++)
            {
                object key = Keys[i].TableSelfRef
                    ? t                                                  // the table is its own key (t[t])
                    : SerializedLSLPrimitive.ResolveValue(Keys[i].Value, depth);
                object val = Values[i].TableSelfRef
                    ? t                                                  // self-reference -> the table itself
                    : SerializedLSLPrimitive.ResolveValue(Values[i].Value, depth);
                if (val is Types.LuaNil) continue;                       // nil / cycle-broken -> absent key
                t.Set(key, val);
            }

            if (MetatableSelf) t.Metatable = t;
            else if (Metatable != null) t.Metatable = Metatable.ToTable(depth + 1);
            return t;
        }
    }
}
