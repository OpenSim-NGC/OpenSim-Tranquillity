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
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// An object that crosses into a neighbouring region takes the avatars standing on it along (their soft link, see
/// <see cref="SoftLink"/>), the way it already takes the seated ones. Two regions run in one process; the crossing
/// thread runs inline so each step is finished when the call returns.
/// </summary>
public class StandingRidersCrossingTests : OpenSimTestCase
{
    private static readonly Vector3 PlatformStart = new(128, 10, 2);

    // the platform's origin leaves the region by a tad, and arrives 0.2 m inside the neighbour
    private static readonly Vector3 PlatformTarget = new(128, -0.05f, 2);

    private readonly List<(ScenePresence rider, Vector3 offset)> m_riders = new();

    private TestScene m_sceneA;
    private TestScene m_sceneB;
    private SceneObjectGroup m_platform;
    private UUID m_platformId;

    public StandingRidersCrossingTests()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private void SetupTwoRegions(float platformSize = 0.5f)
    {
        EntityTransferModule etmA = new();
        EntityTransferModule etmB = new();
        LocalSimulationConnectorModule lscm = new();

        IConfigSource config = new IniConfigSource();
        IConfig modulesConfig = config.AddConfig("Modules");
        modulesConfig.Set("EntityTransferModule", etmA.Name);
        modulesConfig.Set("SimulationServices", lscm.Name);
        IConfig entityTransferConfig = config.AddConfig("EntityTransfer");

        // a single threaded test cannot wait for the destination's callback
        entityTransferConfig.Set("wait_for_callback", false);

        SceneHelpers sh = new();
        m_sceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), 1000, 1000);
        m_sceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), 1000, 999);
        m_sceneA.EnableStandingRiderCrossing = true;
        m_sceneB.EnableStandingRiderCrossing = true;

        SceneHelpers.SetupSceneModules(new Scene[] { m_sceneA, m_sceneB }, config, lscm);
        SceneHelpers.SetupSceneModules(m_sceneA, config, new CapabilitiesModule(), etmA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, new CapabilitiesModule(), etmB);

        m_platform = SceneHelpers.AddSceneObject(m_sceneA, 1, TestHelpers.ParseTail(0x1), "", 0x2);
        m_platformId = m_platform.UUID;
        if (platformSize != 0.5f)
            m_platform.RootPart.Scale = new Vector3(platformSize, platformSize, 0.5f);
        m_platform.AbsolutePosition = PlatformStart;
    }

    /// <summary>An avatar in the first region at <paramref name="offset"/> from the platform's origin.</summary>
    private ScenePresence AddAvatar(int userTail, Vector3 offset, bool linked)
    {
        UUID userId = TestHelpers.ParseTail(userTail);
        AgentCircuitData acd = SceneHelpers.GenerateAgentData(userId);
        // the helper gives every agent the same circuit code, and two avatars cannot share one
        acd.circuitcode = (uint)(1000 + userTail);
        TestClient tc = new(acd, m_sceneA);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, new List<TestClient>());

        ScenePresence sp = SceneHelpers.AddScenePresence(m_sceneA, tc, acd);
        sp.AbsolutePosition = PlatformStart + offset;
        m_riders.Add((sp, offset));

        if (linked)
        {
            sp.RideOn.Update(0, m_platformId, true);
            sp.RideOn.Update(RideOnLimits.StartMs, m_platformId, true);
            Assert.True(sp.RideOn.IsLinked);
        }

        return sp;
    }

    private void CrossPlatform()
    {
        m_sceneA.Update(4);
        m_sceneB.Update(4);

        // the engine carries a standing avatar along with its platform: do the same here
        foreach ((ScenePresence rider, Vector3 offset) in m_riders)
            rider.AbsolutePosition = PlatformTarget + offset;

        m_sceneA.SceneGraph.UpdatePrimGroupPosition(m_platform.LocalId, PlatformTarget, m_riders[0].rider.ControllingClient);
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

    [Fact]
    public void A_standing_rider_crosses_with_the_object_and_arrives_at_the_same_offset()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        Vector3 offset = new(0.5f, 0f, 0.1f);
        ScenePresence rider = AddAvatar(0x11, offset, linked: true);
        Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.0f);
        rider.Rotation = facing;

        CrossPlatform();

        // the old region keeps its frozen copy until the rider has arrived (see HeldCrossingTests)
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        Assert.NotNull(arrived);

        Assert.True(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.False(rider.RideOn.IsLinked);
        ScenePresence riderB = Arrive(rider.UUID);

        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platformId));
        Assert.False(riderB.IsChildAgent);
        Vector3 expected = arrived.AbsolutePosition + offset;
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, expected), 0f, 0.05f);
        Assert.InRange(Quaternion.Dot(riderB.Rotation, facing), 0.999f, 1.001f);
    }

    [Fact]
    public void Two_standing_riders_each_keep_their_own_offset()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        Vector3 offset1 = new(0.5f, 0f, 0.1f);
        Vector3 offset2 = new(-0.5f, 0f, 0.1f);
        ScenePresence rider1 = AddAvatar(0x11, offset1, linked: true);
        ScenePresence rider2 = AddAvatar(0x12, offset2, linked: true);

        CrossPlatform();

        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        Assert.NotNull(arrived);
        ScenePresence r1 = Arrive(rider1.UUID);
        ScenePresence r2 = Arrive(rider2.UUID);

        Assert.InRange(Vector3.Distance(r1.AbsolutePosition, arrived.AbsolutePosition + offset1), 0f, 0.05f);
        Assert.InRange(Vector3.Distance(r2.AbsolutePosition, arrived.AbsolutePosition + offset2), 0f, 0.05f);
    }

    [Fact]
    public void An_avatar_that_is_not_linked_stays_behind()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        ScenePresence bystander = AddAvatar(0x11, new Vector3(0.5f, 0f, 0.1f), linked: false);

        CrossPlatform();

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platformId));
        Assert.False(m_sceneA.GetScenePresence(bystander.UUID).IsChildAgent);
        Assert.True(m_sceneB.GetScenePresence(bystander.UUID)?.IsChildAgent ?? true);
    }

    [Fact]
    public void A_link_to_an_avatar_that_moved_away_without_reports_is_dropped_at_the_crossing()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        // well outside the object's bounds plus the position gate, and no contact report since
        float far = 0.25f + RideOnLimits.PositionGate + 5f;
        ScenePresence gone = AddAvatar(0x11, new Vector3(far, 0f, 0.1f), linked: true);

        CrossPlatform();

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platformId));
        Assert.False(gone.RideOn.IsLinked);
        Assert.False(m_sceneA.GetScenePresence(gone.UUID).IsChildAgent);
        Assert.True(m_sceneB.GetScenePresence(gone.UUID)?.IsChildAgent ?? true);
    }

    [Fact]
    public void A_rider_inside_the_position_gate_next_to_the_object_is_still_carried()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        float near = 0.25f + RideOnLimits.PositionGate - 0.1f;
        ScenePresence edge = AddAvatar(0x11, new Vector3(near, 0f, 0.1f), linked: true);

        CrossPlatform();

        Assert.True(m_sceneA.GetScenePresence(edge.UUID).IsChildAgent);
    }

    // ---- arrival outside the new region's bounds: a rider behind the object's centre lands past the edge ----

    // 8 m platform crossing towards -y: a rider 3 m behind its centre arrives 2.8 m past the destination's far edge
    private static readonly Vector3 BehindCentre = new(0f, 3f, 0.1f);

    private static ContactPoint Feet(float z) => new(new Vector3(0, 0, z), new Vector3(0, 0, -1), 0f, true);

    private static void Report(ScenePresence sp, double t, uint objectId)
        => sp.UpdateSoftLink(new Dictionary<uint, ContactPoint> { { objectId, Feet(20f) } }, t);

    // The engine names the platform for the first quarter second, then the avatar is on the object for good.
    private static void StandOn(ScenePresence sp, uint platformLocalId)
    {
        Report(sp, 0, platformLocalId);
        Report(sp, RideOnLimits.StartMs, platformLocalId);
    }

    // Ground only (id 0) for longer than the break time: the avatar has walked off.
    private static void WalkOff(ScenePresence sp, double from)
    {
        for (double t = from; t <= from + RideOnLimits.BreakMs + 100; t += 100)
            Report(sp, t, 0);
    }

    // One physics step and one update of the presence in the second region, the way a frame runs them (the scene's own
    // update loop decides by the wall clock whether it steps physics, which a test cannot wait for).
    private void Frames(ScenePresence sp, int count)
    {
        // A presence skips its update (and with it the border check) until the viewer's handshake and caps have
        // come in; the fixture has no viewer, so mark the initial data as sent.
        typeof(ScenePresence).GetField("NeedInitialData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .SetValue(sp, -1);
        for (int i = 0; i < count; i++)
        {
            m_sceneB.PhysicsScene.Simulate(m_sceneB.FrameTime);
            sp.Update();
        }
    }

    private ScenePresence ArriveBehindCentre(out SceneObjectGroup arrived)
    {
        SetupTwoRegions(platformSize: 8f);
        ScenePresence rider = AddAvatar(0x11, BehindCentre, linked: true);
        CrossPlatform();
        arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        Assert.NotNull(arrived);
        ScenePresence riderB = Arrive(rider.UUID);
        Assert.False(riderB.IsChildAgent);
        return riderB;
    }

    [Fact]
    public void A_rider_behind_the_centre_arrives_outside_the_bounds_at_its_exact_offset_and_is_carried()
    {
        TestHelpers.InMethod();
        ScenePresence riderB = ArriveBehindCentre(out SceneObjectGroup arrived);

        Vector3 expected = arrived.AbsolutePosition + BehindCentre;
        Assert.True(expected.Y > m_sceneB.RegionInfo.RegionSizeY, $"the test needs an arrival outside the bounds, not {expected}");
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, expected), 0f, 0.05f);
        Assert.Equal(m_platformId, riderB.CarriedByGroup);

        // the border check would send it straight back to the first region: it must not, while carried
        Frames(riderB, 5);
        Assert.False(riderB.IsChildAgent);
        Assert.False(riderB.IsInTransit);
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, expected), 0f, 0.05f);
        Assert.True(m_sceneA.GetScenePresence(riderB.UUID).IsChildAgent);
        Assert.True(riderB.PhysicsActor.AllowOutsideRegion);
    }

    [Fact]
    public void A_carried_rider_outside_the_bounds_can_teleport_within_the_region()
    {
        TestHelpers.InMethod();
        ScenePresence riderB = ArriveBehindCentre(out SceneObjectGroup _);
        Assert.True(riderB.AbsolutePosition.Y > m_sceneB.RegionInfo.RegionSizeY, "the rider must be outside the bounds");
        m_sceneB.Update(4);

        Vector3 target = new(100f, 100f, 30f);
        m_sceneB.RequestTeleportLocation(riderB.ControllingClient, m_sceneB.RegionInfo.RegionHandle, target,
            Vector3.UnitX, (uint)TeleportFlags.ViaLocation);

        Assert.False(riderB.IsInTransit);
        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, target), 0f, 0.5f);
        Assert.False(riderB.IsChildAgent);
        Assert.Equal(UUID.Zero, riderB.CarriedByGroup);
        Assert.False(riderB.PhysicsActor.AllowOutsideRegion);

        // and it stays there (the test engine settles height onto the terrain, so only x and y are compared)
        m_sceneB.Update(4);
        Assert.InRange(Vector2.Distance(new Vector2(riderB.AbsolutePosition.X, riderB.AbsolutePosition.Y),
            new Vector2(target.X, target.Y)), 0f, 0.5f);
        Assert.False(riderB.IsChildAgent);
    }

    [Fact]
    public void A_carried_rider_that_walks_off_the_object_outside_the_bounds_is_sent_back_by_the_border_check()
    {
        TestHelpers.InMethod();
        ScenePresence riderB = ArriveBehindCentre(out SceneObjectGroup arrived);
        uint platformId = arrived.RootPart.LocalId;
        StandOn(riderB, platformId);
        Frames(riderB, 3);
        Assert.False(riderB.IsChildAgent);

        // it walks off the far side, away from the region it is standing in
        riderB.Velocity = new Vector3(0f, 2f, 0f);
        WalkOff(riderB, 300);
        Assert.Equal(UUID.Zero, riderB.CarriedByGroup);
        Assert.False(riderB.PhysicsActor.AllowOutsideRegion);

        // The ordinary border check takes over: the first region is there, so the avatar crosses back to it. The
        // crossing is finished on this side when the avatar is a child agent here; it becomes a root agent in the
        // first region when its viewer completes the movement, which the test client does on request.
        UUID id = riderB.UUID;
        Frames(riderB, 1);
        Assert.True(riderB.IsChildAgent);
        Assert.False(riderB.IsInTransit);

        m_sceneA.Update(4);
        ScenePresence backA = m_sceneA.GetScenePresence(id);
        ((TestClient)backA.ControllingClient).CompleteMovement();
        Assert.False(backA.IsChildAgent);
        Assert.Equal(UUID.Zero, backA.CarriedByGroup);
        Assert.False(backA.PhysicsActor.AllowOutsideRegion);
    }

    [Fact]
    public void A_carried_rider_that_walks_off_the_object_past_an_edge_with_no_neighbour_is_held_inside()
    {
        TestHelpers.InMethod();
        ScenePresence riderB = ArriveBehindCentre(out SceneObjectGroup arrived);
        uint platformId = arrived.RootPart.LocalId;
        StandOn(riderB, platformId);

        // carried 3 m past the west edge, where no region lies
        riderB.AbsolutePosition = new Vector3(-3f, 100f, 25f);
        Frames(riderB, 3);
        Assert.False(riderB.IsChildAgent);
        Assert.InRange(riderB.AbsolutePosition.X, -3.001f, -2.999f);

        WalkOff(riderB, 300);
        Frames(riderB, 1);

        // one pull back inside, not an attempt every frame
        Assert.False(riderB.IsChildAgent);
        Assert.False(riderB.IsInTransit);
        Vector3 held = riderB.AbsolutePosition;
        Assert.InRange(held.X, 0f, 1f);
        Frames(riderB, 5);
        Assert.Equal(held, riderB.AbsolutePosition);
        Assert.False(riderB.IsInTransit);
    }

    [Fact]
    public void A_carried_rider_whose_link_never_forms_is_not_exempt_for_ever()
    {
        TestHelpers.InMethod();
        ScenePresence riderB = ArriveBehindCentre(out SceneObjectGroup _);

        // reports that never name the object (it was never under the avatar here)
        for (double t = 0; t <= RideOnLimits.CarriedGraceMs + 200; t += 100)
            Report(riderB, t, 0);

        Assert.Equal(UUID.Zero, riderB.CarriedByGroup);
        Assert.False(riderB.PhysicsActor.AllowOutsideRegion);
    }

    [Fact]
    public void A_carried_rider_inside_the_bounds_arrives_exactly_and_an_ordinary_avatar_has_no_exemption()
    {
        TestHelpers.InMethod();
        SetupTwoRegions();
        Vector3 offset = new(0.5f, 0f, 0.1f);
        ScenePresence rider = AddAvatar(0x11, offset, linked: true);
        CrossPlatform();
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platformId);
        ScenePresence riderB = Arrive(rider.UUID);

        Assert.InRange(Vector3.Distance(riderB.AbsolutePosition, arrived.AbsolutePosition + offset), 0f, 0.05f);
        for (int i = 0; i < 3; i++)
            m_sceneB.Update(4);
        Assert.False(riderB.IsChildAgent);

        // an avatar nobody carries: no exemption, and the border check applies as it always did
        ScenePresence walker = SceneHelpers.AddScenePresence(m_sceneB, TestHelpers.ParseTail(0x31));
        Assert.False(walker.PhysicsActor.AllowOutsideRegion);
        Assert.Equal(UUID.Zero, walker.CarriedByGroup);
    }

    [Fact]
    public void The_carried_flag_and_the_offset_survive_the_agent_data_wire_format()
    {
        UUID group = UUID.Random();
        AgentData sent = new()
        {
            AgentID = UUID.Random(),
            Position = new Vector3(1, 2, 3),
            CrossingFlags = (byte)(1 | AgentData.CrossingCarried),
            CarriedByGroup = group,
            CarriedOffset = new Vector3(0.5f, -1.5f, 0.25f)
        };

        AgentData received = new();
        received.Unpack(sent.Pack(new EntityTransferContext()), null, new EntityTransferContext());

        Assert.Equal(sent.CrossingFlags, received.CrossingFlags);
        Assert.Equal(group, received.CarriedByGroup);
        Assert.Equal(sent.CarriedOffset, received.CarriedOffset);

        AgentData plain = new() { AgentID = UUID.Random(), CrossingFlags = 1 };
        AgentData plainReceived = new();
        plainReceived.Unpack(plain.Pack(new EntityTransferContext()), null, new EntityTransferContext());
        Assert.Equal(UUID.Zero, plainReceived.CarriedByGroup);
    }
}
