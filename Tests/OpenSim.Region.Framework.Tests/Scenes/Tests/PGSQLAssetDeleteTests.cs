/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The PostgreSQL asset store deletes an asset, as the MySQL and SQLite stores do. Before, Delete did nothing
/// and answered false, so the Robust console's "delete asset" and an allowed remote delete left the asset in place.
///
/// The test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>, dropped when
/// it ends.
/// </summary>
public class PGSQLAssetDeleteTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_DeleteRemovesThatAssetOnly()
    {
        PGSQLAssetData store = new();
        store.Initialise(NewDatabase());

        AssetBase deleted = Asset();
        AssetBase kept = Asset();
        Assert.True(store.StoreAsset(deleted));
        Assert.True(store.StoreAsset(kept));

        Assert.True(store.Delete(deleted.ID));

        Assert.Null(store.GetAsset(deleted.FullID));
        Assert.NotNull(store.GetAsset(kept.FullID));
        Assert.False(store.Delete(deleted.ID));
        Assert.False(store.Delete("not an id"));
    }

    private static AssetBase Asset() =>
        new(UUID.Random(), "note", (sbyte)AssetType.Notecard, UUID.Random().ToString()) { Data = new byte[] { 1, 2, 3 } };

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "assetdel_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup = () =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Admin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        };
        return conn;
    }

    private static void Admin(string admin, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(admin);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
