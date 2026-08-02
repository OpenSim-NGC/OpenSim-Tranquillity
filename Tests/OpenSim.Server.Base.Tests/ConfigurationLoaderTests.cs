/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests;

/// <summary>
/// Ported from the legacy NUnit <c>OpenSim.Tests.ConfigurationLoaderTests</c>.
/// </summary>
public sealed class ConfigurationLoaderTests : IDisposable
{
    private const string TestSubdirectory = "test";
    private readonly string _basePath;
    private readonly string _workingDirectory;
    private IConfigSource _config;

    public ConfigurationLoaderTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(_basePath, TestSubdirectory);
        Directory.CreateDirectory(path);

        // The loader resolves ini paths relative to the current directory.
        _workingDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(path);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_workingDirectory);
        Directory.Delete(_basePath, recursive: true);
    }

    /// <summary>
    /// Ini files referenced with absolute and relative Include-* paths are all merged.
    /// </summary>
    [Fact]
    public void IncludeTests()
    {
        const string mainIniFile = "OpenSimDefaults.ini";
        _config = new IniConfigSource();

        IniConfigSource ini;
        IConfig config;

        ini = new IniConfigSource();
        config = ini.AddConfig("IncludeTest");
        config.Set("Include-absolute", "absolute/one/config/setting.ini");
        config.Set("Include-absolute1", "absolute/two/config/setting1.ini");
        config.Set("Include-absolute2", "absolute/two/config/setting2.ini");
        config.Set("Include-relative", "../" + TestSubdirectory + "/relative/one/config/setting.ini");
        config.Set("Include-relative1", "../" + TestSubdirectory + "/relative/two/config/setting1.ini");
        config.Set("Include-relative2", "../" + TestSubdirectory + "/relative/two/config/setting2.ini");
        CreateIni(mainIniFile, ini);

        ini = new IniConfigSource();
        ini.AddConfig("Absolute1").Set("name1", "value1");
        CreateIni("absolute/one/config/setting.ini", ini);

        ini = new IniConfigSource();
        ini.AddConfig("Absolute2").Set("name2", 2.3);
        CreateIni("absolute/two/config/setting1.ini", ini);

        ini = new IniConfigSource();
        ini.AddConfig("Absolute2").Set("name3", "value3");
        CreateIni("absolute/two/config/setting2.ini", ini);

        ini = new IniConfigSource();
        ini.AddConfig("Relative1").Set("name4", "value4");
        CreateIni("relative/one/config/setting.ini", ini);

        ini = new IniConfigSource();
        ini.AddConfig("Relative2").Set("name5", true);
        CreateIni("relative/two/config/setting1.ini", ini);

        ini = new IniConfigSource();
        ini.AddConfig("Relative2").Set("name6", 6);
        CreateIni("relative/two/config/setting2.ini", ini);

        ConfigurationLoader cl = new ConfigurationLoader();
        IConfigSource argvSource = new IniConfigSource();
        argvSource.AddConfig("Startup").Set("inifile", mainIniFile);
        argvSource.AddConfig("Network");

        IConfigSource source = cl.LoadConfigSettings(
            argvSource, out ConfigSettings _, out NetworkServersInfo _);

        // Drop the sections injected by the loader/argv so only the merged includes remain.
        source.Configs.Remove(source.Configs["Startup"]);
        source.Configs.Remove(source.Configs["Network"]);

        Assert.Equal(_config.ToString(), source.ToString());
    }

    private void CreateIni(string filepath, IniConfigSource source)
    {
        string path = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(path))
            Directory.CreateDirectory(path);

        source.Save(filepath);
        _config.Merge(source);
    }
}
