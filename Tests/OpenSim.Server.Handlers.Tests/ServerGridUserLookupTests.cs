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
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The grid user lookup by id prefix (GetAll) on a real MySQL or PostgreSQL server, each test in a new database. The
/// id comes from a Robust request, so it is compared as data: a quote in it is not SQL, and a % or _ matches only
/// itself. The prefix match itself stays, because a Hypergrid user's id is "uuid;url;name" and is found by the uuid.
/// </summary>
public sealed class ServerGridUserLookupTests : IDisposable
{
    private readonly List<Action> m_cleanup = new();

    public void Dispose()
    {
        foreach (Action a in m_cleanup)
            a();
    }

    private (IGridUserData, Func<long>) MySQL()
    {
        string? server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        Assert.False(string.IsNullOrEmpty(server), MySQLServerFactAttribute.EnvVar + " is not set");
        string name = "griduser_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Execute(new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`");
        });
        return (new MySQLGridUserData(conn, "GridUser"),
            () => Count(new MySqlConnection(conn), "SELECT COUNT(*) FROM GridUser"));
    }

    private (IGridUserData, Func<long>) PGSQL()
    {
        string? server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        Assert.False(string.IsNullOrEmpty(server), PGSQLServerFactAttribute.EnvVar + " is not set");
        string name = "griduser_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Execute(new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        });
        return (new PGSQLGridUserData(conn, "GridUser"),
            () => Count(new NpgsqlConnection(conn), "SELECT COUNT(*) FROM GridUser"));
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

    private static long Count(System.Data.Common.DbConnection c, string sql)
    {
        using (c)
        {
            c.Open();
            using System.Data.Common.DbCommand cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    private static string Stored(IGridUserData db, string id)
    {
        GridUserData row = new GridUserData { UserID = id };
        row.Data["Online"] = false.ToString();
        row.Data["Login"] = "0";
        row.Data["Logout"] = "0";
        Assert.True(db.Store(row), "the row " + id + " was not stored");
        return id;
    }

    private static void PrefixLookups((IGridUserData, Func<long>) store)
    {
        (IGridUserData db, Func<long> count) = store;
        string local = Stored(db, UUID.Random().ToString());
        string visitor = UUID.Random().ToString();
        string hg = Stored(db, visitor + ";http://grid.example.org:8002/;First Last");
        string marked = Stored(db, "!_%-" + UUID.Random());

        GridUserData found = Assert.Single(db.GetAll(local));
        Assert.Equal(local, found.UserID);

        found = Assert.Single(db.GetAll(visitor));
        Assert.Equal(hg, found.UserID);

        // the characters the pattern escapes still match themselves
        found = Assert.Single(db.GetAll("!_%-"));
        Assert.Equal(marked, found.UserID);

        // an empty prefix still lists everyone ("show grid users online", the grid stats page)
        Assert.Equal(3, db.GetAll(string.Empty).Length);
    }

    private static void QuoteIsOnlyCompared((IGridUserData, Func<long>) store)
    {
        (IGridUserData db, Func<long> count) = store;
        Stored(db, UUID.Random().ToString());
        Stored(db, UUID.Random() + ";http://grid.example.org:8002/;First Last");
        long before = count();

        GridUserData[] found = db.GetAll("O'Brien");

        Assert.Empty(found);
        Assert.Equal(before, count());
        Assert.Equal(2, db.GetAll(string.Empty).Length);
    }

    private static void WildcardsMatchOnlyThemselves((IGridUserData, Func<long>) store)
    {
        (IGridUserData db, Func<long> _) = store;
        string local = Stored(db, UUID.Random().ToString());
        Stored(db, UUID.Random() + ";http://grid.example.org:8002/;First Last");

        Assert.Empty(db.GetAll("%"));
        Assert.Empty(db.GetAll("_"));
        Assert.Empty(db.GetAll(local[..8] + "_"));
    }

    [MySQLServerFact]
    public void MySQLFindsUsersByIdPrefix() => PrefixLookups(MySQL());

    [MySQLServerFact]
    public void MySQLComparesAQuoteInTheIdAsText() => QuoteIsOnlyCompared(MySQL());

    [MySQLServerFact]
    public void MySQLMatchesWildcardsInTheIdLiterally() => WildcardsMatchOnlyThemselves(MySQL());

    [PGSQLServerFact]
    public void PGSQLFindsUsersByIdPrefix() => PrefixLookups(PGSQL());

    [PGSQLServerFact]
    public void PGSQLComparesAQuoteInTheIdAsText() => QuoteIsOnlyCompared(PGSQL());

    [PGSQLServerFact]
    public void PGSQLMatchesWildcardsInTheIdLiterally() => WildcardsMatchOnlyThemselves(PGSQL());
}
