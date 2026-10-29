/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using Xunit;
using Xunit.Abstractions;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The log line for listen events the per-script rate cap ([InWorldz.Phlox] MaxListenEventsPerSecond) refuses: one when a
/// script starts being capped, then at most one a minute with how many were refused since the line before, and none
/// written while the listen lock is held. What the cap refuses does not change. The engine's clock, the cap's second and
/// the log are driven by the test; the clock and the log are process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class ListenCapLogTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly SchedulerHarness H;
    private ulong m_now = 1_000_000;
    private readonly Capture m_log;
    private readonly ILoggerFactory m_previous;

    public ListenCapLogTests(ITestOutputHelper o)
    {
        _out = o;
        m_previous = LoggerProvider.LoggerFactory;
        Clock.SetSourceForTesting(() => m_now);
        H = new SchedulerHarness();
        H.Engine.ListenManager.CurrentSecond = () => (long)(m_now / 1000);
        object listenLock = typeof(global::Phlox.ScriptEngine.PhloxListenManager)
            .GetField("m_Lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(H.Engine.ListenManager);
        m_log = new Capture(() => Monitor.IsEntered(listenLock));
        LoggerProvider.LoggerFactory = m_log;
    }

    public void Dispose()
    {
        try { H.Dispose(); }
        finally { Clock.SetSourceForTesting(null); LoggerProvider.LoggerFactory = m_previous; }
    }

    /// <summary>Captures every line, and counts the cap lines written while the listen lock was held.</summary>
    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<string> m_lines = new();
        private readonly Func<bool> m_lockHeld;
        public int UnderLock;
        public Capture(Func<bool> lockHeld) => m_lockHeld = lockHeld;
        public string[] Lines { get { lock (m_lines) return m_lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                string line = formatter(state, exception);
                if (line.Contains("listen events") && m_c.m_lockHeld()) Interlocked.Increment(ref m_c.UnderLock);
                lock (m_c.m_lines) m_c.m_lines.Add(line);
            }
        }
    }

    private const string Counter =
        "integer n; default { state_entry() { llListen(9, \"\", \"\", \"\"); llSay(0, \"up\"); } " +
        "listen(integer c, string nm, key k, string m) { n++; } }";

    /// <summary>The cap's lines for one script.</summary>
    private string[] CapLines(UUID item)
        => m_log.Lines.Where(l => l.Contains("[PhloxListen]") && l.Contains("listen events") && l.Contains(item.ToString())).ToArray();

    /// <summary>How many refused deliveries a cap line reports.</summary>
    private static int Refused(string line) => int.Parse(Regex.Match(line, @"(\d+) refused").Groups[1].Value);

    private long DroppedListenEvents => H.Engine.ListenManager.DroppedListenEvents;

    /// <summary>Pump with the clock moved 100 ms at a time when idle, up to <paramref name="clock"/>.</summary>
    private void RunTo(ulong clock)
    {
        var wall = DateTime.UtcNow.AddSeconds(60);
        while (m_now < clock && DateTime.UtcNow < wall)
            if (!H.PumpOnceBusy()) m_now = Math.Min(clock, m_now + 100);
    }

    /// <summary>30 lines of chat on the script's channel in the current second: the cap lets 20 through.</summary>
    private void Burst()
    {
        for (int i = 0; i < 30; i++) H.Engine.ListenManager.DeliverChat(9, "Test User", UUID.Random(), "m" + i);
    }

    private UUID Start()
    {
        UUID item = H.RezScript(Counter);
        Assert.True(H.PumpUntil(() => H.Said.Contains("up")), string.Join(" | ", H.Said));
        return item;
    }

    /// <summary>
    /// Capped every second for two and a half minutes: a line when it starts, then one each minute with the refusals
    /// since the line before, the last of them after the capping stopped; not one line a second. A script capped again
    /// after a quiet minute gets a new first line at once. The lines add up to every refused delivery.
    /// </summary>
    [Fact]
    public void ACappedScriptGetsALineWhenItStartsThenOneAMinuteWithTheRefusedCount()
    {
        UUID item = Start();
        m_now = (m_now / 1000 + 1) * 1000;                             // the start of a second
        ulong t0 = m_now;
        for (int s = 0; s < 150; s++)
        {
            RunTo(t0 + (ulong)s * 1000);
            Burst();
            if (s == 0) Assert.Single(CapLines(item));                 // the first line, at once
            if (s == 59) Assert.Single(CapLines(item));                // nothing more before its minute
        }
        Assert.Equal(150 * 10, DroppedListenEvents);                   // the cap itself is unchanged
        RunTo(t0 + 150_500);
        _out.WriteLine("lines after 150 capped seconds: " + CapLines(item).Length);
        Assert.Equal(3, CapLines(item).Length);                        // at the start, 60 s and 120 s
        RunTo(t0 + 240_000);                                           // the refusals after 120 s, at 180 s; then it ends
        _out.WriteLine(string.Join("\n", CapLines(item)));
        Assert.Equal(4, CapLines(item).Length);
        Assert.Equal(DroppedListenEvents, CapLines(item).Sum(Refused)); // every refusal is on a line, once

        RunTo(t0 + 300_000);
        Burst();                                                       // capped again after a quiet minute: a new run
        Assert.Equal(5, CapLines(item).Length);
        Assert.Equal(1, Refused(CapLines(item)[4]));
    }

    /// <summary>No cap line is written while the listen lock is held: a slow log sink would hold up every listen.</summary>
    [Fact]
    public void TheCapLineIsNotWrittenUnderTheListenLock()
    {
        UUID item = Start();
        ulong t0 = m_now;
        for (int s = 0; s < 130; s++)
        {
            RunTo(t0 + (ulong)s * 1000);
            Burst();
        }
        RunTo(t0 + 200_000);
        _out.WriteLine($"cap lines {CapLines(item).Length}, written under the listen lock {m_log.UnderLock}");
        Assert.NotEmpty(CapLines(item));
        Assert.Equal(0, m_log.UnderLock);
    }
}
