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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Two regions that disagree about where an avatar is: both hold it as a root agent for the same session. A teleport
/// into the region that still has the stale root used to treat it as a zombie from a crashed session and close it as a
/// root, which logged the user off the grid, closed the avatar's child agents elsewhere, and then refused the
/// teleport because the user was offline. The stale root is now made a child agent, as a region the avatar leaves
/// does, and the arrival uses it. A new login still replaces a zombie root as before.
/// Region coordinates are ones no other test class uses: the grid data is shared between classes.
/// </summary>
public class AgentLocationAgreementTests : OpenSimTestCase
{
    public AgentLocationAgreementTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private sealed class Rig
    {
        public SceneHelpers Helpers;
        public TestScene A, B;
        public AgentCircuitData Circuit;
        public TestClient ClientA, ClientB;
        public ScenePresence InA, InB;
    }

    /// <summary>A and B far apart, the same session root in both.</summary>
    private static Rig SplitBrain(uint x)
    {
        Rig rig = new() { Helpers = new SceneHelpers() };
        EntityTransferModule etmA = new(), etmB = new();
        LocalSimulationConnectorModule lscm = new();

        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        rig.A = rig.Helpers.SetupScene("agreeA", UUID.Random(), x, 7300);
        rig.B = rig.Helpers.SetupScene("agreeB", UUID.Random(), x + 10, 7300);
        SceneHelpers.SetupSceneModules(rig.A, config, etmA);
        SceneHelpers.SetupSceneModules(rig.B, config, etmB);
        SceneHelpers.SetupSceneModules(new Scene[] { rig.A, rig.B }, config, lscm);

        rig.Circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        rig.ClientA = new TestClient(rig.Circuit, rig.A);
        rig.InA = SceneHelpers.AddScenePresence(rig.A, rig.ClientA, rig.Circuit);
        rig.InA.AbsolutePosition = new Vector3(30, 31, 32);

        AgentCircuitData inB = SceneHelpers.GenerateAgentData(rig.Circuit.AgentID);
        inB.SessionID = rig.Circuit.SessionID;
        inB.SecureSessionID = rig.Circuit.SecureSessionID;
        rig.ClientB = new TestClient(inB, rig.B);
        rig.InB = SceneHelpers.AddScenePresence(rig.B, rig.ClientB, inB);
        rig.InB.AbsolutePosition = new Vector3(40, 41, 42);

        rig.A.Update(4);
        rig.B.Update(4);
        Assert.False(rig.InA.IsChildAgent);
        Assert.False(rig.InB.IsChildAgent);
        return rig;
    }

    [Fact]
    public void ATeleportIntoTheRegionWithAStaleRootTakesTheAvatarThereAndKeepsTheUserOnline()
    {
        TestHelpers.InMethod();
        Rig rig = SplitBrain(7500);

        // the viewer's connection to A finishes the arrival
        rig.ClientB.OnTestClientSendRegionTeleport += (_, _, _, _, _, _) =>
            ThreadPool.UnsafeQueueUserWorkItem(_ => rig.ClientA.CompleteMovement(), null);

        rig.B.RequestTeleportLocation(rig.ClientB, rig.A.RegionInfo.RegionHandle, new Vector3(10, 11, 12),
            Vector3.UnitX, (uint)TeleportFlags.ViaLocation);

        Assert.Empty(rig.ClientB.TeleportFailedReasons);

        ScenePresence inA = rig.A.GetScenePresence(rig.Circuit.AgentID);
        Assert.NotNull(inA);
        Assert.False(inA.IsChildAgent);
        Assert.Equal(10f, inA.AbsolutePosition.X);
        Assert.Equal(11f, inA.AbsolutePosition.Y);

        ScenePresence inB = rig.B.GetScenePresence(rig.Circuit.AgentID);
        Assert.True(inB is null || inB.IsChildAgent);

        // still logged in: the session is known to the presence service
        Assert.NotNull(rig.A.PresenceService.GetAgent(rig.Circuit.SessionID));
    }

    [Fact]
    public void ANewLoginStillReplacesAZombieRoot()
    {
        TestHelpers.InMethod();
        Rig rig = SplitBrain(7520);

        // a new session for the same user logs in to A
        AgentCircuitData relog = SceneHelpers.GenerateAgentData(rig.Circuit.AgentID);
        Assert.NotEqual(rig.Circuit.SessionID, relog.SessionID);
        TestClient relogClient = new(relog, rig.A);
        ScenePresence fresh = SceneHelpers.AddScenePresence(rig.A, relogClient, relog);

        Assert.True(rig.InA.IsDeleted);
        Assert.NotSame(rig.InA, fresh);
        Assert.False(fresh.IsChildAgent);
    }
}
