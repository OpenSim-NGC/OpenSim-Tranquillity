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
    private static readonly UUID s_owner = new UUID("e8a14c07-6d3b-4f52-b9e0-17c5a2d983f6");

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
        {
            // Rezzed one at a time, in an order unlike the link order (evens, then odds, from the top), and each
            // stored as its own object, as the region does before the prims are linked.
            int[] rezOrder = Enumerable.Range(0, count).Where(i => i % 2 == 1).Reverse()
                .Concat(Enumerable.Range(0, count).Where(i => i % 2 == 0).Reverse()).ToArray();
            foreach (int i in rezOrder)
                before.StoreObject(new SceneObjectGroup(parts[i]), s_region);
        }

        SceneObjectGroup linkset = new SceneObjectGroup(parts[0]);
        for (int i = 1; i < count; i++)
            linkset.AddPart(parts[i]);
        Assert.Equal(Expected(count), Describe(linkset));

        before.StoreObject(linkset, s_region);

        List<SceneObjectGroup> loaded = NewStore(conn).LoadObjects(s_region);

        SceneObjectGroup reloaded = Assert.Single(loaded);
        Assert.Equal(parts[0].UUID, reloaded.RootPart.UUID);
        Assert.Equal(count, reloaded.PrimCount);
        Assert.Equal(Expected(count), Describe(reloaded));
    }
}
