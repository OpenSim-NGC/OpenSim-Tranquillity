/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;
using Nini.Config;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Framework.InventoryAccess.Tests;

/// <summary>
/// With OutboundPermission false a Hypergrid visitor's new or changed items still go to their home
/// inventory, but the assets stay here. The visitor is told, once for each item.
/// </summary>
public class HGInventoryAccessModuleTests : OpenSimTestCase
{
    // the visitor's grid; nothing listens there, so a post home, if one were tried, fails at once
    private const string VisitorHome = "http://127.0.0.1:1";
    private const string VisitorAssetServer = "http://127.0.0.1:1";

    private TestScene m_scene = null!;
    private HGInventoryAccessModule m_module = null!;
    private readonly List<string> m_alerts = new();
    private readonly UUID m_visitorId = TestHelpers.ParseTail(0x100);

    private void SetUpScene(bool? outboundPermission)
    {
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Modules");
        config.Configs["Modules"].Set("InventoryAccessModule", "HGInventoryAccessModule");
        config.AddConfig("Hypergrid");
        config.Configs["Hypergrid"].Set("GatekeeperURI", "http://127.0.0.1:9000");
        config.Configs["Hypergrid"].Set("HomeURI", "http://127.0.0.1:9000");
        config.AddConfig("HGInventoryAccessModule");
        if (outboundPermission.HasValue)
            config.Configs["HGInventoryAccessModule"].Set("OutboundPermission", outboundPermission.Value.ToString());

        m_scene = new SceneHelpers().SetupScene("HG region", TestHelpers.ParseTail(0x1000), 1000, 1000, config);
        m_module = new HGInventoryAccessModule();
        SceneHelpers.SetupSceneModules(m_scene, config, new UserManagementModule(), m_module);
    }

    /// <summary>A visitor from another grid: no account here, and an asset server at home.</summary>
    private ScenePresence AddVisitor()
    {
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(m_visitorId);
        acd.ServiceURLs["HomeURI"] = VisitorHome;
        acd.ServiceURLs["AssetServerURI"] = VisitorAssetServer;
        ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, acd);
        ((TestClient)sp.ControllingClient).OnReceivedAlertMessage += m_alerts.Add;
        return sp;
    }

    private InventoryItemBase NewItem(UUID owner, int tail, string name, AssetType type)
    {
        return new InventoryItemBase
        {
            ID = TestHelpers.ParseTail(tail),
            Owner = owner,
            Folder = TestHelpers.ParseTail(0x9000),
            Name = name,
            AssetType = (int)type,
            AssetID = TestHelpers.ParseTail(tail + 1),
        };
    }

    private void Added(InventoryItemBase item) => m_scene.EventManager.TriggerOnNewInventoryItemUploadComplete(item, 0);

    private static string Expected(string name) =>
        "This grid does not send content to other grids. \"" + name +
        "\" is in your inventory, but it will only work on this grid.";

    [Fact]
    public void VisitorIsToldOncePerItemWhenOutboundPermissionIsAtItsDefault()
    {
        TestHelpers.InMethod();
        SetUpScene(null);
        AddVisitor();

        InventoryItemBase box = NewItem(m_visitorId, 0x200, "Box", AssetType.Object);
        Added(box);
        Assert.Equal(new[] { Expected("Box") }, m_alerts);

        // the same item changed again (a notecard or script saved): not told twice
        AssetBase newer = AssetHelpers.CreateNotecardAsset(TestHelpers.ParseTail(0x210), "second save");
        box.AssetID = newer.FullID;
        Assert.True(m_module.UpdateInventoryItemAsset(m_visitorId, box, newer));
        Assert.Single(m_alerts);

        // another item: told about that one
        Added(NewItem(m_visitorId, 0x300, "Note", AssetType.Notecard));
        Assert.Equal(new[] { Expected("Box"), Expected("Note") }, m_alerts);
    }

    [Fact]
    public void LandmarksGoHomeSoTheVisitorIsNotTold()
    {
        TestHelpers.InMethod();
        SetUpScene(false);
        AddVisitor();

        Added(NewItem(m_visitorId, 0x400, "Here", AssetType.Landmark));
        Assert.Empty(m_alerts);
    }

    [Fact]
    public void NothingIsSaidWhenOutboundPermissionIsTrue()
    {
        TestHelpers.InMethod();
        SetUpScene(true);
        AddVisitor();

        Added(NewItem(m_visitorId, 0x500, "Box", AssetType.Object));
        Assert.Empty(m_alerts);
    }

    [Fact]
    public void LocalUsersAreNotTold()
    {
        TestHelpers.InMethod();
        SetUpScene(false);
        UUID localId = TestHelpers.ParseTail(0x600);
        UserAccountHelpers.CreateUserWithInventory(m_scene, "Local", "User", localId, "password");
        ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, localId);
        ((TestClient)sp.ControllingClient).OnReceivedAlertMessage += m_alerts.Add;

        Added(NewItem(localId, 0x700, "Box", AssetType.Object));
        Assert.Empty(m_alerts);
    }
}
