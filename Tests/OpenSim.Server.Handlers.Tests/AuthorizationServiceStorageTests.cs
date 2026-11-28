/*
 * Copyright (c) 2026, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using Nini.Config;
using OpenSim.Services.AuthorizationService;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The authorization service answers every region request without reading a database. It used to refuse to start
/// unless a StorageProvider named an asset database it then never read; now that setting is optional, and a
/// configuration that still names one keeps loading it.
/// </summary>
public class AuthorizationServiceStorageTests
{
    [Fact]
    public void StartsWithoutAStorageProvider()
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("AuthorizationService");

        AuthorizationService service = new(config);

        Assert.True(service.IsAuthorizedForRegion(
            "00000000-0000-0000-0000-000000000001", "Test", "User", "00000000-0000-0000-0000-000000000002",
            out string message));
        Assert.Equal("Authorized", message);
    }

    [Fact]
    public void ANamedStorageProviderIsStillLoaded()
    {
        string db = Path.Combine(Path.GetTempPath(), "authz-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            IConfigSource config = new IniConfigSource();
            IConfig section = config.AddConfig("DatabaseService");
            section.Set("StorageProvider", Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.SQLite.dll"));
            section.Set("ConnectionString", "URI=file:" + db);

            AuthorizationService service = new(config);

            Assert.True(service.IsAuthorizedForRegion(
                "00000000-0000-0000-0000-000000000001", "Test", "User", "00000000-0000-0000-0000-000000000002",
                out _));
            Assert.True(File.Exists(db), "the named asset database was not opened");
        }
        finally
        {
            try { File.Delete(db); } catch (IOException) { /* best effort */ }
        }
    }
}
