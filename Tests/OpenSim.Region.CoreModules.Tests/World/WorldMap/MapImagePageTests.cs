/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using CoreJ2K;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.World.WorldMap;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using SkiaSharp;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.WorldMap;

/// <summary>
/// The region's map image page (index.php?method=regionImage&lt;region id&gt;), which a Hypergrid gatekeeper
/// gives other grids as the region's map image. It answers with the region's map tile as JPEG.
/// </summary>
public class MapImagePageTests
{
    [Fact]
    public void ThePageAnswersWithTheTileTheRegionMade()
    {
        WithRegion((scene, map) =>
        {
            map.GenerateMaptile();
            Assert.True(scene.RegionInfo.RegionSettings.TerrainImageID.IsNotZero());

            TestOSHttpResponse response = Fetch(map);

            Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/jpeg", response.ContentType);
            Assert.Equal(scene.AssetService.Get(scene.RegionInfo.RegionSettings.TerrainImageID.ToString()).Data, response.RawBuffer);
        });
    }

    [Fact]
    public void ATileStoredAsJpeg2000IsSentAsJpeg()
    {
        WithRegion((scene, map) =>
        {
            using SKBitmap tile = Tile();
            AssetBase asset = new(UUID.Random(), "terrainImage_old", (sbyte)AssetType.Texture, scene.RegionInfo.RegionID.ToString())
            {
                Data = J2kImage.ToBytes(tile),
                Flags = AssetFlags.Maptile,
            };
            scene.AssetService.Store(asset);
            scene.RegionInfo.RegionSettings.TerrainImageID = asset.FullID;

            TestOSHttpResponse response = Fetch(map);

            Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
            using SKBitmap sent = SKBitmap.Decode(response.RawBuffer);
            Assert.NotNull(sent);
            Assert.Equal(tile.Width, sent.Width);
        });
    }

    private static TestOSHttpResponse Fetch(WorldMapModule map)
    {
        IOSHttpRequest request = StubProxy.Create<IOSHttpRequest>((method, args) =>
            method.Name == "get_HttpMethod" ? (true, "GET") : (false, null));
        TestOSHttpResponse response = new();
        map.OnHTTPGetMapImage(request, response);
        return response;
    }

    private static void WithRegion(Action<Scene, WorldMapModule> test)
    {
        // The module registers its page on the default HTTP server; it is not started, only needed to exist.
        const uint Port = 19437;
        bool added = MainServer.Instance.DefaultServer is null;
        if (added)
            MainServer.Instance.AddHttpServer(new BaseHttpServer(Port));
        try
        {
            Scene scene = new SceneHelpers().SetupScene();
            IMapImageGenerator generator = StubProxy.Create<IMapImageGenerator>((method, args) =>
                method.Name == nameof(IMapImageGenerator.CreateMapTile) ? (true, Tile()) : (false, null));
            scene.RegisterModuleInterface(generator);

            IConfigSource config = new IniConfigSource();
            config.AddConfig("Map").Set("WorldMapModule", "WorldMap");
            WorldMapModule map = new();
            SceneHelpers.SetupSceneModules(scene, config, map);

            test(scene, map);
        }
        finally
        {
            if (added)
                MainServer.Instance.RemoveHttpServer(Port);
        }
    }

    private static SKBitmap Tile()
    {
        SKBitmap bitmap = new(256, 256);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(new SKColor(40, 120, 60));
        return bitmap;
    }
}
