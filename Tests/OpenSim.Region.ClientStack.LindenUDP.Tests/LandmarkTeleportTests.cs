/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// TeleportLandmarkRequest names a landmark asset. If the asset is not a landmark the viewer is told the
/// teleport failed; otherwise it waits on a teleport that never comes.
/// </summary>
public class LandmarkTeleportTests : OpenSimTestCase
{
    public LandmarkTeleportTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        StatsManager.SimExtraStats = new SimExtraStatsCollector();
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    [Fact]
    public void AGoodLandmarkStillTeleports()
    {
        UUID region = UUID.Random();
        AssetLandmark lm = Request($"Landmark version 2\nregion_id {region}\nlocal_pos 10 20 30\nregion_handle 1099511628032000\n", out bool failed);

        Assert.False(failed);
        Assert.NotNull(lm);
        Assert.Equal(region, lm.RegionID);
        Assert.Equal(new Vector3(10, 20, 30), lm.Position);
    }

    [Theory]
    // No region_handle line: the viewer itself reads only region_id and local_pos.
    [InlineData("Landmark version 2\nregion_id {0}\nlocal_pos 10 20 30\n")]
    // A region_handle line cut short.
    [InlineData("Landmark version 2\nregion_id {0}\nlocal_pos 10 20 30\nregion_handle\n")]
    [InlineData("Landmark version 2\r\nregion_id {0}\r\nlocal_pos 10 20 30\r\nregion_handle 1099511628032000\r\n")]
    public void AShortLandmarkStillTeleports(string format)
    {
        UUID region = UUID.Random();
        AssetLandmark lm = Request(string.Format(format, region), out bool failed);

        Assert.False(failed);
        Assert.NotNull(lm);
        Assert.Equal(region, lm.RegionID);
        Assert.Equal(new Vector3(10, 20, 30), lm.Position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a landmark")]
    [InlineData("Landmark version 2\nregion_id 1234\n")]
    public void ALandmarkThatCannotBeReadFailsTheTeleport(string data)
    {
        AssetLandmark lm = Request(data, out bool failed);

        Assert.Null(lm);
        Assert.True(failed);
    }

    /// <summary>Sends a TeleportLandmarkRequest for a landmark asset holding <paramref name="data"/>.</summary>
    private static AssetLandmark Request(string data, out bool failed)
    {
        Scene scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(scene);
        ScenePresence sp = ClientStackHelpers.AddChildClient(scene, udp, UUID.Random(), UUID.Random(), 734612);
        LLClientView client = (LLClientView)sp.ControllingClient;

        UUID landmark = UUID.Random();
        scene.AssetService.Store(new AssetBase(landmark, "lm", (sbyte)AssetType.Landmark, UUID.Zero.ToString())
        {
            Data = Encoding.UTF8.GetBytes(data),
        });
        AssetLandmark requested = null;
        client.OnTeleportLandmarkRequest += (c, lm) => requested = lm;

        TeleportLandmarkRequestPacket request = new();
        request.Info.AgentID = sp.UUID;
        request.Info.LandmarkID = landmark;
        typeof(LLClientView).GetMethod("HandleTeleportLandmarkRequest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { client, request });

        failed = udp.PacketsSent.Exists(p => p is TeleportFailedPacket);
        scene.CloseAgent(sp.UUID, true);
        return requested;
    }
}
