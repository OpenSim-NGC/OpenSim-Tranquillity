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
//     resolver asks before it probes for a joltc of its own (JoltApi.cs in JoltPhysicsSharp at the commit
//     behind 2.19.1). The hook is on an internal class, so it is reached by reflection; without it the
//     binding would load whichever joltc its own probing finds, so a missing hook is an error.
//   - this assembly's own DllImport("joltc") calls (JoltPhysicsBackend), through SetDllImportResolver.
// The load happens once per process; every region and every backend shares it.
//
// The record also says whether a build is safe for more than one job pool. The stock joltc gives every physics system
// one process-wide TempAllocator, so two physics updates at once on two pools stop the process; on it the backend runs
// one pool whatever [Jolt] JobPools asks (JoltPhysicsBackend.ResolveJobPools). More than one pool needs a joltc that
// gives each system its own allocator; no recorded build does.

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
        /// <summary>The package version for <see cref="JoltNativeOrigin.Package"/>.</summary>
        public string? PackageVersion { get; }
        /// <summary>Each physics system has its own scratch allocator, so more than one job pool may step at once.</summary>
        public bool SafeForMultiplePools { get; }
        /// <summary>This project's test suite and harness run on this file (on the GitHub runners for its platform).</summary>
        public bool TestedByProject { get; }

        public JoltNativeBuild(string folder, string file, string sha256, JoltNativeOrigin origin, string? packageVersion,
                               bool safeForMultiplePools, bool testedByProject)
        {
            Folder = folder;
            File = file;
            Sha256 = sha256;
            Origin = origin;
            PackageVersion = packageVersion;
            SafeForMultiplePools = safeForMultiplePools;
            TestedByProject = testedByProject;
        }

        /// <summary>Where the file comes from, in words: "stock JoltPhysics.Native 1.0.4".</summary>
        public string Source => $"stock {JoltNative.PackageId} {PackageVersion}";
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
                ? $"the joltc for {Rid} ({Build.Source}) is recorded from its package, but this project does not test it: " +
                  "the module's tests run on win-x64 and linux-x64."
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
        /// 2.19.1 depends on). A package bump fails JoltNativePackageTests until the record below is checked again.</summary>
        public const string PackageId = "JoltPhysics.Native";
        public const string PackageVersion = "1.0.4";

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
        /// Every joltc build the module knows, with its SHA-256 (upper-case hex). The package entries are read from the
        /// JoltPhysics.Native package of <see cref="PackageVersion"/>.
        /// </summary>
        public static readonly IReadOnlyList<JoltNativeBuild> Known = new[]
        {
            Stock("win-x64", "joltc.dll", "67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910", tested: true),
            Stock("linux-x64", "libjoltc.so", "5FC051708BDD05031A816796612A2F87E17ED32CC198F195A43B2305B3D990FD", tested: true),
            Stock("win-arm64", "joltc.dll", "B0A7D05151A8E504765A39E23B2EC196B88B3BF2CE3E8B884161E6925E7578E4", tested: false),
            Stock("linux-arm64", "libjoltc.so", "FDE70508C826370B5CF6BB54C6EA9CEEAB0C37C826BCD71579EBB9B4AC3E0D61", tested: false),
            Stock("osx", "libjoltc.dylib", "39E4A8728307E48026D965D8AB661348A8808A9A2AFDDDE6CE58272B37D83E91", tested: false),
        };

        private static JoltNativeBuild Stock(string folder, string file, string sha256, bool tested)
            => new JoltNativeBuild(folder, file, sha256, JoltNativeOrigin.Package, PackageVersion, safeForMultiplePools: false, tested);

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
                    "load the checked joltc. The binding is pinned to 2.19.1; a newer one needs this loader updated.");
            hook.AddEventHandler(null, new DllImportResolver(Resolve));
        }
    }
}
