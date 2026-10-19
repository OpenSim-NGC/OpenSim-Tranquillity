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
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A region logs one INFO line at start-up saying whether [EntityTransfer] EnableStandingRiderCrossing is on, so an
/// operator can check a deployment from the region log. The line is written whether or not the key is in the
/// configuration (a region that does not set it has it off). It swaps the process-wide logger factory, so it shares the
/// "process-logger" collection with the other tests that do.
/// </summary>
[Collection("process-logger")]
public class StandingRiderCrossingLogTests
{
    private sealed class Capture : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Message)> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly Capture m_capture;
            public Logger(Capture capture) { m_capture = capture; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (m_capture.Lines)
                    m_capture.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // Sets up a region named `name` with the given [EntityTransfer] value (null = the key is not set) and returns the
    // lines it logged about the setting.
    private static List<(LogLevel Level, string Message)> LinesFor(string name, uint x, string value)
    {
        var config = new IniConfigSource();
        if (value != null)
            config.AddConfig("EntityTransfer").Set("EnableStandingRiderCrossing", value);

        var capture = new Capture();
        ILoggerFactory saved = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try { new SceneHelpers().SetupScene(name, UUID.Random(), x, 6400, config); }
        finally { LoggerProvider.LoggerFactory = saved; }

        lock (capture.Lines)
            return capture.Lines.Where(l => l.Message.Contains(name, StringComparison.Ordinal)
                                         && l.Message.Contains("EnableStandingRiderCrossing", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void A_region_that_turns_the_feature_on_says_so()
    {
        TestHelpers.InMethod();
        var line = Assert.Single(LinesFor("Rider log region on", 6401, "true"));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.EndsWith("EnableStandingRiderCrossing is on", line.Message);
    }

    [Fact]
    public void A_region_that_sets_the_feature_off_says_so()
    {
        TestHelpers.InMethod();
        var line = Assert.Single(LinesFor("Rider log region off", 6402, "false"));
        Assert.EndsWith("EnableStandingRiderCrossing is off", line.Message);
    }

    [Fact]
    public void A_region_that_does_not_set_the_key_says_it_is_off()
    {
        TestHelpers.InMethod();
        var line = Assert.Single(LinesFor("Rider log region unset", 6403, null));
        Assert.EndsWith("EnableStandingRiderCrossing is off", line.Message);
    }
}
