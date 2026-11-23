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
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.PGSQL;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The PostgreSQL friends store answers GetFriends for a Guid with the same rows as for that id as a UUID. Before,
/// the Guid overload called itself and overflowed the stack.
///
/// The test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>, dropped when
/// it ends.
/// </summary>
public class PGSQLFriendsGuidTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_GetFriendsByGuidAnswersTheFriendships()
    {
        PGSQLFriendsData store = new(NewDatabase(), "Friends");
        UUID me = UUID.Random();
        UUID mutual = UUID.Random();
        UUID oneWay = UUID.Random();

        Assert.True(store.Store(Friendship(me, mutual, "1")));
        Assert.True(store.Store(Friendship(mutual, me, "3")));
        Assert.True(store.Store(Friendship(me, oneWay, "0")));
        Assert.True(store.Store(Friendship(oneWay, UUID.Random(), "1")));

        FriendsData[] byGuid = store.GetFriends(me.Guid);
        FriendsData[] byUuid = store.GetFriends(me);

        Assert.Equal(2, byGuid.Length);
        Assert.Equal(Rows(byUuid), Rows(byGuid));
        Assert.Equal("3", byGuid.Single(f => f.Friend == mutual.ToString()).Data["TheirFlags"].Trim());
        Assert.Equal("-1", byGuid.Single(f => f.Friend == oneWay.ToString()).Data["TheirFlags"].Trim());

        Assert.Empty(store.GetFriends(Guid.NewGuid()));
    }

    private static FriendsData Friendship(UUID principal, UUID friend, string flags) => new()
    {
        PrincipalID = principal.ToString(),
        Friend = friend.ToString(),
        Data = new Dictionary<string, string> { ["Flags"] = flags, ["Offered"] = "0" },
    };

    private static string[] Rows(FriendsData[] rows) =>
        rows.Select(f => $"{f.PrincipalID} {f.Friend} {f.Data["Flags"].Trim()} {f.Data["TheirFlags"].Trim()}").OrderBy(r => r).ToArray();

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "friendsguid_" + Guid.NewGuid().ToString("N");
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
