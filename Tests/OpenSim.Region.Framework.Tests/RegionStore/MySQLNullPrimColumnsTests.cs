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
using OpenSim.Data.MySQL;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Tests;
using OpenSim.Tests.Common;
using Xunit;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The MySQL region store loads a region whose prims, primshapes and primitems rows hold NULL in every column the
/// schema allows to be NULL, as rows written by other tools can. Each NULL used to be cast to a string or a number,
/// which threw InvalidCastException and stopped the whole region's load. A NULL now reads as a default.
///
/// Opt-in (OPENSIM_TEST_MYSQL): each test gets a new database, dropped when it ends. No process-wide state.
/// </summary>
public class MySQLNullPrimColumnsTests : IDisposable
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

    [MySQLServerFact]
    public void RowsWithNullInEveryNullableColumn_Load()
    {
        string conn = NewMySQLDatabase();
        UUID regionID = UUID.Random();
        MySQLSimulationData store = new MySQLSimulationData(conn);

        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(2, UUID.Random());
        SceneObjectPart root = sog.RootPart;
        TaskInventoryItem item = new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            ParentID = root.UUID,
            ParentPartID = root.UUID,
            OwnerID = root.OwnerID,
            CreatorID = root.OwnerID,
            Name = "item",
            Description = "description",
            Type = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard
        };
        store.StoreObject(sog, regionID);
        store.StorePrimInventory(root.UUID, new List<TaskInventoryItem> { item });

        // Every nullable column, but those the load finds the rows by: the region, the linkset, the shape row and
        // the item's prim.
        using (MySqlConnection c = new MySqlConnection(conn))
        {
            c.Open();
            SetNullableColumnsToNull(c, "prims", "RegionUUID", "SceneGroupID");
            SetNullableColumnsToNull(c, "primshapes", "Shape");
            SetNullableColumnsToNull(c, "primitems", "primID");
        }

        SceneObjectGroup loaded = Assert.Single(new MySQLSimulationData(conn).LoadObjects(regionID));
        Assert.Equal(2, loaded.PrimCount);
        SceneObjectPart part = loaded.GetPart(root.UUID);
        Assert.NotNull(part);

        Assert.Equal(string.Empty, part.Text);
        Assert.Equal(string.Empty, part.Description);
        Assert.Equal(string.Empty, part.SitName);
        Assert.Equal(string.Empty, part.TouchName);
        // A NULL mask grants nothing.
        Assert.Equal(0u, part.BaseMask);
        Assert.Equal(0u, part.OwnerMask);
        Assert.Equal(0u, part.EveryoneMask);
        Assert.Equal(Quaternion.Identity, part.RotationOffset);
        Assert.Equal(Quaternion.Identity, part.SitTargetOrientationLL);
        Assert.Equal(1000f, part.Density);
        Assert.Equal(0.6f, part.Friction);
        Assert.True(part.AllowUnsit);
        Assert.False(part.ScriptedSitOnly);

        // A NULL shape column reads as the default box's.
        Assert.Equal((byte)PCode.Prim, part.Shape.PCode);
        Assert.Equal((byte)Extrusion.Straight, part.Shape.PathCurve);
        Assert.Equal((byte)ProfileShape.Square, part.Shape.ProfileCurve);
        Assert.Equal((byte)100, part.Shape.PathScaleX);
        Assert.NotNull(part.Shape.TextureEntry);

        TaskInventoryItem loadedItem = part.Inventory.GetInventoryItem(item.ItemID);
        Assert.NotNull(loadedItem);
        Assert.Equal(string.Empty, loadedItem.Name);
        Assert.Equal(string.Empty, loadedItem.Description);
        Assert.Equal(0u, loadedItem.BasePermissions);
        Assert.Equal(0u, loadedItem.EveryonePermissions);
        Assert.Equal((int)AssetType.Unknown, loadedItem.Type);
    }

    /// <summary>The values the region writes still read back as written.</summary>
    [MySQLServerFact]
    public void StoredValues_ReadBackAsWritten()
    {
        string conn = NewMySQLDatabase();
        UUID regionID = UUID.Random();
        MySQLSimulationData store = new MySQLSimulationData(conn);

        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random());
        SceneObjectPart root = sog.RootPart;
        root.Text = "text";
        root.Description = "description";
        root.SitName = "sit";
        root.TouchName = "touch";
        root.CreationDate = 1234567;
        root.BaseMask = (uint)(PermissionMask.All | PermissionMask.Export);
        root.OwnerMask = (uint)PermissionMask.All;
        root.EveryoneMask = (uint)PermissionMask.Copy;
        root.RotationOffset = new Quaternion(0f, 0f, 1f, 0f);
        root.Density = 500f;
        root.AllowUnsit = false;
        root.ScriptedSitOnly = true;
        root.Shape = PrimitiveBaseShape.CreateCylinder();
        root.Shape.PathScaleX = 50;
        root.Shape.PathTwist = -20;
        root.Shape.ProfileHollow = 5000;
        TaskInventoryItem item = new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = UUID.Random(),
            ParentID = root.UUID,
            ParentPartID = root.UUID,
            OwnerID = root.OwnerID,
            CreatorID = root.OwnerID,
            Name = "item",
            Description = "item description",
            CreationDate = 7654321,
            BasePermissions = (uint)PermissionMask.All,
            EveryonePermissions = (uint)PermissionMask.Move,
            Type = (int)AssetType.Notecard,
            InvType = (int)InventoryType.Notecard
        };
        store.StoreObject(sog, regionID);
        store.StorePrimInventory(root.UUID, new List<TaskInventoryItem> { item });

        SceneObjectPart part = Assert.Single(new MySQLSimulationData(conn).LoadObjects(regionID)).RootPart;

        Assert.Equal("text", part.Text);
        Assert.Equal("description", part.Description);
        Assert.Equal("sit", part.SitName);
        Assert.Equal("touch", part.TouchName);
        Assert.Equal(1234567, part.CreationDate);
        Assert.Equal((uint)(PermissionMask.All | PermissionMask.Export), part.BaseMask);
        Assert.Equal((uint)PermissionMask.All, part.OwnerMask);
        Assert.Equal((uint)PermissionMask.Copy, part.EveryoneMask);
        Assert.Equal(new Quaternion(0f, 0f, 1f, 0f), part.RotationOffset);
        Assert.Equal(500f, part.Density);
        Assert.False(part.AllowUnsit);
        Assert.True(part.ScriptedSitOnly);
        Assert.Equal(root.Shape.ProfileCurve, part.Shape.ProfileCurve);
        Assert.Equal(root.Shape.PathCurve, part.Shape.PathCurve);
        Assert.Equal((byte)50, part.Shape.PathScaleX);
        Assert.Equal((sbyte)-20, part.Shape.PathTwist);
        Assert.Equal((ushort)5000, part.Shape.ProfileHollow);
        Assert.Equal(root.Shape.TextureEntry, part.Shape.TextureEntry);

        TaskInventoryItem loadedItem = part.Inventory.GetInventoryItem(item.ItemID);
        Assert.NotNull(loadedItem);
        Assert.Equal("item", loadedItem.Name);
        Assert.Equal("item description", loadedItem.Description);
        Assert.Equal(7654321u, loadedItem.CreationDate);
        Assert.Equal((uint)PermissionMask.All, loadedItem.BasePermissions);
        Assert.Equal((uint)PermissionMask.Move, loadedItem.EveryonePermissions);
        Assert.Equal((int)AssetType.Notecard, loadedItem.Type);
        Assert.Equal((int)InventoryType.Notecard, loadedItem.InvType);
    }

    private static void SetNullableColumnsToNull(MySqlConnection c, string table, params string[] keep)
    {
        List<string> columns = new();
        using (MySqlCommand cmd = new MySqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() " +
            "AND table_name = @table AND is_nullable = 'YES'", c))
        {
            cmd.Parameters.AddWithValue("@table", table);
            using MySqlDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(0));
        }
        columns.RemoveAll(col => keep.Contains(col, StringComparer.OrdinalIgnoreCase));
        Assert.NotEmpty(columns);

        string set = string.Join(", ", columns.Select(col => $"`{col}` = NULL"));
        using MySqlCommand update = new MySqlCommand($"UPDATE `{table}` SET {set}", c);
        Assert.True(update.ExecuteNonQuery() > 0);
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "nullprims_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

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
