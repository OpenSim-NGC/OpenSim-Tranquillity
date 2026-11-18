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
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// The full compacting collection run when a client closes is for a simulator gone idle. It ran when the closing
/// client was the last in its region, which on a simulator with several regions stopped the whole process each time
/// one region emptied while the others had avatars.
/// </summary>
public class IdleCollectionTests : OpenSimTestCase
{
    private readonly Action m_collect = LLClientView.CollectWhenIdle;

    public IdleCollectionTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        StatsManager.SimExtraStats = new SimExtraStatsCollector();
    }

    public override void Dispose()
    {
        LLClientView.CollectWhenIdle = m_collect;
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    [Fact]
    public void ARegionThatEmptiesWhileAnotherHasAClientDoesNotStopTheProcessToCollect()
    {
        TestHelpers.InMethod();
        int collections = 0;
        LLClientView.CollectWhenIdle = () => collections++;

        SceneHelpers sh = new();
        Scene a = sh.SetupScene("idleA", UUID.Random(), 7700, 7700);
        Scene b = sh.SetupScene("idleB", UUID.Random(), 7710, 7700);
        TestLLUDPServer udpA = ClientStackHelpers.AddUdpServer(a);
        TestLLUDPServer udpB = ClientStackHelpers.AddUdpServer(b);
        ScenePresence inA = ClientStackHelpers.AddChildClient(a, udpA, UUID.Random(), UUID.Random(), 734641);
        ScenePresence inB = ClientStackHelpers.AddChildClient(b, udpB, UUID.Random(), UUID.Random(), 734642);

        // A empties; B still has its client
        a.CloseAgent(inA.UUID, true);
        Assert.Equal(0, a.GetNumberOfClients());
        Assert.Equal(0, collections);

        b.CloseAgent(inB.UUID, true);
    }
}
