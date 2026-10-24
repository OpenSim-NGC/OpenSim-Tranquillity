/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.SQLite;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// SQLite stores run text they are given as data. Each test uses a database file; the stores keep one static
/// connection per store type for the whole process, so one file per store type is created on first use.
/// SQLITE_QUERY_TEST_DIR names the folder for the files (default: the test output folder).
/// All tests are in one class, so they run one after another; the class touches process-wide state (the stores'
/// static connections) and so shares no state with other classes.
/// </summary>
public class SqliteQueryParameterTests
{
    private const string QuoteText = "x' or '1'='1";
    private const string StatementText = "x'; delete from tokens; --";

    private static readonly string s_dir = Environment.GetEnvironmentVariable("SQLITE_QUERY_TEST_DIR")
        is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;
    private static readonly string s_run = Guid.NewGuid().ToString("N");

    private static string ConnectionFor(string store) =>
        "Data Source=" + Path.Combine(s_dir, "sqlquery-" + store + "-" + s_run + ".db") + ";Version=3";

    private static readonly System.Lazy<SQLiteAuthenticationData> s_auth = new(() => new SQLiteAuthenticationData(ConnectionFor("auth"), "auth"));
    private static readonly System.Lazy<SQLiteGridUserData> s_gridUser = new(() => new SQLiteGridUserData(ConnectionFor("griduser"), "GridUser"));
    private static readonly System.Lazy<SQLiteUserAccountData> s_accounts = new(() => new SQLiteUserAccountData(ConnectionFor("accounts"), "UserAccounts"));

    private static UUID NewAuthRow()
    {
        UUID id = UUID.Random();
        Xunit.Assert.True(s_auth.Value.Store(new AuthenticationData
        {
            PrincipalID = id,
            Data = new Dictionary<string, object> { ["passwordHash"] = "hash", ["passwordSalt"] = "salt", ["accountType"] = "UserAccount" }
        }));
        return id;
    }

    [Xunit.Fact]
    public void OrdinaryTokenIsStoredCheckedAndRefused()
    {
        UUID id = NewAuthRow();
        string token = UUID.Random().ToString();

        Xunit.Assert.True(s_auth.Value.SetToken(id, token, 30));
        Xunit.Assert.True(s_auth.Value.CheckToken(id, token, 30));
        Xunit.Assert.False(s_auth.Value.CheckToken(id, UUID.Random().ToString(), 30));
        Xunit.Assert.False(s_auth.Value.CheckToken(UUID.Random(), token, 30));
    }

    [Xunit.Fact]
    public void TokenWithQuotesIsOnlyEverCompared()
    {
        UUID id = NewAuthRow();
        string good = UUID.Random().ToString();
        Xunit.Assert.True(s_auth.Value.SetToken(id, good, 30));

        Xunit.Assert.False(s_auth.Value.CheckToken(id, QuoteText, 30));
        Xunit.Assert.False(s_auth.Value.CheckToken(id, StatementText, 30));
        Xunit.Assert.False(s_auth.Value.CheckToken(id, "'", 30));
        Xunit.Assert.True(s_auth.Value.CheckToken(id, good, 30));
    }

    [Xunit.Fact]
    public void TokenWithQuotesIsStoredAsGiven()
    {
        UUID id = NewAuthRow();
        UUID other = NewAuthRow();
        string otherToken = UUID.Random().ToString();
        Xunit.Assert.True(s_auth.Value.SetToken(other, otherToken, 30));

        Xunit.Assert.True(s_auth.Value.SetToken(id, "it's", 30));
        Xunit.Assert.True(s_auth.Value.CheckToken(id, "it's", 30));
        Xunit.Assert.False(s_auth.Value.CheckToken(id, "its", 30));
        Xunit.Assert.True(s_auth.Value.CheckToken(other, otherToken, 30));
    }

    [Xunit.Fact]
    public void SetDataItemStoresTextAsGiven()
    {
        UUID id = NewAuthRow();
        UUID other = NewAuthRow();

        Xunit.Assert.True(s_auth.Value.SetDataItem(id, "webLoginKey", "it's a key"));
        Xunit.Assert.Equal("it's a key", s_auth.Value.Get(id).Data["webLoginKey"]);

        Xunit.Assert.True(s_auth.Value.SetDataItem(id, "webLoginKey", "x', accountType = 'y"));
        AuthenticationData row = s_auth.Value.Get(id);
        Xunit.Assert.Equal("x', accountType = 'y", row.Data["webLoginKey"]);
        Xunit.Assert.Equal("UserAccount", row.Data["accountType"]);
        Xunit.Assert.Equal("", s_auth.Value.Get(other).Data["webLoginKey"]);
    }

    [Xunit.Fact]
    public void SetDataItemRefusesAColumnTheTableDoesNotHave()
    {
        UUID id = NewAuthRow();

        Xunit.Assert.False(s_auth.Value.SetDataItem(id, "nosuchcolumn", "v"));
        Xunit.Assert.False(s_auth.Value.SetDataItem(id, "passwordHash` = 'a', `passwordSalt", "v"));
        Xunit.Assert.False(s_auth.Value.SetDataItem(id, "", "v"));
        AuthenticationData row = s_auth.Value.Get(id);
        Xunit.Assert.Equal("hash", row.Data["passwordHash"]);
        Xunit.Assert.Equal("salt", row.Data["passwordSalt"]);
    }

    [Xunit.Fact]
    public void GridUserPrefixLookupFindsOnlyTheMatchingRows()
    {
        string prefix = UUID.Random().ToString();
        Xunit.Assert.True(s_gridUser.Value.Store(new GridUserData { UserID = prefix + ";http://home.example.org:8002/;Test User" }));
        Xunit.Assert.True(s_gridUser.Value.Store(new GridUserData { UserID = UUID.Random().ToString() }));

        GridUserData[] found = s_gridUser.Value.GetAll(prefix);
        Xunit.Assert.Single(found);
        Xunit.Assert.StartsWith(prefix, found[0].UserID);
        Xunit.Assert.True(s_gridUser.Value.GetAll("").Length >= 2);
    }

    [Xunit.Fact]
    public void GridUserPrefixWithQuotesIsOnlyEverCompared()
    {
        string quoted = "it's-" + UUID.Random();
        Xunit.Assert.True(s_gridUser.Value.Store(new GridUserData { UserID = quoted }));
        Xunit.Assert.True(s_gridUser.Value.Store(new GridUserData { UserID = UUID.Random().ToString() }));

        Xunit.Assert.Empty(s_gridUser.Value.GetAll(QuoteText));
        Xunit.Assert.Empty(s_gridUser.Value.GetAll("x%' or UserID like '"));
        Xunit.Assert.Single(s_gridUser.Value.GetAll("it's-"));
    }

    private static void AddAccount(string first, string last)
    {
        Xunit.Assert.True(s_accounts.Value.Store(new UserAccountData
        {
            PrincipalID = UUID.Random(),
            ScopeID = UUID.Zero,
            FirstName = first,
            LastName = last,
            Data = new Dictionary<string, string>()
        }));
    }

    [Xunit.Fact]
    public void AccountSearchFindsTheMatchingNames()
    {
        AddAccount("Ordinary", "Smithson");
        AddAccount("Other", "Person");

        Xunit.Assert.Single(s_accounts.Value.GetUsers(UUID.Zero, "Smith"));
        Xunit.Assert.Single(s_accounts.Value.GetUsers(UUID.Zero, "Ordin Smit"));
        Xunit.Assert.Empty(s_accounts.Value.GetUsers(UUID.Zero, "Nomatch"));
    }

    [Xunit.Fact]
    public void AccountSearchWithQuotesIsOnlyEverCompared()
    {
        AddAccount("Quentin", "O'Brienson");
        AddAccount("Other", "Person");

        Xunit.Assert.Single(s_accounts.Value.GetUsers(UUID.Zero, "O'Brien"));
        Xunit.Assert.Empty(s_accounts.Value.GetUsers(UUID.Zero, "zzz'or'1'='1'or'x"));
        Xunit.Assert.Empty(s_accounts.Value.GetUsers(UUID.Zero, "zzz'or'1'='1'or'x zzz'or'1'='1'or'x"));
        Xunit.Assert.NotEmpty(s_accounts.Value.GetUsers(UUID.Zero, "Other"));
    }
}
