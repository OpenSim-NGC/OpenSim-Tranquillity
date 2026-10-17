/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// [Startup] TimerResolutionMs: 0 (the default) changes nothing; a value from 1 to 15 asks Windows for that timer
/// period once, logs one line, and gives it back once; off Windows, or out of range, nothing is asked.
/// </summary>
public class TimerResolutionTests : OpenSimTestCase
{
    private sealed class Calls
    {
        public readonly List<uint> Begin = new();
        public readonly List<uint> End = new();
        public readonly List<(LogLevel Level, string Message)> Log = new();
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly Calls _calls;
        public CaptureLogger(Calls calls) { _calls = calls; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            _calls.Log.Add((logLevel, formatter(state, exception)));
        }
    }

    private static TimerResolution Apply(Calls calls, int ms, bool windows = true)
    {
        return TimerResolution.Apply(ms, calls.Begin.Add, calls.End.Add, windows, () => 1.07, new CaptureLogger(calls));
    }

    [Fact]
    public void Off_by_default_asks_for_nothing_and_logs_nothing()
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        using TimerResolution t = Apply(calls, 0);

        Assert.Null(t);
        Assert.Empty(calls.Begin);
        Assert.Empty(calls.End);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public void On_asks_once_logs_one_line_and_gives_it_back_once_at_shutdown()
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        TimerResolution t = Apply(calls, 1);

        Assert.NotNull(t);
        Assert.Equal(1, t.Milliseconds);
        Assert.Equal(new uint[] { 1 }, calls.Begin);
        Assert.Empty(calls.End);
        var line = Assert.Single(calls.Log);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("1 ms", line.Message);
        Assert.Contains("1.07", line.Message);

        t.Dispose();
        t.Dispose();   // a second shutdown path must not end the request twice

        Assert.Equal(new uint[] { 1 }, calls.Begin);
        Assert.Equal(new uint[] { 1 }, calls.End);
        Assert.Single(calls.Log);
    }

    [Fact]
    public void The_end_call_carries_the_value_the_begin_call_had()
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        Apply(calls, 5).Dispose();

        Assert.Equal(new uint[] { 5 }, calls.Begin);
        Assert.Equal(new uint[] { 5 }, calls.End);
    }

    [Fact]
    public void Off_Windows_nothing_is_asked()
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        TimerResolution t = Apply(calls, 1, windows: false);

        Assert.Null(t);
        Assert.Empty(calls.Begin);
        Assert.Empty(calls.End);
        var line = Assert.Single(calls.Log);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("Windows only", line.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    public void An_unset_or_zero_text_setting_is_off(string setting)
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        TimerResolution t = TimerResolution.Apply(setting, calls.Begin.Add, calls.End.Add, true, () => 1.0, new CaptureLogger(calls));

        Assert.Null(t);
        Assert.Empty(calls.Begin);
        Assert.Empty(calls.Log);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1 ms")]
    public void A_text_setting_that_is_not_a_whole_number_is_refused_with_a_warning(string setting)
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        TimerResolution t = TimerResolution.Apply(setting, calls.Begin.Add, calls.End.Add, true, () => 1.0, new CaptureLogger(calls));

        Assert.Null(t);
        Assert.Empty(calls.Begin);
        Assert.Empty(calls.End);
        var line = Assert.Single(calls.Log);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("TimerResolutionMs", line.Message);
    }

    [Fact]
    public void A_text_setting_of_a_valid_number_asks_for_it_and_gives_it_back()
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        TimerResolution t = TimerResolution.Apply(" 2 ", calls.Begin.Add, calls.End.Add, true, () => 2.0, new CaptureLogger(calls));
        Assert.Equal(new uint[] { 2 }, calls.Begin);
        t.Dispose();

        Assert.Equal(new uint[] { 2 }, calls.End);
    }

    [Fact]
    public void A_failure_while_measuring_gives_the_timer_back_before_it_propagates()
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        Assert.Throws<InvalidOperationException>(() =>
            TimerResolution.Apply(1, calls.Begin.Add, calls.End.Add, true, () => throw new InvalidOperationException(), new CaptureLogger(calls)));

        Assert.Equal(new uint[] { 1 }, calls.Begin);
        Assert.Equal(new uint[] { 1 }, calls.End);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(1000)]
    public void A_value_out_of_range_is_refused_with_a_warning_and_asks_for_nothing(int ms)
    {
        TestHelpers.InMethod();
        var calls = new Calls();

        TimerResolution t = Apply(calls, ms);

        Assert.Null(t);
        Assert.Empty(calls.Begin);
        Assert.Empty(calls.End);
        var line = Assert.Single(calls.Log);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("TimerResolutionMs", line.Message);
    }

    [Fact]
    public void The_shipped_defaults_do_not_turn_it_on()
    {
        TestHelpers.InMethod();

        // The region server reads GetString("TimerResolutionMs", "0"): the default file must not set the key.
        string defaults = Path.Combine(AppContext.BaseDirectory, "RegionServerDefaults.ini");
        if (!File.Exists(defaults))
            return;   // the defaults are part of the region server's own output, not of this test project's

        foreach (string raw in File.ReadAllLines(defaults))
        {
            string line = raw.Trim();
            Assert.False(line.StartsWith("TimerResolutionMs", StringComparison.OrdinalIgnoreCase),
                "RegionServerDefaults.ini sets TimerResolutionMs: the key must stay commented out (off)");
        }
    }
}
