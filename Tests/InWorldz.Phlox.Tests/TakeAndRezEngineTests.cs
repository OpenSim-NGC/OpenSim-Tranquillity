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
/// A script that an engine holds through saved state stays with that engine after its object is taken into inventory and
/// rezzed again. The take is the core's own (BasicInventoryAccessModule.CopyToInventory, which serializes the object with
/// each running script's state) and so is the rez (RezObject: new ids for every item, then CreateScriptInstances), so
/// the script comes back under a new item id and the state it carried is what places it (Scene.ResolveScriptEngine).
/// A fresh start would say its entry line again, so "said once" proves the state was restored, by the engine that held it.
/// YEngine is the second real engine; the "--!slua" case has a stand-in engine that claims such scripts, as the SLua engine does.
/// In "phlox-yengine" (YEngine's statics).
/// </summary>
[Collection("phlox-yengine")]
public class TakeAndRezEngineTests
{
    private const string Phlox = "InWorldz.Phlox", YEngine = "YEngine";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

    private const string PhloxLsl = "default { state_entry() { llSay(0, \"phlox-entry\"); } }";
    private const string YEngineLsl = "//YEngine:\ndefault { state_entry() { llSay(0, \"yengine-entry\"); } }";
    private const string PhloxLua = "--!slua\nll.Say(0, \"lua-entry\")\n";

    /// <summary>Claims a script whose first line is "--!slua" when nothing else placed it, as the SLua engine's ClaimsScript does.</summary>
    private sealed class Claimer : MockScriptEngine, IScriptModule, IScriptEngineSelection
    {
        public new string ScriptEngineName => "Claimer";
        public IReadOnlyList<string> HeaderAliases { get; } = Array.Empty<string>();
        public bool ClaimsScript(string scriptSource) => scriptSource.StartsWith("--!slua");

        // The core asks every engine about every script of an object; this one holds none.
        public new bool ResumeScript(UUID itemID) => false;
        public new bool SuspendScript(UUID itemID) => false;
        public new bool GetScriptState(UUID itemID) => false;
        public new bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public new System.Collections.ArrayList GetScriptErrors(UUID itemID) => new();
        public new string GetXMLState(UUID itemID) => string.Empty;
        public new bool SetXMLState(UUID itemID, string xml) => false;
    }

    private sealed class Region
    {
        public readonly SchedulerHarness H;
        public readonly BasicInventoryAccessModule Iam = new();
        public readonly UserAccount User;
        public readonly IClientAPI Client;

        public Region(SchedulerHarness h)
        {
            H = h;
            // The test scene reads no [Startup] section, so it has no default engine until one is set.
            typeof(Scene).GetField("m_defaultScriptEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(h.Scene, Phlox);
            var config = new IniConfigSource();
            config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            SceneHelpers.SetupSceneModules(h.Scene, config, Iam);
            User = UserAccountHelpers.CreateUserWithInventory(h.Scene, UUID.Random());
            Client = SceneHelpers.AddScenePresence(h.Scene, User).ControllingClient;
        }

        /// <summary>An object of the user's with the given scripts, started as the region starts them.</summary>
        public (SceneObjectGroup group, Dictionary<string, UUID> items) Build(params (string name, string source)[] scripts)
        {
            SceneObjectGroup group = SceneHelpers.AddSceneObject(H.Scene, "Example Object", User.PrincipalID);
            var items = new Dictionary<string, UUID>();
            foreach (var (name, source) in scripts)
            {
                TaskInventoryItem item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, group.RootPart, UUID.Random(), UUID.Random(), name, source);
                items[name] = item.ItemID;
                Assert.True(group.RootPart.Inventory.CreateScriptInstance(item.ItemID, 0, false, Phlox, 0));
            }
            group.ResumeScripts();
            return (group, items);
        }

        /// <summary>Takes the object into the user's inventory and removes it from the region, then rezzes it from that item.</summary>
        public SceneObjectGroup TakeAndRez(SceneObjectGroup group)
        {
            UUID folder = H.Scene.InventoryService.GetFolderForType(User.PrincipalID, FolderType.Object).ID;
            List<InventoryItemBase> taken = Iam.CopyToInventory(DeRezAction.Take, folder, new List<SceneObjectGroup> { group }, Client, false);
            InventoryItemBase item = Assert.Single(taken);
            Assert.NotNull(item);
            H.Scene.DeleteSceneObject(group, false);

            SceneObjectGroup rezzed = Iam.RezObject(Client, item.ID, UUID.Zero, Vector3.Zero, Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);
            Assert.NotNull(rezzed);
            Assert.NotEqual(group.UUID, rezzed.UUID);
            return rezzed;
        }

        public Dictionary<string, UUID> ItemsOf(SceneObjectGroup group)
            => group.RootPart.Inventory.GetInventoryItems(InventoryType.LSL).ToDictionary(i => i.Name, i => i.ItemID);
    }

    private static int Count(SchedulerHarness h, string marker) => h.Said.Count(s => s == marker);
    private static bool InPhlox(SchedulerHarness h, UUID item) => h.Engine.HasScript(item, out _);
    private static bool InYEngine(SchedulerHarness h, UUID item) => h.YEngine.HasScript(item, out _);

    private static void AssertSaidOnceOnly(SchedulerHarness h, params string[] markers)
    {
        foreach (string marker in markers)
            Assert.True(h.PumpUntil(() => Count(h, marker) >= 1, Cap), $"nobody said '{marker}': [{string.Join(" | ", h.Said)}]");
        // A real window: this proves nothing more is said (a script started fresh in any engine would say its entry again).
        h.PumpFor(TimeSpan.FromSeconds(1));
        foreach (string marker in markers)
            Assert.Equal(1, Count(h, marker));
    }

    [Fact]
    public void APhloxScriptAndAYEngineScriptKeepTheirEnginesWhenTheirObjectIsTakenAndRezzed()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var region = new Region(h);
        var (group, before) = region.Build(("lsl-phlox", PhloxLsl), ("lsl-yengine", YEngineLsl));
        AssertSaidOnceOnly(h, "phlox-entry", "yengine-entry");
        Assert.True(InPhlox(h, before["lsl-phlox"]) && !InYEngine(h, before["lsl-phlox"]));
        Assert.True(InYEngine(h, before["lsl-yengine"]) && !InPhlox(h, before["lsl-yengine"]));

        SceneObjectGroup rezzed = region.TakeAndRez(group);
        Dictionary<string, UUID> after = region.ItemsOf(rezzed);

        Assert.NotEqual(before["lsl-phlox"], after["lsl-phlox"]);       // a real take: the ids changed
        Assert.NotEqual(before["lsl-yengine"], after["lsl-yengine"]);
        Assert.True(h.PumpUntil(() => InPhlox(h, after["lsl-phlox"]) && InYEngine(h, after["lsl-yengine"]), Cap),
            $"phlox={InPhlox(h, after["lsl-phlox"])} yengine={InYEngine(h, after["lsl-yengine"])} said=[{string.Join(" | ", h.Said)}]");
        Assert.False(InYEngine(h, after["lsl-phlox"]));
        Assert.False(InPhlox(h, after["lsl-yengine"]));
        AssertSaidOnceOnly(h, "phlox-entry", "yengine-entry");          // restored, not started again
    }

    [Fact]
    public void ASluaMarkedScriptHeldByPhloxStaysInPhloxWhenTakenAndRezzedWithAnEngineThatClaimsSuchScriptsLoaded()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        var region = new Region(h);
        // Phlox holds the script: nothing else claimed it when it was first started.
        var (group, before) = region.Build(("lua-phlox", PhloxLua));
        AssertSaidOnceOnly(h, "lua-entry");
        Assert.True(InPhlox(h, before["lua-phlox"]));

        // The claiming engine is loaded from now on: without saved state, the region would give this script to it.
        var claimer = new Claimer();
        h.Scene.StackModuleInterface<IScriptModule>(claimer);
        Assert.Same(claimer, h.Scene.ResolveScriptEngine(Phlox, PhloxLua));

        SceneObjectGroup rezzed = region.TakeAndRez(group);
        Dictionary<string, UUID> after = region.ItemsOf(rezzed);

        Assert.NotEqual(before["lua-phlox"], after["lua-phlox"]);
        Assert.True(h.PumpUntil(() => InPhlox(h, after["lua-phlox"]), Cap), $"said=[{string.Join(" | ", h.Said)}]");
        AssertSaidOnceOnly(h, "lua-entry");
    }
}
