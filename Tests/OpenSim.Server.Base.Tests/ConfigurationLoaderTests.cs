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
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Base.Tests;

// These tests mutate the process-global working directory, so they must not run
// in parallel with other collections (some of which load config from their own CWD).
[CollectionDefinition("WorkingDirectorySensitive", DisableParallelization = true)]
public sealed class WorkingDirectorySensitiveCollection { }

/// <summary>
/// Ported from the legacy NUnit <c>OpenSim.Tests.ConfigurationLoaderTests</c>.
/// </summary>
[Collection("WorkingDirectorySensitive")]
public sealed class ConfigurationLoaderTests : IDisposable
{
    private const string TestSubdirectory = "test";
    private readonly string _basePath;
    private readonly string _workingDirectory;
    private IConfigSource _config = new IniConfigSource();

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
        const string mainIniFile = "RegionServerDefaults.ini";
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

    /// <summary>
    /// Multiple --inifile values (encoded as inifile, inifile2, ...) are all merged.
    /// </summary>
    [Fact]
    public void MultipleIniFilesAreAllLoaded()
    {
        _config = new IniConfigSource();

        IniConfigSource ini = new IniConfigSource();
        ini.AddConfig("SectionOne").Set("keyA", "valueA");
        CreateIni("first.ini", ini);

        ini = new IniConfigSource();
        ini.AddConfig("SectionTwo").Set("keyB", "valueB");
        CreateIni("second.ini", ini);

        ConfigurationLoader cl = new ConfigurationLoader();
        IConfigSource argvSource = new IniConfigSource();
        IConfig startup = argvSource.AddConfig("Startup");
        startup.Set("inimaster", "none");
        startup.Set("inifile", "first.ini");
        startup.Set("inifile2", "second.ini");
        argvSource.AddConfig("Network");

        IConfigSource source = cl.LoadConfigSettings(
            argvSource, out ConfigSettings _, out NetworkServersInfo _);

        Assert.Equal("valueA", source.Configs["SectionOne"].GetString("keyA"));
        Assert.Equal("valueB", source.Configs["SectionTwo"].GetString("keyB"));
    }

    /// <summary>
    /// A standalone set up as BUILDING.md says, with the shipped RegionServerDefaults.ini and config-include files
    /// and StandaloneCommon.ini copied from its example, loads with no missing include file, and the asset cache
    /// gets its settings from RegionServerDefaults.ini.
    /// </summary>
    [Fact]
    public void FreshStandaloneHasNoMissingIncludeAndAssetCacheDefaults()
    {
        CopyShippedStandalone();

        IConfigSource source;
        using (LogCapture log = new LogCapture())
        {
            source = LoadShippedStandalone();
            Assert.False(log.Contains("Could not find include file"),
                string.Join(Environment.NewLine, log.Messages.Where(m => m.Contains("include", StringComparison.Ordinal))));
        }

        Assert.Equal("FlotsamAssetCache", source.Configs["Modules"].GetString("AssetCaching"));
        IConfig assetCache = source.Configs["AssetCache"];
        Assert.NotNull(assetCache);
        Assert.Equal("./assetcache", assetCache.GetString("CacheDirectory"));
        Assert.True(assetCache.GetBoolean("FileCacheEnabled"));
        Assert.False(assetCache.GetBoolean("MemoryCacheEnabled"));
        Assert.Equal(48.0, assetCache.GetDouble("FileCacheTimeout"));
        Assert.Equal(0.0, assetCache.GetDouble("FileCleanupTimer"));
    }

    /// <summary>
    /// A FlotsamCache.ini made from its example and included as StandaloneCommon.ini.example says still overrides the
    /// defaults.
    /// </summary>
    [Fact]
    public void IncludedFlotsamCacheIniOverridesAssetCacheDefaults()
    {
        CopyShippedStandalone();

        const string commonPath = "config-include/StandaloneCommon.ini";
        string common = File.ReadAllText(commonPath);
        const string commentedInclude = "; Include-FlotsamCache = \"config-include/FlotsamCache.ini\"";
        Assert.Contains(commentedInclude, common);
        File.WriteAllText(commonPath, common.Replace(commentedInclude, commentedInclude.Substring(2)));

        string flotsam = File.ReadAllText("config-include/FlotsamCache.ini.example");
        Assert.Contains("CacheDirectory = ./assetcache", flotsam);
        File.WriteAllText("config-include/FlotsamCache.ini",
            flotsam.Replace("CacheDirectory = ./assetcache", "CacheDirectory = ./mycache"));

        IConfigSource source;
        using (LogCapture log = new LogCapture())
        {
            source = LoadShippedStandalone();
            Assert.False(log.Contains("Could not find include file"));
        }

        Assert.Equal("./mycache", source.Configs["AssetCache"].GetString("CacheDirectory"));
        Assert.Equal(0.0, source.Configs["AssetCache"].GetDouble("FileCleanupTimer"));
    }

    // The region server project copies its AppData folder, with RegionServerDefaults.ini and config-include, to
    // this project's output.
    private static void CopyShippedStandalone()
    {
        string shipped = AppContext.BaseDirectory;
        File.Copy(Path.Combine(shipped, "RegionServerDefaults.ini"), "RegionServerDefaults.ini");
        CopyDirectory(Path.Combine(shipped, "config-include"), "config-include");
        File.Copy("config-include/StandaloneCommon.ini.example", "config-include/StandaloneCommon.ini");

        // The settings a RegionServer.ini made from its example needs for these files to load.
        IniConfigSource ini = new IniConfigSource();
        IConfig constants = ini.AddConfig("Const");
        constants.Set("BaseHostname", "127.0.0.1");
        constants.Set("BaseURL", "http://${Const|BaseHostname}");
        constants.Set("PublicPort", "9000");
        constants.Set("PrivURL", "${Const|BaseURL}");
        constants.Set("PrivatePort", "8003");
        ini.AddConfig("Architecture").Set("Include-Architecture", "config-include/Standalone.ini");
        ini.Save("RegionServer.ini");
    }

    private static IConfigSource LoadShippedStandalone()
    {
        IConfigSource argvSource = new IniConfigSource();
        IConfig startup = argvSource.AddConfig("Startup");
        startup.Set("inimaster", "RegionServerDefaults.ini");
        startup.Set("inifile", "RegionServer.ini");
        argvSource.AddConfig("Network");

        return new ConfigurationLoader().LoadConfigSettings(
            argvSource, out ConfigSettings _, out NetworkServersInfo _);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private void CreateIni(string filepath, IniConfigSource source)
    {
        string? path = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(path))
            Directory.CreateDirectory(path);

        source.Save(filepath);
        _config.Merge(source);
    }
}
