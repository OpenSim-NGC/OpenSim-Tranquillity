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

using System.Diagnostics;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// After a far teleport the departing region holds its old copy of the avatar until the destination calls back
/// (release, once the viewer has arrived there), then closes it. The wait looked for the call every 250 ms; the call
/// now wakes it, and without a call the wait ends at ReleaseWait (15 s by default, as before).
/// Region coordinates are ones no other test class uses: the grid data is shared between classes.
/// </summary>
public class TeleportReleaseWaitTests : OpenSimTestCase
{
    public TeleportReleaseWaitTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private static (TestScene a, TestScene c, EntityTransferModule etmA, TestClient client, ScenePresence sp) FarApart(uint x)
    {
        EntityTransferModule etmA = new(), etmC = new();
        LocalSimulationConnectorModule lscm = new();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        SceneHelpers sh = new();
        TestScene a = sh.SetupScene("relA", UUID.Random(), x, 7800);
        TestScene c = sh.SetupScene("relC", UUID.Random(), x + 10, 7800);
        SceneHelpers.SetupSceneModules(a, config, etmA);
        SceneHelpers.SetupSceneModules(c, config, etmC);
        SceneHelpers.SetupSceneModules(new Scene[] { a, c }, config, lscm);

        AgentCircuitData acd = SceneHelpers.GenerateAgentData(UUID.Random());
        TestClient client = new(acd, a);
        ScenePresence sp = SceneHelpers.AddScenePresence(a, client, acd);
        sp.AbsolutePosition = new Vector3(30, 31, 32);
        return (a, c, etmA, client, sp);
    }

    /// <summary>The viewer connects to C and arrives; when <paramref name="release"/>, C calls A back as it would over HTTP.</summary>
    private static void ViewerArrivesInC(TestClient client, TestScene c, EntityTransferModule etmA, UUID agent, bool release)
    {
        client.OnTestClientSendRegionTeleport += (_, _, _, _, _, _) =>
        {
            TestClient there = new(client.RequestClientInfo(), c);
            c.AddNewAgent(there, PresenceType.User);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                there.CompleteMovement();
                if (release)
                    etmA.AgentArrivedAtDestination(agent);
            }, null);
        };
    }

    [Fact]
    public void TheReleaseCallEndsTheWaitAndTheOldCopyIsClosed()
    {
        TestHelpers.InMethod();
        var (a, c, etmA, client, sp) = FarApart(7800);
        etmA.ReleaseWait = TimeSpan.FromSeconds(60);
        ViewerArrivesInC(client, c, etmA, sp.UUID, release: true);

        Stopwatch watch = Stopwatch.StartNew();
        a.RequestTeleportLocation(client, c.RegionInfo.RegionHandle, new Vector3(10, 11, 12), Vector3.UnitX,
            (uint)TeleportFlags.ViaLocation);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the teleport took {watch.Elapsed}");
        Assert.False(c.GetScenePresence(sp.UUID).IsChildAgent);
        Assert.Null(a.GetScenePresence(sp.UUID));
    }

    [Fact]
    public void WithoutAReleaseCallTheWaitEndsAtReleaseWaitAndTheOldCopyIsClosed()
    {
        TestHelpers.InMethod();
        var (a, c, etmA, client, sp) = FarApart(7820);
        etmA.ReleaseWait = TimeSpan.FromSeconds(1);
        ViewerArrivesInC(client, c, etmA, sp.UUID, release: false);

        Stopwatch watch = Stopwatch.StartNew();
        a.RequestTeleportLocation(client, c.RegionInfo.RegionHandle, new Vector3(10, 11, 12), Vector3.UnitX,
            (uint)TeleportFlags.ViaLocation);

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), $"the teleport took {watch.Elapsed}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the teleport took {watch.Elapsed}");
        Assert.Null(a.GetScenePresence(sp.UUID));
    }
}
