using System.Collections;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// YEngine reads a script's first line ("//YEngine:", XMREngine.OnRezScript) to decide whether the script is
/// its own; Phlox ran every script whenever it was the default engine. On a region running both, with Phlox the default,
/// a script starting "//YEngine:" ran in both engines; with YEngine the default, a script naming Phlox ran in neither.
/// Phlox now applies YEngine's rule: the engine the first line names if it is loaded, otherwise the default engine.
/// Every rez here goes through the region's own path (SceneObjectPartInventory.CreateScriptInstance, which offers it to
/// every engine with the default engine's name), never Phlox's OnRezScript alone. In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class EngineHeaderTests
{
    private readonly ITestOutputHelper _out;
    public EngineHeaderTests(ITestOutputHelper o) => _out = o;

    private const string Phlox = "InWorldz.Phlox", YEngine = "YEngine";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    private static string Says(string marker) => $"default {{ state_entry() {{ llSay(0, \"{marker}\"); }} }}";

    private static TaskInventoryItem Add(SchedulerHarness h, string source, UUID itemId = default, UUID assetId = default)
        => TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, itemId.IsZero() ? UUID.Random() : itemId,
            assetId.IsZero() ? UUID.Random() : assetId, "hdr" + Guid.NewGuid().ToString("N")[..6], source);

    /// <summary>A rez as the region does it: offered to every engine with the region's default engine, then resumed.</summary>
    private static UUID Rez(SchedulerHarness h, string source, string defaultEngine, UUID itemId = default, UUID assetId = default)
    {
        var item = Add(h, source, itemId, assetId);
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item.ItemID, 0, false, defaultEngine, 0));
        h.Prim.ParentGroup.ResumeScripts();   // YEngine holds a new script suspended until the region resumes it
        return item.ItemID;
    }

    private bool InPhlox(SchedulerHarness h, UUID item) => h.Engine.HasScript(item, out _);
    private bool InYEngine(SchedulerHarness h, UUID item) => h.YEngine != null && h.YEngine.HasScript(item, out _);
    private static int Count(SchedulerHarness h, string marker) => h.Said.Count(s => s == marker);

    /// <summary>Wait for the line, then a real window in which a second instance would have said it again.</summary>
    private void SaidExactlyOnce(SchedulerHarness h, string marker)
    {
        Assert.True(h.PumpUntil(() => Count(h, marker) >= 1, Cap), $"nobody said '{marker}': [{string.Join(" | ", h.Said)}]");
        h.PumpFor(TimeSpan.FromSeconds(1));
        _out.WriteLine($"'{marker}' said {Count(h, marker)} time(s)");
        Assert.Equal(1, Count(h, marker));
    }

    /// <summary>The Phlox side of "not Phlox's": no instance, no load in flight, nothing Phlox would answer for.</summary>
    private static void NotPhloxs(SchedulerHarness h, UUID item)
    {
        Assert.False(h.Engine.HasScript(item, out _));
        Assert.Null(h.InterpreterFor(item));
        Assert.False(h.Engine.ResumeScript(item));
        Assert.False(h.Engine.SuspendScript(item));
        Assert.False(h.Engine.GetScriptState(item));
        Assert.Equal(0f, h.Engine.GetScriptExecutionTime(new List<UUID> { item }));
        Assert.Equal(0, h.Engine.GetScriptsMemory(new List<UUID> { item }));
    }

    [Fact]
    public void NoHeaderWithPhloxTheDefaultRunsInPhloxOnly()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Rez(h, Says("plain"), Phlox);
        SaidExactlyOnce(h, "plain");
        Assert.True(InPhlox(h, item));
        Assert.False(InYEngine(h, item));
    }

    [Fact]
    public void AYEngineHeaderWithBothLoadedRunsInYEngineOnly()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Rez(h, "//YEngine:\n" + Says("yengine header"), Phlox);
        NotPhloxs(h, item);   // decided at the rez itself: Phlox never posted a load
        SaidExactlyOnce(h, "yengine header");
        Assert.True(InYEngine(h, item));
        NotPhloxs(h, item);
    }

    [Fact]
    public void AYEngineHeaderWithYEngineNotLoadedFallsToTheDefaultPhlox()
    {
        // YEngine's rule: a header naming an engine that is not loaded leaves the script to the default engine.
        using var h = new SchedulerHarness();
        var item = Rez(h, "//YEngine:\n" + Says("no yengine here"), Phlox);
        SaidExactlyOnce(h, "no yengine here");
        Assert.True(InPhlox(h, item));
    }

    [Fact]
    public void PhloxsOwnHeaderWithYEngineTheDefaultRunsInPhloxOnly()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Rez(h, "//InWorldz.Phlox:\n" + Says("phlox header"), YEngine);
        SaidExactlyOnce(h, "phlox header");
        Assert.True(InPhlox(h, item));
        Assert.False(InYEngine(h, item));
    }

    [Fact]
    public void NoHeaderWithYEngineTheDefaultRunsInYEngineOnly()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Rez(h, Says("yengine default"), YEngine);
        NotPhloxs(h, item);
        SaidExactlyOnce(h, "yengine default");
        Assert.True(InYEngine(h, item));
        NotPhloxs(h, item);
    }

    /// <summary>The region-start loader (Scene.CreateScriptInstances -> SceneObjectGroup.CreateScriptInstances, state
    /// source RegionStart) partitions a restored prim's scripts the same way.</summary>
    [Theory]
    [InlineData(Phlox)]
    [InlineData(YEngine)]
    public void ARestoredRegionPartitionsTheSameWay(string defaultEngine)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var plain = Add(h, Says("restored plain")).ItemID;
        var yhead = Add(h, "//YEngine:\n" + Says("restored yengine")).ItemID;
        var phead = Add(h, "//InWorldz.Phlox:\n" + Says("restored phlox")).ItemID;
        var other = Add(h, "// Note: an ordinary comment\n" + Says("restored comment")).ItemID;   // names no loaded engine

        Assert.Equal(4, h.Prim.ParentGroup.CreateScriptInstances(0, false, defaultEngine, 0));
        h.Prim.ParentGroup.ResumeScripts();

        foreach (var m in new[] { "restored plain", "restored yengine", "restored phlox", "restored comment" })
            Assert.True(h.PumpUntil(() => Count(h, m) >= 1, Cap), $"nobody said '{m}': [{string.Join(" | ", h.Said)}]");
        h.PumpFor(TimeSpan.FromSeconds(1));
        foreach (var m in new[] { "restored plain", "restored yengine", "restored phlox", "restored comment" })
            Assert.Equal(1, Count(h, m));

        bool phloxDefault = defaultEngine == Phlox;
        Assert.Equal(phloxDefault, InPhlox(h, plain));
        Assert.Equal(!phloxDefault, InYEngine(h, plain));
        Assert.Equal(phloxDefault, InPhlox(h, other));
        Assert.Equal(!phloxDefault, InYEngine(h, other));
        Assert.True(InYEngine(h, yhead));
        NotPhloxs(h, yhead);
        Assert.True(InPhlox(h, phead));
        Assert.False(InYEngine(h, phead));
    }

    /// <summary>
    /// The editor's Save, as Scene.CapsUpdateTaskInventoryScriptAsset does it: the old instance removed, the item given
    /// the new asset, then CreateScriptInstanceEr with the region's default engine on the caps thread, answering the
    /// editor with every engine's GetScriptErrors.
    /// </summary>
    private static ArrayList Save(SchedulerHarness h, UUID itemId, string source, string defaultEngine)
    {
        var item = h.Prim.Inventory.GetInventoryItem(itemId);
        var text = new OpenMetaverse.Assets.AssetScriptText { Source = source };
        text.Encode();
        var asset = AssetHelpers.CreateAsset(UUID.Random(), AssetType.LSLText, text.AssetData, item.OwnerID);
        h.Scene.AssetService.Store(asset);
        h.Prim.Inventory.RemoveScriptInstance(itemId, false);
        item.AssetID = asset.FullID;
        h.Prim.Inventory.UpdateInventoryItem(item);
        var save = Task.Run(() => h.Prim.Inventory.CreateScriptInstanceEr(itemId, 0, false, defaultEngine, 1));
        Assert.True(h.PumpUntil(() => save.IsCompleted, TimeSpan.FromSeconds(30)), "the Save did not return");
        h.Prim.ParentGroup.ResumeScripts();
        return save.Result;
    }

    [Fact]
    public void AnEditAddingTheHeaderMovesTheScriptToYEngineAndRemovingItBringsItBackToPhloxFresh()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        const string body = "integer n; default { state_entry() { ++n; llSay(0, \"{0} start \" + (string)n); } " +
                            "touch_start(integer t) { ++n; llSay(0, \"{0} touched \" + (string)n); } }";
        string Src(string tag) => body.Replace("{0}", tag);

        var item = Rez(h, Src("phlox1"), Phlox);
        SaidExactlyOnce(h, "phlox1 start 1");
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("phlox1 touched 2"), Cap));
        h.SaveState(item);   // a state row for the item, as a flush would leave one

        var errors = Save(h, item, "//YEngine:\n" + Src("yengine"), Phlox);
        Assert.Empty(errors);
        SaidExactlyOnce(h, "yengine start 1");
        Assert.True(InYEngine(h, item));
        NotPhloxs(h, item);

        errors = Save(h, item, Src("phlox2"), Phlox);
        Assert.Empty(errors);
        SaidExactlyOnce(h, "phlox2 start 1");   // state_entry again, from n = 0: fresh, nothing restored
        Assert.True(InPhlox(h, item));
        Assert.False(InYEngine(h, item));
    }

    [Fact]
    public void AnEditMovingAScriptToYEngineDoesNotShowPhloxsEarlierCompileErrors()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Rez(h, Says("x"), Phlox);
        SaidExactlyOnce(h, "x");
        var broken = File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(EngineHeaderTests).Assembly.Location)!, "Fixtures", "phlox22-syntaxerror.lsl"));
        var first = Save(h, item, broken, Phlox);
        Assert.Equal(new[] { "(8,4) Error: missing ';' at '}'" }, first.Cast<string>().ToArray());   // Phlox's failure

        var moved = Save(h, item, "//YEngine:\n" + Says("moved fine"), Phlox);
        _out.WriteLine($"[{string.Join(" | ", moved.Cast<object>())}]");
        Assert.Empty(moved);   // the owning engine's answer (compiled), not Phlox's earlier failure
        SaidExactlyOnce(h, "moved fine");
        NotPhloxs(h, item);
    }

    /// <summary>
    /// A Phlox state row for an item whose asset is unchanged but which another engine now owns (the operator loaded
    /// YEngine) is kept, with no Phlox instance, and restored when the script comes back to Phlox (the operator unloaded
    /// YEngine again), as YEngine keeps its own state file for a script it declines.
    /// </summary>
    [Fact]
    public void AStateRowOfAScriptAnotherEngineTookIsKeptAndRestoredWhenItComesBack()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();
        const string src = "//YEngine:\ninteger n; default { state_entry() { llSay(0, \"fresh\"); } " +
                           "touch_start(integer t) { ++n; llSay(0, \"count \" + (string)n); } }";

        using (var h1 = new SchedulerHarness())   // no YEngine: the header falls to the default, Phlox
        {
            Rez(h1, src, Phlox, itemId, assetId);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("fresh"), Cap));
            h1.PostTouch(itemId);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("count 1"), Cap));
            h1.SaveState(itemId);
        }
        using (var h2 = new SchedulerHarness(withYEngine: true))   // YEngine loaded: the script is YEngine's
        {
            Rez(h2, src, Phlox, itemId, assetId);
            SaidExactlyOnce(h2, "fresh");
            Assert.True(InYEngine(h2, itemId));
            NotPhloxs(h2, itemId);
            Assert.True(h2.PumpUntilIdle(TimeSpan.FromSeconds(10)));   // the loader has run the disown
        }
        Assert.Equal(1, StateRows(itemId));   // kept, though Phlox has no instance of it
        using (var h3 = new SchedulerHarness())   // YEngine gone again: Phlox's, resumed
        {
            Rez(h3, src, Phlox, itemId, assetId);
            Assert.True(h3.PumpUntil(() => h3.InterpreterFor(itemId) != null, Cap), h3.Diagnose(itemId));
            h3.PostTouch(itemId);
            Assert.True(h3.PumpUntil(() => h3.Said.Any(s => s.StartsWith("count ")), Cap));
            Assert.Contains("count 2", h3.Said);
            Assert.DoesNotContain("fresh", h3.Said);
        }
    }

    /// <summary>
    /// The header variants, against YEngine's parse (XMREngine.OnRezScript), all in one prim with Phlox the default and
    /// YEngine loaded. Each script runs in exactly the engine the rule picks. Core's Scene.ResolveScriptEngine (the same
    /// parse, used for the parcel check) is the cross-check.
    /// </summary>
    [Fact]
    public void TheHeaderVariantsYEngineAcceptsAndRejectsBehaveTheSameInPhlox()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var cases = new (string header, string owner)[]
        {
            ("//YEngine:\n", YEngine),
            ("//YEngine:\r\n", YEngine),             // trailing \r trimmed
            ("// YEngine:\n", YEngine),              // blanks after // trimmed
            ("//YEngine :\n", YEngine),              // blanks before the colon trimmed
            ("//   YEngine   :  lsl\n", YEngine),    // language lsl
            ("//YEngine:lsl\n", YEngine),
            ("//InWorldz.Phlox:\n", Phlox),
            ("//InWorldz.Phlox: lsl\n", Phlox),
            ("//yengine:\n", Phlox),                 // case matters: names no loaded engine -> default
            ("//YENGINE:\n", Phlox),
            ("//YEngine\n", Phlox),                  // no colon
            (" //YEngine:\n", Phlox),                // // not at the very start
            ("\n//YEngine:\n", Phlox),               // not the first line
            ("/*YEngine:*/\n", Phlox),               // not a // comment
            ("//YE:Engine:\n", Phlox),               // first colon at index 2: no name
            ("//XEngine:\n", Phlox),                 // names an engine that is not loaded -> default
            ("// http://example.com\n", Phlox),      // an ordinary comment: "http" is no loaded engine
        };

        var items = new List<(UUID item, string marker, string owner, string header)>();
        int i = 0;
        foreach (var (header, owner) in cases)
        {
            string marker = "variant " + (i++);
            var item = Add(h, header + Says(marker)).ItemID;
            Assert.Equal(owner, h.Scene.ResolveScriptEngine(Phlox, header + Says(marker))?.ScriptEngineName);
            items.Add((item, marker, owner, header));
        }
        Assert.Equal(cases.Length, h.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, 0));
        h.Prim.ParentGroup.ResumeScripts();

        foreach (var (_, marker, _, _) in items)
            Assert.True(h.PumpUntil(() => Count(h, marker) >= 1, Cap), $"nobody said '{marker}': [{string.Join(" | ", h.Said)}]");
        h.PumpFor(TimeSpan.FromSeconds(1));
        foreach (var (item, marker, owner, header) in items)
        {
            _out.WriteLine($"{header.Replace("\r", "\\r").Replace("\n", "\\n"),-28} -> phlox={InPhlox(h, item)} yengine={InYEngine(h, item)} said={Count(h, marker)}");
            Assert.Equal(1, Count(h, marker));
            Assert.Equal(owner == Phlox, InPhlox(h, item));
            Assert.Equal(owner == YEngine, InYEngine(h, item));
        }
    }

    /// <summary>
    /// YEngine is the chosen engine but declines the script (a language part other than "lsl"): by the rule it is not
    /// Phlox's either, so it runs in neither - YEngine's own behaviour, not changed here.
    /// </summary>
    [Fact]
    public void AYEngineHeaderWithAnotherLanguageRunsInNeither()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Rez(h, "//YEngine: my notes\n" + Says("declined"), Phlox);
        h.PumpFor(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Count(h, "declined"));
        Assert.False(InYEngine(h, item));
        NotPhloxs(h, item);
    }

    /// <summary>
    /// Every query the scene makes of the engines, for a script Phlox skipped: Phlox answers as for a script that is not
    /// its own, so the region shows YEngine's answer (running flag, compile errors, state, reset, suspend/resume,
    /// changed(CHANGED_OWNER) on a rez into new ownership).
    /// </summary>
    [Fact]
    public void ASkippedScriptGetsTheOwningEnginesAnswers()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Rez(h, "//YEngine:\ninteger n; default { state_entry() { ++n; llSay(0, \"q start \" + (string)n); } " +
                          "touch_start(integer t) { ++n; } " +
                          "changed(integer c) { if (c & CHANGED_OWNER) llSay(0, \"q owner changed\"); } }", Phlox);
        SaidExactlyOnce(h, "q start 1");

        NotPhloxs(h, item);
        Assert.Empty(h.Engine.GetScriptErrors(item));                                   // at once, not after 15 s
        Assert.True(TaskInventoryHelpersRunning(h, item, out bool running) && running);  // the scene finds it in YEngine
        Assert.Equal(1, h.Prim.Inventory.RunningScriptCount());
        Assert.False(h.Engine.GetObjectScriptsExecutionTimes().ContainsKey(h.Prim.LocalId));

        // Reset, as the viewer's Reset does (EventManager.TriggerScriptReset to every engine): YEngine restarts it once.
        h.Scene.EventManager.TriggerScriptReset(h.Prim.LocalId, item);
        Assert.True(h.PumpUntil(() => Count(h, "q start 1") >= 2, Cap), $"[{string.Join(" | ", h.Said)}]");
        NotPhloxs(h, item);

        // Ownership change: ResumeScripts asks every engine; only the owning one may answer true, so YEngine's script
        // gets changed(CHANGED_OWNER) whichever engine is asked first.
        h.Prim.Inventory.GetInventoryItem(item).OwnerChanged = true;
        Assert.False(h.Engine.ResumeScript(item));
        h.Prim.ParentGroup.ResumeScripts();
        SaidExactlyOnce(h, "q owner changed");
    }

    /// <summary>fe31bac769 still holds: a Phlox script still loading answers ResumeScript true, so changed(CHANGED_OWNER)
    /// is posted to it (and held for it until it starts).</summary>
    [Fact]
    public void APhloxScriptStillLoadingStillGetsChangedOwner()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = Add(h, "default { state_entry() { llSay(0, \"p start\"); } " +
                          "changed(integer c) { if (c & CHANGED_OWNER) llSay(0, \"p owner changed\"); } }").ItemID;
        h.Prim.Inventory.GetInventoryItem(item).OwnerChanged = true;
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, 0));
        Assert.True(h.Engine.ResumeScript(item));   // not loaded yet - its load is posted
        h.Prim.Inventory.GetInventoryItem(item).OwnerChanged = true;
        h.Prim.ParentGroup.ResumeScripts();
        SaidExactlyOnce(h, "p owner changed");
    }

    private static int StateRows(UUID itemId)
    {
        using var conn = new System.Data.SQLite.SQLiteConnection("Data Source=ScriptEngines/Phlox/state/script_state.db");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM script_state WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool TaskInventoryHelpersRunning(SchedulerHarness h, UUID item, out bool running)
        => SceneObjectPartInventory.TryGetScriptInstanceRunning(h.Scene, h.Prim.Inventory.GetInventoryItem(item), out running);
}
