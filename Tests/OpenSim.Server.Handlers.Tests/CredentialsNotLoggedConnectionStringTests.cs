/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Data.SQLite;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Connection strings are logged when a data store connects; the password in them must not be.
/// </summary>
[Collection("LogCapture")]
public class ConnectionStringCredentialLogTests
{
    [Xunit.Theory]
    [Xunit.InlineData("Server=db.example.org;Password=SECRET;Database=grid", "Server=db.example.org;Password=***;Database=grid")]
    [Xunit.InlineData("Server=db.example.org;Database=grid;Password=SECRET", "Server=db.example.org;Database=grid;Password=***")]
    [Xunit.InlineData("Data Source=grid.db;password=SECRET;", "Data Source=grid.db;password=***;")]
    [Xunit.InlineData("Data Source=grid.db;Version=3", "Data Source=grid.db;Version=3")]
    public void DisplayConnectionStringMasksThePasswordWhereverItSits(string connectionString, string expected)
    {
        Xunit.Assert.Equal(expected, Util.GetDisplayConnectionString(connectionString));
    }

    [Xunit.Theory]
    [Xunit.InlineData("estate")]
    [Xunit.InlineData("profiles")]
    public void SqliteStoresLogTheConnectionStringWithoutItsPassword(string store)
    {
        string password = "fake-database-password-" + Guid.NewGuid().ToString("N");
        string connectionString = "Data Source=:memory:;Version=3;Password=" + password;

        using LogCapture log = new();
        // The connection is in memory and the password means nothing to it; whatever happens after the log line
        // is not what is under test. The region store is not driven here: it ends the process when it cannot
        // open its database.
        try
        {
            switch (store)
            {
                case "estate": new SQLiteEstateStore().Initialise(connectionString); break;
                default: new SQLiteUserProfilesData().Initialise(connectionString); break;
            }
        }
        catch (Exception) { }

        Xunit.Assert.False(log.Contains(password), "database password reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information,
            log.LevelOf("Sqlite - connecting: Data Source=:memory:;Version=3;Password=***"));
    }
}
