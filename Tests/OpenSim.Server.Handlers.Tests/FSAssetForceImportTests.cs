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

using System.Reflection;
using System.Runtime.CompilerServices;
using MySqlConnector;
using Npgsql;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Data.MySQL;
using OpenSim.Data.PGSQL;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.FSAssetService;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The FSAssets console command "force import &lt;conn&gt; &lt;table&gt;" is meant to replace the content of an asset the
/// store already has with the legacy table's content. The service passed the force flag to nobody, and the stores
/// keep an existing row as it is, so a forced import changed nothing. With force the row now gets the new hash; an
/// ordinary import or store still keeps it. The legacy table is the assets table of the matching asset store, in a
/// new database on the server named by OPENSIM_TEST_MYSQL or OPENSIM_TEST_PGSQL, dropped when the test ends.
/// </summary>
public sealed class FSAssetForceImportTests : IDisposable
{
    private readonly string m_dir = Path.Combine(Path.GetTempPath(), "fsassets-force-" + Guid.NewGuid().ToString("N"));
    private readonly List<Action> m_cleanup = new();

    public FSAssetForceImportTests()
    {
        Directory.CreateDirectory(Path.Combine(m_dir, "data"));
        Directory.CreateDirectory(Path.Combine(m_dir, "spool", "spool"));
    }

    public void Dispose()
    {
        foreach (Action a in m_cleanup)
        {
            try { a(); } catch { /* best effort */ }
        }
        Directory.Delete(m_dir, true);
    }

    [MySQLServerFact]
    public void MySQLForceImportReplacesTheContent() => ForceImportReplacesTheContent(MySQL());

    [PGSQLServerFact]
    public void PGSQLForceImportReplacesTheContent() => ForceImportReplacesTheContent(PGSQL());

    private void ForceImportReplacesTheContent((string Conn, IFSAssetDataPlugin Store, AssetDataBase Legacy) db)
    {
        FSAssetConnector service = Service(db.Store);
        UUID id = UUID.Random();
        byte[] stored = Content();
        byte[] legacy = Content();

        AssetBase first = new AssetBase(id, "first", (sbyte)AssetType.Notecard, UUID.Zero.ToString()) { Data = stored };
        Assert.Equal(id.ToString(), service.Store(first));
        AssetBase old = new AssetBase(id, "legacy", (sbyte)AssetType.Notecard, UUID.Zero.ToString()) { Data = legacy };
        Assert.True(db.Legacy.StoreAsset(old));

        // An ordinary import keeps what the store has.
        Import(service, "import", db.Conn);
        Assert.Equal(Util.SHA256Hash(stored), HashOf(db.Store, id));
        Assert.Equal(stored, service.Get(id.ToString())?.Data);

        // A forced import points the row at the legacy content. The type stays.
        Import(service, "force", "import", db.Conn);
        Assert.Equal(Util.SHA256Hash(legacy), HashOf(db.Store, id));
        AssetBase read = service.Get(id.ToString());
        Assert.Equal(legacy, read?.Data);
        Assert.Equal((sbyte)AssetType.Notecard, read!.Type);

        // A later ordinary store of other content for the id still keeps the row.
        AssetBase again = new AssetBase(id, "again", (sbyte)AssetType.Notecard, UUID.Zero.ToString()) { Data = Content() };
        Assert.Equal(id.ToString(), service.Store(again));
        Assert.Equal(Util.SHA256Hash(legacy), HashOf(db.Store, id));
    }

    private static string HashOf(IFSAssetDataPlugin store, UUID id)
    {
        Assert.NotNull(store.Get(id.ToString(), out string hash));
        return hash;
    }

    private static byte[] Content()
    {
        byte[] data = new byte[512];
        Random.Shared.NextBytes(data);
        return data;
    }

    /// <summary>Runs the console command, as "import" or "force import", over the legacy table "assets".</summary>
    private static void Import(FSAssetConnector service, params string[] words)
    {
        MainConsole.Instance ??= new MockConsole();
        string[] args = words.Append("assets").ToArray();
        typeof(FSAssetConnector).GetMethod("HandleImportAssets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { "fsassets", args });
    }

    /// <summary>A service over the store and this test's folders, with no fallback and no writer.</summary>
    private FSAssetConnector Service(IFSAssetDataPlugin store)
    {
        var service = (FSAssetConnector)RuntimeHelpers.GetUninitializedObject(typeof(FSAssetConnector));
        Set(service, "m_DataConnector", store);
        Set(service, "m_SpoolDirectory", Path.Combine(m_dir, "spool"));
        Set(service, "m_FSBase", Path.Combine(m_dir, "data"));
        Set(service, "m_readLock", new object());
        Set(service, "m_statsLock", new object());
        Set(service, "m_showStats", false);
        return service;
    }

    private static void Set(FSAssetConnector service, string field, object value) =>
        (typeof(FSAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(FSAssetConnector), field)).SetValue(service, value);

    private (string, IFSAssetDataPlugin, AssetDataBase) MySQL()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar)!;
        string name = "fsforce_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new MySqlConnection(admin), $"CREATE DATABASE `{name}`");
        m_cleanup.Add(() =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Execute(new MySqlConnection(admin), $"DROP DATABASE IF EXISTS `{name}`");
        });

        MySQLFSAssetData store = new();
        store.Initialise(conn, "fsassets", 0);
        MySQLAssetData legacy = new();
        legacy.Initialise(conn);
        return (conn, store, legacy);
    }

    private (string, IFSAssetDataPlugin, AssetDataBase) PGSQL()
    {
        string server = Environment.GetEnvironmentVariable(PGSQLServerFactAttribute.EnvVar)!;
        string name = "fsforce_" + Guid.NewGuid().ToString("N");
        string admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        string conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Execute(new NpgsqlConnection(admin), $"CREATE DATABASE \"{name}\"");
        m_cleanup.Add(() =>
        {
            using (NpgsqlConnection c = new NpgsqlConnection(conn))
                NpgsqlConnection.ClearPool(c);
            Execute(new NpgsqlConnection(admin), $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        });

        PGSQLFSAssetData store = new();
        store.Initialise(conn, "fsassets", 0);
        PGSQLAssetData legacy = new();
        legacy.Initialise(conn);
        return (conn, store, legacy);
    }

    private static void Execute(System.Data.Common.DbConnection c, string sql)
    {
        using (c)
        {
            c.Open();
            using System.Data.Common.DbCommand cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
