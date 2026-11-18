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
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// The end of a viewer's connection: the logout reply, and what the close runs before the avatar leaves the scene.
/// </summary>
public class ConnectionCloseTests : OpenSimTestCase
{
    public ConnectionCloseTests()
    {
        // everything on this thread: the circuit is made when the packet is handled
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private static (Scene Scene, TestLLUDPServer Udp, LLClientView Client) Connect(int tail)
    {
        Scene scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(scene);
        ScenePresence sp = ClientStackHelpers.AddChildClient(
            scene, udp, TestHelpers.ParseTail(tail), TestHelpers.ParseTail(tail + 0x100), 123456 + (uint)tail);
        LLClientView client = (LLClientView)sp.ControllingClient;
        client.IsActive = true;
        return (scene, udp, client);
    }

    // A connection closed handler that threw stopped the close before the client was taken out of the scene. The
    // client was already inactive, which the timeout does not look at again: the avatar stayed in the region until it
    // restarted, and the handlers after the one that threw (the presence and grid user logouts among them) never ran.
    [Fact]
    public void AHandlerThatThrowsStopsNeitherTheOthersNorTheRemoval()
    {
        TestHelpers.InMethod();
        (Scene scene, _, LLClientView client) = Connect(0x1);
        UUID agent = client.AgentId;
        bool laterRan = false;
        client.OnConnectionClosed += _ => throw new InvalidOperationException("a handler that fails");
        client.OnConnectionClosed += _ => laterRan = true;

        Exception thrown = Record.Exception(() => scene.CloseAgent(agent, false));

        Assert.Null(thrown);
        Assert.True(laterRan, "the handler after the one that threw did not run");
        Assert.Null(scene.GetScenePresence(agent));
        Assert.Null(scene.AuthenticateHandler.GetAgentCircuitData(agent));
        Assert.Equal(0, scene.GetNumberOfClients());
    }

    // The logout reply was queued on the Task throttle, behind the region's object updates, and the close that
    // follows a logout drops the client's queues: a viewer in a busy region got no reply and waited out its own logout
    // timeout. A packet of the Unknown category is not queued (it has no throttle) but sent at once.
    [Fact]
    public void TheLogoutReplyIsSentAtOnce()
    {
        TestHelpers.InMethod();
        (_, TestLLUDPServer udp, LLClientView client) = Connect(0x2);

        client.SendLogoutPacket();

        int i = Assert.Single(
            Enumerable.Range(0, udp.PacketsSent.Count), i => udp.PacketsSent[i].Type == PacketType.LogoutReply);
        Assert.Equal(ThrottleOutPacketType.Unknown, udp.CategoriesSent[i]);
        Assert.Equal(client.AgentId, ((LogoutReplyPacket)udp.PacketsSent[i]).AgentData.AgentID);
    }
}
