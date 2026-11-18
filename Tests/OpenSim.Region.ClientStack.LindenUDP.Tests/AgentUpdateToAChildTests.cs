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

using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A viewer sends agent updates only to the region it believes it is in. One that reaches a region holding the
/// avatar as a child agent is ignored for movement, as before, and now also tells the presence
/// (OnAgentUpdateWhileChild): after a crossing out of that region, the viewer did not take the crossing in.
/// </summary>
public class AgentUpdateToAChildTests : OpenSimTestCase
{
    private static readonly MethodInfo HandleAgentUpdate =
        typeof(LLClientView).GetMethod("HandleAgentUpdate", BindingFlags.NonPublic | BindingFlags.Static)!;

    public AgentUpdateToAChildTests()
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
    public void AnAgentUpdateThatReachesAChildAgentIsReportedOnTheChild()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(scene);
        ScenePresence child = ClientStackHelpers.AddChildClient(scene, udp, UUID.Random(), UUID.Random(), 734631);
        Assert.True(child.IsChildAgent);
        LLClientView client = (LLClientView)child.ControllingClient;

        int reported = 0;
        int moved = 0;
        child.OnAgentUpdateWhileChild += _ => reported++;
        client.OnAgentUpdate += (_, _) => moved++;

        AgentUpdatePacket update = new();
        update.AgentData.AgentID = child.UUID;
        update.AgentData.SessionID = client.SessionId;
        update.Header.Sequence = 7;
        HandleAgentUpdate.Invoke(null, new object[] { client, update });

        Assert.Equal(1, reported);
        Assert.Equal(0, moved);

        scene.CloseAgent(child.UUID, true);
    }
}
