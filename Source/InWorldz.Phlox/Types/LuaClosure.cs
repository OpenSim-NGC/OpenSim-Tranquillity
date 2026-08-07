namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// A heap cell holding one captured (upvalue) variable. A captured local lives in a cell (not
    /// the frame's flat slot) so it outlives the enclosing scope, and the cell is shared BY
    /// REFERENCE between the defining frame and any closures that capture it — giving correct Lua
    /// shared-upvalue semantics (a write through one closure is seen by the other).
    /// </summary>
    public class UpvalCell
    {
        public object Value;
        public UpvalCell() { }
        public UpvalCell(object v) { Value = v; }
    }

    /// <summary>
    /// A first-class function value: a code reference (FunctionInfo) plus its captured upvalue cells.
    /// type(closure) == "function". Calling it (the `callv` opcode) sets up a frame for Fn whose
    /// upvalue access (getupval/setupval) reads/writes Upvals[i].Value.
    /// </summary>
    public class LuaClosure
    {
        public VM.FunctionInfo Fn;
        public UpvalCell[] Upvals;
        public LuaClosure() { }
        public LuaClosure(VM.FunctionInfo fn, UpvalCell[] ups) { Fn = fn; Upvals = ups; }
    }
}
