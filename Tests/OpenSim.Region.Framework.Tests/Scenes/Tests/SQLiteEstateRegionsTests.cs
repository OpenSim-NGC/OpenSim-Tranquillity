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
using System.Reflection;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The SQLite estate store lists the regions linked to an estate (GetRegions, read from estate_map), as the MySQL and
/// PostgreSQL estate stores do. Scene.GetEstateRegions and the estate service's GetRegions call read this list.
///
/// SQLite runs against a throwaway in-memory database per test (shared cache, kept alive by an anchor connection, so
/// that a second store instance reads what the first one wrote, as a restart does). No files are written.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SQLiteEstateRegionsTests : OpenSimTestCase
{
    private static readonly UUID s_regionA = new UUID("4d8a1c3e-5b72-4f09-a6e1-0c3b9d2f7e58");
    private static readonly UUID s_regionB = new UUID("c71f0e2a-8d43-4b65-9a1c-3e5f7b0d2a96");
    private static readonly UUID s_regionC = new UUID("19e6b3d0-4a2c-4e87-b5f3-8d0a6c1e9b27");

    private readonly List<IDisposable> m_disposables = new();

    static SQLiteEstateRegionsTests()
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
        string conn = "FullUri=file:estateregions_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_disposables.Add(anchor);
        return conn;
    }

    private SQLiteEstateStore OpenStore(string conn)
    {
        SQLiteEstateStore store = new SQLiteEstateStore(conn);
        m_disposables.Add(ConnectionOf(store));
        return store;
    }

    // The store has no close of its own: its connection lives as long as the region process does.
    private static SQLiteConnection ConnectionOf(SQLiteEstateStore store)
    {
        FieldInfo field = typeof(SQLiteEstateStore).GetField("m_connection", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (SQLiteConnection)field.GetValue(store);
    }

    [Fact]
    public void AnEstateListsTheRegionsLinkedToIt()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        int estate = (int)store.LoadEstateSettings(s_regionA, true).EstateID;
        int other = (int)store.CreateNewEstate(0).EstateID;
        Assert.True(store.LinkRegion(s_regionB, estate));
        Assert.True(store.LinkRegion(s_regionC, other));

        SQLiteEstateStore reopened = OpenStore(conn);

        Assert.Equal(new HashSet<UUID> { s_regionA, s_regionB }, new HashSet<UUID>(reopened.GetRegions(estate)));
        Assert.Equal(new[] { s_regionC }, reopened.GetRegions(other));
    }

    [Fact]
    public void ARegionMovedToAnotherEstateLeavesTheFirst()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        int first = (int)store.LoadEstateSettings(s_regionA, true).EstateID;
        Assert.True(store.LinkRegion(s_regionB, first));
        int second = (int)store.CreateNewEstate(0).EstateID;

        Assert.True(store.LinkRegion(s_regionB, second));

        Assert.Equal(new[] { s_regionA }, store.GetRegions(first));
        Assert.Equal(new[] { s_regionB }, store.GetRegions(second));
    }

    [Fact]
    public void AnEstateWithNoRegionsListsNone()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        store.LoadEstateSettings(s_regionA, true);
        int empty = (int)store.CreateNewEstate(0).EstateID;

        Assert.Empty(store.GetRegions(empty));
    }
}
