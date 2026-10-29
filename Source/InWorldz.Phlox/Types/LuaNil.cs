namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Singleton sentinel representing Lua/SLua <c>nil</c> on the operand stack and in local/global
    /// slots. We cannot use .NET <c>null</c> for nil because the VM's SafeOperandsPush and _Load
    /// reject null (a bug-guard for the LSL path). LuaNil is a normal object, so it loads/stores and
    /// serializes like any other value. Tables never STORE nil (assigning nil removes the key), so
    /// LuaNil only ever lives transiently on the stack / in slots (e.g. an iteration cursor).
    /// </summary>
    public sealed class LuaNil
    {
        public static readonly LuaNil Instance = new LuaNil();
        private LuaNil() { }
        public override string ToString() { return "nil"; }
    }
}
