using System.Diagnostics;
using System.Reflection;
using OpenMetaverse;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The master scheduler never waits on a compile. Before, PhloxScriptLoader.CompileOnCompilerThread
/// ran the compile on a 16 MB thread but Join()ed it from the loader's DoWork, which runs on the master scheduler
/// thread, so every script on that scheduler stopped for the whole compile (9.45 s for 7,500 nested calls).
/// A test-only hook (PhloxScriptLoader.CompileDelayForTest) makes one compile take as long as a test needs.
/// </summary>
[Collection("phlox-state")]
public class CompileOffSchedulerTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public CompileOffSchedulerTests(ITestOutputHelper o) { _out = o; }
    public void Dispose() => global::Phlox.ScriptEngine.PhloxScriptLoader.CompileDelayForTest = null;

    /// <summary>Any script whose text contains <paramref name="marker"/> takes <paramref name="ms"/> to compile.</summary>
    private static void SlowCompile(string marker, int ms)
        => global::Phlox.ScriptEngine.PhloxScriptLoader.CompileDelayForTest = text => text.Contains(marker) ? ms : 0;

    private static void PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed < budget) { if (!h.PumpOnceBusy()) Thread.Sleep(1); }
    }

    [Fact]
    public void OtherScriptsKeepRunningWhileOneCompilesForThreeSeconds()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { llSetTimerEvent(0.1); } timer() { llSay(0, \"tick\"); } }");
        var toucher = h.RezScript("default { touch_start(integer n) { llSay(0, \"touched\"); } }");
        PumpUntil(h, () => h.Said.Count(s => s == "tick") >= 3, TimeSpan.FromSeconds(30));
        Assert.True(h.Said.Count(s => s == "tick") >= 3, "the timer script never started");

        SlowCompile("SLOW-B1", 3000);
        h.ClearSaid(toucher);
        var sw = Stopwatch.StartNew();
        h.RezScript("// SLOW-B1\ndefault { state_entry() { llSay(0, \"slow started\"); } }");
        bool touched = false;
        var ticksAt = new List<long>();
        while (sw.ElapsedMilliseconds < 3000)
        {
            int before = h.Said.Count(s => s == "tick");
            h.PumpOnce();
            if (h.Said.Count(s => s == "tick") > before) ticksAt.Add(sw.ElapsedMilliseconds);
            if (!touched && sw.ElapsedMilliseconds > 1000) { h.PostTouch(toucher); touched = true; }
            Thread.Sleep(1);
        }
        int ticks = h.Said.Count(s => s == "tick");
        _out.WriteLine($"in the 3 s compile window: {ticks} ticks at [{string.Join(",", ticksAt)}] ms; touched={h.Said.Contains("touched")}; slow started={h.Said.Contains("slow started")}");
        PumpUntil(h, () => h.Said.Contains("slow started"), TimeSpan.FromSeconds(30));
        h.PumpUntil(() => h.Said.Contains("touched") && h.Said.Contains("slow started"));

        Assert.True(ticks >= 20, $"the 0.1 s timer fired {ticks} times during a 3 s compile (about 30 expected): the scheduler waited on the compile");
        Assert.Contains("touched", h.Said);
        Assert.Contains("slow started", h.Said);
    }

    [Fact]
    public void ReSavingDuringASlowCompileStartsOnlyTheNewestVersion()
    {
        using var h = new SchedulerHarness();
        SlowCompile("SLOW-B2", 2000);
        var item = h.RezScript("// SLOW-B2\ndefault { state_entry() { llSay(0, \"version A\"); } }");
        h.PumpOnce();   // version A is now compiling
        h.ResaveScript(item, "default { state_entry() { llSay(0, \"version B\"); } }");
        PumpUntil(h, () => h.Said.Contains("version B"), TimeSpan.FromSeconds(30));
        PumpUntil(h, () => false, TimeSpan.FromSeconds(2.5));   // past A's compile, so a stale start would show
        _out.WriteLine($"said: [{string.Join(" | ", h.Said)}] status: {h.StatusOf(item)}");
        Assert.Contains("version B", h.Said);
        Assert.DoesNotContain("version A", h.Said);
    }

    [Fact]
    public void ScriptsInOnePrimStartInRezOrderWhenTheFirstCompilesSlowly()
    {
        using var h = new SchedulerHarness();
        SlowCompile("SLOW-B3", 1500);
        h.RezScript("// SLOW-B3\ndefault { state_entry() { llSay(0, \"first\"); } }");
        h.RezScript("default { state_entry() { llSay(0, \"second\"); } }");
        PumpUntil(h, () => h.Said.Contains("first") && h.Said.Contains("second"), TimeSpan.FromSeconds(30));
        _out.WriteLine($"said: [{string.Join(" | ", h.Said)}]");
        Assert.Equal(new[] { "first", "second" }, h.Said.Where(s => s == "first" || s == "second").ToArray());
    }

    [Fact]
    public void SetScriptStateOnAnItemStillCompilingIsAppliedWhenItStarts()
    {
        using var h = new SchedulerHarness();
        SlowCompile("SLOW-B4", 1500);
        var slow = h.RezScript("// SLOW-B4\ndefault { state_entry() { llSay(0, \"slow ran\"); } }");
        h.PumpOnce();   // compiling
        h.Engine.SetScriptState(slow, false, false);   // what llSetScriptState(name, FALSE) calls
        PumpUntil(h, () => h.InterpreterFor(slow) != null, TimeSpan.FromSeconds(30));
        PumpUntil(h, () => false, TimeSpan.FromSeconds(0.5));
        _out.WriteLine($"status: {h.StatusOf(slow)} said: [{string.Join(" | ", h.Said)}]");
        Assert.NotNull(h.InterpreterFor(slow));
        Assert.DoesNotContain("slow ran", h.Said);
        Assert.False(h.Engine.GetScriptState(slow));
    }

    [Fact]
    public void ResetOnAnItemStillCompilingStartsItFresh()
    {
        using var h = new SchedulerHarness();
        SlowCompile("SLOW-B5", 1500);
        var slow = h.RezScript("// SLOW-B5\ndefault { state_entry() { llSay(0, \"entry\"); } }");
        h.PumpOnce();   // compiling
        h.Engine.ResetScript(slow);   // llResetOtherScript / the viewer's Reset
        PumpUntil(h, () => h.Said.Contains("entry"), TimeSpan.FromSeconds(30));
        PumpUntil(h, () => false, TimeSpan.FromSeconds(0.5));
        _out.WriteLine($"said: [{string.Join(" | ", h.Said)}]");
        Assert.Equal(1, h.Said.Count(s => s == "entry"));   // started once, fresh; not lost, not doubled
    }

    [Fact]
    public void ShutdownWithACompileInFlightIsClean()
    {
        var h = new SchedulerHarness();
        SlowCompile("SLOW-B6", 1500);
        var slow = h.RezScript("// SLOW-B6\ndefault { state_entry() { llSay(0, \"slow started\"); } }");
        h.PumpOnce();   // compiling
        var loader = h.Loader;
        var sw = Stopwatch.StartNew();
        h.Engine.RemoveRegion(h.Scene);   // region shutdown
        _out.WriteLine($"RemoveRegion returned in {sw.ElapsedMilliseconds} ms");
        Thread.Sleep(2500);               // past the compile
        for (int i = 0; i < 20; i++) { try { h.PumpOnce(); } catch (Exception e) { Assert.Fail("pump after shutdown threw: " + e); } }
        Assert.DoesNotContain("slow started", h.Said);
        Assert.Null(h.InterpreterFor(slow));
        Assert.False((bool)loader.GetType().GetProperty("CompileThreadAlive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loader)!,
            "the compile thread is still running after shutdown");
        Assert.True(sw.ElapsedMilliseconds < 1000 + 2500 + 2000, "shutdown waited on the compile");
        try { h.Dispose(); } catch { }
    }
}
