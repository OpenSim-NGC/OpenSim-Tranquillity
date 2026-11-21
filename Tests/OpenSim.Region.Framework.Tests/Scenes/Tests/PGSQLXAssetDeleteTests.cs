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
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Deletes an asset through the PostgreSQL XAssets store. The id column is a uuid and the store bound the id as
/// text, which PostgreSQL refuses (42883: operator does not exist: uuid = character varying): Delete threw and the
/// asset stayed. The content of a deleted asset stays, as on MySQL: another asset with the same content shares it.
///
/// The test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>, dropped when
/// it ends. No process-wide state: it drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class PGSQLXAssetDeleteTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_Delete_DeletesTheAsset()
    {
        string conn = NewDatabase();
        PGSQLXAssetData store = new();
        store.Initialise(conn);

        byte[] content = { 1, 2, 3 };
        AssetBase deleted = Asset("deleted", content);
        AssetBase sameContent = Asset("same content", content);
        AssetBase other = Asset("other", new byte[] { 4, 5, 6 });
        store.StoreAsset(deleted);
        store.StoreAsset(sameContent);
        store.StoreAsset(other);
        Assert.NotNull(store.GetAsset(deleted.FullID));

        Assert.True(store.Delete(deleted.ID));

        Assert.Null(store.GetAsset(deleted.FullID));
        Assert.Equal(new[] { false, true, true },
            store.AssetsExist(new[] { deleted.FullID, sameContent.FullID, other.FullID }));
        Assert.Equal(content, store.GetAsset(sameContent.FullID)?.Data);
        Assert.Equal(other.Data, store.GetAsset(other.FullID)?.Data);
        Assert.Equal(2L, Scalar(conn, "SELECT COUNT(*) FROM xassetsdata"));

        // A missing id deletes nothing, as on MySQL; an id that is not a UUID is refused.
        Assert.True(store.Delete(UUID.Random().ToString()));
        Assert.False(store.Delete("nope"));
        Assert.Equal(other.Data, store.GetAsset(other.FullID)?.Data);
    }

    private static AssetBase Asset(string name, byte[] data) =>
        new(UUID.Random(), name, (sbyte)AssetType.Notecard, UUID.Random().ToString()) { Data = data };

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "xassetdel_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Scalar(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup = () =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Scalar(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        };
        return conn;
    }

    private static object Scalar(string conn, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }
}
