using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Does a script instance actually run?
///
/// <para>
/// A build carrying the fresh-start fix — a fresh script is left <c>Waiting</c> so
/// <c>ProcessEventQueue</c> starts its <c>state_entry</c> — changed nothing in world: a fresh
/// prim with the default New Script logged <c>Starting shared script 2074003b</c> and
/// then said nothing. So the question this file exists to answer is whether the scheduler runs a
/// fresh instance at all, and it is asked through the whole engine on a test scene rather than
/// against source text.
/// </para>
///
/// <para>
/// <b>If these pass, the fault is not in the scheduler</b> and the next place to look is outside it.
/// A green run here is a real result, not a failure of the test.
/// </para>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class FreshInstanceExecutionTests
{
    private readonly ITestOutputHelper _out;
    public FreshInstanceExecutionTests(ITestOutputHelper o) => _out = o;

    private const string SaysHello = @"
default
{
    state_entry()
    {
        llSay(0, ""Hello, Avatar!"");
    }

    touch_start(integer n)
    {
        llSay(0, ""Touched."");
    }
}
";

    [Fact]
    public void AFreshCompileRunsItsStateEntry()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(SaysHello);
        h.PumpUntil(() => h.InterpreterFor(item) != null && h.SaidAnything(item));

        _out.WriteLine(h.Diagnose(item));

        Assert.NotNull(h.InterpreterFor(item));
        Assert.True(h.SaidAnything(item), $"state_entry never ran; RunState={h.RunStateOf(item)}");
    }

    [Fact]
    public void ASecondInstanceOfTheSameAssetAlsoRuns()
    {
        // The in-world symptom was a SHARED script start: "Starting shared script 2074003b" for a second
        // instance of an asset already loaded. That path skips compilation entirely.
        using var h = new SchedulerHarness();

        var first = h.RezScript(SaysHello);
        h.PumpUntil(() => h.SaidAnything(first));
        Assert.True(h.SaidAnything(first), "the first instance must run before the shared path means anything");

        var second = h.RezScript(SaysHello);
        h.PumpUntil(() => h.InterpreterFor(second) != null && h.Said.Count(s => s == "Hello, Avatar!") >= 2);

        _out.WriteLine($"second instance RunState={h.RunStateOf(second)}");
        Assert.NotNull(h.InterpreterFor(second));
        Assert.True(h.SaidAnything(second),
            $"the shared-script start did not run state_entry; RunState={h.RunStateOf(second)}");
    }

    [Fact]
    public void AFreshInstanceAlsoHandlesAPostedTouch()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(SaysHello);
        h.PumpUntil(() => h.SaidAnything(item));
        Assert.True(h.SaidAnything(item), $"state_entry never ran; RunState={h.RunStateOf(item)}");

        h.ClearSaid(item);
        h.PostTouch(item);
        h.PumpUntil(() => h.SaidAnything(item));

        Assert.True(h.SaidAnything(item),
            $"a posted touch_start was never delivered; RunState={h.RunStateOf(item)}");
    }
}
