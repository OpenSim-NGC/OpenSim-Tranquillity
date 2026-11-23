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
/// An asset fetched from another grid's asset service is cached and stored under its own id, so the region
/// takes it only if it is the asset it asked for.
/// </summary>
public class ForeignAssetIdTests : OpenSimTestCase
{
    private const string ForeignUrl = "http://other.example:8002";

    private sealed class Grid
    {
        public readonly Dictionary<string, AssetBase> Local = new();
        public readonly Dictionary<string, AssetBase> Foreign = new();
        public RegionAssetConnector Connector;
        public FlotsamAssetCache Cache;
    }

    [Fact]
    public void TheAssetAskedForIsTakenAndStored()
    {
        Grid g = NewGrid();
        AssetBase asked = Notecard(UUID.Random());
        g.Foreign[asked.ID] = asked;

        AssetBase got = g.Connector.Get(asked.ID, ForeignUrl, true);

        Assert.NotNull(got);
        Assert.Equal(asked.FullID, got.FullID);
        Assert.True(g.Local.ContainsKey(asked.ID));
    }

    [Fact]
    public void AnAssetWithAnotherIdIsRefused()
    {
        Grid g = NewGrid();
        AssetBase local = Notecard(UUID.Random());
        g.Local[local.ID] = local;
        g.Cache.Cache(local);
        UUID asked = UUID.Random();
        // the other grid answers the request for a new id with an asset carrying an existing local id
        g.Foreign[asked.ToString()] = Notecard(local.FullID, data: new byte[] { 9, 9, 9 });

        AssetBase got = g.Connector.Get(asked.ToString(), ForeignUrl, true);

        Assert.Null(got);
        Assert.False(g.Local.ContainsKey(asked.ToString()));
        Assert.Equal(local.Data, g.Local[local.ID].Data);
        Assert.Equal(local.Data, g.Cache.Get(local.ID).Data);
    }

    [Fact]
    public void AnAssetWhoseIdIsNotAUuidIsRefused()
    {
        Grid g = NewGrid();
        UUID asked = UUID.Random();
        AssetBase answer = Notecard(asked);
        answer.ID = ForeignUrl + "/" + asked;
        g.Foreign[asked.ToString()] = answer;

        Assert.Null(g.Connector.Get(asked.ToString(), ForeignUrl, true));
        Assert.Empty(g.Local);
    }

    private static AssetBase Notecard(UUID id, byte[] data = null) =>
        new(id, "note", (sbyte)AssetType.Notecard, UUID.Zero.ToString()) { Data = data ?? new byte[] { 1, 2, 3 } };

    private static Grid NewGrid()
    {
        Grid g = new();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules").Set("AssetCaching", "FlotsamAssetCache");
        IConfig cacheConfig = config.AddConfig("AssetCache");
        cacheConfig.Set("FileCacheEnabled", "false");
        cacheConfig.Set("MemoryCacheEnabled", "true");
        g.Cache = new FlotsamAssetCache();
        SceneHelpers.SetupSceneModules(new SceneHelpers().SetupScene(), config, g.Cache);

        IAssetService local = StubProxy.Create<IAssetService>((method, args) => method.Name switch
        {
            "Get" when args.Length == 1 => (true, g.Local.TryGetValue((string)args[0], out AssetBase a) ? a : null),
            "Store" => (true, Put(g.Local, (AssetBase)args[0])),
            _ => (false, null),
        });
        IAssetService foreign = StubProxy.Create<IAssetService>((method, args) => method.Name switch
        {
            "Get" when args.Length == 3 => (true, g.Foreign.TryGetValue((string)args[0], out AssetBase f) ? f : null),
            _ => (false, null),
        });

        g.Connector = new RegionAssetConnector();
        Set(g.Connector, "m_localConnector", local);
        Set(g.Connector, "m_HGConnector", foreign);
        Set(g.Connector, "m_Cache", g.Cache);
        return g;
    }

    private static string Put(Dictionary<string, AssetBase> store, AssetBase asset)
    {
        store.TryAdd(asset.ID, asset);
        return asset.ID;
    }

    private static void Set(RegionAssetConnector connector, string field, object value) =>
        (typeof(RegionAssetConnector).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(nameof(RegionAssetConnector), field)).SetValue(connector, value);
}
