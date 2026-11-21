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

using System.Data.SQLite;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.SQLite;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Two account store gaps. SQLiteUserAccountData.GetUsersWhere returned null, so every multi-account lookup
/// (getmultiaccounts, GetUserAccounts) came back empty on SQLite. No store had a UserCountry column, so saving an
/// account that has a country (UserAccountService stores it whenever it is set) failed. Shares the "SQLite stores"
/// collection: SQLite stores keep one connection per stored type for the process.
/// </summary>
[Xunit.Collection("SQLite stores")]
public class UserAccountCountryAndLookupTests
{
    private static string NewDatabase() =>
        "Data Source=" + Path.Combine(AppContext.BaseDirectory,
            "useraccount-country-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3";

    private static readonly System.Lazy<UserAccountService> s_service = new(() =>
    {
        IniConfigSource config = new();
        IConfig section = config.AddConfig("UserAccountService");
        section.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        section.Set("ConnectionString", NewDatabase());
        return new UserAccountService(config);
    });

    [Xunit.Fact]
    public void SqliteMigrationsAddTheCountryColumn()
    {
        using SQLiteConnection conn = new(NewDatabase());
        conn.Open();
        new Migration(conn, typeof(SQLiteUserAccountData).Assembly, "UserAccount").Update();
        using SQLiteCommand cmd = new("select count(*) from pragma_table_info('UserAccounts') where name = 'UserCountry'", conn);
        Xunit.Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Xunit.Fact]
    public void MySqlMigrationsAddTheCountryColumn()
    {
        // read from the source tree: this test project does not reference the MySQL store
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string migrations = File.ReadAllText(Path.Combine(root, "Source", "OpenSim.Data.MySQL", "Resources", "UserAccount.migrations"));
        Xunit.Assert.Contains("ADD COLUMN `UserCountry`", migrations);
    }

    [Xunit.Fact]
    public void AnAccountWithACountryIsSavedAndReadBack()
    {
        UserAccountService service = s_service.Value;
        UUID id = UUID.Random();
        UserAccount account = service.CreateUser(UUID.Zero, id, "Country", "Tester" + id.ToString()[..6], "", "");
        Xunit.Assert.NotNull(account);

        account.UserCountry = "NZ";
        Xunit.Assert.True(service.StoreUserAccount(account), "the account with a country was not saved");
        Xunit.Assert.Equal("NZ", service.GetUserAccount(UUID.Zero, id).UserCountry);
    }

    [Xunit.Fact]
    public void SeveralAccountsAreLookedUpAtOnce()
    {
        UserAccountService service = s_service.Value;
        UUID a = UUID.Random(), b = UUID.Random();
        Xunit.Assert.NotNull(service.CreateUser(UUID.Zero, a, "Multi", "A" + a.ToString()[..6], "", ""));
        Xunit.Assert.NotNull(service.CreateUser(UUID.Zero, b, "Multi", "B" + b.ToString()[..6], "", ""));

        // ids that are not UUIDs are left out, never written into the statement
        List<UserAccount> found = service.GetUserAccounts(UUID.Zero,
            [a.ToString(), b.ToString().ToUpperInvariant(), "not-an-id", "x') or ('1'='1"]);

        Xunit.Assert.Equal(new[] { a, b }.OrderBy(x => x.ToString()), found.Select(x => x.PrincipalID).OrderBy(x => x.ToString()));
        Xunit.Assert.Empty(service.GetUserAccounts(UUID.Zero, ["x') or ('1'='1"]));
    }
}
