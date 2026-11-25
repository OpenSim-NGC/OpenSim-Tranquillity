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
using System.Data.Common;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A migration step that fails. Some shipped steps copy rows out of tables from older versions and fail on every new
/// database (PostgreSQL AuthStore, FriendsStore and UserAccount 2; MySQL os_groups_Store 2; SQLite AgentPrefs,
/// AuthStore, FriendsStore, UserAccount and XInventoryStore 2), and the versions after them must still run, so the
/// failed version is still recorded. The failure is logged as a warning that names the store, the version and the
/// database's error; it used to be logged at Debug only, and on MySQL and PostgreSQL without the error.
///
/// The steps come from an in-memory resource set, not from a store's own file. SQLite runs on a private in-memory
/// database; MySQL and PostgreSQL are opt-in and each test gets a new database, dropped when it ends. The log capture
/// swaps a process-wide factory; this assembly runs its tests one at a time (AssemblyInfo.cs).
/// </summary>
public class MigrationFailedStepTests : IDisposable
{
    private const string FailingStore = "TB38FailingStore";
    private const string PassingStore = "TB38PassingStore";

    // Version 2 reads a table that does not exist, as the shipped copy steps do on a new database. Each step is
    // in a transaction, as the shipped steps are.
    private const string FailingSteps =
        ":VERSION 1\nBEGIN;\nCREATE TABLE tb38_items (id INT NOT NULL);\nCOMMIT;\n\n" +
        ":VERSION 2\nBEGIN;\nINSERT INTO tb38_items (id) SELECT id FROM tb38_missing_old_table;\nCOMMIT;\n\n" +
        ":VERSION 3\nBEGIN;\nCREATE TABLE tb38_after (id INT NOT NULL);\nCOMMIT;\n";

    private const string PassingSteps =
        ":VERSION 1\nBEGIN;\nCREATE TABLE tb38_ok (id INT NOT NULL);\nCOMMIT;\n\n" +
        ":VERSION 2\nBEGIN;\nINSERT INTO tb38_ok (id) VALUES (1);\nCOMMIT;\n\n" +
        ":VERSION 3\nBEGIN;\nCREATE TABLE tb38_ok_after (id INT NOT NULL);\nCOMMIT;\n";

    private Action m_cleanup;

    static MigrationFailedStepTests()
    {
        // The System.Data.SQLite native provider must be resolvable before the first use.
        if (Util.IsWindows())
            Util.LoadArchSpecificWindowsDll("sqlite3.dll");
    }

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [Fact]
    public void SQLite_FailedStep_IsAWarningAndLaterStepsRun()
    {
        using SQLiteConnection conn = new SQLiteConnection("Data Source=:memory:");
        conn.Open();
        Check(conn, (a, store) => new Migration(conn, a, store), "no such table");
    }

    [PGSQLServerFact]
    public void PGSQL_FailedStep_IsAWarningAndLaterStepsRun()
    {
        using NpgsqlConnection conn = new NpgsqlConnection(NewPGSQLDatabase());
        conn.Open();
        Check(conn, (a, store) => new PGSQLMigration(conn, a, store), "does not exist");
    }

    [MySQLServerFact]
    public void MySQL_FailedStep_IsAWarningAndLaterStepsRun()
    {
        using MySqlConnection conn = new MySqlConnection(NewMySQLDatabase());
        conn.Open();
        Check(conn, (a, store) => new MySqlMigration(conn, a, store), "doesn't exist");
    }

    private static void Check(DbConnection conn, Func<Assembly, string, Migration> migration, string dbError)
    {
        StepsAssembly steps = new(FailingStore, FailingSteps, PassingStore, PassingSteps);

        using (LogCapture log = new LogCapture())
        {
            Migration failing = migration(steps, FailingStore);
            failing.Update();

            string failed = log.Messages.FirstOrDefault(m => m.Contains(FailingStore + " version 2 failed", StringComparison.Ordinal));
            Assert.True(failed != null, "no failure line for version 2; logged: " + string.Join(" | ", log.Messages));
            Assert.Equal(LogLevel.Warning, log.LevelOf(FailingStore + " version 2 failed"));
            // The database's own error and the statement.
            Assert.Contains(dbError, failed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("INSERT INTO tb38_items", failed, StringComparison.Ordinal);

            // Versions after the failed one still run, as the shipped steps need on a new database.
            Assert.Equal(3, failing.Version);
            Assert.Equal(0, Count(conn, "tb38_items"));
            Assert.Equal(0, Count(conn, "tb38_after"));
        }

        using (LogCapture log = new LogCapture())
        {
            Migration passing = migration(steps, PassingStore);
            passing.Update();

            Assert.Equal(3, passing.Version);
            Assert.Equal(1, Count(conn, "tb38_ok"));
            Assert.Equal(0, Count(conn, "tb38_ok_after"));
            Assert.DoesNotContain(log.Messages, m => m.Contains("failed", StringComparison.Ordinal));
        }
    }

    private static long Count(DbConnection conn, string table)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>An assembly whose only manifest resources are the given stores' migration files.</summary>
    private sealed class StepsAssembly : Assembly
    {
        private readonly (string Name, string Text)[] m_resources;

        public StepsAssembly(string store1, string text1, string store2, string text2)
        {
            m_resources = new[]
            {
                ("TB38.Resources." + store1 + ".migrations", text1),
                ("TB38.Resources." + store2 + ".migrations", text2),
            };
        }

        public override string[] GetManifestResourceNames() => m_resources.Select(r => r.Name).ToArray();

        public override Stream GetManifestResourceStream(string name)
        {
            foreach (var (n, text) in m_resources)
                if (n == name)
                    return new MemoryStream(Encoding.UTF8.GetBytes(text));
            return null;
        }
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "migfail_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        MySQLAdmin(admin, $"CREATE DATABASE `{name}`");
        m_cleanup = () =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            MySQLAdmin(admin, $"DROP DATABASE IF EXISTS `{name}`");
        };
        return conn;
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "migfail_" + Guid.NewGuid().ToString("N");
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
}
