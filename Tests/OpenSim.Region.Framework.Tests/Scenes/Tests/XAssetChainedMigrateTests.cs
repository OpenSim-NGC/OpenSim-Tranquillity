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
using System.Linq;
using MySqlConnector;
using Nini.Config;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.AssetService;
using OpenSim.Services.Interfaces;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// XAssetService with a chained asset service: a Get of an asset that only the chained service has moves it to
/// XAssets (a store, then a delete from the chained service). A store that fails is logged and rolled back, and
/// the store method returns as usual, so the delete removed the only copy and the next Get found nothing. A name
/// with a four-byte character in MySQL's utf8 column, or a creator id longer than its column, is enough.
///
/// The chained service here is an in-memory one, so the asset can hold values the database store refuses. Each
/// test gets a new database on the server named by the environment, dropped when it ends.
/// </summary>
#pragma warning disable CS0612 // XAssetService is obsolete
public class XAssetChainedMigrateTests : IDisposable
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
    public void MySQL_ChainedMigrate_MovesTheAsset()
    {
        CheckMoved("OpenSim.Data.MySQL.dll", NewMySQLDatabase());
    }

    [PGSQLServerFact]
    public void PGSQL_ChainedMigrate_MovesTheAsset()
    {
        CheckMoved("OpenSim.Data.PGSQL.dll", NewPGSQLDatabase());
    }

    [MySQLServerFact]
    public void MySQL_ChainedMigrate_FailedStore_KeepsTheChainedAsset()
    {
        string conn = NewMySQLDatabase();
        CheckKept("OpenSim.Data.MySQL.dll", conn, Asset("smile \U0001F600", UUID.Random().ToString()));
        CheckKept("OpenSim.Data.MySQL.dll", conn, Asset("long creator", new string('c', 129)));
    }

    [PGSQLServerFact]
    public void PGSQL_ChainedMigrate_FailedStore_KeepsTheChainedAsset()
    {
        CheckKept("OpenSim.Data.PGSQL.dll", NewPGSQLDatabase(), Asset("long creator", new string('c', 129)));
    }

    private static void CheckMoved(string provider, string conn)
    {
        MemoryAssetService chained = new();
        ChainedXAssetService service = new(Config(provider, conn), chained);
        AssetBase asset = Asset("ordinary", UUID.Random().ToString());
        chained.Store(asset);

        Assert.Equal(asset.Data, service.Get(asset.ID)?.Data);
        Assert.False(chained.Has(asset.ID));
        Assert.Equal(asset.Data, service.Get(asset.ID)?.Data);
    }

    private static void CheckKept(string provider, string conn, AssetBase asset)
    {
        MemoryAssetService chained = new();
        ChainedXAssetService service = new(Config(provider, conn), chained);
        chained.Store(asset);

        Assert.Equal(asset.Data, service.Get(asset.ID)?.Data);
        Assert.True(chained.Has(asset.ID));
        Assert.Equal(asset.Data, service.Get(asset.ID)?.Data);
    }

    private static IConfigSource Config(string provider, string conn)
    {
        IConfigSource config = new IniConfigSource();
        IConfig section = config.AddConfig("AssetService");
        section.Set("StorageProvider", provider);
        section.Set("ConnectionString", conn);
        return config;
    }

    private static AssetBase Asset(string name, string creator) =>
        new(UUID.Random(), name, (sbyte)AssetType.Notecard, creator) { Data = new byte[] { 1, 2, 3, (byte)Random.Shared.Next(256) } };

    /// <summary>XAssetService with an in-memory chained service.</summary>
    private sealed class ChainedXAssetService : XAssetService
    {
        public ChainedXAssetService(IConfigSource config, IAssetService chained) : base(config)
        {
            m_ChainedAssetService = chained;
        }
    }

    private sealed class MemoryAssetService : IAssetService
    {
        private readonly Dictionary<string, AssetBase> m_assets = new();

        public bool Has(string id) => m_assets.ContainsKey(id);

        public AssetBase Get(string id) => m_assets.TryGetValue(id, out AssetBase a) ? a : null;
        public AssetBase Get(string id, string foreignAssetService, bool storeOnLocalGrid) => Get(id);
        public AssetMetadata GetMetadata(string id) => Get(id)?.Metadata;
        public byte[] GetData(string id) => Get(id)?.Data;
        public AssetBase GetCached(string id) => Get(id);
        public bool Get(string id, object sender, AssetRetrieved handler) { handler(id, sender, Get(id)); return true; }
        public void Get(string id, string foreignAssetService, bool storeOnLocalGrid, SimpleAssetRetrieved callBack) => callBack(Get(id));
        public bool[] AssetsExist(string[] ids) => ids.Select(Has).ToArray();
        public string Store(AssetBase asset) { m_assets[asset.ID] = asset; return asset.ID; }
        public bool UpdateContent(string id, byte[] data) => false;
        public bool Delete(string id) => m_assets.Remove(id);
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "xmigrate_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Execute(new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`");
        });
        return conn;
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "xmigrate_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Execute(new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\"");
        });
        return conn;
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using (connection)
        {
            connection.Open();
            using System.Data.Common.DbCommand cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
#pragma warning restore CS0612
