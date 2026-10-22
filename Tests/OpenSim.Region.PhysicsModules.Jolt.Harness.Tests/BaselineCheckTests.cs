/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The harness regression check (BaselineCheck): the baseline is chosen by the joltc build the runs loaded, a build
/// with no baseline is skipped, and the committed baselines match the run list. Files only, each test in its own
/// temporary folder, no native loaded: runs in parallel with the other classes.
/// </summary>
public class BaselineCheckTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string CommittedBaselines => Path.Combine(RepoRoot(), "Tests", "JoltPhysicsHarness", "ci", "baselines");

    private static readonly string Header = RunResult.SummaryHeader;
    private static readonly int Columns = Header.Split(',').Length;

    // A summary row: the label, then every other column 0 except those given (column name -> value).
    private static string Row(string label, params (string Column, string Value)[] cells)
    {
        string[] names = Header.Split(',');
        var values = Enumerable.Repeat("0", Columns).ToArray();
        values[0] = label;
        foreach (var (column, value) in cells)
            values[Array.IndexOf(names, column)] = value;
        return string.Join(",", values);
    }

    private static string Summary(params string[] rows) => Header + "\n" + string.Join("\n", rows) + "\n";

    private static JoltNativeBuild Build(JoltNativeOrigin origin, string folder)
        => JoltNative.Known.Single(b => b.Origin == origin && b.Folder == folder);

    private static JoltNativeInfo Info(string rid, JoltNativeBuild build, string sha = null)
        => new JoltNativeInfo(rid, "/x/" + rid, sha ?? build?.Sha256 ?? "00", build);

    // A temporary folder per test. Nothing is removed afterwards: the check never writes outside it.
    private sealed class Scratch
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "jolt-baseline-test-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "out");
        public string Baselines => Path.Combine(Root, "baselines");

        public void AddRun(string run, string summary, JoltNativeInfo native)
        {
            string dir = Path.Combine(Output, run);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "summary.csv"), summary);
            BaselineCheck.WriteNativeFile(dir, native);
        }

        public void AddBaseline(string key, string run, string summary)
        {
            string dir = Path.Combine(Baselines, key);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, run + ".csv"), summary);
        }

        public (BaselineOutcome Outcome, string Report) Check()
        {
            var w = new StringWriter();
            BaselineOutcome o = BaselineCheck.Check(Output, Baselines, w);
            return (o, w.ToString());
        }
    }

    // ------------------------------------------------------------------ the baseline a build gets

    [Fact]
    public void Each_recorded_build_names_its_baseline_folder()
    {
        Assert.Equal($"stock-{JoltNative.PackageVersion}-win-x64", BaselineCheck.KeyFor(Build(JoltNativeOrigin.Package, "win-x64")));
        Assert.Equal($"stock-{JoltNative.PackageVersion}-linux-x64", BaselineCheck.KeyFor(Build(JoltNativeOrigin.Package, "linux-x64")));
        Assert.Equal($"stock-{JoltNative.PackageVersion}-osx", BaselineCheck.KeyFor(Build(JoltNativeOrigin.Package, "osx")));
        Assert.Null(BaselineCheck.KeyFor(null));
    }

    [Fact]
    public void The_native_file_names_the_build_and_its_baseline()
    {
        string stock = BaselineCheck.Describe(Info("linux-x64", Build(JoltNativeOrigin.Package, "linux-x64")));
        Assert.Contains("rid=linux-x64\n", stock);
        Assert.Contains($"build=stock JoltPhysics.Native {JoltNative.PackageVersion}\n", stock);
        Assert.Contains($"baseline=stock-{JoltNative.PackageVersion}-linux-x64\n", stock);

        string unrecorded = BaselineCheck.Describe(Info("win-x64", null, "ABCD"));
        Assert.Contains("build=unrecorded\n", unrecorded);
        Assert.Contains("baseline=none\n", unrecorded);
    }

    // The two stock platforms' files get their own baselines.
    [Fact]
    public void The_baseline_is_chosen_by_the_loaded_build_not_by_the_platform()
    {
        var s = new Scratch();
        string stockWinRows = Summary(Row("drop", ("end_z", "25.501")));
        string stockLinuxRows = Summary(Row("drop", ("end_z", "25.502")));
        s.AddBaseline($"stock-{JoltNative.PackageVersion}-win-x64", "drop-run", stockWinRows);
        s.AddBaseline($"stock-{JoltNative.PackageVersion}-linux-x64", "drop-run", stockLinuxRows);

        foreach (var (rid, origin, rows) in new[]
                 {
                     ("win-x64", JoltNativeOrigin.Package, stockWinRows),
                     ("linux-x64", JoltNativeOrigin.Package, stockLinuxRows),
                 })
        {
            JoltNativeBuild build = Build(origin, rid);
            s.AddRun("drop-run", rows, Info(rid, build));
            var (outcome, report) = s.Check();
            Assert.True(outcome == BaselineOutcome.Passed, $"{rid} {origin}: {report}");
            Assert.Contains($"baseline {BaselineCheck.KeyFor(build)} for", report);

            // Any other build's rows fail against this build's baseline.
            string other = rows == stockWinRows ? stockLinuxRows : stockWinRows;
            s.AddRun("drop-run", other, Info(rid, build));
            Assert.Equal(BaselineOutcome.Failed, s.Check().Outcome);
        }
    }

    [Fact]
    public void A_build_with_no_baseline_folder_is_skipped_not_passed_or_failed()
    {
        var s = new Scratch();
        s.AddBaseline($"stock-{JoltNative.PackageVersion}-win-x64", "drop-run", Summary(Row("drop")));
        s.AddRun("drop-run", Summary(Row("drop", ("end_z", "99"))), Info("linux-arm64", Build(JoltNativeOrigin.Package, "linux-arm64")));
        var (outcome, report) = s.Check();
        Assert.Equal(BaselineOutcome.Skipped, outcome);
        Assert.StartsWith("SKIPPED: no baseline for this native (stock JoltPhysics.Native", report);
        Assert.Contains("linux-arm64", report);

        var w = new StringWriter();
        Assert.Equal(3, BaselineCheck.Run(new[] { "--check-baseline", s.Output, "--baselines", s.Baselines }, w));
    }

    [Fact]
    public void An_unrecorded_native_has_no_baseline()
    {
        var s = new Scratch();
        s.AddBaseline($"stock-{JoltNative.PackageVersion}-win-x64", "drop-run", Summary(Row("drop")));
        s.AddRun("drop-run", Summary(Row("drop")), Info("win-x64", null, "ABCDEF"));
        var (outcome, report) = s.Check();
        Assert.Equal(BaselineOutcome.Skipped, outcome);
        Assert.StartsWith("SKIPPED: no baseline for this native (unrecorded for win-x64, sha256 ABCDEF)", report);
    }

    // ------------------------------------------------------------------ comparing

    [Fact]
    public void Wall_clock_columns_are_left_out_and_every_other_column_counts()
    {
        Assert.Equal(new[] { "ray_us_per_cast" }, BaselineCheck.WallClockColumns.ToArray());
        string recorded = Summary(Row("raycast-cost", ("ray_casts", "2750"), ("ray_us_per_cast", "22.0")));
        Assert.Empty(BaselineCheck.Compare(recorded, Summary(Row("raycast-cost", ("ray_casts", "2750"), ("ray_us_per_cast", "14.8")))));

        foreach (string column in Header.Split(',').Skip(1).Where(c => !BaselineCheck.WallClockColumns.Contains(c)))
        {
            List<string> d = BaselineCheck.Compare(recorded, Summary(Row("raycast-cost", ("ray_casts", "2750"), ("ray_us_per_cast", "22.0"), (column, column == "ray_casts" ? "2749" : "1"))));
            Assert.True(d.Count == 1, column);
            Assert.Contains($"raycast-cost: {column} recorded", d[0]);
        }
    }

    [Fact]
    public void Line_endings_do_not_count_but_rows_and_columns_do()
    {
        string recorded = Summary(Row("a"), Row("b"));
        Assert.Empty(BaselineCheck.Compare(recorded, recorded.Replace("\n", "\r\n")));
        Assert.Contains(BaselineCheck.Compare(recorded, Summary(Row("a"))), d => d.StartsWith("row count differs"));
        Assert.Contains(BaselineCheck.Compare(recorded, "scenario,steps\na,1\n"), d => d.StartsWith("columns differ"));
    }

    [Fact]
    public void A_run_with_no_baseline_file_is_skipped_and_the_others_still_count()
    {
        var s = new Scratch();
        JoltNativeInfo native = Info("win-x64", Build(JoltNativeOrigin.Package, "win-x64"));
        string key = BaselineCheck.KeyFor(native.Build);
        s.AddBaseline(key, "recorded", Summary(Row("a")));
        s.AddRun("recorded", Summary(Row("a")), native);
        s.AddRun("unrepeatable", Summary(Row("b")), native);
        var (outcome, report) = s.Check();
        Assert.Equal(BaselineOutcome.Passed, outcome);
        Assert.Contains("SKIPPED unrepeatable: no baseline for this run on this native", report);
        Assert.Contains("1 passed, 0 failed, 1 skipped", report);

        s.AddRun("recorded", Summary(Row("a", ("end_x", "1.000"))), native);
        (outcome, report) = s.Check();
        Assert.Equal(BaselineOutcome.Failed, outcome);
        Assert.Contains("row 1 a: end_x recorded 0, now 1.000", report);
        Assert.Equal(1, BaselineCheck.Run(new[] { "--check-baseline", s.Output, "--baselines", s.Baselines }, new StringWriter()));
    }

    [Fact]
    public void A_recorded_run_that_did_not_run_fails()
    {
        var s = new Scratch();
        string key = $"stock-{JoltNative.PackageVersion}-linux-x64";
        s.AddBaseline(key, "a", Summary(Row("a")));
        s.AddBaseline(key, "b", Summary(Row("b")));
        s.AddRun("a", Summary(Row("a")), Info("linux-x64", Build(JoltNativeOrigin.Package, "linux-x64")));
        var (outcome, report) = s.Check();
        Assert.Equal(BaselineOutcome.Failed, outcome);
        Assert.Contains("FAILED  b: recorded in the baseline but not run", report);
    }

    [Fact]
    public void Runs_that_loaded_different_natives_fail()
    {
        var s = new Scratch();
        s.AddBaseline($"stock-{JoltNative.PackageVersion}-win-x64", "a", Summary(Row("a")));
        s.AddRun("a", Summary(Row("a")), Info("win-x64", Build(JoltNativeOrigin.Package, "win-x64")));
        s.AddRun("b", Summary(Row("b")), Info("linux-x64", Build(JoltNativeOrigin.Package, "linux-x64")));
        var (outcome, report) = s.Check();
        Assert.Equal(BaselineOutcome.Failed, outcome);
        Assert.StartsWith("FAILED: the runs loaded different joltc files", report);
    }

    [Fact]
    public void Recording_writes_the_loaded_builds_folder_and_leaves_out_the_runs_named()
    {
        var s = new Scratch();
        JoltNativeInfo native = Info("linux-x64", Build(JoltNativeOrigin.Package, "linux-x64"));
        s.AddRun("a", Summary(Row("a")).Replace("\n", "\r\n"), native);
        s.AddRun("b", Summary(Row("b")), native);
        Assert.Equal(0, BaselineCheck.Run(new[] { "--record-baseline", s.Output, "--baselines", s.Baselines, "--leave-out", "b" }, new StringWriter()));
        string folder = Path.Combine(s.Baselines, BaselineCheck.KeyFor(native.Build));
        Assert.Equal(new[] { "a.csv" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        Assert.DoesNotContain("\r", File.ReadAllText(Path.Combine(folder, "a.csv")));
        var (outcome, report) = s.Check();
        Assert.Equal(BaselineOutcome.Passed, outcome);
        Assert.Contains("SKIPPED b", report);
    }

    // ------------------------------------------------------------------ the committed baselines

    // Every committed baseline is a run of ci/runs.txt with today's summary columns, and its folder is the baseline of a
    // build the module has a record of. Every build this project tests has one holding every listed run; the stock files
    // it does not test (Arm64, macOS) have none.
    [Fact]
    public void The_committed_baselines_match_the_run_list_and_the_record()
    {
        string[] runs = File.ReadAllLines(Path.Combine(RepoRoot(), "Tests", "JoltPhysicsHarness", "ci", "runs.txt"))
            .Where(l => !l.TrimStart().StartsWith('#') && l.Contains(':'))
            .Select(l => l[..l.IndexOf(':')])
            .ToArray();
        string[] keys = JoltNative.Known.Select(BaselineCheck.KeyFor).Distinct().ToArray();
        string[] folders = Directory.GetDirectories(CommittedBaselines).Select(Path.GetFileName).ToArray();
        Assert.Contains($"stock-{JoltNative.PackageVersion}-win-x64", folders);
        Assert.Contains($"stock-{JoltNative.PackageVersion}-linux-x64", folders);
        foreach (string folder in folders)
        {
            Assert.Contains(folder, keys);
            foreach (string file in Directory.GetFiles(Path.Combine(CommittedBaselines, folder)))
            {
                Assert.Equal(".csv", Path.GetExtension(file));
                Assert.Contains(Path.GetFileNameWithoutExtension(file), runs);
                Assert.Equal(Header, File.ReadLines(file).First());
            }
        }
        foreach (JoltNativeBuild untested in JoltNative.Known.Where(b => !b.TestedByProject))
            Assert.DoesNotContain(BaselineCheck.KeyFor(untested), folders);

        foreach (string key in JoltNative.Known.Where(b => b.TestedByProject).Select(BaselineCheck.KeyFor).Distinct())
            Assert.Equal(runs.OrderBy(r => r, StringComparer.Ordinal),
                         Directory.GetFiles(Path.Combine(CommittedBaselines, key)).Select(Path.GetFileNameWithoutExtension).OrderBy(r => r, StringComparer.Ordinal));
    }
}
