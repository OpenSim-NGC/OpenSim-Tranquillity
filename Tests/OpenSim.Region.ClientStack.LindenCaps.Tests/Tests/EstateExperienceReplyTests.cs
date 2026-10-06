/*
 * Copyright (c) Legion Builds
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

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Packets;

using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenUDP;
using OpenSim.Region.CoreModules.World.Estate;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests
{
    /// <summary>
    /// The region's "setexperience" reply to the viewer's estateexperiencedelta request carries the estate's
    /// blocked, trusted and allowed Experience lists in the layout LL's viewer reads
    /// (llfloaterregioninfo.cpp, LLDispatchSetEstateExperience): [0] estate id, [1] send_to_agent_only,
    /// [2] num blocked, [3] num trusted, [4] num allowed, then the blocked, trusted and allowed ids.
    /// </summary>
    // No test reaches a network service: the UDP server is never started and records what it is given to send.
    // Test grouping: each test builds its own scene, server and client and touches no process-wide state (the class
    // does not derive from OpenSimTestCase, which clears MainServer), so it runs in parallel.
    public class EstateExperienceReplyTests
    {
        private const uint EstateId = 101;

        private static UUID[] Ids(int count)
        {
            UUID[] ids = new UUID[count];
            for (int i = 0; i < count; i++)
                ids[i] = UUID.Random();
            return ids;
        }

        /// <summary>Send the reply through a real LLClientView and return the packet it gives the UDP server.</summary>
        private static EstateOwnerMessagePacket SendThroughClientView(UUID invoice, UUID[] allowed, UUID[] key, UUID[] blocked)
        {
            TestScene scene = new SceneHelpers().SetupScene();
            TestLLUDPServer udpServer = new TestLLUDPServer(
                IPAddress.Loopback, 0, 0, new IniConfigSource(), scene.AuthenticateHandler);

            UUID agentId = UUID.Random();
            uint circuitCode = 123456;
            LLUDPClient udpClient = new LLUDPClient(
                udpServer, udpServer.ThrottleRates, udpServer.Throttle, circuitCode, agentId,
                new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
            AuthenticateResponse session = new AuthenticateResponse { Authorised = true, LoginInfo = new Login() };

            LLClientView client = new LLClientView(scene, udpServer, udpClient, session, agentId, UUID.Random(), circuitCode);

            client.SendEstateExperiences(invoice, allowed, key, blocked, EstateId);

            // The packet LLClientView hands to the UDP server to send.
            return Assert.Single(udpServer.PacketsSent.OfType<EstateOwnerMessagePacket>());
        }

        private static string Text(EstateOwnerMessagePacket p, int index) => Utils.BytesToString(p.ParamList[index].Parameter);

        /// <summary>Read the reply as LL's viewer does: counts at [2], [3], [4]; ids from [5] in that order.</summary>
        private static (UUID[] blocked, UUID[] trusted, UUID[] allowed) ReadAsViewer(EstateOwnerMessagePacket p)
        {
            int numBlocked = int.Parse(Text(p, 2));
            int numTrusted = int.Parse(Text(p, 3));
            int numAllowed = int.Parse(Text(p, 4));
            UUID[] ids = p.ParamList.Skip(5).Select(b => new UUID(b.Parameter, 0)).ToArray();
            return (ids.Take(numBlocked).ToArray(),
                    ids.Skip(numBlocked).Take(numTrusted).ToArray(),
                    ids.Skip(numBlocked + numTrusted).Take(numAllowed).ToArray());
        }

        [Fact]
        public void AnEstateWithBlockedExperiencesHasThemCountedAndNamedInTheReply()
        {
            UUID invoice = UUID.Random();
            UUID[] allowed = Ids(3), key = Ids(1), blocked = Ids(2);

            EstateOwnerMessagePacket p = SendThroughClientView(invoice, allowed, key, blocked);

            Assert.Equal("setexperience", Utils.BytesToString(p.MethodData.Method));
            Assert.Equal(invoice, p.MethodData.Invoice);
            Assert.Equal(5 + 2 + 1 + 3, p.ParamList.Length);
            Assert.Equal("2", Text(p, 2));
            Assert.Equal(blocked, ReadAsViewer(p).blocked);
        }

        [Fact]
        public void TheTrustedAndAllowedListsAreSentAsBefore()
        {
            UUID[] allowed = Ids(3), key = Ids(2), blocked = Ids(4);

            EstateOwnerMessagePacket p = SendThroughClientView(UUID.Random(), allowed, key, blocked);

            Assert.Equal(EstateId.ToString(), Text(p, 0));
            Assert.Equal("0", Text(p, 1));
            Assert.Equal("2", Text(p, 3));
            Assert.Equal("3", Text(p, 4));
            (UUID[] _, UUID[] trusted, UUID[] allowedRead) = ReadAsViewer(p);
            Assert.Equal(key, trusted);
            Assert.Equal(allowed, allowedRead);

            // Without the blocked ids, the reply is the one sent before: trusted ids, then allowed ids.
            UUID[] after = p.ParamList.Skip(5 + blocked.Length).Select(b => new UUID(b.Parameter, 0)).ToArray();
            Assert.Equal(key.Concat(allowed).ToArray(), after);
        }

        [Fact]
        public void AnEstateWithNoBlockedExperiencesRepliesAsBefore()
        {
            UUID[] allowed = Ids(2), key = Ids(1);

            EstateOwnerMessagePacket p = SendThroughClientView(UUID.Random(), allowed, key, Array.Empty<UUID>());

            Assert.Equal(5 + 1 + 2, p.ParamList.Length);
            Assert.Equal(new[] { EstateId.ToString(), "0", "0", "1", "2" }, Enumerable.Range(0, 5).Select(i => Text(p, i)));
            UUID[] ids = p.ParamList.Skip(5).Select(b => new UUID(b.Parameter, 0)).ToArray();
            Assert.Equal(key.Concat(allowed).ToArray(), ids);
        }

        /// <summary>
        /// Each list holds at most 8 Experiences (Constants.EstateAccessLimits; LL's ESTATE_MAX_EXPERIENCE_IDS = 8).
        /// The viewer replaces all three lists from each reply, so full lists go in one reply.
        /// </summary>
        [Fact]
        public void FullListsGoInOneReply()
        {
            UUID[] allowed = Ids(8), key = Ids(8), blocked = Ids(8);

            EstateOwnerMessagePacket p = SendThroughClientView(UUID.Random(), allowed, key, blocked);

            Assert.Equal(5 + 24, p.ParamList.Length);
            // LLUDPServer.SendPacket splits a packet only when packet.Length + 20 > MTU.
            Assert.True(p.Length + 20 <= LLUDPServer.MTU, $"reply of {p.Length} bytes would be split");
            (UUID[] b, UUID[] t, UUID[] a) = ReadAsViewer(p);
            Assert.Equal(blocked, b);
            Assert.Equal(key, t);
            Assert.Equal(allowed, a);
        }

        [Fact]
        public void TheEstateModuleRepliesWithTheEstatesThreeLists()
        {
            EstateManagementModule emm = new EstateManagementModule();
            TestScene scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(scene, emm);

            EstateSettings es = scene.RegionInfo.EstateSettings;
            es.EstateID = EstateId;
            UUID[] allowed = Ids(2), key = Ids(1), blocked = Ids(3);
            foreach (UUID id in allowed) es.AddAllowedExperience(id);
            foreach (UUID id in key) es.AddKeyExperience(id);
            foreach (UUID id in blocked) es.AddBlockedExperience(id);

            TestClient client = new TestClient(new AgentCircuitData { AgentID = UUID.Random() }, scene);
            UUID invoice = UUID.Random();

            emm.SendEstateExperienceLists(client, invoice);

            var reply = Assert.Single(client.EstateExperienceReplies);
            Assert.Equal(invoice, reply.Invoice);
            Assert.Equal(EstateId, reply.EstateID);
            Assert.Equal(allowed, reply.Allowed);
            Assert.Equal(key, reply.Key);
            Assert.Equal(blocked, reply.Blocked);
        }
    }
}
