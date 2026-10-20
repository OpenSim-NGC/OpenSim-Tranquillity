/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Runs one test of this project in a child test host: the same test build (dotnet test --no-build, same
/// configuration), filtered to that one test, with the given environment variables added. The child process runs
/// nothing else, so a test that reads process-wide state (the process's memory, a loaded native) sees only its own
/// work there.
/// </summary>
internal static class ChildTestHost
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string TestProject => Path.Combine(RepoRoot(), "Tests", "OpenSim.Region.PhysicsModules.Jolt.Tests");

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    /// <summary>
    /// Runs the test <paramref name="method"/> of <paramref name="testClass"/> in a child test host with
    /// <paramref name="environment"/> added, and asserts that it ran and passed. Returns the child's output.
    /// </summary>
    public static string RunAndAssertPassed(Type testClass, string method, IReadOnlyDictionary<string, string> environment)
    {
        string name = $"{testClass.FullName}.{method}";
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Run where the repository's global.json applies, not wherever the parent test host was started.
            WorkingDirectory = TestProject,
        };
        foreach (string a in new[] { "test", TestProject, "--no-build", "-c", Configuration, "--nologo",
                                     "--filter", $"FullyQualifiedName={name}", "-l", "console;verbosity=detailed" })
            psi.ArgumentList.Add(a);
        // No MSBuild node is left running after the child.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;

        using var p = Process.Start(psi)!;
        // Both streams are read at once, so a full stderr pipe cannot stall the child.
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd() + err.Result;
        Assert.True(p.WaitForExit(15 * 60 * 1000), $"the child test host for {name} did not finish in 15 minutes");
        Assert.True(p.ExitCode == 0, $"the child test host for {name} exited {p.ExitCode}:\n{output}");
        // The detailed console logger: the test's own result line and output, then the run's totals.
        Assert.Contains($"  Passed {name} [", output);
        Assert.Matches(new Regex(@"Total tests: 1\r?\n\s+Passed: 1\r?\n"), output);
        return output;
    }
}
