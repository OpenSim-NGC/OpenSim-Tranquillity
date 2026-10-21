/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>
/// Keeps a log line that a remote party or a script can trigger without limit from filling the log.
/// </summary>
/// <remarks>
/// <para>One gate belongs to one call site (a <c>static readonly</c> field next to the log call). The first
/// occurrence in a window is written in full at the level the caller chose. Later occurrences in the window are
/// counted and not written. The next occurrence after the window is written in full with
/// "(N more like this in the last T s)" appended, and opens the next window. If no further occurrence ever comes,
/// the held-back count is never written.</para>
/// <para>The window is <see cref="WindowSeconds"/> seconds. The gate has no key, so what a caller sends (an address,
/// a grid, a script, an agent) cannot grow it: its memory is one counter and one timestamp per call site.</para>
/// <para>Callers on any thread may share a gate; the counter and the window are updated under one lock, held only
/// for that update and never while logging.</para>
/// </remarks>
public sealed class RateLimitedLog
{
    /// <summary>Length of the window, in seconds.</summary>
    public const int WindowSeconds = 60;

    private const long WindowMs = WindowSeconds * 1000L;

    private static Func<long>? s_clockForTesting;
    private static int s_clockEpoch;

    private readonly Func<long>? m_clock;
    private readonly object m_lock = new();
    private bool m_started;
    private long m_windowStartMs;
    private long m_held;
    private int m_epoch;

    public RateLimitedLog() { }

    /// <summary>A gate that reads <paramref name="clockMs"/> (milliseconds) instead of the system clock.</summary>
    public RateLimitedLog(Func<long> clockMs)
    {
        m_clock = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
    }

    /// <summary>
    /// Makes every gate without its own clock read <paramref name="clockMs"/> (milliseconds), and forgets what
    /// those gates have counted so far; null returns to the system clock. For tests that drive a real call path.
    /// The clock is process-wide, so tests that use it must not run in parallel with each other.
    /// </summary>
    public static void UseClockForTesting(Func<long>? clockMs)
    {
        s_clockForTesting = clockMs;
        Interlocked.Increment(ref s_clockEpoch);
    }

    private long Now() => m_clock?.Invoke() ?? s_clockForTesting?.Invoke() ?? Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

    /// <summary>
    /// True when the caller should write its line now. <paramref name="held"/> is how many occurrences were counted
    /// since the line before, and <paramref name="elapsedSeconds"/> how long ago that line was written (0 for the first).
    /// </summary>
    public bool TryAcquire(out long held, out long elapsedSeconds)
    {
        long now = Now();
        int epoch = Volatile.Read(ref s_clockEpoch);
        lock (m_lock)
        {
            if (m_epoch != epoch)
            {
                m_epoch = epoch;
                m_started = false;
                m_held = 0;
            }

            if (m_started && now - m_windowStartMs < WindowMs)
            {
                if (m_held < long.MaxValue) m_held++;
                held = 0;
                elapsedSeconds = 0;
                return false;
            }

            held = m_held;
            elapsedSeconds = m_started ? (now - m_windowStartMs) / 1000 : 0;
            m_held = 0;
            m_windowStartMs = now;
            m_started = true;
            return true;
        }
    }

    /// <summary>Writes <paramref name="message"/> through <paramref name="logger"/> if the gate lets it through.</summary>
    public void Log(ILogger logger, LogLevel level, string message, params object?[] args)
    {
        if (!logger.IsEnabled(level) || !TryAcquire(out long held, out long elapsed))
            return;
        logger.Log(level, held > 0 ? message + Summary(held, elapsed) : message, args);
    }

    /// <summary>As <see cref="Log(ILogger, LogLevel, string, object?[])"/>, with an exception on the written line.</summary>
    public void Log(ILogger logger, LogLevel level, Exception exception, string message, params object?[] args)
    {
        if (!logger.IsEnabled(level) || !TryAcquire(out long held, out long elapsed))
            return;
        logger.Log(level, exception, held > 0 ? message + Summary(held, elapsed) : message, args);
    }

    private static string Summary(long held, long elapsedSeconds)
        => " (" + held + " more like this in the last " + elapsedSeconds + " s)";
}
