/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

public sealed class LegacyIniConfigSourceAccessorTests : IDisposable
{
    private readonly string _tempDir;

    public LegacyIniConfigSourceAccessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"opensim-legacy-ini-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteIni(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ConfigSource_IsEmptyWhenNoIniFilesExist()
    {
        var accessor = new LegacyIniConfigSourceAccessor(new ServerStartupOptions
        {
            IniMaster = Path.Combine(_tempDir, "missing.ini"),
        });

        Assert.NotNull(accessor.ConfigSource);
        Assert.Empty(accessor.ConfigSource.Configs);
    }

    [Fact]
    public void ExplicitFile_OverridesMaster()
    {
        string master = WriteIni("master.ini", "[Startup]\nServerPort=9000\n");
        string overrideFile = WriteIni("override.ini", "[Startup]\nServerPort=9001\n");

        var accessor = new LegacyIniConfigSourceAccessor(new ServerStartupOptions
        {
            IniMaster = master,
            IniFiles = [overrideFile],
        });

        Assert.Equal("9001", accessor.ConfigSource.Configs["Startup"].GetString("ServerPort"));
    }

    [Fact]
    public void DirectoryFiles_AreLoadedLastAndSorted()
    {
        string master = WriteIni("master.ini", "[Startup]\nServerPort=100\n");
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        File.WriteAllText(Path.Combine(configDir, "b.ini"), "[Startup]\nServerPort=200\n");
        File.WriteAllText(Path.Combine(configDir, "a.ini"), "[Startup]\nServerPort=150\n");

        var accessor = new LegacyIniConfigSourceAccessor(new ServerStartupOptions
        {
            IniMaster = master,
            IniDirectory = configDir,
        });

        // a.ini then b.ini; b.ini wins.
        Assert.Equal("200", accessor.ConfigSource.Configs["Startup"].GetString("ServerPort"));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new LegacyIniConfigSourceAccessor(null!));
    }
}
