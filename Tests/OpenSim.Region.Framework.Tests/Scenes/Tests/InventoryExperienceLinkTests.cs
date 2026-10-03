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

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Server.Handlers.Inventory;
using OpenSim.Services.Connectors;
using OpenSim.Tests.Common;
using Xunit;

using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A script compiled into an Experience keeps that Experience (InventoryItemBase.ExperienceID) while it is in a
/// user's inventory: in the inventory store, through the inventory service and its wire format, and on the way
/// out of a prim, into a prim, to another resident and into a copy.
///
/// SQLite runs against a throwaway in-memory database per test (shared-cache, kept alive by an anchor connection).
/// No files are written. SQLiteGenericTableHandler keeps one static connection per row type for the whole process,
/// so the store test reads and writes through the database the first store in the process opened; this project
/// runs its test classes one at a time (AssemblyInfo.cs).
/// </summary>
public class InventoryExperienceLinkTests : OpenSimTestCase
{
    private static readonly UUID s_experience = new UUID("3a8e6d2f-91c4-4b57-8e0d-c62f1b7a4d39");

    private readonly List<IDisposable> m_disposables = new();

    static InventoryExperienceLinkTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public override void Dispose()
    {
        for (int i = m_disposables.Count - 1; i >= 0; i--)
        {
            try { m_disposables[i].Dispose(); } catch { /* best effort */ }
        }
        m_disposables.Clear();
        base.Dispose();
    }

    private string NewMemoryDatabase()
    {
        string conn = "FullUri=file:invexp_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_disposables.Add(anchor);
        return conn;
    }

    private static XInventoryItem NewStoreItem(UUID experience)
    {
        return new XInventoryItem
        {
            inventoryID = UUID.Random(),
            assetID = UUID.Random(),
            assetType = (int)AssetType.LSLText,
            invType = (int)InventoryType.LSL,
            inventoryName = "script",
            inventoryDescription = string.Empty,
            creatorID = UUID.Random().ToString(),
            avatarID = UUID.Random(),
            parentFolderID = UUID.Random(),
            groupID = UUID.Zero,
            experienceID = experience
        };
    }

    // ---- inventory store (SQLite) ---------------------------------------------------------------------------

    [Fact]
    public void SQLiteInventoryStoreKeepsAnItemsExperience()
    {
        string conn = NewMemoryDatabase();
        SQLiteXInventoryData store = new SQLiteXInventoryData(conn, string.Empty);

        XInventoryItem with = NewStoreItem(s_experience);
        XInventoryItem without = NewStoreItem(UUID.Zero);
        Assert.True(store.StoreItem(with));
        Assert.True(store.StoreItem(without));

        Assert.Equal(s_experience, store.GetItems(new[] { "inventoryID" }, new[] { with.inventoryID.ToString() })[0].experienceID);
        Assert.Equal(UUID.Zero, store.GetItems(new[] { "inventoryID" }, new[] { without.inventoryID.ToString() })[0].experienceID);
    }

    [Fact]
    public void SQLiteInventoryRowWrittenBeforeTheColumnExistedReadsZeroAfterMigration()
    {
        // The store's table handler keeps one connection per process, so the migration is driven here on a
        // connection of the test's own, with the store's migration file.
        string conn = NewMemoryDatabase();
        using SQLiteConnection c = new SQLiteConnection(conn);
        c.Open();
        new Migration(c, typeof(SQLiteXInventoryData).Assembly, "XInventoryStore").Update();

        // Put the database back to the schema before this change (XInventoryStore 2, no experienceID column)
        // and write an item row the way the older code did.
        UUID itemID = UUID.Random();
        using (SQLiteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE inventoryitems DROP COLUMN experienceID";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "UPDATE migrations SET version = 2 WHERE name = 'XInventoryStore'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
            cmd.CommandText =
                "INSERT INTO inventoryitems (inventoryID, assetID, assetType, inventoryName, inventoryDescription, " +
                "invType, creatorID, avatarID, parentFolderID, groupID) VALUES " +
                "(:itemID, :assetID, 10, 'old script', '', 10, :owner, :owner, :folder, :zero)";
            cmd.Parameters.AddWithValue(":itemID", itemID.ToString());
            cmd.Parameters.AddWithValue(":assetID", UUID.Random().ToString());
            cmd.Parameters.AddWithValue(":owner", UUID.Random().ToString());
            cmd.Parameters.AddWithValue(":folder", UUID.Random().ToString());
            cmd.Parameters.AddWithValue(":zero", UUID.Zero.ToString());
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        // The store's next start runs the migration; the old row reads as "no Experience".
        new Migration(c, typeof(SQLiteXInventoryData).Assembly, "XInventoryStore").Update();

        using (SQLiteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM migrations WHERE name = 'XInventoryStore'";
            Assert.Equal(3L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "SELECT inventoryName, experienceID FROM inventoryitems WHERE inventoryID = :itemID";
            cmd.Parameters.AddWithValue(":itemID", itemID.ToString());
            using SQLiteDataReader r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("old script", r.GetString(0));
            Assert.Equal(UUID.Zero.ToString(), r.GetString(1));
        }
    }

    // ---- the inventory service's wire format ----------------------------------------------------------------

    private static Dictionary<string, object> ServerEncodes(InventoryItemBase item)
    {
        object handler = RuntimeHelpers.GetUninitializedObject(typeof(XInventoryConnectorPostHandler));
        MethodInfo encode = typeof(XInventoryConnectorPostHandler).GetMethod("EncodeItem", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Dictionary<string, object>)encode.Invoke(handler, new object[] { item });
    }

    private static InventoryItemBase ServerReads(Dictionary<string, object> data)
    {
        object handler = RuntimeHelpers.GetUninitializedObject(typeof(XInventoryConnectorPostHandler));
        MethodInfo build = typeof(XInventoryConnectorPostHandler).GetMethod("BuildItem", BindingFlags.NonPublic | BindingFlags.Instance);
        return (InventoryItemBase)build.Invoke(handler, new object[] { data });
    }

    private static InventoryItemBase ClientReads(Dictionary<string, object> data)
    {
        MethodInfo build = typeof(XInventoryServicesConnector).GetMethod("BuildItem", BindingFlags.NonPublic | BindingFlags.Static);
        return (InventoryItemBase)build.Invoke(null, new object[] { data });
    }

    private static InventoryItemBase NewScriptItem(UUID owner, UUID folder, UUID experience)
    {
        return new InventoryItemBase(UUID.Random(), owner)
        {
            AssetID = UUID.Random(),
            AssetType = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            Name = "script",
            Description = string.Empty,
            CreatorId = owner.ToString(),
            CreatorData = string.Empty,
            Folder = folder,
            BasePermissions = (uint)PermissionMask.All,
            CurrentPermissions = (uint)PermissionMask.All,
            NextPermissions = (uint)PermissionMask.All,
            EveryOnePermissions = 0,
            GroupPermissions = 0,
            ExperienceID = experience
        };
    }

    [Fact]
    public void TheExperienceCrossesTheInventoryServiceWire()
    {
        InventoryItemBase item = NewScriptItem(UUID.Random(), UUID.Random(), s_experience);

        Dictionary<string, object> fromServer = ServerEncodes(item);
        Assert.Equal(s_experience, ClientReads(fromServer).ExperienceID);

        // The client sends the same keys the server encodes, as strings.
        Assert.Equal(s_experience, ServerReads(fromServer).ExperienceID);
    }

    [Fact]
    public void AMessageWithoutTheExperienceReadsZero()
    {
        // What a server or region from before this change sends.
        Dictionary<string, object> old = ServerEncodes(NewScriptItem(UUID.Random(), UUID.Random(), s_experience));
        old.Remove("ExperienceID");

        Assert.Equal(UUID.Zero, ClientReads(old).ExperienceID);
        Assert.Equal(UUID.Zero, ServerReads(old).ExperienceID);
    }

    // ---- the inventory service and the region ---------------------------------------------------------------

    private static UUID ScriptFolder(Scene scene, UUID user)
        => scene.InventoryService.GetFolderForType(user, FolderType.LSLText).ID;

    private static InventoryItemBase AddToInventory(Scene scene, UUID user, UUID experience)
    {
        InventoryItemBase item = NewScriptItem(user, ScriptFolder(scene, user), experience);
        Assert.True(scene.InventoryService.AddItem(item));
        return item;
    }

    [Fact]
    public void TheInventoryServiceKeepsAnItemsExperience()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID user = UserAccountHelpers.CreateUserWithInventory(scene).PrincipalID;

        InventoryItemBase item = AddToInventory(scene, user, s_experience);

        Assert.Equal(s_experience, scene.InventoryService.GetItem(user, item.ID).ExperienceID);
    }

    private static (SceneObjectPart part, TaskInventoryItem script) PrimWithScript(Scene scene, UUID owner, UUID experience)
    {
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, owner);
        scene.AddNewSceneObject(sog, false);
        TaskInventoryItem script = new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            Name = "script",
            Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            OwnerID = owner,
            CreatorID = owner,
            BasePermissions = (uint)PermissionMask.All,
            CurrentPermissions = (uint)PermissionMask.All,
            NextPermissions = (uint)PermissionMask.All,
            ExperienceID = experience
        };
        sog.RootPart.Inventory.AddInventoryItem(script, false);
        return (sog.RootPart, script);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AScriptTakenFromAPrimIntoInventoryKeepsItsExperience(bool withExperience)
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID user = UserAccountHelpers.CreateUserWithInventory(scene).PrincipalID;
        UUID experience = withExperience ? s_experience : UUID.Zero;
        (SceneObjectPart part, TaskInventoryItem script) = PrimWithScript(scene, user, experience);

        InventoryItemBase taken = scene.MoveTaskInventoryItem(user, ScriptFolder(scene, user), part, script.ItemID, out _);

        Assert.NotNull(taken);
        Assert.Equal(experience, scene.InventoryService.GetItem(user, taken.ID).ExperienceID);
    }

    [Fact]
    public void AScriptPutIntoAPrimFromInventoryKeepsItsExperience()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID user = UserAccountHelpers.CreateUserWithInventory(scene).PrincipalID;
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, user);
        scene.AddNewSceneObject(sog, false);
        InventoryItemBase item = AddToInventory(scene, user, s_experience);

        Assert.True(sog.AddInventoryItem(user, sog.RootPart.LocalId, item, UUID.Zero));

        Assert.Equal(s_experience, sog.RootPart.Inventory.GetInventoryItem(item.ID).ExperienceID);
    }

    [Fact]
    public void AScriptGivenToAnotherResidentKeepsItsExperience()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID giver = UserAccountHelpers.CreateUserWithInventory(scene, TestHelpers.ParseTail(0x1)).PrincipalID;
        UUID receiver = UserAccountHelpers.CreateUserWithInventory(scene, TestHelpers.ParseTail(0x2)).PrincipalID;
        InventoryItemBase item = AddToInventory(scene, giver, s_experience);

        InventoryItemBase given = scene.GiveInventoryItem(receiver, giver, item.ID, out _);

        Assert.NotNull(given);
        Assert.Equal(s_experience, scene.InventoryService.GetItem(receiver, given.ID).ExperienceID);
    }

    [Fact]
    public void ACopiedScriptKeepsItsExperience()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID user = UserAccountHelpers.CreateUserWithInventory(scene).PrincipalID;
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, user);
        InventoryItemBase item = AddToInventory(scene, user, s_experience);
        UUID folder = ScriptFolder(scene, user);

        scene.CopyInventoryItem(sp.ControllingClient, 0, user, item.ID, folder, "script copy");

        InventoryItemBase copy = null;
        foreach (InventoryItemBase i in scene.InventoryService.GetFolderContent(user, folder).Items)
            if (i.Name == "script copy")
                copy = i;
        Assert.NotNull(copy);
        Assert.NotEqual(item.ID, copy.ID);
        Assert.Equal(s_experience, copy.ExperienceID);
    }
}
