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

using MySqlConnector;
using Nini.Config;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.MySQL;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.Friends;
using OpenSim.Services.Interfaces;
using Xunit;
using Assert = Xunit.Assert;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// FriendsService.GetFriends(string), the lookup by a text id that Hypergrid friends use, through the real friends
/// stores. It checked the row's friend before reading it, so every row was skipped and every lookup found nobody. On
/// MySQL the id is matched as a prefix (a uuid also finds its "uuid;url;name" rows); a % or _ in it matches only
/// itself. MySQL and PostgreSQL tests are opt-in (OPENSIM_TEST_MYSQL, OPENSIM_TEST_PGSQL), each on a new database;
/// the SQLite test uses a file (see SqliteQueryParameterTests).
/// </summary>
[Collection("SQLite stores")]
public sealed class FriendsStringLookupTests : IDisposable
{
    private const string GridUrl = "http://grid.example.org:8002/";
    private readonly List<Action> m_cleanup = new();

    public void Dispose()
    {
        foreach (Action a in m_cleanup)
            a();
    }

    private static FriendsService NewService(string provider, string connectionString)
    {
        MainConsole.Instance ??= new MockConsole();
        IniConfigSource config = new();
        IConfig friends = config.AddConfig("FriendsService");
        friends.Set("StorageProvider", provider);
        friends.Set("ConnectionString", connectionString);
        return new FriendsService(config);
    }

    private FriendsService MySQL() => NewService("OpenSim.Data.MySQL.dll", MySQLDatabase());

    private string MySQLDatabase()
    {
        string? server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        Assert.False(string.IsNullOrEmpty(server), MySQLServerFactAttribute.EnvVar + " is not set");
        string name = "friends_" + Guid.NewGuid().ToString("N");
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

    private FriendsService PGSQL()
    {
        string? server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        Assert.False(string.IsNullOrEmpty(server), PGSQLServerFactAttribute.EnvVar + " is not set");
        string name = "friends_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Execute(new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        });
        return NewService("OpenSim.Data.PGSQL.dll", conn);
    }

    private static FriendsService SQLite()
    {
        string dir = Environment.GetEnvironmentVariable("SQLITE_QUERY_TEST_DIR") is { Length: > 0 } d ? d : AppContext.BaseDirectory;
        return NewService("OpenSim.Data.SQLite.dll",
            "Data Source=" + Path.Combine(dir, "friends-string-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3");
    }

    private static void Execute(System.Data.Common.DbConnection c, string sql)
    {
        using (c)
        {
            c.Open();
            using System.Data.Common.DbCommand cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A mutual friendship between two local users; answers the first user.</summary>
    private static (string me, string friend) Mutual(FriendsService s)
    {
        string me = UUID.Random().ToString();
        string friend = UUID.Random().ToString();
        Assert.True(s.StoreFriend(me, friend, 1));
        Assert.True(s.StoreFriend(friend, me, 3));
        return (me, friend);
    }

    private static void FindsTheFriendsOfALocalUser(FriendsService s)
    {
        (string me, string friend) = Mutual(s);

        FriendInfo found = Assert.Single(s.GetFriends(me));
        Assert.Equal(me, found.PrincipalID.ToString());
        Assert.Equal(friend, found.Friend);
        Assert.Equal(1, found.MyFlags);
        Assert.Equal(3, found.TheirFlags);

        // The lookup by uuid answers the same row.
        Assert.Equal(friend, Assert.Single(s.GetFriends(new UUID(me))).Friend);
    }

    private static void MatchesWildcardsLiterally(FriendsService s)
    {
        (string me, _) = Mutual(s);

        Assert.Empty(s.GetFriends("%"));
        Assert.Empty(s.GetFriends("_"));
        Assert.Empty(s.GetFriends("_" + me[1..]));
        Assert.Empty(s.GetFriends(me[..35] + "_"));
        Assert.Empty(s.GetFriends(me[..8] + "%" + me[9..]));
        Assert.Empty(s.GetFriends(string.Empty));
        Assert.Single(s.GetFriends(me));
    }

    [Fact]
    public void SQLiteFindsTheFriendsOfALocalUser() => FindsTheFriendsOfALocalUser(SQLite());

    [MySQLServerFact]
    public void MySQLFindsTheFriendsOfALocalUser() => FindsTheFriendsOfALocalUser(MySQL());

    [PGSQLServerFact]
    public void PGSQLFindsTheFriendsOfALocalUser() => FindsTheFriendsOfALocalUser(PGSQL());

    [MySQLServerFact]
    public void MySQLFindsTheFriendsOfAHypergridVisitorByUuid()
    {
        FriendsService s = MySQL();
        string local = UUID.Random().ToString();
        string visitor = UUID.Random().ToString();
        string visitorId = visitor + ";" + GridUrl + ";Visiting User";
        Assert.True(s.StoreFriend(visitorId, local, 1));

        FriendInfo found = Assert.Single(s.GetFriends(visitor));
        Assert.Equal(visitor, found.PrincipalID.ToString());
        Assert.Equal(local, found.Friend);
        Assert.Equal(-1, found.TheirFlags);
    }

    [Fact]
    public void SQLiteMatchesWildcardsLiterally() => MatchesWildcardsLiterally(SQLite());

    [MySQLServerFact]
    public void MySQLMatchesWildcardsLiterally() => MatchesWildcardsLiterally(MySQL());

    [PGSQLServerFact]
    public void PGSQLMatchesWildcardsLiterally() => MatchesWildcardsLiterally(PGSQL());

    [MySQLServerFact]
    public void MySQLComparesTheEscapeCharacterAsText()
    {
        string conn = MySQLDatabase();
        FriendsService s = NewService("OpenSim.Data.MySQL.dll", conn);
        string local = UUID.Random().ToString();
        string odd = "!_%" + local;
        Assert.True(s.StoreFriend(odd, local, 1));
        Assert.True(s.StoreFriend("x" + local, local, 1));

        // The service skips a principal that is not a user id, so read the store itself.
        MySqlFriendsData store = new MySqlFriendsData(conn, "Friends");
        Assert.Equal(odd, Assert.Single(store.GetFriends("!_%")).PrincipalID);
        Assert.Equal(odd, Assert.Single(store.GetFriends("!")).PrincipalID);
        Assert.Empty(store.GetFriends("!x"));
        Assert.Empty(store.GetFriends("__%"));
    }
}
