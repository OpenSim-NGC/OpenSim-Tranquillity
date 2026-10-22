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
using OpenSim.Tests.Common;
using TeleportFlags = OpenSim.Framework.Constants.TeleportFlags;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The look-at that AgentMovementComplete carries is what the viewer turns the avatar to. For an avatar an object
/// carried across a border it is the way the body faces, whichever way the camera points; for any other arrival it is
/// the camera heading, as before. With the rider crossing feature off nothing changes.
/// </summary>
public class RiderArrivalFacingTests : OpenSimTestCase
{
    private static readonly Vector3 PlatformStart = new(128, 10, 2);
    private static readonly Vector3 PlatformOut = new(128, -0.05f, 2);
    private static readonly Vector3 PlatformBack = new(128, 256.05f, 2);
    private static readonly Vector3 Offset = new(0.5f, 0f, 0.1f);

    // body facing +Y (a quarter turn), camera looking along -X
    private static readonly Quaternion BodyFacingPlusY = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
    private static readonly Vector3 CameraAtMinusX = new(-1f, 0f, 0f);

    private TestScene m_sceneA;
    private TestScene m_sceneB;
    private SceneObjectGroup m_platform;
    private UUID m_platformId;

    public RiderArrivalFacingTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private void SetupTwoRegions(bool featureOnInB = true)
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
        m_sceneB.EnableStandingRiderCrossing = featureOnInB;
        m_sceneA.PhysicsScene = new BasicScene();
        m_sceneB.PhysicsScene = new BasicScene();

        SceneHelpers.SetupSceneModules(new Scene[] { m_sceneA, m_sceneB }, config, lscm);
        SceneHelpers.SetupSceneModules(m_sceneA, config, new CapabilitiesModule(), etmA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, new CapabilitiesModule(), etmB);

        m_platform = SceneHelpers.AddSceneObject(m_sceneA, 1, TestHelpers.ParseTail(0x1), "", 0x2);
        m_platformId = m_platform.UUID;
        m_platform.AbsolutePosition = PlatformStart;
    }

    private ScenePresence AddRider(int userTail)
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
        sp.Rotation = BodyFacingPlusY;
        sp.CameraAtAxis = CameraAtMinusX;
        return sp;
    }

    private static void MovePlatform(TestScene scene, SceneObjectGroup platform, Vector3 to, ScenePresence rider)
    {
        scene.Update(4);
        rider.AbsolutePosition = to + Offset;
        scene.SceneGraph.UpdatePrimGroupPosition(platform.LocalId, to, rider.ControllingClient);
        scene.Update(4);
    }

    /// <summary>Lets the avatar's client in the scene complete the movement; returns the look-at it was sent.</summary>
    private Vector3 Arrive(TestScene scene, UUID userId)
    {
        ScenePresence sp = scene.GetScenePresence(userId);
        Assert.NotNull(sp);
        TestClient client = (TestClient)sp.ControllingClient;
        Vector3? sent = null;
        client.OnReceivedMoveAgentIntoRegion += (ri, pos, look) => sent = look;
        client.CompleteMovement();
        m_sceneA.Update(4);
        m_sceneB.Update(4);
        Assert.True(sent.HasValue, "AgentMovementComplete was not sent");
        return sent.Value;
    }

    private static void AssertHeading(Vector3 expected, Vector3 sent)
    {
        Vector3 flat = new(sent.X, sent.Y, 0f);
        Assert.InRange(Vector3.Distance(Vector3.Normalize(in flat), expected), 0f, 0.01f);
    }

    [Fact]
    public void A_carried_arrival_is_told_to_face_the_body_not_the_camera()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11);

        MovePlatform(m_sceneA, m_platform, PlatformOut, rider);
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platformId));

        Vector3 look = Arrive(m_sceneB, rider.UUID);

        AssertHeading(Vector3.UnitY, look);
        Assert.Equal(m_platformId, m_sceneB.GetScenePresence(rider.UUID).CarriedByGroup);
    }

    [Fact]
    public void A_carried_arrival_on_the_return_is_told_to_face_the_body_with_the_camera_turned_round()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11);

        MovePlatform(m_sceneA, m_platform, PlatformOut, rider);
        Arrive(m_sceneB, rider.UUID);

        // out in the other region the rider turns to face -Y, with the camera now looking back along +X
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        ScenePresence riderB = m_sceneB.GetScenePresence(rider.UUID);
        Assert.False(riderB.IsChildAgent);
        riderB.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f);
        riderB.CameraAtAxis = Vector3.UnitX;
        riderB.RideOn.Update(0, m_platformId, true);
        riderB.RideOn.Update(RideOnLimits.StartMs, m_platformId, true);
        Assert.True(riderB.RideOn.IsLinked);

        MovePlatform(m_sceneB, arrived, PlatformBack, riderB);
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platformId));

        Vector3 look = Arrive(m_sceneA, rider.UUID);

        AssertHeading(-Vector3.UnitY, look);
    }

    private void SendOrdinary(ScenePresence sent, byte crossingFlags, TeleportFlags teleportFlags)
    {
        sent.m_crossingFlags = crossingFlags;
        sent.CarriedByGroup = UUID.Zero;
        AgentData data = new();
        sent.CopyTo(data, true);
        AgentData received = new();
        received.Unpack(data.Pack(new EntityTransferContext()), null, new EntityTransferContext());
        ScenePresence child = m_sceneB.GetScenePresence(sent.UUID);
        Assert.True(child.IsChildAgent);
        child.UpdateChildAgent(received);
        child.TeleportFlags = teleportFlags;
    }

    [Fact]
    public void A_crossing_that_carries_nobody_is_still_told_the_camera_heading()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11);
        MovePlatform(m_sceneA, m_platform, PlatformOut, rider);

        SendOrdinary(m_sceneA.GetScenePresence(rider.UUID), 0, 0);

        AssertHeading(-Vector3.UnitX, Arrive(m_sceneB, rider.UUID));
    }

    [Fact]
    public void A_teleport_arrival_is_still_told_the_camera_heading()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence rider = AddRider(0x11);
        MovePlatform(m_sceneA, m_platform, PlatformOut, rider);

        SendOrdinary(m_sceneA.GetScenePresence(rider.UUID), 0, TeleportFlags.ViaLocation);

        AssertHeading(-Vector3.UnitX, Arrive(m_sceneB, rider.UUID));
    }

    [Fact]
    public void With_the_rider_crossing_feature_off_in_the_new_region_the_camera_heading_is_sent()
    {
        TestHelpers.InMethod();
        SetupTwoRegions(featureOnInB: false);
        ScenePresence rider = AddRider(0x11);
        MovePlatform(m_sceneA, m_platform, PlatformOut, rider);

        // sent as carried to a region that has the feature off: it takes the avatar for an ordinary one
        Vector3 look = Arrive(m_sceneB, rider.UUID);

        Assert.Equal(UUID.Zero, m_sceneB.GetScenePresence(rider.UUID).CarriedByGroup);
        AssertHeading(-Vector3.UnitX, look);
    }
}
