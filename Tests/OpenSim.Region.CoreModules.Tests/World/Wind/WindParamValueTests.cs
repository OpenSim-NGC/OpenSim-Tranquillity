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
using OpenSim.Region.CoreModules.World.Wind.Plugins;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// Wind values that are not finite numbers (NaN, plus and minus infinity) are refused where scripts and the console
/// set them, and fall back to the default where the [Wind] section supplies them; a wind parameter change writes one
/// log line.
/// </summary>
// The log capture swaps the process's logger factory, which is process-wide; this assembly runs its tests one at a
// time (AssemblyInfo.cs), so no collection is needed.
public class WindParamValueTests : OpenSimTestCase
{
    private readonly ILoggerFactory m_previousFactory = LoggerProvider.LoggerFactory;
    private readonly Capture m_capture = new Capture();

    public WindParamValueTests()
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
        public readonly List<(LogLevel Level, string Text)> Lines = new();

        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        public List<(LogLevel Level, string Text)> Snapshot()
        {
            lock (Lines) return new List<(LogLevel, string)>(Lines);
        }

        public void Clear()
        {
            lock (Lines) Lines.Clear();
        }

        /// <summary>The lines about wind; threads of other code in the process may log into the capture too.</summary>
        public List<(LogLevel Level, string Text)> Wind()
            => Snapshot().Where(l => l.Text.Contains("[WIND]") || l.Text.Contains("Wind]") || l.Text.Contains("[ZephyrWind]")
                || l.Text.Contains("[ConfigurableWind]") || l.Text.Contains("[SimpleRandomWind]")).ToList();

        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (m_c.Lines) m_c.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    public static IEnumerable<object[]> NotNumbers()
    {
        yield return new object[] { float.NaN };
        yield return new object[] { float.PositiveInfinity };
        yield return new object[] { float.NegativeInfinity };
    }

    private static TestScene NewScene()
    {
        TestScene scene = new SceneHelpers().SetupScene("Wind value test region", UUID.Random(), 1000, 1000, 256, 256, new IniConfigSource());
        scene.RegionInfo.RegionSettings.WaterHeight = 0f;
        return scene;
    }

    /// <summary>A wind module on a scene with the given plugin selected. Every plugin can be addressed by name.</summary>
    private static WindModule ModuleOn(TestScene scene, string plugin, params (string Key, string Value)[] settings)
    {
        IniConfigSource config = new IniConfigSource();
        IConfig windConfig = config.AddConfig("Wind");
        windConfig.Set("wind_plugin", plugin);
        foreach (var (key, value) in settings)
            windConfig.Set(key, value);
        var module = new WindModule();
        module.Initialise(config);
        module.AddRegion(scene);
        return module;
    }

    // plugin name, a parameter it has, and the value that parameter starts with when no settings are given
    public static IEnumerable<object[]> Plugins()
    {
        yield return new object[] { "SimpleRandomWind", "strength", 1.0f };
        yield return new object[] { "ConfigurableWind", "avgStrength", 5.0f };
        yield return new object[] { "ZephyrWind", "avgStrength", 5.0f };
        yield return new object[] { "ZephyrWind", "rateChangeFlutter", 64.0f };
    }

    public static IEnumerable<object[]> PluginsAndNotNumbers()
    {
        foreach (object[] plugin in Plugins())
            foreach (object[] bad in NotNumbers())
                yield return new object[] { plugin[0], plugin[1], plugin[2], bad[0] };
    }

    // ---- WindModule.WindParamSet ----

    [Theory]
    [MemberData(nameof(PluginsAndNotNumbers))]
    public void ANonFiniteValueIsRefusedAndTheOldValueStays(string plugin, string param, float initial, float bad)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin);
        Assert.Equal(initial, module.WindParamGet(plugin, param));
        m_capture.Clear();

        module.WindParamSet(plugin, param, bad);

        Assert.Equal(initial, module.WindParamGet(plugin, param));
        var lines = m_capture.Wind();
        // One line, a warning, naming the plugin and the parameter: a script calling in a loop adds one line per call.
        var warning = Assert.Single(lines);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(plugin, warning.Text);
        Assert.Contains(param, warning.Text);
        module.RemoveRegion(scene);
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void AnOrdinaryValueIsStillSet(string plugin, string param, float initial)
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, plugin);

        module.WindParamSet(plugin, param, 12.5f);
        Assert.Equal(12.5f, module.WindParamGet(plugin, param));

        // zero and negative values are not new ranges to refuse: they pass as before
        module.WindParamSet(plugin, param, 0f);
        Assert.Equal(0f, module.WindParamGet(plugin, param));
        module.WindParamSet(plugin, param, -3f);
        Assert.Equal(-3f, module.WindParamGet(plugin, param));
        Assert.DoesNotContain(m_capture.Wind(), l => l.Level == LogLevel.Warning);
        module.RemoveRegion(scene);
    }

    [Fact]
    public void AnUnknownPluginStillThrows()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ZephyrWind");
        Assert.Throws<Exception>(() => module.WindParamSet("NoSuchWind", "avgStrength", float.NaN));
        module.RemoveRegion(scene);
    }

    // ---- [Wind] section values ----

    // plugin, key in [Wind], parameter that reads it, default
    public static IEnumerable<object[]> IniKeys()
    {
        yield return new object[] { "SimpleRandomWind", "strength", "strength", 1.0f };
        yield return new object[] { "ConfigurableWind", "avg_strength", "avgStrength", 5.0f };
        yield return new object[] { "ConfigurableWind", "avg_direction", "avgDirection", 0.0f };
        yield return new object[] { "ConfigurableWind", "var_strength", "varStrength", 5.0f };
        yield return new object[] { "ConfigurableWind", "var_direction", "varDirection", 30.0f };
        yield return new object[] { "ConfigurableWind", "rate_change", "rateChange", 1.0f };
        yield return new object[] { "ZephyrWind", "avg_strength", "avgStrength", 5.0f };
        yield return new object[] { "ZephyrWind", "avg_direction", "avgDirection", 0.0f };
        yield return new object[] { "ZephyrWind", "var_strength", "varStrength", 5.0f };
        yield return new object[] { "ZephyrWind", "var_direction", "varDirection", 30.0f };
        yield return new object[] { "ZephyrWind", "rate_change_aloft", "rateChangeAloft", 1.0f };
        yield return new object[] { "ZephyrWind", "rate_change_flutter", "rateChangeFlutter", 64.0f };
    }

    public static IEnumerable<object[]> IniKeysAndNotNumbers()
    {
        foreach (object[] key in IniKeys())
            foreach (string bad in new[] { "NaN", "Infinity", "-Infinity", "1e999" })
                yield return new object[] { key[0], key[1], key[2], key[3], bad };
    }

    [Theory]
    [MemberData(nameof(IniKeysAndNotNumbers))]
    public void ANonFiniteIniValueFallsBackToTheDefaultWithOneWarning(string plugin, string key, string param, float defaultValue, string bad)
    {
        TestScene scene = NewScene();
        m_capture.Clear();
        WindModule module = ModuleOn(scene, plugin, (key, bad));

        Assert.Equal(defaultValue, module.WindParamGet(plugin, param));
        var warning = Assert.Single(m_capture.Wind(), l => l.Level == LogLevel.Warning);
        Assert.Contains(plugin, warning.Text);
        Assert.Contains(key, warning.Text);
        module.RemoveRegion(scene);
    }

    [Theory]
    [MemberData(nameof(IniKeys))]
    public void AnOrdinaryIniValueIsReadAsBefore(string plugin, string key, string param, float defaultValue)
    {
        TestScene scene = NewScene();
        m_capture.Clear();
        WindModule module = ModuleOn(scene, plugin, (key, "7.25"));

        Assert.Equal(7.25f, module.WindParamGet(plugin, param));
        Assert.DoesNotContain(m_capture.Wind(), l => l.Level == LogLevel.Warning);
        module.RemoveRegion(scene);
    }

    // ---- logging ----

    [Fact]
    public void ZephyrWritesOneLinePerChangeAndItShowsTheFlutterRate()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, "ZephyrWind");
        m_capture.Clear();

        module.WindParamSet("ZephyrWind", "avgStrength", 8f);
        Assert.Single(m_capture.Wind());

        module.WindParamSet("ZephyrWind", "rateChangeFlutter", 33f);
        var lines = m_capture.Wind();
        Assert.Equal(2, lines.Count);
        Assert.Contains("rate change flutter 33", lines[1].Text);

        for (int i = 0; i < 20; i++)
            module.WindParamSet("ZephyrWind", "varStrength", i);
        Assert.Equal(22, m_capture.Wind().Count);

        // a parameter the plugin does not have changes nothing and writes nothing
        module.WindParamSet("ZephyrWind", "noSuchParameter", 1f);
        Assert.Equal(22, m_capture.Wind().Count);
        module.RemoveRegion(scene);
    }

    [Fact]
    public void ZephyrWritesOneLineWhenItsSettingsAreRead()
    {
        TestScene scene = NewScene();
        m_capture.Clear();
        WindModule module = ModuleOn(scene, "ZephyrWind", ("avg_strength", "9"));
        Assert.Single(m_capture.Wind(), l => l.Text.Contains("[ZephyrWind] Settings"));
        module.RemoveRegion(scene);
    }

    // ---- the region's day fraction ----

    private static ZephyrWind ZephyrOn(TestScene scene, Func<float> day)
    {
        var z = new ZephyrWind { DayFractionSource = day };
        IConfig windConfig = new IniConfigSource().AddConfig("Wind");
        windConfig.Set("var_strength", "3");
        windConfig.Set("var_direction", "25");
        z.WindConfig(scene, windConfig);
        return z;
    }

    [Theory]
    [MemberData(nameof(NotNumbers))]
    public void ADayFractionThatIsNotANumberGivesTheWindOfTheStartOfTheDay(float bad)
    {
        TestScene scene = NewScene();
        var atZero = ZephyrOn(scene, () => 0f);
        atZero.WindUpdate(0);

        var z = ZephyrOn(scene, () => bad);
        m_capture.Clear();
        z.WindUpdate(0);

        Vector3 expected = atZero.WindSpeed(40, 40, 500f);
        Vector3 actual = z.WindSpeed(40, 40, 500f);
        Assert.True(float.IsFinite(actual.X) && float.IsFinite(actual.Y), $"got {actual}");
        Assert.Equal(expected, actual);
        Assert.All(z.WindLLClientArray(), v => Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y)));
    }

    [Fact]
    public void TheDayFractionWarningIsWrittenOnceUntilTheFractionIsANumberAgain()
    {
        TestScene scene = NewScene();
        float day = float.NaN;
        var z = ZephyrOn(scene, () => day);
        m_capture.Clear();

        z.WindUpdate(0);
        z.WindUpdate(1);
        z.WindUpdate(2);
        Assert.Single(m_capture.Wind(), l => l.Level == LogLevel.Warning && l.Text.Contains("day fraction"));

        day = 0.4f;
        z.WindUpdate(3);
        day = float.PositiveInfinity;
        z.WindUpdate(4);
        Assert.Equal(2, m_capture.Wind().Count(l => l.Level == LogLevel.Warning && l.Text.Contains("day fraction")));
    }

    [Fact]
    public void AnOrdinaryDayFractionWritesNoWarning()
    {
        TestScene scene = NewScene();
        var z = ZephyrOn(scene, () => 0.3f);
        m_capture.Clear();
        z.WindUpdate(0);
        Assert.DoesNotContain(m_capture.Wind(), l => l.Level == LogLevel.Warning);
    }
}
