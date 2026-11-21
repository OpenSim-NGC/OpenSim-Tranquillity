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
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A script engine chosen for a script ("engine:&lt;Name&gt;" saved with the script) is kept in the item's flags,
/// picks the engine at the same step as a Luau target, is kept by every save from a viewer that cannot name engines,
/// and travels with the item wherever its flags go.
/// </summary>
public class ScriptEngineChoiceSelectionTests : OpenSimTestCase
{
    private const string YEngine = "YEngine", Phlox = "InWorldz.Phlox";

    /// <summary>A stand-in engine with a real engine's name, so the fixed table finds it.</summary>
    private sealed class Engine : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName { get; }
        public IReadOnlyList<string> HeaderAliases { get; init; } = Array.Empty<string>();
        public Func<UUID, UUID, bool> HoldsState { get; set; } = (_, _) => false;
        public readonly List<UUID> Started = new();

        public Engine(string name) { ScriptEngineName = name; }

        public bool ClaimsScript(string scriptSource) => false;
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

        public void Attach(Scene scene)
        {
            scene.EventManager.OnRezScript += (localID, itemID, script, startParam, postOnRez, engine, stateSource) =>
            {
                if (engine == ScriptEngineName)
                    Started.Add(itemID);
            };
        }
    }

    private sealed class Rig
    {
        public Scene Scene;
        public Engine Y = new(YEngine);
        public Engine P = new(Phlox) { HeaderAliases = new[] { "Phlox", "Ph" } };
        public SceneObjectPart Part;
        public TaskInventoryItem Item;
        public TestClient Client;
        public BasicInventoryAccessModule Iam;

        public Rig(string source = "plain", string defaultEngine = YEngine, bool loadPhlox = true, string choiceSetting = null, bool inventoryAccess = false)
        {
            var config = new IniConfigSource();
            IConfig startup = config.AddConfig("Startup");
            startup.Set("DefaultScriptEngine", defaultEngine);
            if (choiceSetting is not null)
                startup.Set("ScriptEngineChoice", choiceSetting);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            if (inventoryAccess)
            {
                Iam = new BasicInventoryAccessModule();
                config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
                SceneHelpers.SetupSceneModules(Scene, config, Iam);
            }
            Scene.StackModuleInterface<IScriptModule>(Y);
            Y.Attach(Scene);
            if (loadPhlox)
            {
                Scene.StackModuleInterface<IScriptModule>(P);
                P.Attach(Scene);
            }
            Part = SceneHelpers.AddSceneObject(Scene).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "script", source);
            Client = new TestClient(SceneHelpers.GenerateAgentData(UUID.Random()), Scene);
        }

        public void Choose(int code) => Item.Flags = ScriptEngineChoices.WithChoice(Item.Flags, code);

        public string Selected(string source) => Scene.ResolveScriptEngine(Scene.DefaultScriptEngine, source, Item)?.ScriptEngineName;

        public ArrayList Save(string source, string target)
            => Scene.CapsUpdateTaskInventoryScriptAsset(Client, Item.ItemID, Part.UUID, true, UUID.Zero, Encoding.UTF8.GetBytes(source), target);

        public uint Flags => Part.Inventory.GetInventoryItem(Item.ItemID).Flags;
        public int Choice => ScriptEngineChoices.FromFlags(Flags);
    }

    [Theory]
    // a saved engine wins over the first line and the default, as a Luau target does
    [InlineData(ScriptEngineChoices.YEngine, "x", Phlox, YEngine)]
    [InlineData(ScriptEngineChoices.YEngine, "//InWorldz.Phlox:\nx", Phlox, YEngine)]
    [InlineData(ScriptEngineChoices.YEngine, "//Phlox:\nx", Phlox, YEngine)]
    [InlineData(ScriptEngineChoices.Phlox, "x", YEngine, Phlox)]
    [InlineData(ScriptEngineChoices.Phlox, "//YEngine:\nx", YEngine, Phlox)]
    // no saved engine: the first line and the default decide, exactly as before
    [InlineData(ScriptEngineChoices.None, "x", YEngine, YEngine)]
    [InlineData(ScriptEngineChoices.None, "//Phlox:\nx", YEngine, Phlox)]
    [InlineData(ScriptEngineChoices.None, "//YEngine:\nx", Phlox, YEngine)]
    public void ASavedEnginePicksTheEngineAheadOfTheFirstLine(int choice, string source, string defaultEngine, string expected)
    {
        var rig = new Rig(source, defaultEngine);
        rig.Choose(choice);

        Assert.Equal(expected, rig.Selected(source));
    }

    [Theory]
    [InlineData("x", YEngine)]
    [InlineData("//YEngine:\nx", YEngine)]
    public void ASavedEngineThatIsNotLoadedFallsThroughToTheFirstLineAndTheDefault(string source, string expected)
    {
        var rig = new Rig(source, YEngine, loadPhlox: false);
        rig.Choose(ScriptEngineChoices.Phlox);

        Assert.Equal(expected, rig.Selected(source));
        // the choice is kept for a region that has the engine
        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
    }

    [Fact]
    public void AnEngineChoicePlacesTheScriptEvenWhenAnotherEngineHoldsSavedState()
    {
        // A choice of engine is changed without changing the script's text, so state another engine saved for the same
        // item and text is from before the choice and does not hold the script there.
        var rig = new Rig("x", YEngine);
        rig.Choose(ScriptEngineChoices.Phlox);
        UUID item = rig.Item.ItemID, asset = rig.Item.AssetID;
        rig.Y.HoldsState = (i, a) => i == item && a == asset;

        Assert.Equal(Phlox, rig.Selected("x"));
    }

    [Fact]
    public void ALuaTargetStillComesFirst()
    {
        // Lua targets and engine choices are never both set by a save; if flags hold both, the Lua target's engine is asked first.
        var rig = new Rig("x", YEngine);
        rig.Choose(ScriptEngineChoices.Phlox);
        rig.Item.Flags = ScriptCompileTargets.WithTarget(rig.Item.Flags, ScriptCompileTarget.Luau);

        // no engine here accepts Luau, so the engine choice decides
        Assert.Equal(Phlox, rig.Selected("x"));
    }

    [Theory]
    [InlineData("engine:Phlox", ScriptEngineChoices.Phlox, Phlox)]
    [InlineData("engine:InWorldz.Phlox", ScriptEngineChoices.Phlox, Phlox)]
    [InlineData("engine:Ph", ScriptEngineChoices.Phlox, Phlox)]
    [InlineData("engine:YEngine", ScriptEngineChoices.YEngine, YEngine)]
    public void SavingWithAnEngineTargetKeepsItAndRestartsTheScriptInThatEngine(string target, int expected, string engine)
    {
        var rig = new Rig("//Nobody:\nx", defaultEngine: expected == ScriptEngineChoices.Phlox ? YEngine : Phlox);

        rig.Save("//Nobody:\ny", target);

        Assert.Equal(expected, rig.Choice);
        Assert.Equal(new[] { rig.Item.ItemID }, (engine == Phlox ? rig.P : rig.Y).Started);
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("lsl2")]
    [InlineData(null)]
    public void ASaveFromAViewerThatCannotNameEnginesKeepsTheEngine(string target)
    {
        var rig = new Rig("x", YEngine);
        rig.Save("y", "engine:Phlox");
        rig.P.Started.Clear();

        rig.Save("z", target);

        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.P.Started);
        Assert.Empty(rig.Y.Started);
    }

    [Theory]
    [InlineData("luau", ScriptCompileTarget.Luau)]
    [InlineData("lsl-luau", ScriptCompileTarget.LslOnLuau)]
    public void ASaveWithALuaTargetReplacesTheEngine(string target, ScriptCompileTarget expected)
    {
        var rig = new Rig("x", YEngine);
        rig.Save("y", "engine:Phlox");

        rig.Save("z", target);

        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
        Assert.Equal(expected, ScriptCompileTargets.FromFlags(rig.Flags));
    }

    [Fact]
    public void AnEngineTargetIsIgnoredWhenTheRegionDoesNotSaveChoices()
    {
        var rig = new Rig("x", YEngine, choiceSetting: "false");
        Assert.False(rig.Scene.ScriptEngineChoiceEnabled);

        rig.Save("y", "engine:Phlox");

        Assert.Equal(ScriptEngineChoices.None, rig.Choice);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Y.Started);
    }

    [Theory]
    [InlineData("engine:")]
    [InlineData("engine:YEngine")]
    public void ARegionThatKeepsNoChoicesLeavesAnEarlierChoiceAsItWas(string target)
    {
        var rig = new Rig("x", YEngine, choiceSetting: "false");
        rig.Choose(ScriptEngineChoices.Phlox);

        rig.Save("y", target);

        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
    }

    [Fact]
    public void EngineChoiceIsOnByDefault()
    {
        var rig = new Rig();
        Assert.True(rig.Scene.ScriptEngineChoiceEnabled);
    }

    [Fact]
    public void TheRegionNamesTheSavedEngineAndNothingWhenThereIsNone()
    {
        var rig = new Rig("x", YEngine);
        Assert.Equal(string.Empty, rig.Scene.GetScriptEngineChoice(rig.Part.UUID, rig.Item.ItemID));

        rig.Save("y", "engine:Phlox");
        Assert.Equal(Phlox, rig.Scene.GetScriptEngineChoice(rig.Part.UUID, rig.Item.ItemID));

        rig.Save("y", "engine:YEngine");
        Assert.Equal(YEngine, rig.Scene.GetScriptEngineChoice(rig.Part.UUID, rig.Item.ItemID));

        rig.Save("y", "luau");
        Assert.Equal(string.Empty, rig.Scene.GetScriptEngineChoice(rig.Part.UUID, rig.Item.ItemID));
        Assert.Equal(string.Empty, rig.Scene.GetScriptEngineChoice(UUID.Random(), rig.Item.ItemID));
    }

    [Theory]
    [InlineData("mono", ScriptEngineChoices.Phlox)]
    [InlineData("lsl2", ScriptEngineChoices.Phlox)]
    [InlineData("engine:YEngine", ScriptEngineChoices.YEngine)]
    [InlineData("luau", ScriptEngineChoices.None)]
    public void ASavedScriptInAnAvatarsInventoryFollowsTheSameRule(string target, int expected)
    {
        var rig = new Rig("x", inventoryAccess: true);
        UUID userId = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(rig.Scene, userId);
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, userId);
        InventoryItemBase item = AddAgentScript(rig.Scene, userId, ScriptEngineChoices.WithChoice(0x40000000u, ScriptEngineChoices.Phlox));

        UUID newAsset = rig.Scene.CapsUpdateItemAsset(sp.UUID, item.ID, UUID.Zero, Encoding.UTF8.GetBytes("new text"), target);

        Assert.NotEqual(UUID.Zero, newAsset);
        InventoryItemBase stored = rig.Scene.InventoryService.GetItem(userId, item.ID);
        Assert.Equal(expected, ScriptEngineChoices.FromFlags(stored.Flags));
        Assert.Equal(0x40000000u, stored.Flags & 0x40000000u);
    }

    private static InventoryItemBase AddAgentScript(Scene scene, UUID userId, uint flags)
    {
        AssetBase asset = AssetHelpers.CreateNotecardAsset();
        asset.Type = (sbyte)AssetType.LSLText;
        scene.AssetService.Store(asset);
        InventoryFolderBase folder = scene.InventoryService.GetFolderForType(userId, FolderType.LSLText);
        var item = new InventoryItemBase
        {
            Name = "agent script",
            AssetID = asset.FullID,
            ID = UUID.Random(),
            Owner = userId,
            CreatorId = userId.ToString(),
            AssetType = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            BasePermissions = (uint)OpenMetaverse.PermissionMask.All,
            CurrentPermissions = (uint)OpenMetaverse.PermissionMask.All,
            NextPermissions = (uint)OpenMetaverse.PermissionMask.All,
            Folder = folder.ID,
            Flags = flags,
        };
        scene.AddInventoryItem(item);
        return item;
    }

    // ---- the choice travels with the item ----

    [Fact]
    public void TheChoiceSurvivesCopyingFromAnObjectToInventoryAndBack()
    {
        var rig = new Rig("x", inventoryAccess: true);
        UUID userId = rig.Part.OwnerID;
        UserAccountHelpers.CreateUserWithInventory(rig.Scene, userId);
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, userId);
        rig.Choose(ScriptEngineChoices.Phlox);
        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);

        // object to inventory
        UUID folder = rig.Scene.InventoryService.GetFolderForType(userId, FolderType.LSLText).ID;
        InventoryItemBase copied = rig.Scene.MoveTaskInventoryItem(userId, folder, rig.Part, rig.Item.ItemID, out _);
        Assert.NotNull(copied);
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(rig.Scene.InventoryService.GetItem(userId, copied.ID).Flags));

        // inventory to another object
        SceneObjectPart other = SceneHelpers.AddSceneObject(rig.Scene, "other", userId).RootPart;
        rig.Scene.RezScript(sp.ControllingClient, rig.Scene.InventoryService.GetItem(userId, copied.ID), UUID.Zero, other.LocalId);
        TaskInventoryItem dropped = Assert.Single(other.Inventory.GetInventoryItems(InventoryType.LSL));
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(dropped.Flags));
        Assert.Contains(dropped.ItemID, rig.P.Started);
    }

    [Fact]
    public void TheChoiceSurvivesCopyingBetweenObjects()
    {
        var rig = new Rig("x");
        rig.Choose(ScriptEngineChoices.YEngine);
        SceneObjectPart other = SceneHelpers.AddSceneObject(rig.Scene, "other", rig.Part.OwnerID).RootPart;

        rig.Scene.MoveTaskInventoryItem(other.UUID, rig.Part, rig.Item.ItemID);

        TaskInventoryItem copied = Assert.Single(other.Inventory.GetInventoryItems(InventoryType.LSL));
        Assert.Equal(ScriptEngineChoices.YEngine, ScriptEngineChoices.FromFlags(copied.Flags));
    }

    [Fact]
    public void AViewerChangingTheScriptsPropertiesKeepsTheChoice()
    {
        var rig = new Rig("x");
        rig.Choose(ScriptEngineChoices.Phlox);
        var client = new TestClient(SceneHelpers.GenerateAgentData(rig.Part.OwnerID), rig.Scene);
        TaskInventoryItem fromViewer = (TaskInventoryItem)rig.Part.Inventory.GetInventoryItem(rig.Item.ItemID).Clone();
        fromViewer.Flags = ScriptCompileTargets.ForViewer(fromViewer.Flags);
        fromViewer.Name = "renamed";

        rig.Scene.UpdateTaskInventory(client, UUID.Zero, fromViewer, rig.Part.LocalId);

        Assert.Equal(ScriptEngineChoices.Phlox, rig.Choice);
    }

    [Fact]
    public void TheChoiceSurvivesTheObjectXmlUsedByTakeRezAndOar()
    {
        var rig = new Rig("x");
        rig.Choose(ScriptEngineChoices.Phlox);

        string xml2 = SceneObjectSerializer.ToXml2Format(rig.Part.ParentGroup);
        SceneObjectGroup fromXml2 = SceneObjectSerializer.FromXml2Format(xml2);
        string original = SceneObjectSerializer.ToOriginalXmlFormat(rig.Part.ParentGroup);
        SceneObjectGroup fromOriginal = SceneObjectSerializer.FromOriginalXmlFormat(original);

        foreach (SceneObjectGroup group in new[] { fromXml2, fromOriginal })
        {
            TaskInventoryItem item = Assert.Single(group.RootPart.Inventory.GetInventoryItems(InventoryType.LSL));
            Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(item.Flags));
        }
    }

    [Fact]
    public void TheChoiceSurvivesTakeAndRez()
    {
        var rig = new Rig("x", inventoryAccess: true);
        UUID userId = rig.Part.OwnerID;
        UserAccountHelpers.CreateUserWithInventory(rig.Scene, userId);
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, userId);
        rig.Choose(ScriptEngineChoices.Phlox);
        SceneObjectGroup group = rig.Part.ParentGroup;

        UUID folder = rig.Scene.InventoryService.GetFolderForType(userId, FolderType.Object).ID;
        List<InventoryItemBase> taken = rig.Iam.CopyToInventory(DeRezAction.Take, folder, new List<SceneObjectGroup> { group }, sp.ControllingClient, false);
        InventoryItemBase objectItem = Assert.Single(taken);
        rig.Scene.DeleteSceneObject(group, false);
        rig.P.Started.Clear();

        SceneObjectGroup rezzed = rig.Iam.RezObject(sp.ControllingClient, objectItem.ID, UUID.Zero, Vector3.Zero, Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);

        Assert.NotNull(rezzed);
        TaskInventoryItem script = Assert.Single(rezzed.RootPart.Inventory.GetInventoryItems(InventoryType.LSL));
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(script.Flags));
        Assert.Contains(script.ItemID, rig.P.Started);
        Assert.DoesNotContain(script.ItemID, rig.Y.Started);
    }
}
