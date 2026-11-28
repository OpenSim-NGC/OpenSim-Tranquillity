/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using MySqlConnector;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Data.MySQL;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Tests;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The MySQL region store loads a region in which one stored row cannot be read as it is: a task-inventory row
/// whose flags are negative, a task-inventory row whose experience id or prim id is not a uuid, and a prim whose
/// dynamic attributes are not a map. Each of these used to throw out of LoadObjects, so the region loaded no objects at all.
/// Now the unreadable item is left out, the experience id is the zero id and the prim loads without its dynamic
/// attributes; every other prim and item loads.
///
/// Opt-in (OPENSIM_TEST_MYSQL): each test gets a new database, dropped when it ends. No process-wide state.
/// </summary>
public class MySQLRegionLoadBadRowsTests : IDisposable
{
    private const string DynNamespace = "OpenSim";
    private const string DynStore = "badrows";

    private readonly List<Action> m_cleanup = new();

    public void Dispose()
    {
        for (int i = m_cleanup.Count - 1; i >= 0; i--)
        {
            try { m_cleanup[i](); } catch { /* best effort */ }
        }
        m_cleanup.Clear();
    }

    /// <summary>A region with two linksets, items in a root and a child, and good dynamic attributes.</summary>
    private sealed class Seed
    {
        public UUID RegionID = UUID.Random();
        public SceneObjectGroup Linkset;
        public SceneObjectGroup Single;
        public List<TaskInventoryItem> RootItems;
        public TaskInventoryItem ChildItem;
        public TaskInventoryItem SingleItem;
    }

    private static Seed StoreSeed(string conn)
    {
        Seed seed = new Seed();
        MySQLSimulationData store = new MySQLSimulationData(conn);

        seed.Linkset = SceneHelpers.CreateSceneObject(3, UUID.Random(), "linkset", 0x10);
        seed.Single = SceneHelpers.CreateSceneObject(1, UUID.Random(), "single", 0x20);
        foreach (SceneObjectPart part in seed.Linkset.Parts.Concat(seed.Single.Parts))
        {
            part.DynAttrs = new DAMap();
            part.DynAttrs.SetStore(DynNamespace, DynStore, new OSDMap { ["part"] = OSD.FromUUID(part.UUID) });
        }

        SceneObjectPart root = seed.Linkset.RootPart;
        SceneObjectPart child = seed.Linkset.Parts.First(p => p != root);
        seed.RootItems = new List<TaskInventoryItem>
        {
            NewItem(root, "first"), NewItem(root, "second"), NewItem(root, "third")
        };
        seed.ChildItem = NewItem(child, "child");
        seed.SingleItem = NewItem(seed.Single.RootPart, "single");

        store.StoreObject(seed.Linkset, seed.RegionID);
        store.StoreObject(seed.Single, seed.RegionID);
        store.StorePrimInventory(root.UUID, seed.RootItems);
        store.StorePrimInventory(child.UUID, new List<TaskInventoryItem> { seed.ChildItem });
        store.StorePrimInventory(seed.Single.RootPart.UUID, new List<TaskInventoryItem> { seed.SingleItem });
        return seed;
    }

    private static TaskInventoryItem NewItem(SceneObjectPart part, string name)
    {
        return new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            ParentID = part.UUID,
            ParentPartID = part.UUID,
            OwnerID = part.OwnerID,
            LastOwnerID = part.OwnerID,
            CreatorID = part.OwnerID,
            Name = name,
            Description = name + " description",
            Type = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard
        };
    }

    /// <summary>Every prim of the seed loads, with its dynamic attributes, and each item named loads.</summary>
    private static void AssertSeedLoaded(Seed seed, List<SceneObjectGroup> loaded, UUID skipDynAttrsOf,
        params TaskInventoryItem[] items)
    {
        Assert.Equal(2, loaded.Count);
        SceneObjectGroup linkset = Assert.Single(loaded, g => g.UUID == seed.Linkset.UUID);
        SceneObjectGroup single = Assert.Single(loaded, g => g.UUID == seed.Single.UUID);
        Assert.Equal(3, linkset.PrimCount);
        Assert.Equal(1, single.PrimCount);

        foreach (SceneObjectPart part in linkset.Parts.Concat(single.Parts))
        {
            if (part.UUID == skipDynAttrsOf)
                continue;
            Assert.NotNull(part.DynAttrs);
            Assert.True(part.DynAttrs.TryGetStore(DynNamespace, DynStore, out OSDMap store), $"dynamic attributes of {part.UUID}");
            Assert.Equal(part.UUID, store["part"].AsUUID());
        }

        foreach (TaskInventoryItem item in items)
        {
            SceneObjectGroup group = loaded.Single(g => g.GetPart(item.ParentPartID) is not null);
            TaskInventoryItem got = group.GetPart(item.ParentPartID).Inventory.GetInventoryItem(item.ItemID);
            Assert.NotNull(got);
            Assert.Equal(item.Name, got.Name);
        }
    }

    [MySQLServerFact]
    public void ItemRowThatCannotBeBuilt_IsLeftOut_AndTheRegionLoads()
    {
        string conn = NewMySQLDatabase(oldGuids: true);
        Seed seed = StoreSeed(conn);
        TaskInventoryItem bad = seed.RootItems[1];

        // flags is a signed int column; the item's flags are a uint, so a negative value cannot be read.
        Execute(conn, "UPDATE primitems SET flags = -1 WHERE itemID = @id", bad.ItemID);

        List<SceneObjectGroup> loaded = new MySQLSimulationData(conn).LoadObjects(seed.RegionID);

        AssertSeedLoaded(seed, loaded, UUID.Zero,
            seed.RootItems[0], seed.RootItems[2], seed.ChildItem, seed.SingleItem);
        SceneObjectPart root = loaded.Single(g => g.UUID == seed.Linkset.UUID).RootPart;
        Assert.Null(root.Inventory.GetInventoryItem(bad.ItemID));
        Assert.Equal(2, root.Inventory.Count);
    }

    /// <summary>
    /// A connection that reads CHAR(36) as a Guid (Old Guids=false, the connector's default) throws on text that
    /// is not a uuid; the item loads with the zero id, as it already did on a connection that reads it as text.
    /// </summary>
    [MySQLServerFact]
    public void ExperienceIdThatIsNotAUuid_LoadsAsTheZeroId_WhenReadAsGuid()
    {
        ExperienceIdThatIsNotAUuid_LoadsAsTheZeroId(oldGuids: false);
    }

    [MySQLServerFact]
    public void ExperienceIdThatIsNotAUuid_LoadsAsTheZeroId_WhenReadAsText()
    {
        ExperienceIdThatIsNotAUuid_LoadsAsTheZeroId(oldGuids: true);
    }

    private void ExperienceIdThatIsNotAUuid_LoadsAsTheZeroId(bool oldGuids)
    {
        string conn = NewMySQLDatabase(oldGuids);
        Seed seed = StoreSeed(conn);
        TaskInventoryItem odd = seed.ChildItem;
        UUID experience = UUID.Random();
        Execute(conn, $"UPDATE primitems SET experienceID = '{experience}' WHERE itemID = @id", seed.SingleItem.ItemID);
        Execute(conn, "UPDATE primitems SET experienceID = 'not-a-uuid' WHERE itemID = @id", odd.ItemID);

        List<SceneObjectGroup> loaded = new MySQLSimulationData(conn).LoadObjects(seed.RegionID);

        AssertSeedLoaded(seed, loaded, UUID.Zero,
            seed.RootItems[0], seed.RootItems[1], seed.RootItems[2], seed.ChildItem, seed.SingleItem);
        SceneObjectGroup linkset = loaded.Single(g => g.UUID == seed.Linkset.UUID);
        Assert.Equal(UUID.Zero, linkset.GetPart(odd.ParentPartID).Inventory.GetInventoryItem(odd.ItemID).ExperienceID);
        SceneObjectGroup single = loaded.Single(g => g.UUID == seed.Single.UUID);
        Assert.Equal(experience, single.RootPart.Inventory.GetInventoryItem(seed.SingleItem.ItemID).ExperienceID);
    }

    [MySQLServerFact]
    public void DynamicAttributesThatAreNotAMap_AreLeftOut_AndThePrimLoads()
    {
        string conn = NewMySQLDatabase(oldGuids: true);
        Seed seed = StoreSeed(conn);
        SceneObjectPart child = seed.Linkset.Parts.First(p => p != seed.Linkset.RootPart);
        Execute(conn, "UPDATE prims SET DynAttrs = '<llsd><integer>1</integer></llsd>' WHERE UUID = @id", child.UUID);

        List<SceneObjectGroup> loaded = new MySQLSimulationData(conn).LoadObjects(seed.RegionID);

        AssertSeedLoaded(seed, loaded, child.UUID,
            seed.RootItems[0], seed.RootItems[1], seed.RootItems[2], seed.ChildItem, seed.SingleItem);
        SceneObjectPart loadedChild = loaded.Single(g => g.UUID == seed.Linkset.UUID).GetPart(child.UUID);
        Assert.True(loadedChild.DynAttrs is null || loadedChild.DynAttrs.CountNamespaces == 0);
    }

    /// <summary>
    /// LoadObjects first asks which prims have items, over the whole table. On a connection that reads CHAR(36) as
    /// a Guid (Old Guids=false), a row whose prim id is not a uuid threw there, and no region loaded any object.
    /// </summary>
    [MySQLServerFact]
    public void ItemRowWithAPrimIdThatIsNotAUuid_IsSkipped_AndTheRegionLoads()
    {
        string conn = NewMySQLDatabase(oldGuids: false);
        Seed seed = StoreSeed(conn);
        Execute(conn, "INSERT INTO primitems (itemID, primID) VALUES (@id, 'garbage')", UUID.Random());

        List<SceneObjectGroup> loaded = new MySQLSimulationData(conn).LoadObjects(seed.RegionID);

        AssertSeedLoaded(seed, loaded, UUID.Zero,
            seed.RootItems[0], seed.RootItems[1], seed.RootItems[2], seed.ChildItem, seed.SingleItem);
        Assert.Equal(3, loaded.Single(g => g.UUID == seed.Linkset.UUID).RootPart.Inventory.Count);
    }

    /// <summary>One bad row of each kind at once, in one linkset: everything else loads.</summary>
    [MySQLServerFact]
    public void OneBadRowOfEachKind_EverythingElseLoads()
    {
        string conn = NewMySQLDatabase(oldGuids: false);
        Seed seed = StoreSeed(conn);
        SceneObjectPart root = seed.Linkset.RootPart;
        Execute(conn, "UPDATE primitems SET flags = -1 WHERE itemID = @id", seed.RootItems[0].ItemID);
        Execute(conn, "UPDATE primitems SET experienceID = 'nope' WHERE itemID = @id", seed.RootItems[2].ItemID);
        Execute(conn, "UPDATE prims SET DynAttrs = '<llsd><integer>1</integer></llsd>' WHERE UUID = @id", root.UUID);

        List<SceneObjectGroup> loaded = new MySQLSimulationData(conn).LoadObjects(seed.RegionID);

        AssertSeedLoaded(seed, loaded, root.UUID,
            seed.RootItems[1], seed.RootItems[2], seed.ChildItem, seed.SingleItem);
        SceneObjectPart loadedRoot = loaded.Single(g => g.UUID == seed.Linkset.UUID).RootPart;
        Assert.Equal(2, loadedRoot.Inventory.Count);
        Assert.Null(loadedRoot.Inventory.GetInventoryItem(seed.RootItems[0].ItemID));
        Assert.Equal(UUID.Zero, loadedRoot.Inventory.GetInventoryItem(seed.RootItems[2].ItemID).ExperienceID);
    }

    private static void Execute(string conn, string sql, UUID id)
    {
        using MySqlConnection c = new MySqlConnection(conn);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase(bool oldGuids)
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "badrows_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name, OldGuids = oldGuids }.ConnectionString;

        Admin(admin, $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Admin(admin, $"DROP DATABASE IF EXISTS `{name}`");
        });
        return conn;
    }

    private static void Admin(string conn, string sql)
    {
        using MySqlConnection c = new MySqlConnection(conn);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
