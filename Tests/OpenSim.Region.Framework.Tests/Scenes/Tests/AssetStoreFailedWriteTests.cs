/*
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
using MySqlConnector;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Stores an asset that the database refuses. StoreAsset answers whether the asset was stored, and the asset
/// service answers the zero UUID when it was not. The PostgreSQL store logged the database error and still answered
/// true, so the service handed out the id of an asset that was never written.
///
/// The database refuses the write through a CHECK constraint the test adds to the assets table after the store has
/// created it. Each test gets a new database on the server named by the environment, dropped when it ends. No
/// process-wide state: each test drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class AssetStoreFailedWriteTests : IDisposable
{
    private const string Refused = "refused by the database";

    private readonly List<Action> m_cleanup = new();

    public void Dispose()
    {
        for (int i = m_cleanup.Count - 1; i >= 0; i--)
        {
            try { m_cleanup[i](); } catch { /* best effort */ }
        }
        m_cleanup.Clear();
    }

    [PGSQLServerFact]
    public void PGSQL_RefusedWrite_ReturnsFalse()
    {
        string conn = NewPGSQLDatabase();
        PGSQLAssetData store = new();
        store.Initialise(conn);
        PGSQLAdmin(conn, $"ALTER TABLE assets ADD CONSTRAINT refuse_name CHECK (name <> '{Refused}')");

        Check(store);
    }

    [MySQLServerFact]
    public void MySQL_RefusedWrite_ReturnsFalse()
    {
        string conn = NewMySQLDatabase();
        MySQLAssetData store = new();
        store.Initialise(conn);
        MySQLAdmin(conn, $"ALTER TABLE assets ADD CONSTRAINT refuse_name CHECK (name <> '{Refused}')");

        Check(store);
    }

    private static void Check(AssetDataBase store)
    {
        AssetBase stored = Asset("stored");
        AssetBase refused = Asset(Refused);

        Assert.True(store.StoreAsset(stored));
        Assert.False(store.StoreAsset(refused));

        Assert.NotNull(store.GetAsset(stored.FullID));
        Assert.Null(store.GetAsset(refused.FullID));
    }

    private static AssetBase Asset(string name) =>
        new(UUID.Random(), name, (sbyte)AssetType.Notecard, UUID.Random().ToString()) { Data = new byte[] { 1, 2, 3 } };

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "assetfail_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        PGSQLAdmin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            PGSQLAdmin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        });
        return conn;
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "assetfail_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        MySQLAdmin(admin, $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            MySQLAdmin(admin, $"DROP DATABASE IF EXISTS `{name}`");
        });
        return conn;
    }

    private static void PGSQLAdmin(string conn, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static void MySQLAdmin(string conn, string sql)
    {
        using MySqlConnection c = new MySqlConnection(conn);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
