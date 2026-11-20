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
using System.IO;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The SQLite and PostgreSQL account stores search people on the words of the query that have three characters
/// or more. They removed short words in place and then moved on, which skipped the word that slid into the removed
/// one's slot, so with two short words in a row the second was searched. "a b Ann Smith" left three words, which
/// the stores refuse, so it found nobody; "a b Smith" also found everyone whose first name starts with "b".
/// The SQLite test shares the "SQLite stores" collection, as UserAccountDisplayNameColumnTests explains.
/// </summary>
[Xunit.Collection("SQLite stores")]
public class UserSearchWordsTests
{
    [Xunit.Theory]
    [Xunit.InlineData("Smith", new[] { "Smith" })]
    [Xunit.InlineData("Ann Smith", new[] { "Ann", "Smith" })]
    [Xunit.InlineData("a Smith", new[] { "Smith" })]
    [Xunit.InlineData("a b Smith", new[] { "Smith" })]
    [Xunit.InlineData("Ann a b", new[] { "Ann" })]
    [Xunit.InlineData("a b c", new string[0])]
    [Xunit.InlineData("Ann  Smith", new[] { "Ann", "Smith" })]
    [Xunit.InlineData("", new string[0])]
    public void ShortWordsAreAllDropped(string query, string[] expected)
    {
        Xunit.Assert.Equal(expected, UserSearchWords.From(query));
    }

    [Xunit.Fact]
    public void TwoShortWordsAreBothIgnoredOnSqlite()
    {
        IniConfigSource config = new();
        IConfig section = config.AddConfig("UserAccountService");
        section.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        section.Set("ConnectionString", "Data Source=" + Path.Combine(AppContext.BaseDirectory,
            "user-search-words-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3");
        UserAccountService service = new(config);

        // Last names of their own, since the SQLite stores may share one database across the tests.
        string last = "Search" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string otherLast = "Other" + Guid.NewGuid().ToString("N").Substring(0, 8);
        UUID ann = UUID.Random();
        UUID bert = UUID.Random();
        Xunit.Assert.NotNull(service.CreateUser(UUID.Zero, ann, "Ann", last, "", ""));
        Xunit.Assert.NotNull(service.CreateUser(UUID.Zero, bert, "Bert", otherLast, "", ""));

        // Before: "b" was kept, so three words were left and the search returned nobody.
        Xunit.Assert.Contains(service.GetUserAccounts(UUID.Zero, "a b Ann " + last), a => a.PrincipalID == ann);

        // Before: "b" was kept as a first name, which also matched Bert.
        var found = service.GetUserAccounts(UUID.Zero, "a b " + last);
        Xunit.Assert.Contains(found, a => a.PrincipalID == ann);
        Xunit.Assert.DoesNotContain(found, a => a.PrincipalID == bert);
    }
}
