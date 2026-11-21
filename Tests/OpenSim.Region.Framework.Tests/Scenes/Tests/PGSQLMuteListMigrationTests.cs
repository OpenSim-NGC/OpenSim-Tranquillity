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
using System.Linq;
using Npgsql;
using OpenSim.Data.PGSQL;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The PostgreSQL mute list store creates its table. Migration.Update reads a store's migrations from the
/// resources embedded in the store's assembly, and Resources/MuteListStore.migrations was not embedded, so the
/// MuteList table was never created and MuteListStore had no version.
///
/// The second test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>,
/// dropped when it ends. No process-wide state: it drops only its own connection pool, so the class runs in
/// parallel with others.
/// </summary>
public class PGSQLMuteListMigrationTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [Fact]
    public void MuteListStoreMigrations_AreEmbedded()
    {
        string[] names = typeof(PGSQLMuteListData).Assembly.GetManifestResourceNames();
        Assert.Single(names, n => n.EndsWith(".MuteListStore.migrations", StringComparison.Ordinal));
    }

    [PGSQLServerFact]
    public void PGSQL_MuteListStore_CreatesItsTable()
    {
        string conn = NewDatabase();

        _ = new PGSQLMuteListData(conn);

        Assert.Equal(1, Scalar(conn, "SELECT version FROM migrations WHERE name = 'MuteListStore'"));
        Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'mutelist'"));
    }

    private static object Scalar(string conn, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "mutelist_" + Guid.NewGuid().ToString("N");
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
