namespace Phlox.ScriptEngine
{
    internal struct WorkStatus
    {
        public bool WorkWasDone;
        public bool WorkIsPending;
        public ulong NextWakeUpTime;
    }
}
