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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>A fact that runs only against a MySQL or MariaDB scratch database named by TRANQUILLITY_MYSQL_TEST_CONN.</summary>
public sealed class MySqlScratchFactAttribute : FactAttribute
{
    public const string EnvVar = "TRANQUILLITY_MYSQL_TEST_CONN";

    public MySqlScratchFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Needs a MySQL scratch database; set {EnvVar} to a connection string to run.";
    }
}

/// <summary>
/// The account store on MySQL: the country column's migration runs, an account with a country is saved and read
/// back, and several accounts are looked up at once with malformed ids left out.
/// </summary>
public class MySqlUserAccountTests
{
    private static UserAccountService Service()
    {
        IniConfigSource config = new();
        IConfig section = config.AddConfig("UserAccountService");
        section.Set("StorageProvider", "OpenSim.Data.MySQL.dll");
        section.Set("ConnectionString", Environment.GetEnvironmentVariable(MySqlScratchFactAttribute.EnvVar));
        return new UserAccountService(config);
    }

    [MySqlScratchFact]
    public void AnAccountWithACountryIsSavedAndReadBack()
    {
        UserAccountService service = Service();
        UUID id = UUID.Random();
        UserAccount account = service.CreateUser(UUID.Zero, id, "Country", "My" + id.ToString()[..8], "", "");
        Assert.NotNull(account);

        account.UserCountry = "NZ";
        Assert.True(service.StoreUserAccount(account), "the account with a country was not saved");
        Assert.Equal("NZ", service.GetUserAccount(UUID.Zero, id).UserCountry);
    }

    [MySqlScratchFact]
    public void SeveralAccountsAreLookedUpAtOnce()
    {
        UserAccountService service = Service();
        UUID a = UUID.Random(), b = UUID.Random();
        Assert.NotNull(service.CreateUser(UUID.Zero, a, "Multi", "A" + a.ToString()[..8], "", ""));
        Assert.NotNull(service.CreateUser(UUID.Zero, b, "Multi", "B" + b.ToString()[..8], "", ""));

        var found = service.GetUserAccounts(UUID.Zero, [a.ToString(), b.ToString(), "x') or ('1'='1"]);
        Assert.Equal(2, found.Count);
        Assert.Empty(service.GetUserAccounts(UUID.Zero, ["x') or ('1'='1"]));
    }
}
