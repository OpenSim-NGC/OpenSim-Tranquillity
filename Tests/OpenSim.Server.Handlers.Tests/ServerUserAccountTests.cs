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
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The account service on a real MySQL or PostgreSQL server, each test in a new database: the migrations run from
/// empty, an account with a country is saved and read back, and several accounts are looked up at once with malformed
/// ids left out.
/// </summary>
public sealed class ServerUserAccountTests : IDisposable
{
    private readonly List<Action> m_cleanup = new();

    public void Dispose()
    {
        foreach (Action a in m_cleanup)
            a();
    }

    private static UserAccountService Service(string dll, string conn)
    {
        IniConfigSource config = new();
        IConfig section = config.AddConfig("UserAccountService");
        section.Set("StorageProvider", dll);
        section.Set("ConnectionString", conn);
        return new UserAccountService(config);
    }

    private UserAccountService MySQL()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "useracct_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        MySQLAdmin(admin, $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            MySQLAdmin(admin, $"DROP DATABASE IF EXISTS `{name}`");
        });
        return Service("OpenSim.Data.MySQL.dll", conn);
    }

    private UserAccountService PGSQL()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "useracct_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        PGSQLAdmin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            PGSQLAdmin(admin, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        });
        return Service("OpenSim.Data.PGSQL.dll", conn);
    }

    private static void MySQLAdmin(string admin, string sql)
    {
        using MySqlConnection c = new MySqlConnection(admin);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static void PGSQLAdmin(string admin, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(admin);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static void CountryIsSavedAndReadBack(UserAccountService service)
    {
        UUID id = UUID.Random();
        UserAccount account = service.CreateUser(UUID.Zero, id, "Country", "My" + id.ToString()[..8], "", "");
        Assert.NotNull(account);

        account.UserCountry = "NZ";
        Assert.True(service.StoreUserAccount(account), "the account with a country was not saved");
        Assert.Equal("NZ", service.GetUserAccount(UUID.Zero, id).UserCountry);
    }

    private static void SeveralAreLookedUpAtOnce(UserAccountService service)
    {
        UUID a = UUID.Random(), b = UUID.Random();
        Assert.NotNull(service.CreateUser(UUID.Zero, a, "Multi", "A" + a.ToString()[..8], "", ""));
        Assert.NotNull(service.CreateUser(UUID.Zero, b, "Multi", "B" + b.ToString()[..8], "", ""));

        var found = service.GetUserAccounts(UUID.Zero, [a.ToString(), b.ToString(), "x') or ('1'='1"]);
        Assert.Equal(2, found.Count);
        Assert.Empty(service.GetUserAccounts(UUID.Zero, ["x') or ('1'='1"]));
    }

    [MySQLServerFact]
    public void MySQLSavesAnAccountWithACountry() => CountryIsSavedAndReadBack(MySQL());

    [MySQLServerFact]
    public void MySQLLooksUpSeveralAccountsAtOnce() => SeveralAreLookedUpAtOnce(MySQL());

    [PGSQLServerFact]
    public void PGSQLSavesAnAccountWithACountry() => CountryIsSavedAndReadBack(PGSQL());

    [PGSQLServerFact]
    public void PGSQLLooksUpSeveralAccountsAtOnce() => SeveralAreLookedUpAtOnce(PGSQL());
}
