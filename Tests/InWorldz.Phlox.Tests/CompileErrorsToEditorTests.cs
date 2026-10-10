using System.Collections;
using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The script editor's Save goes Scene.CapsUpdateTaskInventoryScriptAsset ->
/// SceneObjectPartInventory.CreateScriptInstanceEr, which asks every engine's GetScriptErrors for the item just
/// rezzed and returns the list to the viewer (compiled = list empty). Phlox answered an empty list at once, so the
/// viewer said "compiled" for any script. It now waits for that item's compile, as YEngine does, and answers
/// "(line,col) Error: message" - on the caps thread, never the scheduler's.
/// </summary>
[Collection("phlox-state")]
public class CompileErrorsToEditorTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public CompileErrorsToEditorTests(ITestOutputHelper o) => _out = o;

    public void Dispose()
    {
        global::Phlox.ScriptEngine.PhloxScriptLoader.CompileDelayForTest = null;
        global::Phlox.ScriptEngine.PhloxScriptLoader.ErrorWaitTimeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>Put a script item in the prim WITHOUT rezzing it - the editor's Save rezzes it through CreateScriptInstanceEr.</summary>
    private static UUID AddScriptItem(SchedulerHarness h, string source)
        => TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "saved" + Guid.NewGuid().ToString("N").Substring(0, 6), source).ItemID;

    /// <summary>The Save path, on its own thread (the caps thread), while this thread pumps the scheduler.</summary>
    private static (ArrayList errors, long ms) Save(SchedulerHarness h, UUID item, Action whilePumping = null)
    {
        var sw = Stopwatch.StartNew();
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item, 0, false, h.Engine.Name, 1));
        while (!save.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(30)) { h.PumpOnce(); whilePumping?.Invoke(); Thread.Sleep(1); }
        Assert.True(save.IsCompleted, "the Save did not return in 30 s");
        return (save.Result, sw.ElapsedMilliseconds);
    }

    [Fact]
    public void ASyntaxErrorReachesTheEditorWithLineAndColumnAndNoOwnerAlert()
    {
        using var h = new SchedulerHarness();
        var alerts = RecordingDialogs.Create(out var rec);
        h.Scene.RegisterModuleInterface<IDialogModule>(alerts);
        var item = AddScriptItem(h, "default\n{\n    state_entry()\n    {\n        llSay(0, \"x\")\n    }\n}\n");
        var (errors, ms) = Save(h, item);
        _out.WriteLine($"{ms} ms: [{string.Join(" | ", errors.Cast<object>())}] alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.NotEmpty(errors);
        Assert.Matches(@"^\(6,4\) Error: ", (string)errors[0]!);
        Assert.Empty(rec.Alerts);   // the editor shows it; the owner is not also sent an alert
    }

    [Fact]
    public void AnSluaErrorReachesTheEditor()
    {
        using var h = new SchedulerHarness();
        var item = AddScriptItem(h, "--!slua\nlocal x = (1\nll.Say(0, tostring(x))\n");
        var (errors, _) = Save(h, item);
        _out.WriteLine($"[{string.Join(" | ", errors.Cast<object>())}]");
        Assert.NotEmpty(errors);
        Assert.Matches(@"^\(\d+,0\) Error: ", (string)errors[0]!);
    }

    [Fact]
    public void AGoodScriptReturnsNoErrorsAndRuns()
    {
        using var h = new SchedulerHarness();
        var item = AddScriptItem(h, "default { state_entry() { llSay(0, \"saved and running\"); } }");
        var (errors, _) = Save(h, item);
        Assert.Empty(errors);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !h.Said.Contains("saved and running")) h.PumpOnce();
        Assert.Contains("saved and running", h.Said);
    }

    [Fact]
    public void ACompileFailureNoEditorWaitsOnStillAlertsTheOwner()
    {
        using var h = new SchedulerHarness();
        var alerts = RecordingDialogs.Create(out var rec);
        h.Scene.RegisterModuleInterface<IDialogModule>(alerts);
        h.RezScript("default { state_entry() { llSay(0, \"x\") } }");   // a rez, not a Save
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && rec.Alerts.Count == 0) h.PumpOnce();
        Assert.Single(rec.Alerts);
        Assert.Contains("failed to compile", rec.Alerts[0]);
    }

    /// <summary>
    /// Two clocks meet here. The Save's wait is real time on the caps thread (WaitForCompileErrors, bounded by
    /// ErrorWaitTimeout), so it keeps a real wait. The timer that shows the scheduler still runs is a wake on the
    /// engine's clock (Clock), which this test stops and moves by hand, so its ticks are counted exactly. The compile
    /// under test is held until the test lets it go, never slowed by a fixed delay, so it cannot finish inside the wait.
    /// The class is already in "phlox-state", which the process-wide clock needs.
    /// </summary>
    [Fact]
    public async Task ATimeoutAnswersTheRegionsTimeoutAndDoesNotBlockTheScheduler()
    {
        bool frozen = false;
        ulong now = 0;
        InWorldz.Phlox.Util.Clock.SetSourceForTesting(() => frozen ? now : (ulong)Environment.TickCount64);
        using var held = new ManualResetEventSlim(false);
        global::Phlox.ScriptEngine.PhloxScriptLoader.CompileDelayForTest = t => { if (t.Contains("HELD-C")) held.Wait(TimeSpan.FromSeconds(60)); return 0; };
        try
        {
            // The chat pause (ChatThrottle) is a sleep on the same clock; off, so it cannot hold a tick back.
            using var h = new SchedulerHarness(cfg => cfg.Configs["InWorldz.Phlox"].Set("ChatThrottle", "false"));
            h.RezScript("default { state_entry() { llSetTimerEvent(0.1); } timer() { llSay(0, \"tick\"); } }");
            int Ticks() => h.Said.Count(s => s == "tick");
            Assert.True(h.PumpUntil(() => Ticks() >= 1, TimeSpan.FromSeconds(20)), string.Join(" | ", h.Said));

            // From here no tick falls due unless the test moves the clock.
            now = (ulong)Environment.TickCount64;
            frozen = true;
            Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
            int before = Ticks();

            // 1. While a Save waits for its compile, the scheduler runs: each 100 ms of the clock brings exactly one tick,
            //    and the Save is still waiting after all five (its compile is held). The wait then ends with the
            //    compile's own answer, not the timeout.
            var good = AddScriptItem(h, "// HELD-C\ndefault { state_entry() { llSay(0, \"held\"); } }");
            var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(good, 0, false, h.Engine.Name, 1));
            for (int i = 1; i <= 5; i++)
            {
                now += 100;
                Assert.True(h.PumpUntil(() => Ticks() == before + i, TimeSpan.FromSeconds(10)),
                    $"tick {i} did not come while the Save waited: the wait blocked the scheduler ({Ticks() - before} ticks)");
            }
            for (int i = 0; i < 20; i++) h.PumpOnce();
            Assert.Equal(before + 5, Ticks());
            Assert.False(save.IsCompleted, "the Save returned while its compile was still held");
            held.Set();
            Assert.True(h.PumpUntil(() => save.IsCompleted, TimeSpan.FromSeconds(30)), "the Save did not return");
            var answer = await save;
            _out.WriteLine($"while waiting: {Ticks() - before} ticks; answer [{string.Join(" | ", answer.Cast<object>())}]");
            Assert.Empty(answer);

            // 2. A compile that does not come in time: the wait ends at ErrorWaitTimeout with the region's timeout answer.
            //    Real time only lengthens the wait, so 900 ms is a sound floor; far below the 15 s default is the ceiling.
            held.Reset();
            global::Phlox.ScriptEngine.PhloxScriptLoader.ErrorWaitTimeout = TimeSpan.FromSeconds(1);
            var slow = AddScriptItem(h, "// HELD-C\ndefault { state_entry() { llSay(0, \"slow\"); } }");
            var (errors, ms) = Save(h, slow);
            held.Set();
            _out.WriteLine($"{ms} ms: [{string.Join(" | ", errors.Cast<object>())}]");
            Assert.Equal(new[] { "timedout waiting for errors" }, errors.Cast<string>().ToArray());
            Assert.InRange(ms, 900, 10000);
        }
        finally
        {
            held.Set();
            InWorldz.Phlox.Util.Clock.SetSourceForTesting(null);
        }
    }
}
