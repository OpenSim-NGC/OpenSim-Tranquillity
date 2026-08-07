using System;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// A script-raised error from SLua <c>error(value)</c> / <c>assert</c>. Carries the raw error
    /// VALUE (Lua errors can be any type, not just a string) so <c>pcall</c> can return it intact.
    /// </summary>
    public class LuaError : Exception
    {
        public readonly object Value;

        public LuaError(object value)
            : base(value as string ?? (value != null ? value.ToString() : "nil"))
        {
            Value = value;
        }
    }
}
