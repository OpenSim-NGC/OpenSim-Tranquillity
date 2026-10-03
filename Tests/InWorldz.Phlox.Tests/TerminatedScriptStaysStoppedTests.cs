using System;
using System.Linq;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A script terminated by a runtime error stays stopped until it is reset, the way SL keeps a
/// crashed script halted: TerminateWithError turns the Running flag off exactly as llSetScriptState(FALSE) and the
/// viewer's checkbox do - GeneralEnable in the persisted state, and the item's flag - and records why. A restore
/// therefore holds it (no state_entry, not on the run queue) and a reset, or ticking Running, starts it fresh.
///
/// <para>Shares StateManager's one SQLite file with the other round-trip tests, hence the collection.</para>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class TerminatedScriptStaysStoppedTests
{
    private readonly ITestOutputHelper _out;
    public TerminatedScriptStaysStoppedTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;

    // osSetRot is VeryHigh; at the default VeryLow it is denied, and the denial is a thrown syscall - the in-world case
    private const string Crasher = @"default {
        state_entry() { llSay(0, ""up""); osSetRot(llGetKey(), <0,0,0,1>); llSay(0, ""after""); }
        touch_start(integer n) { llSay(0, ""touched""); }
    }";

    private static SchedulerHarness Scene() => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "VeryLow"));
    private static string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    [Fact]
    public void ACrashedScriptIsStoppedWithItsReasonAndDoesNotRunAgainAfterARestoreUntilReset()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        using (var h1 = Scene())
        {
            h1.RezScript(Crasher, assetId, itemId);
            h1.PumpFor(TimeSpan.FromSeconds(1));
            h1.PumpUntil(() => h1.RunStateOf(itemId) == "Killed" && h1.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied")));
            _out.WriteLine("engine 1: said=[" + string.Join(" | ", h1.Said) + "] errors=[" + Errors(h1) + "] RunState=" + h1.RunStateOf(itemId));

            Assert.Equal(1, h1.Said.Count(s => s == "up"));
            Assert.DoesNotContain("after", h1.Said);
            Assert.Single(h1.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied"));
            Assert.Equal("Killed", h1.RunStateOf(itemId));

            // the Running flag is off in both places the checkbox persists it, and the reason is on record
            Assert.False(h1.Engine.GetScriptState(itemId));
            Assert.False(h1.Prim.Inventory.GetInventoryItem(itemId).ScriptRunning);
            Assert.Contains("terminated=", h1.StatusOf(itemId)); Assert.Contains("osSetRot", h1.StatusOf(itemId));
            Assert.False(h1.IsOnRunQueue(itemId));

            h1.SaveState(itemId);   // as shutdown does
        }

        using var h2 = Scene();
        h2.RezScript(Crasher, assetId, itemId);
        h2.PumpFor(TimeSpan.FromSeconds(1));
        h2.PumpUntil(() => h2.StatusOf(itemId).Contains("osSetRot"));
        _out.WriteLine("engine 2 after restore: said=[" + string.Join(" | ", h2.Said) + "] errors=[" + Errors(h2) + "] RunState=" + h2.RunStateOf(itemId));

        // no state_entry, no re-throw, still stopped, still with its reason
        Assert.DoesNotContain("up", h2.Said);
        Assert.Empty(Errors(h2));
        Assert.False(h2.Engine.GetScriptState(itemId));
        Assert.False(h2.IsOnRunQueue(itemId));
        Assert.Contains("osSetRot", h2.StatusOf(itemId));

        // a reset starts it fresh: state_entry runs (and crashes again, as an in-world prim would)
        h2.Engine.ResetScript(itemId);
        h2.PumpUntil(() => h2.SaidOn.Any(s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied")) && h2.RunStateOf(itemId) == "Killed" && !h2.Engine.GetScriptState(itemId));
        _out.WriteLine("engine 2 after reset: said=[" + string.Join(" | ", h2.Said) + "] errors=[" + Errors(h2) + "]");
        Assert.Equal(1, h2.Said.Count(s => s == "up"));
        Assert.Single(h2.SaidOn, s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied"));
        Assert.False(h2.Engine.GetScriptState(itemId));
    }

    [Fact]
    public void TickingRunningOnACrashedScriptStartsItFresh()
    {
        using var h = Scene();
        var itemId = h.RezScript(Crasher);
        h.PumpUntil(() => h.RunStateOf(itemId) == "Killed" && !h.Engine.GetScriptState(itemId));
        Assert.Equal(1, h.Said.Count(s => s == "up"));
        Assert.False(h.Engine.GetScriptState(itemId));

        // the viewer's checkbox: TriggerStartScript, which PhloxEngine turns into an enable request
        h.Scene.EventManager.TriggerStartScript(h.Prim.LocalId, itemId);
        h.PumpUntil(() => h.SaidOn.Count(s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied")) >= 2);
        _out.WriteLine("said=[" + string.Join(" | ", h.Said) + "] errors=[" + Errors(h) + "]");

        Assert.Equal(2, h.Said.Count(s => s == "up"));   // ran state_entry again from a fresh state, not from the dead frame
        Assert.Equal(2, h.SaidOn.Count(s => s.Channel == DebugChannel && s.Message.Contains("osSetRot permission denied")));
    }

    [Fact]
    public void AnItemWhoseRunningFlagIsOffLoadsHeldAndRunsNothing()
    {
        using var h = Scene();
        var itemId = h.RezScript("default { state_entry() { llSay(0, \"up\"); } }", UUID.Random(), UUID.Random(), running: false);
        h.PumpFor(TimeSpan.FromSeconds(1));

        Assert.DoesNotContain("up", h.Said);
        Assert.False(h.Engine.GetScriptState(itemId));
        Assert.False(h.IsOnRunQueue(itemId));

        h.PumpUntil(() => h.InterpreterFor(itemId) != null);
        h.Scene.EventManager.TriggerStartScript(h.Prim.LocalId, itemId);
        h.PumpUntil(() => h.Said.Contains("up") && h.Engine.GetScriptState(itemId));
        Assert.Contains("up", h.Said);
        Assert.True(h.Engine.GetScriptState(itemId));
    }

    /// <summary>
    /// The path a real restart takes and the round trip above does not. A region stop never calls
    /// ScriptUnloaded: PhloxEngine.OnShutdown calls StateManager.Stop(), which flushes the DIRTY set only, and a script
    /// that crashed in its first slice was never marked dirty - the crash branch of RunNextScript returns before
    /// ScriptChanged. Its row still carried the previous asset, was discarded as stale at the next load, and the script
    /// started fresh with the item's Running flag at its default - the region DB does not store that flag. Item
    /// 9262c036 in world: crashed, ran state_entry again and crashed again at the next start.
    /// </summary>
    [Fact]
    public void Crashed_script_stays_stopped_across_the_live_shutdown_path()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();
        using (var h1 = Scene())
        {
            h1.RezScript(Crasher, assetId, itemId);
            h1.PumpUntil(() => h1.RunStateOf(itemId) == "Killed");
            Assert.Equal(1, h1.Said.Count(s => s == "up"));
            Assert.Equal("Killed", h1.RunStateOf(itemId));
            h1.StopRegionAsTheSimulatorDoes();   // the only save a region stop makes - no SaveState / ScriptUnloaded
        }

        using var h2 = Scene();
        h2.RezScript(Crasher, assetId, itemId);   // the item as the region DB presents it: Running flag at its default, true
        h2.PumpFor(TimeSpan.FromSeconds(1));
        h2.PumpUntil(() => h2.StatusOf(itemId).Contains("osSetRot"));
        _out.WriteLine("engine 2 after the restart path: said=[" + string.Join(" | ", h2.Said) + "] errors=[" + Errors(h2) + "] RunState=" + h2.RunStateOf(itemId));

        Assert.DoesNotContain("up", h2.Said);                                        // no state_entry
        Assert.Empty(Errors(h2));                                                    // 0 terminated at load
        Assert.False(h2.Engine.GetScriptState(itemId));                              // Running=False
        Assert.False(h2.Prim.Inventory.GetInventoryItem(itemId).ScriptRunning);      // and the item's flag agrees
        Assert.False(h2.IsOnRunQueue(itemId));
        Assert.Contains("osSetRot", h2.StatusOf(itemId));                            // still with its reason
    }
}
