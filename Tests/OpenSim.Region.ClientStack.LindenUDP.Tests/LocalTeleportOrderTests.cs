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
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// The snap back after a local teleport while moving: the avatar's own terse updates go out in the Task queue, and
/// TeleportLocal skipped the throttles. A terse update built a moment before the teleport, still queued, reached the
/// viewer after the TeleportLocal and put the avatar back where it had been until the next update (seen in about one
/// moving local teleport in ten, driving a Jolt region with a viewer protocol client). TeleportLocal now goes in the
/// same queue, behind the updates already built, so the viewer gets them in the order they were made.
/// </summary>
public class LocalTeleportOrderTests : OpenSimTestCase
{
    public LocalTeleportOrderTests()
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
    public void TheLocalTeleportGoesInTheQueueOfTheAvatarsOwnUpdates()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(scene);
        ScenePresence sp = ClientStackHelpers.AddChildClient(scene, udp, UUID.Random(), UUID.Random(), 734611);
        LLClientView client = (LLClientView)sp.ControllingClient;

        client.SendLocalTeleport(new Vector3(60, 70, 30), Vector3.UnitX, (uint)TeleportFlags.ViaLocation);

        int i = udp.PacketsSent.FindLastIndex(p => p is TeleportLocalPacket);
        Assert.True(i >= 0, "no TeleportLocal was sent");
        Assert.Equal(new Vector3(60, 70, 30), ((TeleportLocalPacket)udp.PacketsSent[i]).Info.Position);
        Assert.Equal(ThrottleOutPacketType.Task, udp.CategoriesSent[i]);

        scene.CloseAgent(sp.UUID, true);
    }
}
