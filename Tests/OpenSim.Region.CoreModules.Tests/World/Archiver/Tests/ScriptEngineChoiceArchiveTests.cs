/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Inventory.Archiver;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Archiver.Tests;

/// <summary>
/// A script engine saved with a script (ScriptEngineChoices, in the item's flags) is still there after the region is
/// saved to an OAR and loaded into another region, and after a user's inventory is saved to an IAR and loaded for another
/// user: both for a script in inventory and for a script inside an object in inventory.
/// </summary>
[Collection("FireAndForgetMethod")]
public class ScriptEngineChoiceArchiveTests : OpenSimTestCase
{
    private static uint WithPhlox(uint flags) => ScriptEngineChoices.WithChoice(flags, ScriptEngineChoices.Phlox);

    public override void SetUp()
    {
        base.SetUp();
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    [Fact]
    public void TheChoiceSurvivesAnOar()
    {
        var saveArchiver = new ArchiverModule();
        Scene source = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(source, saveArchiver, new SerialiserModule(), new TerrainModule());
        SceneObjectGroup group = SceneHelpers.CreateSceneObject(1, UUID.Random(), "Example Object", 0x10);
        source.AddNewSceneObject(group, true);
        TaskInventoryItem script = TaskInventoryHelpers.AddScript(source.AssetService, group.RootPart, "chosen", "default { state_entry() { } }");
        script.Flags = WithPhlox(script.Flags | 0x40000000u);

        var saved = new ManualResetEvent(false);
        string saveMessage = null;
        source.EventManager.OnOarFileSaved += (guid, message) => { saveMessage = message; saved.Set(); };
        var oar = new MemoryStream();
        saveArchiver.ArchiveRegion(oar, Guid.NewGuid());
        Assert.True(saved.WaitOne(60000), "the OAR was not saved");
        Assert.True(string.IsNullOrEmpty(saveMessage), saveMessage);

        var loadArchiver = new ArchiverModule();
        Scene target = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(target, loadArchiver, new SerialiserModule(), new TerrainModule());
        var loaded = new ManualResetEvent(false);
        string loadMessage = null;
        target.EventManager.OnOarFileLoaded += (guid, scenes, message) => { loadMessage = message; loaded.Set(); };
        loadArchiver.DearchiveRegion(new MemoryStream(oar.ToArray()));
        Assert.True(loaded.WaitOne(60000), "the OAR was not loaded");
        Assert.True(string.IsNullOrEmpty(loadMessage), loadMessage);
        SceneObjectPart part = Assert.Single(target.GetSceneObjectGroups()).RootPart;
        Assert.Equal(group.Name, part.ParentGroup.Name);
        TaskInventoryItem after = Assert.Single(part.Inventory.GetInventoryItems(InventoryType.LSL));
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(after.Flags));
        Assert.Equal(0x40000000u, after.Flags & 0x40000000u);
    }

    [Fact]
    public void TheChoiceSurvivesAnIarForAScriptAndForAScriptInsideAnObject()
    {
        var saveArchiver = new InventoryArchiverModule();
        Scene source = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(source, saveArchiver, new SerialiserModule());
        UserAccount saver = UserAccountHelpers.CreateUserWithInventory(source, "Test", "Saver", UUID.Random(), "password");
        UUID root = source.InventoryService.GetRootFolder(saver.PrincipalID).ID;

        AssetBase scriptAsset = AssetHelpers.CreateAsset(UUID.Random(), AssetType.LSLText, "default { state_entry() { } }", saver.PrincipalID);
        source.AssetService.Store(scriptAsset);
        source.AddInventoryItem(new InventoryItemBase
        {
            Name = "chosen script",
            ID = UUID.Random(),
            AssetID = scriptAsset.FullID,
            AssetType = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            CreatorId = saver.PrincipalID.ToString(),
            Owner = saver.PrincipalID,
            Folder = root,
            Flags = WithPhlox(0x40000000u),
        });

        SceneObjectGroup boxed = SceneHelpers.CreateSceneObject(1, saver.PrincipalID, "Boxed Object", 0x20);
        TaskInventoryItem inside = TaskInventoryHelpers.AddScript(source.AssetService, boxed.RootPart, "inside", "default { state_entry() { } }");
        inside.Flags = WithPhlox(inside.Flags);
        AssetBase objectAsset = AssetHelpers.CreateAsset(UUID.Random(), boxed);
        source.AssetService.Store(objectAsset);
        source.AddInventoryItem(new InventoryItemBase
        {
            Name = "boxed",
            ID = UUID.Random(),
            AssetID = objectAsset.FullID,
            AssetType = (int)AssetType.Object,
            InvType = (int)InventoryType.Object,
            CreatorId = saver.PrincipalID.ToString(),
            Owner = saver.PrincipalID,
            Folder = root,
        });

        var saved = new ManualResetEvent(false);
        saveArchiver.OnInventoryArchiveSaved += (id, ok, user, path, stream, e, count, filtered) => saved.Set();
        var iar = new MemoryStream();
        saveArchiver.ArchiveInventory(UUID.Random(), "Test", "Saver", "/*", "password", iar);
        Assert.True(saved.WaitOne(60000), "the IAR was not saved");

        var loadArchiver = new InventoryArchiverModule();
        Scene target = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(target, loadArchiver, new SerialiserModule());
        UserAccount loader = UserAccountHelpers.CreateUserWithInventory(target, "Test", "Loader", UUID.Random(), "password");
        loadArchiver.DearchiveInventory(UUID.Random(), "Test", "Loader", "/", "password", new MemoryStream(iar.ToArray()));

        InventoryItemBase loadedScript = InventoryArchiveUtils.FindItemByPath(target.InventoryService, loader.PrincipalID, "chosen script");
        Assert.NotNull(loadedScript);
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(loadedScript.Flags));
        Assert.Equal(0x40000000u, loadedScript.Flags & 0x40000000u);

        InventoryItemBase loadedObject = InventoryArchiveUtils.FindItemByPath(target.InventoryService, loader.PrincipalID, "boxed");
        Assert.NotNull(loadedObject);
        string xml = Utils.BytesToString(target.AssetService.Get(loadedObject.AssetID.ToString()).Data);
        SceneObjectGroup unboxed = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        TaskInventoryItem loadedInside = Assert.Single(unboxed.RootPart.Inventory.GetInventoryItems(InventoryType.LSL));
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(loadedInside.Flags));
    }
}
