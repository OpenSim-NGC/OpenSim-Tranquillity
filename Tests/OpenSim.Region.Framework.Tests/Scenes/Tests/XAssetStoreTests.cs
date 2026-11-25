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
/// Stores assets through the XAssets stores and reads them back. The MySQL store began a transaction and ran its
/// commands outside it; MySqlConnector refuses such a command ("The transaction associated with this command is not
/// the connection's active transaction"), the store logged the error and rolled back, and no asset was ever written.
///
/// Each test gets a new database on the server named by the environment, dropped when it ends. No process-wide
/// state: each test drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class XAssetStoreTests : IDisposable
{
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
    public void MySQL_StoredAssets_CanBeRead()
    {
        string conn = NewMySQLDatabase();
        MySQLXAssetData store = new();
        store.Initialise(conn);

        Check(store, () => Convert.ToInt32(MySQLScalar(conn, "SELECT COUNT(*) FROM XAssetsData")));
    }

    [PGSQLServerFact]
    public void PGSQL_StoredAssets_CanBeRead()
    {
        string conn = NewPGSQLDatabase();
        PGSQLXAssetData store = new();
        store.Initialise(conn);

        Check(store, () => Convert.ToInt32(PGSQLScalar(conn, "SELECT COUNT(*) FROM xassetsdata")));
    }

    /// <summary>
    /// Two assets with the same content share one data row; storing an id again replaces its metadata.
    /// </summary>
    private static void Check(IXAssetDataPlugin store, Func<int> dataRows)
    {
        byte[] content = { 1, 2, 3, 4 };
        AssetBase first = Asset("first", content);
        AssetBase second = Asset("second", content);
        AssetBase other = Asset("other", new byte[] { 5, 6, 7 });

        store.StoreAsset(first);
        store.StoreAsset(second);
        store.StoreAsset(other);

        AssetBase read = store.GetAsset(first.FullID);
        Assert.NotNull(read);
        Assert.Equal("first", read.Name);
        Assert.Equal((sbyte)AssetType.Notecard, read.Type);
        Assert.Equal(content, read.Data);
        Assert.Equal(content, store.GetAsset(second.FullID)?.Data);
        Assert.Equal(other.Data, store.GetAsset(other.FullID)?.Data);
        Assert.Equal(2, dataRows());

        first.Name = "first, again";
        store.StoreAsset(first);
        Assert.Equal("first, again", store.GetAsset(first.FullID)?.Name);
        Assert.Equal(2, dataRows());
    }

    private static AssetBase Asset(string name, byte[] data) =>
        new(UUID.Random(), name, (sbyte)AssetType.Notecard, UUID.Random().ToString()) { Data = data };

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "xasset_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        MySQLScalar(admin, $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            MySQLScalar(admin, $"DROP DATABASE IF EXISTS `{name}`");
        });
        return conn;
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
