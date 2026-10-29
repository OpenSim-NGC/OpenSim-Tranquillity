using System;
using System.Threading;
using OpenMetaverse;

namespace InWorldz.Phlox.Glue
{
    /// <summary>
    /// One syscall running OFF the scheduler thread. The worker enters it for the
    /// duration of the body, so everything the API does on that thread lands here instead of on the
    /// script:
    /// <list type="bullet">
    /// <item>ScriptSleep adds to <see cref="DelayMs"/> instead of writing RunState = Sleeping. That
    /// write, from a worker, used to strand the script: if it landed after the scheduler had taken the
    /// script off the run queue as Syscall, the script was Sleeping but tracked by nothing, and its
    /// return was dropped because it was no longer in Syscall (SyscallSleepRaceTests).</item>
    /// <item>SysReturn records the first result; the single post happens when the body ends.</item>
    /// </list>
    /// <see cref="Seq"/> is the script's syscall sequence number at the moment of the call. The
    /// scheduler applies a return only when it matches the script's CURRENT number, so a result that
    /// arrives after a reset, or after a timeout already answered the call, cannot land in a later
    /// syscall.
    /// </summary>
    public sealed class SyscallContext
    {
        [ThreadStatic] private static SyscallContext t_current;
        private static int s_lastSeq;

        /// <summary>
        /// A process-wide, never-repeating sequence number. Per-script counting is not enough: a reset
        /// can give the script a fresh RuntimeState whose counter starts again at zero, and the first
        /// call after it would then carry the same number as the stale one still outstanding.
        /// </summary>
        public static int NextSeq()
        {
            int s = Interlocked.Increment(ref s_lastSeq);
            if (s <= 0) { Interlocked.CompareExchange(ref s_lastSeq, 1, s); s = Interlocked.Increment(ref s_lastSeq); } // wrap: stay positive
            return s;
        }

        /// <summary>The call this thread is running, or null on the scheduler thread / any other thread.</summary>
        public static SyscallContext Current => t_current;

        public readonly UUID ItemId;
        public readonly int Seq;
        public int DelayMs;
        public object Result;
        public bool HasResult;

        public SyscallContext(UUID itemId, int seq)
        {
            ItemId = itemId;
            Seq = seq;
        }

        public void Enter() { t_current = this; }
        public static void Exit() { t_current = null; }

        public void AddDelay(int ms) { if (ms > 0) DelayMs += ms; }

        /// <summary>First result wins; later ones (a RunAsync completion after the body's own SysReturn) are ignored.</summary>
        public void SetResult(object result, int delayMs)
        {
            if (HasResult) return;
            HasResult = true;
            Result = result;
            AddDelay(delayMs);
        }
    }

    /// <summary>
    /// A syscall that may reach a service (user accounts, grid, assets, experience,
    /// groups, teleport, ...) handed to the scheduler's service lane instead of running inline on the
    /// scheduler thread. <see cref="Body"/> is the unchanged shim call (API call plus LSL conversion),
    /// so the value the script receives is computed by exactly the code that computed it inline.
    /// Exactly one of the worker (result or fault) and the deadline (<see cref="FailureValue"/>) wins
    /// <see cref="TryFinish"/>; the loser is dropped.
    /// </summary>
    public sealed class DeferredServiceCall
    {
        public readonly SyscallContext Context;
        public readonly string FunctionName;
        /// <summary>What the script receives if the deadline passes first; null for a void function.</summary>
        public readonly object FailureValue;
        public readonly Func<object> Body;
        public ulong Deadline;
        private int m_done;

        public DeferredServiceCall(SyscallContext context, string functionName, object failureValue, Func<object> body)
        {
            Context = context;
            FunctionName = functionName;
            FailureValue = failureValue;
            Body = body;
        }

        public bool IsFinished => Volatile.Read(ref m_done) != 0;
        public bool TryFinish() => Interlocked.Exchange(ref m_done, 1) == 0;
    }

    /// <summary>
    /// Implemented by the system API: can this call be answered without leaving the
    /// process (the subject is in the region, or the answer is already in a local cache the service
    /// call would consult first)? False means answer inline, exactly as before; true means defer.
    /// </summary>
    public interface ISyscallDeferralAdvisor
    {
        bool NeedsService(string functionName, object[] args);
    }
}
