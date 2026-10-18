/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Data.SQLite;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Saved-state ownership keeps unchanged scripts on their engine, with the default engine's state taking priority.
/// Edited scripts have a new asset and start fresh on the default.
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
    public void AScriptWithPhloxStateStaysInPhloxWhenYEngineBecomesTheDefault()
    {
        var itemId = UUID.Random();
        var assetId = UUID.Random();
        RunInPhloxAndSave(itemId, assetId, Body);
        Assert.True(WaitForRow(itemId), "the Phlox save left no row");
        byte[] saved = RowBytes(itemId);

        using (var h2 = new SchedulerHarness(withYEngine: true))   // the operator makes YEngine the default
        {
            RegionStart(h2, Body, itemId, assetId, YEngine);
            Assert.True(h2.PumpUntil(() => h2.InterpreterFor(itemId) != null, Cap), h2.Diagnose(itemId));
            Assert.False(h2.YEngine.HasScript(itemId, out _));
            Assert.DoesNotContain("fresh", h2.Said);
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

        // A viewer save gives the item a new asset, invalidating both engines' old state.
        string edited = Body.Replace("fresh", "fresh edit");
        var editedAsset = UUID.Random();
        using var h3 = new SchedulerHarness(withYEngine: true);
        RegionStart(h3, edited, itemId, editedAsset, Phlox);
        Assert.True(h3.PumpUntil(() => h3.Said.Contains("fresh edit"), Cap), "not started fresh: " + h3.Diagnose(itemId));
        h3.PostTouch(itemId);
        Assert.True(h3.PumpUntil(() => h3.Said.Contains("count 1"), Cap), h3.Diagnose(itemId));
        Assert.DoesNotContain(h3.Said, s => s.StartsWith("two count"));
    }

    private string RunInYEngineAndSave(UUID itemId, UUID assetId)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        RegionStart(h, Body, itemId, assetId, YEngine);
        Assert.True(h.PumpUntil(() => h.Said.Contains("fresh"), Cap), "YEngine did not start it");
        Assert.True(h.YEngine.PostScriptEvent(itemId, "touch_start", new object[] { 1 }));
        Assert.True(h.PumpUntil(() => h.Said.Contains("count 1"), Cap));
        Assert.True(h.YEngine.PostScriptEvent(itemId, "touch_start", new object[] { 1 }));
        Assert.True(h.PumpUntil(() => h.Said.Contains("in two"), Cap));
        string state = h.YEngine.GetXMLState(itemId);
        Assert.False(string.IsNullOrEmpty(state));
        return state;
    }

    [Fact]
    public void YEngineStateKeepsAnUnheadedScriptOnYEngineWithPhloxDefault()
    {
        var item = UUID.Random();
        var asset = UUID.Random();
        string state = RunInYEngineAndSave(item, asset);
        using var h = new SchedulerHarness(withYEngine: true);
        Assert.True(h.YEngine.SetXMLState(item, state));
        RegionStart(h, Body, item, asset, Phlox);
        Assert.True(h.YEngine.HasScript(item, out _));
        NoPhloxInstance(h, item);
        Assert.True(h.YEngine.PostScriptEvent(item, "touch_start", new object[] { 1 }));
        Assert.True(h.PumpUntil(() => h.Said.Contains("two count 3"), Cap));
        Assert.DoesNotContain("fresh", h.Said);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void YEngineOwnershipLookupReadsBothFileLayoutsWithoutMovingState(bool legacy)
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var item = UUID.Random();
        var asset = UUID.Random();
        string directory = (string)SavedStateRig.Field(h.YEngine, "m_ScriptBasePath");
        string name = item + ".state";
        string oldPath = Path.Combine(directory, name);
        string newPath = Path.Combine(directory, name[..1], name[1..]);
        string path = legacy ? oldPath : newPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<ScriptState Asset=\"{asset}\" />");
        Assert.True(h.YEngine.HasSavedScriptState(item, asset));
        Assert.False(h.YEngine.HasSavedScriptState(item, UUID.Random()));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(legacy ? newPath : oldPath));
        File.WriteAllText(path, "unreadable state");
        Assert.True(h.YEngine.HasSavedScriptState(item, asset));
        Assert.Equal("unreadable state", File.ReadAllText(path));
    }

    [Fact]
    public void DefaultPhloxStateWinsWhenBothEnginesHaveMatchingState()
    {
        var item = UUID.Random();
        var asset = UUID.Random();
        string yState = RunInYEngineAndSave(item, asset);
        RunInPhloxAndSave(item, asset, Body);
        using var h = new SchedulerHarness(withYEngine: true);
        Assert.True(h.YEngine.SetXMLState(item, yState));
        RegionStart(h, Body, item, asset, Phlox);
        Assert.True(h.PumpUntil(() => h.InterpreterFor(item) != null, Cap), h.Diagnose(item));
        Assert.False(h.YEngine.HasScript(item, out _));
        h.PostTouch(item);
        Assert.True(h.PumpUntil(() => h.Said.Contains("two count 3"), Cap));
        Assert.DoesNotContain("fresh", h.Said);
    }

    [Fact]
    public void RecompilingAYEngineScriptMovesItToDefaultPhloxAndPersistsItsNewState()
    {
        var itemId = UUID.Random();
        var oldAsset = UUID.Random();
        string state = RunInYEngineAndSave(itemId, oldAsset);
        var newAsset = UUID.Random();
        using (var h = new SchedulerHarness(withYEngine: true))
        {
            Assert.True(h.YEngine.SetXMLState(itemId, state));
            RegionStart(h, Body, itemId, oldAsset, Phlox);
            Assert.True(h.YEngine.HasScript(itemId, out _));
            h.Prim.Inventory.RemoveScriptInstance(itemId, false);
            var item = h.Prim.Inventory.GetInventoryItem(itemId);
            var text = new OpenMetaverse.Assets.AssetScriptText { Source = Body };
            text.Encode();
            h.Scene.AssetService.Store(AssetHelpers.CreateAsset(newAsset, AssetType.LSLText, text.AssetData, item.OwnerID));
            item.AssetID = newAsset;
            h.Prim.Inventory.UpdateInventoryItem(item);
            Assert.True(h.Prim.Inventory.CreateScriptInstance(itemId, 0, false, Phlox, 1));
            h.Prim.ParentGroup.ResumeScripts();
            Assert.True(h.PumpUntil(() => h.Said.Contains("fresh"), Cap), h.Diagnose(itemId));
            Assert.False(h.YEngine.HasScript(itemId, out _));
            h.PostTouch(itemId);
            Assert.True(h.PumpUntil(() => h.Said.Contains("count 1"), Cap));
            h.SaveState(itemId);
        }

        using var restart = new SchedulerHarness(withYEngine: true);
        // A stale YEngine file must not win over the new Phlox row.
        Assert.True(restart.YEngine.SetXMLState(itemId, state));
        RegionStart(restart, Body, itemId, newAsset, Phlox);
        Assert.True(restart.PumpUntil(() => restart.InterpreterFor(itemId) != null, Cap), restart.Diagnose(itemId));
        restart.PostTouch(itemId);
        Assert.True(restart.PumpUntil(() => restart.Said.Contains("count 2"), Cap));
        Assert.DoesNotContain("fresh", restart.Said);
        Assert.False(restart.YEngine.HasScript(itemId, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CarriedYEngineStateSelectsYEngineAndSurvivesADisabledRegion(bool initiallyDisabled)
    {
        var oldItem = UUID.Random();
        var asset = UUID.Random();
        string state = RunInYEngineAndSave(oldItem, asset);
        using var h = new SchedulerHarness(withYEngine: true);
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), asset, "carried", Body);
        item.OldItemID = oldItem;
        var saved = new Dictionary<UUID, string> { [oldItem] = state };
        h.Prim.ParentGroup.GetType()
            .GetField("m_savedScriptState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(h.Prim.ParentGroup, saved);
        if (initiallyDisabled)
        {
            h.Scene.RegionInfo.RegionSettings.DisableScripts = true;
            Assert.False(h.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, 3));
            Assert.Equal(state, saved[oldItem]);
            h.Scene.RegionInfo.RegionSettings.DisableScripts = false;
        }
        Assert.True(h.Prim.Inventory.CreateScriptInstance(item, 0, false, Phlox, 3));
        Assert.Empty(saved);
        h.Prim.ParentGroup.ResumeScripts();
        Assert.True(h.YEngine.HasScript(item.ItemID, out _));
        NoPhloxInstance(h, item.ItemID);
        Assert.True(h.YEngine.PostScriptEvent(item.ItemID, "touch_start", new object[] { 1 }));
        Assert.True(h.PumpUntil(() => h.Said.Contains("two count 3"), Cap));
        Assert.DoesNotContain("fresh", h.Said);
    }
}
