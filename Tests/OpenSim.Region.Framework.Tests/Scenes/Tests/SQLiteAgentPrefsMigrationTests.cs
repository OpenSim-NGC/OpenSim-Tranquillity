/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenSim.Data;
using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The SQLite AgentPrefs migrations, run as SQLiteAgentPreferencesData runs them (a Migration over the SQLite
/// assembly's AgentPrefs.migrations). Version 1 first used MySQL syntax that SQLite refuses and made no table, and
/// version 2 made it; version 1 was fixed later, after which version 2 failed on every new database with "table
/// AgentPrefs already exists". A new database, a database whose version 1 made the table and one whose version 1
/// made none all end at version 2 with the table, and no step fails.
///
/// Each test runs on a private in-memory database. The log capture swaps a process-wide factory; this assembly runs
/// its tests one at a time (AssemblyInfo.cs).
/// </summary>
public class SQLiteAgentPrefsMigrationTests
{
    private const string Store = "AgentPrefs";

    private static readonly string[] Columns =
    {
        "PrincipalID", "AccessPrefs", "HoverHeight", "Language", "LanguageIsPublic",
        "PermEveryone", "PermGroup", "PermNextOwner",
    };

    static SQLiteAgentPrefsMigrationTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    private static Assembly StoreAssembly => typeof(SQLiteAgentPreferencesData).Assembly;

    [Fact]
    public void NewDatabase_MigratesWithNoFailedStep()
    {
        using SQLiteConnection conn = Open();

        Migrate(conn);

        CheckTable(conn);
    }

    [Fact]
    public void DatabaseAtVersion1_UpgradesWithNoFailedStepAndKeepsItsRows()
    {
        using SQLiteConnection conn = Open();
        Execute(conn, Version1Script());
        Execute(conn, "create table migrations(name varchar(100), version int);" +
                      "insert into migrations(name, version) values('migrations', 1);" +
                      "insert into migrations(name, version) values('AgentPrefs', 1);");
        Execute(conn, "insert into AgentPrefs(PrincipalID, AccessPrefs, Language) " +
                      "values('11111111-2222-3333-4444-555555555555', 'A', 'de')");

        Migrate(conn);

        CheckTable(conn);
        using SQLiteCommand cmd = new SQLiteCommand(
            "select AccessPrefs, Language from AgentPrefs where PrincipalID = '11111111-2222-3333-4444-555555555555'",
            conn);
        using SQLiteDataReader reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the row stored at version 1 is gone");
        Assert.Equal("A", reader.GetString(0));
        Assert.Equal("de", reader.GetString(1));
    }

    /// <summary>A database whose version 1 was the one SQLite refused: version 1 is recorded but there is no table.</summary>
    [Fact]
    public void DatabaseAtVersion1WithNoTable_GetsTheTable()
    {
        using SQLiteConnection conn = Open();
        Execute(conn, "create table migrations(name varchar(100), version int);" +
                      "insert into migrations(name, version) values('migrations', 1);" +
                      "insert into migrations(name, version) values('AgentPrefs', 1);");

        Migrate(conn);

        CheckTable(conn);
    }

    private static SQLiteConnection Open()
    {
        SQLiteConnection conn = new SQLiteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    /// <summary>Runs the store's migrations and checks that none of its steps failed.</summary>
    private static void Migrate(SQLiteConnection conn)
    {
        using LogCapture log = new LogCapture();
        Migration migration = new Migration(conn, StoreAssembly, Store);
        migration.Update();

        string failed = log.Messages.FirstOrDefault(m => m.Contains(Store + " version", StringComparison.Ordinal)
                                                         && m.Contains("failed", StringComparison.Ordinal));
        Assert.True(failed == null, failed);
        Assert.Equal(2, migration.Version);
    }

    /// <summary>The table the MySQL and PostgreSQL stores make: these columns, keyed by PrincipalID, with the defaults
    /// a new agent's preferences read.</summary>
    private static void CheckTable(SQLiteConnection conn)
    {
        List<(string Name, bool Key)> info = new();
        using (SQLiteCommand cmd = new SQLiteCommand("pragma table_info(AgentPrefs)", conn))
        using (SQLiteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                info.Add((reader.GetString(1), Convert.ToInt32(reader.GetValue(5)) != 0));
        }
        Assert.Equal(Columns, info.Select(c => c.Name));
        Assert.Equal(new[] { "PrincipalID" }, info.Where(c => c.Key).Select(c => c.Name));

        string id = Guid.NewGuid().ToString();
        Execute(conn, "insert into AgentPrefs(PrincipalID) values('" + id + "')");
        using (SQLiteCommand cmd = new SQLiteCommand(
                   "select AccessPrefs, Language, LanguageIsPublic, PermNextOwner from AgentPrefs where PrincipalID = '" + id + "'",
                   conn))
        using (SQLiteDataReader reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal("M", reader.GetString(0));
            Assert.Equal("en-us", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.Equal(532480, reader.GetInt32(3));
        }
        Assert.Throws<SQLiteException>(() => Execute(conn, "insert into AgentPrefs(PrincipalID) values('" + id + "')"));
    }

    /// <summary>The shipped version 1 step, as Migration reads it.</summary>
    private static string Version1Script()
    {
        string name = StoreAssembly.GetManifestResourceNames().Single(n => n.EndsWith("." + Store + ".migrations"));
        using Stream stream = StoreAssembly.GetManifestResourceStream(name);
        using StreamReader reader = new StreamReader(stream);
        Match v1 = Regex.Match(reader.ReadToEnd(), @"^:VERSION 1\s*$(?<sql>.*?)^:VERSION 2",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(v1.Success, "no version 1 step");
        return v1.Groups["sql"].Value;
    }

    private static void Execute(SQLiteConnection conn, string sql)
    {
        using SQLiteCommand cmd = new SQLiteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
