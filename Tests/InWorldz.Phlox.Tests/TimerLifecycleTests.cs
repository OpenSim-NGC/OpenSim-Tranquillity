/*
 * Copyright (c) Legion Builds
 * TimerLifecycleTests.cs - the script timer, llGetTime and the Running flag across stop, start, state change and reset.
 */

using System.Globalization;
using InWorldz.Phlox.VM;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What a script's timer and its run time do across llSetTimerEvent(0), a state change, the Running flag and a reset.
/// SL wiki llSetTimerEvent: "Setting sec to 0.0 stops the timer" and "The timer persists across state changes, but gets
/// removed when the script is reset". SL wiki llGetTime: the time "is reset when the script is reset". Halcyon
/// (ExecutionScheduler ScriptSetTimer, DoStateTransitionAndFindEventHandler, EnableScript/InjectScript, AfterDisable)
/// where SL says nothing.
/// <para>
/// In "phlox-state": these tests measure real time (a timer that must not tick inside a window, a sleep that must
/// not end early), which machine load from parallel classes would read as a failure.
/// </para>
/// </summary>
[Collection("phlox-state")]
public class TimerLifecycleTests
{
    private readonly ITestOutputHelper _out;
    public TimerLifecycleTests(ITestOutputHelper o) => _out = o;

    private static int Count(SchedulerHarness h, string line) => h.Said.Count(s => s == line);

    private static bool Stopped(SchedulerHarness h, UUID id) => h.StatusOf(id).Contains("GeneralEnable=False");

    private string Said(SchedulerHarness h) => "[" + string.Join(" | ", h.Said) + "]";

    [Fact]
    public void SettingTheTimerToZeroDropsATimerEventAlreadyQueued()
    {
        using var h = new SchedulerHarness();
        // The timer fires while the script sleeps, so its event waits in the queue; no timer is armed when the
        // script then stops it.
        var id = h.RezScript(@"
default
{
    state_entry()
    {
        llSetTimerEvent(0.2);
        llSleep(0.6);
        llSetTimerEvent(0.0);
        llSay(0, ""stopped"");
    }
    timer() { llSay(0, ""tick""); }
}");
        Assert.True(h.PumpUntil(() => Count(h, "stopped") == 1), Said(h));

        h.PumpFor(TimeSpan.FromSeconds(1));   // a window for something that must NOT happen
        Assert.True(Count(h, "tick") == 0, "a stopped timer still ran its queued event: " + Said(h));
    }

    [Fact]
    public void TheTimerKeepsRunningAfterAStateChange()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry()
    {
        llSetTimerEvent(0.2);
        state two;
    }
}
state two
{
    state_entry() { llSay(0, ""in two""); }
    timer() { llSay(0, ""two tick""); llSetTimerEvent(0.0); }
}");
        Assert.True(h.PumpUntil(() => Count(h, "in two") == 1), Said(h));
        Assert.True(h.PumpUntil(() => Count(h, "two tick") == 1, TimeSpan.FromSeconds(10)),
            "the timer did not survive the state change: " + Said(h));
    }

    [Fact]
    public void AStoppedAndRestartedScriptKeepsItsTimer()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry() { llSetTimerEvent(0.2); }
    timer() { llSay(0, ""tick""); }
}");
        Assert.True(h.PumpUntil(() => Count(h, "tick") >= 1, TimeSpan.FromSeconds(10)), Said(h));

        h.Engine.SetScriptState(id, false, false);
        Assert.True(h.PumpUntil(() => Stopped(h, id)), h.StatusOf(id));
        h.ClearSaid(id);
        h.PumpFor(TimeSpan.FromSeconds(0.6));   // stopped: no tick in this window
        Assert.True(Count(h, "tick") == 0, "a stopped script's timer ticked: " + Said(h));

        h.Engine.SetScriptState(id, true, false);
        Assert.True(h.PumpUntil(() => Count(h, "tick") >= 1, TimeSpan.FromSeconds(10)),
            "the timer did not come back when the script was started again: " + Said(h) + " " + h.StatusOf(id));
    }

    [Fact]
    public void ASleepingScriptStoppedAndRestartedDoesNotWakeEarly()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry()
    {
        llSay(0, ""sleeping"");
        llSleep(2.5);
        llSay(0, ""woke"");
    }
}");
        Assert.True(h.PumpUntil(() => Count(h, "sleeping") == 1 && h.RunStateOf(id) == "Sleeping"), Said(h));

        h.Engine.SetScriptState(id, false, false);
        Assert.True(h.PumpUntil(() => Stopped(h, id)), h.StatusOf(id));
        h.Engine.SetScriptState(id, true, false);
        Assert.True(h.PumpUntil(() => !Stopped(h, id)), h.StatusOf(id));

        h.PumpFor(TimeSpan.FromSeconds(0.5));   // the sleep has well over a second left
        Assert.True(Count(h, "woke") == 0, "a restarted script cut its llSleep short: " + Said(h));
        Assert.True(h.PumpUntil(() => Count(h, "woke") == 1, TimeSpan.FromSeconds(10)),
            "the restarted script never woke: " + Said(h) + " " + h.StatusOf(id));
    }

    [Fact]
    public void LlGetTimeStartsAgainFromZeroAfterAReset()
    {
        using var h = new SchedulerHarness();
        var id = h.RezScript(@"
default
{
    state_entry() { llSay(0, ""start""); }
    touch_start(integer n) { llSay(0, ""t="" + (string)llGetTime()); }
}");
        Assert.True(h.PumpUntil(() => Count(h, "start") == 1 && h.RunStateOf(id) == "Waiting"), Said(h));
        h.PumpFor(TimeSpan.FromSeconds(1.2));

        h.Engine.ResetScript(id);
        Assert.True(h.PumpUntil(() => Count(h, "start") == 2 && h.RunStateOf(id) == "Waiting"), Said(h));
        h.PostTouch(id);
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("t=", StringComparison.Ordinal))), Said(h));

        float t = float.Parse(h.Said.First(s => s.StartsWith("t=", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture);
        _out.WriteLine("llGetTime after reset = " + t);
        Assert.True(t < 0.9f, "llGetTime was not reset with the script: " + t);
    }

    [Fact]
    public void LlGetTimeCarriesAcrossASaveAndRestore()
    {
        const string source = @"
default
{
    state_entry() { llSay(0, ""start""); }
    touch_start(integer n) { llSay(0, ""t="" + (string)llGetTime()); }
}";
        var assetId = UUID.Random();
        var itemId = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(source, assetId, itemId);
            Assert.True(h1.PumpUntil(() => Count(h1, "start") == 1 && h1.RunStateOf(itemId) == "Waiting"), Said(h1));
            h1.PumpFor(TimeSpan.FromSeconds(1.2));
            h1.SaveState(itemId);
        }

        using var h2 = new SchedulerHarness();
        h2.RezScript(source, assetId, itemId);
        Assert.True(h2.PumpUntil(() => h2.RunStateOf(itemId) == "Waiting"), h2.Diagnose(itemId));
        h2.PostTouch(itemId);
        Assert.True(h2.PumpUntil(() => h2.Said.Any(s => s.StartsWith("t=", StringComparison.Ordinal))), Said(h2));

        float t = float.Parse(h2.Said.First(s => s.StartsWith("t=", StringComparison.Ordinal))[2..], CultureInfo.InvariantCulture);
        _out.WriteLine("llGetTime after restore = " + t);
        Assert.True(t >= 1.2f, "llGetTime lost the time run before the save: " + t);
    }

    [Fact]
    public void ATimerThatFiresIntoAFullQueueIsNotLost()
    {
        using var h = new SchedulerHarness();
        // The script is busy while 64 events queue up behind it; its timer fires into that full queue.
        // Losing that one event would stop the timer for good: a timer re-arms only when its event runs.
        var id = h.RezScript(@"
default
{
    state_entry()
    {
        llSay(0, ""busy"");
        llSetTimerEvent(0.3);
        llSleep(1.2);
    }
    changed(integer c) { }
    timer() { llSay(0, ""tick""); llSetTimerEvent(0.0); }
}");
        Assert.True(h.PumpUntil(() => Count(h, "busy") == 1 && h.RunStateOf(id) == "Sleeping"), Said(h));
        var exe = ExeOf(h);
        for (int i = 0; i < 64; i++)
            exe.PostEvent(id, new PostedEvent { EventType = InWorldz.Phlox.Types.SupportedEventList.Events.CHANGED, Args = new object[] { 1 } });

        Assert.True(h.PumpUntil(() => Count(h, "tick") == 1, TimeSpan.FromSeconds(10)),
            "the timer event was dropped by the full queue: " + Said(h));
    }

    internal static global::Phlox.ScriptEngine.PhloxExecutionScheduler ExeOf(SchedulerHarness h)
        => (global::Phlox.ScriptEngine.PhloxExecutionScheduler)h.Engine.GetType()
            .GetField("m_ExeScheduler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(h.Engine)!;
}
