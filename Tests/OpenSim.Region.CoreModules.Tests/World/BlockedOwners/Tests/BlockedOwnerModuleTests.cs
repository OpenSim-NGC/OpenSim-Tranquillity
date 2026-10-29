/*
 * Copyright (c) Legion Builds
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Inventory.Archiver;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.World.Objects.BlockedOwners;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.BlockedOwners.Tests;

/// <summary>
/// The blocked-owner module refuses every rez path for an owner on the region's list, and changes nothing
/// for anyone else or for a region that blocks no one.
/// </summary>
public class BlockedOwnerModuleTests : OpenSimTestCase
{
    private static readonly UUID BlockedId = new("a1b6e2c0-3f5d-4e8a-9c71-2d4f6b8e0a13");
    private static readonly UUID OtherId = new("5e9c7a21-8b34-4f06-a2d8-c1e7f3b95046");
    private static readonly Vector3 RezPos = new(10, 10, 25);

    private TestScene m_scene = null!;
    private BlockedOwnerModule m_module = null!;
    private BasicInventoryAccessModule m_iam = null!;

    private void Setup(bool blockEstateBanned = false)
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
        if (blockEstateBanned)
            config.AddConfig("BlockedOwners").Set("BlockEstateBanned", "true");

        m_module = new BlockedOwnerModule();
        m_iam = new BasicInventoryAccessModule();
        m_scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, config, m_iam, m_module);

        UserAccountHelpers.CreateUserWithInventory(m_scene, "Blocked", "Owner", BlockedId, "pw");
        UserAccountHelpers.CreateUserWithInventory(m_scene, "Other", "Owner", OtherId, "pw");
    }

    // A rezzer object owned by `owner` holding an object item, both owned by `owner`.
    private int m_nextTail = 0x100;

    private (SceneObjectPart host, TaskInventoryItem item) AddRezzer(UUID owner, string itemName)
    {
        SceneObjectGroup rezzer = SceneHelpers.CreateSceneObject(1, owner, "rezzer-" + itemName, m_nextTail++);
        SceneObjectGroup child = SceneHelpers.CreateSceneObject(1, owner, itemName, m_nextTail++);
        TaskInventoryItem item = TaskInventoryHelpers.AddSceneObject(
            m_scene.AssetService, rezzer.RootPart, itemName, UUID.Random(), child, UUID.Random());
        Assert.True(m_scene.AddSceneObject(rezzer));
        return (rezzer.RootPart, item);
    }

    // An object item in `owner`'s user inventory.
    private UUID AddUserInventoryObject(UUID owner, string itemName)
    {
        SceneObjectGroup obj = SceneHelpers.CreateSceneObject(1, owner, itemName, m_nextTail++);
        AssetBase asset = AssetHelpers.CreateAsset(UUID.Random(), obj);
        m_scene.AssetService.Store(asset);

        InventoryItemBase item = new()
        {
            Name = itemName,
            AssetID = asset.FullID,
            ID = UUID.Random(),
            Owner = owner,
            Folder = InventoryArchiveUtils.FindFoldersByPath(m_scene.InventoryService, owner, "Objects")[0].ID
        };
        m_scene.AddInventoryItem(item);
        return item.ID;
    }

    private SceneObjectGroup RezFromUserInventory(UUID owner, UUID itemId)
    {
        TestClient client = new(new AgentCircuitData { AgentID = owner }, m_scene);
        return m_iam.RezObject(
            client, itemId, UUID.Zero, RezPos, Vector3.Zero, UUID.Zero, 1, false, false, false, UUID.Zero, false);
    }

    private bool AddNewPrim(UUID owner)
    {
        int before = m_scene.SceneGraph.GetTotalObjectsCount();
        m_scene.AddNewPrim(owner, UUID.Zero, RezPos, Quaternion.Identity, PrimitiveBaseShape.CreateBox(),
            1, RezPos, UUID.Zero, 0, 0);
        return m_scene.SceneGraph.GetTotalObjectsCount() == before + 1;
    }

    private SceneObjectGroup Duplicate(UUID agent)
    {
        SceneObjectGroup original = SceneHelpers.CreateSceneObject(1, agent, "original", m_nextTail++);
        m_scene.AddNewSceneObject(original, false);
        return m_scene.SceneGraph.DuplicateObject(
            original.LocalId, new Vector3(1, 0, 0), agent, UUID.Zero, Quaternion.Identity, false);
    }

    private int HandlerCount(string eventName)
    {
        FieldInfo f = typeof(ScenePermissions).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ScenePermissions).FullName, eventName);
        return (f.GetValue(m_scene.Permissions) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void AScriptRezByABlockedOwnerIsRefused()
    {
        // Scene.RezObject(part, item, pos, ...) is what YEngine's llRezObject and llRezAtRoot call.
        Setup();
        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(BlockedId, "child");
        m_module.Block(BlockedId);

        Assert.Null(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));
        Assert.Null(m_scene.GetSceneObjectGroup("child"));
    }

    [Fact]
    public void ARezNamingTheNewOwnerIsRefusedForABlockedOwner()
    {
        // The overload with an explicit owner is what the Phlox engine's rez functions and a viewer rez
        // from a prim's inventory call.
        Setup();
        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(BlockedId, "child");
        m_module.Block(BlockedId);

        Assert.Null(m_scene.RezObject(host, item, BlockedId, UUID.Zero, RezPos, null, Vector3.Zero, 0, false, false, false));
        Assert.Null(m_scene.GetSceneObjectGroup("child"));
    }

    [Fact]
    public void ARezFromUserInventoryByABlockedOwnerIsRefused()
    {
        Setup();
        UUID itemId = AddUserInventoryObject(BlockedId, "worn-out");
        m_module.Block(BlockedId);

        Assert.Null(RezFromUserInventory(BlockedId, itemId));
        Assert.Null(m_scene.GetSceneObjectGroup("worn-out"));
    }

    [Fact]
    public void ANewPrimByABlockedOwnerIsRefused()
    {
        Setup();
        m_module.Block(BlockedId);

        Assert.False(AddNewPrim(BlockedId));
    }

    [Fact]
    public void ADuplicateByABlockedOwnerIsRefused()
    {
        // Duplication does not ask CanRezObject; the module answers CanDuplicateObject too.
        Setup();
        SceneHelpers.AddScenePresence(m_scene, BlockedId);
        m_module.Block(BlockedId);

        Assert.Null(Duplicate(BlockedId));
    }

    [Fact]
    public void EveryCallerOfCanRezObjectIsRefusedForABlockedOwner()
    {
        // YEngine's llRezObjectWithParams, the object-add and upload capabilities, detach to ground,
        // NPC creation and the JsonStore rez all ask Permissions.CanRezObject directly.
        Setup();
        m_module.Block(BlockedId);

        Assert.False(m_scene.Permissions.CanRezObject(1, BlockedId, RezPos));
        Assert.True(m_scene.Permissions.CanRezObject(1, OtherId, RezPos));
    }

    [Fact]
    public void AnotherOwnerStillRezzesByEveryPathWhileSomeoneIsBlocked()
    {
        Setup();
        SceneHelpers.AddScenePresence(m_scene, OtherId);
        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(OtherId, "child");
        (SceneObjectPart host2, TaskInventoryItem item2) = AddRezzer(OtherId, "child2");
        UUID invItem = AddUserInventoryObject(OtherId, "from-inventory");
        m_module.Block(BlockedId);

        Assert.NotNull(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));
        Assert.NotNull(m_scene.RezObject(host2, item2, OtherId, UUID.Zero, RezPos, null, Vector3.Zero, 0, false, false, false));
        Assert.NotNull(RezFromUserInventory(OtherId, invItem));
        Assert.True(AddNewPrim(OtherId));
        Assert.NotNull(Duplicate(OtherId));
    }

    [Fact]
    public void AnUnblockedOwnerRezzesAgain()
    {
        Setup();
        SceneHelpers.AddScenePresence(m_scene, BlockedId);
        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(BlockedId, "child");
        m_module.Block(BlockedId);
        Assert.Null(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));

        Assert.True(m_module.Unblock(BlockedId));

        Assert.False(m_module.IsBlocked(BlockedId));
        Assert.NotNull(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));
        Assert.True(AddNewPrim(BlockedId));
        Assert.NotNull(Duplicate(BlockedId));
    }

    [Fact]
    public void ARegionThatBlocksNoOneRegistersNoHandlerAndRezzesAsBefore()
    {
        Setup();
        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(BlockedId, "child");
        UUID invItem = AddUserInventoryObject(BlockedId, "from-inventory");

        Assert.Equal(0, HandlerCount("OnRezObject"));
        Assert.Equal(0, HandlerCount("OnDuplicateObject"));
        Assert.Equal(0, HandlerCount("OnObjectEntry"));

        Assert.NotNull(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));
        Assert.NotNull(RezFromUserInventory(BlockedId, invItem));
        Assert.True(AddNewPrim(BlockedId));
        // With no OnDuplicateObject handler a duplicate needs no presence, as in a region without the module.
        Assert.NotNull(Duplicate(BlockedId));
    }

    [Fact]
    public void HandlersAreRegisteredOnlyWhileSomeoneIsBlocked()
    {
        Setup();
        m_module.Block(BlockedId);
        m_module.Block(OtherId);
        Assert.Equal(1, HandlerCount("OnRezObject"));
        Assert.Equal(1, HandlerCount("OnDuplicateObject"));
        Assert.Equal(1, HandlerCount("OnObjectEntry"));

        m_module.Unblock(BlockedId);
        Assert.Equal(1, HandlerCount("OnRezObject"));

        m_module.Unblock(OtherId);
        Assert.Equal(0, HandlerCount("OnRezObject"));
        Assert.Equal(0, HandlerCount("OnDuplicateObject"));
        Assert.Equal(0, HandlerCount("OnObjectEntry"));
    }

    [Fact]
    public void AnEstateBannedOwnerIsNotBlockedByDefault()
    {
        Setup();
        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(BlockedId, "child");
        m_scene.RegionInfo.EstateSettings.AddBan(new EstateBan { BannedUserID = BlockedId });

        Assert.False(m_module.IsBlocked(BlockedId));
        Assert.Equal(0, HandlerCount("OnRezObject"));
        Assert.NotNull(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));
    }

    [Fact]
    public void AnEstateBannedOwnerIsBlockedWhenTheRegionOptsIn()
    {
        Setup(blockEstateBanned: true);
        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(BlockedId, "child");
        (SceneObjectPart host2, TaskInventoryItem item2) = AddRezzer(OtherId, "child2");
        m_scene.RegionInfo.EstateSettings.AddBan(new EstateBan { BannedUserID = BlockedId });

        Assert.True(m_module.IsBlocked(BlockedId));
        Assert.Empty(m_module.GetBlockedOwners());
        Assert.Null(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));
        Assert.NotNull(m_scene.RezObject(host2, item2, RezPos, null, Vector3.Zero, 0, false));
    }

    [Fact]
    public void WithTheEstateBanOptionOnABanAddedAfterStartupTakesEffectAtOnce()
    {
        // With the option on, the handlers are registered when the region is added, before any ban,
        // and each check reads the estate's ban list as it is at that moment.
        Setup(blockEstateBanned: true);
        Assert.Equal(1, HandlerCount("OnRezObject"));
        Assert.Equal(1, HandlerCount("OnDuplicateObject"));
        Assert.Equal(1, HandlerCount("OnObjectEntry"));

        (SceneObjectPart host, TaskInventoryItem item) = AddRezzer(BlockedId, "child");
        Assert.True(m_scene.Permissions.CanRezObject(1, BlockedId, RezPos));

        m_scene.RegionInfo.EstateSettings.AddBan(new EstateBan { BannedUserID = BlockedId });
        Assert.False(m_scene.Permissions.CanRezObject(1, BlockedId, RezPos));
        Assert.Null(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));

        m_scene.RegionInfo.EstateSettings.RemoveBan(BlockedId);
        Assert.True(m_scene.Permissions.CanRezObject(1, BlockedId, RezPos));
        Assert.NotNull(m_scene.RezObject(host, item, RezPos, null, Vector3.Zero, 0, false));

        // Emptying the console list does not take the handlers away while the option is on.
        m_module.Block(OtherId);
        m_module.Unblock(OtherId);
        Assert.Equal(1, HandlerCount("OnRezObject"));
    }

    [Fact]
    public void TheListStartsEmptyAndHoldsWhatTheOperatorAdds()
    {
        Setup();
        Assert.Empty(m_module.GetBlockedOwners());
        Assert.False(m_module.IsBlocked(BlockedId));

        Assert.False(m_module.Block(UUID.Zero));
        Assert.True(m_module.Block(BlockedId));
        Assert.False(m_module.Block(BlockedId));
        Assert.Equal(new[] { BlockedId }, m_module.GetBlockedOwners());
        Assert.False(m_module.Unblock(OtherId));
    }

    [Fact]
    public void OtherCodeFindsTheHookThroughTheScene()
    {
        Setup();
        IBlockedOwnerModule hook = m_scene.RequestModuleInterface<IBlockedOwnerModule>();
        Assert.Same(m_module, hook);

        hook.Block(BlockedId);
        Assert.False(m_scene.Permissions.CanRezObject(1, BlockedId, RezPos));
    }
}
