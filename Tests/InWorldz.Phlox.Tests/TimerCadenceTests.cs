using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// <c>llSetTimerEvent(3.0)</c> must fire once per three seconds. In world the same
/// script fired roughly five times a second.
/// </summary>
[Collection("phlox-state")]
public class TimerCadenceTests
{
    private readonly ITestOutputHelper _out;
    public TimerCadenceTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void AThreeSecondTimerDoesNotFireFiveTimesASecond()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"
default
{
    state_entry()
    {
        llSetTimerEvent(3.0);
    }

    timer()
    {
        llSay(0, ""tick"");
    }
}
");
        h.PumpUntil(() => h.InterpreterFor(item) != null && h.RunStateOf(item) == "Waiting");   // state_entry ran: the timer is armed
        h.ClearSaid(item);

        h.PumpFor(TimeSpan.FromSeconds(3.5));
        var ticks = h.Said.Count(m => m.Contains("tick"));

        _out.WriteLine($"ticks in 3.5s = {ticks}");
        // The defect signature was about five ticks a second - seventeen or so in this window.
        // This bound was once widened to 4 after the test looked flaky under full-suite load; that
        // diagnosis was wrong. The failure was AsyncCommandManager.StartThread racing when a second
        // engine was constructed, not timing, and it is fixed at the source. Back to the tight bound.
        Assert.True(ticks <= 2, $"a 3 s timer fired {ticks} times in 3.5 s");
        Assert.True(ticks >= 1, $"a 3 s timer did not fire at all in 3.5 s");
    }
}
