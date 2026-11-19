/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// osSetScriptEngine called from a Phlox script moves another script of the same prim to the engine named, through the
/// real Phlox and YEngine: the call answers 1, the choice is kept with the item, and the script restarts in YEngine.
/// In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class SetScriptEngineFunctionTests
{
    private const string Phlox = "InWorldz.Phlox";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    [Fact]
    public void APhloxScriptMovesAnotherScriptOfItsPrimToYEngine()
    {
        using var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "High"), withYEngine: true);
        typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(h.Scene, Phlox);
        SceneObjectGroup group = SceneHelpers.AddSceneObject(h.Scene, "Example Object", UUID.Random());
        TaskInventoryItem other = TaskInventoryHelpers.AddScript(h.Scene.AssetService, group.RootPart, UUID.Random(), UUID.Random(),
            "other", "default { state_entry() { llSay(0, \"other-up\"); } }");
        Assert.True(group.RootPart.Inventory.CreateScriptInstance(other.ItemID, 0, false, Phlox, 0));
        group.ResumeScripts();
        Assert.True(h.PumpUntil(() => h.Engine.HasScript(other.ItemID, out _) && h.Said.Contains("other-up"), Cap),
            $"said=[{string.Join(" | ", h.Said)}]");

        TaskInventoryItem caller = TaskInventoryHelpers.AddScript(h.Scene.AssetService, group.RootPart, UUID.Random(), UUID.Random(),
            "caller", "default { state_entry() { llSay(0, \"set \" + (string)osSetScriptEngine(\"other\", \"YEngine\")); } }");
        Assert.True(group.RootPart.Inventory.CreateScriptInstance(caller.ItemID, 0, false, Phlox, 0));
        group.ResumeScripts();

        Assert.True(h.PumpUntil(() => h.Said.Contains("set 1"), Cap), $"said=[{string.Join(" | ", h.Said)}]");
        Assert.True(h.PumpUntil(() => h.YEngine.HasScript(other.ItemID, out _) && h.Said.Count(s => s == "other-up") >= 2, Cap),
            $"yengine={h.YEngine.HasScript(other.ItemID, out _)} said=[{string.Join(" | ", h.Said)}]");
        Assert.Equal(ScriptEngineChoices.YEngine, ScriptEngineChoices.FromFlags(group.RootPart.Inventory.GetInventoryItem(other.ItemID).Flags));
        Assert.True(h.PumpUntil(() => !h.Engine.HasScript(other.ItemID, out _), Cap));
    }
}
