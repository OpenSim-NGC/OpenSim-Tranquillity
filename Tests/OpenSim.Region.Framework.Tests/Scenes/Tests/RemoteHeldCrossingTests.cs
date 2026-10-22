/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Server.Handlers.Base;
using OpenSim.Server.Handlers.Simulation;
using OpenSim.Services.Connectors.Simulation;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A held crossing between regions of different simulators: the three messages (the held CreateObject, recall and
/// complete) go through the REST connector, are serialized to JSON, and are answered by the object handler of the
/// other side, exactly as over the network; only the socket is missing. What the in-process tests of
/// <see cref="HeldCrossingTests"/> say about a crossing must hold here too.
/// </summary>
public class RemoteHeldCrossingTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup() => SetupTwoRegions(5500, 5500);

    private WireConnector Wire => (WireConnector)m_connector;

    protected override FaultyConnector NewConnector() => new WireConnector();

    // ---- the destination holds, as in one process -------------------------------------------------------------------

    [Fact]
    public void The_arriving_object_waits_for_its_standing_rider_and_the_old_region_lets_go_when_told()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.DoesNotContain(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Empty(Wire.Sent("complete"));

        Arrive(rider.UUID);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        OSDMap complete = Assert.Single(Wire.Sent("complete"));
        Assert.Equal(m_platform.UUID, complete["object_id"].AsUUID());
        Assert.Empty((OSDArray)complete["missing"]);
    }

    [Fact]
    public void The_arriving_object_waits_for_its_seated_rider()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence sitter = AddSeatedRider(0x11);

        Cross(sitter);

        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.DoesNotContain(m_platformScript.ItemID, m_engineB.Resumed);

        Arrive(sitter.UUID);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void With_two_riders_the_hold_ends_with_the_second_one()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        ScenePresence rider2 = AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));

        Cross(rider1);
        Arrive(rider1.UUID);

        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Empty(Wire.Sent("complete"));

        Arrive(rider2.UUID);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Single(Wire.Sent("complete"));
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void A_rider_that_never_arrives_releases_the_object_and_is_reported_missing()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        Elapse(RideOnLimits.ArriveHoldMs - 1);

        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Empty(Wire.Sent("complete"));

        Elapse(2);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        OSDMap complete = Assert.Single(Wire.Sent("complete"));
        OSDArray missing = (OSDArray)complete["missing"];
        Assert.Equal(new[] { rider.UUID }, missing.Select(m => m.AsUUID()).ToArray());
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
    }

    // ---- what goes over the wire --------------------------------------------------------------------------------------

    [Fact]
    public void The_object_is_sent_with_the_sending_region_and_the_expected_riders()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        ScenePresence sitter = AddSeatedRider(0x12);

        Cross(rider1);

        OSDMap create = Assert.Single(Wire.Sent("create"));
        Assert.Equal(m_sceneA.RegionInfo.RegionID, create["hold_source_uuid"].AsUUID());
        Assert.Equal(m_sceneA.RegionInfo.RegionName, create["hold_source_name"].AsString());
        Assert.Equal(m_sceneA.RegionInfo.ServerURI, create["hold_source_uri"].AsString());
        Assert.Equal((int)m_sceneA.RegionInfo.WorldLocX, create["hold_source_x"].AsInteger());
        Assert.Equal((int)m_sceneA.RegionInfo.WorldLocY, create["hold_source_y"].AsInteger());
        Assert.Equal(m_sceneB.RegionInfo.RegionID, create["destination_uuid"].AsUUID());
        Assert.Equal(
            new[] { rider1.UUID, sitter.UUID }.OrderBy(u => u).ToArray(),
            ((OSDArray)create["hold_riders"]).Select(r => r.AsUUID()).OrderBy(u => u).ToArray());

        // the question whether the other side can hold comes first, and only once per crossing
        Assert.Single(Wire.Sent("supports_held"));
        Assert.True(Wire.SentOrder.IndexOf("supports_held") < Wire.SentOrder.IndexOf("create"));
    }

    // ---- rollback, as in one process ---------------------------------------------------------------------------------

    [Fact]
    public void When_the_first_rider_fails_after_the_destination_accepted_the_object_is_recalled_and_all_is_unfrozen()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        ScenePresence rider2 = AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));
        m_connector.RefuseAgentUpdates.Add(1);

        Cross(rider1);

        OSDMap recall = Assert.Single(Wire.Sent("recall"));
        Assert.Equal(m_platform.UUID, recall["object_id"].AsUUID());
        Assert.Equal(m_sceneB.RegionInfo.RegionID, recall["destination_uuid"].AsUUID());
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_platform.inTransit);
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Empty(m_engineA.StillSuspended);
        Assert.Contains(m_platformScript.ItemID, m_engineA.Resumed);
        foreach (ScenePresence rider in new[] { rider1, rider2 })
        {
            Assert.False(rider.IsChildAgent);
            Assert.False(rider.IsInTransit);
            Assert.NotNull(rider.PhysicsActor);
        }
        Assert.Empty(Wire.Sent("complete"));
    }

    [Fact]
    public void When_the_recall_never_arrives_the_object_stays_at_the_destination_and_the_old_region_lets_go_when_it_is_released()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        m_connector.RefuseAgentUpdates.Add(1);
        Wire.Lost.Add("recall");

        Cross(rider);

        Assert.Single(Wire.Sent("recall"));
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.False(rider.IsChildAgent);
        Assert.False(rider.IsInTransit);
        Assert.NotNull(rider.PhysicsActor);
        Assert.True(m_sceneA.HasPendingCrossing(m_platform.UUID));

        Elapse(RideOnLimits.ArriveHoldMs + 1);

        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
    }

    [Fact]
    public void An_old_copy_whose_complete_message_was_lost_is_let_go_after_a_while()
    {
        TestHelpers.InMethod();
        Setup();
        m_sceneA.CrossingClock = () => m_now;
        Wire.Lost.Add("complete");
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        Arrive(rider.UUID);

        // the new region let the object go, and its report never arrived
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Single(Wire.Sent("complete"));
        Assert.True(m_sceneA.HasPendingCrossing(m_platform.UUID));

        m_now += RideOnLimits.ArriveHoldMs + RideOnLimits.PendingGraceMs - 1;
        m_sceneA.ReleaseExpiredCrossingHolds();
        Assert.True(m_sceneA.HasPendingCrossing(m_platform.UUID));

        m_now += 2;
        m_sceneA.ReleaseExpiredCrossingHolds();

        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void A_recall_after_a_rider_arrived_is_refused_by_the_destination_handler()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));

        Cross(rider1);
        Arrive(rider1.UUID);

        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.False(Wire.Client.RecallObject(new GridRegion(m_sceneB.RegionInfo), m_platform.UUID));
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
    }

    [Fact]
    public void The_messages_for_an_unknown_object_are_harmless_and_a_region_that_is_not_there_says_no()
    {
        TestHelpers.InMethod();
        Setup();
        GridRegion b = new(m_sceneB.RegionInfo);
        GridRegion a = new(m_sceneA.RegionInfo);

        Assert.True(Wire.Client.RecallObject(b, UUID.Random()));
        Assert.True(Wire.Client.CrossingCompleted(a, UUID.Random(), new List<UUID>()));
        Assert.False(Wire.Client.RecallObject(new GridRegion { RegionID = UUID.Random() }, UUID.Random()));
        Assert.False(Wire.Client.CrossingCompleted(new GridRegion { RegionID = UUID.Random() }, UUID.Random(), new List<UUID>()));
    }

    // ---- a destination that cannot hold gets the old path --------------------------------------------------------------

    [Fact]
    public void A_destination_that_does_not_answer_the_question_is_sent_the_object_the_old_way()
    {
        TestHelpers.InMethod();
        Setup();
        Wire.OldRegion = true;
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        // as before the held crossing existed: the object is in the new region at once, running, and gone from the old
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Empty(Wire.Sent("create"));
        Assert.Empty(Wire.Sent("recall"));
        Assert.Empty(Wire.Sent("complete"));
    }

    [Fact]
    public void The_answer_of_a_region_is_remembered_for_a_while_and_then_asked_again()
    {
        TestHelpers.InMethod();
        Setup();
        GridRegion b = new(m_sceneB.RegionInfo);

        Assert.True(Wire.Client.SupportsHeldCrossing(b));
        Assert.True(Wire.Client.SupportsHeldCrossing(b));
        Assert.Single(Wire.Sent("supports_held"));

        // an older region that was upgraded in the meantime is found again, not written off for good
        Wire.OldRegion = true;
        Wire.Client.Now += 60 * 60 * 1000;
        Assert.False(Wire.Client.SupportsHeldCrossing(b));
        Assert.Equal(2, Wire.Sent("supports_held").Count);
        Assert.False(Wire.Client.SupportsHeldCrossing(b));
        Assert.Equal(2, Wire.Sent("supports_held").Count);

        Wire.OldRegion = false;
        Wire.Client.Now += 60 * 60 * 1000;
        Assert.True(Wire.Client.SupportsHeldCrossing(b));
    }

    [Fact]
    public void The_connector_of_a_region_on_another_simulator_asks_the_other_side_and_the_local_one_needs_no_question()
    {
        TestHelpers.InMethod();
        Setup();
        GridRegion local = new(m_sceneB.RegionInfo);
        GridRegion elsewhere = new() { RegionID = UUID.Random(), RegionName = "Elsewhere", ServerURI = "http://elsewhere.example.org:9000/" };

        var module = new ProbeRemoteModule();
        module.Start(m_sceneB);

        Assert.True(module.SupportsHeldCrossing(local));
        Assert.Empty(module.Remote.Posts);

        Assert.True(module.SupportsHeldCrossing(elsewhere));
        Assert.Equal("supports_held", module.Remote.Posts.Single()["action"].AsString());
        Assert.Equal(elsewhere.ServerURI + "object/", module.Remote.Uris.Single()[..(elsewhere.ServerURI.Length + 7)]);

        Assert.True(module.RecallObject(elsewhere, UUID.Random()));
        Assert.True(module.CrossingCompleted(elsewhere, UUID.Random(), new List<UUID>()));
        Assert.Equal(new[] { "supports_held", "recall", "complete" }, module.Remote.Posts.Select(p => p["action"].AsString()).ToArray());
    }

    // ---- stand-ins -------------------------------------------------------------------------------------------------

    /// <summary>The REST connector with the network taken out: a message is handed to a function.</summary>
    public sealed class WireClient : SimulationServiceConnector
    {
        private readonly Func<string, OSDMap, OSDMap> m_transport;

        public double Now { get; set; } = 1000;

        public WireClient(Func<string, OSDMap, OSDMap> transport)
        {
            m_transport = transport;
        }

        protected override OSDMap PostObjectMessage(string uri, OSDMap args, int timeout) => m_transport(uri, args);

        protected override double NowMs() => Now;
    }

    /// <summary>The object handler of a region, opened up so that a test can hand it a message.</summary>
    public sealed class TestObjectHandler : ObjectSimpleHandler
    {
        public TestObjectHandler(ISimulationService service)
            : base(service, new ControlPlaneAccess(new IniConfigSource()))
        {
        }

        public void Post(OSDMap args, OpenSim.Framework.Servers.HttpServer.IOSHttpResponse response) => DoObjectPost(args, response);
    }

    /// <summary>
    /// The simulation connector of two regions that live on different simulators: each held-crossing message is built
    /// by the REST connector, goes through JSON, and is answered by the object handler on the other side.
    /// </summary>
    public sealed class WireConnector : FaultyConnector
    {
        [ThreadStatic]
        private static bool t_inHandler;

        private readonly TestObjectHandler m_handler;

        public WireClient Client { get; }

        /// <summary>Every message as the handler received it, in order.</summary>
        public List<OSDMap> Posts { get; } = new();

        public List<string> SentOrder { get; } = new();

        /// <summary>The destination answers like a handler from before the held messages: with an error to each.</summary>
        public bool OldRegion { get; set; }

        /// <summary>The actions (create, recall, complete, supports_held) whose message never arrives.</summary>
        public HashSet<string> Lost { get; } = new();

        public WireConnector()
        {
            Client = new WireClient(Transport);
            m_handler = new TestObjectHandler(this);
        }

        public List<OSDMap> Sent(string action) => Posts.Where(p => ActionOf(p) == action).ToList();

        private static string ActionOf(OSDMap args) => args.ContainsKey("action") ? args["action"].AsString() : "create";

        private OSDMap Transport(string uri, OSDMap sent)
        {
            OSDMap received = (OSDMap)OSDParser.DeserializeJson(OSDParser.SerializeJsonString(sent));
            string action = ActionOf(received);
            Posts.Add(received);
            SentOrder.Add(action);

            if (OldRegion || Lost.Contains(action))
                return Answer(false);

            TestOSHttpResponse response = new();
            t_inHandler = true;
            try
            {
                m_handler.Post(received, response);
            }
            finally
            {
                t_inHandler = false;
            }

            bool ok = response.StatusCode == 200 && response.RawBuffer is not null
                && Encoding.UTF8.GetString(response.RawBuffer).Equals("True", StringComparison.OrdinalIgnoreCase);
            return Answer(ok);
        }

        private static OSDMap Answer(bool success) => new()
        {
            ["Success"] = OSD.FromBoolean(success),
            ["success"] = OSD.FromBoolean(success)
        };

        protected override bool HeldSupported(GridRegion destination)
            => t_inHandler ? SupportsHeldCrossing(destination) : Client.SupportsHeldCrossing(destination);

        protected override bool SendHeld(GridRegion destination, Vector3 newPosition, ISceneObject sog, bool isLocalCall, CrossingHoldRequest hold)
        {
            if (t_inHandler)
                return CreateObject(destination, newPosition, sog, isLocalCall, hold);
            if (ThrowOnObject)
                throw new InvalidOperationException("the send failed");
            return !RefuseObject && Client.CreateObject(destination, newPosition, sog, isLocalCall, hold);
        }

        protected override bool SendRecall(GridRegion destination, UUID objectID)
        {
            if (t_inHandler)
                return RecallObject(destination, objectID);
            Recalled.Add(objectID);
            return Client.RecallObject(destination, objectID);
        }

        protected override bool SendCompleted(GridRegion source, UUID objectID, IList<UUID> missingRiders)
        {
            if (t_inHandler)
                return CrossingCompleted(source, objectID, missingRiders);
            Completed.Add(objectID);
            return Client.CrossingCompleted(source, objectID, missingRiders);
        }
    }

    /// <summary>
    /// The connector module a simulator runs when other simulators are configured (RemoteSimulationConnectorModule),
    /// with its REST connector replaced by one that records, so that the choice between local and remote shows.
    /// </summary>
    public sealed class ProbeRemoteModule : OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation.RemoteSimulationConnectorModule
    {
        public RecordingClient Remote { get; private set; }

        public void Start(params Scene[] localScenes)
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules").Set("SimulationServices", Name);
            Initialise(config);
            Remote = new RecordingClient();
            m_remoteConnector = Remote;
            foreach (Scene scene in localScenes)
                m_localBackend.Init(scene);
        }
    }

    public sealed class RecordingClient : SimulationServiceConnector
    {
        public List<OSDMap> Posts { get; } = new();
        public List<string> Uris { get; } = new();

        protected override OSDMap PostObjectMessage(string uri, OSDMap args, int timeout)
        {
            Uris.Add(uri);
            Posts.Add(args);
            return new OSDMap { ["Success"] = OSD.FromBoolean(true), ["success"] = OSD.FromBoolean(true) };
        }
    }
}
