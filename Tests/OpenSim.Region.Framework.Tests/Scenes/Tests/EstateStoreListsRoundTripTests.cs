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
using System.Reflection;
using MySqlConnector;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Runs only when <see cref="EnvVar"/> holds a connection string for a PostgreSQL server (no database named) on
/// which the test may create and drop its own databases. Unset, the test is reported as skipped, never passed.
/// </summary>
public sealed class PGSQLServerFactAttribute : FactAttribute
{
    public const string EnvVar = "OPENSIM_TEST_PGSQL";

    public PGSQLServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Requires a PostgreSQL server; set {EnvVar} to run.";
    }
}

/// <summary>
/// Runs only when <see cref="EnvVar"/> holds a connection string for a MySQL or MariaDB server (no database named)
/// on which the test may create and drop its own databases. Unset, the test is reported as skipped, never passed.
/// </summary>
public sealed class MySQLServerFactAttribute : FactAttribute
{
    public const string EnvVar = "OPENSIM_TEST_MYSQL";

    public MySQLServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Requires a MySQL or MariaDB server; set {EnvVar} to run.";
    }
}

/// <summary>
/// Saves an estate with a ban, a manager, an allowed user and an allowed group through one estate store, loads it
/// back through a second store on the same database (as a region does after a restart) and compares every stored
/// value of each list: for the ban the banned avatar, the banning avatar and the ban time, the three ban values
/// the estateban table keeps for an avatar ban. A second test saves bans, reloads them through a new store, saves
/// the loaded estate again and loads it once more: the stores do not set EstateBan.EstateID when they load a ban,
/// so a save must file each ban under the estate being saved.
///
/// SQLite uses a throwaway in-memory database. PostgreSQL and MySQL each get a new database on the server named by
/// the environment, dropped when the test ends. No process-wide state: each test has its own database and drops
/// only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class EstateStoreListsRoundTripTests : IDisposable
{
    private readonly List<Action> m_cleanup = new();

    static EstateStoreListsRoundTripTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public void Dispose()
    {
        for (int i = m_cleanup.Count - 1; i >= 0; i--)
        {
            try { m_cleanup[i](); } catch { /* best effort */ }
        }
        m_cleanup.Clear();
    }

    [Fact]
    public void SQLite_EstateWithABan_ReadsBackEveryList() => RoundTrip(SQLiteStores());

    [PGSQLServerFact]
    public void PGSQL_EstateWithABan_ReadsBackEveryList() => RoundTrip(PGSQLStores());

    [MySQLServerFact]
    public void MySQL_EstateWithABan_ReadsBackEveryList() => RoundTrip(MySQLStores());

    [Fact]
    public void SQLite_LoadedBans_SurviveASecondSave() => ResaveRoundTrip(SQLiteStores());

    [PGSQLServerFact]
    public void PGSQL_LoadedBans_SurviveASecondSave() => ResaveRoundTrip(PGSQLStores());

    [MySQLServerFact]
    public void MySQL_LoadedBans_SurviveASecondSave() => ResaveRoundTrip(MySQLStores());

    /// <summary>Opens a new SQLite estate store on one throwaway in-memory database per call of this method.</summary>
    private Func<IEstateDataStore> SQLiteStores()
    {
        string conn = "FullUri=file:estatert_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        // Shared-cache memory databases live while a connection is open; this one keeps it for the later stores.
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_cleanup.Add(anchor.Dispose);

        return () =>
        {
            SQLiteEstateStore store = new SQLiteEstateStore(conn);
            // The store has no close of its own: its connection lives as long as the region process does.
            FieldInfo field = typeof(SQLiteEstateStore).GetField("m_connection", BindingFlags.NonPublic | BindingFlags.Instance);
            SQLiteConnection c = (SQLiteConnection)field?.GetValue(store);
            if (c != null)
                m_cleanup.Add(c.Dispose);
            return store;
        };
    }

    /// <summary>Creates a new database on the PostgreSQL server and opens estate stores on it.</summary>
    private Func<IEstateDataStore> PGSQLStores()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "estatert_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        PGSQLAdmin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            PGSQLAdmin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        });

        return () =>
        {
            PGSQLEstateStore store = new PGSQLEstateStore();
            store.Initialise(conn);
            return store;
        };
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and opens estate stores on it.</summary>
    private Func<IEstateDataStore> MySQLStores()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "estatert_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        MySQLAdmin(admin, $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            MySQLAdmin(admin, $"DROP DATABASE IF EXISTS `{name}`");
        });

        return () =>
        {
            MySQLEstateStore store = new MySQLEstateStore();
            store.Initialise(conn);
            return store;
        };
    }

    private static void PGSQLAdmin(string admin, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(admin);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static void MySQLAdmin(string admin, string sql)
    {
        using MySqlConnection c = new MySqlConnection(admin);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static void RoundTrip(Func<IEstateDataStore> openStore)
    {
        UUID region = UUID.Random();
        UUID owner = UUID.Random();
        UUID banned = UUID.Random();
        UUID banning = UUID.Random();
        UUID manager = UUID.Random();
        UUID user = UUID.Random();
        UUID group = UUID.Random();
        const int banTime = 1700000123;

        IEstateDataStore writer = openStore();
        EstateSettings saved = writer.LoadEstateSettings(region, true);
        saved.EstateName = "Example Estate";
        saved.EstateOwner = owner;
        saved.AddBan(new EstateBan { EstateID = saved.EstateID, BannedUserID = banned, BanningUserID = banning, BanTime = banTime });
        saved.AddEstateManager(manager);
        saved.AddEstateUser(user);
        saved.AddEstateGroup(group);
        writer.StoreEstateSettings(saved);
        writer.LinkRegion(region, (int)saved.EstateID);

        EstateSettings loaded = openStore().LoadEstateSettings(region, false);

        Assert.NotNull(loaded);
        Assert.Equal(saved.EstateID, loaded.EstateID);
        Assert.Equal("Example Estate", loaded.EstateName);
        Assert.Equal(owner, loaded.EstateOwner);

        EstateBan ban = Assert.Single(loaded.EstateBans);
        Assert.Equal(banned, ban.BannedUserID);
        Assert.Equal(banning, ban.BanningUserID);
        Assert.Equal(banTime, ban.BanTime);

        Assert.Equal(new[] { manager }, loaded.EstateManagers);
        Assert.Equal(new[] { user }, loaded.EstateAccess);
        Assert.Equal(new[] { group }, loaded.EstateGroups);
    }

    private static void ResaveRoundTrip(Func<IEstateDataStore> openStore)
    {
        UUID firstRegion = UUID.Random();
        UUID region = UUID.Random();
        UUID banned = UUID.Random();
        UUID banned2 = UUID.Random();
        UUID banning = UUID.Random();

        IEstateDataStore writer = openStore();
        // Another estate is created first, so the estate under test does not have id 1, the id a new EstateBan has.
        EstateSettings first = writer.LoadEstateSettings(firstRegion, true);
        writer.LinkRegion(firstRegion, (int)first.EstateID);
        EstateSettings saved = writer.LoadEstateSettings(region, true);
        Assert.NotEqual(1u, saved.EstateID);
        saved.EstateName = "Example Estate";
        saved.AddBan(new EstateBan { EstateID = saved.EstateID, BannedUserID = banned, BanningUserID = banning, BanTime = 1700000201 });
        saved.AddBan(new EstateBan { EstateID = saved.EstateID, BannedUserID = banned2, BanningUserID = banning, BanTime = 1700000202 });
        writer.StoreEstateSettings(saved);
        writer.LinkRegion(region, (int)saved.EstateID);

        // As after a restart: a new store loads the estate, and the loaded estate is saved again unchanged.
        IEstateDataStore restarted = openStore();
        EstateSettings loaded = restarted.LoadEstateSettings(region, false);
        Assert.Equal(2, loaded.EstateBans.Length);
        restarted.StoreEstateSettings(loaded);

        IEstateDataStore reader = openStore();
        EstateBan[] bans = reader.LoadEstateSettings(region, false).EstateBans.OrderBy(b => b.BanTime).ToArray();
        Assert.Equal(2, bans.Length);
        Assert.Equal(banned, bans[0].BannedUserID);
        Assert.Equal(banning, bans[0].BanningUserID);
        Assert.Equal(1700000201, bans[0].BanTime);
        Assert.Equal(banned2, bans[1].BannedUserID);
        Assert.Equal(banning, bans[1].BanningUserID);
        Assert.Equal(1700000202, bans[1].BanTime);

        // The bans must not have been filed under another estate either.
        Assert.Empty(reader.LoadEstateSettings(firstRegion, false).EstateBans);
    }
}
