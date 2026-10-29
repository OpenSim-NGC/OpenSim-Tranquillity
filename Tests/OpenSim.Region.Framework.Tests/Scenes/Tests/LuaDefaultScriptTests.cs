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
/// A viewer that is told the region runs Lua creates a new script with the Lua subtype (the low byte of the item's
/// flags is 1, Second Life's SST_LUA). The region then gives the script a Lua default text in place of the LSL one,
/// both for a script made in an object and for one made in the avatar's inventory. A region with no engine that takes
/// Lua scripts keeps the LSL default whatever the viewer sent.
/// </summary>
public class LuaDefaultScriptTests : OpenSimTestCase
{
    private const string Default = "Alpha", Other = "Beta";

    private sealed class Engine : MockScriptEngine, IScriptModule, IScriptStateProvider, IScriptEngineSelection
    {
        public new string ScriptEngineName { get; }
        public IReadOnlyList<string> HeaderAliases => Array.Empty<string>();
        public bool AcceptsTargets { get; set; }
        public readonly List<UUID> Started = new();

        public Engine(string name) { ScriptEngineName = name; }

        public bool ClaimsScript(string scriptSource) => false;
        public bool AcceptsCompileTarget(ScriptCompileTarget target) => AcceptsTargets;

        public ArrayList GetScriptErrors(UUID itemID) => new();
        public bool SuspendScript(UUID itemID) => true;
        public bool ResumeScript(UUID itemID) => true;
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public bool GetScriptState(UUID itemID) => false;
        public void SetScriptState(UUID itemID, bool state, bool self) { }
        public void SaveAllState() { }
        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;
        public bool HasSavedScriptState(UUID itemID, UUID assetID) => false;

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
        public readonly Scene Scene;
        public readonly Engine Alpha = new(Default);
        public readonly Engine Beta = new(Other) { AcceptsTargets = true };
        public readonly SceneObjectPart Part;
        public readonly UUID AgentId = UUID.Random();
        public readonly BasicInventoryAccessModule InventoryAccess = new();

        public Rig(bool betaAcceptsTargets = true)
        {
            Beta.AcceptsTargets = betaAcceptsTargets;
            var config = new IniConfigSource();
            config.AddConfig("Startup").Set("DefaultScriptEngine", Default);
            Scene = new SceneHelpers().SetupScene("Unit test region", UUID.Random(), 1000, 1000, config);
            config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            SceneHelpers.SetupSceneModules(Scene, config, InventoryAccess);
            Scene.StackModuleInterface<IScriptModule>(Alpha);
            Scene.StackModuleInterface<IScriptModule>(Beta);
            Alpha.Attach(Scene);
            Beta.Attach(Scene);
            Part = SceneHelpers.AddSceneObject(Scene).RootPart;
        }

        /// <summary>What a viewer sends to make a new script in an object: a script with no item id, and the subtype in the flags.</summary>
        public TaskInventoryItem NewScriptInObject(uint flags, string name = "New Script")
        {
            var itemBase = new InventoryItemBase
            {
                Folder = Part.UUID,
                Owner = AgentId,
                CreatorId = AgentId.ToString(),
                Name = name,
                Description = string.Empty,
                AssetType = (int)AssetType.LSLText,
                InvType = (int)InventoryType.LSL,
                BasePermissions = (uint)PermissionMask.All,
                CurrentPermissions = (uint)PermissionMask.All,
                NextPermissions = (uint)PermissionMask.All,
                Flags = flags,
            };
            Assert.NotNull(Scene.RezNewScript(AgentId, itemBase));
            return Assert.Single(Part.Inventory.GetInventoryItems(name));
        }

        /// <summary>What the avatar's inventory creates for "New Script" in a folder: the subtype is a separate field of the request.</summary>
        public InventoryItemBase NewScriptInInventory(byte subType, string name = "New Script")
        {
            UserAccountHelpers.CreateUserWithInventory(Scene, AgentId);
            ScenePresence sp = SceneHelpers.AddScenePresence(Scene, AgentId);
            InventoryFolderBase folder = Scene.InventoryService.GetFolderForType(AgentId, FolderType.LSLText);
            InventoryAccess.CreateNewInventoryItem(
                sp.ControllingClient, UUID.Zero, folder.ID, 1, string.Empty, name,
                (sbyte)InventoryType.LSL, (sbyte)AssetType.LSLText, subType, (uint)PermissionMask.All, Util.UnixTimeSinceEpoch());
            return Assert.Single(Scene.InventoryService.GetFolderContent(AgentId, folder.ID).Items, i => i.Name == name);
        }
    }

    // ---- the default text itself ----

    [Fact]
    public void TheLuaDefaultIsAScriptOfItsOwnWithTheTextOfTheLslDefaultsMessage()
    {
        Assert.NotEqual(UUID.Zero, DefaultLuaScript.ID);
        Assert.NotEqual(Constants.DefaultScriptID, DefaultLuaScript.ID);
        Assert.NotEmpty(DefaultLuaScript.Text);
        // it does what the LSL default does: says "Script running" on channel 0 when the script starts
        Assert.Contains("\"Script running\"", Constants.DefaultScriptText);
        Assert.Contains("ll.Say(0, \"Script running\")", DefaultLuaScript.Text);
        // and it is Lua, not LSL
        Assert.DoesNotContain("state_entry", DefaultLuaScript.Text);
        Assert.DoesNotContain("llSay", DefaultLuaScript.Text);
    }

    // ---- a script made in an object ----

    [Fact]
    public void ANewScriptInAnObjectWithTheLuaSubtypeGetsTheLuaDefaultAndIsStartedByTheEngineThatTakesLua()
    {
        var rig = new Rig();

        TaskInventoryItem item = rig.NewScriptInObject(1u);

        Assert.Equal(DefaultLuaScript.ID, item.AssetID);
        AssetBase asset = rig.Scene.AssetService.Get(DefaultLuaScript.ID.ToString());
        Assert.NotNull(asset);
        Assert.Equal((sbyte)AssetType.LSLText, asset.Type);
        Assert.Equal(DefaultLuaScript.Text, Encoding.UTF8.GetString(asset.Data));
        Assert.Equal(ScriptCompileTarget.Luau, ScriptCompileTargets.FromFlags(item.Flags));
        Assert.Equal(new[] { item.ItemID }, rig.Beta.Started);
        Assert.Empty(rig.Alpha.Started);
    }

    [Fact]
    public void ANewScriptInAnObjectWithNoSubtypeGetsTheLslDefaultAndTheDefaultEngineAsBefore()
    {
        var rig = new Rig();

        TaskInventoryItem item = rig.NewScriptInObject(0u);

        Assert.Equal(Constants.DefaultScriptID, item.AssetID);
        Assert.Equal(ScriptCompileTarget.Unspecified, ScriptCompileTargets.FromFlags(item.Flags));
        Assert.Equal(new[] { item.ItemID }, rig.Alpha.Started);
        Assert.Empty(rig.Beta.Started);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(7u)]
    [InlineData(255u)]
    public void ASubtypeThatIsNotLuaGetsTheLslDefault(uint flags)
    {
        var rig = new Rig();

        TaskInventoryItem item = rig.NewScriptInObject(flags);

        Assert.Equal(Constants.DefaultScriptID, item.AssetID);
        Assert.Equal(ScriptCompileTarget.Unspecified, ScriptCompileTargets.FromFlags(item.Flags));
        Assert.Equal(new[] { item.ItemID }, rig.Alpha.Started);
    }

    [Fact]
    public void TheLuaSubtypeIsIgnoredWhenNoEngineTakesLuaScripts()
    {
        var rig = new Rig(betaAcceptsTargets: false);

        TaskInventoryItem item = rig.NewScriptInObject(1u);

        // the region keeps the LSL default, and the item does not claim to be a Lua script
        Assert.Equal(Constants.DefaultScriptID, item.AssetID);
        Assert.Equal(0u, item.Flags & ScriptCompileTargets.SubtypeMask);
        Assert.Equal(new[] { item.ItemID }, rig.Alpha.Started);
    }

    [Fact]
    public void TwoNewLuaScriptsShareTheOneDefaultAsset()
    {
        var rig = new Rig();

        TaskInventoryItem first = rig.NewScriptInObject(1u, "First");
        TaskInventoryItem second = rig.NewScriptInObject(1u, "Second");

        Assert.Equal(DefaultLuaScript.ID, first.AssetID);
        Assert.Equal(DefaultLuaScript.ID, second.AssetID);
        Assert.Equal(DefaultLuaScript.Text, Encoding.UTF8.GetString(rig.Scene.AssetService.Get(DefaultLuaScript.ID.ToString()).Data));
    }

    // ---- a script made in the avatar's inventory ----

    [Fact]
    public void ANewScriptInTheInventoryWithTheLuaSubtypeGetsTheLuaDefaultAndKeepsTheSubtype()
    {
        var rig = new Rig();

        InventoryItemBase item = rig.NewScriptInInventory(1);

        Assert.Equal(DefaultLuaScript.ID, item.AssetID);
        Assert.Equal(1u, (uint)item.Flags & ScriptCompileTargets.SubtypeMask);
        Assert.NotNull(rig.Scene.AssetService.Get(DefaultLuaScript.ID.ToString()));
    }

    [Fact]
    public void ANewScriptInTheInventoryWithNoSubtypeGetsTheLslDefaultAsBefore()
    {
        var rig = new Rig();

        InventoryItemBase item = rig.NewScriptInInventory(0);

        Assert.Equal(Constants.DefaultScriptID, item.AssetID);
        Assert.Equal(0u, (uint)item.Flags & ScriptCompileTargets.SubtypeMask);
    }

    [Fact]
    public void ANewScriptInTheInventoryIgnoresTheLuaSubtypeWhenNoEngineTakesLuaScripts()
    {
        var rig = new Rig(betaAcceptsTargets: false);

        InventoryItemBase item = rig.NewScriptInInventory(1);

        Assert.Equal(Constants.DefaultScriptID, item.AssetID);
        Assert.Equal(0u, (uint)item.Flags & ScriptCompileTargets.SubtypeMask);
    }
}
