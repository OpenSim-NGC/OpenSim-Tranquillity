/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using OpenSim.Server.GridServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="GridServerConfigSource"/>, which loads the master ini,
/// expands <c>Include-*</c> directives, merges environment variables and resolves
/// <c>${...}</c> key references. Mirrors the include-expansion coverage of the legacy
/// <c>ConfigurationLoaderTests</c>. Includes are resolved relative to the current
/// working directory, so each test runs inside an isolated temp directory.
/// </summary>
public sealed class GridServerConfigSourceTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _workingDirectory;

    public GridServerConfigSourceTests()
    {
        _workingDirectory = Directory.GetCurrentDirectory();
        _basePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_basePath);
        Directory.SetCurrentDirectory(_basePath);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_workingDirectory);
        Directory.Delete(_basePath, recursive: true);
    }

    private static string WriteIni(string fileName, string contents)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Constructor_WhenMasterMissing_DoesNotThrow_AndProducesEmptyConfig()
    {
        var source = new GridServerConfigSource(Path.Combine(_basePath, "does-not-exist.ini"));

        Assert.NotNull(source.m_config);
        Assert.Null(source.m_config.Configs["Startup"]);
    }

    [Fact]
    public void Constructor_LoadsMasterIni()
    {
        string master = WriteIni("master.ini",
            "[Startup]\n" +
            "ServiceConnectors = Foo\n");

        var source = new GridServerConfigSource(master);

        Assert.Equal("Foo", source.m_config.Configs["Startup"].GetString("ServiceConnectors"));
    }

    [Fact]
    public void Constructor_LoadsExplicitIniFiles_WhenMasterMissing()
    {
        // Reproduces the launch scenario where the real config is passed via
        // --inifile and --inimaster stays at its (non-existent) default.
        string iniFile = WriteIni("Robust.ini",
            "[Startup]\n" +
            "ServiceConnectors = Foo\n");

        var source = new GridServerConfigSource(
            iniMaster: Path.Combine(_basePath, "does-not-exist.ini"),
            iniFiles: new[] { iniFile });

        Assert.NotNull(source.m_config.Configs["Startup"]);
        Assert.Equal("Foo", source.m_config.Configs["Startup"].GetString("ServiceConnectors"));
    }

    [Fact]
    public void Constructor_IniFileOverridesMaster()
    {
        string master = WriteIni("master.ini",
            "[Startup]\n" +
            "Value = FromMaster\n");
        string iniFile = WriteIni("override.ini",
            "[Startup]\n" +
            "Value = FromIniFile\n");

        var source = new GridServerConfigSource(master, new[] { iniFile });

        Assert.Equal("FromIniFile", source.m_config.Configs["Startup"].GetString("Value"));
    }

    [Fact]
    public void Constructor_LoadsIniDirectory()
    {
        string dir = Path.Combine(_basePath, "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extra.ini"),
            "[DirSection]\n" +
            "DirKey = DirValue\n");

        var source = new GridServerConfigSource(
            iniMaster: Path.Combine(_basePath, "does-not-exist.ini"),
            iniFiles: null,
            iniDirectory: dir);

        Assert.Equal("DirValue", source.m_config.Configs["DirSection"].GetString("DirKey"));
    }

    [Fact]
    public void Constructor_ExpandsRelativeIncludes()
    {
        WriteIni("extra.ini",
            "[ExtraSection]\n" +
            "ExtraKey = ExtraValue\n");

        string master = WriteIni("master.ini",
            "[Startup]\n" +
            "Include-Extra = extra.ini\n");

        var source = new GridServerConfigSource(master);

        Assert.NotNull(source.m_config.Configs["ExtraSection"]);
        Assert.Equal("ExtraValue", source.m_config.Configs["ExtraSection"].GetString("ExtraKey"));
    }

    [Fact]
    public void Constructor_ResolvesKeyValueReferences()
    {
        string master = WriteIni("master.ini",
            "[Const]\n" +
            "BaseURL = http://example.com\n" +
            "\n" +
            "[GridService]\n" +
            "URL = ${Const|BaseURL}/grid\n");

        var source = new GridServerConfigSource(master);

        Assert.Equal("http://example.com/grid", source.m_config.Configs["GridService"].GetString("URL"));
    }
}
