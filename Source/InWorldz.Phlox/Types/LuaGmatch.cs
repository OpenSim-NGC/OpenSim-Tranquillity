namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Iterator state for string.gmatch — a mutable value holding the source, pattern, and current
    /// 0-based scan position. Used by the `gmatchnext` opcode (mirroring how `pairs` uses `tabnext`).
    /// It is a plain serializable value (two strings + an int), so a `for w in string.gmatch(...)`
    /// loop round-trips through serialization for free (the state is just an operand/slot value).
    /// </summary>
    public class LuaGmatch
    {
        public string Src;
        public string Pat;
        public int Pos; // 0-based scan position

        public LuaGmatch() { }
        public LuaGmatch(string src, string pat) { Src = src; Pat = pat; Pos = 0; }
    }
}
