using System;
using System.Linq;
using Nini.Config;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An exception escaping a syscall shim terminates the script ONCE. There used to be three
/// stops per OSSL denial - the permission error, then "Unable to cast Int32 to String", then "Stack
/// empty" - because the timeslice's catch marked the script Killed but never took it off the run queue,
/// so the next pass ticked the dead script again on its torn operand stack. After: one stop, one
/// DEBUG_CHANNEL line, RunState Killed, LastSyscallIndex -1, not on the run queue.
/// </summary>
[Collection("phlox-state")]
public class ThrownSyscallStaysDeadTests
{
    private readonly ITestOutputHelper _out;
    public ThrownSyscallStaysDeadTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    private void AssertDeadOnce(SchedulerHarness h, OpenMetaverse.UUID item, string expectedText)
    {
        var errors = h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message).ToList();
        _out.WriteLine("errors=[" + string.Join(" | ", errors) + "] status=" + h.StatusOf(item));
        Assert.True(errors.Count == 1, $"expected exactly one script-error chat, got {errors.Count}: {string.Join(" | ", errors)}");
        Assert.Contains(expectedText, errors[0]);
        Assert.Equal("Killed", h.RunStateOf(item));
        Assert.Equal(-1, h.LastSyscallIndexOf(item));
        Assert.False(h.IsOnRunQueue(item), "the dead script is still on the run queue");
        Assert.DoesNotContain("after", h.Said);
    }

    [Fact]
    public void AnOsslDenialStopsTheScriptExactlyOnce()
    {
        using var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("Allow_osGetSimulatorVersion", "false"));
        var item = h.RezScript(@"default { state_entry() { llSay(0, ""v="" + osGetSimulatorVersion()); llSay(0, ""after""); } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        h.PumpUntil(() => h.RunStateOf(item) == "Killed" && h.SaidOn.Any(s => s.Channel == DebugChannel));
        AssertDeadOnce(h, item, "osGetSimulatorVersion disabled in region configuration");
    }

    [Fact]
    public void ANonPermissionExceptionFromAShimStopsTheScriptExactlyOnce()
    {
        Assert.True(InWorldz.Phlox.Types.Defaults.TryGetMethod("llGetKey", out var sig));
        InWorldz.Phlox.Glue.SyscallShim.ThrowForTest = idx => idx == sig.TableIndex ? new ArgumentException("forced by the test") : null;
        try
        {
            using var h = new SchedulerHarness();
            var item = h.RezScript(@"default { state_entry() { llSay(0, ""k="" + (string)llGetKey()); llSay(0, ""after""); } }");
            h.PumpFor(TimeSpan.FromSeconds(2));
            h.PumpUntil(() => h.RunStateOf(item) == "Killed" && h.SaidOn.Any(s => s.Channel == DebugChannel));
            AssertDeadOnce(h, item, "forced by the test");
        }
        finally { InWorldz.Phlox.Glue.SyscallShim.ThrowForTest = null; }
    }
}
