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
using System.Collections.Generic;
using System.Linq;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The console's "import &lt;conn&gt; &lt;table&gt; &lt;start&gt; &lt;count&gt;" reads count rows of a legacy assets table from
/// row start. The PostgreSQL store swapped the two (limit start offset count), so a page read the wrong rows: the
/// first page, from row 0, read none. Pages are in id order, so imports of consecutive pages read every row once.
///
/// The legacy table is the assets table of the PostgreSQL asset store, in a new database on the server named by the
/// environment, dropped when the test ends. MainConsole.Instance is set for the import's progress lines and put back
/// afterwards; this assembly runs its tests one at a time (AssemblyInfo.cs).
/// </summary>
public class PGSQLFSAssetImportTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_ImportPages_ReadEachRowOnce()
    {
        string conn = NewPGSQLDatabase();

        PGSQLAssetData legacy = new();
        legacy.Initialise(conn);
        List<string> ids = new();
        for (int i = 0; i < 7; i++)
        {
            UUID id = UUID.Random();
            AssetBase asset = new AssetBase(id, "legacy " + i, (sbyte)AssetType.Notecard, UUID.Zero.ToString());
            asset.Data = new byte[] { (byte)i, 1, 2 };
            Assert.True(legacy.StoreAsset(asset));
            ids.Add(id.ToString());
        }
        ids.Sort(StringComparer.Ordinal);

        PGSQLFSAssetData store = new();
        store.Initialise(conn, "fsassets", 0);

        ICommandConsole savedConsole = MainConsole.Instance;
        MainConsole.Instance = new MockConsole();
        try
        {
            List<List<string>> pages = new();
            for (int start = 0; start < 9; start += 3)
            {
                List<string> page = new();
                store.Import(conn, "assets", start, 3, false, (asset, force) => { page.Add(asset.ID); return asset.ID; });
                pages.Add(page);
            }

            Assert.Equal(new[] { 3, 3, 1 }, pages.Select(p => p.Count));
            Assert.Equal(ids, pages.SelectMany(p => p));

            List<string> all = new();
            store.Import(conn, "assets", 0, -1, false, (asset, force) => { all.Add(asset.ID); return asset.ID; });
            Assert.Equal(ids, all.OrderBy(id => id, StringComparer.Ordinal));
        }
        finally
        {
            MainConsole.Instance = savedConsole;
        }
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "fsimport_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        PGSQLAdmin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup = () =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            PGSQLAdmin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        };
        return conn;
    }

    private static void PGSQLAdmin(string admin, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(admin);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
