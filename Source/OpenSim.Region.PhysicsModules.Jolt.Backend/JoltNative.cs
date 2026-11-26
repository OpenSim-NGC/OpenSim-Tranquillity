/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Finds, checks and loads the joltc native for the platform the process runs on.
//
// The native comes from the JoltPhysics.Native package that JoltPhysicsSharp depends on: a build puts the package's
// files under runtimes/<rid>/native/ next to the application's assemblies (a build or publish for one runtime
// identifier puts the file for that platform in the output folder itself). This class picks the file for the running
// platform, checks its SHA-256 against the record below, loads it by full path, and hands that one handle to every
// P/Invoke into "joltc":
//   - JoltPhysicsSharp's own calls, through the binding's JoltApi.JoltDllImporterResolver hook, which its
//     resolver asks before it probes for a joltc of its own (JoltApi.cs in JoltPhysicsSharp 2.22.0). The hook
//     is on an internal class, so it is reached by reflection; without it the binding would load whichever
//     joltc its own probing finds, so a missing hook is an error.
//   - this assembly's own DllImport("joltc") calls (JoltPhysicsBackend), through SetDllImportResolver.
// The load happens once per process; every region and every backend shares it.
//
// The record also says whether a build is safe for more than one job pool. The physics step uses an allocator per
// backend. CharacterVirtual's scratch calls still share one process-wide TempAllocator, so two physics steps at once
// on two pools stop the process; the backend runs one pool whatever [Jolt] JobPools asks
// (JoltPhysicsBackend.ResolveJobPools). More than one pool needs character calls that take an allocator; no recorded
// build has that.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    /// <summary>Where a recorded joltc build comes from.</summary>
    public enum JoltNativeOrigin
    {
        /// <summary>The stock file of the JoltPhysics.Native package (<see cref="JoltNative.PackageId"/>).</summary>
        Package,

        /// <summary>
        /// A deterministic rebuild from named joltc and Jolt commits. Not the bytes of the package file it replaces.
        /// </summary>
        Rebuilt,
    }

    /// <summary>One joltc build the module has a record of.</summary>
    public sealed class JoltNativeBuild
    {
        /// <summary>The runtimes/&lt;folder&gt;/native/ folder the file sits in: a runtime identifier, or "osx" for the
        /// package's one macOS file, which serves both osx-x64 and osx-arm64.</summary>
        public string Folder { get; }
        public string File { get; }
        /// <summary>Upper-case hex.</summary>
        public string Sha256 { get; }
        public JoltNativeOrigin Origin { get; }
        /// <summary>The package version for <see cref="JoltNativeOrigin.Package"/>. Null for a rebuild.</summary>
        public string? PackageVersion { get; }
        /// <summary>Each physics system has its own scratch allocator, so more than one job pool may step at once.</summary>
        public bool SafeForMultiplePools { get; }
        /// <summary>This project's test suite and harness run on this file (on the GitHub runners for its platform).</summary>
        public bool TestedByProject { get; }
        /// <summary>Where the file comes from, in words. A package file is "stock JoltPhysics.Native &lt;version&gt;".</summary>
        public string Source { get; }
        /// <summary>
        /// The harness baseline folder for this file, when it is not "stock-&lt;version&gt;-&lt;folder&gt;".
        /// The linux-x64 file is a deterministic rebuild, so its folder names the joltc commit.
        /// </summary>
        public string? BaselineName { get; }

        public JoltNativeBuild(string folder, string file, string sha256, JoltNativeOrigin origin, string? packageVersion,
                               bool safeForMultiplePools, bool testedByProject, string? source = null, string? baselineName = null)
        {
            Folder = folder;
            File = file;
            Sha256 = sha256;
            Origin = origin;
            PackageVersion = packageVersion;
            SafeForMultiplePools = safeForMultiplePools;
            TestedByProject = testedByProject;
            Source = source ?? $"stock {JoltNative.PackageId} {packageVersion}";
            BaselineName = baselineName;
        }
    }

    /// <summary>What was loaded: the platform, the file, its SHA-256 and the record it matched, if any.</summary>
    public sealed class JoltNativeInfo
    {
        public string Rid { get; }
        public string Path { get; }
        public string Sha256 { get; }
        /// <summary>The recorded build the file matched; null when the hash is not recorded.</summary>
        public JoltNativeBuild? Build { get; }
        public bool Recorded => Build != null;

        /// <summary>
        /// More than one job pool may step at once on this file. Only a recorded build marked safe is: an unrecorded
        /// file may share one allocator between systems as the stock one does, so the module does not assume otherwise.
        /// </summary>
        public bool SafeForMultiplePools => Build?.SafeForMultiplePools ?? false;

        public JoltNativeInfo(string rid, string path, string sha256, JoltNativeBuild? build)
        {
            Rid = rid;
            Path = path;
            Sha256 = sha256;
            Build = build;
        }

        /// <summary>The one line the module logs at start.</summary>
        public string Describe() =>
            $"joltc for {Rid}: {Path} sha256 {Sha256} " +
            (Build == null
                ? "(NOT a build this module has a record of; allowed by [Jolt] AllowUnrecordedNative; one job pool)"
                : $"({Build.Source}; {(Build.SafeForMultiplePools ? "safe for more than one job pool" : "one job pool")})");

        /// <summary>A notice for a recorded file this project's tests do not run on, or null.</summary>
        public string? UntestedNotice =>
            Build is { TestedByProject: false }
                ? $"the joltc for {Rid} ({Build.Source}) is not tested by this project: the module's tests run on linux-x64."
                : null;
    }

    /// <summary>The native cannot be used: no build for this platform, the file is missing, or its hash is not recorded.</summary>
    public sealed class JoltNativeException : Exception
    {
        public JoltNativeException(string message) : base(message) { }
        public JoltNativeException(string message, Exception inner) : base(message, inner) { }
    }

    public static class JoltNative
    {
        /// <summary>The package the stock native comes from, and the version the projects use (the one JoltPhysicsSharp
        /// 2.22.0 depends on). linux-x64 is replaced at load by the deterministic rebuild recorded below; the package
        /// file's own hash stays in the record and is checked against the restored package. A package bump fails
        /// JoltNativePackageTests until the record is checked again.</summary>
        public const string PackageId = "JoltPhysics.Native";
        public const string PackageVersion = "1.1.0";

        /// <summary>
        /// The platforms the module has a native for, by runtime identifier: the runtimes/ folder the package puts the
        /// file in, and the file's name.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, (string Folder, string File)> Platforms =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal)
            {
                ["win-x64"] = ("win-x64", "joltc.dll"),
                ["linux-x64"] = ("linux-x64", "libjoltc.so"),
                ["win-arm64"] = ("win-arm64", "joltc.dll"),
                ["linux-arm64"] = ("linux-arm64", "libjoltc.so"),
                ["osx-x64"] = ("osx", "libjoltc.dylib"),
                ["osx-arm64"] = ("osx", "libjoltc.dylib"),
            };

        /// <summary>
        /// Every joltc build the module knows, with its SHA-256 (upper-case hex). Five are the files of the
        /// JoltPhysics.Native package of <see cref="PackageVersion"/>, including that package's own linux-x64 file.
        /// The other linux-x64 row is the deterministic SSE2 rebuild of joltc 886e0886 (Jolt e77f1755,
        /// CROSS_PLATFORM_DETERMINISTIC) that the build copies over the package file. It is its own origin.
        /// Windows is the package file: not built deterministic here, not tested here, and a follow-up.
        /// </summary>
        public static readonly IReadOnlyList<JoltNativeBuild> Known = new[]
        {
            Stock("win-x64", "joltc.dll", "79125AB6882A1AEEECD5509BFE9EDF383E8E48A34D9A8B642A6B9A197D44DF74", tested: false),
            Stock("linux-x64", "libjoltc.so", "3800CC6FFF9E5470C524B15E8895599F90E9B96129B3F5CF9F76504BEDA178A3", tested: false),
            Rebuilt("linux-x64", "libjoltc.so", "27EFC48C813E75294DA11EAF22618BC1E0A5426A50EDDE7870EF4A60C8E4288B", tested: true,
                source: "joltc 886e0886 Jolt e77f1755 CROSS_PLATFORM_DETERMINISTIC SSE2", baselineName: "det-886e0886-linux-x64"),
            Stock("win-arm64", "joltc.dll", "D32B29B9AADFD7887E4681302A766C5476A196C6AD77FF1F86473BBFB47325E0", tested: false),
            Stock("linux-arm64", "libjoltc.so", "DAF06442FDF25BD2FC4234C252E3862CA3A94E32792A4466A9D93723C8BB03CB", tested: false),
            Stock("osx", "libjoltc.dylib", "386DBEEC5E6892C218F9692572528EE3AF748184C9298861C90AAC1E3443C2EC", tested: false),
        };

        private static JoltNativeBuild Stock(string folder, string file, string sha256, bool tested, string? source = null, string? baselineName = null)
            => new JoltNativeBuild(folder, file, sha256, JoltNativeOrigin.Package, PackageVersion, safeForMultiplePools: false, tested, source, baselineName);

        private static JoltNativeBuild Rebuilt(string folder, string file, string sha256, bool tested, string source, string baselineName)
            => new JoltNativeBuild(folder, file, sha256, JoltNativeOrigin.Rebuilt, packageVersion: null, safeForMultiplePools: false, tested, source, baselineName);

        public static string SupportedList => string.Join(", ", Platforms.Keys);

        /// <summary>The recorded build for the platform with this hash, or null.</summary>
        public static JoltNativeBuild? Find(string rid, string sha256)
        {
            if (!Platforms.TryGetValue(rid, out var platform))
                return null;
            return Known.FirstOrDefault(b => b.Folder == platform.Folder && b.File == platform.File &&
                                             string.Equals(b.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// TEST-ONLY: accept an unrecorded native in every load, as if each caller passed allowUnrecorded = true. The
        /// Jolt tests set it from JOLT_TEST_ACCEPT_UNRECORDED_NATIVE=1 (TestNativeOverride), so the whole suite can run
        /// against another joltc build. Never set by the module.
        /// </summary>
        internal static bool AcceptUnrecordedForTest;

        private static readonly object s_gate = new object();
        private static JoltNativeInfo? s_loaded;
        private static IntPtr s_handle;

        /// <summary>
        /// The runtime identifier of a platform, in the runtimes/&lt;rid&gt;/native/ naming: win-, linux-, linux-musl-
        /// or osx-, then the process architecture (x64, x86, arm64, arm, ...). Null for an OS with no such folder.
        /// </summary>
        public static string? RidFor(OSPlatform? os, Architecture arch, bool musl = false)
        {
            string? prefix = null;
            if (os == OSPlatform.Windows) prefix = "win";
            else if (os == OSPlatform.Linux) prefix = musl ? "linux-musl" : "linux";
            else if (os == OSPlatform.OSX) prefix = "osx";
            else if (os == OSPlatform.FreeBSD) prefix = "freebsd";
            if (prefix == null)
                return null;
            return prefix + "-" + arch.ToString().ToLowerInvariant();
        }

        /// <summary>The runtime identifier of the running process.</summary>
        public static string CurrentRid()
        {
            OSPlatform? os =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OSPlatform.Linux :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX :
                RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? OSPlatform.FreeBSD : (OSPlatform?)null;
            // A glibc build does not load on musl (Alpine); the runtime's own identifier says which libc it was built for.
            bool musl = RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase);
            return RidFor(os, RuntimeInformation.ProcessArchitecture, musl)
                ?? RuntimeInformation.OSDescription.Trim() + " " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        }

        /// <summary>The folder the runtimes/ tree sits in: the folder of this assembly, else the application's base folder.</summary>
        public static string DefaultBaseDirectory()
        {
            string location = typeof(JoltNative).Assembly.Location;
            string? dir = string.IsNullOrEmpty(location) ? null : System.IO.Path.GetDirectoryName(location);
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
        }

        /// <summary>The path of the native for <paramref name="rid"/> under <paramref name="baseDirectory"/>'s runtimes/ tree.</summary>
        public static string PathFor(string baseDirectory, string rid)
        {
            if (!Platforms.TryGetValue(rid, out var platform))
                throw new JoltNativeException(
                    $"Jolt physics has no native library for this platform ({rid}). Supported platforms: {SupportedList}. " +
                    "Choose another physics engine in [Startup] physics.");
            return System.IO.Path.Combine(baseDirectory, "runtimes", platform.Folder, "native", platform.File);
        }

        /// <summary>
        /// The file the module loads for <paramref name="rid"/>: the one under runtimes/, where a portable build puts
        /// it; else the one in <paramref name="baseDirectory"/> itself, where a build or publish for one runtime
        /// identifier puts the package's native. Null when neither exists.
        /// </summary>
        public static string? Locate(string baseDirectory, string rid)
        {
            string underRuntimes = PathFor(baseDirectory, rid);
            if (File.Exists(underRuntimes))
                return underRuntimes;
            string flat = System.IO.Path.Combine(baseDirectory, Platforms[rid].File);
            return File.Exists(flat) ? flat : null;
        }

        public static string Sha256Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>
        /// Finds the native for <paramref name="rid"/> and checks its hash, without loading it. Throws
        /// <see cref="JoltNativeException"/> when the platform has no native, the file is missing, or the hash is
        /// not recorded and <paramref name="allowUnrecorded"/> is false.
        /// </summary>
        public static JoltNativeInfo Check(string baseDirectory, string rid, bool allowUnrecorded)
        {
            string expected = PathFor(baseDirectory, rid);
            string? path = Locate(baseDirectory, rid);
            if (path == null)
                throw new JoltNativeException(
                    $"Jolt physics: the native library for {rid} is missing: {expected}. It comes from the {PackageId} " +
                    $"package, under runtimes/{Platforms[rid].Folder}/native/; copy the whole build output, including the runtimes folder.");

            string hash = Sha256Of(path);
            JoltNativeBuild? build = Find(rid, hash);
            if (build == null && !allowUnrecorded)
                throw new JoltNativeException(
                    $"Jolt physics: {path} has sha256 {hash}, which is not a joltc build this module has a record of for {rid} " +
                    $"(recorded: {RecordedFor(rid)}). Put back the file from the {PackageId} {PackageVersion} package, " +
                    "or set [Jolt] AllowUnrecordedNative = true to run a build you made yourself.");
            return new JoltNativeInfo(rid, path, hash, build);
        }

        private static string RecordedFor(string rid)
        {
            var platform = Platforms[rid];
            return string.Join(", ", Known.Where(b => b.Folder == platform.Folder && b.File == platform.File)
                                          .Select(b => $"{b.Source} {b.Sha256}"));
        }

        /// <summary>
        /// Loads the native for the running platform once per process and routes every "joltc" P/Invoke to it.
        /// Later calls return the first load's result, after checking it against <paramref name="allowUnrecorded"/>.
        /// </summary>
        public static JoltNativeInfo EnsureLoaded(bool allowUnrecorded)
        {
            allowUnrecorded |= AcceptUnrecordedForTest;
            lock (s_gate)
            {
                if (s_loaded != null)
                {
                    if (!s_loaded.Recorded && !allowUnrecorded)
                        throw new JoltNativeException(
                            $"Jolt physics: the loaded {s_loaded.Path} (sha256 {s_loaded.Sha256}) is not a build this module has a record of, " +
                            "and [Jolt] AllowUnrecordedNative is false.");
                    return s_loaded;
                }

                JoltNativeInfo info = Check(DefaultBaseDirectory(), CurrentRid(), allowUnrecorded);

                IntPtr handle;
                try
                {
                    handle = NativeLibrary.Load(info.Path);
                }
                catch (Exception e)
                {
                    throw new JoltNativeException($"Jolt physics: {info.Path} could not be loaded: {e.Message}", e);
                }

                HookBinding();
                s_handle = handle;
                NativeLibrary.SetDllImportResolver(typeof(JoltNative).Assembly, Resolve);
                s_loaded = info;
                return info;
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
            libraryName == "joltc" ? s_handle : IntPtr.Zero;

        /// <summary>The binding's resolver hook, or null when this version of JoltPhysicsSharp has none.</summary>
        public static EventInfo? BindingHook() =>
            typeof(JoltPhysicsSharp.Foundation).Assembly.GetType("JoltPhysicsSharp.JoltApi")
                ?.GetEvent("JoltDllImporterResolver", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        private static void HookBinding()
        {
            EventInfo? hook = BindingHook();
            if (hook == null || hook.EventHandlerType != typeof(DllImportResolver))
                throw new JoltNativeException(
                    "Jolt physics: JoltPhysicsSharp has no JoltApi.JoltDllImporterResolver hook, so the module cannot make it " +
                    "load the checked joltc. The binding is pinned to 2.22.0; a newer one needs this loader updated.");
            hook.AddEventHandler(null, new DllImportResolver(Resolve));
        }
    }
}
