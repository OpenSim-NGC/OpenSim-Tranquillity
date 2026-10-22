/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A physical object that is being dragged with the mouse reaches the border of its region. The object ends up in the
/// neighbour exactly once. The region it left holds no drag any more. For an engine that carries a physical object's
/// targets across a border (<see cref="PhysicsActor.CarriesTargetsAcrossCrossing"/>), the drag goes on in the neighbour:
/// the new actor is told it is dragged, steered to the same point of the world, and the viewer's release (or its next
/// update) reaches it. Halcyon keeps the grab target in the actor's properties and moves it by the region size when the
/// object crosses (PhysxPrim.cs, SuspendPhysicsSync). For any other engine the drag ends at the border.
/// </summary>
public class DragCrossingTests : HeldCrossingFixture
{
    // the platform is at (128, 10, 2) in the first region; the second region lies beyond its low-y edge
    private static readonly Vector3 DragStart = new(128f, 10f, 2.5f);
    private static readonly Vector3 DragBeyondTheEdge = new(128f, -20f, 5.5f);
    private static readonly Vector3 Anchor = new(0f, 0f, -0.5f); // the platform's origin less the first point dragged

    private DragPhysics m_physicsA;
    private DragPhysics m_physicsB;
    private ScenePresence m_bystander;
    private IClientAPI m_dragClient;
    private UUID m_dragId;

    // coordinates no other test class uses: the grid data is shared between classes
    protected virtual uint RegionCell => 6400;

    private void Setup(bool engineCarries = true, bool draggerKnownInSecondRegion = true)
    {
        SetupTwoRegions(RegionCell, RegionCell);
        m_physicsA = NewPhysics(m_sceneA, engineCarries);
        m_physicsB = NewPhysics(m_sceneB, engineCarries);
        m_platform.RootPart.RemoveFromPhysics();
        m_platform.RootPart.UpdatePrimFlags(true, false, false, false, false);
        Assert.NotNull(m_platform.RootPart.PhysActor);
        Assert.True(m_platform.RootPart.PhysActor.IsPhysical);

        m_bystander = AddAvatar(0x13, Start + new Vector3(-0.5f, 0f, 0.1f));
        if (draggerKnownInSecondRegion)
        {
            ScenePresence dragger = AddAvatar(0x11, Start + new Vector3(0.5f, 0f, 0.1f));
            m_dragClient = dragger.ControllingClient;
            if (m_sceneB.GetScenePresence(dragger.UUID) is null)
                SceneHelpers.AddChildScenePresence(m_sceneB, dragger.UUID);
        }
        else
        {
            // an agent that neither region has a presence for: the viewer of a dragger who has moved on
            AgentCircuitData acd = SceneHelpers.GenerateAgentData(TestHelpers.ParseTail(0x14));
            acd.circuitcode = 1014;
            m_dragClient = new TestClient(acd, m_sceneA);
        }
        m_dragId = m_dragClient.AgentId;
    }

    private static DragPhysics NewPhysics(Scene scene, bool carries)
    {
        DragPhysics physics = new() { Carries = carries };
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup").Set("physics", "basicphysics");
        ((INonSharedRegionModule)physics).Initialise(config);
        ((INonSharedRegionModule)physics).AddRegion(scene);
        scene.PhysicsScene = physics;
        return physics;
    }

    // the first region's actor, kept from before the crossing: the part has none once the object has left
    private DragPrim m_actorA;
    private DragPrim ActorA => m_actorA ??= (DragPrim)m_platform.RootPart.PhysActor;

    private void Drag(SceneObjectGroup group, Vector3 point)
        => group.GrabMovement(group.RootPart.UUID, Vector3.Zero, point, m_dragClient);

    private void DragAcrossTheBorder(ScenePresence mover = null)
    {
        Drag(m_platform, DragStart);
        Drag(m_platform, DragBeyondTheEdge);
        Assert.True(ActorA.IsGrabbed);
        Assert.True(ActorA.PidOn);
        Assert.Equal(DragBeyondTheEdge + Anchor, ActorA.Target);
        Cross(mover ?? m_bystander);
    }

    private DragPrim Arrived()
    {
        DragPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Assert.NotNull(arrived);
        return arrived;
    }

    private static int GrabSubscribers(Scene scene)
    {
        int count = 0;
        foreach (string name in new[] { "OnRemovePresence", "OnMakeChildAgent" })
        {
            FieldInfo field = typeof(EventManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(field);
            if (field.GetValue(scene.EventManager) is Delegate d)
                count += d.GetInvocationList().Count(h => h.Target is SceneObjectGroup);
        }
        return count;
    }

    // ---- the object ends up once -----------------------------------------------------------------------------------

    [Fact]
    public void A_dragged_object_that_reaches_the_border_ends_up_in_the_neighbour_exactly_once()
    {
        TestHelpers.InMethod();
        Setup();

        DragAcrossTheBorder();

        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        SceneObjectGroup there = m_sceneB.GetSceneObjectGroup(m_platform.UUID);
        Assert.NotNull(there);
        Assert.Single(m_sceneB.GetSceneObjectGroups(), g => g.UUID == m_platform.UUID);
        Assert.Equal(1, m_physicsB.Count);
    }

    // ---- the region it left -----------------------------------------------------------------------------------------

    [Fact]
    public void The_region_the_object_left_holds_no_drag_any_more()
    {
        TestHelpers.InMethod();
        Setup();
        Assert.True(GrabSubscribers(m_sceneA) == 0);
        Drag(m_platform, DragStart);
        Assert.Equal(2, GrabSubscribers(m_sceneA));

        DragAcrossTheBorder();

        Assert.False(ActorA.IsGrabbed);
        Assert.False(ActorA.PidOn);
        Assert.Equal(0, GrabSubscribers(m_sceneA));
    }

    // ---- the region it arrived in ----------------------------------------------------------------------------------

    [Fact]
    public void The_drag_goes_on_in_the_neighbour_to_the_same_point_of_the_world()
    {
        TestHelpers.InMethod();
        Setup();

        DragAcrossTheBorder();

        DragPrim arrived = Arrived();
        Assert.True(arrived.IsGrabbed);
        Assert.True(arrived.PidOn);
        Assert.Equal(0.25f, arrived.Tau);
        // 20 m beyond the edge of the first region is 236 m into the next
        Assert.Equal(DragBeyondTheEdge.X + Anchor.X, arrived.Target.X, 0.01f);
        Assert.Equal(m_sceneB.RegionInfo.RegionSizeY + DragBeyondTheEdge.Y + Anchor.Y, arrived.Target.Y, 0.01f);
        Assert.Equal(DragBeyondTheEdge.Z + Anchor.Z, arrived.Target.Z, 0.01f);
        Assert.Equal(1, GrabSubscribers(m_sceneB) / 2);
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID).RootPart.CrossingTargets);
    }

    [Fact]
    public void The_viewers_release_in_the_neighbour_ends_the_drag_there()
    {
        TestHelpers.InMethod();
        Setup();
        DragAcrossTheBorder();
        DragPrim arrived = Arrived();
        Assert.True(arrived.IsGrabbed);
        SceneObjectGroup there = m_sceneB.GetSceneObjectGroup(m_platform.UUID);

        m_sceneB.ProcessObjectDeGrab(there.RootPart.LocalId, m_dragClient, null);

        Assert.False(arrived.IsGrabbed);
        Assert.False(arrived.PidOn);
        Assert.Equal(0, GrabSubscribers(m_sceneB));
    }

    [Fact]
    public void The_viewers_next_update_in_the_neighbour_steers_the_same_grabbed_point()
    {
        TestHelpers.InMethod();
        Setup();
        DragAcrossTheBorder();
        DragPrim arrived = Arrived();
        Assert.True(arrived.IsGrabbed);
        SceneObjectGroup there = m_sceneB.GetSceneObjectGroup(m_platform.UUID);

        Vector3 next = new(100f, 200f, 8f);
        Drag(there, next);

        Assert.True(arrived.IsGrabbed);
        Assert.Equal(next + Anchor, arrived.Target);
    }

    [Fact]
    public void Another_agents_release_does_not_end_the_drag_in_the_neighbour()
    {
        TestHelpers.InMethod();
        Setup();
        DragAcrossTheBorder();
        DragPrim arrived = Arrived();
        SceneObjectGroup there = m_sceneB.GetSceneObjectGroup(m_platform.UUID);

        there.ReleaseGrab(TestHelpers.ParseTail(0x99));

        Assert.True(arrived.IsGrabbed);
        Assert.True(arrived.PidOn);
    }

    [Fact]
    public void A_script_move_under_the_drag_is_taken_up_again_when_the_drag_ends_in_the_neighbour()
    {
        TestHelpers.InMethod();
        Setup();
        Vector3 scriptTarget = new(120f, -30f, 6f);
        m_platform.MoveToTarget(scriptTarget, 0.5f);
        DragAcrossTheBorder();
        DragPrim arrived = Arrived();
        SceneObjectGroup there = m_sceneB.GetSceneObjectGroup(m_platform.UUID);
        Assert.Equal(0.25f, arrived.Tau);

        there.ReleaseGrab(m_dragId);

        Assert.False(arrived.IsGrabbed);
        Assert.True(arrived.PidOn);
        Assert.Equal(0.5f, arrived.Tau);
        Assert.Equal(m_sceneB.RegionInfo.RegionSizeY + scriptTarget.Y, arrived.Target.Y, 0.01f);
    }

    [Fact]
    public void An_object_held_for_its_rider_carries_the_drag_too()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));

        DragAcrossTheBorder(rider);

        DragPrim arrived = Arrived();
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.True(arrived.IsGrabbed);
        Assert.True(arrived.PidOn);
        Assert.Equal(m_sceneB.RegionInfo.RegionSizeY + DragBeyondTheEdge.Y + Anchor.Y, arrived.Target.Y, 0.01f);
        Assert.Equal(0, GrabSubscribers(m_sceneA));
    }

    [Fact]
    public void A_copy_of_a_dragged_object_does_not_inherit_the_drag()
    {
        TestHelpers.InMethod();
        Setup();
        Drag(m_platform, DragStart);
        Assert.True(ActorA.IsGrabbed);

        SceneObjectGroup copy = m_platform.Copy(false);
        copy.ReleaseGrab(m_dragId);

        Assert.True(ActorA.IsGrabbed);
        Assert.True(ActorA.PidOn);
        Assert.Equal(2, GrabSubscribers(m_sceneA));
    }

    // ---- what must not change ---------------------------------------------------------------------------------------

    [Fact]
    public void A_drag_that_was_let_go_before_the_border_carries_nothing()
    {
        TestHelpers.InMethod();
        Setup();
        Drag(m_platform, DragStart);
        m_platform.ReleaseGrab(m_dragId);
        Assert.False(ActorA.IsGrabbed);

        Cross(m_bystander);

        DragPrim arrived = Arrived();
        Assert.False(arrived.IsGrabbed);
        Assert.False(arrived.PidOn);
        Assert.Equal(0, GrabSubscribers(m_sceneB));
    }

    [Fact]
    public void An_object_that_was_not_dragged_crosses_without_a_drag()
    {
        TestHelpers.InMethod();
        Setup();

        Cross(m_bystander);

        DragPrim arrived = Arrived();
        Assert.False(arrived.IsGrabbed);
        Assert.False(arrived.PidOn);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void An_engine_that_does_not_carry_targets_ends_the_drag_at_the_border()
    {
        TestHelpers.InMethod();
        Setup(engineCarries: false);

        DragAcrossTheBorder();

        Assert.False(ActorA.IsGrabbed);
        Assert.False(ActorA.PidOn);
        Assert.Equal(0, GrabSubscribers(m_sceneA));
        DragPrim arrived = Arrived();
        Assert.False(arrived.IsGrabbed);
        Assert.False(arrived.PidOn);
        Assert.Single(m_sceneB.GetSceneObjectGroups(), g => g.UUID == m_platform.UUID);
    }

    [Fact]
    public void A_dragger_the_neighbour_does_not_know_leaves_the_object_free_there()
    {
        TestHelpers.InMethod();
        Setup(draggerKnownInSecondRegion: false);
        Assert.Null(m_sceneB.GetScenePresence(m_dragId));

        DragAcrossTheBorder();

        DragPrim arrived = Arrived();
        Assert.False(arrived.IsGrabbed);
        Assert.False(arrived.PidOn);
        Assert.Equal(0, GrabSubscribers(m_sceneB));
        Assert.Equal(0, GrabSubscribers(m_sceneA));
    }

    [Fact]
    public void A_crossing_the_neighbour_refuses_leaves_the_drag_going_on_the_original()
    {
        TestHelpers.InMethod();
        Setup();
        m_connector.RefuseObject = true;
        ScenePresence rider = AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));

        DragAcrossTheBorder(rider);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(ActorA.IsGrabbed);
        Assert.True(ActorA.PidOn);
        m_platform.ReleaseGrab(m_dragId);
        Assert.False(ActorA.IsGrabbed);
        Assert.False(ActorA.PidOn);
    }

    // ---- stand-ins -------------------------------------------------------------------------------------------------

    private sealed class DragPrim : BasicPhysicsPrim
    {
        private readonly bool m_carries;

        public DragPrim(string name, uint localId, Vector3 position, Vector3 size, Quaternion orientation, PrimitiveBaseShape shape, bool carries)
            : base(name, localId, position, size, orientation, shape)
        {
            m_carries = carries;
        }

        public override bool CarriesTargetsAcrossCrossing => m_carries;

        private bool m_physical;
        public override bool IsPhysical { get => m_physical; set => m_physical = value; }

        public bool IsGrabbed;
        public override bool Grabbed { set => IsGrabbed = value; }

        public Vector3 Target;
        public float Tau;
        public bool PidOn;
        public override Vector3 PIDTarget { set => Target = value; }
        public override float PIDTau { set => Tau = value; }
        public override bool PIDActive { get => PidOn; set => PidOn = value; }
    }

    private sealed class DragPhysics : BasicScene
    {
        private readonly List<DragPrim> m_prims = new();

        public bool Carries { get; set; }
        public int Count => m_prims.Count;

        public DragPrim PrimOf(UUID objectId)
            => m_prims.FirstOrDefault(p => p.Name.EndsWith(objectId.ToString(), StringComparison.Ordinal)) ?? m_prims.LastOrDefault();

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position, Vector3 size,
            Quaternion rotation, bool isPhysical, uint localid)
        {
            DragPrim prim = new(primName, localid, position, size, rotation, pbs, Carries) { IsPhysical = isPhysical };
            m_prims.Add(prim);
            return prim;
        }
    }
}

/// <summary>
/// The same crossings with the object sent as the remote connector sends it: held crossings (an object with a rider) go
/// through the JSON messages and the other side's handler, so the saved object is written and read back in between.
/// </summary>
public class DragCrossingOverTheWireTests : DragCrossingTests
{
    protected override uint RegionCell => 6420;

    protected override FaultyConnector NewConnector() => new RemoteHeldCrossingTests.WireConnector();
}

/// <summary>The drag a crossing carries is written into the saved object as optional elements and read back.</summary>
public class DragCrossingRecordTests : OpenSimTestCase
{
    [Fact]
    public void The_drag_survives_the_saved_object()
    {
        TestHelpers.InMethod();
        UUID agent = TestHelpers.ParseTail(0x31);
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, TestHelpers.ParseTail(0x1), "dragged", 0x80);
        sog.RootPart.CrossingTargets = new CrossingTargets
        {
            DragAgent = agent, DragAnchor = new Vector3(0.5f, -1.25f, 0.75f), DragTarget = new Vector3(100f, 236.5f, 9f),
        };

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
        CrossingTargets back = SceneObjectSerializer.FromOriginalXmlFormat(xml).RootPart.CrossingTargets;

        Assert.NotNull(back);
        Assert.True(back.HasDrag);
        Assert.Equal(agent, back.DragAgent);
        Assert.Equal(new Vector3(0.5f, -1.25f, 0.75f), back.DragAnchor);
        Assert.Equal(new Vector3(100f, 236.5f, 9f), back.DragTarget);
        Assert.False(back.HasMove);
    }

    [Fact]
    public void An_object_that_is_not_dragged_writes_no_drag_element()
    {
        TestHelpers.InMethod();
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, TestHelpers.ParseTail(0x1), "plain", 0x81);
        sog.RootPart.CrossingTargets = new CrossingTargets { MoveTarget = new Vector3(1f, 2f, 3f), MoveTau = 1f };

        Assert.DoesNotContain("CTDrag", SceneObjectSerializer.ToOriginalXmlFormat(sog));
    }
}
