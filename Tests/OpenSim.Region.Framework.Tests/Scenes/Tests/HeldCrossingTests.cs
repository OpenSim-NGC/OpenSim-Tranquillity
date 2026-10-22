/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The shared setup of the held-crossing tests: two regions in one process, a platform with a script in the first,
/// a script engine in each that records what it is asked, and a simulation connector that can be told to fail.
/// </summary>
public abstract class HeldCrossingFixture : OpenSimTestCase
{
    protected static readonly Vector3 Start = new(128, 10, 2);

    // the object's origin leaves the region by a tad
    protected static readonly Vector3 Target = new(128, -0.05f, 2);

    protected static readonly UUID OwnerId = TestHelpers.ParseTail(0x1);

    protected TestScene m_sceneA;
    protected TestScene m_sceneB;
    protected HoldEngine m_engineA;
    protected HoldEngine m_engineB;
    protected FaultyConnector m_connector;
    protected SceneObjectGroup m_platform;
    protected TaskInventoryItem m_platformScript;
    protected double m_now;

    private readonly Dictionary<UUID, Vector3> m_offsets = new();

    protected HeldCrossingFixture()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    /// <summary>The simulation connector both regions use; a class of tests can bring its own.</summary>
    protected virtual FaultyConnector NewConnector() => new FaultyConnector();

    /// <summary>Whether the rider crossing feature is switched on in each region (it is off by default in a real region).</summary>
    protected virtual bool FeatureOnInFirstRegion => true;
    protected virtual bool FeatureOnInSecondRegion => true;

    protected void SetupTwoRegions(uint regionX, uint regionY, bool banOwnerInDestination = false)
    {
        EntityTransferModule etmA = new();
        EntityTransferModule etmB = new();
        m_connector = NewConnector();
        m_engineA = new HoldEngine();
        m_engineB = new HoldEngine();

        IConfigSource config = new IniConfigSource();
        IConfig modulesConfig = config.AddConfig("Modules");
        modulesConfig.Set("EntityTransferModule", etmA.Name);
        modulesConfig.Set("SimulationServices", m_connector.Name);
        IConfig entityTransferConfig = config.AddConfig("EntityTransfer");
        entityTransferConfig.Set("wait_for_callback", false);

        SceneHelpers sh = new();
        m_sceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), regionX, regionY);
        m_sceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), regionX, regionY - 1);
        m_sceneA.EnableStandingRiderCrossing = FeatureOnInFirstRegion;
        m_sceneB.EnableStandingRiderCrossing = FeatureOnInSecondRegion;

        SceneHelpers.SetupSceneModules(new Scene[] { m_sceneA, m_sceneB }, config, m_connector);
        SceneHelpers.SetupSceneModules(m_sceneA, config, new CapabilitiesModule(), etmA, m_engineA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, new CapabilitiesModule(), etmB, m_engineB);

        if (banOwnerInDestination)
            m_sceneB.RegionInfo.EstateSettings.AddBan(new EstateBan { BannedUserID = OwnerId });

        m_platform = SceneHelpers.AddSceneObject(m_sceneA, 1, OwnerId, "", 0x2);
        m_platform.RootPart.Scale = new Vector3(8f, 8f, 0.5f);
        m_platform.AbsolutePosition = Start;
        m_platformScript = TaskInventoryHelpers.AddScript(m_sceneA.AssetService, m_platform.RootPart, "script",
            "default { state_entry() { } }");

        m_now = 1000;
        m_sceneB.CrossingClock = () => m_now;
    }

    protected ScenePresence AddAvatar(int userTail, Vector3 position, List<TestClient> neighbourClients = null)
    {
        UUID userId = TestHelpers.ParseTail(userTail);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        acd.circuitcode = (uint)(1000 + userTail);
        TestClient tc = new(acd, m_sceneA);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, neighbourClients ?? new List<TestClient>());
        ScenePresence sp = SceneHelpers.AddScenePresence(m_sceneA, tc, acd);
        sp.AbsolutePosition = position;
        return sp;
    }

    /// <summary>Gives the avatar an attachment with one script, which the engine of the first region runs.</summary>
    protected TaskInventoryItem Wear(ScenePresence sp)
    {
        SceneObjectGroup worn = SceneHelpers.CreateSceneObject(1, sp.UUID, "worn", 0x50 + (int)(sp.UUID.GetHashCode() & 0xff));
        TaskInventoryItem item = TaskInventoryHelpers.AddScript(m_sceneA.AssetService, worn.RootPart, "script",
            "default { state_entry() { } }");
        sp.AddAttachment(worn);
        return item;
    }

    protected ScenePresence AddStandingRider(int userTail, Vector3 offset)
    {
        ScenePresence sp = AddAvatar(userTail, Start + offset);
        sp.RideOn.Update(0, m_platform.UUID, true);
        sp.RideOn.Update(RideOnLimits.StartMs, m_platform.UUID, true);
        Assert.True(sp.RideOn.IsLinked);
        m_offsets[sp.UUID] = offset;
        return sp;
    }

    protected ScenePresence AddSeatedRider(int userTail)
    {
        ScenePresence sp = AddAvatar(userTail, Start);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, m_platform.UUID, Vector3.Zero);
        Assert.True(sp.IsSatOnObject);
        return sp;
    }

    /// <summary>The platform's origin leaves the region; the standing riders go with it, as the engine carries them.</summary>
    protected void Cross(ScenePresence mover)
    {
        m_sceneA.Update(4);
        m_sceneB.Update(4);
        foreach (KeyValuePair<UUID, Vector3> rider in m_offsets)
            m_sceneA.GetScenePresence(rider.Key).AbsolutePosition = Target + rider.Value;
        m_sceneA.SceneGraph.UpdatePrimGroupPosition(m_platform.LocalId, Target, mover.ControllingClient);
        m_sceneA.Update(4);
        m_sceneB.Update(4);
    }

    /// <summary>The viewer's last step of a crossing, in the second region.</summary>
    protected ScenePresence Arrive(UUID userId)
    {
        ScenePresence sp = m_sceneB.GetScenePresence(userId);
        Assert.NotNull(sp);
        ((TestClient)sp.ControllingClient).CompleteMovement();
        m_sceneA.Update(4);
        m_sceneB.Update(4);
        return sp;
    }

    /// <summary>Lets the time pass in the second region and releases what has run out.</summary>
    protected void Elapse(int ms)
    {
        m_now += ms;
        m_sceneB.ReleaseExpiredCrossingHolds();
    }

    // ---- stand-ins -------------------------------------------------------------------------------------------------

    /// <summary>The local simulation connector, told to fail a step, with a record of the new messages.</summary>
    public class FaultyConnector : LocalSimulationConnectorModule, ISimulationService
    {
        private int m_agentUpdates;

        /// <summary>The numbers (1 is the first) of the agent updates that are refused.</summary>
        public HashSet<int> RefuseAgentUpdates { get; } = new();

        public bool RefuseObject { get; set; }
        public bool ThrowOnObject { get; set; }

        /// <summary>The destination takes the object in, and the send throws afterwards (an answer lost on the way).</summary>
        public bool ThrowAfterObject { get; set; }
        public bool RefuseRecall { get; set; }

        /// <summary>False: the destination answers the access question as a region from before carried riders does.</summary>
        public bool CarriedRidersSupported { get; set; } = true;

        /// <summary>The avatars the destination's access question refuses (a ban line, an access level, a full region).</summary>
        public HashSet<UUID> RefuseAccess { get; } = new();

        public List<UUID> Recalled { get; } = new();
        public List<UUID> Completed { get; } = new();

        bool ISimulationService.UpdateAgent(GridRegion destination, AgentData cAgentData, EntityTransferContext ctx)
        {
            if (RefuseAgentUpdates.Contains(++m_agentUpdates))
                return false;
            return UpdateAgent(destination, cAgentData, ctx);
        }

        bool ISimulationService.QueryAccess(GridRegion destination, UUID agentID, string agentHomeURI, bool viaTeleport,
            Vector3 position, List<UUID> features, EntityTransferContext ctx, out string reason)
        {
            if (RefuseAccess.Contains(agentID))
            {
                reason = "The region will not take you.";
                return false;
            }

            bool allowed = QueryAccess(destination, agentID, agentHomeURI, viaTeleport, position, features, ctx, out reason);
            if (!CarriedRidersSupported)
                ctx.CarriedRiders = false;
            return allowed;
        }

        bool ISimulationService.SupportsHeldCrossing(GridRegion destination) => HeldSupported(destination);

        bool ISimulationService.CreateObject(GridRegion destination, Vector3 newPosition, ISceneObject sog, bool isLocalCall, CrossingHoldRequest hold)
            => SendHeld(destination, newPosition, sog, isLocalCall, hold);

        bool ISimulationService.RecallObject(GridRegion destination, UUID objectID) => SendRecall(destination, objectID);

        bool ISimulationService.CrossingCompleted(GridRegion source, UUID objectID, IList<UUID> missingRiders)
            => SendCompleted(source, objectID, missingRiders);

        // the four messages of a held crossing; a class of tests that sends them another way overrides these
        protected virtual bool HeldSupported(GridRegion destination) => SupportsHeldCrossing(destination);

        protected virtual bool SendHeld(GridRegion destination, Vector3 newPosition, ISceneObject sog, bool isLocalCall, CrossingHoldRequest hold)
        {
            if (ThrowOnObject)
                throw new InvalidOperationException("the send failed");
            if (RefuseObject)
                return false;
            bool taken = CreateObject(destination, newPosition, sog, isLocalCall, hold);
            if (ThrowAfterObject)
                throw new InvalidOperationException("the answer was lost");
            return taken;
        }

        protected virtual bool SendRecall(GridRegion destination, UUID objectID)
        {
            Recalled.Add(objectID);
            return !RefuseRecall && RecallObject(destination, objectID);
        }

        protected virtual bool SendCompleted(GridRegion source, UUID objectID, IList<UUID> missingRiders)
        {
            Completed.Add(objectID);
            return CrossingCompleted(source, objectID, missingRiders);
        }
    }

    /// <summary>
    /// A script engine that runs every script it is asked about, and notes each suspend and resume. It can be told to
    /// throw when a script is suspended.
    /// </summary>
    public sealed class HoldEngine : IScriptModule
    {
        private readonly HashSet<UUID> m_suspended = new();

        public Func<UUID, bool> ThrowOnSuspend { get; set; }
        public List<UUID> Suspended { get; } = new();
        public List<UUID> Resumed { get; } = new();
        public IEnumerable<UUID> StillSuspended => m_suspended.ToArray();

        public string Name => "HoldEngine";
        public string ScriptEngineName => "HoldEngine";
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene) { scene.StackModuleInterface<IScriptModule>(this); }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }

        public string GetXMLState(UUID itemID) => $"<State Engine=\"HoldEngine\" UUID=\"{itemID}\"><ScriptState/></State>";
        public bool SetXMLState(UUID itemID, string xml) => true;

        public bool SuspendScript(UUID itemID)
        {
            if (ThrowOnSuspend?.Invoke(itemID) == true)
                throw new InvalidOperationException("the engine failed");
            m_suspended.Add(itemID);
            Suspended.Add(itemID);
            return true;
        }

        public bool ResumeScript(UUID itemID)
        {
            m_suspended.Remove(itemID);
            Resumed.Add(itemID);
            return true;
        }

        public event ScriptRemoved OnScriptRemoved { add { } remove { } }
        public event ObjectRemoved OnObjectRemoved { add { } remove { } }
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public ArrayList GetScriptErrors(UUID itemID) => new();
        public bool HasScript(UUID itemID, out bool running) { running = true; return true; }
        public bool GetScriptState(UUID itemID) => false;
        public void SaveAllState() { }
        public void StartProcessing() { }
        public float GetScriptExecutionTime(List<UUID> itemIDs) => 0f;
        public int GetScriptsMemory(List<UUID> itemIDs) => 0;
        public Dictionary<uint, float> GetObjectScriptsExecutionTimes() => new();
        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
        {
            totaltime = 0f;
            totalmemory = 0f;
            return new List<ScriptTopStatsData>();
        }
    }
}

/// <summary>
/// An object that crosses with riders is held in the new region until they have arrived (or the hold ran out): its
/// scripts do not start before. The old region keeps the frozen original until the new one reports the crossing
/// complete, and takes everything back if the crossing fails after the new region accepted the object.
/// </summary>
public class HeldCrossingTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup(bool banOwnerInDestination = false) => SetupTwoRegions(5100, 5100, banOwnerInDestination);

    // ---- the destination holds -------------------------------------------------------------------------------------

    [Fact]
    public void The_arriving_object_does_not_run_its_scripts_until_its_standing_rider_has_arrived()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.DoesNotContain(m_platformScript.ItemID, m_engineB.Resumed);

        Arrive(rider.UUID);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
    }

    [Fact]
    public void The_arriving_object_does_not_run_its_scripts_until_its_seated_rider_has_arrived()
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
        Assert.DoesNotContain(m_platformScript.ItemID, m_engineB.Resumed);

        Arrive(rider2.UUID);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
    }

    [Fact]
    public void A_rider_that_never_arrives_releases_the_object_when_the_hold_runs_out()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        Elapse(RideOnLimits.ArriveHoldMs - 1);
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.DoesNotContain(m_platformScript.ItemID, m_engineB.Resumed);

        Elapse(2);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Contains(m_platform.UUID, m_connector.Completed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void An_object_with_no_riders_is_not_held()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence bystander = AddAvatar(0x11, Start + StandingOffset);

        Cross(bystander);

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
    }

    // ---- the old region keeps its copy until complete --------------------------------------------------------------

    [Fact]
    public void The_old_region_keeps_the_frozen_object_until_the_crossing_is_complete()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineA.StillSuspended);
        Assert.Empty(m_connector.Completed);

        Arrive(rider.UUID);

        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Equal(new[] { m_platform.UUID }, m_connector.Completed);
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void The_messages_are_idempotent_and_a_released_object_cannot_be_recalled()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        UUID unknown = UUID.Random();

        Assert.True(m_sceneB.RecallHeldObject(unknown));
        m_sceneA.CrossingCompleted(unknown, new List<UUID>());

        Cross(rider);
        Arrive(rider.UUID);

        Assert.False(m_sceneB.RecallHeldObject(m_platform.UUID));
        m_sceneA.CrossingCompleted(m_platform.UUID, new List<UUID>());
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
    }

    // ---- rollback --------------------------------------------------------------------------------------------------

    [Fact]
    public void When_the_destination_refuses_the_object_everything_is_given_back()
    {
        TestHelpers.InMethod();
        Setup(banOwnerInDestination: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        ScenePresence sitter = AddSeatedRider(0x12);
        TaskInventoryItem wornR = Wear(rider);
        TaskInventoryItem wornS = Wear(sitter);

        Cross(rider);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_platform.inTransit);
        Assert.Empty(m_engineA.StillSuspended);
        Assert.Contains(wornR.ItemID, m_engineA.Resumed);
        Assert.Contains(wornS.ItemID, m_engineA.Resumed);
        Assert.False(rider.IsInTransit);
        Assert.NotNull(rider.PhysicsActor);
        Assert.False(sitter.IsInTransit);
        Assert.True(sitter.IsSatOnObject);
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
    }

    [Fact]
    public void When_the_first_rider_fails_after_the_destination_accepted_the_object_is_recalled_and_all_is_unfrozen()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        ScenePresence rider2 = AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));
        TaskInventoryItem worn1 = Wear(rider1);
        TaskInventoryItem worn2 = Wear(rider2);
        m_connector.RefuseAgentUpdates.Add(1);

        Cross(rider1);

        Assert.Equal(new[] { m_platform.UUID }, m_connector.Recalled);
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_platform.inTransit);
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Empty(m_engineA.StillSuspended);
        Assert.Contains(m_platformScript.ItemID, m_engineA.Resumed);
        foreach ((ScenePresence rider, TaskInventoryItem worn) in new[] { (rider1, worn1), (rider2, worn2) })
        {
            Assert.False(rider.IsChildAgent);
            Assert.False(rider.IsInTransit);
            Assert.NotNull(rider.PhysicsActor);
            Assert.Contains(worn.ItemID, m_engineA.Resumed);
        }
        Assert.Empty(m_connector.Completed);
    }

    [Fact]
    public void A_rider_that_fails_after_another_crossed_is_stood_up_here_and_the_hold_runs_out_for_it()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        ScenePresence rider2 = AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));
        m_connector.RefuseAgentUpdates.Add(2);

        Cross(rider1);

        // no recall: one rider is already on its way
        Assert.Empty(m_connector.Recalled);
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        ScenePresence failed = rider1.IsChildAgent ? rider2 : rider1;
        Assert.False(failed.IsChildAgent);
        Assert.False(failed.IsInTransit);
        Assert.NotNull(failed.PhysicsActor);

        ScenePresence crossed = failed == rider1 ? rider2 : rider1;
        Arrive(crossed.UUID);
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID), "the failed rider is still expected");

        Elapse(RideOnLimits.ArriveHoldMs + 1);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
    }

    [Fact]
    public void When_the_recall_is_refused_the_object_stays_at_the_destination_and_the_old_region_lets_go_when_it_is_released()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        m_connector.RefuseAgentUpdates.Add(1);
        m_connector.RefuseRecall = true;

        Cross(rider);

        Assert.Equal(new[] { m_platform.UUID }, m_connector.Recalled);
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
}

/// <summary>
/// An exception between the freeze and the end of a crossing must not leave scripts suspended, riders frozen or the
/// object in transit. Each step has a label (see Scene.CrossingStepHook); a failure is injected at each in turn.
/// </summary>
public class CrossingExceptionSafetyTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    public static IEnumerable<object[]> Steps => new[]
    {
        new object[] { "riders-frozen" },
        new object[] { "scripts-suspended" },
        new object[] { "motion-stopped" },
        new object[] { "before-send" },
        new object[] { "object-sent" },
        new object[] { "rider-crossed" },
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public void A_failure_at_any_step_leaves_nothing_frozen(string step)
    {
        TestHelpers.InMethod();
        SetupTwoRegions(5300, 5300);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        ScenePresence sitter = AddSeatedRider(0x12);
        TaskInventoryItem wornR = Wear(rider);
        TaskInventoryItem wornS = Wear(sitter);
        bool thrown = false;
        m_sceneA.CrossingStepHook = (sog, label) =>
        {
            if (label == step && !thrown)
            {
                thrown = true;
                throw new InvalidOperationException("injected failure at " + label);
            }
        };

        Cross(rider);

        Assert.True(thrown, "the step was never reached");
        bool sent = step is "object-sent" or "rider-crossed";

        // whatever was not handed over runs again here
        Assert.False(m_platform.inTransit && !sent, "the object was left in transit");
        foreach (ScenePresence av in new[] { rider, sitter })
        {
            if (av.IsChildAgent)
                continue;
            Assert.False(av.IsInTransit, $"{av.Name} was left in transit");
            Assert.True(av.PhysicsActor is not null || av.IsSatOnObject, $"{av.Name} was left without physics");
        }
        Assert.True(rider.IsChildAgent || rider.CarriedByGroup.IsZero());

        if (!sent)
        {
            Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
            Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
            Assert.Empty(m_engineA.StillSuspended);
            Assert.False(rider.IsChildAgent);
            Assert.True(sitter.IsSatOnObject);
            Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        }
        else
        {
            // the object is committed to the other region: it runs there once its riders arrived or the hold ran out,
            // and this region then lets go of its copy
            Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
            foreach (UUID id in new[] { rider.UUID, sitter.UUID })
            {
                ScenePresence inB = m_sceneB.GetScenePresence(id);
                if (inB is not null && inB.IsChildAgent && m_sceneA.GetScenePresence(id).IsChildAgent)
                    ((TestClient)inB.ControllingClient).CompleteMovement();
            }
            Elapse(RideOnLimits.ArriveHoldMs + 1);
            Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
            Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
            Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
            Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
            foreach (ScenePresence av in new[] { rider, sitter })
            {
                ScenePresence a = m_sceneA.GetScenePresence(av.UUID);
                ScenePresence b = m_sceneB.GetScenePresence(av.UUID);
                Assert.True(a is { IsChildAgent: false } ^ b is { IsChildAgent: false },
                    $"{av.Name} is a root agent in neither or both regions");
                Assert.False((a?.IsInTransit ?? false) || (b?.IsInTransit ?? false));
            }
        }
    }

    [Fact]
    public void An_engine_that_throws_when_a_script_is_suspended_does_not_stop_the_crossing_or_strand_the_others()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(5320, 5320);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        TaskInventoryItem worn1 = Wear(rider);
        m_engineA.ThrowOnSuspend = id => id == m_platformScript.ItemID;

        Cross(rider);

        // the platform's own script could not be suspended, the crossing still ended cleanly
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.Contains(worn1.ItemID, m_engineA.Suspended);
        Assert.False(rider.IsInTransit && !rider.IsChildAgent);
    }

    [Fact]
    public void A_failure_in_the_send_itself_leaves_the_object_and_its_riders_running_here()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(5340, 5340);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        TaskInventoryItem worn = Wear(rider);
        m_connector.ThrowOnObject = true;

        Cross(rider);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_platform.inTransit);
        Assert.Empty(m_engineA.StillSuspended);
        Assert.Contains(worn.ItemID, m_engineA.Resumed);
        Assert.False(rider.IsInTransit);
        Assert.NotNull(rider.PhysicsActor);
    }
}
