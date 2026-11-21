/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
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
/// The FSAssets stores answer each asset's hash and type in one query, for the service to check the files
/// AssetsExist answers for. Each test gets a new database on the server named by the environment
/// (<see cref="MySQLServerFactAttribute.EnvVar"/>, <see cref="PGSQLServerFactAttribute.EnvVar"/>), dropped when it
/// ends.
/// </summary>
public class FSAssetStoreHashesTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [MySQLServerFact]
    public void MySQL_GetHashes_AnswersEachRow()
    {
        MySQLFSAssetData store = new();
        store.Initialise(NewMySQLDatabase(), "fsassets", 0);
        AnswersEachRow(store);
    }

    [PGSQLServerFact]
    public void PGSQL_GetHashes_AnswersEachRow()
    {
        PGSQLFSAssetData store = new();
        store.Initialise(NewPGSQLDatabase(), "fsassets", 0);
        AnswersEachRow(store);
    }

    private static void AnswersEachRow(IFSAssetDataPlugin store)
    {
        AssetMetadata texture = Metadata(AssetType.Texture);
        AssetMetadata notecard = Metadata(AssetType.Notecard);
        string textureHash = Util.SHA256Hash(Guid.NewGuid().ToByteArray());
        string notecardHash = Util.SHA256Hash(Guid.NewGuid().ToByteArray());
        Assert.True(store.Store(texture, textureHash));
        Assert.True(store.Store(notecard, notecardHash));

        (string Hash, sbyte Type)[] rows = store.GetHashes(new[] { notecard.FullID, UUID.Random(), texture.FullID, notecard.FullID });

        Assert.Equal(4, rows.Length);
        Assert.Equal((notecardHash, (sbyte)AssetType.Notecard), rows[0]);
        Assert.Null(rows[1].Hash);
        Assert.Equal((textureHash, (sbyte)AssetType.Texture), rows[2]);
        Assert.Equal((notecardHash, (sbyte)AssetType.Notecard), rows[3]);
        Assert.Empty(store.GetHashes(Array.Empty<UUID>()));
    }

    private static AssetMetadata Metadata(AssetType type)
    {
        UUID id = UUID.Random();
        return new AssetMetadata { FullID = id, ID = id.ToString(), Name = "probe", Description = "", Type = (sbyte)type };
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "fshashes_" + Guid.NewGuid().ToString("N");
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
        string name = "fshashes_" + Guid.NewGuid().ToString("N");
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
