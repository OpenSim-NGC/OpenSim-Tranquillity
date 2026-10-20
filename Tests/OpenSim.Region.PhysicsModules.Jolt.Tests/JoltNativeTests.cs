/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nini.Config;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// How the module finds its native: the file for the running platform under runtimes/&lt;rid&gt;/native/, its
/// hash checked against the record, an unrecorded build refused unless [Jolt] AllowUnrecordedNative is set, and a
/// platform with no native refused with one clear message. Runs in parallel with the other classes: the only
/// process-wide state it touches is the one-time load, which every backend test shares and which is idempotent.
/// </summary>
public class JoltNativeTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string ModuleRuntimes => Path.Combine(RepoRoot(), "Source", "OpenSim.Region.PhysicsModules.Jolt", "runtimes");

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), "jolt-native-test-" + Guid.NewGuid().ToString("N"));

    // ------------------------------------------------------------------ choosing the file

    [Theory]
    [InlineData("windows", Architecture.X64, false, "win-x64")]
    [InlineData("windows", Architecture.Arm64, false, "win-arm64")]
    [InlineData("linux", Architecture.X64, false, "linux-x64")]
    [InlineData("linux", Architecture.Arm64, false, "linux-arm64")]
    [InlineData("linux", Architecture.X64, true, "linux-musl-x64")]
    [InlineData("osx", Architecture.Arm64, false, "osx-arm64")]
    [InlineData("osx", Architecture.X64, false, "osx-x64")]
    [InlineData("freebsd", Architecture.X64, false, "freebsd-x64")]
    public void The_platform_maps_to_its_runtime_identifier(string os, Architecture arch, bool musl, string rid)
    {
        OSPlatform platform = os switch
        {
            "windows" => OSPlatform.Windows,
            "linux" => OSPlatform.Linux,
            "osx" => OSPlatform.OSX,
            _ => OSPlatform.FreeBSD,
        };
        Assert.Equal(rid, JoltNative.RidFor(platform, arch, musl));
    }

    [Fact]
    public void An_unknown_os_has_no_runtime_identifier()
    {
        Assert.Null(JoltNative.RidFor(null, Architecture.X64));
        Assert.Null(JoltNative.RidFor(OSPlatform.Create("PLAN9"), Architecture.X64));
    }

    [Theory]
    [InlineData("win-x64", "win-x64", "joltc.dll")]
    [InlineData("linux-x64", "linux-x64", "libjoltc.so")]
    [InlineData("win-arm64", "win-arm64", "joltc.dll")]
    [InlineData("linux-arm64", "linux-arm64", "libjoltc.so")]
    [InlineData("osx-x64", "osx", "libjoltc.dylib")]
    [InlineData("osx-arm64", "osx", "libjoltc.dylib")]
    public void Each_platform_gets_its_own_file_under_runtimes(string rid, string folder, string file)
    {
        string baseDir = Path.Combine("any", "app");
        Assert.Equal(Path.Combine(baseDir, "runtimes", folder, "native", file), JoltNative.PathFor(baseDir, rid));
    }

    // The stock record entry for the platform the suite runs on.
    private static JoltNativeBuild StockFor(string rid)
        => JoltNative.Known.Single(b => b.Origin == JoltNativeOrigin.Package && b.Folder == JoltNative.Platforms[rid].Folder);

    [Fact]
    public void The_running_process_loads_the_stock_file_of_the_package_from_runtimes()
    {
        JoltNativeInfo info = JoltNative.EnsureLoaded(allowUnrecorded: false);

        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : null;
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        Assert.NotNull(os);     // the suite runs on Windows, Linux and macOS, on x64 and Arm64
        Assert.NotNull(arch);
        string expectedRid = os + "-" + arch;
        Assert.Equal(expectedRid, info.Rid);
        Assert.Equal(JoltNative.PathFor(JoltNative.DefaultBaseDirectory(), expectedRid), info.Path);
        Assert.Equal(os == "win" ? "joltc.dll" : os == "linux" ? "libjoltc.so" : "libjoltc.dylib", Path.GetFileName(info.Path));
        Assert.True(info.Recorded);
        Assert.Same(StockFor(expectedRid), info.Build);
        Assert.Equal(StockFor(expectedRid).Sha256, info.Sha256);
        Assert.False(info.SafeForMultiplePools);
        // A platform the record marks as tested logs no notice; an untested one logs it.
        Assert.Equal(!info.Build.TestedByProject, info.UntestedNotice != null);
        Assert.Contains(info.Path, info.Describe());
        Assert.Contains(info.Sha256, info.Describe());
        Assert.Contains($"stock JoltPhysics.Native {JoltNative.PackageVersion}; one job pool", info.Describe());

        // Nothing at the output root: the file under runtimes/ is the only one.
        Assert.False(File.Exists(Path.Combine(JoltNative.DefaultBaseDirectory(), "joltc.dll")));
        Assert.False(File.Exists(Path.Combine(JoltNative.DefaultBaseDirectory(), "libjoltc.so")));
        Assert.False(File.Exists(Path.Combine(JoltNative.DefaultBaseDirectory(), "libjoltc.dylib")));
    }

    [Fact]
    public void The_output_holds_one_joltc_per_platform_folder_and_it_is_the_stock_one()
    {
        string runtimes = Path.Combine(JoltNative.DefaultBaseDirectory(), "runtimes");
        var joltcFiles = Directory.GetFiles(runtimes, "*joltc*", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))!))
            .ToDictionary(g => g.Key, g => g.Select(Path.GetFileName).ToArray());
        Assert.All(joltcFiles, kv => Assert.Single(kv.Value));   // no joltc_double beside joltc

        foreach (string rid in JoltNative.Platforms.Keys)
        {
            JoltNativeInfo info = JoltNative.Check(JoltNative.DefaultBaseDirectory(), rid, allowUnrecorded: false);
            Assert.Equal(JoltNativeOrigin.Package, info.Build!.Origin);
        }
    }

    [Fact]
    public void A_build_for_one_runtime_identifier_finds_the_file_beside_the_assemblies()
    {
        // dotnet publish -r <rid> puts the package's native next to the application's assemblies, not under runtimes/.
        string dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string flat = Path.Combine(dir, "libjoltc.so");
            File.WriteAllText(flat, "a native");
            Assert.Equal(flat, JoltNative.Locate(dir, "linux-x64"));
            Assert.Equal(flat, JoltNative.Check(dir, "linux-x64", allowUnrecorded: true).Path);

            // The runtimes/ file wins when both are there.
            string nested = JoltNative.PathFor(dir, "linux-x64");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, "another native");
            Assert.Equal(nested, JoltNative.Locate(dir, "linux-x64"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ the record's entries

    [Fact]
    public void The_record_has_the_stock_files()
    {
        // Stock files of the package version the projects use: tested here on win-x64 and linux-x64, none safe for more
        // than one pool.
        var rows = JoltNative.Known.Select(b => (b.Folder, b.File, b.Origin, b.PackageVersion, b.SafeForMultiplePools, b.TestedByProject))
            .OrderBy(r => r.Origin).ThenBy(r => r.Folder, StringComparer.Ordinal).ToArray();
        var v = JoltNative.PackageVersion;
        Assert.Equal(new[]
        {
            ("linux-arm64", "libjoltc.so", JoltNativeOrigin.Package, (string)v, false, false),
            ("linux-x64", "libjoltc.so", JoltNativeOrigin.Package, (string)v, false, true),
            ("osx", "libjoltc.dylib", JoltNativeOrigin.Package, (string)v, false, false),
            ("win-arm64", "joltc.dll", JoltNativeOrigin.Package, (string)v, false, false),
            ("win-x64", "joltc.dll", JoltNativeOrigin.Package, (string)v, false, true),
        }, rows);
        Assert.Equal(JoltNative.Known.Count, JoltNative.Known.Select(b => b.Sha256).Distinct().Count());
        Assert.All(JoltNative.Known, b => Assert.Matches("^[0-9A-F]{64}$", b.Sha256));
    }

    [Fact]
    public void The_start_line_names_the_build_and_an_untested_platform_gets_a_notice()
    {
        JoltNativeBuild stockWin = JoltNative.Known.Single(b => b.Folder == "win-x64" && b.Origin == JoltNativeOrigin.Package);
        JoltNativeBuild stockOsx = JoltNative.Known.Single(b => b.Folder == "osx");

        var stock = new JoltNativeInfo("win-x64", "p", stockWin.Sha256, stockWin);
        Assert.Equal($"joltc for win-x64: p sha256 {stockWin.Sha256} (stock JoltPhysics.Native 1.0.4; one job pool)", stock.Describe());
        Assert.Null(stock.UntestedNotice);

        // The record has no build safe for more than one pool; the line for one that was, built here.
        var safeBuild = new JoltNativeBuild("linux-x64", "libjoltc.so", new string('A', 64), JoltNativeOrigin.Package, JoltNative.PackageVersion,
                                            safeForMultiplePools: true, testedByProject: true);
        var safe = new JoltNativeInfo("linux-x64", "p", safeBuild.Sha256, safeBuild);
        Assert.Equal($"joltc for linux-x64: p sha256 {safeBuild.Sha256} (stock JoltPhysics.Native 1.0.4; safe for more than one job pool)", safe.Describe());
        Assert.True(safe.SafeForMultiplePools);
        Assert.Null(safe.UntestedNotice);

        var mac = new JoltNativeInfo("osx-arm64", "p", stockOsx.Sha256, stockOsx);
        Assert.Equal("the joltc for osx-arm64 (stock JoltPhysics.Native 1.0.4) is recorded from its package, but this project does not test it: " +
                     "the module's tests run on win-x64 and linux-x64.", mac.UntestedNotice);

        var unrecorded = new JoltNativeInfo("win-x64", "p", new string('0', 64), null);
        Assert.False(unrecorded.SafeForMultiplePools);
        Assert.Null(unrecorded.UntestedNotice);
        Assert.Contains("AllowUnrecordedNative", unrecorded.Describe());
    }

    [Fact]
    public void Find_matches_a_hash_only_on_its_own_platform()
    {
        JoltNativeBuild stockWin = JoltNative.Known.Single(b => b.Folder == "win-x64" && b.Origin == JoltNativeOrigin.Package);
        Assert.Same(stockWin, JoltNative.Find("win-x64", stockWin.Sha256.ToLowerInvariant()));
        Assert.Null(JoltNative.Find("linux-x64", stockWin.Sha256));
        Assert.Null(JoltNative.Find("freebsd-x64", stockWin.Sha256));
        JoltNativeBuild osx = JoltNative.Known.Single(b => b.Folder == "osx");
        Assert.Same(osx, JoltNative.Find("osx-x64", osx.Sha256));
        Assert.Same(osx, JoltNative.Find("osx-arm64", osx.Sha256));
    }

    [Fact]
    public void The_binding_has_the_resolver_hook_the_loader_uses()
    {
        var hook = JoltNative.BindingHook();
        Assert.NotNull(hook);
        Assert.Equal(typeof(DllImportResolver), hook.EventHandlerType);
    }

    // ------------------------------------------------------------------ the hash record

    [Fact]
    public void The_record_matches_the_guard_script_and_the_repository_keeps_no_native()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "OpenSim.Region.PhysicsModules.Jolt.Backend", "assert-joltc-native.ps1"));
        var guardStock = Regex.Matches(guard, "\"([0-9A-F]{64})\"\\s*=\\s*@\\{\\s*Folder\\s*=\\s*\"([^\"]+)\";\\s*File\\s*=\\s*\"([^\"]+)\"")
            .Select(m => (Folder: m.Groups[2].Value, File: m.Groups[3].Value, Sha256: m.Groups[1].Value)).OrderBy(x => x.Folder).ToArray();
        Assert.Equal(JoltNative.Known.Where(b => b.Origin == JoltNativeOrigin.Package)
            .Select(b => (b.Folder, b.File, b.Sha256)).OrderBy(x => x.Folder).ToArray(), guardStock);
        Assert.Contains($"$stockVersion = \"{JoltNative.PackageVersion}\"", guard);

        // The module's project holds no compiled native: the package's file reaches the output through NuGet.
        string[] kept = Directory.Exists(ModuleRuntimes) ? Directory.GetFiles(ModuleRuntimes, "*", SearchOption.AllDirectories) : [];
        Assert.Empty(kept);
    }

    [Fact]
    public void The_readme_hash_tables_match_the_record()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "native", "joltc", "README.md"));
        // The stock files of the package: `JoltPhysics.Native <version>` | `<folder>/<file>` | `<sha256>`.
        var stock = Regex.Matches(readme, @"^\| `JoltPhysics\.Native ([0-9.]+)` \| `([a-z0-9-]+)/([^`]+)` \| `([0-9a-f]{64})` \|", RegexOptions.Multiline)
            .Select(m => (Version: m.Groups[1].Value, Folder: m.Groups[2].Value, File: m.Groups[3].Value, Sha256: m.Groups[4].Value.ToUpperInvariant()))
            .OrderBy(x => x.Folder).ToArray();
        Assert.Equal(JoltNative.Known.Where(b => b.Origin == JoltNativeOrigin.Package)
            .Select(b => (b.PackageVersion, b.Folder, b.File, b.Sha256)).OrderBy(x => x.Folder).ToArray(), stock);
    }

    [Fact]
    public void An_unrecorded_native_is_refused_and_the_key_allows_it()
    {
        string dir = NewTempDir();
        try
        {
            string native = Path.Combine(dir, "runtimes", "win-x64", "native", "joltc.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            File.WriteAllText(native, "not a recorded build");
            string hash = JoltNative.Sha256Of(native);

            var e = Assert.Throws<JoltNativeException>(() => JoltNative.Check(dir, "win-x64", allowUnrecorded: false));
            Assert.Contains(native, e.Message);
            Assert.Contains(hash, e.Message);
            Assert.Contains("AllowUnrecordedNative", e.Message);

            JoltNativeInfo allowed = JoltNative.Check(dir, "win-x64", allowUnrecorded: true);
            Assert.False(allowed.Recorded);
            Assert.Equal(native, allowed.Path);
            Assert.Equal(hash, allowed.Sha256);
            Assert.Contains("AllowUnrecordedNative", allowed.Describe());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ no native

    [Theory]
    [InlineData("linux-musl-x64")]
    [InlineData("freebsd-x64")]
    [InlineData("linux-arm")]
    [InlineData("win-x86")]
    public void A_platform_without_a_native_gets_one_clear_error(string rid)
    {
        var e = Assert.Throws<JoltNativeException>(() => JoltNative.Check(JoltNative.DefaultBaseDirectory(), rid, allowUnrecorded: true));
        Assert.Equal(
            $"Jolt physics has no native library for this platform ({rid}). Supported platforms: win-x64, linux-x64, win-arm64, " +
            "linux-arm64, osx-x64, osx-arm64. Choose another physics engine in [Startup] physics.",
            e.Message);
    }

    // ------------------------------------------------------------------ job pools on the loaded native

    [Fact]
    public void A_native_not_safe_for_pools_gets_one_pool_with_a_reason_and_a_remedy()
    {
        JoltNativeBuild stockWin = JoltNative.Known.Single(b => b.Folder == "win-x64" && b.Origin == JoltNativeOrigin.Package);
        JoltNativeBuild stockArm = JoltNative.Known.Single(b => b.Folder == "linux-arm64");
        // The record has no build safe for more than one pool; ResolveJobPools is checked on one built here.
        var safeWin = new JoltNativeBuild("win-x64", "joltc.dll", new string('A', 64), JoltNativeOrigin.Package, JoltNative.PackageVersion,
                                          safeForMultiplePools: true, testedByProject: true);
        var stock = new JoltNativeInfo("win-x64", "p", stockWin.Sha256, stockWin);
        var safe = new JoltNativeInfo("win-x64", "p", safeWin.Sha256, safeWin);
        var unrecorded = new JoltNativeInfo("win-x64", "p", new string('0', 64), null);

        foreach (int asked in new[] { 0, 1 })
        {
            Assert.Equal(1, JoltPhysicsBackend.ResolveJobPools(asked, stock));
            Assert.Null(JoltPhysicsBackend.JobPoolLimitReason(asked, stock));
            Assert.Null(CapacityReport.JobPoolLimitWarning(asked, stock));
        }
        Assert.Equal(1, JoltPhysicsBackend.ResolveJobPools(3, stock));
        Assert.Equal(1, JoltPhysicsBackend.ResolveJobPools(3, unrecorded));
        Assert.Equal(3, JoltPhysicsBackend.ResolveJobPools(3, safe));
        Assert.Equal(64, JoltPhysicsBackend.ResolveJobPools(100, safe));
        Assert.Null(JoltPhysicsBackend.JobPoolLimitReason(3, safe));
        Assert.Null(CapacityReport.JobPoolLimitWarning(3, safe));

        string reason = "the loaded joltc (stock JoltPhysics.Native 1.0.4) is not safe for more than one job pool: its physics systems " +
                        "share one scratch allocator, and two physics updates at once would stop the process";
        Assert.Equal(reason, JoltPhysicsBackend.JobPoolLimitReason(3, stock));
        Assert.Equal(
            $"[Jolt] JobPools = 3, but the module runs ONE job pool: {reason}. Regions take turns on that pool. More than one pool needs " +
            "a change in joltc itself; set [Jolt] JobPools = 1.",
            CapacityReport.JobPoolLimitWarning(3, stock));
        Assert.Contains("(a build this module has no record of)", JoltPhysicsBackend.JobPoolLimitReason(2, unrecorded));
        Assert.EndsWith("More than one pool needs a change in joltc itself; set [Jolt] JobPools = 1.",
            CapacityReport.JobPoolLimitWarning(2, new JoltNativeInfo("linux-arm64", "p", stockArm.Sha256, stockArm)));
    }

    [Fact]
    public void Jolt_capacity_shows_the_pools_in_use_and_why()
    {
        var asAsked = new PhysicsCapacityStats { JobPools = 2, JobPoolsRequested = 2, JobThreadsPerPool = 4, JobThreadCount = 8 };
        Assert.Equal("2, as [Jolt] JobPools asks", CapacityReport.PoolsInUse(in asAsked));
        var limited = new PhysicsCapacityStats { JobPools = 1, JobPoolsRequested = 3, JobPoolsLimitedBy = "the reason", JobThreadsPerPool = 4, JobThreadCount = 4 };
        Assert.Equal("1 of the 3 [Jolt] JobPools asks for: the reason", CapacityReport.PoolsInUse(in limited));
        Assert.Contains("  pools in use      1 of the 3 [Jolt] JobPools asks for: the reason", CapacityReport.Render("R", in limited, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void A_missing_file_names_the_path_and_the_runtimes_folder()
    {
        string dir = NewTempDir();
        var e = Assert.Throws<JoltNativeException>(() => JoltNative.Check(dir, "linux-x64", allowUnrecorded: false));
        Assert.Contains(Path.Combine(dir, "runtimes", "linux-x64", "native", "libjoltc.so"), e.Message);
        Assert.Contains("missing", e.Message);
        Assert.False(Directory.Exists(dir));
    }

    // ------------------------------------------------------------------ the module

    [Fact]
    public void AllowUnrecordedNative_defaults_off_and_reaches_the_backend()
    {
        Assert.False(JoltConfig.FromConfig(new IniConfigSource(), new List<string>()).AllowUnrecordedNative);
        Assert.False(PhysicsBackendSettings.Default.AllowUnrecordedNative);

        var src = new IniConfigSource();
        src.AddConfig("Jolt").Set("AllowUnrecordedNative", "true");
        var warnings = new List<string>();
        JoltConfig c = JoltConfig.FromConfig(src, warnings);
        Assert.Empty(warnings);
        Assert.True(c.AllowUnrecordedNative);
        Assert.True(c.ToBackendSettings(256, 256, false).AllowUnrecordedNative);
    }

    [Fact]
    public void The_module_loads_the_native_before_it_enables()
    {
        string scene = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "OpenSim.Region.PhysicsModules.Jolt", "JoltScene.cs"));
        int at = scene.IndexOf("public void Initialise(IConfigSource source)", StringComparison.Ordinal);
        string body = scene[at..scene.IndexOf("public void Close()", at, StringComparison.Ordinal)];
        int load = body.IndexOf("NativeLoader(", StringComparison.Ordinal);
        int enable = body.IndexOf("m_Enabled = true;", StringComparison.Ordinal);
        Assert.True(load > 0 && enable > load, "Initialise must load and check the native before it sets m_Enabled");
        Assert.Contains("NativeLoader = JoltNative.EnsureLoaded;", scene);
        Assert.Contains("catch (JoltNativeException e)", body);
        Assert.Contains("m_log.LogError($\"{LogHeader} {e.Message}\");", body);
    }

    [Fact]
    public void The_module_with_physics_Jolt_initialises_on_this_platform()
    {
        var src = new IniConfigSource();
        var startup = src.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        var module = new JoltScene();
        module.Initialise(src);
        Assert.True(JoltNative.EnsureLoaded(false).Recorded);
    }
}
