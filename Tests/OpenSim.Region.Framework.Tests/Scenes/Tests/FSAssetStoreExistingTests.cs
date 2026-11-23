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
/// Stores an FSAssets row for an id that already has one. Store answers whether the asset is stored, and the
/// service answers the zero UUID, a failure, when it is not. The MySQL store keeps the row and answers true. The
/// PostgreSQL store kept the row and answered false, so storing an asset a second time failed although it was there.
///
/// Each test gets a new database on the server named by the environment, dropped when it ends. No process-wide
/// state: each test drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class FSAssetStoreExistingTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_StoreAgain_IsStored()
    {
        PGSQLFSAssetData store = new();
        store.Initialise(NewPGSQLDatabase(), "fsassets", 0);
        Check(store);
    }

    [MySQLServerFact]
    public void MySQL_StoreAgain_IsStored()
    {
        MySQLFSAssetData store = new();
        store.Initialise(NewMySQLDatabase(), "fsassets", 0);
        Check(store);
    }

    private static void Check(IFSAssetDataPlugin store)
    {
        UUID id = UUID.Random();
        AssetMetadata meta = new() { FullID = id, ID = id.ToString(), Name = "stored", Description = "", Type = (sbyte)AssetType.Notecard };
        string hash = Util.SHA256Hash(new byte[] { 1, 2, 3 });

        Assert.True(store.Store(meta, hash));
        Assert.True(store.Store(meta, hash));

        // Another hash for the same id: the row is kept as it is.
        Assert.True(store.Store(meta, Util.SHA256Hash(new byte[] { 4, 5, 6 })));

        Assert.NotNull(store.Get(meta.ID, out string stored));
        Assert.Equal(hash, stored);
        Assert.Equal(1, store.Count());
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "fsagain_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        MySQLAdmin(admin, $"CREATE DATABASE `{name}`");
        m_cleanup = () =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            MySQLAdmin(admin, $"DROP DATABASE IF EXISTS `{name}`");
        };
        return conn;
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "fsagain_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        PGSQLAdmin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup = () =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            PGSQLAdmin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        };
        return conn;
    }

    private static void MySQLAdmin(string admin, string sql)
    {
        using MySqlConnection c = new MySqlConnection(admin);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static void PGSQLAdmin(string admin, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(admin);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
