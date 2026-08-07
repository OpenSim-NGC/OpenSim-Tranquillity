namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// A SLua DetectedEvent passed to LLEvents:on handlers — a lightweight wrapper over a 0-based
    /// detection index. Its methods (getKey/getName/getPos/...) map to the existing llDetected*
    /// syscalls, read while the event's detection context is live (handlers run synchronously during
    /// dispatch). Transient (built during dispatch, never stored across a yield), so no serialization.
    /// </summary>
    public class LuaDetected
    {
        public int Index;
        public LuaDetected() { }
        public LuaDetected(int i) { Index = i; }
    }
}
