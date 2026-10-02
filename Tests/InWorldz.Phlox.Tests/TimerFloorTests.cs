using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// <c>llSetTimerEvent</c> had no floor, so a script could ask a region for any rate it liked
/// and get it.
///
/// <para>
/// One resident script asked for <c>llSetTimerEvent(0.01)</c> — a 10 ms timer. Phlox honoured
/// it, <c>phlox status</c> read back <c>timer: 10 ms</c>, and the region logged <c>Slow timeslice</c>
/// warnings of 1–1.8 s for two days across three regions until the prim was deleted. One script, one
/// prim, three regions degraded.
/// </para>
///
/// <para>
/// <b>The floor is not from SL and not from InWorldz</b> — neither clamps. The SL wiki documents only
/// that "Passing in 0.0 stops further timer events", and Halcyon assigns
/// <c>TimerInterval = (int)(sec * 1000)</c> with no minimum
/// (<c>InWorldz.Phlox.Engine/ExecutionScheduler.cs:1846-1853</c>). The floor comes from the other engine
/// in this very repo: upstream's <c>LSL_Api.llSetTimerEvent</c> clamps at <c>m_MinTimerInterval</c>
/// (<c>LSL_Api.cs:4005-4011</c>), whose shipped value in <c>OpenSimDefaults.ini</c> <c>[YEngine]</c> is
/// <b>0.1</b> — and that is what a region on the shipped defaults runs. Before this, the same call behaved differently
/// depending on which engine happened to run the script.
/// </para>
///
/// <para>
/// These go through the whole engine rather than a helper, because the thing that matters is the value
/// the <i>scheduler</i> ends up holding — that is what <c>phlox status</c> prints and what the wake loop
/// uses.
/// </para>
/// </summary>
[Collection("phlox-state")]
public class TimerFloorTests
{
    private readonly ITestOutputHelper _out;
    public TimerFloorTests(ITestOutputHelper o) => _out = o;

    /// <summary>The interval the scheduler is actually holding for this script, in milliseconds.</summary>
    private static int TimerIntervalOf(SchedulerHarness h, OpenMetaverse.UUID itemId)
    {
        var interp = h.InterpreterFor(itemId);
        Assert.NotNull(interp);
        var state = interp.GetType().GetProperty("ScriptState")!.GetValue(interp)!;
        var t = state.GetType();
        var v = t.GetProperty("TimerInterval")?.GetValue(state)
                ?? t.GetField("TimerInterval", BindingFlags.Public | BindingFlags.Instance)?.GetValue(state);
        Assert.NotNull(v);
        return (int)v!;
    }

    private static int IntervalAfter(string call, ITestOutputHelper o)
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript("default { state_entry() { " + call + " } }");
        h.PumpUntil(() => h.InterpreterFor(item) != null && h.RunStateOf(item) == "Waiting");   // state_entry ran
        var ms = TimerIntervalOf(h, item);
        o.WriteLine(call + "  ->  " + ms + " ms");
        return ms;
    }

    /// <summary>100 ms is the floor: 0.1 s, the other engine's shipped value.</summary>
    private const int FloorMs = 100;

    [Fact]
    public void ATenMillisecondRequestIsRaisedToTheFloor()
    {
        // The exact call the resident's prim made.
        Assert.Equal(FloorMs, IntervalAfter("llSetTimerEvent(0.01);", _out));
    }

    [Fact]
    public void ASubMillisecondRequestIsRaisedToTheFloorToo()
    {
        // Worth its own case: (int)(0.0001 * 1000) is 0, so before the clamp this did not mean "very
        // fast", it meant "timer off". The clamp changes what this script does, not merely how fast.
        Assert.Equal(FloorMs, IntervalAfter("llSetTimerEvent(0.0001);", _out));
    }

    [Fact]
    public void ZeroStillStopsTheTimer()
    {
        // SL: "Passing in 0.0 stops further timer events." The floor must not resurrect a stopped timer.
        Assert.Equal(0, IntervalAfter("llSetTimerEvent(0.0);", _out));
    }

    [Fact]
    public void ANegativeValueKeepsWhateverItMeantBefore()
    {
        // Negative was never specified; in Phlox it lands <= 0 and the scheduler only arms a timer when
        // the interval is > 0, so it is off. The clamp must not turn that into a running timer.
        Assert.True(IntervalAfter("llSetTimerEvent(-1.0);", _out) <= 0);
    }

    [Fact]
    public void AValueAboveTheFloorIsUntouched()
    {
        Assert.Equal(5000, IntervalAfter("llSetTimerEvent(5.0);", _out));
    }

    [Fact]
    public void TheFloorItselfIsNotRaised()
    {
        Assert.Equal(FloorMs, IntervalAfter("llSetTimerEvent(0.1);", _out));
    }
}
