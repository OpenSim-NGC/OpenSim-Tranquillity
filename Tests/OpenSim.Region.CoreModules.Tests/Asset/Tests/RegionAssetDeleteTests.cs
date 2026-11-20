/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Asset;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.Asset.Tests;

/// <summary>
/// The region asset connector, in front of the region's asset cache. An asset it deletes is no longer served
/// from the cache.
/// </summary>
public class RegionAssetDeleteTests : OpenSimTestCase
{
    [Fact]
    public void ADeletedAssetIsNotServedFromTheCache()
    {
        Dictionary<string, AssetBase> store = new();
        RegionAssetConnector connector = Connector(store, out _);

        AssetBase asset = AssetHelpers.CreateNotecardAsset();
        asset.ID = UUID.Random().ToString();
        connector.Store(asset);
        Assert.NotNull(connector.Get(asset.ID));

        Assert.True(connector.Delete(asset.ID));

        Assert.False(store.ContainsKey(asset.ID));
        Assert.Null(connector.Get(asset.ID));
    }

    private static RegionAssetConnector Connector(Dictionary<string, AssetBase> store, out FlotsamAssetCache cache)
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("AssetCaching", "FlotsamAssetCache");
        IConfig cacheConfig = config.AddConfig("AssetCache");
        cacheConfig.Set("FileCacheEnabled", "false");
        cacheConfig.Set("MemoryCacheEnabled", "true");
        cache = new FlotsamAssetCache();
        SceneHelpers.SetupSceneModules(new SceneHelpers().SetupScene(), config, cache);

        IAssetService local = StubProxy.Create<IAssetService>((method, args) => method.Name switch
        {
            "Get" when args.Length == 1 => (true, store.TryGetValue((string)args[0], out AssetBase a) ? a : null),
            "Store" => (true, Put(store, (AssetBase)args[0])),
            "Delete" => (true, store.Remove((string)args[0])),
            _ => (false, null),
        });

        RegionAssetConnector connector = new();
        Set(connector, "m_localConnector", local);
        Set(connector, "m_Cache", cache);
        return connector;
    }

    private static string Put(Dictionary<string, AssetBase> store, AssetBase asset)
    {
        store[asset.ID] = asset;
        return asset.ID;
    }

    private static void Set(RegionAssetConnector connector, string field, object value)
    {
        (typeof(RegionAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(RegionAssetConnector), field)).SetValue(connector, value);
    }
}
