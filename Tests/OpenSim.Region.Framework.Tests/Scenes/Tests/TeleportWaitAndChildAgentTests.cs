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
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Teleports that go wrong part of the way, found by driving three regions with a real viewer protocol client.
/// <list type="bullet">
/// <item>A viewer that has lost track of a crossing asks the region it left (where the avatar is now only a child
/// agent) to teleport it. That region must not: teleporting the child into the avatar's root region closed the
/// real avatar there and logged it off the grid.</item>
/// <item>A destination waiting for the viewer stops as soon as the agent it waits for is closed, or the viewer logs
/// out, instead of holding the departing region for the full wait (25 seconds for the arrival, 30 for the
/// connection).</item>
/// <item>A refused teleport whose traveller was closed meanwhile is a failed teleport, not an exception.</item>
/// </list>
/// The waits that are not expected to end are set far longer than the bounds asserted, so a wait that runs out
/// shows. Region coordinates are ones no other test class uses: the grid data is shared between classes.
/// </summary>
[Collection("process-logger")]
public class TeleportWaitAndChildAgentTests : OpenSimTestCase
{
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

    public TeleportWaitAndChildAgentTests()
    {
        // FireAndForget on the calling thread; the waits under test run on the teleport's own thread.
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;

        // a server (never started) for the regions' capabilities, so that closing an agent can remove its caps;
        // OpenSimTestCase removes it again before the next test
        MainServer.Instance.AddHttpServer(new BaseHttpServer(99997));
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
    }

    /// <summary>An entity transfer module whose CreateAgent can be made to do something first.</summary>
    private sealed class HookedEntityTransferModule : EntityTransferModule
    {
        public Func<ScenePresence, bool> BeforeCreateAgent;

        protected override bool CreateAgent(ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            AgentCircuitData agentCircuit, uint teleportFlags, EntityTransferContext ctx, out string reason, out bool logout)
        {
            if (BeforeCreateAgent is not null && !BeforeCreateAgent(sp))
            {
                reason = "the destination refused";
                logout = false;
                return false;
            }
            return base.CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, ctx, out reason, out logout);
        }
    }

    private sealed class Rig
    {
        public TestScene A, B, C;
        public HookedEntityTransferModule EtmA;
        public TestClient Client;
        public ScenePresence Presence;
        public UUID User;
        public readonly List<TestClient> Destinations = new();
    }

    /// <summary>A at (x, y) with a root avatar, B next to it, C far away; each with its own transfer module.</summary>
    private static Rig Build(uint x, uint y, bool neighbourChild)
    {
        Rig rig = new() { User = UUID.Random() };
        rig.EtmA = new HookedEntityTransferModule();
        LocalSimulationConnectorModule lscm = new();

        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", rig.EtmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        SceneHelpers sh = new();
        rig.A = sh.SetupScene("tpwA", UUID.Random(), x, y);
        rig.B = sh.SetupScene("tpwB", UUID.Random(), x + 1, y);
        rig.C = sh.SetupScene("tpwC", UUID.Random(), x + 10, y);

        SceneHelpers.SetupSceneModules(new Scene[] { rig.A, rig.B, rig.C }, config, lscm);
        SceneHelpers.SetupSceneModules(rig.A, config, new CapabilitiesModule(), rig.EtmA);
        SceneHelpers.SetupSceneModules(rig.B, config, new CapabilitiesModule(), new EntityTransferModule());
        // C, far away, needs no capabilities module: only neighbours share seeds
        SceneHelpers.SetupSceneModules(rig.C, config, new EntityTransferModule());

        AgentCircuitData acd = SceneHelpers.GenerateAgentData(rig.User);
        rig.Client = new TestClient(acd, rig.A);
        if (neighbourChild)
            EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(rig.Client, rig.Destinations);

        rig.Presence = SceneHelpers.AddScenePresence(rig.A, rig.Client, acd);
        rig.Presence.AbsolutePosition = new Vector3(30, 31, 32);
        rig.A.Update(4);
        rig.B.Update(4);
        return rig;
    }

    /// <summary>The viewer connects to the destination it is sent to, and finishes arriving only if asked to.</summary>
    private static void ViewerGoesWhereItIsSent(Rig rig, bool completes, Action<TestClient> connected = null)
    {
        rig.Client.OnTestClientSendRegionTeleport += (regionHandle, _, _, _, _, _) =>
        {
            Util.RegionHandleToRegionLoc(regionHandle, out uint rx, out uint ry);
            SceneManager.Instance.TryGetScene(rx, ry, out Scene destination);

            TestClient there = rig.Destinations.FirstOrDefault(d => d.Scene == destination);
            if (there is null)
            {
                there = new TestClient(rig.Client.RequestClientInfo(), destination);
                rig.Destinations.Add(there);
                destination.AddNewAgent(there, PresenceType.User);
            }
            connected?.Invoke(there);
            if (completes)
                ThreadPool.UnsafeQueueUserWorkItem(_ => there.CompleteMovement(), null);
        };
    }

    private static TimeSpan TimedTeleport(Rig rig, IClientAPI client, Scene from, Scene to, Vector3 position)
    {
        Stopwatch watch = Stopwatch.StartNew();
        from.RequestTeleportLocation(client, to.RegionInfo.RegionHandle, position, Vector3.UnitX,
            (uint)TeleportFlags.ViaLocation);
        return watch.Elapsed;
    }

    private static void Later(int ms, Action action)
        => ThreadPool.UnsafeQueueUserWorkItem(_ => { Thread.Sleep(ms); action(); }, null);

    /// <summary>Whether <paramref name="condition"/> holds within <see cref="Quick"/>.</summary>
    private static bool SoonTrue(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Quick)
                return false;
            Thread.Sleep(20);
        }
        return true;
    }

    // ---- a region that holds only a child agent does not teleport it ----------------------------------------------

    [Fact]
    public void ARegionWhereTheAvatarIsOnlyAChildDoesNotTeleportItAndLeavesTheAvatarInItsRootRegionAlone()
    {
        TestHelpers.InMethod();
        Rig rig = Build(7300, 7300, neighbourChild: true);
        ViewerGoesWhereItIsSent(rig, completes: true);

        // into B: A keeps a child agent, B has the avatar
        TimedTeleport(rig, rig.Client, rig.A, rig.B, new Vector3(10, 11, 12));
        ScenePresence inB = rig.B.GetScenePresence(rig.User);
        ScenePresence childInA = rig.A.GetScenePresence(rig.User);
        Assert.False(inB.IsChildAgent);
        Assert.True(childInA.IsChildAgent);
        Vector3 childPosition = childInA.AbsolutePosition;
        int failuresBefore = rig.Client.TeleportFailedReasons.Count;

        // the viewer, confused, asks A: into B itself, to a far region, and within A
        TimedTeleport(rig, rig.Client, rig.A, rig.B, new Vector3(100, 100, 30));
        TimedTeleport(rig, rig.Client, rig.A, rig.C, new Vector3(100, 100, 30));
        TimedTeleport(rig, rig.Client, rig.A, rig.A, new Vector3(60, 60, 30));

        // the avatar in B is the same presence, root and running: not closed, not logged off
        Assert.Same(inB, rig.B.GetScenePresence(rig.User));
        Assert.False(inB.IsChildAgent);
        Assert.False(inB.IsDeleted);
        Assert.Equal(new Vector3(10, 11, 12).X, inB.AbsolutePosition.X);
        Assert.Null(rig.C.GetScenePresence(rig.User));

        // A's child agent did not move, and the viewer was told each time
        Assert.True(childInA.IsChildAgent);
        Assert.Equal(childPosition, childInA.AbsolutePosition);
        Assert.Equal(failuresBefore + 3, rig.Client.TeleportFailedReasons.Count);
        Assert.All(rig.Client.TeleportFailedReasons.Skip(failuresBefore),
            reason => Assert.Contains("not in this region", reason));
    }

    // ---- the destination stops waiting for a viewer that will not come ----------------------------------------------

    [Fact]
    public void TheWaitForTheViewerToFinishArrivingEndsWhenTheAgentIsClosedThere()
    {
        TestHelpers.InMethod();
        Rig rig = Build(7320, 7300, neighbourChild: false);
        rig.C.RootAgentWait = LongWait;

        // the viewer connects to C but never finishes arriving; C closes the agent a moment later
        ViewerGoesWhereItIsSent(rig, completes: false, connected: _ => Later(300, () => rig.C.CloseAgent(rig.User, false)));

        TimeSpan took = TimedTeleport(rig, rig.Client, rig.A, rig.C, new Vector3(10, 11, 12));

        Assert.True(took < Quick, $"the teleport took {took}");
        Assert.False(rig.Presence.IsChildAgent);
        Assert.Same(rig.Presence, rig.A.GetScenePresence(rig.User));
        Assert.NotEmpty(rig.Client.TeleportFailedReasons);
    }

    [Fact]
    public void TheWaitForTheViewerToConnectEndsWhenTheAgentIsClosedThere()
    {
        TestHelpers.InMethod();
        Rig rig = Build(7340, 7300, neighbourChild: false);
        rig.C.ChildPresenceWait = LongWait;

        // the viewer never reaches C; C closes the agent it was told to expect a moment later
        rig.Client.OnTestClientSendRegionTeleport += (_, _, _, _, _, _) => Later(300, () => rig.C.CloseAgent(rig.User, false));

        TimeSpan took = TimedTeleport(rig, rig.Client, rig.A, rig.C, new Vector3(10, 11, 12));

        Assert.True(took < Quick, $"the teleport took {took}");
        Assert.False(rig.Presence.IsChildAgent);
        Assert.Null(rig.C.GetScenePresence(rig.User));
        Assert.NotEmpty(rig.Client.TeleportFailedReasons);
    }

    [Fact]
    public void AViewerThatLogsOutWhileTheDestinationWaitsForItIsClosedThereAtOnce()
    {
        TestHelpers.InMethod();
        Rig rig = Build(7360, 7300, neighbourChild: false);
        rig.C.RootAgentWait = LongWait;
        rig.C.ChildPresenceWait = LongWait;

        // the viewer connects to C, then quits before it finishes arriving
        ViewerGoesWhereItIsSent(rig, completes: false, connected: _ => Later(300, () => rig.Client.Logout()));

        TimeSpan took = TimedTeleport(rig, rig.Client, rig.A, rig.C, new Vector3(10, 11, 12));

        Assert.True(took < Quick, $"the teleport took {took}");

        // the teleport ends when C starts closing the agent; the close itself finishes on C's own thread
        Assert.True(
            SoonTrue(() => rig.C.GetScenePresence(rig.User) is null
                && rig.C.AuthenticateHandler.GetAgentCircuitData(rig.User) is null),
            "C still holds the agent");
    }

    [Fact]
    public void AViewerThatFinishesArrivingIsStillTaken()
    {
        TestHelpers.InMethod();
        Rig rig = Build(7380, 7300, neighbourChild: false);
        rig.C.RootAgentWait = LongWait;
        ViewerGoesWhereItIsSent(rig, completes: true);

        // (no time bound: after the arrival a far teleport waits for the destination's release call, which in a
        // test without HTTP never comes)
        TimedTeleport(rig, rig.Client, rig.A, rig.C, new Vector3(10, 11, 12));

        ScenePresence inC = rig.C.GetScenePresence(rig.User);
        Assert.NotNull(inC);
        Assert.False(inC.IsChildAgent);
        Assert.Empty(rig.Client.TeleportFailedReasons);
    }

    [Fact]
    public void TheWaitForArrivalDefaultsToTwentyFiveSecondsAndCanBeSet()
    {
        TestHelpers.InMethod();
        Assert.Equal(TimeSpan.FromSeconds(25), new SceneHelpers().SetupScene().RootAgentWait);

        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup").Set("RootAgentWaitSeconds", "9");
        Assert.Equal(TimeSpan.FromSeconds(9),
            new SceneHelpers().SetupScene("tpwConfig", UUID.Random(), 7400, 7300, config).RootAgentWait);
    }

    // ---- a refusal after the traveller was closed -------------------------------------------------------------------

    [Fact]
    public void ARefusedTeleportWhoseTravellerWasClosedMeanwhileIsAFailureNotAnError()
    {
        TestHelpers.InMethod();
        Rig rig = Build(7420, 7300, neighbourChild: false);

        // while the destination is asked, the traveller is closed (its viewer went, or the destination found the
        // avatar there already and closed it here); a close during a transfer has to be forced
        bool closed = false;
        rig.EtmA.BeforeCreateAgent = sp =>
        {
            rig.A.CloseAgent(sp.UUID, true);
            closed = sp.IsDeleted && sp.ControllingClient is null;
            return false;
        };

        using LogCapture log = new();
        TimedTeleport(rig, rig.Client, rig.A, rig.C, new Vector3(10, 11, 12));

        Assert.True(closed, "the traveller was not closed during the teleport");

        Assert.False(log.Contains("Exception on teleport"), string.Join("\n", log.Messages.Where(m => m.Contains("Exception"))));
        Assert.True(log.Contains("was refused because"));
    }
}
