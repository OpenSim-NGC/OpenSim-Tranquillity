/*
 * Copyright (c) Legion Builds
 * All rights reserved.
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
using System.Linq;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The SQLite region store reads back a prim's task inventory when an item's description is NULL in primitems.
/// The column is nullable (RegionStore.migrations creates it as "description varchar(255)"), and StorePrimInventory
/// writes a null TaskInventoryItem.Description as NULL.
///
/// SQLite runs against a throwaway in-memory database per test (shared cache, kept alive by an anchor connection,
/// so that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SQLiteTaskItemDescriptionTests : OpenSimTestCase
{
    private readonly List<IDisposable> m_disposables = new();
    private readonly List<SQLiteSimulationData> m_stores = new();

    static SQLiteTaskItemDescriptionTests()
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

    private string NewMemoryDatabase()
    {
        string conn = "FullUri=file:taskdesc_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_disposables.Add(anchor);
        return conn;
    }

    private SQLiteSimulationData NewStore(string conn)
    {
        SQLiteSimulationData store = new SQLiteSimulationData(conn);
        m_stores.Add(store);
        return store;
    }

    private static TaskInventoryItem NewItem(SceneObjectPart part, string name, string description)
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
            Description = description,
            Type = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard
        };
    }

    [Fact]
    public void TaskItemWithNullDescription_ReloadsWithTheRestOfTheInventory()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();

        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random());
        SceneObjectPart part = sog.RootPart;
        TaskInventoryItem nullDescription = NewItem(part, "no description", null);
        TaskInventoryItem withDescription = NewItem(part, "with description", "a description");

        SQLiteSimulationData first = NewStore(conn);
        first.StoreObject(sog, regionID);
        first.StorePrimInventory(part.UUID, new List<TaskInventoryItem> { nullDescription, withDescription });

        // A new store instance reads the database afresh, as after a region restart.
        SQLiteSimulationData second = NewStore(conn);
        SceneObjectGroup loaded = second.LoadObjects(regionID).Single(g => g.GetPart(part.UUID) is not null);
        SceneObjectPart loadedPart = loaded.GetPart(part.UUID);

        TaskInventoryItem reloadedNull = loadedPart.Inventory.GetInventoryItem(nullDescription.ItemID);
        Assert.NotNull(reloadedNull);
        Assert.Equal("no description", reloadedNull.Name);
        Assert.Equal(string.Empty, reloadedNull.Description);

        TaskInventoryItem reloadedWith = loadedPart.Inventory.GetInventoryItem(withDescription.ItemID);
        Assert.NotNull(reloadedWith);
        Assert.Equal("a description", reloadedWith.Description);

        Assert.Equal(2, loadedPart.Inventory.Count);
    }
}
