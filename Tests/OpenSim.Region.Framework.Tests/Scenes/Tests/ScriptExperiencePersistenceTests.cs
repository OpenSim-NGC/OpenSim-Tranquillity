using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.SimulationService;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A script compiled into an Experience keeps that Experience (TaskInventoryItem.ExperienceID) through the region
/// store (primitems) and through the object XML (take and rez, region crossing, attachments, OAR).
///
/// SQLite runs against a throwaway in-memory database per test (shared-cache, kept alive by an anchor connection so
/// that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// </summary>
public class ScriptExperiencePersistenceTests : OpenSimTestCase
{
    private static readonly UUID s_experience = new UUID("6e2a5f0c-3b1d-4c8e-9a47-0d5b8c2e1f93");

    private readonly List<IDisposable> m_disposables = new();
    private readonly List<SQLiteSimulationData> m_stores = new();

    static ScriptExperiencePersistenceTests()
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
        string conn = "FullUri=file:core8_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
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

    private static TaskInventoryItem NewScriptItem(SceneObjectPart part, UUID experience)
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
            Name = "experience script",
            Description = "",
            Type = (int)AssetType.LSLText,
            InvType = (int)InventoryType.LSL,
            ExperienceID = experience
        };
    }

    private static TaskInventoryItem LoadItem(SQLiteSimulationData store, UUID regionID, UUID partID, UUID itemID)
    {
        SceneObjectGroup sog = store.LoadObjects(regionID).Single(g => g.GetPart(partID) is not null);
        TaskInventoryItem item = sog.GetPart(partID).Inventory.GetInventoryItem(itemID);
        Assert.NotNull(item);
        return item;
    }

    // ---- region store (SQLite) ---------------------------------------------------------------------------------

    [Fact]
    public void SQLite_StoreAndReload_KeepsExperienceID()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();

        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random());
        SceneObjectPart part = sog.RootPart;
        TaskInventoryItem withExperience = NewScriptItem(part, s_experience);
        TaskInventoryItem withoutExperience = NewScriptItem(part, UUID.Zero);

        SQLiteSimulationData first = NewStore(conn);
        first.StoreObject(sog, regionID);
        first.StorePrimInventory(part.UUID, new List<TaskInventoryItem> { withExperience, withoutExperience });

        // A new store instance reads the database afresh, as after a region restart.
        SQLiteSimulationData second = NewStore(conn);
        Assert.Equal(s_experience, LoadItem(second, regionID, part.UUID, withExperience.ItemID).ExperienceID);
        Assert.Equal(UUID.Zero, LoadItem(second, regionID, part.UUID, withoutExperience.ItemID).ExperienceID);
    }

    [Fact]
    public void SQLite_RowWrittenBeforeTheColumnExisted_ReadsZeroAfterMigration()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();

        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random());
        SceneObjectPart part = sog.RootPart;

        SQLiteSimulationData first = NewStore(conn);
        first.StoreObject(sog, regionID);
        first.Dispose();

        // Put the database back to the schema before this change (RegionStore 43, no experienceID column, and
        // no prims SitTargetActive column, which step 45 adds) and write a task item row the way the older code did.
        UUID itemID = UUID.Random();
        using (SQLiteConnection c = new SQLiteConnection(conn))
        {
            c.Open();
            using SQLiteCommand cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE primitems DROP COLUMN experienceID";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "ALTER TABLE prims DROP COLUMN SitTargetActive";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "UPDATE migrations SET version = 43 WHERE name = 'RegionStore'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
            cmd.CommandText =
                "INSERT INTO primitems (itemID, primID, assetID, parentFolderID, invType, assetType, name, description, " +
                "creationDate, creatorID, ownerID, lastOwnerID, groupID, nextPermissions, currentPermissions, " +
                "basePermissions, everyonePermissions, groupPermissions, flags) VALUES " +
                "(:itemID, :primID, :assetID, :primID, 10, 10, 'old script', '', 0, :ownerID, :ownerID, :ownerID, " +
                ":zero, 2147483647, 2147483647, 2147483647, 0, 0, 0)";
            cmd.Parameters.AddWithValue(":itemID", itemID.ToString());
            cmd.Parameters.AddWithValue(":primID", part.UUID.ToString());
            cmd.Parameters.AddWithValue(":assetID", UUID.Random().ToString());
            cmd.Parameters.AddWithValue(":ownerID", part.OwnerID.ToString());
            cmd.Parameters.AddWithValue(":zero", UUID.Zero.ToString());
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        // Opening the store runs the migration; the old row reads as "no Experience".
        SQLiteSimulationData second = NewStore(conn);
        TaskInventoryItem item = LoadItem(second, regionID, part.UUID, itemID);
        Assert.Equal("old script", item.Name);
        Assert.Equal(UUID.Zero, item.ExperienceID);

        using (SQLiteConnection c = new SQLiteConnection(conn))
        {
            c.Open();
            using SQLiteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT version FROM migrations WHERE name = 'RegionStore'";
            Assert.Equal(45L, Convert.ToInt64(cmd.ExecuteScalar()));
            cmd.CommandText = "SELECT experienceID FROM primitems WHERE itemID = :itemID";
            cmd.Parameters.AddWithValue(":itemID", itemID.ToString());
            Assert.Equal(UUID.Zero.ToString(), (string)cmd.ExecuteScalar());
        }
    }

    // ---- object XML ----------------------------------------------------------------------------------------------

    private static (SceneObjectGroup sog, TaskInventoryItem item) NewObjectWithScript(UUID experience)
    {
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random());
        TaskInventoryItem item = NewScriptItem(sog.RootPart, experience);
        sog.RootPart.Inventory.AddInventoryItem(item, false);
        return (sog, item);
    }

    [Fact]
    public void Xml_WithExperienceID_RoundTrips()
    {
        (SceneObjectGroup sog, TaskInventoryItem item) = NewObjectWithScript(s_experience);

        string xml = SceneObjectSerializer.ToXml2Format(sog);
        Assert.Contains("<ExperienceID><UUID>" + s_experience + "</UUID></ExperienceID>", xml);

        SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(xml);
        Assert.Equal(s_experience, back.RootPart.Inventory.GetInventoryItem(item.ItemID).ExperienceID);
    }

    [Fact]
    public void Xml_WithoutExperienceID_WritesNoElement()
    {
        (SceneObjectGroup sog, TaskInventoryItem item) = NewObjectWithScript(UUID.Zero);

        string xml = SceneObjectSerializer.ToXml2Format(sog);
        Assert.DoesNotContain("ExperienceID", xml);

        SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(xml);
        Assert.Equal(UUID.Zero, back.RootPart.Inventory.GetInventoryItem(item.ItemID).ExperienceID);
    }

    [Fact]
    public void Xml_OldXmlWithoutTheElement_ReadsZeroAndKeepsTheRest()
    {
        (SceneObjectGroup sog, TaskInventoryItem item) = NewObjectWithScript(s_experience);

        // XML as written before this change: the same item, no <ExperienceID> element.
        string xml = SceneObjectSerializer.ToXml2Format(sog);
        string oldXml = xml.Replace("<ExperienceID><UUID>" + s_experience + "</UUID></ExperienceID>", "");
        Assert.NotEqual(xml, oldXml);

        SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(oldXml);
        TaskInventoryItem read = back.RootPart.Inventory.GetInventoryItem(item.ItemID);
        Assert.NotNull(read);
        Assert.Equal(item.Name, read.Name);
        Assert.Equal(item.AssetID, read.AssetID);
        Assert.Equal(UUID.Zero, read.ExperienceID);
    }

    // ---- scene, end to end ---------------------------------------------------------------------------------------

    /// <summary>
    /// A scene backs an object up through the region's simulation data service (configured for SQLite as an
    /// operator would), the region "restarts" (a new service instance on the same database), and the restored
    /// object's script still names its Experience.
    /// </summary>
    [Fact]
    public void Scene_BackupAndRestore_KeepsScriptExperience()
    {
        string conn = NewMemoryDatabase();

        IConfigSource config = new IniConfigSource();
        config.AddConfig("SimulationDataStore");
        config.Configs["SimulationDataStore"].Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        config.Configs["SimulationDataStore"].Set("ConnectionString", conn);

        TestScene scene = new SceneHelpers().SetupScene();
        scene.UseBackup = true;
        SceneObjectGroup sog = SceneHelpers.AddSceneObject(scene);
        TaskInventoryItem script = TaskInventoryHelpers.AddScript(scene.AssetService, sog.RootPart);

        // The helper leaves Description null; items from a viewer or inventory always carry one, and the SQLite
        // store cannot read a NULL description back.
        script.Description = string.Empty;

        // What a compile into an Experience leaves on the item (Scene.CapsUpdateTaskInventoryScriptAsset).
        script.ExperienceID = s_experience;
        sog.UpdateInventoryItem(script);
        sog.HasGroupChanged = true;

        SimulationDataService before = new SimulationDataService(config);
        sog.ProcessBackup(before, true);

        SimulationDataService after = new SimulationDataService(config);
        SceneObjectGroup restored = after.LoadObjects(scene.RegionInfo.RegionID).Single(g => g.UUID == sog.UUID);

        TestScene reloaded = new SceneHelpers().SetupScene();
        Assert.True(reloaded.AddRestoredSceneObject(restored, true, true));

        TaskInventoryItem item = reloaded.GetSceneObjectGroup(sog.UUID).RootPart.Inventory.GetInventoryItem(script.ItemID);
        Assert.NotNull(item);
        Assert.Equal(s_experience, item.ExperienceID);
    }

    // ---- script copied from one prim to another ------------------------------------------------------------------

    // The link belongs to the compiled script, so a copy of the script carries it.

    private static (TestScene scene, SceneObjectPart src, SceneObjectPart dest, TaskInventoryItem withExperience,
        TaskInventoryItem withoutExperience) NewPrimsWithScripts()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        UUID owner = UUID.Random();
        SceneObjectPart src = SceneHelpers.AddSceneObject(scene, "source", owner).RootPart;
        SceneObjectPart dest = SceneHelpers.AddSceneObject(scene, "destination", owner).RootPart;

        TaskInventoryItem withExperience = NewScriptItem(src, s_experience);
        withExperience.Name = "with experience";
        TaskInventoryItem withoutExperience = NewScriptItem(src, UUID.Zero);
        withoutExperience.Name = "without experience";
        src.Inventory.AddInventoryItem(withExperience, false);
        src.Inventory.AddInventoryItem(withoutExperience, false);
        return (scene, src, dest, withExperience, withoutExperience);
    }

    private static TaskInventoryItem CopiedItem(SceneObjectPart dest, string name)
    {
        TaskInventoryItem item = dest.Inventory.GetInventoryItem(name);
        Assert.NotNull(item);
        return item;
    }

    /// <summary>llGiveInventory, llGiveInventoryList and osGiveLinkInventory(List) to a prim.</summary>
    [Fact]
    public void MoveTaskInventoryItem_ToAnotherPrim_KeepsExperienceID()
    {
        (TestScene scene, SceneObjectPart src, SceneObjectPart dest, TaskInventoryItem withExperience,
            TaskInventoryItem withoutExperience) = NewPrimsWithScripts();

        scene.MoveTaskInventoryItem(dest.UUID, src, withExperience.ItemID);
        scene.MoveTaskInventoryItems(dest.UUID, "ignored", src, new List<UUID> { withoutExperience.ItemID });

        TaskInventoryItem copied = CopiedItem(dest, "with experience");
        Assert.NotEqual(withExperience.ItemID, copied.ItemID);
        Assert.Equal(s_experience, copied.ExperienceID);
        Assert.Equal(UUID.Zero, CopiedItem(dest, "without experience").ExperienceID);
    }

    /// <summary>llRemoteLoadScriptPin.</summary>
    [Fact]
    public void RezScriptFromPrim_KeepsExperienceID()
    {
        (TestScene scene, SceneObjectPart src, SceneObjectPart dest, TaskInventoryItem withExperience,
            TaskInventoryItem withoutExperience) = NewPrimsWithScripts();
        const int pin = 4711;
        dest.ScriptAccessPin = pin;

        scene.RezScriptFromPrim(withExperience.ItemID, src, dest.UUID, pin, 0, 0);
        scene.RezScriptFromPrim(withoutExperience.ItemID, src, dest.UUID, pin, 0, 0);

        TaskInventoryItem copied = CopiedItem(dest, "with experience");
        Assert.NotEqual(withExperience.ItemID, copied.ItemID);
        Assert.Equal(s_experience, copied.ExperienceID);
        Assert.Equal(UUID.Zero, CopiedItem(dest, "without experience").ExperienceID);
    }
}
