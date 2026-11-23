/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using MySqlConnector;
using OpenMetaverse;
using OpenSim.Data.MySQL;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The MySQL FSAssets store answers a row it could not insert as not stored. It used to answer every insert as
/// stored, so the service told the region the asset was saved while every later read of it was a 404. A name or
/// description holding a four-byte character, which the <c>utf8</c> table refuses, is stored without it.
///
/// The database tests get a new database on the server named by <see cref="MySQLServerFactAttribute.EnvVar"/>,
/// dropped when they end.
/// </summary>
public class MySQLFSAssetStoreTests : IDisposable
{
    private Action m_cleanup;

    public void Dispose()
    {
        try { m_cleanup?.Invoke(); } catch { /* best effort */ }
        m_cleanup = null;
    }

    [Fact]
    public void MySQL_StoreWithoutTheDatabase_AnswersNotStored()
    {
        // Nothing listens on port 1: opening the connection fails at once.
        MySQLFSAssetData store = new();
        store.Initialise("Server=127.0.0.1;Port=1;Database=fsassets_none;User ID=none;Password=none;Connection Timeout=2;Pooling=false", "fsassets", 0);

        Assert.False(store.Store(Metadata("probe", ""), Hash()));
    }

    [MySQLServerFact]
    public void MySQL_FourByteCharacters_AreLeftOutOfTheRow()
    {
        MySQLFSAssetData store = new();
        store.Initialise(NewDatabase(), "fsassets", 0);

        AssetMetadata emojiName = Metadata("Shirt \U0001F455", "");
        AssetMetadata emojiDescription = Metadata("Plain name", "Made with ❤️ \U0001F308");
        AssetMetadata threeBytes = Metadata("Café ♥", "♥");
        string hash = Hash();

        Assert.True(store.Store(emojiName, hash));
        Assert.True(store.Store(emojiDescription, hash));
        Assert.True(store.Store(threeBytes, hash));

        AssetMetadata read = store.Get(emojiName.ID, out string readHash);
        Assert.NotNull(read);
        Assert.Equal(hash, readHash);
        Assert.Equal("Shirt ", read.Name);

        read = store.Get(emojiDescription.ID, out _);
        Assert.NotNull(read);
        Assert.Equal("Plain name", read.Name);
        Assert.Equal("Made with ❤️ ", read.Description);

        read = store.Get(threeBytes.ID, out _);
        Assert.NotNull(read);
        Assert.Equal("Café ♥", read.Name);
        Assert.Equal("♥", read.Description);
    }

    private static AssetMetadata Metadata(string name, string description)
    {
        UUID id = UUID.Random();
        return new AssetMetadata
        {
            FullID = id,
            ID = id.ToString(),
            Name = name,
            Description = description,
            Type = (sbyte)AssetType.Texture,
        };
    }

    private static string Hash() => Util.SHA256Hash(Guid.NewGuid().ToByteArray());

    /// <summary>Creates a new database on the MySQL or MariaDB server and returns its connection string.</summary>
    private string NewDatabase()
    {
        string server = Environment.GetEnvironmentVariable(MySQLServerFactAttribute.EnvVar);
        string name = "fsassetstore_" + Guid.NewGuid().ToString("N");
        string admin = new MySqlConnectionStringBuilder(server) { Database = "", Pooling = false }.ConnectionString;
        string conn = new MySqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        Admin(admin, $"CREATE DATABASE `{name}`");
        m_cleanup = () =>
        {
            using (MySqlConnection c = new MySqlConnection(conn))
                MySqlConnection.ClearPool(c);
            Admin(admin, $"DROP DATABASE IF EXISTS `{name}`");
        };
        return conn;
    }

    private static void Admin(string admin, string sql)
    {
        using MySqlConnection c = new MySqlConnection(admin);
        c.Open();
        using MySqlCommand cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
