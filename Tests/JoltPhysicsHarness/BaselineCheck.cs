/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The harness regression check: the summary rows of the listed CI runs (ci/runs.txt) compared with the rows recorded
// for the joltc build the runs loaded.
//
// Different joltc builds give different results: the stock JoltPhysics.Native files are not built with Jolt's
// CROSS_PLATFORM_DETERMINISTIC option, so the stock Windows file and the stock Linux file disagree with each other.
// So a baseline belongs to a build, not to an operating system: each run's --out folder holds native.txt, naming the
// build the run loaded (from JoltNative's record), and the check reads the baseline folder for that build:
//   ci/baselines/stock-<version>-<rid>/    the stock file of JoltPhysics.Native <version> for one platform
// A build with no folder (the stock ARM and macOS files, an unrecorded file, a new package version before its rows
// are recorded) is reported as having no baseline: skipped, neither passed nor failed. A run with no file in the
// folder is skipped the same way, so a run whose rows are not repeatable on one machine can stay unrecorded.
//
// Columns measured on the wall clock (WallClockColumns) are left out of the comparison.

using System.Globalization;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>What the check found.</summary>
public enum BaselineOutcome
{
    /// <summary>Every run with a recorded baseline matched it.</summary>
    Passed,
    /// <summary>A run differed from its baseline, a recorded run is missing, or the runs cannot be checked.</summary>
    Failed,
    /// <summary>No baseline for the build the runs loaded, or none for any of the runs.</summary>
    Skipped,
}

public static class BaselineCheck
{
    /// <summary>The file each run's --out folder holds, naming the joltc build the run loaded.</summary>
    public const string NativeFile = "native.txt";

    /// <summary>The summary columns timed on the wall clock, so they differ from run to run and machine to machine.</summary>
    public static readonly IReadOnlySet<string> WallClockColumns = new HashSet<string>(StringComparer.Ordinal) { "ray_us_per_cast" };

    // How many differing cells are listed per run; the count is always given.
    private const int ListedDifferences = 20;

    /// <summary>
    /// The baseline folder name for a joltc build: "stock-&lt;package version&gt;-&lt;runtimes folder&gt;" for a
    /// package file. Null for an unrecorded file.
    /// </summary>
    public static string KeyFor(JoltNativeBuild build) => build == null
        ? null
        : $"stock-{build.PackageVersion}-{build.Folder}";

    /// <summary>The lines of native.txt for the build a run loaded.</summary>
    public static string Describe(JoltNativeInfo info) =>
        $"rid={info.Rid}\nsha256={info.Sha256}\nbuild={(info.Build == null ? "unrecorded" : info.Build.Source)}\n" +
        $"baseline={KeyFor(info.Build) ?? "none"}\n";

    /// <summary>Writes native.txt for the native loaded in this process into <paramref name="outDir"/>.</summary>
    public static void WriteNativeFile(string outDir, JoltNativeInfo info)
        => File.WriteAllText(Path.Combine(outDir, NativeFile), Describe(info));

    /// <summary>
    /// Compares every run under <paramref name="outputRoot"/> (one folder per run, each with summary.csv and
    /// native.txt) with the baseline under <paramref name="baselinesRoot"/> for the build the runs loaded, and writes
    /// what it found to <paramref name="report"/>.
    /// </summary>
    public static BaselineOutcome Check(string outputRoot, string baselinesRoot, TextWriter report)
    {
        if (!Directory.Exists(outputRoot))
        {
            report.WriteLine($"FAILED: no harness output at {outputRoot}");
            return BaselineOutcome.Failed;
        }
        string[] runs = Directory.GetDirectories(outputRoot)
            .Where(d => File.Exists(Path.Combine(d, "summary.csv")))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        if (runs.Length == 0)
        {
            report.WriteLine($"FAILED: no run folder with a summary.csv under {outputRoot}");
            return BaselineOutcome.Failed;
        }

        // Every run must name the same build: one baseline is chosen for all of them.
        var natives = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string run in runs)
        {
            string path = Path.Combine(outputRoot, run, NativeFile);
            if (!File.Exists(path))
            {
                report.WriteLine($"FAILED: {run} has no {NativeFile}, so the build it loaded is not known");
                return BaselineOutcome.Failed;
            }
            natives[run] = ReadKeyValues(path);
        }
        string[] builds = natives.Values.Select(n => Value(n, "sha256")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (builds.Length != 1)
        {
            report.WriteLine("FAILED: the runs loaded different joltc files: " +
                             string.Join("; ", natives.Select(kv => $"{kv.Key} {Value(kv.Value, "build")} {Value(kv.Value, "sha256")}")));
            return BaselineOutcome.Failed;
        }
        Dictionary<string, string> native = natives[runs[0]];
        string key = Value(native, "baseline");
        string what = $"{Value(native, "build")} for {Value(native, "rid")}, sha256 {Value(native, "sha256")}";
        string folder = key is null or "none" ? null : Path.Combine(baselinesRoot, key);
        if (folder == null || !Directory.Exists(folder))
        {
            report.WriteLine($"SKIPPED: no baseline for this native ({what})" +
                             (folder == null ? "" : $"; {Path.Combine(baselinesRoot, key)} does not exist"));
            return BaselineOutcome.Skipped;
        }

        report.WriteLine($"baseline {key} for {what}");
        int passed = 0, failed = 0, skipped = 0;
        foreach (string run in runs)
        {
            string expected = Path.Combine(folder, run + ".csv");
            if (!File.Exists(expected))
            {
                report.WriteLine($"  SKIPPED {run}: no baseline for this run on this native");
                skipped++;
                continue;
            }
            List<string> differences = Compare(File.ReadAllText(expected), File.ReadAllText(Path.Combine(outputRoot, run, "summary.csv")));
            if (differences.Count == 0)
            {
                report.WriteLine($"  passed  {run}");
                passed++;
                continue;
            }
            report.WriteLine($"  FAILED  {run}: {differences.Count} difference(s)");
            foreach (string d in differences.Take(ListedDifferences))
                report.WriteLine("    " + d);
            if (differences.Count > ListedDifferences)
                report.WriteLine($"    ... {differences.Count - ListedDifferences} more");
            failed++;
        }
        foreach (string recorded in Directory.GetFiles(folder, "*.csv").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!runs.Contains(recorded, StringComparer.Ordinal))
            {
                report.WriteLine($"  FAILED  {recorded}: recorded in the baseline but not run");
                failed++;
            }
        }

        report.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped");
        if (failed > 0)
            return BaselineOutcome.Failed;
        return passed > 0 ? BaselineOutcome.Passed : BaselineOutcome.Skipped;
    }

    /// <summary>
    /// The differences between a recorded summary.csv and a new one, one line each: a header or row count that differs,
    /// or a cell (row label, column, recorded value, new value). Wall-clock columns are skipped. Empty when they match.
    /// </summary>
    public static List<string> Compare(string recordedCsv, string actualCsv)
    {
        var differences = new List<string>();
        string[] recorded = Lines(recordedCsv), actual = Lines(actualCsv);
        if (recorded.Length == 0 || actual.Length == 0)
        {
            differences.Add($"empty summary (recorded {recorded.Length} lines, now {actual.Length})");
            return differences;
        }
        if (recorded[0] != actual[0])
        {
            differences.Add($"columns differ: recorded '{recorded[0]}', now '{actual[0]}'");
            return differences;
        }
        string[] columns = recorded[0].Split(',');
        if (recorded.Length != actual.Length)
            differences.Add($"row count differs: recorded {recorded.Length - 1}, now {actual.Length - 1}");
        for (int i = 1; i < Math.Min(recorded.Length, actual.Length); i++)
        {
            string[] a = recorded[i].Split(','), b = actual[i].Split(',');
            if (a.Length != columns.Length || b.Length != columns.Length)
            {
                differences.Add($"row {i}: {a.Length} recorded and {b.Length} new cells for {columns.Length} columns");
                continue;
            }
            for (int c = 0; c < columns.Length; c++)
            {
                if (WallClockColumns.Contains(columns[c]) || a[c] == b[c])
                    continue;
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"row {i} {a[0]}: {columns[c]} recorded {a[c]}, now {b[c]}"));
            }
        }
        return differences;
    }

    /// <summary>
    /// Copies every run's summary.csv under <paramref name="outputRoot"/> into the baseline folder of the build the runs
    /// loaded, as &lt;run&gt;.csv, except the runs in <paramref name="leaveOut"/>. Returns the folder.
    /// </summary>
    public static string Record(string outputRoot, string baselinesRoot, IReadOnlyCollection<string> leaveOut, TextWriter report)
    {
        string[] runs = Directory.GetDirectories(outputRoot)
            .Where(d => File.Exists(Path.Combine(d, "summary.csv")))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        string[] keys = runs.Select(r => Path.Combine(outputRoot, r, NativeFile))
            .Select(p => File.Exists(p) ? Value(ReadKeyValues(p), "baseline") : null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runs.Length == 0 || keys.Length != 1 || keys[0] is null or "none")
            throw new ArgumentException($"--record-baseline: the runs under {outputRoot} do not all name one recorded joltc build in {NativeFile}");
        string folder = Path.Combine(baselinesRoot, keys[0]);
        Directory.CreateDirectory(folder);
        foreach (string run in runs.Where(r => !leaveOut.Contains(r)))
        {
            string text = string.Join("\n", Lines(File.ReadAllText(Path.Combine(outputRoot, run, "summary.csv")))) + "\n";
            File.WriteAllText(Path.Combine(folder, run + ".csv"), text);
            report.WriteLine($"recorded {run} in {keys[0]}");
        }
        return folder;
    }

    /// <summary>
    /// The command line: --check-baseline OUTROOT --baselines DIR, or --record-baseline OUTROOT --baselines DIR
    /// [--leave-out RUN[,RUN..]]. Exit 0 passed (or recorded), 1 failed, 3 skipped (no baseline for this native).
    /// </summary>
    public static int Run(string[] args, TextWriter output)
    {
        string mode = args.Length > 0 ? args[0] : null;
        string outputRoot = args.Length > 1 ? args[1] : throw new ArgumentException($"{mode} needs the folder the runs wrote to");
        string baselines = null;
        var leaveOut = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 2; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--baselines": baselines = Next(); break;
                case "--leave-out" when mode == "--record-baseline": leaveOut.UnionWith(Next().Split(',')); break;
                default: throw new ArgumentException($"unknown argument '{a}'");
            }
        }
        if (baselines == null)
            throw new ArgumentException($"{mode} needs --baselines DIR");

        if (mode == "--record-baseline")
        {
            Record(outputRoot, baselines, leaveOut, output);
            return 0;
        }
        return Check(outputRoot, baselines, output) switch
        {
            BaselineOutcome.Passed => 0,
            BaselineOutcome.Skipped => 3,
            _ => 1,
        };
    }

    private static string[] Lines(string text)
        => text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();

    private static Dictionary<string, string> ReadKeyValues(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Lines(File.ReadAllText(path)))
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
                d[line[..eq]] = line[(eq + 1)..];
        }
        return d;
    }

    private static string Value(Dictionary<string, string> d, string key) => d.TryGetValue(key, out string v) ? v : null;
}
