/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
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
/// A linkset saved through the SQLite region store keeps every prim's link number across a region restart, as the
/// MySQL and PostgreSQL region stores do with their prims.LinkNumber column. A script sees these numbers through
/// llGetLinkNumber and addresses prims by them (llSetLinkPrimitiveParamsFast, llGetLinkName), so a change of numbers
/// on restart changes what the script does.
///
/// Each case stores a linkset whose link order is not the order its prims were first stored in, the way a builder
/// rezzes prims one at a time and links them later, and a linkset stored once, already linked. A second store
/// instance on the same database then loads it, as a region restart does.
///
/// SQLite runs against a throwaway in-memory database per test (shared cache, kept alive by an anchor connection,
/// so that a second store instance reads what the first one wrote). No files are written.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SQLiteLinkNumberTests : OpenSimTestCase
{
    private static readonly UUID s_region = new UUID("5d2c8e41-0b7f-4a93-9e16-c4a8f20b7d35");
    private static readonly UUID s_otherRegion = new UUID("0b6f2d93-7c41-4e8a-a5d2-93e17c4b0f68");
    private static readonly UUID s_owner =new UUID("e8a14c07-6d3b-4f52-b9e0-17c5a2d983f6");

    private readonly List<IDisposable> m_disposables = new();
    private readonly List<SQLiteSimulationData> m_stores = new();

    static SQLiteLinkNumberTests()
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
        string conn = "FullUri=file:linknumber_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
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

    /// <summary>
    /// Builds the parts of a linkset named L1..Ln, where Lk is to have link number k. The UUIDs come from a fixed
    /// seed, so every run stores the same rows.
    /// </summary>
    private static SceneObjectPart[] NewParts(int count, int seed)
    {
        Random random = new Random(seed);
        SceneObjectPart[] parts = new SceneObjectPart[count];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[16];
            random.NextBytes(bytes);
            parts[i] = SceneHelpers.CreateSceneObjectPart("L" + (i + 1), new UUID(new Guid(bytes)), s_owner);
            parts[i].OffsetPosition = new Vector3(0, 0.5f * i, 0);
        }
        return parts;
    }

    /// <summary>Each part as "name=link number", in the order the group holds them by link number.</summary>
    private static string Describe(SceneObjectGroup group)
    {
        return string.Join(" ", group.Parts.OrderBy(p => p.LinkNum).Select(p => p.Name + "=" + p.LinkNum));
    }

    private static string Expected(int count)
    {
        return string.Join(" ", Enumerable.Range(1, count).Select(k => "L" + k + "=" + k));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(12, false)]
    [InlineData(12, true)]
    public void EveryPrimKeepsItsLinkNumberAcrossARestart(int count, bool rezzedSeparatelyFirst)
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData before = NewStore(conn);
        SceneObjectPart[] parts = NewParts(count, seed: count * 10 + (rezzedSeparatelyFirst ? 1 : 0));

        if (rezzedSeparatelyFirst)
            StoreSeparately(before, parts);

        SceneObjectGroup linkset = Link(parts);
        Assert.Equal(Expected(count), Describe(linkset));

        before.StoreObject(linkset, s_region);

        List<SceneObjectGroup> loaded = NewStore(conn).LoadObjects(s_region);

        SceneObjectGroup reloaded = Assert.Single(loaded);
        Assert.Equal(parts[0].UUID, reloaded.RootPart.UUID);
        Assert.Equal(count, reloaded.PrimCount);
        Assert.Equal(Expected(count), Describe(reloaded));
    }

    /// <summary>
    /// The order the parts are rezzed in, one at a time, before they are linked: unlike the link order (evens, then
    /// odds, from the top).
    /// </summary>
    private static int[] RezOrder(int count)
    {
        return Enumerable.Range(0, count).Where(i => i % 2 == 1).Reverse()
            .Concat(Enumerable.Range(0, count).Where(i => i % 2 == 0).Reverse()).ToArray();
    }

    /// <summary>Stores each part as its own object, in <see cref="RezOrder"/>, as the region does before linking.</summary>
    private static void StoreSeparately(SQLiteSimulationData store, SceneObjectPart[] parts)
    {
        foreach (int i in RezOrder(parts.Length))
            store.StoreObject(new SceneObjectGroup(parts[i]), s_region);
    }

    /// <summary>Links the parts in array order: parts[0] is the root, parts[k] gets link number k + 1.</summary>
    private static SceneObjectGroup Link(SceneObjectPart[] parts)
    {
        SceneObjectGroup linkset = new SceneObjectGroup(parts[0]);
        for (int i = 1; i < parts.Length; i++)
            linkset.AddPart(parts[i]);
        return linkset;
    }

    /// <summary>
    /// The numbers the store's loader gave a linkset stored by <see cref="StoreSeparately"/> and then linked, before
    /// the LinkNumber column: the root 1, then the children numbered 2, 3, ... in the order of their rows, which is
    /// the order they were first stored in.
    /// </summary>
    private static string RowOrderNumbers(int count)
    {
        return string.Join(" ", RowOrderMap(count).OrderBy(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value));
    }

    /// <summary>The numbers of <see cref="RowOrderNumbers"/>, by prim name.</summary>
    private static Dictionary<string, int> RowOrderMap(int count)
    {
        Dictionary<string, int> numbers = new() { ["L1"] = 1 };
        int next = 2;
        foreach (int i in RezOrder(count).Where(i => i != 0))
            numbers["L" + (i + 1)] = next++;
        return numbers;
    }

    private SQLiteConnection OpenConnection(string conn)
    {
        SQLiteConnection c = new SQLiteConnection(conn);
        c.Open();
        m_disposables.Add(c);
        return c;
    }

    /// <summary>Each stored prim's LinkNumber column, by prim name, null for NULL.</summary>
    private static Dictionary<string, int?> StoredLinkNumbers(SQLiteConnection c)
    {
        Dictionary<string, int?> numbers = new();
        using SQLiteCommand cmd = new SQLiteCommand("select Name, LinkNumber from prims", c);
        using SQLiteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            numbers.Add(reader.GetString(0), reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1)));
        return numbers;
    }

    private static void Execute(SQLiteConnection c, string sql)
    {
        using SQLiteCommand cmd = new SQLiteCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The last SQLite RegionStore migration step before the LinkNumber column was added.</summary>
    private const int VersionBeforeColumn = 46;

    /// <summary>
    /// A database written before the LinkNumber column, holding a linkset whose prims were stored before they were
    /// linked and a prim alone, migrates once when a store opens it. The step stores the numbers the loader gave
    /// before it (the children in row order), so the linkset loads with the same numbers as before the step, and
    /// keeps them from then on. For the 6-prim case the expected text is also the one the loader gave before this
    /// change in the red run of <see cref="EveryPrimKeepsItsLinkNumberAcrossARestart"/> (same seed, same rows).
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    public void DatabaseWrittenBeforeTheColumn_KeepsTheNumbersItLoadedWith(int count)
    {
        SceneObjectPart[] parts = NewParts(count, seed: count * 10 + 1);
        SceneObjectPart alone = NewParts(1, seed: 7)[0];
        alone.Name = "Alone";
        // a linkset in another region of the same store, its rows between this region's rows
        SceneObjectPart[] other = NewParts(3, seed: 5);
        for (int i = 0; i < other.Length; i++)
            other[i].Name = "O" + (i + 1);

        // Rows as the store writes them, copied in the same row order into a database that stops at the step
        // before the column.
        string written = NewMemoryDatabase();
        SQLiteSimulationData writer = NewStore(written);
        writer.StoreObject(new SceneObjectGroup(other[2]), s_otherRegion);
        StoreSeparately(writer, parts);
        writer.StoreObject(new SceneObjectGroup(other[1]), s_otherRegion);
        writer.StoreObject(new SceneObjectGroup(alone), s_region);
        writer.StoreObject(Link(parts), s_region);
        writer.StoreObject(Link(other), s_otherRegion);

        string old = NewMemoryDatabase();
        SQLiteConnection oldConn = OpenConnection(old);
        SQLiteSitTargetActiveTests.CreateDatabaseAtVersion(oldConn, VersionBeforeColumn);
        Assert.DoesNotContain("LinkNumber", SQLiteSitTargetActiveTests.Columns(oldConn, "prims"));
        SQLiteConnection writtenConn = OpenConnection(written);
        SQLiteSitTargetActiveTests.CopyRows(writtenConn, oldConn, "prims");
        SQLiteSitTargetActiveTests.CopyRows(writtenConn, oldConn, "primshapes");

        List<SceneObjectGroup> loaded = NewStore(old).LoadObjects(s_region);
        int migratedTo = SQLiteSitTargetActiveTests.RegionStoreVersion(oldConn);
        Assert.Equal(VersionBeforeColumn + 1, migratedTo);

        // every prim's number as the step wrote it: the old loader's numbers, per linkset and per region
        Dictionary<string, int?> stored = StoredLinkNumbers(oldConn);
        Assert.Equal(count + 1 + other.Length, stored.Count);
        Assert.Equal(0, stored["Alone"]);
        foreach (KeyValuePair<string, int> expected in RowOrderMap(count))
            Assert.Equal(expected.Value, stored[expected.Key]);
        // O3 and O2 were stored before O1 linked them, so their rows come first: O3 is link 2, O2 link 3
        Assert.Equal(1, stored["O1"]);
        Assert.Equal(2, stored["O3"]);
        Assert.Equal(3, stored["O2"]);
        Assert.Equal("O1=1 O3=2 O2=3", Describe(Assert.Single(NewStore(old).LoadObjects(s_otherRegion))));

        Assert.Equal(2, loaded.Count);
        SceneObjectGroup linkset = Assert.Single(loaded, g => g.RootPart.UUID == parts[0].UUID);
        Assert.Equal(RowOrderNumbers(count), Describe(linkset));
        if (count == 6)
            Assert.Equal("L1=1 L6=2 L4=3 L2=4 L5=5 L3=6", Describe(linkset));
        Assert.Equal(0, Assert.Single(loaded, g => g.RootPart.UUID == alone.UUID).RootPart.LinkNum);

        // A second open runs nothing and loads the same numbers.
        List<SceneObjectGroup> again = NewStore(old).LoadObjects(s_region);
        Assert.Equal(migratedTo, SQLiteSitTargetActiveTests.RegionStoreVersion(oldConn));
        Assert.Equal(RowOrderNumbers(count), Describe(Assert.Single(again, g => g.RootPart.UUID == parts[0].UUID)));
    }

    /// <summary>
    /// A linkset with a child that has no stored number, or two children with the same one, loads with its children
    /// numbered in row order, as before the column, and the next store writes those numbers, which then hold.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("root's")]
    [InlineData("gap")]
    public void LinksetWithUnusableNumbers_LoadsInRowOrder_AndTheNextStoreFixesThem(string damage)
    {
        const int count = 6;
        string conn = NewMemoryDatabase();
        SQLiteSimulationData before = NewStore(conn);
        SceneObjectPart[] parts = NewParts(count, seed: 61);
        StoreSeparately(before, parts);
        before.StoreObject(Link(parts), s_region);

        SQLiteConnection c = OpenConnection(conn);
        Execute(c, damage switch
        {
            "missing" => "update prims set LinkNumber = NULL where Name = 'L4'",
            "repeated" => "update prims set LinkNumber = 4 where Name = 'L5'",
            "gap" => "update prims set LinkNumber = 8 where Name = 'L6'",
            _ => "update prims set LinkNumber = 1 where Name = 'L3'",
        });

        SQLiteSimulationData restarted = NewStore(conn);
        SceneObjectGroup reloaded = Assert.Single(restarted.LoadObjects(s_region));
        Assert.Equal(count, reloaded.PrimCount);
        Assert.Equal(RowOrderNumbers(count), Describe(reloaded));

        restarted.StoreObject(reloaded, s_region);
        Assert.All(StoredLinkNumbers(c).Values, n => Assert.NotNull(n));
        SceneObjectGroup again = Assert.Single(NewStore(conn).LoadObjects(s_region));
        Assert.Equal(RowOrderNumbers(count), Describe(again));
    }

    /// <summary>
    /// A row with no SceneGroupID is logged and skipped as before; the region's other objects still load with their
    /// numbers.
    /// </summary>
    [Fact]
    public void RowWithNoSceneGroupID_IsSkipped_AndTheOtherObjectsLoad()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData before = NewStore(conn);
        SceneObjectPart[] parts = NewParts(6, seed: 61);
        StoreSeparately(before, parts);
        before.StoreObject(Link(parts), s_region);
        SceneObjectPart broken = NewParts(1, seed: 11)[0];
        broken.Name = "Broken";
        before.StoreObject(new SceneObjectGroup(broken), s_region);
        Execute(OpenConnection(conn), "update prims set SceneGroupID = NULL where Name = 'Broken'");

        SceneObjectGroup reloaded = Assert.Single(NewStore(conn).LoadObjects(s_region));
        Assert.Equal(Expected(6), Describe(reloaded));
    }

    /// <summary>A prim alone is stored with link number 0 and loads with it.</summary>
    [Fact]
    public void SinglePrim_StoresAndLoadsLinkNumberZero()
    {
        string conn = NewMemoryDatabase();
        SceneObjectPart part = NewParts(1, seed: 3)[0];
        NewStore(conn).StoreObject(new SceneObjectGroup(part), s_region);

        Assert.Equal(0, StoredLinkNumbers(OpenConnection(conn))["L1"]);
        SceneObjectGroup reloaded = Assert.Single(NewStore(conn).LoadObjects(s_region));
        Assert.Equal(1, reloaded.PrimCount);
        Assert.Equal(0, reloaded.RootPart.LinkNum);
    }

    /// <summary>
    /// The numbers after an unlink (the parts above the unlinked one move down by one, the unlinked prim is alone
    /// with 0) and after linking that prim back (it is added last) are stored and load the same after a restart.
    /// The prims were stored one at a time before the first link, so their rows are not in link order.
    /// </summary>
    [Fact]
    public void UnlinkAndRelink_TheNewNumbersSurviveARestart()
    {
        const int count = 6;
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        SceneObjectPart[] parts = NewParts(count, seed: 97);
        StoreSeparately(store, parts);

        TestScene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup linkset = Link(parts);
        scene.AddSceneObject(linkset);
        store.StoreObject(linkset, s_region);

        SceneObjectGroup unlinked = linkset.DelinkFromGroup(parts[2], false);
        Assert.Equal("L1=1 L2=2 L4=3 L5=4 L6=5", Describe(linkset));
        store.StoreObject(linkset, s_region);
        store.StoreObject(unlinked, s_region);

        List<SceneObjectGroup> loaded = NewStore(conn).LoadObjects(s_region);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("L1=1 L2=2 L4=3 L5=4 L6=5", Describe(Assert.Single(loaded, g => g.RootPart.UUID == parts[0].UUID)));
        Assert.Equal("L3=0", Describe(Assert.Single(loaded, g => g.RootPart.UUID == parts[2].UUID)));

        unlinked.RootPart.ClearUpdateSchedule();
        linkset.RootPart.ClearUpdateSchedule();
        linkset.LinkToGroup(unlinked);
        Assert.Equal("L1=1 L2=2 L4=3 L5=4 L6=5 L3=6", Describe(linkset));
        store.StoreObject(linkset, s_region);

        SceneObjectGroup relinked = Assert.Single(NewStore(conn).LoadObjects(s_region));
        Assert.Equal("L1=1 L2=2 L4=3 L5=4 L6=5 L3=6", Describe(relinked));
    }
}
