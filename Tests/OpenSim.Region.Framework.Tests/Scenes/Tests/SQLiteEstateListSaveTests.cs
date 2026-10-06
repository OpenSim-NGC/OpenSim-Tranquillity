/*
 * Copyright (c) Legion Builds
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
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
using System.Reflection;
using OpenMetaverse;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The SQLite estate store saves each of an estate's lists (ban list, managers, allowed users, allowed groups) by
/// deleting the estate's rows of that list and inserting the current ones. These tests check that each list reads back
/// as saved, that a save that fails part-way leaves the list as it was, and that a long list saves whole.
///
/// SQLite runs against a throwaway in-memory database per test (shared-cache, kept alive by an anchor connection so
/// that a second store instance reads what the first one wrote, as a region restart does). No files are written.
/// A failure part-way is made with a test-only trigger that aborts the insert of one chosen id.
/// </summary>
public class SQLiteEstateListSaveTests : OpenSimTestCase
{
    private static readonly UUID s_region = new UUID("2e7c4a91-6d3b-4f08-9a5e-c1b8f0d6e324");
    private static readonly UUID s_owner = new UUID("8f1d3c6a-04e2-4b97-a1c5-7e9b2d4f6a80");

    private readonly List<IDisposable> m_disposables = new();

    static SQLiteEstateListSaveTests()
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

    private SQLiteConnection m_anchor;

    private string NewMemoryDatabase()
    {
        string conn = "FullUri=file:estatelists_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        m_anchor = new SQLiteConnection(conn);
        m_anchor.Open();
        m_disposables.Add(m_anchor);
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

    /// <summary>One of the lists the store saves, how to set it on an estate, and how to read it back.</summary>
    public enum EstateList { Bans, Managers, Users, Groups }

    private static string TableOf(EstateList list) => list switch
    {
        EstateList.Bans => "estateban",
        EstateList.Managers => "estate_managers",
        EstateList.Users => "estate_users",
        _ => "estate_groups",
    };

    private static string IdColumnOf(EstateList list) => list == EstateList.Bans ? "bannedUUID" : "uuid";

    private static void Set(EstateSettings es, EstateList list, UUID[] ids)
    {
        switch (list)
        {
            case EstateList.Bans:
                es.ClearBans();
                foreach (UUID id in ids)
                    es.AddBan(new EstateBan { BannedUserID = id, BanningUserID = s_owner, EstateID = es.EstateID, BanTime = 1700000000 });
                break;
            case EstateList.Managers: es.EstateManagers = ids; break;
            case EstateList.Users: es.EstateAccess = ids; break;
            default: es.EstateGroups = ids; break;
        }
    }

    private static UUID[] Get(EstateSettings es, EstateList list) => list switch
    {
        EstateList.Bans => es.EstateBans.Select(b => b.BannedUserID).ToArray(),
        EstateList.Managers => es.EstateManagers,
        EstateList.Users => es.EstateAccess,
        _ => es.EstateGroups,
    };

    private static UUID[] NewIds(int count)
    {
        UUID[] ids = new UUID[count];
        for (int i = 0; i < count; i++)
            ids[i] = UUID.Random();
        return ids;
    }

    // Reads the estate back through a second store on the same database, as the region does after a restart.
    private EstateSettings Reload(string conn) => OpenStore(conn).LoadEstateSettings(s_region, false);

    [Fact]
    public void EachListReadsBackAsSaved()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        EstateSettings es = store.LoadEstateSettings(s_region, true);

        Dictionary<EstateList, UUID[]> saved = new();
        foreach (EstateList list in Enum.GetValues<EstateList>())
        {
            saved[list] = NewIds(3);
            Set(es, list, saved[list]);
        }
        es.Save();

        EstateSettings reloaded = Reload(conn);
        foreach (EstateList list in Enum.GetValues<EstateList>())
            Assert.Equal(new HashSet<UUID>(saved[list]), new HashSet<UUID>(Get(reloaded, list)));
    }

    [Fact]
    public void ASecondSaveReplacesEachList()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        EstateSettings es = store.LoadEstateSettings(s_region, true);
        foreach (EstateList list in Enum.GetValues<EstateList>())
            Set(es, list, NewIds(4));
        es.Save();

        Dictionary<EstateList, UUID[]> second = new();
        foreach (EstateList list in Enum.GetValues<EstateList>())
        {
            second[list] = NewIds(2);
            Set(es, list, second[list]);
        }
        es.Save();

        EstateSettings reloaded = Reload(conn);
        foreach (EstateList list in Enum.GetValues<EstateList>())
            Assert.Equal(new HashSet<UUID>(second[list]), new HashSet<UUID>(Get(reloaded, list)));
    }

    [Theory]
    [InlineData(EstateList.Bans)]
    [InlineData(EstateList.Managers)]
    [InlineData(EstateList.Users)]
    [InlineData(EstateList.Groups)]
    public void ASaveThatFailsPartWayLeavesTheListAsItWas(EstateList list)
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        EstateSettings es = store.LoadEstateSettings(s_region, true);
        UUID[] before = NewIds(3);
        Set(es, list, before);
        es.Save();

        // The second id of the new list cannot be inserted: the delete and the first insert have already run.
        UUID poison = UUID.Random();
        using (SQLiteCommand cmd = m_anchor.CreateCommand())
        {
            cmd.CommandText = $"CREATE TRIGGER fail_one_insert BEFORE INSERT ON {TableOf(list)} " +
                $"WHEN NEW.{IdColumnOf(list)} = '{poison}' BEGIN SELECT RAISE(ABORT, 'test: insert refused'); END;";
            cmd.ExecuteNonQuery();
        }

        Set(es, list, new[] { UUID.Random(), poison, UUID.Random() });
        Assert.Throws<SQLiteException>(() => es.Save());

        Assert.Equal(new HashSet<UUID>(before), new HashSet<UUID>(Get(Reload(conn), list)));
    }

    [Fact]
    public void AStoreKeepsWorkingAfterAFailedSave()
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        EstateSettings es = store.LoadEstateSettings(s_region, true);

        UUID poison = UUID.Random();
        using (SQLiteCommand cmd = m_anchor.CreateCommand())
        {
            cmd.CommandText = $"CREATE TRIGGER fail_one_insert BEFORE INSERT ON estate_users " +
                $"WHEN NEW.uuid = '{poison}' BEGIN SELECT RAISE(ABORT, 'test: insert refused'); END;";
            cmd.ExecuteNonQuery();
        }
        es.EstateAccess = new[] { poison };
        Assert.Throws<SQLiteException>(() => es.Save());

        UUID[] after = NewIds(2);
        es.EstateAccess = after;
        es.Save();

        Assert.Equal(new HashSet<UUID>(after), new HashSet<UUID>(Reload(conn).EstateAccess));
    }

    [Theory]
    [InlineData(EstateList.Bans)]
    [InlineData(EstateList.Users)]
    public void ALongListSavesWhole(EstateList list)
    {
        string conn = NewMemoryDatabase();
        SQLiteEstateStore store = OpenStore(conn);
        EstateSettings es = store.LoadEstateSettings(s_region, true);
        UUID[] ids = NewIds(500);
        Set(es, list, ids);
        es.Save();

        UUID[] reloaded = Get(Reload(conn), list);
        Assert.Equal(ids.Length, reloaded.Length);
        Assert.Equal(new HashSet<UUID>(ids), new HashSet<UUID>(reloaded));
    }
}
