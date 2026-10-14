/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.CoreModules.World.Wind.Plugins;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// The ZephyrWind plugin (surface, aloft and underwater wind on 16 m cells, shaped by terrain and following the
/// region's sun), its place in the wind module, and that the default wind plugin does not change.
/// </summary>
// Each test builds its own scene and plugin and reads no clock (the day fraction is supplied), so the class runs in
// parallel with the others.
public class ZephyrWindTests : OpenSimTestCase
{
    private const float Water = 0f;      // the test regions put the water at 0 m so terrain heights are the heights above water
    private const float Surface = 5f;    // a height inside the surface layer
    private const float Aloft = 500f;
    private const float Under = -5f;

    private static TestScene NewScene(uint sizeX = 256, uint sizeY = 256)
    {
        TestScene scene = new SceneHelpers().SetupScene("Zephyr test region", UUID.Random(), 1000, 1000, sizeX, sizeY, new IniConfigSource());
        scene.RegionInfo.RegionSettings.WaterHeight = Water;
        Flat(scene, 0f);
        return scene;
    }

    private static void Flat(Scene scene, float height)
    {
        var map = scene.Heightmap;
        for (int x = 0; x < map.Width; x++)
            for (int y = 0; y < map.Height; y++)
                map[x, y] = height;
    }

    private static IConfig Wind(params (string Key, string Value)[] settings)
    {
        IConfig windConfig = new IniConfigSource().AddConfig("Wind");
        foreach (var (key, value) in settings)
            windConfig.Set(key, value);
        return windConfig;
    }

    /// <summary>A plugin on the scene, with no wind variance unless the settings say otherwise.</summary>
    private static ZephyrWind NewPlugin(TestScene scene, params (string Key, string Value)[] settings)
    {
        var z = new ZephyrWind { DayFractionSource = () => 0.3f };
        z.WindConfig(scene, Wind(settings));
        return z;
    }

    private static readonly (string, string)[] Steady =
    {
        ("avg_strength", "7"), ("avg_direction", "90"), ("var_strength", "0"), ("var_direction", "0"),
    };

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static void Near(Vector2 expected, Vector2 actual, float tolerance = 0.001f)
        => Assert.True(Vector2.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    // ---- plugin selection and defaults ----

    private static WindModule ModuleOn(TestScene scene, params (string Key, string Value)[] settings)
    {
        IniConfigSource config = new IniConfigSource();
        IConfig windConfig = config.AddConfig("Wind");
        foreach (var (key, value) in settings)
            windConfig.Set(key, value);
        var module = new WindModule();
        module.Initialise(config);
        module.AddRegion(scene);
        return module;
    }

    [Fact]
    public void ZephyrIsSelectedWithWindPluginAndNothingElseChanges()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, ("wind_plugin", "ZephyrWind"));
        Assert.Equal("ZephyrWind", module.WindActiveModelPluginName);
        module.RemoveRegion(scene);
    }

    [Fact]
    public void WithoutWindPluginTheDefaultPluginStaysSimpleRandomWind()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene);
        Assert.Equal("SimpleRandomWind", module.WindActiveModelPluginName);
        module.RemoveRegion(scene);
    }

    [Fact]
    public void ZephyrKeysDefaultToHalcyonsValues()
    {
        var z = NewPlugin(NewScene());
        Assert.Equal(5.0f, z.WindParamGet("avgStrength"));
        Assert.Equal(0.0f, z.WindParamGet("avgDirection"));
        Assert.Equal(5.0f, z.WindParamGet("varStrength"));
        Assert.Equal(30.0f, z.WindParamGet("varDirection"));
        Assert.Equal(1.0f, z.WindParamGet("rateChangeAloft"));
        Assert.Equal(64.0f, z.WindParamGet("rateChangeFlutter"));
    }

    [Fact]
    public void ZephyrReadsItsKeys()
    {
        var z = NewPlugin(NewScene(),
            ("avg_strength", "9"), ("avg_direction", "45"), ("var_strength", "2"), ("var_direction", "10"),
            ("rate_change_aloft", "3"), ("rate_change_flutter", "7"));
        Assert.Equal(9f, z.WindParamGet("avgStrength"));
        Assert.Equal(45f, z.WindParamGet("avgDirection"));
        Assert.Equal(2f, z.WindParamGet("varStrength"));
        Assert.Equal(10f, z.WindParamGet("varDirection"));
        Assert.Equal(3f, z.WindParamGet("rateChangeAloft"));
        Assert.Equal(7f, z.WindParamGet("rateChangeFlutter"));
    }

    [Fact]
    public void TheStrengthKeyOfTheOtherPluginIsNotRead()
    {
        // The shipped [Wind] section carries "strength = 1.0" for SimpleRandomWind; as in Halcyon it must not
        // turn Zephyr's average of 5.0 into 1.0.
        var z = NewPlugin(NewScene(), ("strength", "1.0"));
        Assert.Equal(5.0f, z.WindParamGet("avgStrength"));
    }

    [Fact]
    public void ParametersCanBeChangedWhileRunning()
    {
        var z = NewPlugin(NewScene());
        z.WindParamSet("avgStrength", 12f);
        Assert.Equal(12f, z.WindParamGet("avgStrength"));
        Assert.Equal(6, z.WindParams().Count);
    }

    // ---- the grid ----

    [Theory]
    [InlineData(256u, 256u, 16, 16)]
    [InlineData(512u, 256u, 32, 16)]
    [InlineData(1024u, 1024u, 64, 64)]
    public void TheGridHasOneCellPer16MetresOfRegion(uint sizeX, uint sizeY, int cellsX, int cellsY)
    {
        TestScene scene = NewScene(sizeX, sizeY);
        var z = NewPlugin(scene, Steady);
        Assert.True(z.WindUpdate(0));
        Assert.Equal(cellsX, z.CellsX);
        Assert.Equal(cellsY, z.CellsY);

        // The last cell answers; the first metre outside the region does not.
        Assert.NotEqual(Vector3.Zero, z.WindSpeed(sizeX - 1, sizeY - 1, Aloft));
        Assert.Equal(Vector3.Zero, z.WindSpeed(sizeX, 10, Aloft));
        Assert.Equal(Vector3.Zero, z.WindSpeed(10, sizeY, Aloft));
        Assert.Equal(Vector3.Zero, z.WindSpeed(-1, 10, Aloft));
    }

    [Fact]
    public void ABigRegionKeepsItsFinerCellsInsteadOfStretchingSixteen()
    {
        TestScene scene = NewScene(1024, 1024);
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);

        // Two cells 16 m apart hold different winds; a 16 x 16 grid stretched over 1024 m would make them one cell.
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(100, 100, Aloft), new Vector3(1, 0, 0));
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(116, 100, Aloft), new Vector3(0, 3, 0));
        z.WindUpdate(1);
        Near(new Vector3(1, 0, 0), z.WindSpeed(96, 96, Aloft));
        Near(new Vector3(0, 3, 0), z.WindSpeed(112, 96, Aloft));
    }

    [Fact]
    public void TheViewerGetsSixteenBySixteenForAnyRegionSize()
    {
        foreach (uint size in new uint[] { 256, 512, 1024 })
        {
            var z = NewPlugin(NewScene(size, size), Steady);
            z.WindUpdate(0);
            Assert.Equal(256, z.WindLLClientArray().Length);
        }
    }

    [Fact]
    public void OnA256RegionTheViewerArrayIsTheCellsThemselves()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(5 * 16 + 1, 3 * 16 + 1, Aloft), new Vector3(2, -1, 0));
        z.WindUpdate(1);
        Vector2[] viewer = z.WindLLClientArray();
        Near(new Vector2(2, -1), viewer[3 * 16 + 5]);
        Near(new Vector2(0, 7), viewer[0]);
    }

    [Fact]
    public void OnABigRegionTheViewerArraySamplesTheFinerGrid()
    {
        TestScene scene = NewScene(1024, 1024);
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);

        // Viewer entry (i, j) stands for the region's 64 m block at (i * 64, j * 64): the wind at its corner.
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(7 * 64 + 1, 2 * 64 + 1, Aloft), new Vector3(-4, 4, 0));
        z.WindUpdate(1);
        Vector2[] viewer = z.WindLLClientArray();
        Near(new Vector2(-4, 4), viewer[2 * 16 + 7]);
        Near(new Vector2(0, 7), viewer[2 * 16 + 8]);
    }

    [Fact]
    public void EachViewerArrayIsItsOwnArray()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        Assert.NotSame(z.WindLLClientArray(), z.WindLLClientArray());
    }

    // ---- interpolation ----

    private static ZephyrWind FourCorners(out TestScene scene)
    {
        scene = NewScene();
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        int fixedType = (int)WindConstants.WindSpeedFixed;
        z.WindSet(fixedType, new Vector3(0, 0, Aloft), new Vector3(10, 1, 0));
        z.WindSet(fixedType, new Vector3(16, 0, Aloft), new Vector3(2, 3, 0));
        z.WindSet(fixedType, new Vector3(0, 16, Aloft), new Vector3(5, 20, 0));
        z.WindSet(fixedType, new Vector3(16, 16, Aloft), new Vector3(4, 8, 0));
        z.WindUpdate(1);
        return z;
    }

    [Fact]
    public void ACellsCornerGivesTheCellsOwnWind()
    {
        var z = FourCorners(out _);
        Near(new Vector3(10, 1, 0), z.WindSpeed(0, 0, Aloft));
        Near(new Vector3(2, 3, 0), z.WindSpeed(16, 0, Aloft));
        Near(new Vector3(5, 20, 0), z.WindSpeed(0, 16, Aloft));
        Near(new Vector3(4, 8, 0), z.WindSpeed(16, 16, Aloft));
    }

    [Theory]
    [InlineData(8f, 8f)]
    [InlineData(4f, 12f)]
    [InlineData(13f, 3f)]
    [InlineData(1f, 1f)]
    public void BetweenCellsTheWindIsInterpolatedBilinearly(float x, float y)
    {
        var z = FourCorners(out _);
        // f(x,y) = f(0,0)(1-dx)(1-dy) + f(1,0) dx (1-dy) + f(0,1) (1-dx) dy + f(1,1) dx dy
        float dx = x / 16f, dy = y / 16f;
        Vector2 f00 = new(10, 1), f10 = new(2, 3), f01 = new(5, 20), f11 = new(4, 8);
        Vector2 expected = f00 * ((1 - dx) * (1 - dy)) + f10 * (dx * (1 - dy)) + f01 * ((1 - dx) * dy) + f11 * (dx * dy);
        Near(new Vector3(expected.X, expected.Y, 0), z.WindSpeed(x, y, Aloft));
    }

    [Fact]
    public void TheLastCellDoesNotReadPastTheGrid()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        Near(new Vector3(0, 7, 0), z.WindSpeed(255.9f, 255.9f, Aloft));
    }

    // ---- layers ----

    [Fact]
    public void FlatGroundGivesTheSurfaceTheSameWindAsAloft()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        Near(new Vector3(0, 7, 0), z.WindSpeed(100, 100, Aloft));
        Near(new Vector3(0, 7, 0), z.WindSpeed(100, 100, Surface));
    }

    [Fact]
    public void BelowTheWaterTheWindIsAnUnderwaterCurrent()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        Vector3 aloft = z.WindSpeed(100, 100, Aloft);
        Vector3 current = z.WindSpeed(100, 100, Under);
        Assert.NotEqual(aloft, current);
        Assert.True(current.Length() <= 1f + 2f + 0.001f, "the current is 1 m/s on average and varies by at most 2");
    }

    [Fact]
    public void TheWaterlineSeparatesTheCurrentFromTheSurfaceWind()
    {
        TestScene scene = NewScene();
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Assert.Equal(z.WindSpeed(50, 50, Under), z.WindSpeed(50, 50, Water - 0.01f));
        Assert.Equal(z.WindSpeed(50, 50, Surface), z.WindSpeed(50, 50, Water));
    }

    [Fact]
    public void HighAboveATallCellTheWindIsTheWindAloftNotTheSurfaceWind()
    {
        TestScene scene = NewScene();
        var map = scene.Heightmap;
        // A spike 40 m high in the cell at (32..47, 32..47): its boundary layer is 40 + 40 = 80 m.
        map[35, 36] = 40f;
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Vector3 aloft = z.WindSpeed(40, 40, Aloft);
        Assert.NotEqual(aloft, z.WindSpeed(40, 40, 60f));       // inside the layer: turbulent surface wind
        Near(aloft, z.WindSpeed(40, 40, 81f));                  // above it: the wind aloft
    }

    [Fact]
    public void TheSurfaceLayerEndsAtTheWaterHeightPlusTheCellsHeightAndRange()
    {
        // Water at 20 m, ground at 30 m with one 10 m bump: the cell's highest ground stands 20 m out of the water
        // and its range is 10 (the 15 m minimum applies), so the layer ends at 20 + 20 + 15 = 55 m in region height.
        TestScene scene = NewScene();
        scene.RegionInfo.RegionSettings.WaterHeight = 20;
        Flat(scene, 30f);
        scene.Heightmap[35, 36] = 40f;
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Vector3 aloft = z.WindSpeed(40, 40, Aloft);

        Assert.NotEqual(aloft, z.WindSpeed(40, 40, 54f));   // still the surface wind (the old boundary was 35 m)
        Near(aloft, z.WindSpeed(40, 40, 56f));
    }

    [Fact]
    public void TheSurfaceBoundaryMovesUpWithTheWaterHeight()
    {
        TestScene scene = NewScene();
        scene.Heightmap[35, 36] = 40f;
        var dry = NewPlugin(scene, Steady);
        dry.WindUpdate(0);
        Vector3 aloft = dry.WindSpeed(40, 40, Aloft);
        Assert.Equal(dry.WindSpeed(40, 40, 79f), dry.WindSpeed(40, 40, 60f));   // dry region: the layer ends at 80 m
        Near(aloft, dry.WindSpeed(40, 40, 81f));

        // Water at 10 m: the bump stands 30 m out of the water, so the layer ends at 10 + 30 + 30 = 70 m.
        scene.RegionInfo.RegionSettings.WaterHeight = 10;
        var wet = NewPlugin(scene, Steady);
        wet.WindUpdate(0);
        Assert.NotEqual(aloft, wet.WindSpeed(40, 40, 69f));
        Near(aloft, wet.WindSpeed(40, 40, 71f));
    }

    [Fact]
    public void TheViewerArrayStaysTheSurfaceLayerWhateverTheWaterHeight()
    {
        TestScene scene = NewScene();
        scene.RegionInfo.RegionSettings.WaterHeight = 20;
        Flat(scene, 5f);
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Vector2[] viewer = z.WindLLClientArray();

        // A script at the surface, just above the water, reads what the viewers are given.
        Vector3 script = z.WindSpeed(48, 48, 25f);
        Near(new Vector2(script.X, script.Y), viewer[3 * 16 + 3]);
    }

    // ---- terrain shaping ----

    /// <summary>Population skewness of the heights, worked out directly rather than as the plugin does.</summary>
    private static double Skewness(IReadOnlyList<double> values)
    {
        double mean = values.Average();
        double m2 = values.Average(v => (v - mean) * (v - mean));
        double m3 = values.Average(v => (v - mean) * (v - mean) * (v - mean));
        return m2 == 0 ? 0 : m3 / Math.Pow(m2, 1.5);
    }

    [Fact]
    public void ARidgedCellTurnsTheSurfaceWindByItsSkewnessAndRange()
    {
        TestScene scene = NewScene();
        // One 10 m spike in the cell whose south-west corner is (32, 32); the other 255 points are at water level.
        scene.Heightmap[35, 36] = 10f;
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);

        var heights = new List<double>(256);
        for (int i = 0; i < 255; i++) heights.Add(0);
        heights.Add(10);
        double skew = Skewness(heights);
        double range = Math.Max(10.0 / 5.0, 1.0);
        double turn = Math.PI * skew * 0.333 * range;

        // Aloft the wind is 7 m/s at 90 degrees; the surface wind is the same speed turned by 'turn'.
        double baseAngle = Math.PI / 2;
        Vector3 expected = new Vector3((float)(7 * Math.Cos(baseAngle + turn)), (float)(7 * Math.Sin(baseAngle + turn)), 0);
        Near(expected, z.WindSpeed(32, 32, Surface), 0.01f);
        Near(new Vector3(0, 7, 0), z.WindSpeed(32, 32, Aloft));

        // A cell with no relief is untouched.
        Near(new Vector3(0, 7, 0), z.WindSpeed(200, 200, Surface));
    }

    [Fact]
    public void GroundBelowTheWaterCountsAsFlat()
    {
        TestScene scene = NewScene();
        scene.RegionInfo.RegionSettings.WaterHeight = 20;
        Flat(scene, 5f);
        scene.Heightmap[35, 36] = 12f;      // a bump, but still under the 20 m waterline
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Near(new Vector2(0, 7), z.WindLLClientArray()[2 * 16 + 2]);     // the surface wind of the bumpy cell is not turned
    }

    [Fact]
    public void TerrainChangesAreNoticedWhenTheTerrainIsTainted()
    {
        TestScene scene = NewScene();
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Near(new Vector3(0, 7, 0), z.WindSpeed(32, 32, Surface));

        scene.Heightmap[35, 36] = 10f;
        z.WindUpdate(1);
        Near(new Vector3(0, 7, 0), z.WindSpeed(32, 32, Surface));   // not told: the statistics are kept

        scene.EventManager.TriggerTerrainTainted();
        z.WindUpdate(2);
        Assert.NotEqual(new Vector3(0, 7, 0), z.WindSpeed(32, 32, Surface));
    }

    [Fact]
    public void AReplacedHeightmapIsNoticedWithoutATaint()
    {
        TestScene scene = NewScene();
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);

        var other = scene.Heightmap.MakeCopy();
        other[35, 36] = 10f;
        scene.Heightmap = other;
        z.WindUpdate(1);
        Assert.NotEqual(new Vector3(0, 7, 0), z.WindSpeed(32, 32, Surface));
    }

    [Fact]
    public void ABigRegionShapesTheWindInCellsBeyondThe256()
    {
        TestScene scene = NewScene(1024, 1024);
        scene.Heightmap[900 + 3, 700 + 4] = 10f;
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Assert.NotEqual(new Vector3(0, 7, 0), z.WindSpeed(896, 688, Surface));     // the cell holding the spike
        Near(new Vector3(0, 7, 0), z.WindSpeed(600, 600, Surface));
    }

    // ---- the sun ----

    [Fact]
    public void ThePrevailingWindFollowsTheRegionsDay()
    {
        TestScene scene = NewScene();
        float fraction = 0.1f;
        var z = NewPlugin(scene);                 // the default variance
        z.DayFractionSource = () => fraction;
        z.WindUpdate(0);
        Vector3 morning = z.WindSpeed(100, 100, Aloft);

        fraction = 0.2f;
        z.WindUpdate(1);
        Vector3 later = z.WindSpeed(100, 100, Aloft);
        Assert.NotEqual(morning, later);

        // The same time of day gives the same wind, however often the wind is updated.
        z.WindUpdate(2);
        Assert.Equal(later, z.WindSpeed(100, 100, Aloft));
        fraction = 0.1f;
        z.WindUpdate(3);
        Assert.Equal(morning, z.WindSpeed(100, 100, Aloft));
    }

    [Fact]
    public void WithoutAnEnvironmentModuleTheWallClockDrivesTheWind()
    {
        // No DayFractionSource and no environment module in the scene: the update still works and gives wind.
        var z = new ZephyrWind();
        z.WindConfig(NewScene(), Wind(Steady));
        Assert.True(z.WindUpdate(0));
        Near(new Vector3(0, 7, 0), z.WindSpeed(100, 100, Aloft));
    }

    [Fact]
    public void ANegativeWindSpeedBlowsTheOtherWay()
    {
        var z = NewPlugin(NewScene(), ("avg_strength", "-7"), ("avg_direction", "90"), ("var_strength", "0"), ("var_direction", "0"));
        z.WindUpdate(0);
        Near(new Vector3(0, -7, 0), z.WindSpeed(100, 100, Aloft));
    }

    [Fact]
    public void ACalmRegionHasNoNaN()
    {
        // Halcyon divided by the speed, which is 0 here.
        var z = NewPlugin(NewScene(), ("avg_strength", "0"), ("var_strength", "0"));
        z.WindUpdate(0);
        Assert.Equal(Vector3.Zero, z.WindSpeed(100, 100, Surface));
        Assert.All(z.WindLLClientArray(), v => Assert.False(float.IsNaN(v.X) || float.IsNaN(v.Y)));
    }

    // ---- wind setters ----

    [Fact]
    public void AFixedCellHoldsTheGivenWindAndLeavesItsNeighboursAlone()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(40, 40, Aloft), new Vector3(3, -2, 9));
        z.WindUpdate(1);
        z.WindUpdate(2);
        Near(new Vector3(3, -2, 0), z.WindSpeed(32, 32, Aloft));            // z of the speed is dropped
        Near(new Vector3(3, -2, 0), z.WindSpeed(32, 32, Surface));          // fixed means no terrain turbulence
        Near(new Vector3(0, 7, 0), z.WindSpeed(64, 32, Aloft));
    }

    [Fact]
    public void ASetterBelowTheWaterFixesTheCurrent()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(40, 40, Under), new Vector3(0.5f, 0.25f, 0));
        z.WindUpdate(1);
        Near(new Vector3(0.5f, 0.25f, 0), z.WindSpeed(32, 32, Under));
    }

    [Fact]
    public void AFixedCellWithTerrainTurbulenceKeepsItsWindAloftAndTurnsItsSurfaceWind()
    {
        TestScene scene = NewScene();
        scene.Heightmap[35, 36] = 10f;
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        z.WindSet((int)(WindConstants.WindSpeedFixed | WindConstants.WindSpeedTurbulence), new Vector3(32, 32, Aloft), new Vector3(6, 0, 0));
        z.WindUpdate(1);
        Near(new Vector3(6, 0, 0), z.WindSpeed(32, 32, Aloft));
        Vector3 surface = z.WindSpeed(32, 32, Surface);
        Assert.NotEqual(new Vector3(6, 0, 0), surface);
        Assert.Equal(6f, surface.Length(), 0.01f);
    }

    [Fact]
    public void TheDefaultsFormGivesTheCellBackToTheModelWithTerrainTurbulence()
    {
        TestScene scene = NewScene();
        scene.Heightmap[35, 36] = 10f;
        var z = NewPlugin(scene, Steady);
        z.WindUpdate(0);
        Vector3 modelAloft = z.WindSpeed(32, 32, Aloft);
        Vector3 modelSurface = z.WindSpeed(32, 32, Surface);

        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(32, 32, Aloft), new Vector3(1, 1, 0));
        z.WindUpdate(1);
        Near(new Vector3(1, 1, 0), z.WindSpeed(32, 32, Aloft));

        z.WindSet((int)WindConstants.WindSpeedDefault, new Vector3(32, 32, Aloft), Vector3.Zero);
        z.WindUpdate(2);
        Near(modelAloft, z.WindSpeed(32, 32, Aloft));
        Near(modelSurface, z.WindSpeed(32, 32, Surface));
    }

    [Fact]
    public void AnUnderwaterDefaultsFormBringsTheCurrentBack()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        Vector3 current = z.WindSpeed(32, 32, Under);
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(32, 32, Under), new Vector3(4, 4, 0));
        z.WindUpdate(1);
        z.WindSet((int)WindConstants.WindSpeedDefault, new Vector3(32, 32, Under), Vector3.Zero);
        z.WindUpdate(2);
        Near(current, z.WindSpeed(32, 32, Under));
    }

    [Fact]
    public void ASetterOutsideTheRegionIsIgnored()
    {
        var z = NewPlugin(NewScene(), Steady);
        z.WindUpdate(0);
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(-5, 10, Aloft), new Vector3(9, 9, 0));
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(10, 256, Aloft), new Vector3(9, 9, 0));
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(300, 10, Aloft), new Vector3(9, 9, 0));
        z.WindUpdate(1);
        Near(new Vector3(0, 7, 0), z.WindSpeed(10, 10, Aloft));
        Near(new Vector3(0, 7, 0), z.WindSpeed(250, 250, Aloft));
    }

    [Fact]
    public void ASetterReachesTheFarCellsOfABigRegion()
    {
        var z = NewPlugin(NewScene(1024, 1024), Steady);
        z.WindUpdate(0);
        z.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(1020, 1020, Aloft), new Vector3(-3, 3, 0));
        z.WindUpdate(1);
        Near(new Vector3(-3, 3, 0), z.WindSpeed(1016, 1016, Aloft));
    }

    // ---- the wind module around it ----

    [Fact]
    public void TheModuleHandsASetterToTheActivePlugin()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene, ("wind_plugin", "ZephyrWind"), ("avg_strength", "7"), ("avg_direction", "90"),
            ("var_strength", "0"), ("var_direction", "0"));
        module.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(40, 40, Aloft), new Vector3(2, 2, 0));
        Near(new Vector3(0, 7, 0), module.WindSpeed(200, 200, (int)Aloft));
        module.RemoveRegion(scene);
    }

    [Fact]
    public void ADefaultPluginIgnoresASetterAndKeepsItsWind()
    {
        TestScene scene = NewScene();
        WindModule module = ModuleOn(scene);
        Vector3 before = module.WindSpeed(40, 40, 30);
        module.WindSet((int)WindConstants.WindSpeedFixed, new Vector3(40, 40, 30), new Vector3(50, 50, 0));
        Assert.Equal(before, module.WindSpeed(40, 40, 30));
        module.RemoveRegion(scene);
    }

    // The default plugin is internal to the wind module's assembly.
    private static IWindModelPlugin DefaultPlugin()
        => (IWindModelPlugin)Activator.CreateInstance(
            typeof(WindModule).Assembly.GetType("OpenSim.Region.CoreModules.World.Wind.Plugins.SimpleRandomWind"));

    [Fact]
    public void TheDefaultPluginStillGivesTheViewerSixteenBySixteen()
    {
        IWindModelPlugin plugin = DefaultPlugin();
        plugin.WindConfig(NewScene(), null);
        plugin.WindUpdate(0);
        Assert.Equal(256, plugin.WindLLClientArray().Length);
        plugin.WindSet(1, new Vector3(1, 1, 1), new Vector3(1, 1, 1));       // does nothing, throws nothing
    }

    [Fact]
    public void ABigRegionOfTheDefaultPluginIsHandledAsBefore()
    {
        // The default plugin keeps its fixed 16 x 16 grid and clamps outside it, as before this plugin existed.
        IWindModelPlugin plugin = DefaultPlugin();
        plugin.WindConfig(NewScene(1024, 1024), null);
        plugin.WindUpdate(0);
        Assert.Equal(256, plugin.WindLLClientArray().Length);
        Vector3 edge = plugin.WindSpeed(255, 255, 30);
        Assert.Equal(edge, plugin.WindSpeed(900, 900, 30));
    }
}
