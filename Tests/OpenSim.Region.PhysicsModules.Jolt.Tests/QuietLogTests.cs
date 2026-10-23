/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// What a region writes to the console in normal use: the [JOLT METRICS] lines at Debug level on the interval
/// [Jolt] MetricsLogIntervalSeconds sets (0: none), the per-avatar and per-terrain lines at Debug level, and a warning that repeats
/// logged for its first occurrence and every 100th. Lines are counted with a test logger.
/// Serial: it replaces JoltMetrics' clock and logger and LoggerProvider.LoggerFactory, which are process-wide, and its
/// regions step a real backend on the shared job pool.
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class QuietLogTests
{
    private const float Heartbeat = 0.0909f;
    private const int Size = 256;
    private const float Ground = 25f;

    private sealed class CaptureLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Lines = new();
        public LogLevel Minimum = LogLevel.Trace;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            lock (Lines)
                Lines.Add((logLevel, formatter(state, exception)));
        }
        public List<(LogLevel Level, string Message)> Snapshot() { lock (Lines) return Lines.ToList(); }
    }

    private sealed class CaptureFactory : ILoggerFactory
    {
        public readonly CaptureLogger Logger = new();
        public ILogger CreateLogger(string categoryName) => Logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    // One step a second, and an interval closes on the first step more than `interval` s after the last close: the first
    // step closes one, then one every interval + 1 s.
    private static int Closes(int interval, int seconds) => 1 + (seconds - 1) / (interval + 1);

    // The metrics clock stands in for a region stepping for `seconds` of wall time, one step every second.
    private static (List<(LogLevel Level, string Message)> Lines, string LastInterval) RunMetrics(float intervalSetting, int seconds, LogLevel minimum = LogLevel.Trace)
    {
        var log = new CaptureLogger { Minimum = minimum };
        ILogger savedLog = JoltMetrics.Log;
        Func<long> savedClock = JoltMetrics.Clock;
        long now = 1_000_000;
        try
        {
            JoltMetrics.Reset();
            JoltMetrics.Log = log;
            JoltMetrics.Clock = () => now;
            JoltMetrics.Configure(intervalSetting);
            var timing = new HeartbeatTiming();
            for (int i = 0; i < seconds; i++)
            {
                now += 1000;
                JoltMetrics.RecordStep("Example Region", 1.5f, 3, in timing, 90.0, 90.9);
            }
            return (log.Snapshot(), JoltMetrics.LastIntervalLine());
        }
        finally
        {
            JoltMetrics.Log = savedLog;
            JoltMetrics.Clock = savedClock;
            JoltMetrics.Reset();
        }
    }

    [Fact]
    public void An_hour_at_the_default_interval_writes_two_debug_lines_per_interval_and_nothing_above_debug()
    {
        var (lines, last) = RunMetrics(30f, 3600);
        // The first step closes an interval at once (the clock starts far past the first throttle window), then one every 31 s.
        Assert.Equal(2 * Closes(30, 3600), lines.Count);
        Assert.All(lines, l => Assert.Equal(LogLevel.Debug, l.Level));
        Assert.All(lines, l => Assert.StartsWith("[JOLT METRICS]", l.Message));
        Assert.Equal(lines.Count / 2, lines.Count(l => l.Message.StartsWith("[JOLT METRICS] last ", StringComparison.Ordinal)));
        Assert.NotNull(last);
    }

    [Fact]
    public void A_console_that_shows_Information_and_above_gets_no_metrics_lines()
    {
        var (lines, last) = RunMetrics(30f, 3600, LogLevel.Information);
        Assert.Empty(lines);
        Assert.NotNull(last);   // the intervals still close, so `jolt metrics` has its figures
    }

    [Fact]
    public void An_interval_of_zero_logs_nothing_but_still_closes_every_30_seconds_for_the_console_command()
    {
        var (lines, last) = RunMetrics(0f, 3600);
        Assert.Empty(lines);
        Assert.NotNull(last);
        Assert.Contains("last 31.0s", last);
    }

    [Fact]
    public void The_interval_setting_sets_how_often_the_lines_are_written()
    {
        var (lines, last) = RunMetrics(60f, 3600);
        Assert.Equal(2 * Closes(60, 3600), lines.Count);
        Assert.Contains("last 61.0s", last);

        var (fast, _) = RunMetrics(10f, 600);
        Assert.Equal(2 * Closes(10, 600), fast.Count);
    }

    [Fact]
    public void The_start_up_line_for_a_region_stays_at_Information()
    {
        var log = new CaptureLogger();
        ILogger saved = JoltMetrics.Log;
        try
        {
            JoltMetrics.Reset();
            JoltMetrics.Log = log;
            JoltMetrics.RecordRegionInit("Example Region", 40L * 1024 * 1024);
        }
        finally { JoltMetrics.Log = saved; JoltMetrics.Reset(); }
        var line = Assert.Single(log.Snapshot());
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("region 'Example Region'", line.Message);
    }

    [Fact]
    public void The_setting_defaults_to_30_parses_and_takes_zero()
    {
        var warnings = new List<string>();
        Assert.Equal(30f, JoltConfig.FromConfig(new IniConfigSource(), warnings).MetricsLogIntervalSeconds);
        Assert.Empty(warnings);

        Assert.Equal(5f, JoltConfig.FromConfig(Source("5"), warnings).MetricsLogIntervalSeconds);
        Assert.Equal(0f, JoltConfig.FromConfig(Source("0"), warnings).MetricsLogIntervalSeconds);
        Assert.Empty(warnings);

        foreach (string bad in new[] { "-1", "soon", "NaN", "100000" })
        {
            warnings.Clear();
            Assert.Equal(30f, JoltConfig.FromConfig(Source(bad), warnings).MetricsLogIntervalSeconds);
            Assert.Single(warnings);
            Assert.Contains("MetricsLogIntervalSeconds", warnings[0]);
        }
    }

    private static IConfigSource Source(string value)
    {
        var src = new IniConfigSource();
        src.AddConfig("Jolt").Set("MetricsLogIntervalSeconds", value);
        return src;
    }

    [Fact]
    public void Initialise_applies_the_setting_to_the_metrics_clock()
    {
        var src = new IniConfigSource();
        src.AddConfig("Startup").Set("physics", "Jolt");
        src.Configs["Startup"].Set("meshing", "Meshmerizer");
        src.AddConfig("Jolt").Set("MetricsLogIntervalSeconds", "45");
        try
        {
            new JoltScene().Initialise(src);
            Assert.Equal(45, JoltMetrics.IntervalSeconds);
        }
        finally { JoltMetrics.Reset(); }
    }

    private static JoltScene NewScene()
    {
        var src = new IniConfigSource();
        src.AddConfig("Startup").Set("physics", "Jolt");
        src.Configs["Startup"].Set("meshing", "Meshmerizer");
        src.AddConfig("Jolt");
        var scene = new JoltScene();
        scene.Initialise(src);
        var heights = new float[Size * Size];
        Array.Fill(heights, Ground);
        scene.InitialiseWithoutScene("Example Region", Size, Size, heights, 20f, Heartbeat);
        return scene;
    }

    // The lines the module logs while `body` runs.
    private static List<(LogLevel Level, string Message)> Logged(Action body)
    {
        var capture = new CaptureFactory();
        ILoggerFactory saved = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try { body(); }
        finally { LoggerProvider.LoggerFactory = saved; }
        return capture.Logger.Snapshot();
    }

    [Fact]
    public void Avatars_arriving_and_leaving_and_a_terrain_edit_write_nothing_above_Debug()
    {
        JoltScene scene = null;
        try
        {
            var startup = Logged(() => scene = NewScene());
            // Start-up still tells the operator how the region came up.
            Assert.Contains(startup, l => l.Level == LogLevel.Information && l.Message.Contains("backend initialised", StringComparison.Ordinal));

            var normalUse = Logged(() =>
            {
                for (uint i = 0; i < 20; i++)
                {
                    PhysicsActor a = scene.AddAvatar(2000 + i, "Test User", new Vector3(100f + i, 100f, Ground + 2f), new Vector3(0.45f, 0.6f, 1.9f), 0f, false);
                    scene.Simulate(Heartbeat);
                    scene.RemoveAvatar(a);
                }
                var raised = new float[Size * Size];
                Array.Fill(raised, Ground + 3f);
                scene.SetTerrain(raised);
                scene.Simulate(Heartbeat);
            });

            Assert.DoesNotContain(normalUse, l => l.Level >= LogLevel.Information);
            Assert.Equal(20, normalUse.Count(l => l.Level == LogLevel.Debug && l.Message.Contains("placed at", StringComparison.Ordinal)));
            Assert.Equal(20, normalUse.Count(l => l.Level == LogLevel.Debug && l.Message.Contains("removed.", StringComparison.Ordinal)));
            Assert.Single(normalUse, l => l.Level == LogLevel.Debug && l.Message.Contains("terrain set:", StringComparison.Ordinal));
        }
        finally { scene?.Dispose(); }
    }

    [Fact]
    public void The_limiter_logs_the_first_occurrence_and_then_every_hundredth()
    {
        var limiter = new RepeatLimiter();
        var logged = new List<long>();
        for (int i = 0; i < 350; i++)
            if (limiter.ShouldLog(out long n))
                logged.Add(n);
        Assert.Equal(new long[] { 1, 101, 201, 301 }, logged);
        Assert.Equal(350, limiter.Count);
        Assert.Contains("occurrence 101", RepeatLimiter.Suffix(101));
    }

    [Fact]
    public void A_warning_that_repeats_is_logged_for_the_first_and_every_hundredth_occurrence()
    {
        JoltScene scene = null;
        try
        {
            scene = NewScene();
            long before = RepeatingWarnings.TerrainTooShort.Count;
            var lines = Logged(() =>
            {
                var tooShort = new float[10];
                for (int i = 0; i < 250; i++)
                    scene.SetTerrain(tooShort);
            });
            var warnings = lines.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("SetTerrain: heightMap", StringComparison.Ordinal)).ToList();
            int expected = 0;
            for (long n = before + 1; n <= before + 250; n++)
                if ((n - 1) % RepeatLimiter.Every == 0)
                    expected++;
            Assert.Equal(expected, warnings.Count);
            Assert.InRange(expected, 2, 3);
            Assert.All(warnings, w => Assert.Contains("the first and then every 100th are logged", w.Message));
        }
        finally { scene?.Dispose(); }
    }
}
