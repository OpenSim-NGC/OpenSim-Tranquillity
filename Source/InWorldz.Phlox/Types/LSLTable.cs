using System;
using System.Collections.Generic;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// A Luau-style table for SLua-on-Phlox (Tier-2). Unlike <see cref="LSLList"/> (immutable),
    /// a table is a MUTABLE reference type — Lua tables are mutable and shared by reference, so the
    /// single boxed reference on the operand stack / in a slot is mutated in place.
    ///
    /// Minimum-viable model (see SLUA_TIER2.md): an ORDERED map = a Dictionary for O(1) lookup plus
    /// an insertion-ordered key list. The ordering makes iteration stable and, crucially, makes
    /// serialization deterministic so a table survives serialize -> deserialize -> resume mid-pairs.
    /// Keys are normalized: an integral number -> boxed int, strings -> string (so t[1] and t[1.0]
    /// address the same slot, matching Lua). Length = the contiguous integer-key sequence from 1.
    ///
    /// Metatable seam (deferred): all key access funnels through Get/Set, so __index/__newindex can
    /// later hook here (on miss, consult a _metatable field) without touching the VM opcodes or the
    /// front-end. No metatable support in this pass.
    /// </summary>
    public class LSLTable
    {
        public const int MEM_OVERHEAD = 32;

        private readonly Dictionary<object, object> _map;
        private readonly List<object> _keys;   // insertion order (stable iteration + serialization)
        private int _memorySize;

        public int MemorySize { get { return _memorySize + MEM_OVERHEAD; } }

        /// <summary>Number of stored entries (NOT the Lua sequence length; see <see cref="Length"/>).</summary>
        public int Count { get { return _keys.Count; } }

        /// <summary>Keys in insertion order (used by serialization + iteration).</summary>
        public IList<object> OrderedKeys { get { return _keys; } }

        /// <summary>
        /// The table's metatable (or null). Get/Set remain RAW accessors; metamethod dispatch
        /// (__index/__newindex/operators/__call/__tostring/__len) lives in the interpreter opcodes,
        /// which check this cheaply (null = no metatable = fast path, unchanged behavior).
        /// </summary>
        public LSLTable Metatable;

        public LSLTable()
        {
            _map = new Dictionary<object, object>();
            _keys = new List<object>();
            _memorySize = 0;
        }

        /// <summary>Rebuild from ordered (key,value) pairs — used by deserialization.</summary>
        public LSLTable(IList<object> keys, IList<object> values)
        {
            _map = new Dictionary<object, object>(keys.Count);
            _keys = new List<object>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                object k = NormalizeKey(keys[i]);
                if (k == null) continue;
                if (!_map.ContainsKey(k)) _keys.Add(k);
                _map[k] = values[i];
            }
            CalcMemSize();
        }

        /// <summary>
        /// Normalize a key to its canonical stored form: integral float -> int (so t[1]==t[1.0]),
        /// int and string pass through. Other key types are returned as-is (Tier-2 supports int +
        /// string keys; exotic keys are a deferred item).
        /// </summary>
        public static object NormalizeKey(object key)
        {
            if (key == null) return null;
            if (key is int) return key;
            if (key is float f)
            {
                int i = (int)f;
                if (i == f) return i;
                return f;
            }
            return key; // string (or other) passes through
        }

        public object Get(object key)
        {
            object k = NormalizeKey(key);
            if (k == null) return null;
            object v;
            return _map.TryGetValue(k, out v) ? v : null;
        }

        /// <summary>Set t[key]=value. value==null removes the key (Lua nil-assignment semantics).</summary>
        public void Set(object key, object value)
        {
            object k = NormalizeKey(key);
            if (k == null) throw new CheckException("table index is nil");

            if (value == null)
            {
                if (_map.Remove(k)) _keys.Remove(k);
            }
            else
            {
                if (!_map.ContainsKey(k)) _keys.Add(k);
                _map[k] = value;
            }
            CalcMemSize();
        }

        /// <summary>Lua length operator (#t): largest n where keys 1..n are all present.</summary>
        public int Length
        {
            get
            {
                int n = 0;
                while (_map.ContainsKey(n + 1)) n++;
                return n;
            }
        }

        /// <summary>
        /// Lua next(): given a key (null to start), return the following (key,value) in insertion
        /// order. Returns false (out params null) when iteration is exhausted. Stable across a
        /// serialize/deserialize because key order is preserved.
        /// </summary>
        public bool Next(object key, out object nextKey, out object nextValue)
        {
            nextKey = null;
            nextValue = null;

            int idx;
            if (key == null)
            {
                idx = -1;
            }
            else
            {
                object k = NormalizeKey(key);
                idx = _keys.IndexOf(k);
            }

            int ni = idx + 1;
            if (ni < 0 || ni >= _keys.Count) return false;

            nextKey = _keys[ni];
            nextValue = _map[_keys[ni]];
            return true;
        }

        private void CalcMemSize()
        {
            int sz = 0;
            foreach (object k in _keys)
            {
                sz += SizeOfValue(k);
                sz += SizeOfValue(_map[k]);
            }
            _memorySize = sz;
            CheckMemorySize();
        }

        private static int SizeOfValue(object v)
        {
            if (v == null) return 0;
            if (v is LSLTable t) return t.MemorySize;   // nested table (CalcSizeOf can't size it)
            return Util.MemoryCalc.CalcSizeOf(v);        // same accounting LSLList uses
        }

        private void CheckMemorySize()
        {
            // Mirror LSLList's per-container cap (refactor to a shared limit later).
            const int MAX_MEMORY = 0x20000;
            if (_memorySize > MAX_MEMORY)
                throw new CheckException("Out of memory");
        }
    }
}
