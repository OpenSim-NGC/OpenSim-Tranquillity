/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Xunit;
using Microsoft.Extensions.Configuration;
using OpenSim.Server.Base.Hosting;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Tests for <see cref="IniConfigurationExtensions.AddOpenSimIniFiles"/>.
/// Each test writes temporary ini files so the assertions run against real file I/O.
/// </summary>
public sealed class IniConfigurationExtensionsTests : IDisposable
{
    private readonly string _tempDir;

    public IniConfigurationExtensionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"opensim-ini-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string WriteIni(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static IConfiguration Build(ServerStartupOptions opts)
        => new ConfigurationBuilder()
               .AddOpenSimIniFiles(opts)
               .Build();

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void MasterIni_IsLoaded()
    {
        string master = WriteIni("master.ini", "[Startup]\nServerPort=9000\n");

        var config = Build(new ServerStartupOptions { IniMaster = master });

        Assert.Equal("9000", config["Startup:ServerPort"]);
    }

    [Fact]
    public void ExplicitIniFile_OverridesMaster()
    {
        string master   = WriteIni("master.ini",   "[Startup]\nServerPort=9000\n");
        string override_ = WriteIni("override.ini", "[Startup]\nServerPort=9001\n");

        var config = Build(new ServerStartupOptions
        {
            IniMaster = master,
            IniFiles  = [override_],
        });

        Assert.Equal("9001", config["Startup:ServerPort"]);
    }

    [Fact]
    public void DirectoryInis_AreLoadedAfterExplicitFiles()
    {
        string master  = WriteIni("master.ini", "[Startup]\nServerPort=9000\n");
        string dirFile = WriteIni("z_dir.ini",  "[Startup]\nServerPort=9002\n");

        // Put the directory file inside a subdirectory that is passed as IniDirectory.
        string subDir = Path.Combine(_tempDir, "cfg");
        Directory.CreateDirectory(subDir);
        string dirIni = Path.Combine(subDir, "z_dir.ini");
        File.Copy(dirFile, dirIni);

        var config = Build(new ServerStartupOptions
        {
            IniMaster    = master,
            IniDirectory = subDir,
        });

        Assert.Equal("9002", config["Startup:ServerPort"]);
    }

    [Fact]
    public void DirectoryInis_AreSortedAlphabetically()
    {
        // Two directory ini files: b sets port 2, a sets port 1.
        // After alphabetical sort: a.ini runs first (port=1), b.ini runs second (port=2).
        // Highest-precedence file wins → port should be 2.
        string dir = Path.Combine(_tempDir, "sorted");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "b_last.ini"),  "[Startup]\nServerPort=2\n");
        File.WriteAllText(Path.Combine(dir, "a_first.ini"), "[Startup]\nServerPort=1\n");

        var config = Build(new ServerStartupOptions { IniDirectory = dir });

        Assert.Equal("2", config["Startup:ServerPort"]);
    }

    [Fact]
    public void MissingMasterIni_DoesNotThrow()
    {
        var opts = new ServerStartupOptions
        {
            IniMaster = Path.Combine(_tempDir, "nonexistent.ini"),
        };

        // Should not throw — files are registered as optional.
        var config = Build(opts);
        Assert.Null(config["Startup:ServerPort"]);
    }

    [Fact]
    public void MissingDirectory_IsIgnoredGracefully()
    {
        var opts = new ServerStartupOptions
        {
            IniDirectory = Path.Combine(_tempDir, "does_not_exist"),
        };

        var config = Build(opts);
        Assert.Null(config["Startup:ServerPort"]);
    }

    [Fact]
    public void NullOptions_ThrowsArgumentNullException()
    {
        var builder = new ConfigurationBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.AddOpenSimIniFiles(null!));
    }

    [Fact]
    public void NullBuilder_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IConfigurationBuilder)null!).AddOpenSimIniFiles(new ServerStartupOptions()));
    }
}
