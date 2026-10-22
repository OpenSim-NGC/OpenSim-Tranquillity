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
/// An avatar that stands on a moving object ahead of the object's centre, in the direction it travels, reaches the border
/// before the object's centre does. The object decides when it and its riders cross, so the avatar waits for it: it does not
/// cross on its own as an ordinary avatar, which would put it in the next region alone, with the velocity it had (the object's
/// own) as a walking speed that nothing stops, and with no object under it. Both regions run in one process.
/// </summary>
public class RiderAheadOfObjectCrossingTests : OpenSimTestCase
{
    private static readonly Vector3 PlatformInside = new(128, 2.5f, 2);

    // the platform's origin leaves the region by a tad, and arrives 0.2 m inside the neighbour
    private static readonly Vector3 PlatformTarget = new(128, -0.05f, 2);

    // ahead of the platform's origin in the direction it travels (towards the border at y = 0), 1 cm inside the region
    private static readonly Vector3 AheadOffset = new(0f, -2.49f, 0.1f);

    /// <summary>The platform's speed, towards the border.</summary>
    private static readonly Vector3 DeckSpeed = new(0f, -3f, 0f);

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

    private TestScene m_sceneA;
    private TestScene m_sceneB;
    private SceneObjectGroup m_platform;
    private UUID m_platformId;

    public RiderAheadOfObjectCrossingTests()
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
        m_platform.RootPart.Scale = new Vector3(8f, 8f, 0.5f);   // wide enough for a rider 2.5 m ahead of its origin
        m_platform.AbsolutePosition = PlatformInside;
        m_platform.RootPart.Velocity = DeckSpeed;
    }

    /// <summary>A standing rider linked to the platform, ahead of its origin, moving with the platform.</summary>
    private ScenePresence AddRiderAhead(int userTail)
    {
        UUID userId = TestHelpers.ParseTail(userTail);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        acd.circuitcode = (uint)(1000 + userTail);
        TestClient tc = new(acd, m_sceneA);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, new List<TestClient>());

        ScenePresence sp = SceneHelpers.AddScenePresence(m_sceneA, tc, acd);
        sp.AbsolutePosition = PlatformInside + AheadOffset;
        sp.RideOn.Update(0, m_platformId, true);
        sp.RideOn.Update(RideOnLimits.StartMs, m_platformId, true);
        Assert.True(sp.RideOn.IsLinked);

        // what the engine reports for an avatar on a moving deck: the deck's velocity
        sp.PhysicsActor.Velocity = DeckSpeed;
        return sp;
    }

    private static MomentumActor Actor(ScenePresence sp) => Assert.IsType<MomentumActor>(sp.PhysicsActor);

    [Fact]
    public void A_standing_rider_ahead_of_the_object_does_not_cross_on_its_own_before_the_object_does()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRiderAhead(0x11);
        Assert.InRange(Vector3.Distance(rider.Velocity, DeckSpeed), 0f, 0.001f);

        // the rider is at the border with the deck's speed towards it, the object's origin 2.5 m inside
        m_sceneA.Update(8);

        Assert.False(rider.IsChildAgent, "the rider crossed alone");
        Assert.False(rider.IsInTransit, "the rider started a crossing alone");
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platformId));
        Assert.True(rider.RideOn.IsLinked);
        Assert.InRange(rider.AbsolutePosition.Y, 0f, 1f);
        Assert.True(rider.PhysicsActor.AllowOutsideRegion, "the engine would pull the rider inside, off the part of the object that reaches over the border");
    }

    [Fact]
    public void A_standing_rider_ahead_of_the_object_crosses_with_it_and_arrives_at_rest_on_it()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRiderAhead(0x11);
        m_sceneA.Update(8);
        m_sceneB.Update(4);

        // the engine carries a standing avatar along with its platform: the platform's origin leaves the region by a tad
        rider.AbsolutePosition = PlatformTarget + AheadOffset;
        m_sceneA.SceneGraph.UpdatePrimGroupPosition(m_platform.LocalId, PlatformTarget, rider.ControllingClient);
        m_sceneA.Update(4);
        m_sceneB.Update(4);

        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        Assert.NotNull(arrived);
        ScenePresence riderB = m_sceneB.GetScenePresence(rider.UUID);
        Assert.NotNull(riderB);
        ((TestClient)riderB.ControllingClient).CompleteMovement();
        m_sceneA.Update(4);
        m_sceneB.Update(4);

        Assert.False(riderB.IsChildAgent);
        Assert.Equal(m_platformId, riderB.CarriedByGroup);
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, arrived.AbsolutePosition + AheadOffset), 0f, 0.05f);
        MomentumActor actor = Actor(riderB);
        foreach (Vector3 given in actor.Given)
            Assert.InRange(given.Length(), 0f, 0.2f);
        Assert.InRange(riderB.Velocity.Length(), 0f, 0.2f);
    }
}
