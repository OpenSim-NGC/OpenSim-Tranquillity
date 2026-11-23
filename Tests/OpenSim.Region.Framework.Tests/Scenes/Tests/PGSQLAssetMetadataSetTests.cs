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
using System.Linq;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Reads asset metadata a page at a time through the PostgreSQL asset store. FetchAssetMetadataSet(start, count)
/// returns up to count rows after the first start, as the SQLite and MySQL stores do with LIMIT start, count. The
/// PostgreSQL store asked for LIMIT start + count - 1, so every page was one row short, a page of one was empty and
/// a page of none was LIMIT -1, which PostgreSQL refuses.
///
/// The test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>, dropped when
/// it ends. No process-wide state: it drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class PGSQLAssetMetadataSetTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_FetchAssetMetadataSet_ReturnsCountRows()
    {
        PGSQLAssetData store = new();
        store.Initialise(NewDatabase());

        List<UUID> ids = new();
        for (int i = 0; i < 5; i++)
        {
            AssetBase asset = Asset("asset " + i);
            Assert.True(store.StoreAsset(asset));
            ids.Add(asset.FullID);
        }
        // The store pages in id order.
        ids.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));

        Assert.Equal(ids.Take(1), store.FetchAssetMetadataSet(0, 1).Select(m => m.FullID));
        Assert.Equal(ids.Skip(2).Take(2), store.FetchAssetMetadataSet(2, 2).Select(m => m.FullID));
        Assert.Equal(ids, store.FetchAssetMetadataSet(0, 5).Select(m => m.FullID));
        Assert.Equal(ids.Skip(3), store.FetchAssetMetadataSet(3, 10).Select(m => m.FullID));
        Assert.Empty(store.FetchAssetMetadataSet(0, 0));
        Assert.Empty(store.FetchAssetMetadataSet(5, 2));

        // Pages of two cover every asset once.
        List<UUID> paged = new();
        for (int start = 0; start < ids.Count; start += 2)
            paged.AddRange(store.FetchAssetMetadataSet(start, 2).Select(m => m.FullID));
        Assert.Equal(ids, paged);

        AssetMetadata first = Assert.Single(store.FetchAssetMetadataSet(0, 1));
        Assert.Equal((sbyte)AssetType.Notecard, first.Type);
        Assert.StartsWith("asset ", first.Name);
    }

    private static AssetBase Asset(string name) =>
        new(UUID.Random(), name, (sbyte)AssetType.Notecard, UUID.Random().ToString()) { Data = new byte[] { 1, 2, 3 } };

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "assetmeta_" + Guid.NewGuid().ToString("N");
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
