/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// The region's check behind an AIS PATCH that names an asset in <c>asset_id</c>: the asset is the agent's own
/// upload, of the item's type.
/// </summary>
[TestFixture]
public class AisAssetUseTests
{
    private Scene m_scene;
    private AISv3Module.InventoryServiceBackend.AssetUseCheck m_check;
    private readonly UUID m_agent = UUID.Random();

    [SetUp]
    public void SetUp()
    {
        m_scene = new SceneHelpers().SetupScene();
        m_check = AISv3Module.AssetUseFor(m_scene);
    }

    private UUID Stored(AssetType type, string creator)
    {
        AssetBase asset = new(UUID.Random(), "a", (sbyte)type, creator) { Data = new byte[] { 1 } };
        m_scene.AssetService.Store(asset);
        return asset.FullID;
    }

    [Test]
    public void the_agents_own_upload_of_the_items_type_may_be_used()
    {
        UUID asset = Stored(AssetType.Clothing, m_agent.ToString());
        Assert.That(m_check(m_agent, asset, (sbyte)AssetType.Clothing), Is.True);
    }

    [Test]
    public void the_agents_own_upload_named_with_a_home_grid_may_be_used()
    {
        UUID asset = Stored(AssetType.Clothing, m_agent + ";http://home.example:8002/;First Last");
        Assert.That(m_check(m_agent, asset, (sbyte)AssetType.Clothing), Is.True);
    }

    [Test]
    public void someone_elses_asset_may_not_be_used()
    {
        UUID asset = Stored(AssetType.Clothing, UUID.Random().ToString());
        Assert.That(m_check(m_agent, asset, (sbyte)AssetType.Clothing), Is.False);
    }

    [Test]
    public void the_agents_own_asset_of_another_type_may_not_be_used()
    {
        UUID asset = Stored(AssetType.Object, m_agent.ToString());
        Assert.That(m_check(m_agent, asset, (sbyte)AssetType.Clothing), Is.False);
    }

    [Test]
    public void an_asset_that_does_not_exist_may_not_be_used()
    {
        Assert.That(m_check(m_agent, UUID.Random(), (sbyte)AssetType.Clothing), Is.False);
    }
}
