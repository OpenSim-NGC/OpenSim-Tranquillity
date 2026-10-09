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
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// The SQLite region store keeps a region's extra settings (Scene.StoreExtraSetting and RemoveExtraSetting) across a
/// region restart, as the MySQL and PostgreSQL region stores do in their regionextra table. Scene treats a null from
/// GetExtra as "this store keeps no extra settings" and then drops every one it is given.
///
/// SQLite runs against a throwaway in-memory database per test (shared cache, kept alive by an anchor connection,
/// so that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SQLiteRegionExtraTests : OpenSimTestCase
{
    private static readonly UUID s_region = new UUID("6b0e2f4d-93a1-4c57-8e2b-d4f71a09c3e5");
    private static readonly UUID s_otherRegion = new UUID("a3c95e18-27d4-4f6b-b081-5e9d3c7a2f46");

    private readonly List<IDisposable> m_disposables = new();
    private readonly List<SQLiteSimulationData> m_stores = new();

    static SQLiteRegionExtraTests()
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
        string conn = "FullUri=file:regionextra_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
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

    [Fact]
    public void ARegionWithNoExtraSettingsReadsAsEmpty()
    {
        string conn = NewMemoryDatabase();

        Dictionary<string, string> extra = NewStore(conn).GetExtra(s_region);

        Assert.NotNull(extra);
        Assert.Empty(extra);
    }

    [Fact]
    public void SavedSettingsReadBackAfterARestart()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        store.SaveExtra(s_region, "auto_grant_attach_perms", "true");
        store.SaveExtra(s_region, "example_setting", "some value");

        Dictionary<string, string> extra = NewStore(conn).GetExtra(s_region);

        Assert.Equal(2, extra.Count);
        Assert.Equal("true", extra["auto_grant_attach_perms"]);
        Assert.Equal("some value", extra["example_setting"]);
    }

    [Fact]
    public void SavingANameAgainReplacesItsValue()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        store.SaveExtra(s_region, "example_setting", "first");
        store.SaveExtra(s_region, "example_setting", "second");

        Dictionary<string, string> extra = NewStore(conn).GetExtra(s_region);

        Assert.Single(extra);
        Assert.Equal("second", extra["example_setting"]);
    }

    [Fact]
    public void RemovingANameRemovesOnlyThatName()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        store.SaveExtra(s_region, "kept", "1");
        store.SaveExtra(s_region, "removed", "2");
        store.SaveExtra(s_otherRegion, "removed", "3");

        store.RemoveExtra(s_region, "removed");

        SQLiteSimulationData reopened = NewStore(conn);
        Dictionary<string, string> extra = reopened.GetExtra(s_region);
        Assert.Single(extra);
        Assert.Equal("1", extra["kept"]);
        Assert.Equal("3", reopened.GetExtra(s_otherRegion)["removed"]);
    }

    [Fact]
    public void EachRegionReadsOnlyItsOwnSettings()
    {
        string conn = NewMemoryDatabase();
        SQLiteSimulationData store = NewStore(conn);
        store.SaveExtra(s_region, "example_setting", "mine");
        store.SaveExtra(s_otherRegion, "example_setting", "theirs");
        store.SaveExtra(s_otherRegion, "other_setting", "x");

        Dictionary<string, string> extra = NewStore(conn).GetExtra(s_region);

        Assert.Single(extra);
        Assert.Equal("mine", extra["example_setting"]);
    }
}
