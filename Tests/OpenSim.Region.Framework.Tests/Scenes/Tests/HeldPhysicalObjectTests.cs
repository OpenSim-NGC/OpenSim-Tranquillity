/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A physical object that arrives held for its riders is held still by the physics engine (no gravity, no drift, nothing
/// that touches it moves it) and moves on with the velocity it arrived with when the hold ends. The engines do the
/// holding; these tests check that the region asks for it at the right moments.
/// </summary>
public class HeldPhysicalObjectTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    private RecordingPhysics m_physicsB;

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup(bool physical)
    {
        SetupTwoRegions(5800, 5800);

        m_physicsB = new RecordingPhysics();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup").Set("physics", "basicphysics");
        ((INonSharedRegionModule)m_physicsB).Initialise(config);
        ((INonSharedRegionModule)m_physicsB).AddRegion(m_sceneB);
        m_sceneB.PhysicsScene = m_physicsB;

        if (physical)
            m_platform.RootPart.Flags |= PrimFlags.Physics;
    }

    [Fact]
    public void A_physical_object_is_held_still_when_it_arrives_and_let_go_when_its_rider_has_arrived()
    {
        TestHelpers.InMethod();
        Setup(physical: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        RecordingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Assert.NotNull(arrived);
        Assert.True(arrived.IsPhysical);
        Assert.Equal(new[] { "hold" }, arrived.Calls);

        Arrive(rider.UUID);

        Assert.Equal(new[] { "hold", "release" }, arrived.Calls);
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
    }

    [Fact]
    public void A_physical_object_is_let_go_when_the_hold_runs_out()
    {
        TestHelpers.InMethod();
        Setup(physical: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        RecordingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Elapse(RideOnLimits.ArriveHoldMs - 1);
        Assert.Equal(new[] { "hold" }, arrived.Calls);

        Elapse(2);

        Assert.Equal(new[] { "hold", "release" }, arrived.Calls);
    }

    [Fact]
    public void An_object_that_is_not_physical_is_not_asked_to_hold()
    {
        TestHelpers.InMethod();
        Setup(physical: false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        Arrive(rider.UUID);

        RecordingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Assert.NotNull(arrived);
        Assert.False(arrived.IsPhysical);
        Assert.Empty(arrived.Calls);
    }

    [Fact]
    public void A_physical_object_with_no_riders_is_not_held()
    {
        TestHelpers.InMethod();
        Setup(physical: true);
        ScenePresence bystander = AddAvatar(0x11, Start + StandingOffset);

        Cross(bystander);

        RecordingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Assert.NotNull(arrived);
        Assert.Empty(arrived.Calls);
    }

    // ---- stand-ins -------------------------------------------------------------------------------------------------

    private sealed class RecordingPrim : BasicPhysicsPrim
    {
        public RecordingPrim(string name, uint localId, Vector3 position, Vector3 size, Quaternion orientation, PrimitiveBaseShape shape)
            : base(name, localId, position, size, orientation, shape)
        {
        }

        private bool m_physical;

        public List<string> Calls { get; } = new();

        // the basic engine's prim is never physical; this one remembers what it was made as
        public override bool IsPhysical
        {
            get => m_physical;
            set => m_physical = value;
        }

        public override void HoldStill() => Calls.Add("hold");

        public override void ReleaseHold() => Calls.Add("release");
    }

    private sealed class RecordingPhysics : BasicScene
    {
        private readonly List<RecordingPrim> m_prims = new();

        public RecordingPrim PrimOf(UUID objectId)
        {
            return m_prims.FirstOrDefault(p => p.Name.EndsWith(objectId.ToString(), StringComparison.Ordinal))
                ?? m_prims.LastOrDefault();
        }

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position, Vector3 size,
            Quaternion rotation, bool isPhysical, uint localid)
        {
            RecordingPrim prim = new(primName, localid, position, size, rotation, pbs) { IsPhysical = isPhysical };
            m_prims.Add(prim);
            return prim;
        }
    }
}
