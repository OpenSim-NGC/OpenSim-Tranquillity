/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using Xunit;
using Xunit.Abstractions;
using static InWorldz.Phlox.Tests.InventoryGivesRig;
using Clock = InWorldz.Phlox.Util.Clock;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The log line for events a full queue drops. SL (Category:LSL_Events): "If more that 64 events are waiting, new events
/// are discarded until free slots become available"; what is dropped does not change here, only how it is logged: one
/// line per script per run of drops, with how many of each kind, written when the script's queue has room again or the
/// script is unloaded. The engine's clock is moved by the test only when nothing can run, and the log is captured
/// through LoggerProvider; both are process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class EventQueueDropLogTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly SchedulerHarness H;
    private ulong m_now = 1_000_000;
    private readonly Capture m_log = new();
    private readonly ILoggerFactory m_previous;

    public EventQueueDropLogTests(ITestOutputHelper o)
    {
        _out = o;
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_log;
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
        private readonly List<string> m_lines = new();
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
            { lock (m_c.m_lines) m_c.m_lines.Add(formatter(state, exception)); }
        }
    }

    /// <summary>The queue-full lines logged for one script.</summary>
    private string[] FullLines(UUID item) => m_log.Lines.Where(l => l.Contains("Event queue full") && l.Contains(item.ToString())).ToArray();

    private static string Line(UUID item, string dropped)
        => "[PhloxExe]: Event queue full (64 events) for script " + item + ": dropped " + dropped;

    /// <summary>Pump; the clock moves 1 ms only when nothing could run.</summary>
    private bool RunUntil(Func<bool> done, ulong maxClockMs = 60_000)
    {
        ulong until = m_now + maxClockMs;
        var wall = DateTime.UtcNow.AddSeconds(60);
        while (!done())
        {
            if (m_now >= until || DateTime.UtcNow > wall) return false;
            if (!H.PumpOnceBusy()) m_now++;
        }
        return true;
    }

    private void Say(string msg)
        => H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

    private string Said => string.Join(" | ", H.Said);

    /// <summary>"warm" reads line 0 (the notecard is then cached); "flood" asks for lines 1..200 in one handler.</summary>
    private const string Asker = @"
        integer got;
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""A entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""warm"") llGetNotecardLine(""card"", 0);
                else if (m == ""flood"") {
                    integer i;
                    for (i = 1; i <= 200; i++) llGetNotecardLine(""card"", i);
                    llSay(0, ""A asked"");
                }
                else if (m == ""count"") llSay(0, ""A got "" + (string)got);
            }
            dataserver(key id, string d) { if (d == ""warm"") llSay(0, ""A warm""); else got++; }
        }";

    /// <summary>Handles dataserver (counts it) and touch; "nap" sleeps it 3 s.</summary>
    private const string Sleeper = @"
        integer got;
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""C entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""nap"") { llSay(0, ""C asleep""); llSleep(3.0); llSay(0, ""C woke""); }
                else if (m == ""count"") llSay(0, ""C got "" + (string)got);
            }
            dataserver(key id, string d) { got++; }
            touch_start(integer t) { llSay(0, ""C touched""); }
        }";

    private UUID Start(string source, string tag)
    {
        UUID item = H.RezScriptInto(H.Prim, source);
        Assert.True(H.PumpUntil(() => H.Said.Contains(tag + " entry")), Said);
        return item;
    }

    private void Setup()
    {
        AddNotecard(H, H.Prim, "card", "warm\n" + string.Join("\n", Enumerable.Range(1, 200).Select(i => "line " + i)));
    }

    private void Warm()
    {
        Say("warm");
        Assert.True(RunUntil(() => H.Said.Contains("A warm")), Said);
    }

    /// <summary>The count the script says, asked once its queue is empty and it waits (a full queue drops the ask).</summary>
    private int CountOf(UUID item, string tag)
    {
        Assert.True(RunUntil(() => H.Engine.GetEventQueueFreeSpacePercentage(item) == 1.0f && H.RunStateOf(item) == "Waiting"), Said);
        int before = H.Said.Count(s => s.StartsWith(tag + " got "));
        Say("count");
        Assert.True(RunUntil(() => H.Said.Count(s => s.StartsWith(tag + " got ")) > before), Said);
        return int.Parse(H.Said.Last(s => s.StartsWith(tag + " got ")).Substring(tag.Length + 5));
    }

    [Fact]
    public void OneBurstOfDropsIsOneLineWithTheKindAndTheCount()
    {
        Setup();
        UUID a = Start(Asker, "A");
        Warm();
        Say("flood");
        Assert.True(RunUntil(() => H.Said.Contains("A asked")), Said);
        int got = CountOf(a, "A");
        _out.WriteLine(string.Join("\n", FullLines(a)));
        Assert.Equal(64, got);                                         // what is dropped is unchanged
        Assert.Equal(new[] { Line(a, "136 DATASERVER events") }, FullLines(a));
    }

    [Fact]
    public void TwoKindsInOneRunAreCountedOnTheOneLine()
    {
        Setup();
        Start(Asker, "A");
        UUID c = Start(Sleeper, "C");
        Warm();
        Say("nap");
        Assert.True(RunUntil(() => H.Said.Contains("C asleep")), Said);
        Say("flood");
        Assert.True(RunUntil(() => H.Said.Contains("A asked") && H.Engine.GetEventQueueFreeSpacePercentage(c) == 0.0f), Said);
        RunUntil(() => false, 50);                                     // every answer posted has reached C
        Assert.Empty(FullLines(c));                                    // the run has not ended: nothing logged yet
        H.PostTouch(c);
        Assert.True(RunUntil(() => H.Said.Contains("C woke")), Said);
        int got = CountOf(c, "C");
        _out.WriteLine(string.Join("\n", FullLines(c)));
        Assert.Equal(0, H.Said.Count(s => s == "C touched"));          // the touch was dropped, as before
        // C's count includes the warm-up answer (every script in the prim gets it); of the 200 it kept 63, its "flood"
        // chat line holding the 64th slot.
        Assert.Equal(1 + 63, got);
        Assert.Equal(new[] { Line(c, "137 DATASERVER, 1 TOUCH_START events") }, FullLines(c));
    }

    [Fact]
    public void TwoRunsWithRoomBetweenAreTwoLines()
    {
        Setup();
        UUID a = Start(Asker, "A");
        Warm();
        Say("flood");
        Assert.True(RunUntil(() => H.Said.Count(s => s == "A asked") == 1), Said);
        Assert.Equal(64, CountOf(a, "A"));
        Say("flood");
        Assert.True(RunUntil(() => H.Said.Count(s => s == "A asked") == 2), Said);
        Assert.Equal(128, CountOf(a, "A"));
        _out.WriteLine(string.Join("\n", FullLines(a)));
        Assert.Equal(new[] { Line(a, "136 DATASERVER events"), Line(a, "136 DATASERVER events") }, FullLines(a));
    }

    [Fact]
    public void AScriptUnloadedInARunLogsItsLine()
    {
        Setup();
        Start(Asker, "A");
        UUID c = Start(Sleeper, "C");
        Warm();
        Say("nap");
        Assert.True(RunUntil(() => H.Said.Contains("C asleep")), Said);
        Say("flood");
        Assert.True(RunUntil(() => H.Said.Contains("A asked") && H.Engine.GetEventQueueFreeSpacePercentage(c) == 0.0f), Said);
        RunUntil(() => false, 50);
        Assert.Empty(FullLines(c));
        H.Prim.Inventory.RemoveInventoryItem(c);
        Assert.True(RunUntil(() => FullLines(c).Length > 0, 1_000), "no line at unload");
        _out.WriteLine(string.Join("\n", FullLines(c)));
        Assert.Equal(new[] { Line(c, "137 DATASERVER events") }, FullLines(c));
    }

    /// <summary>Asleep for 200 s from "nap"; a touch it would run when awake.</summary>
    private const string LongSleeper = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""L entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""nap"") { llSay(0, ""L asleep""); llSleep(200.0); llSay(0, ""L woke""); }
            }
            touch_start(integer t) { }
        }";

    private void Touches(UUID item, int n)
    {
        for (int i = 0; i < n; i++) H.PostTouch(item);
        RunUntil(() => false, 1);                                      // the posts are handled, the clock barely moves
    }

    /// <summary>Pump with the clock moved 100 ms at a time when idle, up to <paramref name="clock"/>.</summary>
    private void RunTo(ulong clock)
    {
        var wall = DateTime.UtcNow.AddSeconds(60);
        while (m_now < clock && DateTime.UtcNow < wall)
            if (!H.PumpOnceBusy()) m_now = Math.Min(clock, m_now + 100);
    }

    /// <summary>
    /// A run that lasts writes a line each 60 s of the engine's clock with the drops since the line before, and one at
    /// its end with the rest: a queue that stays full for three minutes still shows in the log, once a minute.
    /// </summary>
    [Fact]
    public void ARunThatLastsWritesALineEachMinuteWithTheDropsSinceTheLast()
    {
        UUID l = Start(LongSleeper, "L");
        Say("nap");
        Assert.True(RunUntil(() => H.Said.Contains("L asleep")), Said);

        ulong t0 = m_now;
        Touches(l, 70);                                                // 64 wait, 6 dropped: the run starts
        Assert.Equal(0.0f, H.Engine.GetEventQueueFreeSpacePercentage(l));
        RunTo(t0 + 30_000);
        Touches(l, 5);
        RunTo(t0 + 59_000);
        Assert.Empty(FullLines(l));                                    // not a minute yet
        RunTo(t0 + 61_000);
        Assert.Equal(new[] { Line(l, "11 TOUCH_START events") }, FullLines(l));

        RunTo(t0 + 90_000);
        Touches(l, 7);
        RunTo(t0 + 121_000);
        Assert.Equal(new[] { Line(l, "11 TOUCH_START events"), Line(l, "7 TOUCH_START events") }, FullLines(l));

        RunTo(t0 + 150_000);
        Touches(l, 3);
        RunTo(t0 + 181_000);
        Assert.Equal(3, FullLines(l).Length);
        Assert.Equal(Line(l, "3 TOUCH_START events"), FullLines(l)[2]);

        RunTo(t0 + 190_000);
        Touches(l, 2);
        Assert.Equal(0.0f, H.Engine.GetEventQueueFreeSpacePercentage(l));
        RunTo(t0 + 205_000);                                           // it wakes at 200 s and drains: the run ends
        Assert.True(RunUntil(() => H.Engine.GetEventQueueFreeSpacePercentage(l) == 1.0f), Said);
        RunUntil(() => false, 10);
        _out.WriteLine(string.Join("\n", FullLines(l)));
        Assert.Equal(new[]
        {
            Line(l, "11 TOUCH_START events"), Line(l, "7 TOUCH_START events"),
            Line(l, "3 TOUCH_START events"), Line(l, "2 TOUCH_START events"),
        }, FullLines(l));
    }
}
