using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A long-running syscall that never signals completion leaves the script in
/// <c>Status.Syscall</c> for ever: no error, no timeout, every later event piling up in its queue.
/// In world the manhole sat in <c>RunState=Syscall</c> with four queued events.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SyscallCompletionTests
{
    private readonly ITestOutputHelper _out;
    public SyscallCompletionTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void SetTouchTextLeavesTheScriptWaitingAndTheMenuTextSet()
    {
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"
default
{
    state_entry()
    {
        llSetTouchText(""Enter"");
    }

    touch_start(integer n)
    {
        llSay(0, ""Entered."");
    }
}
");
        h.PumpUntil(() => h.RunStateOf(item) == "Waiting" && h.Prim.TouchName == "Enter");

        _out.WriteLine($"RunState={h.RunStateOf(item)} TouchName='{h.Prim.TouchName}'");

        Assert.Equal("Waiting", h.RunStateOf(item));
        Assert.Equal("Enter", h.Prim.TouchName);

        // and the script is still able to take a touch afterwards
        h.ClearSaid(item);
        h.TouchViaScene();
        h.PumpUntil(() => h.Said.Any(m => m.Contains("Entered")));
        Assert.Contains(h.Said, m => m.Contains("Entered"));
    }

    [Fact]
    public void AnAsyncSyscallAlwaysCompletesRatherThanStrandingTheScript()
    {
        // osTeleportAgent is one of the 24 whose implementation never posted a return. The agent
        // does not exist here, so the body does nothing - which is exactly the case that used to
        // strand the script: the work finishes and no completion is ever signalled.
        using var h = new SchedulerHarness();
        var item = h.RezScript(@"
default
{
    state_entry()
    {
        osTeleportAgent(""00000000-0000-0000-0000-000000000001"", <128,128,25>, <0,1,0>);
        llSay(0, ""after the teleport call"");
    }
}
");
        h.PumpUntil(() => h.Said.Any(m => m.Contains("after the teleport call")) && h.RunStateOf(item) != "Syscall");

        _out.WriteLine($"RunState={h.RunStateOf(item)} said=[{string.Join(",", h.Said)}]");

        Assert.NotEqual("Syscall", h.RunStateOf(item));
        Assert.Contains(h.Said, m => m.Contains("after the teleport call"));
    }
}
