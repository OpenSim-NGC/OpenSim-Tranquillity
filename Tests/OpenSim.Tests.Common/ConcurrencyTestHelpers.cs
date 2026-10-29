using System.Collections.Concurrent;

namespace OpenSim.Tests.Common;

/// <summary>
/// Runs the same body on several dedicated threads that start together behind a barrier.
/// </summary>
public static class ConcurrencyTestHelpers
{
    /// <summary>
    /// Run <paramref name="body"/>(threadIndex) on <paramref name="threads"/> threads at once and wait for all of
    /// them, up to <paramref name="cap"/>. Returns every exception the bodies threw. A corrupted Dictionary can spin
    /// forever, so the threads are background threads and a thread still running at the cap fails the test instead
    /// of hanging the run.
    /// </summary>
    public static List<Exception> RunTogether(int threads, Action<int> body, TimeSpan cap)
    {
        ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
        using Barrier start = new Barrier(threads);
        Thread[] workers = new Thread[threads];

        for (int t = 0; t < threads; t++)
        {
            int index = t;
            workers[t] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    body(index);
                }
                catch (Exception e)
                {
                    errors.Enqueue(e);
                }
            })
            { IsBackground = true, Name = "concurrency-test-" + index };
            workers[t].Start();
        }

        DateTime deadline = DateTime.UtcNow + cap;
        foreach (Thread w in workers)
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            if (left < TimeSpan.Zero || !w.Join(left))
                errors.Enqueue(new TimeoutException("A worker thread was still running after " + cap));
        }

        return errors.ToList();
    }
}
