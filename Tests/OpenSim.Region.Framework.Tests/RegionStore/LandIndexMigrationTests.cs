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
using System.Data.Common;
using System.Data.SQLite;
using MySqlConnector;
using Npgsql;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes.Tests;
using Xunit;

namespace OpenSim.Region.Framework.RegionStore.Tests;

/// <summary>
/// After the region store's migrations, landaccesslist has an index on LandUUID and land one on RegionUUID. The
/// stores read and replace a parcel's access list by LandUUID for every parcel, and MySQL and PostgreSQL load a
/// region's parcels by RegionUUID; without the indexes each of those reads the whole table, once per parcel.
///
/// SQLite runs on a throwaway in-memory database. MySQL and PostgreSQL are opt-in (OPENSIM_TEST_MYSQL,
/// OPENSIM_TEST_PGSQL) and each test gets a new database, dropped when it ends. No process-wide state.
/// </summary>
public class LandIndexMigrationTests : IDisposable
{
    private readonly List<Action> m_cleanup = new();

    static LandIndexMigrationTests()
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
    public void SQLite_Migrations_IndexLandAccessListAndLand()
    {
        string conn = "FullUri=file:landindex_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        SQLiteConnection anchor = new SQLiteConnection(conn);
        anchor.Open();
        m_cleanup.Add(anchor.Dispose);

        SQLiteSimulationData store = new SQLiteSimulationData(conn);
        m_cleanup.Add(store.Dispose);

        // pragma_index_info lists the columns of the index named, in order.
        const string sql =
            "SELECT count(*) FROM sqlite_master m WHERE m.type = 'index' AND m.tbl_name = @table " +
            "AND (SELECT group_concat(name) FROM pragma_index_info(m.name)) = @column";
        Assert.True(HasIndex(anchor, sql, "landaccesslist", "LandUUID"), "landaccesslist has no index on LandUUID");
        Assert.True(HasIndex(anchor, sql, "land", "RegionUUID"), "land has no index on RegionUUID");
    }

    [MySQLServerFact]
    public void MySQL_Migrations_IndexLandAccessListAndLand()
    {
        string conn = NewMySQLDatabase();
        new MySQLSimulationData(conn);

        using MySqlConnection c = new MySqlConnection(conn);
        c.Open();
        // An index whose first column is the one named, as a lookup by that column needs.
        const string sql =
            "SELECT count(*) FROM information_schema.statistics WHERE table_schema = DATABASE() " +
            "AND table_name = @table AND column_name = @column AND seq_in_index = 1";
        Assert.True(HasIndex(c, sql, "landaccesslist", "LandUUID"), "landaccesslist has no index on LandUUID");
        Assert.True(HasIndex(c, sql, "land", "RegionUUID"), "land has no index on RegionUUID");
    }

    [PGSQLServerFact]
    public void PGSQL_Migrations_IndexLandAccessListAndLand()
    {
        string conn = NewPGSQLDatabase();
        new PGSQLSimulationData().Initialise(conn);

        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        // An index whose first column is the one named, as a lookup by that column needs.
        const string sql =
            "SELECT count(*) FROM pg_index i JOIN pg_class t ON t.oid = i.indrelid " +
            "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0] " +
            "WHERE t.relname = @table AND a.attname = @column";
        Assert.True(HasIndex(c, sql, "landaccesslist", "LandUUID"), "landaccesslist has no index on LandUUID");
        Assert.True(HasIndex(c, sql, "land", "RegionUUID"), "land has no index on RegionUUID");
    }

    private static bool HasIndex(DbConnection conn, string sql, string table, string column)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        DbParameter t = cmd.CreateParameter();
        t.ParameterName = "@table";
        t.Value = table;
        cmd.Parameters.Add(t);
        DbParameter col = cmd.CreateParameter();
        col.ParameterName = "@column";
        col.Value = column;
        cmd.Parameters.Add(col);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "landindex_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Admin(new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`");
        });
        return conn;
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "landindex_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Admin(new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\"");
        });
        return conn;
    }

    private static void Admin(DbConnection conn, string sql)
    {
        using (conn)
        {
            conn.Open();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
