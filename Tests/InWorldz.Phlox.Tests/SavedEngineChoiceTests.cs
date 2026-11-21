/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The real YEngine and Phlox honour an engine saved with a script (ScriptEngineChoices in the item's flags) ahead of the
/// script's first line, as an engine that reads Luau targets honours those: exactly one of them runs the script, the one
/// saved. The choice and the engine survive the core's own take and rez. In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class SavedEngineChoiceTests
{
    private const string Phlox = "InWorldz.Phlox";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    // Each first line names the engine that is NOT saved, so only the saved choice can put the script where it lands.
    private const string NamesPhlox = "//InWorldz.Phlox:\ndefault { state_entry() { llSay(0, \"chose-yengine\"); } }";
    private const string NamesYEngine = "//YEngine:\ndefault { state_entry() { llSay(0, \"chose-phlox\"); } }";

    private sealed class Region
    {
        public readonly SchedulerHarness H;
        public readonly BasicInventoryAccessModule Iam = new();
        public readonly UserAccount User;
        public readonly IClientAPI Client;

        public Region(SchedulerHarness h)
        {
            H = h;
            typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(h.Scene, Phlox);
            var config = new IniConfigSource();
            config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            SceneHelpers.SetupSceneModules(h.Scene, config, Iam);
            User = UserAccountHelpers.CreateUserWithInventory(h.Scene, UUID.Random());
            Client = SceneHelpers.AddScenePresence(h.Scene, User).ControllingClient;
        }

        public (SceneObjectGroup group, Dictionary<string, UUID> items) Build(params (string name, string source, int choice)[] scripts)
        {
            SceneObjectGroup group = SceneHelpers.AddSceneObject(H.Scene, "Example Object", User.PrincipalID);
            var items = new Dictionary<string, UUID>();
            foreach (var (name, source, choice) in scripts)
            {
                TaskInventoryItem item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, group.RootPart, UUID.Random(), UUID.Random(), name, source);
                item.Flags = ScriptEngineChoices.WithChoice(item.Flags, choice);
                items[name] = item.ItemID;
                Assert.True(group.RootPart.Inventory.CreateScriptInstance(item.ItemID, 0, false, Phlox, 0));
            }
            group.ResumeScripts();
            return (group, items);
        }

        public SceneObjectGroup TakeAndRez(SceneObjectGroup group)
        {
            UUID folder = H.Scene.InventoryService.GetFolderForType(User.PrincipalID, FolderType.Object).ID;
            List<InventoryItemBase> taken = Iam.CopyToInventory(DeRezAction.Take, folder, new List<SceneObjectGroup> { group }, Client, false);
            InventoryItemBase item = Assert.Single(taken);
            H.Scene.DeleteSceneObject(group, false);
            SceneObjectGroup rezzed = Iam.RezObject(Client, item.ID, UUID.Zero, Vector3.Zero, Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);
            Assert.NotNull(rezzed);
            return rezzed;
        }
    }

    private static int Count(SchedulerHarness h, string marker) => h.Said.Count(s => s == marker);
    private static bool InPhlox(SchedulerHarness h, UUID item) => h.Engine.HasScript(item, out _);
    private static bool InYEngine(SchedulerHarness h, UUID item) => h.YEngine.HasScript(item, out _);

    private static void AssertSaidOnceOnly(SchedulerHarness h, params string[] markers)
    {
        foreach (string marker in markers)
            Assert.True(h.PumpUntil(() => Count(h, marker) >= 1, Cap), $"nobody said '{marker}': [{string.Join(" | ", h.Said)}]");
        // A real window: proves no second engine also started the script.
        h.PumpFor(TimeSpan.FromSeconds(1));
        foreach (string marker in markers)
            Assert.Equal(1, Count(h, marker));
    }

    [Fact]
    public void EachEngineRunsTheScriptsSavedForItWhateverTheirFirstLineSays()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var region = new Region(h);

        var (_, items) = region.Build(
            ("to-yengine", NamesPhlox, ScriptEngineChoices.YEngine),
            ("to-phlox", NamesYEngine, ScriptEngineChoices.Phlox));

        AssertSaidOnceOnly(h, "chose-yengine", "chose-phlox");
        Assert.True(InYEngine(h, items["to-yengine"]));
        Assert.False(InPhlox(h, items["to-yengine"]));
        Assert.True(InPhlox(h, items["to-phlox"]));
        Assert.False(InYEngine(h, items["to-phlox"]));
    }

    [Fact]
    public void TheChoiceAndTheEngineSurviveTakeAndRez()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var region = new Region(h);
        var (group, _) = region.Build(
            ("to-yengine", NamesPhlox, ScriptEngineChoices.YEngine),
            ("to-phlox", NamesYEngine, ScriptEngineChoices.Phlox));
        AssertSaidOnceOnly(h, "chose-yengine", "chose-phlox");

        SceneObjectGroup rezzed = region.TakeAndRez(group);
        var after = rezzed.RootPart.Inventory.GetInventoryItems(InventoryType.LSL).ToDictionary(i => i.Name);

        Assert.Equal(ScriptEngineChoices.YEngine, ScriptEngineChoices.FromFlags(after["to-yengine"].Flags));
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(after["to-phlox"].Flags));
        Assert.True(h.PumpUntil(() => InYEngine(h, after["to-yengine"].ItemID) && InPhlox(h, after["to-phlox"].ItemID), Cap),
            $"said=[{string.Join(" | ", h.Said)}]");
        Assert.False(InPhlox(h, after["to-yengine"].ItemID));
        Assert.False(InYEngine(h, after["to-phlox"].ItemID));
    }
}
