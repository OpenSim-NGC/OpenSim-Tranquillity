/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Region.CoreModules.World.LegacyMap;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.LegacyMap;

/// <summary>
/// The map tile the default map image module (MapImageModule) draws for a region: land drawn by the textured or
/// the shaded terrain renderer, as TextureOnMapTile chooses, and water where the terrain is below the water height.
/// </summary>
public class MapImageModuleTests
{
    // MapColorWater's default, in TexturedMapTileRenderer and ShadedMapTileRenderer.
    private static readonly SKColor Water = new(0x1D, 0x47, 0x5F);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFlatRegionIsNotDrawnBlack(bool textureOnMapTile)
    {
        using SKBitmap tile = Tile(textureOnMapTile, (x, y) => 25f);

        Assert.NotNull(tile);
        for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
                Assert.True(tile.GetPixel(x, y) != SKColors.Black, $"pixel <{x},{y}> is black");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerrainBelowTheWaterIsDrawnInTheWaterColour(bool textureOnMapTile)
    {
        // The west half is 10m, under the default 20m water; the east half is land at 30m.
        using SKBitmap tile = Tile(textureOnMapTile, (x, y) => x < 128 ? 10f : 30f);

        Assert.NotNull(tile);
        Assert.Equal(Water, tile.GetPixel(20, 128));
        Assert.Equal(Water, tile.GetPixel(100, 10));
        SKColor land = tile.GetPixel(200, 128);
        Assert.NotEqual(Water, land);
        Assert.NotEqual(SKColors.Black, land);
    }

    private static SKBitmap Tile(bool textureOnMapTile, Func<int, int, float> height)
    {
        Scene scene = new SceneHelpers().SetupScene();
        var map = scene.Heightmap;
        for (int x = 0; x < map.Width; x++)
            for (int y = 0; y < map.Height; y++)
                map[x, y] = height(x, y);

        IConfigSource config = new IniConfigSource();
        config.AddConfig("Map").Set("TextureOnMapTile", textureOnMapTile.ToString());
        MapImageModule module = new();
        SceneHelpers.SetupSceneModules(scene, config, module);

        return module.CreateMapTile();
    }
}
