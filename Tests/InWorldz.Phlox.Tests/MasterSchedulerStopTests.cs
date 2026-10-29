using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// <c>PhloxMasterScheduler.StopThread</c> sets <c>m_Stop</c>, signals once and joins for up to 5 s. The work
/// loop used to read <c>m_Stop</c> and only then Reset the signal, so a stop landing between the two was erased; with
/// no work queued the loop then waited forever and the join timed out (about 1 harness stop in 12). A region
/// stop that met it took 5 s longer and left the thread blocked. Here a real master scheduler is started and stopped
/// many times, in parallel lanes, with the loop kept busy by work signals right up to the stop (the moment the race
/// needs), and every stop must end the thread well inside the join.
/// </summary>
// Runs in parallel: every cycle builds its own schedulers; nothing process-wide is touched.
public class MasterSchedulerStopTests
{
    private const int Lanes = 8;
    private const int CyclesPerLane = 100;   // 800 cycles in all
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(1);

    private readonly ITestOutputHelper _out;
    public MasterSchedulerStopTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// A loader whose DoWork answers "stopped, nothing pending, no wake-up" at once (PhloxScriptLoader.DoWork's first
    /// line). Built without its constructor, so no cache folder and no compile thread: the master loop only needs DoWork.
    /// </summary>
    private static PhloxScriptLoader StoppedLoader()
    {
        var loader = (PhloxScriptLoader)RuntimeHelpers.GetUninitializedObject(typeof(PhloxScriptLoader));
        typeof(PhloxScriptLoader).GetField("m_Stopped", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(loader, true);
        return loader;
    }

    private static Thread ThreadOf(PhloxMasterScheduler ms)
        => (Thread)typeof(PhloxMasterScheduler).GetField("m_Thread", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ms);

    [Fact]
    public void EveryStopEndsTheThreadWellInsideTheJoin()
    {
        var slow = new ConcurrentBag<(int Lane, int Cycle, double Ms, bool Alive)>();
        var times = new ConcurrentBag<double>();

        Parallel.For(0, Lanes, new ParallelOptions { MaxDegreeOfParallelism = Lanes }, lane =>
        {
            var rng = new Random(lane * 7919 + 1);
            for (int cycle = 0; cycle < CyclesPerLane; cycle++)
            {
                PhloxMasterScheduler ms = null;
                var exe = new PhloxExecutionScheduler(() => ms?.WorkArrived(), null, null);
                ms = new PhloxMasterScheduler(exe, StoppedLoader());
                ms.Start();
                var thread = ThreadOf(ms);

                // Keep the loop cycling (each signal is one more pass), then stop from the same thread, so nothing can
                // re-signal after the stop: a lost stop stays lost.
                int signals = rng.Next(0, 400);
                for (int i = 0; i < signals; i++)
                {
                    ms.WorkArrived();
                    if ((i & 15) == 0) Thread.SpinWait(rng.Next(1, 200));
                }

                var sw = Stopwatch.StartNew();
                ms.StopThread();
                sw.Stop();
                bool alive = thread.IsAlive;
                times.Add(sw.Elapsed.TotalMilliseconds);

                if (sw.Elapsed >= StopLimit || alive)
                {
                    slow.Add((lane, cycle, sw.Elapsed.TotalMilliseconds, alive));
                    // Release a stuck thread (m_Stop is already set) so a failing run leaves nothing behind.
                    var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    do ms.WorkArrived();
                    while (!thread.Join(10) && DateTime.UtcNow < until);
                }
            }
        });

        var all = times.ToArray();
        _out.WriteLine($"{all.Length} stops; max {all.Max():F1} ms; mean {all.Average():F2} ms; slow or stuck {slow.Count}");
        foreach (var s in slow.OrderBy(s => s.Lane).ThenBy(s => s.Cycle))
            _out.WriteLine($"  lane {s.Lane} cycle {s.Cycle}: {s.Ms:F0} ms, thread still running after StopThread: {s.Alive}");

        Assert.Equal(Lanes * CyclesPerLane, all.Length);
        Assert.Empty(slow);
    }
}
