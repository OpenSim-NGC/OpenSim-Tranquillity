/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using System.Reflection;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.World.Permissions;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.ClientStack.LindenCaps.Tests;

/// <summary>
/// The caps that save new content into an existing item (UpdateNotecardAgentInventory, UpdateScriptAgent,
/// UpdateNotecardTaskInventory and the rest). Each saves one type of item, and the checks made before the content
/// is stored are those of that type.
/// </summary>
public class ItemAssetUpdateCapTests : OpenSimTestCase
{
    private const uint NoModify = (uint)(PermissionMask.Copy | PermissionMask.Transfer | PermissionMask.Move);

    private sealed class Region
    {
        public Scene Scene;
        public UUID Owner = UUID.Random();
        public BunchOfCaps Caps;
    }

    private static Region NewRegion()
    {
        Region r = new() { Scene = new SceneHelpers().SetupScene() };
        SceneHelpers.SetupSceneModules(r.Scene, new IniConfigSource(), new DefaultPermissionsModule());
        UserAccountHelpers.CreateUserWithInventory(r.Scene, r.Owner);
        SceneHelpers.AddScenePresence(r.Scene, r.Owner);

        Caps caps = new(new BaseHttpServer(0), "example.org", 9000, "/CAPS/" + UUID.Random(), r.Owner, "Example Region");
        r.Caps = new BunchOfCaps(r.Scene, r.Owner, caps, new BunchOfCapsConfigOptions());
        return r;
    }

    [Fact]
    public void TheNotecardCapSavesANotecard()
    {
        Region r = NewRegion();
        InventoryItemBase notecard = AgentItem(r, AssetType.Notecard, InventoryType.Notecard, (uint)PermissionMask.All);

        OSDMap answer = Ask(r.Caps.UpdateNotecardItemAsset, notecard.ID, UUID.Zero);

        Assert.Equal("upload", answer["state"].AsString());
    }

    [Fact]
    public void TheNotecardCapDoesNotSaveAScript()
    {
        Region r = NewRegion();
        InventoryItemBase script = AgentItem(r, AssetType.LSLText, InventoryType.LSL, NoModify);

        OSDMap answer = Ask(r.Caps.UpdateNotecardItemAsset, script.ID, UUID.Zero);

        Assert.False(answer.ContainsKey("uploader"));
        Assert.Equal("item is not of this type", answer["message"].AsString());
    }

    [Fact]
    public void TheNotecardCapDoesNotSaveAScriptInAnObject()
    {
        Region r = NewRegion();
        SceneObjectGroup so = SceneHelpers.AddSceneObject(r.Scene, "box", r.Owner);
        TaskInventoryItem script = TaskScript(r, so, NoModify);

        OSDMap answer = Ask(r.Caps.UpdateNotecardItemAsset, script.ItemID, so.UUID);

        Assert.False(answer.ContainsKey("uploader"));
        Assert.Equal("item is not of this type", answer["message"].AsString());
    }

    [Fact]
    public void ANoModifyScriptInAnObjectIsNotSaved()
    {
        Region r = NewRegion();
        SceneObjectGroup so = SceneHelpers.AddSceneObject(r.Scene, "box", r.Owner);
        TaskInventoryItem script = TaskScript(r, so, NoModify);
        UUID before = script.AssetID;

        UUID saved = r.Scene.CapsUpdateItemAsset(r.Owner, script.ItemID, so.UUID, Encoding.UTF8.GetBytes("default { }"));

        Assert.Equal(UUID.Zero, saved);
        Assert.Equal(before, so.RootPart.Inventory.GetInventoryItem(script.ItemID).AssetID);
    }

    [Fact]
    public void AModifiableScriptInAnObjectIsSaved()
    {
        Region r = NewRegion();
        SceneObjectGroup so = SceneHelpers.AddSceneObject(r.Scene, "box", r.Owner);
        TaskInventoryItem script = TaskScript(r, so, (uint)PermissionMask.All);

        UUID saved = r.Scene.CapsUpdateItemAsset(r.Owner, script.ItemID, so.UUID, Encoding.UTF8.GetBytes("default { }"));

        Assert.NotEqual(UUID.Zero, saved);
        Assert.Equal(saved, so.RootPart.Inventory.GetInventoryItem(script.ItemID).AssetID);
    }

    private static InventoryItemBase AgentItem(Region r, AssetType type, InventoryType invType, uint perms)
    {
        AssetBase asset = AssetHelpers.CreateAsset(UUID.Random(), type, "content", r.Owner);
        r.Scene.AssetService.Store(asset);
        InventoryItemBase item = new()
        {
            ID = UUID.Random(),
            Name = "item",
            AssetID = asset.FullID,
            AssetType = (int)type,
            InvType = (int)invType,
            Owner = r.Owner,
            CreatorId = r.Owner.ToString(),
            Folder = r.Scene.InventoryService.GetRootFolder(r.Owner).ID,
            BasePermissions = perms,
            CurrentPermissions = perms,
            NextPermissions = perms,
        };
        Assert.True(r.Scene.InventoryService.AddItem(item));
        return item;
    }

    private static TaskInventoryItem TaskScript(Region r, SceneObjectGroup so, uint perms)
    {
        TaskInventoryItem item = TaskInventoryHelpers.AddScript(
            r.Scene.AssetService, so.RootPart, UUID.Random(), UUID.Random(), "script", "default { }");
        item.OwnerID = r.Owner;
        item.BasePermissions = perms;
        item.CurrentPermissions = perms;
        so.RootPart.Inventory.UpdateInventoryItem(item);
        return item;
    }

    /// <summary>Asks the cap, as a viewer does, for an upload address for the item's new content.</summary>
    private static OSDMap Ask(Action<IOSHttpRequest, IOSHttpResponse, OSDMap> cap, UUID itemID, UUID objectID)
    {
        OSDMap body = new() { ["item_id"] = itemID };
        if (objectID.IsNotZero())
            body["task_id"] = objectID;

        IOSHttpRequest request = StubProxy.Create<IOSHttpRequest>((method, args) =>
            method.Name == "get_RemoteIPEndPoint" ? (true, new IPEndPoint(IPAddress.Loopback, 13000)) : (false, null));
        TestOSHttpResponse response = new();
        cap(request, response, body);
        return Assert.IsType<OSDMap>(OSDParser.DeserializeLLSDXml(response.RawBuffer));
    }
}
