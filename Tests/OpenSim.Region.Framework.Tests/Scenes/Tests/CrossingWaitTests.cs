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
/// What an object that crossed in with riders does while it waits for them and right after. The wait is measured on the
/// region's crossing clock and handed to the engine when the hold ends, capped at RideOnLimits.ResumeInterpolationMaxMs,
/// for an engine that moves the object on by it; and an engine that asks for it keeps the object from crossing out again
/// for RideOnLimits.RecrossWaitMs. An engine that does not ask sees neither. The same two behaviours are in
/// Halcyon (PhysxPrim.cs DoResumeInterpolation, SceneObjectGroup.cs TestForAndBeginCrossing).
/// </summary>
public class CrossingWaitTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    // north edge of the second region, a tad over it: the way back to the first
    private static readonly Vector3 BackTarget = new(128, 256.05f, 2);

    private WaitingPhysics m_physicsB;

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup(bool physicalAndMovesOn, bool waitsBeforeCrossingBack)
    {
        SetupTwoRegions(5900, 5900);

        m_physicsB = new WaitingPhysics { MovesOn = physicalAndMovesOn, Waits = waitsBeforeCrossingBack };
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Startup").Set("physics", "basicphysics");
        ((INonSharedRegionModule)m_physicsB).Initialise(config);
        ((INonSharedRegionModule)m_physicsB).AddRegion(m_sceneB);
        m_sceneB.PhysicsScene = m_physicsB;

        m_platform.RootPart.Flags |= PrimFlags.Physics;
    }

    private SceneObjectGroup InSecondRegion() => m_sceneB.GetSceneObjectGroup(m_platform.UUID);

    // ---- the resume --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_engine_is_told_how_long_the_object_waited_for_its_rider()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        WaitingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Elapse(1500);
        Arrive(rider.UUID);

        Assert.Equal(new[] { "hold", "release 1.500" }, arrived.Calls);
    }

    [Fact]
    public void The_time_the_engine_is_told_stops_at_three_seconds()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        WaitingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Elapse(RideOnLimits.ArriveHoldMs);

        Assert.Equal(new[] { "hold", "release 3.000" }, arrived.Calls);
    }

    [Fact]
    public void An_engine_that_only_lets_go_is_released_as_before()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: false, waitsBeforeCrossingBack: false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        WaitingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Elapse(1500);
        Arrive(rider.UUID);

        Assert.Equal(new[] { "hold", "release" }, arrived.Calls);
    }

    [Fact]
    public void A_rider_that_arrived_goes_along_with_the_object_when_the_engine_moves_it_on()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        WaitingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        arrived.Velocity = new Vector3(0f, -4f, 0f);
        Elapse(1000);
        ScenePresence there = Arrive(rider.UUID);
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));

        // the stand-in engine moved the object on by 4 m (its velocity for the 1 s it waited): the rider stands on it still
        Assert.Equal(-4f, arrived.Moved.Y, 3);
        Assert.True(Math.Abs(there.AbsolutePosition.Y - arrived.Position.Y) < 0.3f,
            $"the rider was left behind: rider {there.AbsolutePosition}, object {arrived.Position}");
    }

    // ---- the wait before crossing back -------------------------------------------------------------------------------

    [Fact]
    public void An_object_that_crossed_in_with_a_rider_does_not_cross_out_again_within_three_seconds()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        Cross(rider);
        Arrive(rider.UUID);
        Assert.NotNull(InSecondRegion());

        Elapse(RideOnLimits.RecrossWaitMs - 1);
        InSecondRegion().AbsolutePosition = BackTarget;

        Assert.NotNull(InSecondRegion());
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(InSecondRegion().AbsolutePosition.Y < 256f, $"it was left outside the region: {InSecondRegion().AbsolutePosition}");
    }

    [Fact]
    public void The_same_object_crosses_out_once_three_seconds_have_gone()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        Cross(rider);
        Arrive(rider.UUID);

        Elapse(RideOnLimits.RecrossWaitMs - 1);
        InSecondRegion().AbsolutePosition = BackTarget;
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));

        Elapse(1);
        InSecondRegion().AbsolutePosition = BackTarget;

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void An_engine_that_does_not_ask_lets_the_object_cross_out_at_once()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        Cross(rider);
        Arrive(rider.UUID);

        Elapse(100);
        InSecondRegion().AbsolutePosition = BackTarget;

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void An_object_that_crossed_in_with_no_rider_is_not_kept_from_crossing_out()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: true);
        ScenePresence bystander = AddAvatar(0x11, Start + StandingOffset);
        Cross(bystander);
        Assert.NotNull(InSecondRegion());

        Elapse(100);
        InSecondRegion().AbsolutePosition = BackTarget;

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
    }

    // ---- keeping the object inside the border during the wait -----------------------------------------------------------

    [Fact]
    public void An_engine_that_waits_is_told_to_keep_the_object_inside_the_border_until_the_wait_is_over()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        Cross(rider);
        WaitingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Assert.Equal(new[] { true }, arrived.Keeps);

        Arrive(rider.UUID);
        Elapse(RideOnLimits.RecrossWaitMs - 1);
        Assert.Equal(new[] { true }, arrived.Keeps);

        Elapse(1);
        Assert.Equal(new[] { true, false }, arrived.Keeps);

        Elapse(RideOnLimits.RecrossWaitMs);
        Assert.Equal(new[] { true, false }, arrived.Keeps);
    }

    [Fact]
    public void An_engine_that_does_not_wait_is_not_told_to_keep_the_object_inside()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        Cross(rider);
        WaitingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Arrive(rider.UUID);
        Elapse(RideOnLimits.RecrossWaitMs + 1);

        Assert.Empty(arrived.Keeps);
    }

    [Fact]
    public void The_wait_counts_from_the_arrival_not_from_the_release_of_the_hold()
    {
        TestHelpers.InMethod();
        Setup(physicalAndMovesOn: true, waitsBeforeCrossingBack: true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        Cross(rider);
        WaitingPrim arrived = m_physicsB.PrimOf(m_platform.UUID);
        Elapse(2000);
        Arrive(rider.UUID);
        Assert.Equal(new[] { true }, arrived.Keeps);

        Elapse(999);
        Assert.Equal(new[] { true }, arrived.Keeps);
        Elapse(1);
        Assert.Equal(new[] { true, false }, arrived.Keeps);
    }

    // ---- stand-ins -------------------------------------------------------------------------------------------------

    private sealed class WaitingPrim : BasicPhysicsPrim
    {
        private readonly WaitingPhysics m_owner;
        private bool m_physical;

        public WaitingPrim(WaitingPhysics owner, string name, uint localId, Vector3 position, Vector3 size, Quaternion orientation,
            PrimitiveBaseShape shape)
            : base(name, localId, position, size, orientation, shape)
        {
            m_owner = owner;
        }

        public List<string> Calls { get; } = new();

        /// <summary>The values <see cref="KeepInsideBorder"/> was called with, in order.</summary>
        public List<bool> Keeps { get; } = new();

        /// <summary>How far the stand-in engine moved the object on at the release.</summary>
        public Vector3 Moved { get; private set; }

        // the basic engine's prim is never physical; this one remembers what it was made as
        public override bool IsPhysical
        {
            get => m_physical;
            set => m_physical = value;
        }

        public override bool WaitsBeforeCrossingBack => m_owner.Waits;

        public override void HoldStill() => Calls.Add("hold");

        public override void KeepInsideBorder(bool keep) => Keeps.Add(keep);

        public override void ReleaseHold() => Calls.Add("release");

        public override void ReleaseHold(float heldSeconds)
        {
            if (!m_owner.MovesOn)
            {
                ReleaseHold();
                return;
            }

            Calls.Add($"release {heldSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
            Moved = Velocity * heldSeconds;
            Position += Moved;
        }
    }

    private sealed class WaitingPhysics : BasicScene
    {
        private readonly List<WaitingPrim> m_prims = new();

        public bool MovesOn { get; set; }
        public bool Waits { get; set; }

        public WaitingPrim PrimOf(UUID objectId)
        {
            return m_prims.FirstOrDefault(p => p.Name.EndsWith(objectId.ToString(), StringComparison.Ordinal))
                ?? m_prims.LastOrDefault();
        }

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position, Vector3 size,
            Quaternion rotation, bool isPhysical, uint localid)
        {
            WaitingPrim prim = new(this, primName, localid, position, size, rotation, pbs) { IsPhysical = isPhysical };
            m_prims.Add(prim);
            return prim;
        }
    }
}
