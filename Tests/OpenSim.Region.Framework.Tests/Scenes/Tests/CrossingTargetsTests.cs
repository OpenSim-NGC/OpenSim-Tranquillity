/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Frozen;
using System.Xml;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization.External;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The move-to target, hover and look-at of a physical object go on in the next region when its engine asks for it
/// (<see cref="PhysicsActor.CarriesTargetsAcrossCrossing"/>): core writes them into the saved object as optional elements,
/// the neighbour's new actor takes them up through the usual setters, and a crossing that fails forgets them. An engine
/// that does not ask carries nothing, and the saved object is written as before.
/// </summary>
public class CrossingTargetsTests : HeldCrossingFixture
{
    // the platform is at (128, 10, 2) in the first region; the second region lies beyond its low-y edge
    private static readonly Vector3 TargetBeyondTheEdge = new(120f, -20f, 5f);
    private static readonly Quaternion Facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f);

    private TargetPhysics m_physicsA;
    private TargetPhysics m_physicsB;

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup(bool asksToCarry = true)
    {
        SetupTwoRegions(5850, 5850);
        m_physicsA = NewPhysics(m_sceneA, asksToCarry);
        m_physicsB = NewPhysics(m_sceneB, asksToCarry);
        m_platform.RootPart.RemoveFromPhysics();
        m_platform.RootPart.UpdatePrimFlags(true, false, false, false, false);
        Assert.NotNull(m_platform.RootPart.PhysActor);
        Assert.True(m_platform.RootPart.PhysActor.IsPhysical);
    }

    private static TargetPhysics NewPhysics(Scene scene, bool asksToCarry)
    {
        TargetPhysics physics = new() { AsksToCarry = asksToCarry };
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup").Set("physics", "basicphysics");
        ((INonSharedRegionModule)physics).Initialise(config);
        ((INonSharedRegionModule)physics).AddRegion(scene);
        scene.PhysicsScene = physics;
        return physics;
    }

    private TargetPrim ActorA => (TargetPrim)m_platform.RootPart.PhysActor;

    private void SetAllThree()
    {
        m_platform.MoveToTarget(TargetBeyondTheEdge, 0.5f);
        m_platform.SetHoverHeight(3f, PIDHoverType.Ground, 0.7f, repel: true);
        m_platform.RotLookAt(Facing, 0.8f, 2f);
    }

    private TargetPrim CrossOver()
    {
        ScenePresence bystander = AddAvatar(0x11, Start + new Vector3(0.5f, 0f, 0.1f));
        Cross(bystander);
        TargetPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Assert.NotNull(arrived);
        return arrived;
    }

    // a look-at is the root part's own state until the heartbeat hands it to the engine, so it is read from the part
    private bool LookingAt(Scene scene, out Quaternion target, out float strength, out float damping)
    {
        target = Quaternion.Identity;
        strength = damping = 0f;
        SceneObjectGroup group = scene.GetSceneObjectGroup(m_platform.UUID);
        return group is not null && group.RootPart.TryGetLookAt(out target, out strength, out damping);
    }

    // ---- the saved object ------------------------------------------------------------------------------------------

    [Fact]
    public void An_object_with_nothing_to_carry_is_written_as_before()
    {
        TestHelpers.InMethod();
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, OwnerId, "plain", 0x70);
        Assert.Null(sog.RootPart.CrossingTargets);
        Assert.DoesNotMatch("<CT(Move|Hover|Look)", SceneObjectSerializer.ToOriginalXmlFormat(sog));
    }

    [Fact]
    public void The_targets_survive_the_saved_object()
    {
        TestHelpers.InMethod();
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, OwnerId, "carrier", 0x71);
        sog.RootPart.CrossingTargets = new CrossingTargets
        {
            MoveTarget = new Vector3(1.5f, -20.25f, 3f), MoveTau = 0.5f,
            HoverHeight = 3f, HoverType = (int)PIDHoverType.GroundAndWater, HoverTau = 0.7f, HoverRepel = true,
            LookTarget = Facing, LookStrength = 0.8f, LookDamping = 2f,
        };

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
        SceneObjectGroup back = SceneObjectSerializer.FromOriginalXmlFormat(xml);

        CrossingTargets t = back.RootPart.CrossingTargets;
        Assert.NotNull(t);
        Assert.Equal(new Vector3(1.5f, -20.25f, 3f), t.MoveTarget);
        Assert.Equal(0.5f, t.MoveTau);
        Assert.Equal(3f, t.HoverHeight);
        Assert.Equal((int)PIDHoverType.GroundAndWater, t.HoverType);
        Assert.Equal(0.7f, t.HoverTau);
        Assert.True(t.HoverRepel);
        Assert.Equal(Facing, t.LookTarget);
        Assert.Equal(0.8f, t.LookStrength);
        Assert.Equal(2f, t.LookDamping);
    }

    [Fact]
    public void Only_what_is_set_is_written()
    {
        TestHelpers.InMethod();
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, OwnerId, "carrier", 0x72);
        sog.RootPart.CrossingTargets = new CrossingTargets { MoveTarget = new Vector3(1f, 2f, 3f), MoveTau = 1f };
        string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
        Assert.Contains("<CTMoveTarget>", xml);
        Assert.Contains("<CTMoveTau>", xml);
        Assert.DoesNotContain("CTHover", xml);
        Assert.DoesNotContain("CTLook", xml);
    }

    [Fact]
    public void A_reader_that_does_not_know_the_elements_skips_them()
    {
        TestHelpers.InMethod();
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, OwnerId, "carrier", 0x73);
        sog.RootPart.Buoyancy = 0.25f;
        sog.RootPart.CrossingTargets = new CrossingTargets
        {
            MoveTarget = new Vector3(1.5f, -20.25f, 3f), MoveTau = 0.5f,
            HoverHeight = 3f, HoverType = 1, HoverTau = 0.7f, HoverRepel = false,
            LookTarget = Facing, LookStrength = 0.8f, LookDamping = 2f,
        };
        string xml = SceneObjectSerializer.ToOriginalXmlFormat(sog);
        Assert.Contains("<CTLookTarget>", xml);

        // a reader from before this change: its table of part elements has no entry for the new ones
        List<string> read = new();
        FrozenDictionary<string, Action<List<string>, XmlReader>> oldTable = new Dictionary<string, Action<List<string>, XmlReader>>
        {
            ["Buoyancy"] = (l, r) => l.Add("Buoyancy=" + r.ReadElementContentAsFloat("Buoyancy", string.Empty)),
            ["Force"] = (l, r) => { Util.ReadVector(r, "Force"); l.Add("Force"); },
            ["VolumeDetectActive"] = (l, r) => l.Add("VolumeDetectActive=" + Util.ReadBoolean(r)),
            ["PhysicsShapeType"] = (l, r) => l.Add("PhysicsShapeType=" + r.ReadElementContentAsString("PhysicsShapeType", string.Empty)),
        }.ToFrozenDictionary();

        using XmlReader reader = XmlReader.Create(new StringReader(xml));
        reader.MoveToContent();
        Assert.True(reader.ReadToDescendant("SceneObjectPart"));
        reader.ReadStartElement("SceneObjectPart");
        bool errors = ExternalRepresentationUtils.ExecuteReadProcessors(read, oldTable, reader, (o, n, e) => read.Add("error in " + n));

        Assert.False(errors);
        Assert.Contains("Buoyancy=0.25", read);
        Assert.Contains("Force", read);
        // the elements written after the new ones were still read
        Assert.Contains(read, s => s.StartsWith("PhysicsShapeType="));
        Assert.DoesNotContain(read, s => s.StartsWith("error"));
    }

    // ---- a crossing ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_crossing_carries_the_targets_and_the_neighbours_actor_takes_them_up()
    {
        TestHelpers.InMethod();
        Setup();
        SetAllThree();
        Assert.True(ActorA.PidOn);

        TargetPrim arrived = CrossOver();

        // the point named in the first region is the same point in the second: 20 m beyond the edge is 236 m of the next region
        Assert.True(arrived.PidOn);
        Assert.Equal(0.5f, arrived.Tau);
        Assert.Equal(TargetBeyondTheEdge.X, arrived.Target.X, 0.01f);
        Assert.Equal(m_sceneB.RegionInfo.RegionSizeY + TargetBeyondTheEdge.Y, arrived.Target.Y, 0.01f);
        Assert.Equal(TargetBeyondTheEdge.Z, arrived.Target.Z, 0.01f);

        Assert.True(arrived.HoverActive);
        Assert.Equal(3f, arrived.HoverHeight);
        Assert.Equal(PIDHoverType.Ground, arrived.HoverType);
        Assert.Equal(0.7f, arrived.HoverTau);

        Assert.True(LookingAt(m_sceneB, out Quaternion lookTarget, out float lookStrength, out float lookDamping));
        Assert.Equal(Facing, lookTarget);
        Assert.Equal(0.8f, lookStrength);
        Assert.Equal(2f, lookDamping);

        // taken up once: the object the neighbour holds does not carry the record on
        SceneObjectGroup there = m_sceneB.GetSceneObjectGroup(m_platform.UUID);
        Assert.NotNull(there);
        Assert.Null(there.RootPart.CrossingTargets);
        Assert.DoesNotMatch("<CT(Move|Hover|Look)", SceneObjectSerializer.ToOriginalXmlFormat(there));
    }

    [Fact]
    public void A_crossing_that_holds_the_object_for_its_riders_carries_the_targets_too()
    {
        TestHelpers.InMethod();
        Setup();
        SetAllThree();

        ScenePresence rider = AddStandingRider(0x11, new Vector3(0.5f, 0f, 0.1f));
        Cross(rider);
        TargetPrim arrived = m_physicsB.PrimOf(m_platform.UUID);

        Assert.NotNull(arrived);
        Assert.True(arrived.PidOn);
        Assert.Equal(m_sceneB.RegionInfo.RegionSizeY + TargetBeyondTheEdge.Y, arrived.Target.Y, 0.01f);
        Assert.True(arrived.HoverActive);
        Assert.True(LookingAt(m_sceneB, out _, out _, out _));
    }

    [Fact]
    public void Only_the_targets_that_were_set_cross()
    {
        TestHelpers.InMethod();
        Setup();
        m_platform.MoveToTarget(TargetBeyondTheEdge, 0.5f);

        TargetPrim arrived = CrossOver();

        Assert.True(arrived.PidOn);
        Assert.False(arrived.HoverActive);
        Assert.False(LookingAt(m_sceneB, out _, out _, out _));
    }

    [Fact]
    public void A_target_a_script_stopped_does_not_cross()
    {
        TestHelpers.InMethod();
        Setup();
        SetAllThree();
        m_platform.StopMoveToTarget();
        m_platform.SetHoverHeight(0f, PIDHoverType.Ground, 1f);
        m_platform.StopLookAt();

        TargetPrim arrived = CrossOver();

        Assert.False(arrived.PidOn);
        Assert.False(arrived.HoverActive);
        Assert.False(LookingAt(m_sceneB, out _, out _, out _));
    }

    [Fact]
    public void An_engine_that_does_not_ask_carries_nothing()
    {
        TestHelpers.InMethod();
        Setup(asksToCarry: false);
        SetAllThree();

        TargetPrim arrived = CrossOver();

        Assert.Null(m_platform.RootPart.CrossingTargets);
        Assert.False(arrived.PidOn);
        Assert.False(arrived.HoverActive);
    }

    [Fact]
    public void A_failed_crossing_forgets_what_it_wrote_and_the_object_keeps_its_targets()
    {
        TestHelpers.InMethod();
        Setup();
        m_connector.RefuseObject = true;
        SetAllThree();

        ScenePresence bystander = AddStandingRider(0x11, new Vector3(0.5f, 0f, 0.1f));
        Cross(bystander);

        // the destination did not take the object: it is still here, with nothing left in the record to start again
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_platform.RootPart.CrossingTargets);
        Assert.DoesNotMatch("<CT(Move|Hover|Look)", SceneObjectSerializer.ToOriginalXmlFormat(m_platform));
        Assert.True(ActorA.PidOn);
        Assert.True(ActorA.HoverActive);
    }

    // ---- stand-ins -------------------------------------------------------------------------------------------------

    private sealed class TargetPrim : BasicPhysicsPrim
    {
        private readonly bool m_asks;

        public TargetPrim(string name, uint localId, Vector3 position, Vector3 size, Quaternion orientation, PrimitiveBaseShape shape, bool asks)
            : base(name, localId, position, size, orientation, shape)
        {
            m_asks = asks;
        }

        public override bool CarriesTargetsAcrossCrossing => m_asks;

        private bool m_physical;
        public override bool IsPhysical { get => m_physical; set => m_physical = value; }

        public Vector3 Target;
        public float Tau;
        public bool PidOn;
        public override Vector3 PIDTarget { set => Target = value; }
        public override float PIDTau { set => Tau = value; }
        public override bool PIDActive { get => PidOn; set => PidOn = value; }

        public float HoverHeight, HoverTau;
        public PIDHoverType HoverType;
        public bool HoverActive;
        public override float PIDHoverHeight { set => HoverHeight = value; }
        public override PIDHoverType PIDHoverType { set => HoverType = value; }
        public override float PIDHoverTau { set => HoverTau = value; }
        public override bool PIDHoverActive { get => HoverActive; set => HoverActive = value; }

    }

    private sealed class TargetPhysics : BasicScene
    {
        private readonly List<TargetPrim> m_prims = new();

        public bool AsksToCarry { get; set; }

        public TargetPrim PrimOf(UUID objectId)
            => m_prims.FirstOrDefault(p => p.Name.EndsWith(objectId.ToString(), StringComparison.Ordinal)) ?? m_prims.LastOrDefault();

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position, Vector3 size,
            Quaternion rotation, bool isPhysical, uint localid)
        {
            TargetPrim prim = new(primName, localid, position, size, rotation, pbs, AsksToCarry) { IsPhysical = isPhysical };
            m_prims.Add(prim);
            return prim;
        }
    }
}
