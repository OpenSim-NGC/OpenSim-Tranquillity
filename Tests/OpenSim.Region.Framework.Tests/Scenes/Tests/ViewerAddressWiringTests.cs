/*
 * Copyright (c) Legion Builds
 * Copyright (c) 2026 Legion Builds
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
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The endpoint a viewer is sent for a neighbouring or destination region at each place
/// EntityTransferModule hands one out. With no [ViewerAddress] settings every viewer must get the
/// region's own endpoint, as before; with a public address, a viewer outside the local networks gets that.
/// </summary>
public class ViewerAddressWiringTests : OpenSimTestCase
{
    private const string LanViewer = "198.51.100.20";
    private const string RemoteViewer = "192.0.2.50";
    private const string PublicAddress = "203.0.113.9";
    private const string LocalNetworks = "198.51.100.0/24";

    // The test regions all report 127.0.0.1:9000 as their own endpoint.
    private static readonly IPEndPoint RegionOwn = new(IPAddress.Loopback, 9000);
    private static readonly IPEndPoint RegionPublic = new(IPAddress.Parse(PublicAddress), 9000);

    public ViewerAddressWiringTests()
    {
        // Run asynchronous work on the calling thread so the sends have happened when the call returns.
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
    }

    private sealed class Rig
    {
        public TestScene SceneA, SceneB, SceneC;
        public ProbeEntityTransferModule EtmA;
        public TestClient Client;
        public ScenePresence Presence;
        public readonly List<IPEndPoint> Informs = new();
        public readonly List<IPEndPoint> Teleports = new();
        public readonly List<IPEndPoint> Crossings = new();
        public readonly List<TestClient> Destinations = new();
        public readonly object InformLock = new();

        /// <summary>The newest endpoint the viewer was told about; some sends run on a pool thread, so wait for one.</summary>
        public IPEndPoint LastInform()
        {
            for (int i = 0; i < 400; i++)
            {
                lock (InformLock)
                {
                    if (Informs.Count > 0)
                        return Informs[^1];
                }
                Thread.Sleep(25);
            }
            throw new TimeoutException("the viewer was not told about a neighbour");
        }

        public GridRegion RegionB => new(SceneB.RegionInfo);
        public GridRegion RegionC => new(SceneC.RegionInfo);
    }

    /// <summary>A list that keeps the given networks on the region's own address (a bare network) and gives every other viewer the address.</summary>
    private static string AddressList(string publicAddress, string localNetworks)
        => string.IsNullOrEmpty(publicAddress) ? localNetworks
            : (localNetworks.Length > 0 ? localNetworks + ", " : string.Empty) + "0.0.0.0/0=" + publicAddress;

    private static ViewerAddressSelector Selector(string publicAddress, string localNetworks)
        => ViewerAddressSelector.Create(ViewerAddressConfig.Create(AddressList(publicAddress, localNetworks), 0, null), null);

    /// <summary>
    /// A at 1000,1000 with a root viewer, B next to it, C out of draw distance. The selector is set after
    /// the modules are added, so the viewer is added with it in place.
    /// </summary>
    private static Rig Build(string viewerSource, ViewerAddressSelector selector, IConfigSource config = null,
        Action<RegionInfo> regionSettings = null)
    {
        Rig rig = new();
        UUID userId = TestHelpers.ParseTail(0x1);

        rig.EtmA = new ProbeEntityTransferModule();
        EntityTransferModule etmB = new();
        EntityTransferModule etmC = new();
        LocalSimulationConnectorModule lscm = new();

        config ??= new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", rig.EtmA.Name);
        modules.Set("SimulationServices", lscm.Name);

        SceneHelpers sh = new();
        rig.SceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), 1000, 1000);
        rig.SceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), 1001, 1000);
        rig.SceneC = sh.SetupScene("sceneC", TestHelpers.ParseTail(0x300), 1005, 1000);
        regionSettings?.Invoke(rig.SceneA.RegionInfo);

        SceneHelpers.SetupSceneModules(new Scene[] { rig.SceneA, rig.SceneB, rig.SceneC }, config, lscm);
        SceneHelpers.SetupSceneModules(rig.SceneA, config, new CapabilitiesModule(), rig.EtmA);
        SceneHelpers.SetupSceneModules(rig.SceneB, config, new CapabilitiesModule(), etmB);
        SceneHelpers.SetupSceneModules(rig.SceneC, config, new CapabilitiesModule(), etmC);

        if (selector is not null)
            rig.EtmA.ViewerAddresses = selector;

        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        acd.IPAddress = viewerSource;
        rig.Client = new TestClient(acd, rig.SceneA) { RemoteAddress = IPAddress.Parse(viewerSource) };
        rig.Client.OnTestClientInformClientOfNeighbour += (_, endPoint) =>
        {
            lock (rig.InformLock)
                rig.Informs.Add(endPoint);
        };
        rig.Client.OnTestClientSendRegionTeleport += (_, _, endPoint, _, _, _) =>
        {
            rig.Teleports.Add(endPoint);
            ThreadPool.UnsafeQueueUserWorkItem(_ => rig.Destinations[0].CompleteMovement(), null);
        };
        rig.Client.OnTestClientCrossRegion += (_, endPoint) => rig.Crossings.Add(endPoint);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(rig.Client, rig.Destinations);

        rig.Presence = SceneHelpers.AddScenePresence(rig.SceneA, rig.Client, acd);
        rig.Presence.AbsolutePosition = new Vector3(30, 31, 32);
        rig.SceneA.Update(4);
        rig.SceneB.Update(4);
        return rig;
    }

    private static IPEndPoint Expected(string viewerSource, bool configured)
        => configured && viewerSource == RemoteViewer ? RegionPublic : RegionOwn;

    public static IEnumerable<object[]> Cases()
    {
        foreach (string site in new[] { "login-neighbours", "enable-child-agent", "check-child-agents", "far-child", "teleport", "crossing" })
        {
            yield return new object[] { site, LanViewer, true };
            yield return new object[] { site, RemoteViewer, true };
            yield return new object[] { site, RemoteViewer, false };
        }
    }

    /// <summary>Makes B a neighbour the viewer has not been told about yet.</summary>
    private static void ForgetNeighbourB(Rig rig)
    {
        rig.SceneB.CloseAgent(rig.Presence.UUID, false);
        Dictionary<ulong, string> known = rig.Presence.KnownRegions;
        known.Remove(rig.SceneB.RegionInfo.RegionHandle);
        rig.Presence.KnownRegions = known;
        rig.Informs.Clear();
    }

    /// <summary>Drives one place that sends a viewer a region endpoint and returns what the viewer was sent.</summary>
    private static IPEndPoint Drive(Rig rig, string site)
    {
        EntityTransferContext ctx = new();
        switch (site)
        {
            case "login-neighbours": // EnableChildAgents, run when the viewer was added
                return rig.LastInform();

            case "enable-child-agent":
                ForgetNeighbourB(rig);
                rig.EtmA.EnableChildAgent(rig.Presence, rig.RegionB);
                return rig.LastInform();

            case "check-child-agents":
                ForgetNeighbourB(rig);
                rig.EtmA.CheckChildAgents(rig.Presence);
                return rig.LastInform();

            case "far-child":
                rig.Informs.Clear();
                Assert.True(rig.EtmA.CrossAgentCreateFarChild(rig.Presence, rig.RegionC, new Vector3(30, 31, 32), ctx));
                return rig.LastInform();

            case "teleport":
                rig.SceneA.RequestTeleportLocation(rig.Client, rig.SceneB.RegionInfo.RegionHandle,
                    new Vector3(10, 11, 12), new Vector3(20, 21, 22), (uint)TeleportFlags.ViaLocation);
                return rig.Teleports.Last();

            case "crossing":
                rig.EtmA.CrossAgentToNewRegionAsync(rig.Presence, new Vector3(30, 255, 32), rig.RegionB, false, ctx);
                return rig.Crossings.Last();

            default:
                throw new ArgumentException(site);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ViewerIsSentTheEndpointForItsSource(string site, string viewerSource, bool configured)
    {
        TestHelpers.InMethod();
        Rig rig = Build(viewerSource, configured ? Selector(PublicAddress, LocalNetworks) : null);

        IPEndPoint sent = Drive(rig, site);

        Assert.Equal(Expected(viewerSource, configured), sent);
        // The region's own endpoint is what grid data and the map tile check see: it never changes.
        Assert.Equal(RegionOwn, rig.SceneB.RegionInfo.ExternalEndPoint);
        Assert.Equal(RegionOwn, rig.RegionB.ExternalEndPoint);
    }

    [Fact]
    public void SettingsInOpenSimIniEnableTheSelector()
    {
        TestHelpers.InMethod();
        IConfigSource config = new IniConfigSource();
        IConfig section = config.AddConfig("ViewerAddress");
        section.Set("Addresses", AddressList(PublicAddress, LocalNetworks));

        Rig rig = Build(RemoteViewer, null, config);

        Assert.True(rig.EtmA.ViewerAddresses.Enabled);
        Assert.Equal(RegionPublic, rig.LastInform());
    }

    [Fact]
    public void RegionSettingsReplaceTheSectionsAddresses()
    {
        TestHelpers.InMethod();
        static IConfigSource Section()
        {
            IConfigSource config = new IniConfigSource();
            IConfig section = config.AddConfig("ViewerAddress");
            section.Set("Addresses", AddressList(PublicAddress, "10.1.0.0/16"));
            return config;
        }

        // The viewer is on the network the region lists and gets the region's own address;
        // the section alone would have sent it the public one.
        Rig lan = Build(LanViewer, null, Section(), ri => ri.SetExtraSetting("Addresses", LocalNetworks));
        Assert.Equal(RegionOwn, lan.LastInform());

        Rig remote = Build(RemoteViewer, null, Section(), ri => ri.SetExtraSetting("Addresses", "0.0.0.0/0=203.0.113.77"));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.77"), 9000), remote.LastInform());
    }

    [Fact]
    public void TheRemovedKeysChangeNothing()
    {
        TestHelpers.InMethod();
        IConfigSource config = new IniConfigSource();
        IConfig section = config.AddConfig("ViewerAddress");
        section.Set("PublicAddress", PublicAddress);
        section.Set("PublicDnsServer", "198.51.100.53");
        section.Set("LocalNetworks", LocalNetworks);
        section.Set("PublicRefreshMinutes", "5");

        Rig rig = Build(RemoteViewer, null, config, ri =>
        {
            ri.SetExtraSetting("PublicAddress", "203.0.113.77");
            ri.SetExtraSetting("LocalNetworks", LocalNetworks);
        });

        Assert.False(rig.EtmA.ViewerAddresses.Enabled);
        Assert.Equal(RegionOwn, rig.LastInform());
    }

    [Fact]
    public void AnEmptySectionLeavesTheSelectorDisabled()
    {
        TestHelpers.InMethod();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("ViewerAddress");

        Rig rig = Build(RemoteViewer, null, config);

        Assert.False(rig.EtmA.ViewerAddresses.Enabled);
        Assert.Equal(RegionOwn, rig.LastInform());
    }

    [Fact]
    public void NoSectionLeavesTheSelectorDisabled()
    {
        TestHelpers.InMethod();
        Rig rig = Build(RemoteViewer, null);

        Assert.False(rig.EtmA.ViewerAddresses.Enabled);
        Assert.Equal(RegionOwn, rig.LastInform());
    }

    [Fact]
    public void ViewerWithoutALiveConnectionIsClassifiedByItsCircuitAddress()
    {
        TestHelpers.InMethod();
        Rig rig = Build(LanViewer, null);

        // A root agent is classified by its UDP source.
        rig.Client.RemoteAddress = IPAddress.Parse(RemoteViewer);
        Assert.Equal(IPAddress.Parse(RemoteViewer), rig.EtmA.SourceOf(rig.Presence));

        // A child agent is classified by the address recorded in its circuit.
        rig.Presence.IsChildAgent = true;
        Assert.Equal(IPAddress.Parse(LanViewer), rig.EtmA.SourceOf(rig.Presence));

        // With no circuit recorded, its UDP source is all there is.
        rig.SceneA.AuthenticateHandler.RemoveCircuit(rig.Client.CircuitCode);
        Assert.Equal(IPAddress.Parse(RemoteViewer), rig.EtmA.SourceOf(rig.Presence));
    }

    // ---- a crossing that failed is not tried again at once ----

    private static readonly Vector3 EdgeOfA = new(30, 255, 32);

    /// <summary>A viewer that never reached B: B has no presence for it, so an update sent to B times out.</summary>
    private static Rig BuildViewerThatNeverReachedB(CrossingBackOffTests.TestClock clock)
    {
        Rig rig = Build(LanViewer, null);
        ForgetNeighbourB(rig);
        rig.EtmA.FailedCrossings = new CrossingBackOff(clock);
        return rig;
    }

    private static TimeSpan TimedCrossing(Rig rig)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        rig.EtmA.CrossAgentToNewRegionAsync(rig.Presence, EdgeOfA, rig.RegionB, false, new EntityTransferContext());
        return watch.Elapsed;
    }

    [Fact]
    public void ASecondCrossingIntoARegionThatDidNotTakeTheAvatarFailsAtOnce()
    {
        TestHelpers.InMethod();
        CrossingBackOffTests.TestClock clock = new();
        Rig rig = BuildViewerThatNeverReachedB(clock);

        // The first crossing waits for B and fails.
        rig.SceneB.ChildPresenceWait = TimeSpan.FromMilliseconds(500);
        Assert.True(TimedCrossing(rig) >= TimeSpan.FromMilliseconds(400));
        Assert.Empty(rig.Crossings);
        Assert.False(rig.Presence.IsChildAgent);
        Assert.False(rig.Presence.IsInTransit);
        Assert.True(rig.EtmA.FailedCrossings.IsBlocked(rig.Presence.UUID, rig.SceneB.RegionInfo.RegionHandle));

        // The second does not wait at all: with a 60 second wait in B, anything near it would show.
        rig.SceneB.ChildPresenceWait = TimeSpan.FromSeconds(60);
        Assert.True(TimedCrossing(rig) < TimeSpan.FromSeconds(10));
        Assert.Empty(rig.Crossings);
        Assert.False(rig.Presence.IsChildAgent);
        Assert.False(rig.Presence.IsInTransit);
    }

    [Fact]
    public void AfterTheBackOffTheCrossingWaitsForTheRegionAgain()
    {
        TestHelpers.InMethod();
        CrossingBackOffTests.TestClock clock = new();
        Rig rig = BuildViewerThatNeverReachedB(clock);
        rig.SceneB.ChildPresenceWait = TimeSpan.FromMilliseconds(500);
        TimedCrossing(rig);

        clock.Advance(CrossingBackOff.Period);

        Assert.True(TimedCrossing(rig) >= TimeSpan.FromMilliseconds(400));
        Assert.False(rig.Presence.IsInTransit);
    }

    [Fact]
    public void ACrossingThatSucceedsLeavesNothingBlocked()
    {
        TestHelpers.InMethod();
        Rig rig = Build(LanViewer, null);
        rig.EtmA.FailedCrossings = new CrossingBackOff(new CrossingBackOffTests.TestClock());

        rig.EtmA.CrossAgentToNewRegionAsync(rig.Presence, EdgeOfA, rig.RegionB, false, new EntityTransferContext());

        Assert.Single(rig.Crossings);
        Assert.False(rig.EtmA.FailedCrossings.IsBlocked(rig.Presence.UUID, rig.SceneB.RegionInfo.RegionHandle));
    }

    [Fact]
    public void AFailedCrossingOfOneAvatarDoesNotBlockAnother()
    {
        TestHelpers.InMethod();
        CrossingBackOffTests.TestClock clock = new();
        Rig rig = BuildViewerThatNeverReachedB(clock);
        rig.SceneB.ChildPresenceWait = TimeSpan.FromMilliseconds(250);
        TimedCrossing(rig);

        Assert.False(rig.EtmA.FailedCrossings.IsBlocked(TestHelpers.ParseTail(0x77), rig.SceneB.RegionInfo.RegionHandle));
    }

    // ---- the neighbour says why nobody turned up ----

    [Fact]
    public void TheWaitForTheViewerDefaultsToThirtySecondsAndCanBeSet()
    {
        TestHelpers.InMethod();
        Assert.Equal(TimeSpan.FromSeconds(30), new SceneHelpers().SetupScene().ChildPresenceWait);

        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup").Set("ChildPresenceWaitSeconds", "7");
        Assert.Equal(TimeSpan.FromSeconds(7), new SceneHelpers().SetupScene("s", UUID.Random(), 1000, 1000, config).ChildPresenceWait);
    }

    [Fact]
    public void AnAuthorisedViewerThatNeverConnectedGetsAWarningThatNamesTheAddressAndTheSection()
    {
        TestHelpers.InMethod();
        Rig rig = Build(RemoteViewer, null);
        ForgetNeighbourB(rig);
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(rig.Presence.UUID);
        circuit.IPAddress = RemoteViewer;
        rig.SceneB.AuthenticateHandler.AddNewCircuit(circuit);

        string warning = rig.SceneB.ChildNeverConnectedWarning(rig.Presence.UUID);

        Assert.NotNull(warning);
        Assert.Contains("127.0.0.1:9000", warning);
        Assert.Contains(RemoteViewer, warning);
        Assert.Contains("[ViewerAddress]", warning);
    }

    [Fact]
    public void NoWarningWhenNoCircuitWasAuthorised()
    {
        TestHelpers.InMethod();
        Rig rig = Build(RemoteViewer, null);
        Assert.Null(rig.SceneC.ChildNeverConnectedWarning(TestHelpers.ParseTail(0x78)));
    }

    // ---- regions of another grid ----

    private static readonly IPEndPoint ForeignOwn = new(IPAddress.Parse("192.0.2.80"), 9100);

    /// <summary>A region this grid does not know, as a gatekeeper reports a hypergrid destination: no flags, own address.</summary>
    private static GridRegion ForeignRegion() => new()
    {
        RegionID = TestHelpers.ParseTail(0x900),
        RegionName = "Foreign",
        ExternalHostName = "192.0.2.80",
        InternalEndPoint = new IPEndPoint(IPAddress.Any, 9100),
        ServerURI = "http://192.0.2.80:8002/"
    };

    [Theory]
    [InlineData(RemoteViewer, PublicAddress, "")]
    [InlineData(RemoteViewer, "public.example.org", "")]
    [InlineData(LanViewer, PublicAddress, "")]
    [InlineData(LanViewer, PublicAddress, "=198.51.100.99")]
    public void ARegionOfAnotherGridKeepsItsOwnEndpoint(string viewer, string publicAddress, string networkAddress)
    {
        TestHelpers.InMethod();
        Rig rig = Build(viewer, Selector(publicAddress, LocalNetworks + networkAddress));

        Assert.Equal(ForeignOwn, rig.EtmA.EndPointFor(ForeignRegion(), IPAddress.Parse(viewer)));
    }

    [Fact]
    public void ARegionMarkedAsAHyperlinkKeepsItsOwnEndpointEvenIfTheGridKnowsIt()
    {
        TestHelpers.InMethod();
        Rig rig = Build(RemoteViewer, Selector(PublicAddress, LocalNetworks));
        GridRegion linked = ForeignRegion();
        linked.RegionID = rig.SceneB.RegionInfo.RegionID;
        linked.RegionFlags = OpenSim.Framework.RegionFlags.Hyperlink;

        Assert.Equal(ForeignOwn, rig.EtmA.EndPointFor(linked, IPAddress.Parse(RemoteViewer)));
    }

    [Fact]
    public void WithOnlyBareNetworksARegionOfAnotherGridKeepsItsOwnEndpoint()
    {
        TestHelpers.InMethod();
        Rig rig = Build(RemoteViewer, Selector("", LocalNetworks));

        Assert.True(rig.EtmA.ViewerAddresses.Enabled);
        Assert.Equal(ForeignOwn, rig.EtmA.EndPointFor(ForeignRegion(), IPAddress.Parse(LanViewer)));
        Assert.Equal(ForeignOwn, rig.EtmA.EndPointFor(ForeignRegion(), IPAddress.Parse(RemoteViewer)));
    }

    [Fact]
    public void RegionsOfThisGridStillGetThePublicAddress()
    {
        TestHelpers.InMethod();
        Rig rig = Build(RemoteViewer, Selector(PublicAddress, LocalNetworks));

        Assert.Equal(RegionPublic, rig.EtmA.EndPointFor(rig.RegionB, IPAddress.Parse(RemoteViewer)));
        Assert.Equal(RegionOwn, rig.EtmA.EndPointFor(rig.RegionB, IPAddress.Parse(LanViewer)));
    }

    private static readonly IPEndPoint RegionRegistered = new(IPAddress.Parse("192.0.2.77"), 9000);

    private GridRegion RegionWithRegisteredLocal(Rig rig, string registered)
    {
        GridRegion region = rig.RegionB;
        region.ViewerLocalAddress = registered;
        return region;
    }

    [Fact]
    public void ALocalViewerIsGivenTheAddressTheRegionRegistered()
    {
        TestHelpers.InMethod();
        Rig rig = Build(LanViewer, Selector(PublicAddress, LocalNetworks));

        Assert.Equal(RegionRegistered,
            rig.EtmA.EndPointFor(RegionWithRegisteredLocal(rig, "192.0.2.77"), IPAddress.Parse(LanViewer)));
    }

    [Fact]
    public void AViewerOutsideTheLocalNetworksIsStillGivenThePublicAddress()
    {
        TestHelpers.InMethod();
        Rig rig = Build(RemoteViewer, Selector(PublicAddress, LocalNetworks));

        Assert.Equal(RegionPublic,
            rig.EtmA.EndPointFor(RegionWithRegisteredLocal(rig, "192.0.2.77"), IPAddress.Parse(RemoteViewer)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("2001:db8::1")]
    public void ARegionWithoutAUsableRegisteredAddressGivesTheLocalViewerItsOwnEndpointAsBefore(string registered)
    {
        TestHelpers.InMethod();
        Rig rig = Build(LanViewer, Selector(PublicAddress, LocalNetworks));

        Assert.Equal(RegionOwn,
            rig.EtmA.EndPointFor(RegionWithRegisteredLocal(rig, registered), IPAddress.Parse(LanViewer)));
    }

    [Theory]
    [InlineData(LanViewer)]
    [InlineData(RemoteViewer)]
    public void WithNoViewerAddressKeysARegisteredAddressChangesNothing(string viewer)
    {
        TestHelpers.InMethod();
        Rig rig = Build(viewer, null);

        Assert.False(rig.EtmA.ViewerAddresses.Enabled);
        Assert.Equal(RegionOwn,
            rig.EtmA.EndPointFor(RegionWithRegisteredLocal(rig, "192.0.2.77"), IPAddress.Parse(viewer)));
    }

    [Fact]
    public void ARegisteredAddressIsNeverGivenForARegionOfAnotherGrid()
    {
        TestHelpers.InMethod();
        Rig rig = Build(LanViewer, Selector(PublicAddress, LocalNetworks));
        GridRegion foreign = ForeignRegion();
        foreign.ViewerLocalAddress = "192.0.2.77";

        Assert.Equal(ForeignOwn, rig.EtmA.EndPointFor(foreign, IPAddress.Parse(LanViewer)));
    }

    [Fact]
    public void TheViewerIsToldTheRegisteredAddressWhenANeighbourIsAnnounced()
    {
        TestHelpers.InMethod();
        Rig rig = Build(LanViewer, Selector(PublicAddress, LocalNetworks));

        ForgetNeighbourB(rig);
        rig.EtmA.EnableChildAgent(rig.Presence, RegionWithRegisteredLocal(rig, "192.0.2.77"));

        Assert.Equal(RegionRegistered, rig.LastInform());
    }

    /// <summary>Exposes the protected viewer-source lookup.</summary>
    private sealed class ProbeEntityTransferModule : EntityTransferModule
    {
        public IPAddress SourceOf(ScenePresence sp) => ViewerSource(sp);
        public IPEndPoint EndPointFor(GridRegion region, IPAddress source) => ViewerEndPoint(region, source);
    }
}
