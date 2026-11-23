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
using System.Linq;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The PostgreSQL mute list store keeps several mutes per agent. Version 1 of its migration named the column
/// "MuteId" while MuteData and Delete use "MuteID", so every store failed, and put the primary key on "AgentID"
/// alone, so an agent could keep only one mute. Version 2 renames the column and drops that key; the unique key
/// on ("AgentID", "MuteID", "MuteName") stays, as on MySQL.
///
/// Each test gets a new database on the server named by <see cref="PGSQLServerFactAttribute.EnvVar"/>, dropped
/// when it ends. No process-wide state: it drops only its own connection pool, so the class runs in parallel
/// with others.
/// </summary>
public class PGSQLMuteListStoreTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [PGSQLServerFact]
    public void PGSQL_NewDatabase_MigratesToVersion2()
    {
        string conn = NewDatabase();

        _ = new PGSQLMuteListData(conn);

        Assert.Equal(2, Scalar(conn, "SELECT version FROM migrations WHERE name = 'MuteListStore'"));
        Assert.Equal(new[] { "AgentID", "MuteFlags", "MuteID", "MuteName", "MuteType", "Stamp" }, Columns(conn));
        Assert.Equal(0L, Scalar(conn,
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'mutelist'::regclass AND contype = 'p'"));
        Assert.Equal("AgentID,MuteID,MuteName", Scalar(conn,
            "SELECT string_agg(a.attname, ',' ORDER BY k.ord) FROM pg_constraint c " +
            "CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) " +
            "JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum " +
            "WHERE c.conrelid = 'mutelist'::regclass AND c.contype = 'u'"));
        Assert.Equal(1L, Scalar(conn,
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'mutelist' AND indexname = 'ix_mutelist_agentid'"));
    }

    [PGSQLServerFact]
    public void PGSQL_AnAgent_StoresTwoMutes()
    {
        string conn = NewDatabase();
        UUID agent = UUID.Random();
        MuteData first = Mute(agent, UUID.Random(), "First Resident", 1);
        MuteData second = Mute(agent, UUID.Random(), "Second Resident", 2);

        PGSQLMuteListData store = new PGSQLMuteListData(conn);
        Assert.True(store.Store(first));
        Assert.True(store.Store(second));
        Assert.True(store.Store(Mute(UUID.Random(), UUID.Random(), "Other Agent's Mute", 3)));

        MuteData[] mutes = new PGSQLMuteListData(conn).Get(agent).OrderBy(m => m.Stamp).ToArray();
        Assert.Equal(2, mutes.Length);
        AssertSame(first, mutes[0]);
        AssertSame(second, mutes[1]);
    }

    [PGSQLServerFact]
    public void PGSQL_StoringAnExistingMute_UpdatesIt()
    {
        string conn = NewDatabase();
        UUID agent = UUID.Random();
        MuteData first = Mute(agent, UUID.Random(), "First Resident", 1);
        MuteData second = Mute(agent, UUID.Random(), "Second Resident", 2);

        PGSQLMuteListData store = new PGSQLMuteListData(conn);
        Assert.True(store.Store(first));
        Assert.True(store.Store(second));

        MuteData changed = Mute(agent, first.MuteID, first.MuteName, 5);
        changed.MuteType = 2;
        changed.MuteFlags = 15;
        Assert.True(store.Store(changed));

        MuteData[] mutes = store.Get(agent).OrderBy(m => m.Stamp).ToArray();
        Assert.Equal(2, mutes.Length);
        AssertSame(second, mutes[0]);
        AssertSame(changed, mutes[1]);
    }

    [PGSQLServerFact]
    public void PGSQL_Delete_RemovesThatMuteOnly()
    {
        string conn = NewDatabase();
        UUID agent = UUID.Random();
        MuteData first = Mute(agent, UUID.Random(), "First Resident", 1);
        MuteData second = Mute(agent, UUID.Random(), "Second Resident", 2);

        PGSQLMuteListData store = new PGSQLMuteListData(conn);
        Assert.True(store.Store(first));
        Assert.True(store.Store(second));

        Assert.True(store.Delete(agent, second.MuteID, second.MuteName));

        MuteData[] mutes = store.Get(agent);
        Assert.Single(mutes);
        AssertSame(first, mutes[0]);
    }

    [PGSQLServerFact]
    public void PGSQL_Version1Database_UpgradesKeepingItsRow()
    {
        string conn = NewDatabase();
        UUID agent = UUID.Random();
        UUID muted = UUID.Random();

        // A database left at version 1, holding the one row per agent that version allowed.
        Execute(conn, "create table migrations(name varchar(100), version int)");
        Execute(conn, "insert into migrations(name, version) values('migrations', 1), ('MuteListStore', 1)");
        Execute(conn, Version1Script());
        Execute(conn,
            "INSERT INTO MuteList (\"AgentID\", \"MuteId\", \"MuteName\", \"MuteType\", \"MuteFlags\", \"Stamp\") " +
            $"VALUES ('{agent}', '{muted}', 'Kept Resident', 1, 4, 100)");

        PGSQLMuteListData store = new PGSQLMuteListData(conn);

        Assert.Equal(2, Scalar(conn, "SELECT version FROM migrations WHERE name = 'MuteListStore'"));
        MuteData kept = Mute(agent, muted, "Kept Resident", 100);
        kept.MuteFlags = 4;
        AssertSame(kept, Assert.Single(store.Get(agent)));

        MuteData added = Mute(agent, UUID.Random(), "Added Resident", 200);
        Assert.True(store.Store(added));
        MuteData[] mutes = store.Get(agent).OrderBy(m => m.Stamp).ToArray();
        Assert.Equal(2, mutes.Length);
        AssertSame(kept, mutes[0]);
        AssertSame(added, mutes[1]);
    }

    private static MuteData Mute(UUID agent, UUID muteID, string name, int stamp)
    {
        return new MuteData
        {
            AgentID = agent,
            MuteID = muteID,
            MuteName = name,
            MuteType = 1,
            MuteFlags = 0,
            Stamp = stamp,
        };
    }

    private static void AssertSame(MuteData expected, MuteData actual)
    {
        Assert.Equal(expected.AgentID, actual.AgentID);
        Assert.Equal(expected.MuteID, actual.MuteID);
        Assert.Equal(expected.MuteName, actual.MuteName);
        Assert.Equal(expected.MuteType, actual.MuteType);
        Assert.Equal(expected.MuteFlags, actual.MuteFlags);
        Assert.Equal(expected.Stamp, actual.Stamp);
    }

    /// <summary>The version 1 section of the embedded MuteListStore migrations, as a server ran it.</summary>
    private static string Version1Script()
    {
        var assembly = typeof(PGSQLMuteListData).Assembly;
        string name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(".MuteListStore.migrations", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name);
        using StreamReader reader = new StreamReader(stream);
        string text = reader.ReadToEnd();

        int start = text.IndexOf(":VERSION 1", StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += ":VERSION 1".Length;
        int end = text.IndexOf(":VERSION 2", start, StringComparison.Ordinal);
        return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
    }

    private static string[] Columns(string conn)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'mutelist'", c);
        using NpgsqlDataReader reader = cmd.ExecuteReader();
        var columns = new System.Collections.Generic.List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static object Scalar(string conn, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }

    private static void Execute(string conn, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(conn);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "mutelist_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(admin, $"CREATE DATABASE \"{name}\"");
        m_cleanup = () =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Admin(admin, $"DROP DATABASE IF EXISTS \"{name}\"");
        };
        return conn;
    }

    private static void Admin(string admin, string sql)
    {
        using NpgsqlConnection c = new NpgsqlConnection(admin);
        c.Open();
        using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
