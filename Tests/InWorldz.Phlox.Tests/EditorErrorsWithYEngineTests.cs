using System.Collections;
using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Found in world. Saving phlox22-syntaxerror.lsl and phlox21-deepnest.lsl
/// showed NO error in the editor, and the deep-nest error came as the owner pop-up. A region that runs YEngine AND
/// Phlox adds YEngine to the scene first, so SceneObjectPartInventory.GetScriptErrors asks it first, and
/// YEngine's GetScriptErrors waited - with no timeout and nothing to wake it - for an item it had declined in
/// OnRezScript: the Save never returned and Phlox was never asked (so no editor claimed the errors, and Phlox sent
/// the pop-up). CompileErrorsToEditorTests registered Phlox alone. Also here: a compile that fails before the editor's
/// GetScriptErrors reaches Phlox must reach the editor ONCE - no pop-up as well.
/// </summary>
[Collection("phlox-yengine")]
public class EditorErrorsWithYEngineTests
{
    private readonly ITestOutputHelper _out;
    public EditorErrorsWithYEngineTests(ITestOutputHelper o) => _out = o;

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(EditorErrorsWithYEngineTests).Assembly.Location)!, "Fixtures", name));

    /// <summary>The script editor's Save, on its own thread (the caps thread), while this thread pumps the scheduler.</summary>
    private static (ArrayList errors, long ms) Save(SchedulerHarness h, string source)
    {
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "saved" + Guid.NewGuid().ToString("N").Substring(0, 6), source).ItemID;
        var sw = Stopwatch.StartNew();
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item, 0, false, h.Engine.Name, 1));
        while (!save.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(20)) { if (!h.PumpOnceBusy()) Thread.Sleep(1); }
        Assert.True(save.IsCompleted, $"the Save did not return in 20 s ({h.Scene.RequestModuleInterfaces<IScriptModule>().Length} script engines on the scene)");
        return (save.Result, sw.ElapsedMilliseconds);
    }

    private static void PumpFor(SchedulerHarness h, TimeSpan t)
    {
        var until = DateTime.UtcNow + t;
        while (DateTime.UtcNow < until) { if (!h.PumpOnceBusy()) Thread.Sleep(1); }
    }

    [Fact]
    public void WithYEngineOnTheSceneASyntaxErrorReachesTheEditorAndNoPopUp()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        Assert.Equal(2, h.Scene.RequestModuleInterfaces<IScriptModule>().Length);   // YEngine first, as on a region running both
        Assert.Same(h.YEngine, h.Scene.RequestModuleInterfaces<IScriptModule>()[0]);
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));

        var (errors, ms) = Save(h, Fixture("phlox22-syntaxerror.lsl"));
        PumpFor(h, TimeSpan.FromSeconds(3));   // past the owner-alert grace
        _out.WriteLine($"{ms} ms: [{string.Join(" | ", errors.Cast<object>())}] alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Equal(new[] { "(8,4) Error: missing ';' at '}'" }, errors.Cast<string>().ToArray());
        Assert.Empty(rec.Alerts);
    }

    [Fact]
    public void WithYEngineOnTheSceneTheDeepNestErrorReachesTheEditorAndNoPopUp()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));

        var (errors, ms) = Save(h, Fixture("phlox21-deepnest.lsl"));
        PumpFor(h, TimeSpan.FromSeconds(3));
        _out.WriteLine($"{ms} ms: [{string.Join(" | ", errors.Cast<object>())}] alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Single(errors);
        Assert.Matches(@"^\(\d+,\d+\) Error: expression nested too deeply \(limit 1000\)$", (string)errors[0]!);
        Assert.Empty(rec.Alerts);
    }

    [Fact]
    public void WithYEngineOnTheSceneAGoodScriptSavesAsCompiledAndRuns()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var (errors, _) = Save(h, "default { state_entry() { llSay(0, \"saved beside yengine\"); } }");
        Assert.Empty(errors);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !h.Said.Contains("saved beside yengine")) h.PumpOnce();
        Assert.Contains("saved beside yengine", h.Said);
    }

    [Fact]
    public async Task YEngineStillReportsItsOwnScriptsErrors()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yscript",
            "default { state_entry() { llSay(0, \"x\") } }").ItemID;
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(item, 0, false, h.YEngine.ScriptEngineName, 1));
        Assert.True(await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(60))) == save, "a YEngine save did not return");
        var errors = await save;
        _out.WriteLine($"[{string.Join(" | ", errors.Cast<object>())}]");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task ACompileThatFailsBeforeTheEditorAsksReachesTheEditorOnceWithNoPopUp()
    {
        using var h = new SchedulerHarness();
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));
        // The race: OnRezScript posts the load, the compile fails and is published, and only THEN does the
        // editor's GetScriptErrors reach Phlox (in world: 17 ms from rez to failure).
        var item = h.RezScript(Fixture("phlox22-syntaxerror.lsl"));
        PumpFor(h, TimeSpan.FromMilliseconds(500));
        var ask = Task.Run(() => h.Engine.GetScriptErrors(item));   // the caps thread, not the scheduler's
        while (!ask.IsCompleted) { if (!h.PumpOnceBusy()) Thread.Sleep(1); }
        var errors = await ask;   // already complete, so this continues on the pumping thread
        PumpFor(h, TimeSpan.FromSeconds(3));   // past the owner-alert grace
        _out.WriteLine($"[{string.Join(" | ", errors.Cast<object>())}] alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Equal(new[] { "(8,4) Error: missing ';' at '}'" }, errors.Cast<string>().ToArray());   // the outcome is not lost
        Assert.Empty(rec.Alerts);                                                                         // and not reported twice
    }

    [Fact]
    public void AFailureNothingCollectsStillAlertsTheOwnerOnce()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        h.Scene.RegisterModuleInterface<IDialogModule>(RecordingDialogs.Create(out var rec));
        h.RezScript(Fixture("phlox22-syntaxerror.lsl"));   // a rez, not a Save: no editor will ask
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && rec.Alerts.Count == 0) h.PumpOnce();
        PumpFor(h, TimeSpan.FromSeconds(1));
        _out.WriteLine($"alerts=[{string.Join(" | ", rec.Alerts)}]");
        Assert.Single(rec.Alerts);
        Assert.Contains("failed to compile", rec.Alerts[0]);
    }
}
