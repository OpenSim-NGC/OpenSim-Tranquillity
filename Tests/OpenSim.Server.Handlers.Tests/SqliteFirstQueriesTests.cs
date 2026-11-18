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

using OpenSim.Data;
using OpenSim.Data.SQLite;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// A store learns its table's columns from its first query. It set the list, still empty, and then filled it: a query
/// on another thread at that moment read its rows with only the columns added so far. Logins at the same moment after a
/// simulator started failed ("Error generating Login Response": KeyNotFoundException 'Created' in
/// UserAccountService.MakeUserAccount). Uses the SQLite stores' files (see SqliteQueryParameterTests).
/// </summary>
[Collection("SQLite stores")]
public class SqliteFirstQueriesTests
{
    private static readonly string s_dir = Environment.GetEnvironmentVariable("SQLITE_QUERY_TEST_DIR")
        is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;

    private static string Connection() =>
        "Data Source=" + Path.Combine(s_dir, "griduser-first-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3";

    [Fact]
    public void QueriesAtTheSameMomentOnANewStoreAllGetEveryColumn()
    {
        string connection = Connection();
        string user = Guid.NewGuid().ToString();
        GridUserData stored = new() { UserID = user };
        stored.Data["Online"] = "True";
        Assert.True(new SQLiteGridUserData(connection, "GridUser").Store(stored));

        const int queries = 16;
        List<string> wrong = new();
        for (int round = 0; round < 20; round++)
        {
            // a new store has not learnt its columns yet
            SQLiteGridUserData store = new(connection, "GridUser");
            using Barrier together = new(queries);
            Task[] tasks = Enumerable.Range(0, queries).Select(_ => Task.Factory.StartNew(() =>
            {
                together.SignalAndWait();
                GridUserData d = store.Get(user);
                if (d is null || !d.Data.ContainsKey("Online") || !d.Data.ContainsKey("Logout"))
                {
                    lock (wrong)
                        wrong.Add(d is null ? "no row" : string.Join(",", d.Data.Keys));
                }
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(tasks);
        }

        Assert.Empty(wrong);
    }
}
