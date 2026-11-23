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
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Tests.Common;
using Xunit;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A crossing whose viewer did not take in the CrossedRegion event (lost or late on the event queue): the region it
/// left has made the avatar a child agent and the neighbour waits for the viewer, which still believes it is in the
/// old region and keeps sending its agent updates there. Nothing recovered that: the avatar stood at the border with
/// no region holding it as root. The old region now sends the crossing again while that goes on, a few times. Seen
/// driving two regions with a viewer protocol client made to drop the first CrossedRegion; the resent one completed
/// the crossing.
/// Region coordinates are ones no other test class uses: the grid data is shared between classes.
/// </summary>
public class LostCrossingResendTests : OpenSimTestCase
{
    public LostCrossingResendTests()
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
        public TestScene A, B;
        public EntityTransferModule EtmA;
        public TestEventQueueGetModule EqA;
        public ScenePresence Avatar;
        public int Crossings => EqA.Events.TryGetValue(Avatar.UUID, out var events)
            ? events.Count(e => e.Name == "CrossRegion") : 0;
    }

    /// <summary>A with a root avatar near its east edge, B east of it with the avatar's child agent; crossed into B.</summary>
    private static Rig CrossedIntoB(uint x)
    {
        Rig rig = new() { EtmA = new EntityTransferModule(), EqA = new TestEventQueueGetModule() };
        LocalSimulationConnectorModule lscm = new();

        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", rig.EtmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        SceneHelpers sh = new();
        rig.A = sh.SetupScene("lostA", UUID.Random(), x, 7400);
        rig.B = sh.SetupScene("lostB", UUID.Random(), x + 1, 7400);
        SceneHelpers.SetupSceneModules(new Scene[] { rig.A, rig.B }, config, lscm);
        SceneHelpers.SetupSceneModules(rig.A, config, rig.EqA, new CapabilitiesModule(), rig.EtmA);   // the queue first: the transfer module looks it up when the region is loaded
        SceneHelpers.SetupSceneModules(rig.B, config, new CapabilitiesModule(), new EntityTransferModule());

        AgentCircuitData acd = SceneHelpers.GenerateAgentData(UUID.Random());
        rig.Avatar = SceneHelpers.AddScenePresence(rig.A, new TestClient(acd, rig.A), acd);
        rig.Avatar.AbsolutePosition = new Vector3(250, 128, 30);
        rig.A.Update(4);

        // A told B to expect the child agent (EnableSimulator went to the event queue); the viewer connects to B
        AgentCircuitData atB = rig.B.AuthenticateHandler.GetAgentCircuitData(acd.AgentID);
        Assert.NotNull(atB);
        rig.B.AddNewAgent(new TestClient(atB, rig.B), PresenceType.User);
        Assert.True(rig.B.GetScenePresence(acd.AgentID).IsChildAgent);

        rig.EqA.ClearEvents();
        rig.EtmA.CrossAgentToNewRegionAsync(rig.Avatar, new Vector3(1, 128, 30), new GridRegion(rig.B.RegionInfo), false,
            new EntityTransferContext());
        Assert.True(rig.Avatar.IsChildAgent);
        Assert.Equal(1, rig.Crossings);
        return rig;
    }

    /// <summary>The viewer sends agent updates to A, where the avatar is now a child agent; past the 1 s throttle.</summary>
    private static void ViewerStillSendsToA(Rig rig)
    {
        Thread.Sleep(1100);
        rig.Avatar.AgentUpdateWhileChild();
    }

    [Fact]
    public void AViewerThatKeepsSendingToTheRegionItCrossedOutOfIsSentTheCrossingAgain()
    {
        TestHelpers.InMethod();
        Rig rig = CrossedIntoB(7600);
        rig.EtmA.CrossedRegionResendAfterMs = 500;

        // straight after the crossing is too soon: a real viewer's updates still in flight
        rig.Avatar.AgentUpdateWhileChild();
        Assert.Equal(1, rig.Crossings);

        ViewerStillSendsToA(rig);
        Assert.Equal(2, rig.Crossings);
    }

    [Fact]
    public void TheCrossingIsSentAgainOnlyAFewTimes()
    {
        TestHelpers.InMethod();
        Rig rig = CrossedIntoB(7620);
        rig.EtmA.CrossedRegionResendAfterMs = 0;
        rig.EtmA.CrossedRegionMaxResends = 2;

        for (int i = 0; i < 4; i++)
            ViewerStillSendsToA(rig);

        Assert.Equal(1 + 2, rig.Crossings);
    }

    [Fact]
    public void AViewerThatTookTheCrossingIsNotSentItAgain()
    {
        TestHelpers.InMethod();
        Rig rig = CrossedIntoB(7640);
        rig.EtmA.CrossedRegionResendAfterMs = 0;

        Thread.Sleep(1100);

        Assert.Equal(1, rig.Crossings);
    }

    [Fact]
    public void AnAvatarThatCameBackToTheRegionIsNotSentTheCrossingAgain()
    {
        TestHelpers.InMethod();
        Rig rig = CrossedIntoB(7660);
        rig.EtmA.CrossedRegionResendAfterMs = 0;

        // the avatar crossed back into A and is a root agent here again: the old crossing is no longer lost
        rig.A.EventManager.TriggerOnMakeRootAgent(rig.Avatar);
        ViewerStillSendsToA(rig);

        Assert.Equal(1, rig.Crossings);
    }

    [Fact]
    public void AViewerArrivingBackInTheRegionIsNotSentTheCrossingAgainBeforeItIsRootThere()
    {
        TestHelpers.InMethod();
        Rig rig = CrossedIntoB(7680);
        rig.EtmA.CrossedRegionResendAfterMs = 0;

        // B crosses the avatar back: A receives its agent data first, and only then is the viewer told to come to A.
        // Its agent updates reach A while A still holds it as a child agent, before CompleteMovement makes it root.
        AgentData back = new();
        rig.Avatar.CopyTo(back, true);
        back.RegionID = rig.B.RegionInfo.RegionID;
        back.SessionID = rig.Avatar.ControllingClient.SessionId;
        Assert.True(rig.A.IncomingUpdateChildAgent(back));
        Assert.True(rig.Avatar.IsChildAgent);

        ViewerStillSendsToA(rig);

        Assert.Equal(1, rig.Crossings);
    }
}
