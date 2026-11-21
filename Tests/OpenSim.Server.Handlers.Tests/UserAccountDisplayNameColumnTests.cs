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
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.PGSQL;
using OpenSim.Data.SQLite;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// UserAccountService stores and reads DisplayName and NameChanged with every account
/// (StoreUserAccount puts both in the row's Data, and the generic table handlers write every Data key
/// as a column). MySQL's UserAccount migration 7 adds the two columns; SQLite (before its migration 4)
/// and PostgreSQL (before its migration 6) did not, so creating any account failed with
/// "table UserAccounts has no column named DisplayName", including the estate owner a standalone
/// region creates at its first start.
///
/// The SQLite schema is checked by running the store's migrations on a database file of each test's
/// own. SQLiteGenericTableHandler keeps one static connection per stored type, opened by the first
/// store constructed in the process, so the test through UserAccountService uses whichever database
/// that is (migrated by this build either way), and the class shares the "SQLite stores" collection
/// with SqliteQueryParameterTests so the two never use that connection at once.
/// PostgreSQL is checked by the text of its migrations; no PostgreSQL server is involved.
/// </summary>
[Xunit.Collection("SQLite stores")]
public class UserAccountDisplayNameColumnTests
{
    private static string NewDatabase() =>
        "Data Source=" + Path.Combine(AppContext.BaseDirectory,
            "useraccount-displayname-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3";

    private static long ScalarLong(SQLiteConnection conn, string sql)
    {
        using SQLiteCommand cmd = new(sql, conn);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Migrate(SQLiteConnection conn) =>
        new Migration(conn, typeof(SQLiteUserAccountData).Assembly, "UserAccount").Update();

    [Xunit.Fact]
    public void SqliteMigrationsCreateBothColumns()
    {
        using SQLiteConnection conn = new(NewDatabase());
        conn.Open();

        Migrate(conn);

        Xunit.Assert.Equal(5L, ScalarLong(conn, "select version from migrations where name = 'UserAccount'"));
        Xunit.Assert.Equal(1L, ScalarLong(conn, "select count(*) from pragma_table_info('UserAccounts') where name = 'DisplayName'"));
        Xunit.Assert.Equal(1L, ScalarLong(conn, "select count(*) from pragma_table_info('UserAccounts') where name = 'NameChanged'"));
    }

    [Xunit.Fact]
    public void ASqliteDatabaseAtVersion3IsUpgradedAndKeepsItsAccounts()
    {
        using SQLiteConnection conn = new(NewDatabase());
        conn.Open();
        UUID id = UUID.Random();
        using (SQLiteCommand cmd = new(
            "create table migrations(name varchar(100), version int);" +
            "insert into migrations(name, version) values('migrations', 1);" +
            "insert into migrations(name, version) values('UserAccount', 3);" +
            "CREATE TABLE UserAccounts (PrincipalID CHAR(36) primary key, ScopeID CHAR(36) NOT NULL," +
            " FirstName VARCHAR(64) NOT NULL, LastName VARCHAR(64) NOT NULL, Email VARCHAR(64), ServiceURLs TEXT," +
            " Created INT(11), UserLevel integer NOT NULL DEFAULT 0, UserFlags integer NOT NULL DEFAULT 0," +
            " UserTitle varchar(64) NOT NULL DEFAULT '', active BOOLEAN NOT NULL DEFAULT TRUE);" +
            "insert into UserAccounts (PrincipalID, ScopeID, FirstName, LastName, Email, ServiceURLs, Created)" +
            " values ('" + id + "', '" + UUID.Zero + "', 'Old', 'Account', '', '', 1);",
            conn))
        {
            cmd.ExecuteNonQuery();
        }

        Migrate(conn);

        Xunit.Assert.Equal(5L, ScalarLong(conn, "select version from migrations where name = 'UserAccount'"));
        Xunit.Assert.Equal(1L, ScalarLong(conn,
            "select count(*) from UserAccounts where PrincipalID = '" + id + "' and FirstName = 'Old'" +
            " and DisplayName is null and NameChanged = 0"));
    }

    [Xunit.Fact]
    public void CreateUserAndSetDisplayNameThroughTheService()
    {
        IniConfigSource config = new();
        IConfig section = config.AddConfig("UserAccountService");
        section.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        section.Set("ConnectionString", NewDatabase());
        UserAccountService service = new(config);
        UUID id = UUID.Random();

        UserAccount created = service.CreateUser(UUID.Zero, id, "Display", "Tester", "", "tester@example.invalid");

        Xunit.Assert.NotNull(created);
        UserAccount read = service.GetUserAccount(UUID.Zero, id);
        Xunit.Assert.NotNull(read);
        Xunit.Assert.Equal(string.Empty, read.DisplayName);
        Xunit.Assert.Equal(0u, read.NameChanged);

        Xunit.Assert.True(service.SetDisplayName(id, "Shown Name"));

        read = service.GetUserAccount(UUID.Zero, id);
        Xunit.Assert.Equal("Shown Name", read.DisplayName);
        Xunit.Assert.True(read.NameChanged > 0, "NameChanged should hold the time of the change");
    }

    [Xunit.Theory]
    [Xunit.InlineData("DisplayName")]
    [Xunit.InlineData("NameChanged")]
    [Xunit.InlineData("UserCountry")]
    public void PostgresMigrationAddsTheColumnWithItsQuotedName(string column)
    {
        var assembly = typeof(PGSQLUserAccountData).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".UserAccount.migrations"));
        using Stream stream = assembly.GetManifestResourceStream(name);
        using StreamReader reader = new(stream);
        string migrations = reader.ReadToEnd();

        // PostgreSQL folds unquoted identifiers to lower case, and PGSQLGenericTableHandler quotes the
        // names it writes ("DisplayName"), so the column is created with its quoted name.
        Xunit.Assert.Matches(new Regex("ALTER TABLE UserAccounts ADD \"" + column + "\" "), migrations);
        Xunit.Assert.DoesNotContain("`", migrations);
    }
}
