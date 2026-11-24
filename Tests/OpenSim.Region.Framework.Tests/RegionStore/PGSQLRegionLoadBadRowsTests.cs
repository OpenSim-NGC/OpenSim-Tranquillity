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
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Tests;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The PostgreSQL region store loads a region in which some rows cannot be read: a prim row with a NULL in a column
/// the prim needs, a task-inventory row that cannot be built, a dynamic-attribute document whose top value is not a
/// map. Each used to throw out of LoadObjects, so the region loaded no objects at all. Now only that row (or that
/// prim's dynamic attributes) is left out, with a warning, and the rest of the region loads.
///
/// Also: an item stored under the original id of a root that the load forces to its group id (no row of the
/// linkset has the group's id) is loaded onto that root, and storing that root's inventory afterwards works.
///
/// Opt-in (OPENSIM_TEST_PGSQL): each test gets a new database, dropped when it ends. No process-wide state.
/// </summary>
public class PGSQLRegionLoadBadRowsTests : IDisposable
{
    private readonly List<Action> m_cleanup = new();

    public void Dispose()
    {
        for (int i = m_cleanup.Count - 1; i >= 0; i--)
        {
            try { m_cleanup[i](); } catch { /* best effort */ }
        }
        m_cleanup.Clear();
    }

    [PGSQLServerFact]
    public void PrimRowWithNullRequiredColumn_IsLeftOut_RestOfRegionLoads()
    {
        string conn = NewPGSQLDatabase();
        UUID regionID = UUID.Random();
        PGSQLSimulationData store = new PGSQLSimulationData(conn);

        SceneObjectGroup good = Linkset("good", 3);
        SceneObjectGroup nullName = Linkset("nullname", 1);
        SceneObjectGroup nullDate = Linkset("nulldate", 1);
        SceneObjectGroup badChild = Linkset("badchild", 3);
        SceneObjectGroup badRoot = Linkset("badroot", 2);
        foreach (SceneObjectGroup sog in new[] { good, nullName, nullDate, badChild, badRoot })
            store.StoreObject(sog, regionID);

        SceneObjectPart lostChild = badChild.Parts[1];
        Exec(conn, "UPDATE prims SET \"Name\" = NULL WHERE \"UUID\" = @id", nullName.UUID);
        Exec(conn, "UPDATE prims SET \"CreationDate\" = NULL WHERE \"UUID\" = @id", nullDate.UUID);
        Exec(conn, "UPDATE prims SET \"Text\" = NULL WHERE \"UUID\" = @id", lostChild.UUID);
        Exec(conn, "UPDATE prims SET \"Name\" = NULL WHERE \"UUID\" = @id", badRoot.UUID);

        Dictionary<UUID, SceneObjectGroup> loaded = Load(conn, regionID);

        Assert.Equal(new HashSet<UUID> { good.UUID, badChild.UUID }, new HashSet<UUID>(loaded.Keys));
        Assert.Equal(3, loaded[good.UUID].PrimCount);
        foreach (SceneObjectPart part in good.Parts)
            Assert.NotNull(loaded[good.UUID].GetPart(part.UUID));

        // The child with the NULL is left out; its root and the other child load.
        SceneObjectGroup partial = loaded[badChild.UUID];
        Assert.Equal(2, partial.PrimCount);
        Assert.Null(partial.GetPart(lostChild.UUID));
        Assert.NotNull(partial.GetPart(badChild.Parts[2].UUID));

        // A root that cannot be read leaves out its linkset: no child is made the root in its place.
        Assert.DoesNotContain(loaded.Values, g => g.GetPart(badRoot.Parts[1].UUID) != null);
    }

    [PGSQLServerFact]
    public void ItemRowThatCannotBeBuilt_IsLeftOut_PrimAndOtherItemsLoad()
    {
        string conn = NewPGSQLDatabase();
        UUID regionID = UUID.Random();
        PGSQLSimulationData store = new PGSQLSimulationData(conn);

        SceneObjectGroup holder = Linkset("holder", 2);
        SceneObjectGroup other = Linkset("other", 1);
        store.StoreObject(holder, regionID);
        store.StoreObject(other, regionID);

        TaskInventoryItem kept = Item(holder.RootPart, "kept");
        TaskInventoryItem nullDescription = Item(holder.RootPart, "nulldescription");
        TaskInventoryItem badFlags = Item(holder.RootPart, "badflags");
        TaskInventoryItem otherItem = Item(other.RootPart, "otheritem");
        store.StorePrimInventory(holder.RootPart.UUID, new List<TaskInventoryItem> { kept, nullDescription, badFlags });
        store.StorePrimInventory(other.RootPart.UUID, new List<TaskInventoryItem> { otherItem });

        Exec(conn, "UPDATE primitems SET \"description\" = NULL WHERE \"itemID\" = @id", nullDescription.ItemID);
        // Not a uint: BuildItem reads flags with Convert.ToUInt32.
        Exec(conn, "UPDATE primitems SET \"flags\" = -1 WHERE \"itemID\" = @id", badFlags.ItemID);

        Dictionary<UUID, SceneObjectGroup> loaded = Load(conn, regionID);

        Assert.Equal(new HashSet<UUID> { holder.UUID, other.UUID }, new HashSet<UUID>(loaded.Keys));
        Assert.Equal(2, loaded[holder.UUID].PrimCount);
        SceneObjectPart root = loaded[holder.UUID].RootPart;
        Assert.NotNull(root.Inventory.GetInventoryItem(kept.ItemID));
        Assert.Null(root.Inventory.GetInventoryItem(nullDescription.ItemID));
        Assert.Null(root.Inventory.GetInventoryItem(badFlags.ItemID));
        Assert.Equal(1, root.Inventory.Count);
        Assert.NotNull(loaded[other.UUID].RootPart.Inventory.GetInventoryItem(otherItem.ItemID));
    }

    [PGSQLServerFact]
    public void DynamicAttributesThatAreNotAMap_AreLeftEmpty_PrimLoads()
    {
        string conn = NewPGSQLDatabase();
        UUID regionID = UUID.Random();
        PGSQLSimulationData store = new PGSQLSimulationData(conn);

        SceneObjectGroup bad = Linkset("baddynattrs", 2);
        SceneObjectGroup good = Linkset("gooddynattrs", 1);
        good.RootPart.DynAttrs = new DAMap();
        OpenMetaverse.StructuredData.OSDMap ns = new OpenMetaverse.StructuredData.OSDMap();
        ns["key"] = OpenMetaverse.StructuredData.OSD.FromString("value");
        good.RootPart.DynAttrs.SetStore("TestNamespace", "store", ns);
        store.StoreObject(bad, regionID);
        store.StoreObject(good, regionID);

        Exec(conn, "UPDATE prims SET \"DynAttrs\" = '<llsd><integer>5</integer></llsd>' WHERE \"UUID\" = @id", bad.UUID);

        Dictionary<UUID, SceneObjectGroup> loaded = Load(conn, regionID);

        Assert.Equal(new HashSet<UUID> { bad.UUID, good.UUID }, new HashSet<UUID>(loaded.Keys));
        Assert.Equal(2, loaded[bad.UUID].PrimCount);
        DAMap empty = loaded[bad.UUID].RootPart.DynAttrs;
        Assert.True(empty == null || empty.CountNamespaces == 0);

        DAMap kept = loaded[good.UUID].RootPart.DynAttrs;
        Assert.NotNull(kept);
        Assert.Equal("value", kept.GetStore("TestNamespace", "store")["key"].AsString());
    }

    [PGSQLServerFact]
    public void ItemOnRootForcedToGroupId_Loads_AndItsInventoryStoresAgain()
    {
        string conn = NewPGSQLDatabase();
        UUID regionID = UUID.Random();
        PGSQLSimulationData store = new PGSQLSimulationData(conn);

        SceneObjectGroup orphan = Linkset("orphan", 2);
        SceneObjectGroup good = Linkset("good", 1);
        store.StoreObject(orphan, regionID);
        store.StoreObject(good, regionID);
        UUID originalRoot = orphan.RootPart.UUID;
        TaskInventoryItem item = Item(orphan.RootPart, "onroot");
        TaskInventoryItem goodItem = Item(good.RootPart, "ongood");
        store.StorePrimInventory(originalRoot, new List<TaskInventoryItem> { item });
        store.StorePrimInventory(good.RootPart.UUID, new List<TaskInventoryItem> { goodItem });

        // No row of the linkset has the group's id any more, so the load gives the root the group id.
        UUID groupID = UUID.Random();
        Exec(conn, "UPDATE prims SET \"SceneGroupID\" = @id WHERE \"SceneGroupID\" = '" + originalRoot + "'", groupID);

        Dictionary<UUID, SceneObjectGroup> loaded = Load(conn, regionID);

        Assert.Equal(new HashSet<UUID> { groupID, good.UUID }, new HashSet<UUID>(loaded.Keys));
        SceneObjectPart root = loaded[groupID].RootPart;
        Assert.Equal(groupID, root.UUID);
        Assert.Equal(2, loaded[groupID].PrimCount);
        TaskInventoryItem loadedItem = root.Inventory.GetInventoryItem(item.ItemID);
        Assert.NotNull(loadedItem);
        Assert.Equal(groupID, loadedItem.ParentID);
        Assert.Equal(groupID, loadedItem.ParentPartID);
        Assert.NotNull(loaded[good.UUID].RootPart.Inventory.GetInventoryItem(goodItem.ItemID));

        // Storing the root's inventory moves the item's row to the loaded id instead of failing on its item id.
        store.StorePrimInventory(root.UUID, root.Inventory.GetInventoryItems());
        Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM primitems WHERE \"itemID\" = @id", item.ItemID));
        Assert.Equal(1L, Scalar(conn,
            "SELECT count(*) FROM primitems WHERE \"itemID\" = @id AND \"primID\" = '" + groupID + "'", item.ItemID));
    }

    /// <summary>A linkset with random ids, so that several can share one region.</summary>
    private static SceneObjectGroup Linkset(string name, int parts)
    {
        UUID owner = UUID.Random();
        SceneObjectGroup sog = new SceneObjectGroup(SceneHelpers.CreateSceneObjectPart(name + "1", UUID.Random(), owner));
        for (int i = 2; i <= parts; i++)
            sog.AddPart(SceneHelpers.CreateSceneObjectPart(name + i, UUID.Random(), owner));
        return sog;
    }

    private static TaskInventoryItem Item(SceneObjectPart part, string name)
    {
        return new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            ParentID = part.UUID,
            ParentPartID = part.UUID,
            OwnerID = part.OwnerID,
            CreatorID = part.OwnerID,
            Name = name,
            Description = name + " description",
            Type = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard
        };
    }

    private static Dictionary<UUID, SceneObjectGroup> Load(string conn, UUID regionID)
    {
        Dictionary<UUID, SceneObjectGroup> groups = new();
        foreach (SceneObjectGroup sog in new PGSQLSimulationData(conn).LoadObjects(regionID))
            groups.Add(sog.UUID, sog);
        return groups;
    }

    private static void Exec(string conn, string sql, UUID id)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", id.Guid);
        Assert.True(cmd.ExecuteNonQuery() > 0, sql);
    }

    private static long Scalar(string conn, string sql, UUID id)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", id.Guid);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "badrows_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Admin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        });
        return conn;
    }

    private static void Admin(string conn, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
