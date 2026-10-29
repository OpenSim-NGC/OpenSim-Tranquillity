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
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A viewer's compile target ("luau" or "lsl-luau", sent when its script editor saves a script) is kept with the
/// script item and picks the engine ahead of the script's first line. "mono" and "lsl2" make no explicit choice, so
/// the first line, a claim and the default decide as before. Saved state still places a script until it is edited and
/// saved (a new asset has none).
/// </summary>
public class ScriptCompileTargetSelectionTests : OpenSimTestCase
{
    private const string Default = "Alpha", Other = "Beta";

    private sealed class Engine : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName { get; }
        public IReadOnlyList<string> HeaderAliases { get; init; } = Array.Empty<string>();
        public Func<string, bool> Claims { get; set; } = _ => false;
        public Func<UUID, UUID, bool> HoldsState { get; set; } = (_, _) => false;
        public bool AcceptsTargets { get; set; }
        public readonly List<ScriptCompileTarget> Asked = new();
        public readonly List<UUID> Started = new();

        public Engine(string name) { ScriptEngineName = name; }

        public bool ClaimsScript(string scriptSource) => Claims(scriptSource);

        public bool AcceptsCompileTarget(ScriptCompileTarget target)
        {
            Asked.Add(target);
            return AcceptsTargets;
        }

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
        public Engine Alpha = new(Default) { HeaderAliases = new[] { "Alf" } };
        public Engine Beta = new(Other) { Claims = s => s.StartsWith("--!b"), AcceptsTargets = true };
        public SceneObjectPart Part;
        public TaskInventoryItem Item;
        public TestClient Client;

        public Rig(string source = "plain", bool inventoryAccess = false)
        {
            var config = new IniConfigSource();
            config.AddConfig("Startup").Set("DefaultScriptEngine", Default);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            if (inventoryAccess)
            {
                config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
                SceneHelpers.SetupSceneModules(Scene, config, new BasicInventoryAccessModule());
            }
            Scene.StackModuleInterface<IScriptModule>(Alpha);
            Scene.StackModuleInterface<IScriptModule>(Beta);
            Alpha.Attach(Scene);
            Beta.Attach(Scene);
            Part = SceneHelpers.AddSceneObject(Scene).RootPart;
            Item = TaskInventoryHelpers.AddScript(Scene.AssetService, Part, "script", source);
            Client = new TestClient(SceneHelpers.GenerateAgentData(UUID.Random()), Scene);
        }

        public void WithTarget(ScriptCompileTarget target)
            => Item.Flags = ScriptCompileTargets.WithTarget(Item.Flags, target);

        public string Selected(string source) => Scene.ResolveScriptEngine(Default, source, Item)?.ScriptEngineName;

        public void Start() => Assert.True(Part.Inventory.CreateScriptInstance(Item, 0, false, Default, 0));

        public ArrayList Save(string source, string target)
            => Scene.CapsUpdateTaskInventoryScriptAsset(Client, Item.ItemID, Part.UUID, true, UUID.Zero, Encoding.UTF8.GetBytes(source), target);

        public ScriptCompileTarget Stored => ScriptCompileTargets.FromFlags(Part.Inventory.GetInventoryItem(Item.ItemID).Flags);
    }

    [Theory]
    // an explicit Luau-family target wins over every first line, a claim and the default
    [InlineData(ScriptCompileTarget.Luau, "x", Other)]
    [InlineData(ScriptCompileTarget.Luau, "//Alpha:\nx", Other)]
    [InlineData(ScriptCompileTarget.Luau, "//Alf:\nx", Other)]
    [InlineData(ScriptCompileTarget.Luau, "//Beta:\nx", Other)]
    [InlineData(ScriptCompileTarget.Luau, "--!b\nx", Other)]
    [InlineData(ScriptCompileTarget.LslOnLuau, "x", Other)]
    [InlineData(ScriptCompileTarget.LslOnLuau, "//Alpha:\nx", Other)]
    [InlineData(ScriptCompileTarget.LslOnLuau, "//Alf:\nx", Other)]
    [InlineData(ScriptCompileTarget.LslOnLuau, "//Beta:\nx", Other)]
    [InlineData(ScriptCompileTarget.LslOnLuau, "--!b\nx", Other)]
    // no explicit choice: the first line, then the claim, then the default, exactly as before
    [InlineData(ScriptCompileTarget.Unspecified, "x", Default)]
    [InlineData(ScriptCompileTarget.Unspecified, "//Alpha:\nx", Default)]
    [InlineData(ScriptCompileTarget.Unspecified, "//Alf:\nx", Default)]
    [InlineData(ScriptCompileTarget.Unspecified, "//Beta:\nx", Other)]
    [InlineData(ScriptCompileTarget.Unspecified, "--!b\nx", Other)]
    public void TheTargetPicksTheEngineAheadOfTheFirstLine(ScriptCompileTarget target, string source, string expected)
    {
        var rig = new Rig(source);
        rig.WithTarget(target);

        Assert.Equal(expected, rig.Selected(source));
    }

    [Fact]
    public void TheEnginesAreAskedOnlyForARealTarget()
    {
        var rig = new Rig();

        rig.Selected("x");
        Assert.Empty(rig.Alpha.Asked);
        Assert.Empty(rig.Beta.Asked);

        rig.WithTarget(ScriptCompileTarget.LslOnLuau);
        rig.Selected("x");
        Assert.Contains(ScriptCompileTarget.LslOnLuau, rig.Beta.Asked);
    }

    [Theory]
    [InlineData("x", Default)]
    [InlineData("//Beta:\nx", Other)]
    [InlineData("--!b\nx", Other)]
    public void ATargetNoEngineAcceptsLeavesTheScriptToTheFirstLineTheClaimAndTheDefault(string source, string expected)
    {
        var rig = new Rig(source);
        rig.Beta.AcceptsTargets = false;
        rig.WithTarget(ScriptCompileTarget.Luau);

        Assert.Equal(expected, rig.Selected(source));
    }

    [Fact]
    public void SavedStateInAnotherEngineStillPlacesAScriptThatCarriesATarget()
    {
        var rig = new Rig("x");
        rig.WithTarget(ScriptCompileTarget.Luau);
        UUID item = rig.Item.ItemID, asset = rig.Item.AssetID;
        rig.Alpha.HoldsState = (i, a) => i == item && a == asset;

        Assert.Equal(Default, rig.Selected("x"));
    }

    [Fact]
    public void SavedStateInTheTargetsOwnEngineKeepsTheScriptThere()
    {
        var rig = new Rig("x");
        rig.WithTarget(ScriptCompileTarget.Luau);
        UUID item = rig.Item.ItemID, asset = rig.Item.AssetID;
        rig.Beta.HoldsState = (i, a) => i == item && a == asset;

        Assert.Equal(Other, rig.Selected("x"));
    }

    [Fact]
    public void SavedStateForAnEarlierAssetDoesNotPlaceTheEditedScript()
    {
        var rig = new Rig("x");
        rig.WithTarget(ScriptCompileTarget.Luau);
        UUID item = rig.Item.ItemID;
        rig.Alpha.HoldsState = (i, a) => i == item && a != rig.Item.AssetID;

        Assert.Equal(Other, rig.Selected("x"));
    }

    [Fact]
    public void SavingWithLuauKeepsTheTargetWithTheItemAndStartsTheScriptOnTheEngineThatAcceptsIt()
    {
        var rig = new Rig("x");
        rig.Start();
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Alpha.Started);

        rig.Save("y", "luau");

        Assert.Equal(ScriptCompileTarget.Luau, rig.Stored);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Beta.Started);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Alpha.Started);
    }

    [Fact]
    public void SavingWithLslOnLuauKeepsThatTargetAndBeatsAFirstLineNamingAnotherEngine()
    {
        var rig = new Rig("//Alf:\nx");
        rig.Start();

        rig.Save("//Alf:\ny", "lsl-luau");

        Assert.Equal(ScriptCompileTarget.LslOnLuau, rig.Stored);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Beta.Started);
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("lsl2")]
    public void SavingWithMonoOrLslLegacyClearsAnEarlierTargetAndLetsTheFirstLineAndTheDefaultDecide(string target)
    {
        var rig = new Rig("x");
        rig.Save("y", "luau");
        Assert.Equal(ScriptCompileTarget.Luau, rig.Stored);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Beta.Started);

        rig.Save("z", target);

        Assert.Equal(ScriptCompileTarget.Unspecified, rig.Stored);
        Assert.Equal(new[] { rig.Item.ItemID }, rig.Alpha.Started);
    }

    [Fact]
    public void SavingWithNoTargetFieldLeavesAnEarlierTargetAsItWas()
    {
        var rig = new Rig("x");
        rig.Save("y", "lsl-luau");

        rig.Save("z", null);

        Assert.Equal(ScriptCompileTarget.LslOnLuau, rig.Stored);
    }

    [Fact]
    public void SavingKeepsEveryOtherFlagOfTheItem()
    {
        var rig = new Rig("x");
        rig.Part.Inventory.GetInventoryItem(rig.Item.ItemID).Flags = 0x40000100u;

        rig.Save("y", "luau");

        Assert.Equal(0x40000100u, rig.Part.Inventory.GetInventoryItem(rig.Item.ItemID).Flags & ~ScriptCompileTargets.SubtypeMask);
    }

    [Fact]
    public void TheRegionReportsTheTargetKeptWithAScriptAndNoneForAnythingElse()
    {
        var rig = new Rig("x");

        Assert.Equal(ScriptCompileTarget.Unspecified, rig.Scene.GetScriptCompileTarget(rig.Part.UUID, rig.Item.ItemID));

        rig.Save("y", "luau");
        Assert.Equal(ScriptCompileTarget.Luau, rig.Scene.GetScriptCompileTarget(rig.Part.UUID, rig.Item.ItemID));

        rig.Save("y", "lsl-luau");
        Assert.Equal(ScriptCompileTarget.LslOnLuau, rig.Scene.GetScriptCompileTarget(rig.Part.UUID, rig.Item.ItemID));

        Assert.Equal(ScriptCompileTarget.Unspecified, rig.Scene.GetScriptCompileTarget(UUID.Random(), rig.Item.ItemID));
        Assert.Equal(ScriptCompileTarget.Unspecified, rig.Scene.GetScriptCompileTarget(rig.Part.UUID, UUID.Random()));
    }

    [Theory]
    [InlineData("luau", ScriptCompileTarget.Luau)]
    [InlineData("lsl-luau", ScriptCompileTarget.LslOnLuau)]
    [InlineData("mono", ScriptCompileTarget.Unspecified)]
    public void ASavedScriptInAnAvatarsInventoryKeepsTheTargetInItsFlags(string target, ScriptCompileTarget expected)
    {
        var rig = new Rig("x", inventoryAccess: true);
        UUID userId = UUID.Random();
        UserAccountHelpers.CreateUserWithInventory(rig.Scene, userId);
        ScenePresence sp = SceneHelpers.AddScenePresence(rig.Scene, userId);

        AssetBase asset = AssetHelpers.CreateNotecardAsset();
        asset.Type = (sbyte)AssetType.LSLText;
        rig.Scene.AssetService.Store(asset);
        InventoryFolderBase folder = rig.Scene.InventoryService.GetFolderForType(userId, FolderType.LSLText);
        var item = new InventoryItemBase
        {
            Name = "agent script",
            AssetID = asset.FullID,
            ID = UUID.Random(),
            Owner = userId,
            AssetType = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            BasePermissions = (uint)OpenMetaverse.PermissionMask.All,
            CurrentPermissions = (uint)OpenMetaverse.PermissionMask.All,
            Folder = folder.ID,
            // an earlier target of the other kind must be replaced, a mono save must clear it
            Flags = 0x40000000u | (target == "luau" ? (uint)ScriptCompileTarget.LslOnLuau : (uint)ScriptCompileTarget.Luau),
        };
        rig.Scene.AddInventoryItem(item);

        UUID newAsset = rig.Scene.CapsUpdateItemAsset(sp.UUID, item.ID, UUID.Zero, Encoding.UTF8.GetBytes("new text"), target);

        Assert.NotEqual(UUID.Zero, newAsset);
        InventoryItemBase stored = rig.Scene.InventoryService.GetItem(userId, item.ID);
        Assert.Equal(expected, ScriptCompileTargets.FromFlags((uint)stored.Flags));
        Assert.Equal((uint)expected, (uint)stored.Flags & ScriptCompileTargets.SubtypeMask);   // the stored byte itself, not only how it reads back
        Assert.Equal(0x40000000u, (uint)stored.Flags & ~ScriptCompileTargets.SubtypeMask);
        Assert.Equal(newAsset, stored.AssetID);
    }
}
