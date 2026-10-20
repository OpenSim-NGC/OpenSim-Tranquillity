/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.PhysicsModules.Jolt.Harness;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The physics harness (Tests/JoltPhysicsHarness) as regression tests: its output is repeatable, every scenario
/// runs at every heartbeat rate, and the 11 Hz figures (one Simulate per heartbeat, today's only path) hold.
/// Serial with the other native tests: every run steps a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class HarnessTests
{
    // The figures here were measured with the legacy presets ([Jolt] VehiclePresets = legacy); the documented set's
    // are held by VehiclePresetDriveTests.
    private static RunResult Run(string scenario, double rate = 11.0, float? slope = null, float? duration = null, float? keyRepeat = null,
                                 float? walkFactor = null)
    {
        var o = new HarnessOptions { RateHz = rate, SlopeDeg = slope, Duration = duration, Jolt = { ["VehiclePresets"] = "legacy" } }.SecondLife();
        if (walkFactor.HasValue) o.Jolt["AvatarWalkSpeedFactor"] = walkFactor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (keyRepeat.HasValue) o.KeyRepeat = keyRepeat.Value;
        return Harness.Harness.Run(Harness.Harness.Find(scenario), o);
    }

    [Theory]
    [InlineData("testcar", 15f)]
    [InlineData("avatar-walk", 5f)]
    [InlineData("drop", 0f)]
    public void Two_runs_give_identical_output(string scenario, float slope)
    {
        RunResult a = Run(scenario, 45.0, slope);
        RunResult b = Run(scenario, 45.0, slope);
        Assert.Equal(a.ToCsv(), b.ToCsv());
        Assert.Equal(a.SummaryLine(), b.SummaryLine());
        Assert.True(a.Samples.Count > 10);
    }

    public static IEnumerable<object[]> EveryScenarioAndRate()
    {
        foreach (Scenario s in Harness.Harness.Scenarios)
            foreach (double r in Harness.Harness.Rates)
                yield return new object[] { s.Name, r };
    }

    // Each scenario on its first slope, 8 simulated seconds, at every rate the later substep work compares.
    [Theory]
    [MemberData(nameof(EveryScenarioAndRate))]
    public void Every_scenario_runs_at_every_rate(string scenario, double rate)
    {
        RunResult r = Run(scenario, rate, duration: 8f);
        Assert.Equal(0, r.Summary.NonFinite);
        Assert.True(r.Samples.Count >= (int)(rate * 0.9), $"{r.Name}: {r.Samples.Count} samples");
        double dt = 1.0 / rate;
        for (int i = 0; i < r.Samples.Count; i++)
            Assert.Equal((i + 1) * dt, r.Samples[i].T, 9);
    }

    // The tolerances below cover float noise and nothing more: today the output is bit-for-bit repeatable.
    // "Was" gives the figure before the vehicle motor ramp, linear deflection and the avatar's fall were made
    // independent of the step; "before" the figure before the motors and friction followed the documented model
    // (velocity approaching the motor's exponentially, friction acting on every axis all the time).

    [PinnedMaterials]
    [Fact]
    public void Test_car_on_level_ground_at_11_hz()
    {
        Summary m = Run("testcar", slope: 0f).Summary;
        Assert.InRange(m.ReleaseSpeed, 3.74f, 3.84f);             // 3.792 (before 6.150, was 5.134; 45 Hz now 3.788)
        // 9.363 with the vehicle run in each collision step (9.462 run once per heartbeat; 45 Hz 9.453 then, 9.367 now with the feed below):
        // at one step per heartbeat it now gives the 45 Hz figure to within 0.1%.
        Assert.InRange(m.DistanceBeforeRelease, 9.31f, 9.41f);
        Assert.InRange(m.TimeToRest, 3.05, 3.45);                  // 3.182 s after release (before 4.818, was 3.727); 3.364 with the vehicle run in each collision step: one 11 Hz heartbeat (0.091 s) later
        Assert.InRange(m.MaxTilt, 0f, 1f);
    }

    [PinnedMaterials]
    // The region feed on a test course's pad drive (3 s key, the course's test car). Measured in a region before the
    // motor ramp was made independent of the step: 5.59 m / 5.00 m/s under the key at one step per heartbeat and
    // 8.43 m / 6.14 m/s at PhysicsStepRate 45, which this feed then gave to within 1% (5.62 / 5.01 and 8.40 / 6.12;
    // the heartbeat feed gave 5.91 and 7.45 m). The figures below are this code's; a region proof should land on them.
    // The car's drop from where it was rezzed no longer changes the drive (the motor's equation does not depend on
    // the starting speed), so they equal the heartbeat feed's.
    [Theory]
    [InlineData(0.0, 9.363f, 3.792f)]   // 9.462 before the vehicle ran in each collision step; now the same as at 45 Hz
    [InlineData(45.0, 9.367f, 3.785f)]
    public void Region_feed_drives_the_pad_as_a_region_does(double physicsRate, float distance, float releaseSpeed)
    {
        var o = new HarnessOptions { RateHz = 11.0, SlopeDeg = 0f, PhysicsRateHz = physicsRate, Feed = InputFeed.Region }.SecondLife();
        Summary m = Harness.Harness.Run(Harness.Harness.Find("testcar"), o).Summary;
        Assert.InRange(m.DistanceBeforeRelease, distance * 0.99f, distance * 1.01f);
        Assert.InRange(m.ReleaseSpeed, releaseSpeed * 0.99f, releaseSpeed * 1.01f);
    }

    [PinnedMaterials]
    [Theory]
    [InlineData(5f, 0.856f)]     // in-world 0.90; the equation g sin(5) * 1 s = 0.855 (0.896 with gravity outside it; before 0.907)
    [InlineData(15f, 2.542f)]    // in-world 2.85; the equation 2.538 (2.655 with gravity outside it; before 2.850): friction acts along the car's axes, the
                                 // vertical part included
    [InlineData(33f, 5.345f)]    // in-world 7.94 (released before the crest there); 5.590 with gravity outside the equation, before 6.677, was 7.842.
    public void Test_car_rolls_down_a_slope_at_a_steady_speed_at_11_hz(float slope, float steady)
    {
        Summary m = Run("testcar", slope: slope).Summary;
        Assert.InRange(m.SteadySpeed, steady * 0.98f, steady * 1.02f);
        Assert.True(m.End.Y < Course.RampBottomY, $"stopped on the ramp at y {m.End.Y}");
        Assert.False(double.IsNaN(m.TimeToRest), "came to rest on the run-out");
    }

    [PinnedMaterials]
    [Fact]
    public void Preset_car_on_level_ground_at_11_hz()
    {
        Summary m = Run("car", slope: 0f).Summary;
        Assert.InRange(m.SteadySpeed, 7.853f, 7.953f);   // 7.903 over the last second of the hold (before 7.702, was 7.455)
        Assert.InRange(m.TimeToRest, 2.5, 2.8);            // 2.636 (before 6.000, was 6.182)
    }

    [Theory]
    [InlineData(0f, 4.096f)]     // in-world 4.1
    [InlineData(5f, 4.079f)]     // in-world 4.10
    [InlineData(15f, 3.949f)]    // in-world 3.95
    [InlineData(33f, 3.431f)]    // in-world 3.45
    public void Avatar_walks_at_11_hz(float slope, float along)
    {
        // The in-world figures were measured when the avatar walked at the speed asked (4.096 m/s), which
        // AvatarWalkSpeedFactor 1 gives; AvatarSpeedTests holds the default walk.
        Summary m = Run("avatar-walk", slope: slope, walkFactor: 1f).Summary;
        Assert.InRange(m.SteadySpeed, along - 0.03f, along + 0.03f);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(15f)]
    public void Avatar_stands_still_on_a_slope_at_11_hz(float slope)
    {
        RunResult r = Run("avatar-stand", slope: slope);
        Sample first = r.Samples.First(s => s.T >= 1.0);
        Sample last = r.Samples[^1];
        Assert.InRange((last.Position - first.Position).Length(), 0f, 0.01f);
        Assert.InRange(r.Summary.ZRange, 0f, 0.01f);
    }

    [Fact]
    public void Avatar_jump_at_11_hz()
    {
        Summary m = Run("avatar-jump").Summary;
        Assert.InRange(m.PeakRise, 0.80f, 0.817f);        // 0.806 m, the samples either side of 0.816 (was 0.644)
        Assert.InRange(m.TimeToRest, 0.85, 0.95);          // back on the ground 0.909 s after the jump (was 0.818)
    }

    [Fact]
    public void Dropped_box_settles_at_11_hz()
    {
        Summary m = Run("drop").Summary;
        // 1.818 s after the release (1.091 s before the bounce). The 5 m fall takes sqrt(2 x 5 / g) = 1.01 s and arrives at 9.9 m/s.
        // Wood on land combines to 0.275 under the shipped averaging: it leaves at 2.7 m/s (a flight of 0.55 s), arrives again at
        // 2.7 m/s, leaves at 0.75 m/s (0.15 s), and that arrival is under the engine's 1 m/s bounce cutoff: at rest at 1.01 + 0.55 +
        // 0.15 = 1.71 s, seen at the next 11 Hz sample, the 20th, 1.82 s.
        Assert.InRange(m.TimeToRest, 1.7, 1.9);
        Assert.InRange(m.End.Z, 25.45f, 25.52f);            // 25.480; rests 2 cm low at 11 Hz, 25.500 at 22.5 Hz and up
    }

    [PinnedMaterials]
    [Fact]
    public void Boat_balloon_and_airplane_at_11_hz()
    {
        Summary boat = Run("boat").Summary;
        Assert.InRange(boat.SteadySpeed, 4.856f, 4.956f);  // 4.906 under the motor (before 4.741, was 4.636)
        Assert.InRange(boat.End.Z, 20.45f, 20.55f);         // hovers 0.5 m above the water

        Summary balloon = Run("balloon").Summary;
        Assert.InRange(balloon.End.Z, 28.47f, 28.57f);      // 28.522 at 40 s (before 29.998, then 30.645, was 30.339):
                                                            // hover is a spring with the preset's 10 s timescale, which
                                                            // with the 5 s vertical friction rises slowly toward 5 m

        // The airplane preset has no lift: with the motor held it flies level at 15 m/s and sinks to the ground.
        Summary plane = Run("airplane").Summary;
        Assert.InRange(plane.SteadySpeed, 14.9f, 15.1f);
        Assert.InRange(plane.End.Z, 25f, 26f);
    }

    [PinnedMaterials]
    [Fact]
    public void Sled_runs_at_11_hz()
    {
        // The sled preset has almost no forward friction (timescale 1000): let go on a 15 degree slope it slides
        // down, leaves the ramp at about 16.5 m/s and runs off the region's south edge, which ends the run. Its slope
        // assist is the same at every rate now (8.73 / 8.84 / 8.84 / 8.80 s at 11 / 22.5 / 45 / 90 Hz); it was a
        // force times the step, six times stronger at 11 Hz than at the 15 ms step (before: 7.364 s and 19.85 m/s).
        RunResult r = Run("sled");
        Assert.Equal(0, r.Summary.NonFinite);
        Assert.InRange(r.Summary.LeftRegionT, 8.4, 9.1);                        // 8.727 s
        Sample bottom = r.Samples.First(s => s.Position.Y <= Course.RampBottomY);
        Assert.InRange(bottom.Speed, 16.2f, 16.8f);                             // 16.52 m/s
    }

    [PinnedMaterials]
    [Fact]
    public void A_run_that_leaves_the_region_reports_no_sleep_time()
    {
        // The sled runs off the region's south edge, which ends the run on the first sample outside. By then its body
        // has left the engine, so that sample reads as nothing awake. That is not sleep: sleep_after is "-", not the
        // leaving time less a release time the run never reached.
        RunResult r = Run("sled");
        Assert.False(double.IsNaN(r.Summary.LeftRegionT), "the sled did not leave the region");
        Assert.True(double.IsNaN(r.Summary.SleepAfter), $"sleep_after {r.Summary.SleepAfter}");
        int column = Array.IndexOf(RunResult.SummaryHeader.Split(','), "sleep_after");
        Assert.Equal("-", r.SummaryLine().Split(',')[column]);
    }

    [Fact]
    public void Vehicle_param_overrides_reach_the_controller()
    {
        // The same car with a slower motor covers less ground under the key: an override changes the run.
        var o = new HarnessOptions { RateHz = 11.0, SlopeDeg = 0f }.SecondLife();
        Summary plain = Harness.Harness.Run(Harness.Harness.Find("testcar"), o).Summary;
        o.VehicleParams.Add(VehicleParamSetting.Parse("LINEAR_MOTOR_TIMESCALE=3"));
        Summary slower = Harness.Harness.Run(Harness.Harness.Find("testcar"), o).Summary;
        Assert.True(slower.DistanceBeforeRelease < plain.DistanceBeforeRelease * 0.6f,
            $"under the key: {slower.DistanceBeforeRelease} vs {plain.DistanceBeforeRelease}");
    }

    [Fact]
    public void Start_offset_moves_the_scenario_body_by_that_much()
    {
        RunResult plain = Run("drop");
        var o = new HarnessOptions { StartOffsetX = 0.001f, StartOffsetY = -0.002f, Jolt = { ["VehiclePresets"] = "legacy" } }.SecondLife();
        RunResult moved = Harness.Harness.Run(Harness.Harness.Find("drop"), o);
        // The box falls straight down, so its first sample is still off by the offset on x and y.
        Assert.Equal(plain.Samples[0].Position.X + 0.001f, moved.Samples[0].Position.X, 0.0002f);
        Assert.Equal(plain.Samples[0].Position.Y - 0.002f, moved.Samples[0].Position.Y, 0.0002f);
        Assert.Throws<ArgumentException>(() => Program.Run(new[] { "--scenario", "drop", "--start-offset", "0.001" }, new StringWriter()));
    }

    [Fact]
    public void The_tower_traces_every_box_and_says_when_each_fell_asleep()
    {
        RunResult r = Harness.Harness.Run(Harness.Harness.Find("tower-10"), new HarnessOptions { RateHz = 11.0, PhysicsRateHz = 45.0 }.SecondLife());
        string[] lines = r.PartsToCsv().TrimEnd('\n').Split('\n');
        Assert.Equal(RunResult.PartsCsvHeader, lines[0]);
        // One line per box per heartbeat, every box named.
        Assert.Equal(10 * r.Samples.Count, lines.Length - 1);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"box{i}"), lines.Skip(1).Select(l => l.Split(',')[1]).Distinct());
        List<string> parts = r.PartLines().ToList();
        Assert.Equal(10, parts.Count);
        Assert.All(parts.Zip(r.Parts), x => Assert.StartsWith($"  part {x.Second.Name}: asleep at {(double.IsNaN(x.Second.SleptAt) ? "-" : x.Second.SleptAt.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))} s", x.First));
        // A run of a scenario that keeps no part traces writes none.
        Assert.Null(Run("drop").PartsToCsv());
        Assert.Empty(Run("drop").PartLines());
    }

    [Fact]
    public void Command_line_writes_only_to_its_out_folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jolt-harness-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = new StringWriter();
            int rc = Program.Run(new[] { "--scenario", "drop", "--rate", "11,45", "--out", dir }, output);
            Assert.Equal(0, rc);
            string[] files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "drop-s0-r11.csv", "drop-s0-r45.csv", "native.txt", "summary.csv" }, files);
            // native.txt names the joltc build the runs loaded, for the regression check (BaselineCheck).
            Assert.Equal(BaselineCheck.Describe(Backend.JoltNative.EnsureLoaded(false)), File.ReadAllText(Path.Combine(dir, "native.txt")));
            Assert.StartsWith(RunResult.SummaryHeader, output.ToString());
            Assert.StartsWith(RunResult.CsvHeader, File.ReadAllText(Path.Combine(dir, "drop-s0-r11.csv")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
