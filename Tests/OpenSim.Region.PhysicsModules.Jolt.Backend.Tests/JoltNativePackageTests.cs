/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The package bump check: the natives of the JoltPhysics.Native package this project restored must be the ones
/// JoltNative.Known records. A new package version, or a package whose files changed, fails here with what to check
/// before its hashes are recorded. Reads the restore's project.assets.json and the package folder it names; runs in
/// parallel with the other classes (it touches no process-wide state).
/// </summary>
public class JoltNativePackageTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    // What to check about a new joltc before its hashes go into the record. The stock native shares one scratch
    // allocator between all physics systems and keeps its map of systems without a lock; the module is safe on it only
    // under these rules (ShapeAndAllocatorRuleTests checks the module's side of each).
    internal const string BeforeRecording =
        "Before recording the new hashes in JoltNative.Known (and PackageVersion, assert-joltc-native.ps1 and native/joltc/README.md), " +
        "re-check against the joltc and Jolt sources the new package is built from:\n" +
        "  1. the pool gate rule: the joltc entry points that use the shared TempAllocator are still exactly the seven listed in " +
        "ShapeAndAllocatorRuleTests, and the module calls the ones that do only inside Step, under the job pool's gate;\n" +
        "  2. the maximum depth rule: CharacterVirtual::SetShape still touches no allocator when given the maximum penetration " +
        "depth (Jolt CharacterVirtual.cpp, the test of mMaxPenetrationDepth < FLT_MAX);\n" +
        "  3. no step listener is used: the map of physics systems is still read only by the step listener callback, and the " +
        "module adds no step listener (ShapeAndAllocatorRuleTests).\n" +
        "Then run the whole Jolt suite and the harness on the new files, and record the harness baselines of the new version " +
        "on win-x64 and linux-x64 (JoltPhysicsHarness --record-baseline; Docs/JoltPhysics.md, \"When the JoltPhysics.Native version changes\").";

    /// <summary>The restored package: its version and the folder its files are in.</summary>
    private static (string Version, string Folder) RestoredPackage()
    {
        string assets = Path.Combine(RepoRoot(), "Tests", "OpenSim.Region.PhysicsModules.Jolt.Backend.Tests", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"no restore found: {assets}");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(assets));
        JsonProperty library = doc.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(p => p.Name.StartsWith(JoltNative.PackageId + "/", StringComparison.OrdinalIgnoreCase));
        string version = library.Name[(JoltNative.PackageId.Length + 1)..];
        string relative = library.Value.GetProperty("path").GetString()!;
        string folder = doc.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(f => Path.Combine(f.Name, relative))
            .First(Directory.Exists);
        return (version, folder);
    }

    [Fact]
    public void The_restored_package_natives_are_the_recorded_ones()
    {
        (string version, string folder) = RestoredPackage();

        var problems = new StringBuilder();
        if (version != JoltNative.PackageVersion)
            problems.AppendLine($"  the projects restore {JoltNative.PackageId} {version}; the record is for {JoltNative.PackageVersion}");

        // Every recorded stock file: present in the package with its recorded hash.
        foreach (JoltNativeBuild b in JoltNative.Known.Where(b => b.Origin == JoltNativeOrigin.Package))
        {
            string path = Path.Combine(folder, "runtimes", b.Folder, "native", b.File);
            if (!File.Exists(path))
                problems.AppendLine($"  runtimes/{b.Folder}/native/{b.File}: recorded, but not in the package");
            else if (JoltNative.Sha256Of(path) is string hash && hash != b.Sha256)
                problems.AppendLine($"  runtimes/{b.Folder}/native/{b.File}: sha256 {hash}, recorded {b.Sha256}");
        }

        // Every joltc of a platform the module supports: recorded.
        var supported = JoltNative.Platforms.Values.ToHashSet();
        foreach (string path in Directory.GetFiles(Path.Combine(folder, "runtimes"), "*", SearchOption.AllDirectories))
        {
            string platformFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
            if (!supported.Contains((platformFolder, Path.GetFileName(path))))
                continue;   // joltc_double, Android: not loaded by the module, kept out of the output (JoltNative.targets)
            if (!JoltNative.Known.Any(b => b.Origin == JoltNativeOrigin.Package && b.Folder == platformFolder && b.File == Path.GetFileName(path)))
                problems.AppendLine($"  runtimes/{platformFolder}/native/{Path.GetFileName(path)}: sha256 {JoltNative.Sha256Of(path)}, not recorded");
        }

        Assert.True(problems.Length == 0,
            $"The {JoltNative.PackageId} package in {folder} does not match the record:\n{problems}{BeforeRecording}");
    }

    [Fact]
    public void The_message_names_the_two_rules_and_the_step_listener()
    {
        Assert.Contains("the pool gate rule", BeforeRecording);
        Assert.Contains("the maximum depth rule", BeforeRecording);
        Assert.Contains("no step listener is used", BeforeRecording);
    }
}
