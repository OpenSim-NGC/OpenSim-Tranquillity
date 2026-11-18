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

using System.Net;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A viewer that opens a region again from another endpoint (another UDP port) while the region still has its child
/// agent. Seen driving a region with a viewer protocol client: two UseCircuitCode packets for one circuit arrived in
/// the same millisecond from two ports. Closing the old connection removed the circuit and the caps, so the new one
/// was refused (AddNewAgent then threw on the missing circuit) and the region was left with no presence while the
/// viewer believed itself connected: a teleport into the region later waited 30 seconds and failed.
/// </summary>
public class UseCircuitCodeFromAnotherEndpointTests : OpenSimTestCase
{
    private const uint Circuit = 734521;
    private static readonly IPEndPoint First = new(IPAddress.Loopback, 9201);
    private static readonly IPEndPoint Second = new(IPAddress.Loopback, 9202);

    private readonly Scene m_scene;
    private readonly CapabilitiesModule m_caps = new();
    private readonly AgentCircuitData m_circuit;

    public UseCircuitCodeFromAnotherEndpointTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        StatsManager.SimExtraStats = new SimExtraStatsCollector();

        // a server (never started) for the caps; OpenSimTestCase removes it again before the next test
        MainServer.Instance.AddHttpServer(new BaseHttpServer(99996));

        m_scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, m_caps);

        // the region was told of the child agent, as NewUserConnection does for a neighbour
        m_circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        m_circuit.child = true;
        m_circuit.CapsPath = UUID.Random().ToString();
        m_scene.AuthenticateHandler.AddNewCircuit(Circuit, m_circuit);
        m_caps.SetAgentCapsSeeds(m_circuit);
        m_caps.CreateCaps(m_circuit.AgentID, Circuit);
        m_caps.ActivateCaps(Circuit);
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private void UseCircuitCode(TestLLUDPServer udp, IPEndPoint from)
    {
        UseCircuitCodePacket uccp = new()
        {
            CircuitCode = new UseCircuitCodePacket.CircuitCodeBlock
            {
                Code = Circuit,
                ID = m_circuit.AgentID,
                SessionID = m_circuit.SessionID
            }
        };
        byte[] bytes = uccp.ToBytes();
        // full size: the server may take it into its buffer pool
        UDPPacketBuffer buffer = new(from) { DataLength = bytes.Length };
        Buffer.BlockCopy(bytes, 0, buffer.Data, 0, bytes.Length);
        udp.PacketReceived(buffer);
    }

    [Fact]
    public void TheChildAgentIsKeptOnTheNewConnection()
    {
        TestHelpers.InMethod();
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(m_scene);
        string seed = m_caps.GetCapsForUser(Circuit).CapsObjectPath;

        UseCircuitCode(udp, First);
        ScenePresence first = m_scene.GetScenePresence(m_circuit.AgentID);
        Assert.NotNull(first);
        Assert.True(first.IsChildAgent);

        UseCircuitCode(udp, Second);

        // a child agent again, on the second endpoint; the circuit and the seed the viewer has still work
        ScenePresence second = m_scene.GetScenePresence(m_circuit.AgentID);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(second.IsChildAgent);
        Assert.Equal(Second, second.ControllingClient.RemoteEndPoint);
        Assert.NotNull(m_scene.AuthenticateHandler.GetAgentCircuitData(Circuit));
        Assert.NotNull(m_caps.GetCapsForUser(Circuit));
        Assert.Equal(seed, m_caps.GetCapsForUser(Circuit).CapsObjectPath);
        Assert.NotNull(second.Appearance);
    }

    [Fact]
    public void TheSameEndpointAgainKeepsTheConnection()
    {
        TestHelpers.InMethod();
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(m_scene);

        UseCircuitCode(udp, First);
        ScenePresence first = m_scene.GetScenePresence(m_circuit.AgentID);

        // a resend from the same address and port, as a separate endpoint object (the receive cache of endpoints
        // expires, so the same port is not always the same object)
        UseCircuitCode(udp, new IPEndPoint(First.Address, First.Port));

        Assert.Same(first, m_scene.GetScenePresence(m_circuit.AgentID));
    }

    [Fact]
    public void AnAgentWhoseCircuitWasRemovedIsNotAddedAndNothingThrows()
    {
        TestHelpers.InMethod();
        TestLLUDPServer udp = ClientStackHelpers.AddUdpServer(m_scene);

        LLUDPClient udpClient = new(udp, new ThrottleRates(new Nini.Config.IniConfigSource()), udp.Throttle,
            Circuit, m_circuit.AgentID, First, 0, 0);
        AuthenticateResponse response = new()
        {
            LoginInfo = new Login { First = "Gone", Last = "Before", SecureSession = m_circuit.SecureSessionID }
        };
        LLClientView client = new(m_scene, udp, udpClient, response, m_circuit.AgentID, m_circuit.SessionID, Circuit);

        // the circuit goes between the UseCircuitCode check and the presence being added
        m_scene.AuthenticateHandler.RemoveCircuit(Circuit);

        Assert.Null(m_scene.AddNewAgent(client, PresenceType.User));
        Assert.Null(m_scene.GetScenePresence(m_circuit.AgentID));
        client.Close(false, false);
    }
}
