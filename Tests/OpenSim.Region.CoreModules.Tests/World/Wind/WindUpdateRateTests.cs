/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// wind_update_rate is a frame count that WindUpdate divides by, so a value below 1 would throw a divide-by-zero
/// (or, for a negative value, run on a nonsense schedule) every frame. The [Wind] section falls back to the default
/// with one warning, and the console refuses the value and keeps the current one.
/// </summary>
// The log capture swaps the process's logger factory, which is process-wide; this assembly runs its tests one at a
// time (AssemblyInfo.cs), so no collection is needed.
public class WindUpdateRateTests : OpenSimTestCase
{
    private const int DefaultRate = 150;

    private readonly ILoggerFactory m_previousFactory = LoggerProvider.LoggerFactory;
    private readonly Capture m_capture = new Capture();

    public WindUpdateRateTests()
    {
        LoggerProvider.LoggerFactory = m_capture;
    }

    public override void Dispose()
    {
        LoggerProvider.LoggerFactory = m_previousFactory;
        base.Dispose();
    }

    private sealed class Capture : ILoggerFactory
    {
        private readonly List<(LogLevel Level, string Text)> m_lines = new();

        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        public List<(LogLevel Level, string Text)> Snapshot()
        {
            lock (m_lines) return new List<(LogLevel, string)>(m_lines);
        }

        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (m_c.m_lines) m_c.m_lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static TestScene NewScene()
    {
        TestScene scene = new SceneHelpers().SetupScene("Wind rate test region", UUID.Random(), 1000, 1000, 256, 256, new IniConfigSource());
        scene.RegionInfo.RegionSettings.WaterHeight = 0f;
        return scene;
    }

    private static WindModule ModuleOn(TestScene scene, string rate)
    {
        IniConfigSource config = new IniConfigSource();
        IConfig windConfig = config.AddConfig("Wind");
        windConfig.Set("wind_plugin", "ZephyrWind");
        if (rate != null)
            windConfig.Set("wind_update_rate", rate);
        var module = new WindModule();
        module.Initialise(config);
        module.AddRegion(scene);
        return module;
    }

    private List<string> RateWarnings()
        => m_capture.Snapshot()
            .Where(l => l.Level == LogLevel.Warning && l.Text.Contains("wind_update_rate"))
            .Select(l => l.Text).ToList();

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void AnIniRateBelowOneUsesTheDefaultWithOneWarning(string configured)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, configured);

        Assert.Equal(DefaultRate, module.WindUpdateRate);
        Assert.Single(RateWarnings());
    }

    [Fact]
    public void AValidIniRateIsUsedWithoutAWarning()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "5");

        Assert.Equal(5, module.WindUpdateRate);
        Assert.Empty(RateWarnings());
    }

    [Fact]
    public void AnAbsentIniRateIsTheDefaultWithoutAWarning()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, null);

        Assert.Equal(DefaultRate, module.WindUpdateRate);
        Assert.Empty(RateWarnings());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void WindUpdateDoesNotThrowWhenTheIniRateWasBelowOne(string configured)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, configured);

        // Before the fix a rate of 0 made the very first call divide by zero.
        for (int i = 0; i < 400; i++)
            module.WindUpdate();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void TheConsoleRefusesARateBelowOneAndKeepsTheCurrentValue(string typed)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "7");

        string message = module.SetUpdateRate(typed);

        Assert.Equal(7, module.WindUpdateRate);
        Assert.Contains("at least 1", message);
        for (int i = 0; i < 50; i++)
            module.WindUpdate();
    }

    [Fact]
    public void TheConsoleAcceptsAValidRate()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "7");

        string message = module.SetUpdateRate("20");

        Assert.Equal(20, module.WindUpdateRate);
        Assert.Contains("20", message);
    }

    [Fact]
    public void TheConsoleRefusesATextThatIsNotAWholeNumber()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "7");

        string message = module.SetUpdateRate("fast");

        Assert.Equal(7, module.WindUpdateRate);
        Assert.Contains("fast", message);
    }
}
