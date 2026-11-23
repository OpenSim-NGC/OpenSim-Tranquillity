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
using OpenSim.Data.Null;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The grid service looks regions up by name with Util.EscapeForLike, which escaped "%" and "_" with a backslash but
/// not the backslash itself, so a name or search with a backslash matched the wrong regions, and a trailing backslash
/// made PostgreSQL reject the statement. MySQL also needs the escape named: in NO_BACKSLASH_ESCAPES mode it has none.
/// The null store, which standalone regions use, reads the same patterns. MySQL and PostgreSQL tests are opt-in (OPENSIM_TEST_MYSQL, OPENSIM_TEST_PGSQL), each on a new database.
/// </summary>
public sealed class RegionNameLikeTests : IDisposable
{
    private static readonly string[] s_names = { "Foo_Bar", "FooXBar", @"Back\Slash", "BackSlash", @"Ends\", "Ends" };
    private readonly List<Action> m_cleanup = new();

    public void Dispose()
    {
        foreach (Action a in m_cleanup)
            a();
    }

    private IRegionData MySQL()
    {
        string? server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        Assert.False(string.IsNullOrEmpty(server), MySQLServerFactAttribute.EnvVar + " is not set");
        string name = "regions_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Execute(new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`");
        });
        return new MySqlRegionData(conn, "regions");
    }

    private IRegionData PGSQL()
    {
        string? server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        Assert.False(string.IsNullOrEmpty(server), PGSQLServerFactAttribute.EnvVar + " is not set");
        string name = "regions_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Execute(new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        });
        return new PGSQLRegionData(conn, "regions");
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

    private static IRegionData WithRegions(IRegionData db)
    {
        int x = 1000;
        foreach (string name in s_names)
        {
            RegionData r = new RegionData
            {
                RegionID = UUID.Random(),
                RegionName = name,
                posX = 256 * x,
                posY = 256 * x,
                sizeX = 256,
                sizeY = 256,
                // The columns GridService.RegionInfo2RegionData fills in from a GridRegion.
                Data = new Dictionary<string, object>
                {
                    ["regionHandle"] = Utils.UIntsToLong((uint)(256 * x), (uint)(256 * x)),
                    ["owner_uuid"] = UUID.Zero.ToString(),
                    ["Token"] = UUID.Random().ToString(),
                    ["serverIP"] = "127.0.0.1",
                    ["serverHttpPort"] = "9000",
                    ["serverPort"] = "9000",
                    ["serverURI"] = "http://127.0.0.1:9000/",
                    ["regionMapTexture"] = UUID.Zero.ToString(),
                    ["parcelMapTexture"] = UUID.Zero.ToString(),
                    ["access"] = "13",
                    ["flags"] = "0",
                    ["regionSecret"] = "",
                },
            };
            x++;
            Assert.True(db.Store(r), "the region " + name + " was not stored");
        }
        return db;
    }

    private static string[] Names(IRegionData db, string pattern) =>
        (db.Get(pattern, UUID.Zero) ?? new List<RegionData>())
            .Select(r => r.RegionName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static void NamesMatchLiterally(IRegionData db)
    {
        WithRegions(db);

        // An exact name (GridService.RegisterRegion, GetRegionsByName for one region, HypergridLinker).
        Assert.Equal(new[] { "Foo_Bar" }, Names(db, Util.EscapeForLike("Foo_Bar")));
        Assert.Equal(new[] { @"Back\Slash" }, Names(db, Util.EscapeForLike(@"Back\Slash")));
        Assert.Equal(new[] { @"Ends\" }, Names(db, Util.EscapeForLike(@"Ends\")));

        // A search (GridService.GetRegionsByName, map search).
        Assert.Equal(new[] { @"Back\Slash", @"Ends\" }, Names(db, "%" + Util.EscapeForLike(@"\") + "%"));
        Assert.Equal(new[] { "Foo_Bar" }, Names(db, "%" + Util.EscapeForLike("_") + "%"));
        Assert.Empty(Names(db, "%" + Util.EscapeForLike("%") + "%"));
        Assert.Equal(new[] { @"Ends\" }, Names(db, "%" + Util.EscapeForLike(@"s\")));
    }

    [Fact]
    public void EscapesTheBackslashAsWellAsTheWildcards()
    {
        Assert.Equal(@"foo\_bar\%baz\\", Util.EscapeForLike(@"foo_bar%baz\"));
        Assert.Equal(@"\\\_", Util.EscapeForLike(@"\_"));
        Assert.Equal("plain", Util.EscapeForLike("plain"));
    }

    [Fact]
    public void NullStoreMatchesRegionNamesLiterally() => NamesMatchLiterally(new NullRegionData("!static", "regions"));

    [MySQLServerFact]
    public void MySQLMatchesRegionNamesLiterally() => NamesMatchLiterally(MySQL());

    [PGSQLServerFact]
    public void PGSQLMatchesRegionNamesLiterally() => NamesMatchLiterally(PGSQL());
}
