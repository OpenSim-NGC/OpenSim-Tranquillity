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

    [Fact]
    public void ATimeoutAnswersTheRegionsTimeoutAndDoesNotBlockTheScheduler()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { llSetTimerEvent(0.1); } timer() { llSay(0, \"tick\"); } }");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && h.Said.Count(s => s == "tick") < 3) h.PumpOnce();

        global::Phlox.ScriptEngine.PhloxScriptLoader.ErrorWaitTimeout = TimeSpan.FromSeconds(1);
        global::Phlox.ScriptEngine.PhloxScriptLoader.CompileDelayForTest = t => t.Contains("SLOW-C") ? 3000 : 0;
        var item = AddScriptItem(h, "// SLOW-C\ndefault { state_entry() { llSay(0, \"slow\"); } }");
        int before = h.Said.Count(s => s == "tick");
        var (errors, ms) = Save(h, item);
        int during = h.Said.Count(s => s == "tick") - before;
        _out.WriteLine($"{ms} ms: [{string.Join(" | ", errors.Cast<object>())}] ticks while waiting: {during}");
        Assert.Equal(new[] { "timedout waiting for errors" }, errors.Cast<string>().ToArray());
        Assert.InRange(ms, 900, 3000);
        Assert.True(during >= 5, $"the timer fired {during} times during a 1 s wait: the wait blocked the scheduler");
    }
}
