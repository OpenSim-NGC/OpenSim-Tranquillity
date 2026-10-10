/*
 * Copyright (c) Legion Builds
 * All rights reserved.
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
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.PGSQL;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Sets one column of a user's row through the PostgreSQL authentication store's SetDataItem and reads the row
/// back through a second store on the same database. Only that user's row may change.
///
/// The test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>, dropped when
/// it ends. No process-wide state: it drops only its own connection pool, so the class runs in parallel with others.
/// </summary>
public class PGSQLAuthenticationSetDataItemTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_SetDataItem_ChangesThatUsersColumnOnly()
    {
        string conn = NewDatabase();
        UUID user = UUID.Random();
        UUID otherUser = UUID.Random();

        PGSQLAuthenticationData writer = new PGSQLAuthenticationData(conn, "auth");
        Assert.True(writer.Store(Row(user, "old-key")));
        Assert.True(writer.Store(Row(otherUser, "other-key")));

        Assert.True(writer.SetDataItem(user, "webLoginKey", "new-key"));

        PGSQLAuthenticationData reader = new PGSQLAuthenticationData(conn, "auth");
        AuthenticationData changed = reader.Get(user);
        Assert.NotNull(changed);
        Assert.Equal("new-key", changed.Data["webLoginKey"]);
        Assert.Equal("0123456789abcdef0123456789abcdef", changed.Data["passwordHash"]);

        AuthenticationData other = reader.Get(otherUser);
        Assert.NotNull(other);
        Assert.Equal("other-key", other.Data["webLoginKey"]);
    }

    private static AuthenticationData Row(UUID principal, string webLoginKey)
    {
        return new AuthenticationData
        {
            PrincipalID = principal,
            Data = new Dictionary<string, object>
            {
                ["passwordHash"] = "0123456789abcdef0123456789abcdef",
                ["passwordSalt"] = "fedcba9876543210fedcba9876543210",
                ["webLoginKey"] = webLoginKey,
                ["accountType"] = "UserAccount"
            }
        };
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "authset_" + Guid.NewGuid().ToString("N");
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
