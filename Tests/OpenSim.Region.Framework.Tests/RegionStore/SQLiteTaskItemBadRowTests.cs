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
using System.Data.SQLite;
using System.Linq;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The SQLite region store loads a prim's task inventory when one of its primitems rows cannot be read: here a
/// negative creation date, which a TaskInventoryItem (uint) cannot hold. That row used to throw out of LoadItems, so
/// the prim loaded with no items at all, including the items read before it. Now only that row is left out.
///
/// SQLite runs against a throwaway in-memory database per test (shared cache, kept alive by an anchor connection,
/// so that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SQLiteTaskItemBadRowTests : OpenSimTestCase
{
    private readonly List<IDisposable> m_disposables = new();
    private readonly List<SQLiteSimulationData> m_stores = new();

    static SQLiteTaskItemBadRowTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public override void Dispose()
    {
        foreach (SQLiteSimulationData store in m_stores)
        {
            try { store.Dispose(); } catch { /* best effort */ }
        }
        m_stores.Clear();
        for (int i = m_disposables.Count - 1; i >= 0; i--)
        {
            try { m_disposables[i].Dispose(); } catch { /* best effort */ }
        }
        m_disposables.Clear();
        base.Dispose();
    }

    private SQLiteConnection m_anchor;

    private string NewMemoryDatabase()
    {
        string conn = "FullUri=file:taskbadrow_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        m_anchor = new SQLiteConnection(conn);
        m_anchor.Open();
        m_disposables.Add(m_anchor);
        return conn;
    }

    private SQLiteSimulationData NewStore(string conn)
    {
        SQLiteSimulationData store = new SQLiteSimulationData(conn);
        m_stores.Add(store);
        return store;
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

    [Fact]
    public void ItemRowThatCannotBeBuilt_IsLeftOut_AndThePrimKeepsItsOtherItems()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();

        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(2, UUID.Random());
        SceneObjectPart root = sog.RootPart;
        SceneObjectPart child = sog.Parts.First(p => p != root);
        TaskInventoryItem first = NewItem(root, "first");
        TaskInventoryItem bad = NewItem(root, "bad");
        TaskInventoryItem last = NewItem(root, "last");
        TaskInventoryItem childItem = NewItem(child, "child");

        SQLiteSimulationData store = NewStore(conn);
        store.StoreObject(sog, regionID);
        store.StorePrimInventory(root.UUID, new List<TaskInventoryItem> { first, bad, last });
        store.StorePrimInventory(child.UUID, new List<TaskInventoryItem> { childItem });

        using (SQLiteCommand cmd = new SQLiteCommand("UPDATE primitems SET creationDate = -1 WHERE itemID = @id", m_anchor))
        {
            cmd.Parameters.AddWithValue("@id", bad.ItemID.ToString());
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        // A new store instance reads the database afresh, as after a region restart.
        SceneObjectGroup loaded = Assert.Single(NewStore(conn).LoadObjects(regionID));
        Assert.Equal(2, loaded.PrimCount);

        SceneObjectPart loadedRoot = loaded.GetPart(root.UUID);
        Assert.Null(loadedRoot.Inventory.GetInventoryItem(bad.ItemID));
        Assert.Equal("first", loadedRoot.Inventory.GetInventoryItem(first.ItemID)?.Name);
        Assert.Equal("last", loadedRoot.Inventory.GetInventoryItem(last.ItemID)?.Name);
        Assert.Equal(2, loadedRoot.Inventory.Count);

        SceneObjectPart loadedChild = loaded.GetPart(child.UUID);
        Assert.Equal("child", loadedChild.Inventory.GetInventoryItem(childItem.ItemID)?.Name);
        Assert.Equal(1, loadedChild.Inventory.Count);
    }
}
