/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Data.Common;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MySqlConnector;
using Npgsql;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Two servers started at the same moment on a new shared database both find no [migrations] table, and the second
/// one to create it used to stop with "Table 'migrations' already exists" (on MySQL a FATAL plugin load failure).
/// Each test makes that happen deterministically: a migration whose check for the table answers "no table" lets a
/// second connection, standing for the other server, create the table and its record before the first one creates
/// it. The first server must then go on and migrate its store, and the record is not doubled.
///
/// SQLite runs on a private shared-cache in-memory database (an anchor connection keeps it alive while the other
/// server's connection opens it); MySQL and PostgreSQL are opt-in and each test gets a new database, dropped when it
/// ends. No process-wide state.
/// </summary>
public class MigrationsTableRaceTests : IDisposable
{
    private const string Store = "TB40RaceStore";
    private const string Steps = ":VERSION 1\nBEGIN;\nCREATE TABLE tb40_items (id INT NOT NULL);\nCOMMIT;\n";

    private Action m_cleanup;

    static MigrationsTableRaceTests()
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
    public void SQLite_MigrationsTableCreatedByAnotherServer_StoreStillMigrates()
    {
        string db = "FullUri=file:migrace_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        using SQLiteConnection anchor = new SQLiteConnection(db);
        anchor.Open();
        using SQLiteConnection conn = new SQLiteConnection(db);
        conn.Open();

        RaceSQLiteMigration migration = new RaceSQLiteMigration(conn, new StepsAssembly(Store, Steps), Store, () =>
        {
            using SQLiteConnection other = new SQLiteConnection(db);
            other.Open();
            new Migration(other, new StepsAssembly(Store, Steps), Store).InitMigrationsTable();
        });

        Check(conn, migration, () => migration.Raced);
    }

    /// <summary>
    /// PostgreSQL can refuse even CREATE TABLE IF NOT EXISTS when another session creates the same table at the same
    /// moment (a duplicate key in its catalog). The refusal is stood for here: the other server creates the table
    /// while this one's statement runs, and the statement throws. The table is there, so the store still migrates.
    /// </summary>
    [Fact]
    public void SQLite_CreateRefusedWhileAnotherServerCreatesTheTable_StoreStillMigrates()
    {
        string db = "FullUri=file:migrace_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared;";
        using SQLiteConnection anchor = new SQLiteConnection(db);
        anchor.Open();
        using SQLiteConnection conn = new SQLiteConnection(db);
        conn.Open();

        RaceSQLiteMigration migration = new RaceSQLiteMigration(conn, new StepsAssembly(Store, Steps), Store, () =>
        {
            using SQLiteConnection other = new SQLiteConnection(db);
            other.Open();
            new Migration(other, new StepsAssembly(Store, Steps), Store).InitMigrationsTable();
        }, refuseCreate: true);

        Check(conn, migration, () => migration.Raced);
    }

    [PGSQLServerFact]
    public void PGSQL_MigrationsTableCreatedByAnotherServer_StoreStillMigrates()
    {
        string db = NewPGSQLDatabase();
        using NpgsqlConnection conn = new NpgsqlConnection(db);
        conn.Open();

        RacePGSQLMigration migration = new RacePGSQLMigration(conn, new StepsAssembly(Store, Steps), Store, () =>
        {
            using NpgsqlConnection other = new NpgsqlConnection(db);
            other.Open();
            new PGSQLMigration(other, new StepsAssembly(Store, Steps), Store).InitMigrationsTable();
        });

        Check(conn, migration, () => migration.Raced);
    }

    [MySQLServerFact]
    public void MySQL_MigrationsTableCreatedByAnotherServer_StoreStillMigrates()
    {
        string db = NewMySQLDatabase();
        using MySqlConnection conn = new MySqlConnection(db);
        conn.Open();

        RaceMySQLMigration migration = new RaceMySQLMigration(conn, new StepsAssembly(Store, Steps), Store, () =>
        {
            using MySqlConnection other = new MySqlConnection(db);
            other.Open();
            new MySqlMigration(other, new StepsAssembly(Store, Steps), Store).InitMigrationsTable();
        });

        Check(conn, migration, () => migration.Raced);
    }

    private static void Check(DbConnection conn, Migration migration, Func<bool> raced)
    {
        migration.Update();

        // The other server really created the table after this one looked for it.
        Assert.True(raced(), "the other server never ran");
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM migrations WHERE name = 'migrations'"));
        Assert.Equal(1, migration.Version);
        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM tb40_items"));
    }

    private static long Scalar(DbConnection conn, string sql)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Runs <paramref name="otherServer"/> once, right after the first check for the [migrations] table found none:
    /// the moment at which a second server starting at the same time creates it.
    /// </summary>
    private static int AfterCheck(int version, string type, ref Action otherServer)
    {
        if (type == "migrations" && version < 0 && otherServer != null)
        {
            Action run = otherServer;
            otherServer = null;
            run();
        }
        return version;
    }

    private sealed class RaceSQLiteMigration : Migration
    {
        private Action m_otherServer;
        private readonly bool m_refuseCreate;

        /// <param name="refuseCreate">The other server runs during the CREATE of the [migrations] table instead of
        /// after the check, and the CREATE then throws.</param>
        public RaceSQLiteMigration(DbConnection conn, Assembly assem, string type, Action otherServer,
            bool refuseCreate = false)
            : base(conn, assem, type)
        {
            m_otherServer = otherServer;
            m_refuseCreate = refuseCreate;
        }

        public bool Raced => m_otherServer == null;

        protected override int FindVersion(DbConnection conn, string type)
        {
            int version = base.FindVersion(conn, type);
            return m_refuseCreate ? version : AfterCheck(version, type, ref m_otherServer);
        }

        protected override void ExecuteScript(DbConnection conn, string[] script)
        {
            if (m_refuseCreate && m_otherServer != null
                && script[0].StartsWith("create table", StringComparison.OrdinalIgnoreCase)
                && script[0].Contains(" migrations(", StringComparison.Ordinal))
            {
                AfterCheck(-1, "migrations", ref m_otherServer);
                throw new Exception("duplicate key value violates unique constraint \"pg_type_typname_nsp_index\" in SQL: " + script[0]);
            }
            base.ExecuteScript(conn, script);
        }
    }

    private sealed class RacePGSQLMigration : PGSQLMigration
    {
        private Action m_otherServer;

        public RacePGSQLMigration(NpgsqlConnection conn, Assembly assem, string type, Action otherServer)
            : base(conn, assem, type)
        {
            m_otherServer = otherServer;
        }

        public bool Raced => m_otherServer == null;

        protected override int FindVersion(DbConnection conn, string type)
            => AfterCheck(base.FindVersion(conn, type), type, ref m_otherServer);
    }

    private sealed class RaceMySQLMigration : MySqlMigration
    {
        private Action m_otherServer;

        public RaceMySQLMigration(DbConnection conn, Assembly assem, string type, Action otherServer)
            : base(conn, assem, type)
        {
            m_otherServer = otherServer;
        }

        public bool Raced => m_otherServer == null;

        protected override int FindVersion(DbConnection conn, string type)
            => AfterCheck(base.FindVersion(conn, type), type, ref m_otherServer);
    }

    /// <summary>An assembly whose only manifest resource is the given store's migration file.</summary>
    private sealed class StepsAssembly : Assembly
    {
        private readonly string m_name;
        private readonly string m_text;

        public StepsAssembly(string store, string text)
        {
            m_name = "TB40.Resources." + store + ".migrations";
            m_text = text;
        }

        public override string[] GetManifestResourceNames() => new[] { m_name };

        public override Stream GetManifestResourceStream(string name)
            => name == m_name ? new MemoryStream(Encoding.UTF8.GetBytes(m_text)) : null;
    }

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewMySQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "migrace_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup = () =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Admin(new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`");
        };
        return conn;
    }

    /// <summary>Creates a new database on the PostgreSQL server and returns its connection string.</summary>
    private string NewPGSQLDatabase()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar);
        string name = "migrace_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup = () =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Admin(new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\"");
        };
        return conn;
    }

    private static void Admin(DbConnection connection, string sql)
    {
        using DbConnection c = connection;
        c.Open();
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
