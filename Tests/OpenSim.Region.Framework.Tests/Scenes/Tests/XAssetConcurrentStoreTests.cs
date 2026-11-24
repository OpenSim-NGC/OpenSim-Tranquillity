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
using System.Security.Cryptography;
using System.Threading.Tasks;
using MySqlConnector;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Two XAssets stores of the same new bytes, and a store whose lookup of the data row fails.
///
/// A store writes the metadata row, looks up the data row by the hash of the bytes and inserts it when the lookup
/// finds none. A second store of the same bytes, on another connection or in another process, can insert that hash
/// between the lookup and the insert. The insert then hit the primary key, the store rolled its metadata back and
/// returned, and its id was gone. The race test opens that gap on purpose: a second connection inserts the same
/// bytes and holds its transaction open while the store runs, so the store's insert waits for it.
///
/// On MySQL the lookup now runs before the store's transaction begins. Inside it, the lookup fixed the transaction's
/// snapshot, and MariaDB 11.6.2 and later (innodb_snapshot_isolation, on by default) then refused the insert with
/// error 1020 once the other store committed. The insert is a plain INSERT, so a login needs no UPDATE privilege on
/// the data table.
///
/// A lookup that failed was logged and taken as "no data row", and the store went on to insert.
///
/// Each test gets a new database on the server named by the environment, dropped when it ends. No process-wide
/// state: each test drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class XAssetConcurrentStoreTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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
    public async Task MySQL_SameBytesStoredAtOnce_BothAssetsRemain()
    {
        (string conn, string name) = NewMySQLDatabase();
        MySQLXAssetData store = new();
        store.Initialise(conn);

        await MySQLStoreSameBytesAtOnce(store, conn, name);
    }

    /// <summary>
    /// A login with only the privileges develop's store needs: SELECT, INSERT and DELETE (REPLACE INTO is an insert
    /// and a delete) on the metadata table, SELECT and INSERT on the data table. No UPDATE. New bytes, bytes that are
    /// already stored and two stores of the same new bytes at once all store.
    /// </summary>
    [MySQLServerFact]
    public async Task MySQL_StoreNeedsNoUpdatePrivilege()
    {
        (string conn, string name) = NewMySQLDatabase();
        new MySQLXAssetData().Initialise(conn);
        string limited = LimitedMySQLLogin(conn, name,
            $"GRANT SELECT, INSERT, DELETE ON `{name}`.`XAssetsMeta` TO '{{0}}'@'%'",
            $"GRANT SELECT, INSERT ON `{name}`.`XAssetsData` TO '{{0}}'@'%'");
        MySQLXAssetData store = new();
        store.Initialise(limited);

        byte[] content = Content();
        AssetBase first = Asset("first", content);
        AssetBase second = Asset("second", content);
        store.StoreAsset(first);
        store.StoreAsset(second);
        Assert.Equal(content, store.GetAsset(first.FullID)?.Data);
        Assert.Equal(content, store.GetAsset(second.FullID)?.Data);

        await MySQLStoreSameBytesAtOnce(store, conn, name);
    }

    /// <summary>
    /// Another connection, on <paramref name="conn"/>, inserts an asset with new bytes and holds its transaction open
    /// while <paramref name="store"/> stores another asset with the same bytes; it commits once the store's insert
    /// waits on its row. Both assets must read back, on one data row for those bytes.
    /// </summary>
    private static async Task MySQLStoreSameBytesAtOnce(MySQLXAssetData store, string conn, string name)
    {
        byte[] content = Content();
        AssetBase mine = Asset("mine", content);
        UUID theirs = UUID.Random();

        using MySqlConnection other = new(conn);
        other.Open();
        using (MySqlTransaction transaction = other.BeginTransaction())
        {
            using (MySqlCommand cmd = new(
                "INSERT INTO XAssetsMeta(ID, Hash, Name, Description, AssetType, Local, Temporary, CreateTime, AccessTime, AssetFlags, CreatorID) " +
                "VALUES(?ID, ?Hash, 'theirs', '', 7, 0, 0, 0, 0, 0, '');" +
                "INSERT INTO XAssetsData(Hash, Data) VALUES(?Hash, ?Data)", other, transaction))
            {
                cmd.Parameters.AddWithValue("?ID", theirs.ToString());
                cmd.Parameters.AddWithValue("?Hash", SHA256.HashData(content));
                cmd.Parameters.AddWithValue("?Data", content);
                cmd.ExecuteNonQuery();
            }

            Task storing = Task.Run(() => store.StoreAsset(mine));

            // The store's lookup does not see the other connection's row, so its insert waits on that row's lock.
            await WaitFor(() => Convert.ToInt32(MySQLScalar(conn,
                "SELECT COUNT(*) FROM information_schema.PROCESSLIST " +
                $"WHERE DB = '{name}' AND INFO LIKE 'INSERT INTO XAssetsData%'")) > 0);

            transaction.Commit();
            await storing.WaitAsync(Timeout);
        }

        Assert.Equal(content, store.GetAsset(theirs)?.Data);
        Assert.Equal(content, store.GetAsset(mine.FullID)?.Data);
        Assert.Equal(1, Convert.ToInt32(MySQLScalar(conn,
            "SELECT COUNT(*) FROM XAssetsData WHERE Hash = UNHEX('" + Convert.ToHexString(SHA256.HashData(content)) + "')")));
    }

    [PGSQLServerFact]
    public async Task PGSQL_SameBytesStoredAtOnce_BothAssetsRemain()
    {
        string conn = NewPGSQLDatabase();
        PGSQLXAssetData store = new();
        store.Initialise(conn);

        byte[] content = Content();
        AssetBase mine = Asset("mine", content);
        UUID theirs = UUID.Random();

        using NpgsqlConnection other = new(conn);
        other.Open();
        using (NpgsqlTransaction transaction = other.BeginTransaction())
        {
            using (NpgsqlCommand cmd = new(
                @"INSERT INTO xassetsmeta(id, hash, name, description, ""AssetType"", local, temporary, create_time, access_time, asset_flags, creatorid)
                  VALUES(@id, @hash, 'theirs', '', 7, false, false, 0, 0, 0, '');
                  INSERT INTO xassetsdata(hash, data) VALUES(@hash, @data)", other, transaction))
            {
                cmd.Parameters.AddWithValue("id", Guid.Parse(theirs.ToString()));
                cmd.Parameters.AddWithValue("hash", SHA256.HashData(content));
                cmd.Parameters.AddWithValue("data", content);
                cmd.ExecuteNonQuery();
            }

            Task storing = Task.Run(() => store.StoreAsset(mine));

            // The store's lookup does not see the other connection's row, so its insert waits on that row's lock.
            await WaitFor(() => Convert.ToInt32(PGSQLScalar(conn,
                "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")) > 0);

            transaction.Commit();
            await storing.WaitAsync(Timeout);
        }

        Assert.Equal(content, store.GetAsset(theirs)?.Data);
        Assert.Equal(content, store.GetAsset(mine.FullID)?.Data);
        Assert.Equal(1, Convert.ToInt32(PGSQLScalar(conn, "SELECT COUNT(*) FROM xassetsdata")));
    }

    /// <summary>
    /// A login that may write the metadata and insert data rows but not read the data table: the lookup fails. The
    /// store must not take that as "no data row" and write anyway.
    /// </summary>
    [MySQLServerFact]
    public void MySQL_FailedDataLookup_StoresNothing()
    {
        (string conn, string name) = NewMySQLDatabase();
        new MySQLXAssetData().Initialise(conn);

        string limited = LimitedMySQLLogin(conn, name,
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{name}`.`XAssetsMeta` TO '{{0}}'@'%'",
            $"GRANT INSERT ON `{name}`.`XAssetsData` TO '{{0}}'@'%'");
        MySQLXAssetData store = new();
        store.Initialise(limited);

        AssetBase asset = Asset("unchecked", Content());
        store.StoreAsset(asset);

        Assert.Equal(0, Convert.ToInt32(MySQLScalar(conn, $"SELECT COUNT(*) FROM XAssetsMeta WHERE ID = '{asset.ID}'")));
        Assert.Equal(0, Convert.ToInt32(MySQLScalar(conn, "SELECT COUNT(*) FROM XAssetsData")));
    }

    /// <summary>
    /// As <see cref="MySQL_FailedDataLookup_StoresNothing"/>. On PostgreSQL the failed lookup ends the transaction,
    /// so the insert after it failed too; this pins that nothing is written.
    /// </summary>
    [PGSQLServerFact]
    public void PGSQL_FailedDataLookup_StoresNothing()
    {
        string conn = NewPGSQLDatabase();
        new PGSQLXAssetData().Initialise(conn);

        string role = "xa" + Guid.NewGuid().ToString("N").Substring(0, 12);
        string password = Guid.NewGuid().ToString("N");
        PGSQLScalar(conn, $"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
        m_cleanup.Add(() =>
        {
            PGSQLScalar(conn, $"DROP OWNED BY {role}");
            PGSQLScalar(conn, $"DROP ROLE IF EXISTS {role}");
        });
        PGSQLScalar(conn, $"GRANT SELECT, INSERT, UPDATE, DELETE ON xassetsmeta TO {role}");
        PGSQLScalar(conn, $"GRANT INSERT ON xassetsdata TO {role}");
        PGSQLScalar(conn, $"GRANT SELECT ON migrations TO {role}");

        string limited = new NpgsqlConnectionStringBuilder(conn) { Username = role, Password = password }.ConnectionString;
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(limited))
                NpgsqlConnection.ClearPool(c);
        });
        PGSQLXAssetData store = new();
        store.Initialise(limited);

        AssetBase asset = Asset("unchecked", Content());
        store.StoreAsset(asset);

        Assert.Equal(0, Convert.ToInt32(PGSQLScalar(conn, $"SELECT COUNT(*) FROM xassetsmeta WHERE id = '{asset.ID}'")));
        Assert.Equal(0, Convert.ToInt32(PGSQLScalar(conn, "SELECT COUNT(*) FROM xassetsdata")));
    }

    /// <summary>
    /// A new login on the server of <paramref name="conn"/> that may read the migrations table and has the grants
    /// given (each with {0} for the user name); dropped when the test ends. Returns its connection string.
    /// </summary>
    private string LimitedMySQLLogin(string conn, string name, params string[] grants)
    {
        string user = "xa" + Guid.NewGuid().ToString("N").Substring(0, 12);
        string password = Guid.NewGuid().ToString("N");
        string admin = Admin(conn);
        MySQLScalar(admin, $"CREATE USER '{user}'@'%' IDENTIFIED BY '{password}'");
        m_cleanup.Add(() => MySQLScalar(admin, $"DROP USER IF EXISTS '{user}'@'%'"));
        foreach (string grant in grants)
            MySQLScalar(admin, string.Format(grant, user));
        MySQLScalar(admin, $"GRANT SELECT ON `{name}`.`migrations` TO '{user}'@'%'");

        string limited = new MySqlConnectionStringBuilder(conn) { UserID = user, Password = password, AllowPublicKeyRetrieval = true }.ConnectionString;
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(limited))
                MySqlConnection.ClearPool(c);
        });
        return limited;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "The store never waited on the other connection's row");
            await Task.Delay(20);
        }
    }

    private static byte[] Content()
    {
        byte[] content = new byte[1024];
        Random.Shared.NextBytes(content);
        return content;
    }

    private static AssetBase Asset(string name, byte[] data) =>
        new(UUID.Random(), name, (sbyte)AssetType.Notecard, UUID.Random().ToString()) { Data = data };

    private static string Admin(string conn) =>
        new MySqlConnectionStringBuilder(conn) { Database = "", Pooling = false }.ConnectionString;

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string and name.</summary>
    private (string, string) NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "xasset_" + Guid.NewGuid().ToString("N");
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        string admin = Admin(conn);

        MySQLScalar(admin, $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            MySQLScalar(admin, $"DROP DATABASE IF EXISTS `{name}`");
        });
        return (conn, name);
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "xasset_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        PGSQLScalar(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            PGSQLScalar(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        });
        return conn;
    }

    private static object MySQLScalar(string conn, string sql)
    {
        using MySqlConnection c = new MySqlConnection(conn);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }

    private static object PGSQLScalar(string conn, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }
}
