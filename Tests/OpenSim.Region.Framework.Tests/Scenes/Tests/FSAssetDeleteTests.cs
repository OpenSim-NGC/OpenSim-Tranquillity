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
/// Deletes FSAssets rows. Delete answers whether its statement ran: a stored id and a missing one both answer true.
/// On PostgreSQL the id column is a uuid, and an id that is not a UUID threw from new UUID(id) before the query, so
/// the caller saw an exception; it now deletes nothing and answers false. On MySQL a statement the database refuses
/// answered true; it now answers false. A MySQL id that is not a UUID still runs and deletes nothing.
///
/// Each test gets a new database on the server named by the environment, dropped when it ends. No process-wide
/// state: each test drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class FSAssetDeleteTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_Delete()
    {
        string conn = NewPGSQLDatabase();
        PGSQLFSAssetData store = new();
        store.Initialise(conn, "fsassets", 0);
        DeleteStoredAndMissing(store);

        Assert.False(store.Delete("nope"));
        Assert.Equal(1, store.Count());
    }

    [MySQLServerFact]
    public void MySQL_Delete()
    {
        string conn = NewMySQLDatabase();
        MySQLFSAssetData store = new();
        store.Initialise(conn, "fsassets", 0);
        DeleteStoredAndMissing(store);

        Assert.True(store.Delete("nope"));
        Assert.Equal(1, store.Count());

        // A statement the database refuses: the table is gone.
        MySQLAdmin(conn, "RENAME TABLE fsassets TO fsassets_moved");
        Assert.False(store.Delete(UUID.Random().ToString()));
    }

    /// <summary>Stores two assets, deletes one and a missing id; one row is left.</summary>
    private static void DeleteStoredAndMissing(IFSAssetDataPlugin store)
    {
        AssetMetadata gone = Meta("gone");
        AssetMetadata kept = Meta("kept");
        Assert.True(store.Store(gone, Util.SHA256Hash(new byte[] { 1, 2, 3 })));
        Assert.True(store.Store(kept, Util.SHA256Hash(new byte[] { 4, 5, 6 })));

        Assert.True(store.Delete(gone.ID));
        Assert.Null(store.Get(gone.ID, out _));
        Assert.NotNull(store.Get(kept.ID, out _));

        Assert.True(store.Delete(UUID.Random().ToString()));
        Assert.Equal(1, store.Count());
    }

    private static AssetMetadata Meta(string name)
    {
        UUID id = UUID.Random();
        return new AssetMetadata { FullID = id, ID = id.ToString(), Name = name, Description = "", Type = (sbyte)AssetType.Notecard };
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "fsdelete_" + Guid.NewGuid().ToString("N");
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
        string name = "fsdelete_" + Guid.NewGuid().ToString("N");
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
