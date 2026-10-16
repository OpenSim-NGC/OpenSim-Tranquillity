/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using Phlox.ScriptEngine;
using Xunit;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The pass over the scheduler's incoming queue at region stop (PhloxExecutionScheduler.QueueIncomingForFinalSave):
/// every script that receives an event in it is marked changed, even when the event was posted while the pass was
/// starting, and a drop it counts is written to the log like any other (the scheduler's thread, which writes those
/// lines, has stopped). The log is captured through LoggerProvider and the engine's clock is replaced by the test, both
/// process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class FinalDrainTests : IDisposable
{
    private const string Sleeper = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); }
            listen(integer c, string n, key k, string m) { if (m == ""nap"") llSleep(200.0); }
            touch_start(integer t) { }
        }";

    private readonly List<string> m_lines = new();
    private readonly ILoggerFactory m_previous;
    private readonly SchedulerHarness H;
    private ulong m_now = 1_000_000;

    public FinalDrainTests()
    {
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = new Capture(m_lines);
        Clock.SetSourceForTesting(() => m_now);
        H = new SchedulerHarness();
    }

    public void Dispose()
    {
        try { H.Dispose(); }
        finally { Clock.SetSourceForTesting(null); LoggerProvider.LoggerFactory = m_previous; }
    }

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<string> m_out;
        public Capture(List<string> o) => m_out = o;
        public ILogger CreateLogger(string category) => new L(m_out);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly List<string> m_o;
            public L(List<string> o) => m_o = o;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_o) m_o.Add(formatter(state, exception)); }
        }
    }

    private string[] FullLines(UUID item)
    {
        lock (m_lines) return m_lines.Where(l => l.Contains("Event queue full") && l.Contains(item.ToString())).ToArray();
    }

    private void SaveStateAtStop()
        => typeof(PhloxEngine).GetMethod("SaveStateAtStop", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(H.Engine, new object[] { true });

    private UUID StartedAndAsleep()
    {
        UUID item = H.RezScript(Sleeper);
        Assert.True(H.PumpUntil(() => H.InterpreterFor(item) != null && H.RunStateOf(item) == "Waiting"), "the script did not start");
        H.Scene.SimChat("nap", ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(H.PumpUntil(() => H.RunStateOf(item) == "Sleeping"), "the script did not go to sleep");
        return item;
    }

    private void Touches(UUID item, int n)
    {
        for (int i = 0; i < n; i++) H.PostTouch(item);
    }

    /// <summary>A queue that is full when the region stops: the touches posted after it are dropped by the final pass.</summary>
    [Fact]
    public void ADropCountedByTheFinalPassIsWrittenToTheLog()
    {
        UUID item = StartedAndAsleep();
        Touches(item, 64);
        H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Empty(FullLines(item));                // 64 fit: nothing dropped yet
        Touches(item, 6);                             // posted, not yet taken up by the scheduler
        Assert.Equal(6, SavedStateRig.Exe(H).PendingEventCount);

        SaveStateAtStop();

        string[] lines = FullLines(item);
        Assert.True(lines.Length == 1, "the queue-full lines: [" + string.Join(" | ", lines) + "]");
        Assert.Equal("[PhloxExe]: Event queue full (64 events) for script " + item + ": dropped 6 TOUCH_START events", lines[0]);
    }

    /// <summary>Drops of an earlier pass, whose next line was not due yet, are written at the stop too, with nothing left to take up.</summary>
    [Fact]
    public void DropsCountedBeforeTheStopAndNotYetWrittenAreWrittenAtTheStop()
    {
        UUID item = StartedAndAsleep();
        Touches(item, 70);                            // 64 wait, 6 dropped: the run starts and writes its first line
        H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Single(FullLines(item));
        Touches(item, 5);
        H.PumpUntilIdle(TimeSpan.FromSeconds(2));     // dropped and counted; the clock has not moved, so no line is due
        Assert.Single(FullLines(item));
        Assert.Equal(0, SavedStateRig.Exe(H).PendingEventCount);

        SaveStateAtStop();

        string[] lines = FullLines(item);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("dropped 5 TOUCH_START events", lines[1]);
    }

    private static HashSet<UUID> Dirty(StateManager sm)
    {
        var dirty = (System.Collections.IDictionary)typeof(StateManager).GetField("m_Dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sm)!;
        object gate = typeof(StateManager).GetField("m_Lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sm)!;
        lock (gate) return new HashSet<UUID>(dirty.Keys.Cast<UUID>());
    }

    private static void ClearDirty(StateManager sm)
    {
        var dirty = (System.Collections.IDictionary)typeof(StateManager).GetField("m_Dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sm)!;
        object gate = typeof(StateManager).GetField("m_Lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sm)!;
        lock (gate) dirty.Clear();
    }

    /// <summary>
    /// An event posted by another thread while the final pass starts: whichever script it is for, if the pass put it on
    /// that script's queue the script is marked changed (so the final save takes it). Each round posts one event for
    /// the second script at a varied moment after the pass was started for an event of the first; a round where the
    /// event arrives before the pass, or after it, is no violation, so many rounds are made. The second script's queue
    /// and the marks are cleared between rounds; the rounds are timed by spinning (no sleeps).
    /// </summary>
    [Fact]
    public void ScriptsThatReceiveAnEventInTheFinalPassAreAllMarkedChanged()
    {
        UUID a = H.RezScript(Sleeper), b = H.RezScript(Sleeper);
        Assert.True(H.PumpUntil(() => H.InterpreterFor(a) != null && H.InterpreterFor(b) != null
                                      && H.RunStateOf(a) == "Waiting" && H.RunStateOf(b) == "Waiting"), "the scripts did not start");
        H.PumpUntilIdle(TimeSpan.FromSeconds(2));
        PhloxExecutionScheduler exe = SavedStateRig.Exe(H);
        StateManager sm = SavedStateRig.States(H);
        var bState = ((InWorldz.Phlox.VM.Interpreter)H.InterpreterFor(b)).ScriptState;

        int go = 0, posted = 0, stop = 0;
        var poster = new Thread(() =>
        {
            var local = new Random(54321);
            while (Volatile.Read(ref stop) == 0)
            {
                if (Interlocked.CompareExchange(ref go, 0, 1) != 1) { Thread.SpinWait(20); continue; }
                Thread.SpinWait(local.Next(0, 120));
                H.PostTouch(b);
                Volatile.Write(ref posted, 1);
            }
        }) { IsBackground = true };
        poster.Start();

        int violations = 0, taken = 0, rounds = 0;
        var wall = DateTime.UtcNow.AddSeconds(60);
        try
        {
            while (taken < 300 && DateTime.UtcNow < wall && violations == 0)
            {
                rounds++;
                H.PostTouch(a);
                Volatile.Write(ref posted, 0);
                Volatile.Write(ref go, 1);
                exe.QueueIncomingForFinalSave();
                while (Volatile.Read(ref posted) == 0) Thread.SpinWait(10);

                int queuedForB;
                lock (bState.EventQueueLock) queuedForB = bState.EventQueue.Count;
                if (queuedForB > 0)
                {
                    taken++;
                    if (!Dirty(sm).Contains(b)) violations++;
                }
                // Reset: whatever is still posted is taken up, and the queues and marks start empty.
                exe.QueueIncomingForFinalSave();
                lock (bState.EventQueueLock) bState.EventQueue.Clear();
                var aState = ((InWorldz.Phlox.VM.Interpreter)H.InterpreterFor(a)).ScriptState;
                lock (aState.EventQueueLock) aState.EventQueue.Clear();
                ClearDirty(sm);
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            Assert.True(poster.Join(TimeSpan.FromSeconds(10)));
        }
        Assert.True(taken > 0, "no round had the second script's event taken by the pass (rounds " + rounds + ")");
        Assert.True(violations == 0, "a script got an event in the final pass and was not marked changed (rounds " + rounds + ", taken " + taken + ")");
    }
}
