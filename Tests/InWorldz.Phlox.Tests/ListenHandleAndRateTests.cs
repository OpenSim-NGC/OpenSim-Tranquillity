/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Listen handles are each script's own, numbered from 1 with the lowest free one first, as the core WorldCommModule
/// and Halcyon number them. The listen-event rate cap is an operator setting, [InWorldz.Phlox] MaxListenEventsPerSecond:
/// 20 by default, as before, and 0 for none, as Halcyon and YEngine have none (SL documents no listen rate).
/// </summary>
// Runs in parallel: each test has its own harness.
public class ListenHandleAndRateTests
{
    private readonly ITestOutputHelper _out;
    public ListenHandleAndRateTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void EachScriptNumbersItsOwnListensLowestFreeFirst()
    {
        const string first = "default { state_entry() { llSay(0, \"a=\" + (string)llListen(7, \"\", \"\", \"\")); } }";
        const string second =
            "default { state_entry() { integer x = llListen(7, \"\", \"\", \"one\"); integer y = llListen(7, \"\", \"\", \"two\"); " +
            "llListenRemove(x); llSay(0, \"b=\" + (string)x + \",\" + (string)y + \",\" + (string)llListen(7, \"\", \"\", \"three\")); } }";
        using var h = new SchedulerHarness();
        h.RezScript(first);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("a="))), SavedStateRig.SaidText(h));
        h.RezScript(second);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("b="))), SavedStateRig.SaidText(h));
        Assert.Contains("a=1", h.Said);
        Assert.Contains("b=1,2,1", h.Said);
    }

    private const string CountSrc =
        "integer n; default { listen(integer c, string nm, key k, string m) { n++; } touch_start(integer t) { llSay(0, \"n=\" + (string)n); } " +
        "state_entry() { llListen(9, \"\", \"\", \"\"); llSay(0, \"up\"); } }";

    /// <summary>Burst 30 lines within one wall-clock second; returns how many the script counted.</summary>
    private int Burst(SchedulerHarness h, UUID item)
    {
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < 30; i++) h.Engine.ListenManager.DeliverChat(9, "Test User", UUID.Random(), "m" + i);
        _out.WriteLine("burst inside one second: " + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() == second));
        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h.ClearSaid(item);
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("n="))), SavedStateRig.SaidText(h));
        return int.Parse(h.Said.First(s => s.StartsWith("n=")).Substring(2));
    }

    [Fact]
    public void WithTheCapOffEveryListenEventArrives()
    {
        using var h = new SchedulerHarness(c => c.Configs["InWorldz.Phlox"].Set("MaxListenEventsPerSecond", "0"));
        var item = h.RezScript(CountSrc);
        Assert.True(h.PumpUntil(() => h.Said.Contains("up")), SavedStateRig.SaidText(h));
        int n = Burst(h, item);
        Assert.True(n >= 30, $"the script heard {n} of 30 lines with the cap off");
    }

    [Fact]
    public void TheDefaultCapStaysAtTwentyAndLogsOncePerSecond()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(CountSrc);
        Assert.True(h.PumpUntil(() => h.Said.Contains("up")), SavedStateRig.SaidText(h));
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < 30; i++) h.Engine.ListenManager.DeliverChat(9, "Test User", UUID.Random(), "m" + i);
        bool oneSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds() == second;
        long lines = (long)SavedStateRig.Member(h.Engine.ListenManager, "RateLimitLogLines");
        long dropped = (long)SavedStateRig.Member(h.Engine.ListenManager, "DroppedListenEvents");
        _out.WriteLine($"one second: {oneSecond}, dropped {dropped}, log lines {lines}");
        if (oneSecond)
        {
            Assert.Equal(10, dropped);
            Assert.Equal(1, lines);
        }
        else
            Assert.True(lines <= 2 && dropped <= 10);
    }
}
