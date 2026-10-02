using System.Data.SQLite;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// When the engine rule gives a script to another engine, Phlox keeps the script's saved state, as YEngine keeps its own
/// state file for a script it declines (XMREngine.OnRezScript returns without touching it). A script that comes back to
/// Phlox with the same asset resumes from that state through the normal restore; a script that was saved (edited) has a
/// new asset, so the row is not restored and it starts fresh (StateManager.LoadState, as YEngine's asset check in
/// XMRInstCtor.LoadScriptState).
/// Each "region start" here is a new engine pair over the shared state file, the prim's scripts started by the region's
/// own loader (SceneObjectGroup.CreateScriptInstances, state source RegionStart). In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class EngineSwitchStateTests
{
    private readonly ITestOutputHelper _out;
    public EngineSwitchStateTests(ITestOutputHelper o) => _out = o;

    private const string Phlox = "InWorldz.Phlox", YEngine = "YEngine";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    /// <summary>A global and a state of its own, so a restore shows both.</summary>
    private const string Body =
        "integer n;\n" +
        "default {\n" +
        "    state_entry() { llSay(0, \"fresh\"); }\n" +
        "    touch_start(integer t) { ++n; llSay(0, \"count \" + (string)n); if (n == 2) state two; }\n" +
        "}\n" +
        "state two {\n" +
        "    state_entry() { llSay(0, \"in two\"); }\n" +
        "    touch_start(integer t) { ++n; llSay(0, \"two count \" + (string)n); }\n" +
        "}\n";

    /// <summary>A region start: the item in the prim with its ids pinned, started by the region's loader.</summary>
    private static void RegionStart(SchedulerHarness h, string source, UUID itemId, UUID assetId, string defaultEngine)
    {
        TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, itemId, assetId, "switch", source);
        Assert.Equal(1, h.Prim.ParentGroup.CreateScriptInstances(0, false, defaultEngine, 0));
        h.Prim.ParentGroup.ResumeScripts();
    }

    private static int Rows(UUID itemId)
    {
        using var conn = new SQLiteConnection("Data Source=ScriptEngines/Phlox/state/script_state.db");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM script_state WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>The row's saved bytes, or null.</summary>
    private static byte[] RowBytes(UUID itemId)
    {
        using var conn = new SQLiteConnection("Data Source=ScriptEngines/Phlox/state/script_state.db");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state_data FROM script_state WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        using var r = cmd.ExecuteReader();
        return r.Read() ? (byte[])r[0] : null;
    }

    private static bool WaitForRow(UUID itemId)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Rows(itemId) == 0)
        {
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    private static void NoPhloxInstance(SchedulerHarness h, UUID item)
    {
        Assert.False(h.Engine.HasScript(item, out _));
        Assert.Null(h.InterpreterFor(item));
    }

    /// <summary>Run under Phlox to state two with n = 2 and save, as the region's stop leaves it.</summary>
    private void RunInPhloxAndSave(UUID itemId, UUID assetId, string source)
    {
        using var h = new SchedulerHarness();
        RegionStart(h, source, itemId, assetId, Phlox);
        Assert.True(h.PumpUntil(() => h.Said.Contains("fresh"), Cap), h.Diagnose(itemId));
        h.PostTouch(itemId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("count 1"), Cap), h.Diagnose(itemId));
        h.PostTouch(itemId);
        Assert.True(h.PumpUntil(() => h.Said.Contains("in two"), Cap), h.Diagnose(itemId));
        h.SaveState(itemId);
    }

    [Fact]
    public void AScriptMovedAwayByADefaultEngineSwitchAndBackResumesWhereItLeftOff()
    {
        var itemId = UUID.Random();
        var assetId = UUID.Random();
        RunInPhloxAndSave(itemId, assetId, Body);
        Assert.True(WaitForRow(itemId), "the Phlox save left no row");
        byte[] saved = RowBytes(itemId);

        using (var h2 = new SchedulerHarness(withYEngine: true))   // the operator makes YEngine the default
        {
            RegionStart(h2, Body, itemId, assetId, YEngine);
            Assert.True(h2.PumpUntil(() => h2.Said.Contains("fresh"), Cap), "YEngine did not start it");
            Assert.True(h2.YEngine.HasScript(itemId, out _));
            Assert.True(h2.PumpUntilIdle(TimeSpan.FromSeconds(10)));   // Phlox's loader has run the hand-off
            NoPhloxInstance(h2, itemId);
        }
        Assert.Equal(1, Rows(itemId));
        Assert.Equal(saved, RowBytes(itemId));   // kept as Phlox saved it

        using var h3 = new SchedulerHarness(withYEngine: true);   // and back to Phlox
        RegionStart(h3, Body, itemId, assetId, Phlox);
        Assert.True(h3.PumpUntil(() => h3.InterpreterFor(itemId) != null, Cap), h3.Diagnose(itemId));
        Assert.False(h3.YEngine.HasScript(itemId, out _));
        h3.PostTouch(itemId);
        Assert.True(h3.PumpUntil(() => h3.Said.Any(s => s.Contains("count ")), Cap), h3.Diagnose(itemId));
        _out.WriteLine("after coming back: " + string.Join(" | ", h3.Said));
        Assert.Contains("two count 3", h3.Said);         // n = 2 kept, still in state two
        Assert.DoesNotContain("fresh", h3.Said);
        Assert.DoesNotContain("in two", h3.Said);
    }

    [Fact]
    public void AScriptThatNeverRanInPhloxGetsNoRow()
    {
        var itemId = UUID.Random();
        using var h = new SchedulerHarness(withYEngine: true);
        RegionStart(h, Body, itemId, UUID.Random(), YEngine);
        Assert.True(h.PumpUntil(() => h.Said.Contains("fresh"), Cap), "YEngine did not start it");
        Assert.True(h.PumpUntilIdle(TimeSpan.FromSeconds(10)));
        NoPhloxInstance(h, itemId);
        Assert.Equal(0, Rows(itemId));
    }

    [Fact]
    public void AnEditedScriptComingBackToPhloxStartsFresh()
    {
        var itemId = UUID.Random();
        RunInPhloxAndSave(itemId, UUID.Random(), Body);
        Assert.True(WaitForRow(itemId), "the Phlox save left no row");

        // Edited while YEngine was the default: a save gives the item a new asset.
        string edited = Body.Replace("fresh", "fresh edit");
        var editedAsset = UUID.Random();
        using (var h2 = new SchedulerHarness(withYEngine: true))
        {
            RegionStart(h2, edited, itemId, editedAsset, YEngine);
            Assert.True(h2.PumpUntil(() => h2.Said.Contains("fresh edit"), Cap), "YEngine did not start it");
            Assert.True(h2.PumpUntilIdle(TimeSpan.FromSeconds(10)));
            NoPhloxInstance(h2, itemId);
        }

        using var h3 = new SchedulerHarness(withYEngine: true);
        RegionStart(h3, edited, itemId, editedAsset, Phlox);
        Assert.True(h3.PumpUntil(() => h3.Said.Contains("fresh edit"), Cap), "not started fresh: " + h3.Diagnose(itemId));
        h3.PostTouch(itemId);
        Assert.True(h3.PumpUntil(() => h3.Said.Contains("count 1"), Cap), h3.Diagnose(itemId));
        Assert.DoesNotContain(h3.Said, s => s.StartsWith("two count"));
    }
}
