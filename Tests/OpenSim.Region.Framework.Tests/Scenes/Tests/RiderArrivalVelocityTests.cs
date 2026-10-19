/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// An avatar that crosses a border standing on a moving object arrives on that object at rest relative to it: the
/// object is held still until its riders have arrived and then moves on, and the engine carries a standing avatar
/// with the object it stands on. The velocity the avatar had in the first region already contains the object's own,
/// so handing it to the engine as momentum would count the object's motion twice (the avatar would run ahead of the
/// deck once the deck moves again). Both hand-offs are tested: the crossing between two regions in one process, and
/// the same hand-off through the agent data wire format that a crossing between two simulators uses.
/// </summary>
public class RiderArrivalVelocityTests : OpenSimTestCase
{
    private static readonly Vector3 PlatformStart = new(128, 10, 2);
    private static readonly Vector3 PlatformTarget = new(128, -0.05f, 2);
    private static readonly Vector3 Offset = new(0.5f, 0f, 0.1f);

    /// <summary>The platform's speed in the first region, a metre or so a second under the border.</summary>
    private static readonly Vector3 DeckSpeed = new(0f, -3f, 0f);

    /// <summary>An engine whose avatars note the momentum they are given.</summary>
    private sealed class MomentumActor : BasicActor
    {
        public readonly List<Vector3> Given = new();
        public MomentumActor(Vector3 size) : base(size) { }

        public override void SetMomentum(Vector3 momentum)
        {
            Given.Add(momentum);
            Velocity = momentum;
        }
    }

    private sealed class RecordingEngine : BasicScene
    {
        public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
        {
            return new MomentumActor(size) { Position = position, Velocity = velocity, Flying = isFlying };
        }
    }

    private readonly List<ScenePresence> m_riders = new();
    private TestScene m_sceneA;
    private TestScene m_sceneB;
    private SceneObjectGroup m_platform;
    private UUID m_platformId;

    public RiderArrivalVelocityTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private void SetupTwoRegions()
    {
        EntityTransferModule etmA = new();
        EntityTransferModule etmB = new();
        LocalSimulationConnectorModule lscm = new();

        IConfigSource config = new IniConfigSource();
        IConfig modulesConfig = config.AddConfig("Modules");
        modulesConfig.Set("EntityTransferModule", etmA.Name);
        modulesConfig.Set("SimulationServices", lscm.Name);
        IConfig entityTransferConfig = config.AddConfig("EntityTransfer");
        entityTransferConfig.Set("wait_for_callback", false);

        SceneHelpers sh = new();
        m_sceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), 1000, 1000);
        m_sceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), 1000, 999);
        m_sceneA.EnableStandingRiderCrossing = true;
        m_sceneB.EnableStandingRiderCrossing = true;
        m_sceneA.PhysicsScene = new RecordingEngine();
        m_sceneB.PhysicsScene = new RecordingEngine();

        SceneHelpers.SetupSceneModules(new Scene[] { m_sceneA, m_sceneB }, config, lscm);
        SceneHelpers.SetupSceneModules(m_sceneA, config, new CapabilitiesModule(), etmA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, new CapabilitiesModule(), etmB);

        m_platform = SceneHelpers.AddSceneObject(m_sceneA, 1, TestHelpers.ParseTail(0x1), "", 0x2);
        m_platformId = m_platform.UUID;
        m_platform.AbsolutePosition = PlatformStart;
    }

    /// <summary>A standing rider linked to the platform that moves at <see cref="DeckSpeed"/> plus its own walk.</summary>
    private ScenePresence AddRider(int userTail, Vector3 walking)
    {
        UUID userId = TestHelpers.ParseTail(userTail);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        acd.circuitcode = (uint)(1000 + userTail);
        TestClient tc = new(acd, m_sceneA);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, new List<TestClient>());

        ScenePresence sp = SceneHelpers.AddScenePresence(m_sceneA, tc, acd);
        sp.AbsolutePosition = PlatformStart + Offset;
        sp.RideOn.Update(0, m_platformId, true);
        sp.RideOn.Update(RideOnLimits.StartMs, m_platformId, true);
        Assert.True(sp.RideOn.IsLinked);

        // what the engine reports for an avatar on a moving deck: the deck's velocity and the walk
        sp.PhysicsActor.Velocity = DeckSpeed + walking;
        m_riders.Add(sp);
        return sp;
    }

    private void Cross()
    {
        m_sceneA.Update(4);
        m_sceneB.Update(4);

        // the engine carries a standing avatar along with its platform: do the same here
        foreach (ScenePresence rider in m_riders)
            rider.AbsolutePosition = PlatformTarget + Offset;

        m_sceneA.SceneGraph.UpdatePrimGroupPosition(m_platform.LocalId, PlatformTarget, m_riders[0].ControllingClient);
        m_sceneA.Update(4);
        m_sceneB.Update(4);
    }

    private ScenePresence Arrive(UUID userId)
    {
        ScenePresence sp = m_sceneB.GetScenePresence(userId);
        Assert.NotNull(sp);
        ((TestClient)sp.ControllingClient).CompleteMovement();
        m_sceneA.Update(4);
        m_sceneB.Update(4);
        return sp;
    }

    private static MomentumActor Actor(ScenePresence sp) => Assert.IsType<MomentumActor>(sp.PhysicsActor);

    // The deck is held still while riders arrive, so a rider that arrives with any velocity at all runs off it once
    // the deck is released; and one that arrives with the deck's old velocity runs ahead of it at twice its speed.
    private static void AssertAtRestOnTheDeck(ScenePresence rider)
    {
        MomentumActor actor = Actor(rider);
        foreach (Vector3 given in actor.Given)
            Assert.InRange(given.Length(), 0f, 0.2f);
        Assert.InRange(actor.Velocity.Length(), 0f, 0.2f);
        Assert.InRange(rider.Velocity.Length(), 0f, 0.2f);
    }

    [Fact]
    public void A_standing_rider_arrives_at_rest_on_the_deck_not_with_the_decks_velocity_again()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11, Vector3.Zero);
        Assert.InRange(Vector3.Distance(rider.Velocity, DeckSpeed), 0f, 0.001f);

        Cross();
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        ScenePresence riderB = Arrive(rider.UUID);

        Assert.False(riderB.IsChildAgent);
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, arrived.AbsolutePosition + Offset), 0f, 0.05f);
        AssertAtRestOnTheDeck(riderB);
    }

    [Fact]
    public void A_rider_walking_on_the_deck_arrives_in_place_with_no_launch()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11, new Vector3(1.5f, 0f, 0f));

        Cross();
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        ScenePresence riderB = Arrive(rider.UUID);

        // its controls were frozen for the crossing: it stands where it was on the deck, neither launched
        // along nor up, and nowhere lower than its place on the deck
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, arrived.AbsolutePosition + Offset), 0f, 0.05f);
        AssertAtRestOnTheDeck(riderB);
        Assert.InRange(Math.Abs(Actor(riderB).Velocity.Z), 0f, 0.01f);
        m_sceneB.Update(4);
        Assert.InRange(riderB.AbsolutePosition.Z, arrived.AbsolutePosition.Z + Offset.Z - 0.05f, float.MaxValue);
    }

    [Fact]
    public void A_rider_stays_with_a_deck_that_stops_right_after_the_border()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11, Vector3.Zero);

        Cross();
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        ScenePresence riderB = Arrive(rider.UUID);

        // the deck comes to rest at once: the engine then carries the rider by the deck's (zero) velocity alone,
        // which needs the rider to have no velocity of its own to stop
        arrived.RootPart.Velocity = Vector3.Zero;
        m_sceneB.Update(8);
        AssertAtRestOnTheDeck(riderB);
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, arrived.AbsolutePosition + Offset), 0f, 0.05f);
    }

    [Fact]
    public void A_rider_arriving_through_the_agent_data_wire_format_is_at_rest_on_the_deck_too()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11, Vector3.Zero);
        Cross();
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        Assert.NotNull(arrived);

        // what the sending simulator puts on the wire for the carried rider: its velocity as the engine reported it,
        // the carried flag, the object and the offset; the receiving simulator unpacks it onto the child agent
        ScenePresence sent = m_sceneA.GetScenePresence(rider.UUID);
        Assert.True(sent.IsChildAgent);
        sent.Velocity = DeckSpeed;
        sent.m_crossingFlags = (byte)(AgentData.CrossingCarried);
        sent.CarriedByGroup = m_platformId;
        sent.CarriedOffset = Offset;
        AgentData data = new();
        sent.CopyTo(data, true);
        Assert.InRange(Vector3.Distance(data.Velocity, DeckSpeed), 0f, 0.001f);

        AgentData received = new();
        received.Unpack(data.Pack(new EntityTransferContext()), null, new EntityTransferContext());
        Assert.InRange(Vector3.Distance(received.Velocity, DeckSpeed), 0f, 0.001f);
        Assert.True(m_sceneB.GetScenePresence(rider.UUID).IsChildAgent);
        m_sceneB.GetScenePresence(rider.UUID).UpdateChildAgent(received);

        ScenePresence riderB = Arrive(rider.UUID);
        Assert.False(riderB.IsChildAgent);
        Assert.Equal(m_platformId, riderB.CarriedByGroup);
        AssertAtRestOnTheDeck(riderB);
    }

    [Fact]
    public void An_avatar_that_crosses_on_its_own_still_arrives_with_the_velocity_it_left_with()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11, Vector3.Zero);
        Cross();
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platformId));

        // the same hand-off, but nothing carried: an ordinary crossing avatar
        ScenePresence sent = m_sceneA.GetScenePresence(rider.UUID);
        sent.Velocity = new Vector3(0f, -3f, 0f);
        sent.m_crossingFlags = 0;
        sent.CarriedByGroup = UUID.Zero;
        AgentData data = new();
        sent.CopyTo(data, true);
        AgentData received = new();
        received.Unpack(data.Pack(new EntityTransferContext()), null, new EntityTransferContext());
        m_sceneB.GetScenePresence(rider.UUID).UpdateChildAgent(received);

        ScenePresence riderB = Arrive(rider.UUID);
        Assert.False(riderB.IsChildAgent);
        Assert.Equal(UUID.Zero, riderB.CarriedByGroup);
        Assert.Contains(new Vector3(0f, -3f, 0f), Actor(riderB).Given);
    }
}
