/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// What moves a script between engines after the first start. The region chooses the engine when a script is
/// started (Scene.ResolveScriptEngine). A reset only tells the engine that holds the script to start it again, so it
/// neither asks the region to choose nor moves the script. Saving an edited script gives the item a new asset and
/// starts the script again, which asks the region to choose again; saved state is kept per item and asset, so the new
/// asset has none. This is how Second Life behaves too: the engine is fixed when a script is saved, and a reset does
/// not recompile.
/// </summary>
public class ScriptEngineSelectionLifecycleTests : OpenSimTestCase
{
    private const string Held = "Alpha", Claimer = "Beta";

    private sealed class Engine : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName { get; }
        public IReadOnlyList<string> HeaderAliases { get; init; } = Array.Empty<string>();
        public Func<string, bool> Claims { get; set; } = _ => false;
        public Func<UUID, UUID, bool> HoldsState { get; set; } = (_, _) => false;

        /// <summary>The items the region started on this engine, in order, with the engine name the region selected.</summary>
        public readonly List<UUID> Started = new();
        public readonly List<UUID> Resets = new();

        public Engine(string name) { ScriptEngineName = name; }

        public bool ClaimsScript(string scriptSource) => Claims(scriptSource);

        // The mock engine throws from these; saving a script reaches them.
        public ArrayList GetScriptErrors(UUID itemID) => new();
        public bool SuspendScript(UUID itemID) => true;
        public bool ResumeScript(UUID itemID) => true;
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public bool GetScriptState(UUID itemID) => false;
        public void SetScriptState(UUID itemID, bool state, bool self) { }
        public void SaveAllState() { }
        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;

        public bool HasSavedScriptState(UUID itemID, UUID assetID) => HoldsState(itemID, assetID);

        /// <summary>Does what a real engine does with a rez event: starts the script only if the selected engine is this one.</summary>
        public void Attach(Scene scene)
        {
            scene.EventManager.OnRezScript += (localID, itemID, script, startParam, postOnRez, engine, stateSource) =>
            {
                if (engine == ScriptEngineName)
                    Started.Add(itemID);
            };
            scene.EventManager.OnScriptReset += (localID, itemID) => Resets.Add(itemID);
        }
    }

    private sealed class Rig
    {
        public Scene Scene;
        public Engine Held = new(ScriptEngineSelectionLifecycleTests.Held) { HeaderAliases = new[] { "Alf" } };
        public Engine Claimer = new(ScriptEngineSelectionLifecycleTests.Claimer) { Claims = s => s.StartsWith("--!b") };
        public SceneObjectPart Part;
        public TaskInventoryItem Item;
        public TestClient Client;

        public Rig(string source)
        {
            var config = new IniConfigSource();
            config.AddConfig("Startup").Set("DefaultScriptEngine", ScriptEngineSelectionLifecycleTests.Held);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            Scene.StackModuleInterface<IScriptModule>(Held);
            Scene.StackModuleInterface<IScriptModule>(Claimer);
            Held.Attach(Scene);
            Claimer.Attach(Scene);
            Part = SceneHelpers.AddSceneObject(Scene).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "script", source);
            Client = new TestClient(SceneHelpers.GenerateAgentData(UUID.Random()), Scene);
        }

        /// <summary>The engine holds saved state for the item as it is now (its item and asset).</summary>
        public void HeldHoldsStateOf(Engine engine)
        {
            UUID item = Item.ItemID, asset = Item.AssetID;
            engine.HoldsState = (i, a) => i == item && a == asset;
        }

        public void Start() => Assert.True(Part.Inventory.CreateScriptInstance(Item, 0, false, ScriptEngineSelectionLifecycleTests.Held, 0));

        public void Reset() => Scene.ProcessScriptReset(Client, Part.UUID, Item.ItemID);

        public void EditAndSave(string source)
            => Scene.CapsUpdateTaskInventoryScriptAsset(Client, Item.ItemID, Part.UUID, true, UUID.Zero, Encoding.UTF8.GetBytes(source));

        public string SelectedNow(string source) => Scene.ResolveScriptEngine(ScriptEngineSelectionLifecycleTests.Held, source, Item)?.ScriptEngineName;
    }

    private const string ClaimedSource = "--!b\nprint(1)";

    [Fact]
    public void A_reset_keeps_a_script_the_default_engine_holds_by_saved_state_though_another_engine_would_claim_it()
    {
        var rig = new Rig(ClaimedSource);
        rig.HeldHoldsStateOf(rig.Held);

        rig.Start();
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Held.Started);
        Assert.Empty(rig.Claimer.Started);

        rig.Reset();

        Assert.Equal(new[] { rig.Item.ItemID }, rig.Held.Resets);       // the reset reached the engines ...
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Held.Started);      // ... and started nothing anew
        Assert.Empty(rig.Claimer.Started);
        Assert.Equal(Held, rig.SelectedNow(ClaimedSource));
    }

    [Fact]
    public void A_reset_keeps_a_script_the_claiming_engine_already_runs()
    {
        var rig = new Rig(ClaimedSource);

        rig.Start();
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Claimer.Started);
        rig.HeldHoldsStateOf(rig.Claimer);

        rig.Reset();

        Assert.Equal(new[] { rig.Item.ItemID }, rig.Claimer.Resets);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Claimer.Started);
        Assert.Empty(rig.Held.Started);
        Assert.Equal(Claimer, rig.SelectedNow(ClaimedSource));
    }

    [Fact]
    public void Editing_and_saving_moves_a_script_held_by_saved_state_to_the_engine_that_claims_it()
    {
        var rig = new Rig(ClaimedSource);
        rig.HeldHoldsStateOf(rig.Held);
        rig.Start();
        UUID firstAsset = rig.Item.AssetID;

        rig.EditAndSave("--!b\nprint(2)");

        Assert.NotEqual(firstAsset, rig.Item.AssetID);                  // a new asset, so no saved state for it
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Held.Started);     // not started there again
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Claimer.Started);  // started on the claiming engine
    }

    [Fact]
    public void Editing_and_saving_keeps_a_script_whose_first_line_names_the_default_engine_by_its_alias()
    {
        const string source = "//Alf:\nprint(1)";
        var rig = new Rig(source);
        rig.Claimer.Claims = _ => true;                                  // would take any script that names no engine

        rig.Start();
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Held.Started);

        rig.EditAndSave("//Alf:\nprint(2)");

        Assert.Equal(new[] { rig.Item.ItemID, rig.Item.ItemID }, rig.Held.Started);
        Assert.Empty(rig.Claimer.Started);
    }
}
