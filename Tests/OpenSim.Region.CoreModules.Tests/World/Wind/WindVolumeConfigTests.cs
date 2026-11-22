/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// The [Weather] wind-volume caps as the wind module reads them. A value that is not a number, or not a finite one,
/// takes the key's default (with a warning) instead of stopping the wind module from loading; a number is read as
/// before.
/// </summary>
// Each test builds its own config and store; the module test owns its module. No process-wide state is touched.
public class WindVolumeConfigTests
{
    public static IEnumerable<object[]> Keys() => new[]
    {
        new object[] { "max_volumes_region" },
        new object[] { "max_volumes_object" },
        new object[] { "max_radius" },
        new object[] { "max_height" },
        new object[] { "max_speed" },
        new object[] { "max_lifetime" },
    };

    public static IEnumerable<object[]> FloatKeys() => new[]
    {
        new object[] { "max_radius" },
        new object[] { "max_height" },
        new object[] { "max_speed" },
        new object[] { "max_lifetime" },
    };

    private static IConfig Weather(string key, string value)
    {
        IConfig weather = new IniConfigSource().AddConfig("Weather");
        weather.Set("enabled", "true");
        weather.Set(key, value);
        return weather;
    }

    private static float Limit(WindVolumeLimits limits, string key) => key switch
    {
        "max_volumes_region" => limits.MaxVolumesRegion,
        "max_volumes_object" => limits.MaxVolumesObject,
        "max_radius" => limits.MaxRadius,
        "max_height" => limits.MaxHeight,
        "max_speed" => limits.MaxSpeed,
        "max_lifetime" => limits.MaxLifetimeSeconds,
        _ => throw new ArgumentException(key),
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void AValueThatIsNotANumberTakesTheDefaultAndWeatherStaysOn(string key)
    {
        IWindVolumeStore? store = null;
        Exception thrown = Record.Exception(() => store = WindModule.CreateWindVolumes(Weather(key, "abc")));

        Assert.Null(thrown);
        Assert.True(store!.Enabled);
        Assert.Equal(Limit(new WindVolumeLimits(), key), Limit(store!.Limits, key));
    }

    [Theory]
    [MemberData(nameof(FloatKeys))]
    public void ANumberThatIsNotFiniteTakesTheDefault(string key)
    {
        foreach (string text in new[] { "NaN", "Infinity", "-Infinity" })
        {
            IWindVolumeStore? store = null;
            Exception thrown = Record.Exception(() => store = WindModule.CreateWindVolumes(Weather(key, text)));

            Assert.Null(thrown);
            Assert.Equal(Limit(new WindVolumeLimits(), key), Limit(store!.Limits, key));
        }
    }

    [Fact]
    public void AWholeNumberWithAFractionForACountTakesTheDefault()
    {
        IWindVolumeStore? store = null;
        Exception thrown = Record.Exception(() => store = WindModule.CreateWindVolumes(Weather("max_volumes_region", "4.5")));

        Assert.Null(thrown);
        Assert.Equal(new WindVolumeLimits().MaxVolumesRegion, store!.Limits.MaxVolumesRegion);
    }

    // Guards: what was read correctly before is read the same way now.
    [Theory]
    [InlineData("max_volumes_region", "7", 7f)]
    [InlineData("max_volumes_object", " 2 ", 2f)]
    [InlineData("max_radius", "64.5", 64.5f)]
    [InlineData("max_height", "100", 100f)]
    [InlineData("max_speed", "25", 25f)]
    [InlineData("max_lifetime", "600", 600f)]
    public void ANumberIsReadAsBefore(string key, string text, float expected)
    {
        IWindVolumeStore store = WindModule.CreateWindVolumes(Weather(key, text));
        Assert.Equal(expected, Limit(store!.Limits, key));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void ANegativeNumberIsStillHeldAtZero(string key)
    {
        IWindVolumeStore store = WindModule.CreateWindVolumes(Weather(key, "-5"));
        Assert.Equal(0f, Limit(store!.Limits, key));
    }

    [Theory]
    [InlineData("ture")]
    [InlineData("2")]
    [InlineData("enabled")]
    public void AnEnabledValueThatIsNotTrueOrFalseLeavesWeatherOffAndTheModuleLoads(string text)
    {
        IConfig weather = new IniConfigSource().AddConfig("Weather");
        weather.Set("enabled", text);

        IWindVolumeStore? store = null;
        Exception? thrown = Record.Exception(() => store = WindModule.CreateWindVolumes(weather));

        Assert.Null(thrown);
        Assert.False(store!.Enabled);
    }

    // Guard: the spellings read before are read the same way now.
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void AnEnabledValueOfTrueOrFalseIsReadAsBefore(string text, bool expected)
    {
        IConfig weather = new IniConfigSource().AddConfig("Weather");
        weather.Set("enabled", text);
        Assert.Equal(expected, WindModule.CreateWindVolumes(weather).Enabled);
    }

    [Fact]
    public void TheWindModuleStillLoadsWithAValueThatIsNotANumber()
    {
        IniConfigSource config = new IniConfigSource();
        config.AddConfig("Wind").Set("wind_plugin", "SimpleRandomWind");
        IConfig weather = config.AddConfig("Weather");
        weather.Set("enabled", "true");
        weather.Set("max_speed", "fast");

        var module = new WindModule();
        Exception thrown = Record.Exception(() => module.Initialise(config));

        Assert.Null(thrown);
        Assert.True(module.WindVolumes.Enabled);
        Assert.Equal(new WindVolumeLimits().MaxSpeed, module.WindVolumes.Limits.MaxSpeed);
    }
}
