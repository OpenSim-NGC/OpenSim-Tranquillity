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
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The SQLite region store keeps a prim's sit target state (SceneObjectPart.SitTargetActive) across a region
/// restart. SL PRIM_SIT_TARGET: "If the active value is 0 the sit target is deactivated. If it is nonzero the prim's
/// sit target is set to the indicated offset and rotation" and "Unlike llLinkSitTarget(), an offset of
/// &lt;0.0, 0.0, 0.0&gt; may be explicitly set". A prim whose state follows from its offset and rotation is stored as
/// before, and a database written before the column existed reads as before.
///
/// SQLite runs against a throwaway in-memory database per test (shared cache, kept alive by an anchor connection,
/// so that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SQLiteSitTargetActiveTests : OpenSimTestCase
{
    /// <summary>The last SQLite RegionStore migration step before the sit target column was added.</summary>
    private const int VersionBeforeColumn = 44;

    private readonly List<IDisposable> m_disposables = new();
    private readonly List<SQLiteSimulationData> m_stores = new();

    static SQLiteSitTargetActiveTests()
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
        string conn = "FullUri=file:sittarget_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_disposables.Add(anchor);
        return conn;
    }

    private SQLiteConnection OpenConnection(string conn)
    {
        SQLiteConnection c = new SQLiteConnection(conn);
        c.Open();
        m_disposables.Add(c);
        return c;
    }

    private SQLiteSimulationData NewStore(string conn)
    {
        SQLiteSimulationData store = new SQLiteSimulationData(conn);
        m_stores.Add(store);
        return store;
    }

    private static SceneObjectPart Reload(SQLiteSimulationData store, UUID regionID, SceneObjectPart part)
    {
        SceneObjectGroup loaded = store.LoadObjects(regionID).Single(g => g.GetPart(part.UUID) is not null);
        return loaded.GetPart(part.UUID);
    }

    private static object StoredColumn(SQLiteConnection c, UUID primID)
    {
        using SQLiteCommand cmd = new SQLiteCommand("select SitTargetActive from prims where UUID = :UUID", c);
        cmd.Parameters.AddWithValue(":UUID", primID.ToString());
        using SQLiteDataReader reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return reader.GetValue(0);
    }

    private static int RegionStoreVersion(SQLiteConnection c)
    {
        using SQLiteCommand cmd = new SQLiteCommand("select version from migrations where name = 'RegionStore'", c);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<string> Columns(SQLiteConnection c, string table)
    {
        List<string> columns = new();
        using SQLiteCommand cmd = new SQLiteCommand("pragma table_info(" + table + ")", c);
        using SQLiteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    [Fact]
    public void ActiveTargetAtZeroOffset_SurvivesReopeningTheStore()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();
        SceneObjectPart part = SceneHelpers.CreateSceneObject(1, UUID.Random()).RootPart;
        part.SetSitTarget(true, Vector3.Zero, Quaternion.Identity);
        Assert.True(part.IsSitTargetSet);

        NewStore(conn).StoreObject(part.ParentGroup, regionID);
        SceneObjectPart loaded = Reload(NewStore(conn), regionID, part);

        Assert.True(loaded.IsSitTargetSet);
        Assert.True(loaded.SitTargetActive);
        Assert.Equal(Vector3.Zero, loaded.SitTargetPosition);
        Assert.Equal(Quaternion.Identity, loaded.SitTargetOrientation);
    }

    [Fact]
    public void TargetThatWasNeverSet_StaysNotActive()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();
        SceneObjectPart part = SceneHelpers.CreateSceneObject(1, UUID.Random()).RootPart;
        Assert.False(part.IsSitTargetSet);

        NewStore(conn).StoreObject(part.ParentGroup, regionID);
        SceneObjectPart loaded = Reload(NewStore(conn), regionID, part);

        Assert.False(loaded.IsSitTargetSet);
        Assert.IsType<DBNull>(StoredColumn(OpenConnection(conn), part.UUID));
    }

    [Fact]
    public void TargetSetNotActiveWithAnOffset_StaysNotActive()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();
        Vector3 offset = new Vector3(0.5f, 0, 0.25f);
        SceneObjectPart part = SceneHelpers.CreateSceneObject(1, UUID.Random()).RootPart;
        part.SetSitTarget(false, offset, Quaternion.Identity);
        Assert.False(part.IsSitTargetSet);

        NewStore(conn).StoreObject(part.ParentGroup, regionID);
        SceneObjectPart loaded = Reload(NewStore(conn), regionID, part);

        Assert.False(loaded.IsSitTargetSet);
        Assert.Equal(offset, loaded.SitTargetPosition);
    }

    [Fact]
    public void TargetWithAnOffset_IsUnchangedAndStoredAsBefore()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();
        Vector3 offset = new Vector3(0, 0, 0.6f);
        Quaternion rotation = Quaternion.CreateFromEulers(0, 0, 1.0f);
        SceneObjectPart part = SceneHelpers.CreateSceneObject(1, UUID.Random()).RootPart;
        part.SitTargetPosition = offset;          // as llSitTarget sets it
        part.SitTargetOrientation = rotation;

        NewStore(conn).StoreObject(part.ParentGroup, regionID);
        SceneObjectPart loaded = Reload(NewStore(conn), regionID, part);

        Assert.True(loaded.IsSitTargetSet);
        Assert.Equal(offset, loaded.SitTargetPosition);
        Assert.Equal(rotation, loaded.SitTargetOrientation);
        // The state follows the offset and rotation, so nothing is stored for it.
        Assert.IsType<DBNull>(StoredColumn(OpenConnection(conn), part.UUID));
    }

    [Fact]
    public void ZeroOffsetActiveTargetRemovedByThePlainSetters_IsStoredAsBefore()
    {
        string conn = NewMemoryDatabase();
        UUID regionID = UUID.Random();
        SceneObjectPart part = SceneHelpers.CreateSceneObject(1, UUID.Random()).RootPart;
        SQLiteSimulationData first = NewStore(conn);

        part.SetSitTarget(true, Vector3.Zero, Quaternion.Identity);
        first.StoreObject(part.ParentGroup, regionID);
        Assert.Equal(1L, Convert.ToInt64(StoredColumn(OpenConnection(conn), part.UUID)));

        // llSitTarget(ZERO_VECTOR, ZERO_ROTATION) removes the target (plain setters, state follows the offset).
        part.SitTargetPosition = Vector3.Zero;
        part.SitTargetOrientation = Quaternion.Identity;
        first.StoreObject(part.ParentGroup, regionID);

        SceneObjectPart loaded = Reload(NewStore(conn), regionID, part);
        Assert.False(loaded.IsSitTargetSet);
        Assert.IsType<DBNull>(StoredColumn(OpenConnection(conn), part.UUID));
    }

    /// <summary>
    /// A database at the step before the column (built from the store's own migration text, as Migration runs it),
    /// holding rows the store wrote, migrates once when a store opens it and reads as before: a target with an
    /// offset is active, a prim with no target has none, and a target YEngine saved with its 1e-5 m offset keeps it.
    /// </summary>
    [Fact]
    public void DatabaseWrittenBeforeTheColumn_MigratesOnceAndReadsAsBefore()
    {
        UUID regionID = UUID.Random();
        Vector3 offset = new Vector3(0, 0.2f, 0.6f);
        Quaternion rotation = Quaternion.CreateFromEulers(0, 0, 0.5f);
        Vector3 nudged = new Vector3(0, 0, 1e-5f);

        SceneObjectPart withOffset = SceneHelpers.CreateSceneObject(1, UUID.Random(), 0x11).RootPart;
        withOffset.SitTargetPosition = offset;
        withOffset.SitTargetOrientation = rotation;
        SceneObjectPart noTarget = SceneHelpers.CreateSceneObject(1, UUID.Random(), 0x12).RootPart;
        SceneObjectPart nudgedTarget = SceneHelpers.CreateSceneObject(1, UUID.Random(), 0x13).RootPart;
        nudgedTarget.SitTargetPosition = nudged;
        nudgedTarget.SitTargetOrientation = Quaternion.Identity;

        // Rows as the store writes them, copied into a database that stops at the step before the column.
        string written = NewMemoryDatabase();
        SQLiteSimulationData writer = NewStore(written);
        foreach (SceneObjectPart p in new[] { withOffset, noTarget, nudgedTarget })
            writer.StoreObject(p.ParentGroup, regionID);

        string old = NewMemoryDatabase();
        SQLiteConnection oldConn = OpenConnection(old);
        CreateDatabaseAtVersion(oldConn, VersionBeforeColumn);
        Assert.DoesNotContain("SitTargetActive", Columns(oldConn, "prims"));
        SQLiteConnection writtenConn = OpenConnection(written);
        CopyRows(writtenConn, oldConn, "prims");
        CopyRows(writtenConn, oldConn, "primshapes");

        SQLiteSimulationData reopened = NewStore(old);
        int migratedTo = RegionStoreVersion(oldConn);
        Assert.True(migratedTo > VersionBeforeColumn);
        Assert.Single(Columns(oldConn, "prims"), c => c == "SitTargetActive");

        SceneObjectPart loadedWithOffset = Reload(reopened, regionID, withOffset);
        Assert.True(loadedWithOffset.IsSitTargetSet);
        Assert.Equal(offset, loadedWithOffset.SitTargetPosition);
        Assert.Equal(rotation, loadedWithOffset.SitTargetOrientation);
        Assert.False(Reload(reopened, regionID, noTarget).IsSitTargetSet);
        SceneObjectPart loadedNudged = Reload(reopened, regionID, nudgedTarget);
        Assert.True(loadedNudged.IsSitTargetSet);
        Assert.Equal(nudged, loadedNudged.SitTargetPosition);

        // A second open finds nothing to run and reads the same.
        SQLiteSimulationData again = NewStore(old);
        Assert.Equal(migratedTo, RegionStoreVersion(oldConn));
        Assert.Single(Columns(oldConn, "prims"), c => c == "SitTargetActive");
        Assert.True(Reload(again, regionID, withOffset).IsSitTargetSet);
        Assert.False(Reload(again, regionID, noTarget).IsSitTargetSet);
    }

    /// <summary>
    /// Runs the SQLite RegionStore migration steps up to and including <paramref name="version"/> and records the
    /// version, as OpenSim.Data.Migration does: lines that are empty or start with '#' are skipped, and each
    /// ":VERSION n" line starts a step.
    /// </summary>
    private static void CreateDatabaseAtVersion(SQLiteConnection c, int version)
    {
        var assembly = typeof(SQLiteSimulationData).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".RegionStore.migrations"));
        SortedList<int, string> steps = new();
        using (Stream stream = assembly.GetManifestResourceStream(name))
        using (StreamReader reader = new StreamReader(stream))
        {
            int current = -1;
            StringBuilder sb = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;
                if (line.StartsWith(":VERSION ", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (current > 0)
                        steps[current] = sb.ToString();
                    sb.Clear();
                    int hash = line.IndexOf('#');
                    current = int.Parse((hash >= 0 ? line.Substring(0, hash) : line).Substring(9).Trim());
                    continue;
                }
                sb.AppendLine(line);
            }
            if (current > 0)
                steps[current] = sb.ToString();
        }

        Assert.True(steps.ContainsKey(version));
        foreach (KeyValuePair<int, string> step in steps.Where(s => s.Key <= version))
        {
            using SQLiteCommand cmd = new SQLiteCommand(step.Value, c);
            cmd.ExecuteNonQuery();
        }
        using (SQLiteCommand cmd = new SQLiteCommand(
            "create table migrations(name varchar(100), version int);" +
            "insert into migrations(name, version) values('migrations', 1);" +
            "insert into migrations(name, version) values('RegionStore', " + version + ");", c))
        {
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Copies every row of a table, in the columns the destination has.</summary>
    private static void CopyRows(SQLiteConnection from, SQLiteConnection to, string table)
    {
        List<string> columns = Columns(to, table);
        DataTable rows = new DataTable();
        using (SQLiteCommand select = new SQLiteCommand("select " + string.Join(", ", columns) + " from " + table, from))
        using (SQLiteDataAdapter adapter = new SQLiteDataAdapter(select))
            adapter.Fill(rows);
        Assert.NotEmpty(rows.Rows);

        string insert = "insert into " + table + " (" + string.Join(", ", columns) + ") values (" +
            string.Join(", ", columns.Select(c => ":" + c)) + ")";
        foreach (DataRow row in rows.Rows)
        {
            using SQLiteCommand cmd = new SQLiteCommand(insert, to);
            foreach (string column in columns)
                cmd.Parameters.AddWithValue(":" + column, row[column]);
            cmd.ExecuteNonQuery();
        }
    }
}
