/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// [EntityTransfer] EnableStandingRiderCrossing is the one switch of the whole rider crossing feature, and it is off
/// unless a region turns it on. Off, an object crosses as it did before: nothing is tracked, nobody standing on it is
/// carried, nothing is frozen or held or hidden, and the region answers "no" to its neighbours' questions, so they treat
/// it as an older region.
/// </summary>
public class RiderCrossingSwitchTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    private bool m_firstOn;
    private bool m_secondOn;

    protected override bool FeatureOnInFirstRegion => m_firstOn;
    protected override bool FeatureOnInSecondRegion => m_secondOn;

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup(bool firstOn, bool secondOn)
    {
        m_firstOn = firstOn;
        m_secondOn = secondOn;
        SetupTwoRegions(6300, 6300);
    }

    private static ContactPoint Feet() => new(new Vector3(0, 0, 20f), new Vector3(0, 0, -1), 0f, true);

    private void AssertCrossedPlain(bool senderFreezes = false)
    {
        // the object is in the second region, running, and the first region let go of its copy at once
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        if (!senderFreezes)
            Assert.Empty(m_engineA.Suspended);
    }

    // ---- off: nothing changes for the crossing --------------------------------------------------------------------

    [Fact]
    public void The_switch_is_off_in_a_region_that_does_not_set_it()
    {
        TestHelpers.InMethod();
        Assert.False(new SceneHelpers().SetupScene().EnableStandingRiderCrossing);
    }

    [Fact]
    public void Off_the_feet_of_an_avatar_link_it_to_nothing()
    {
        TestHelpers.InMethod();
        Setup(false, false);
        ScenePresence sp = AddAvatar(0x11, Start + StandingOffset);

        Dictionary<uint, ContactPoint> contacts = new() { { m_platform.RootPart.LocalId, Feet() } };
        sp.UpdateSoftLink(contacts, 0);
        sp.UpdateSoftLink(contacts, RideOnLimits.StartMs + 100);

        Assert.False(sp.RideOn.IsLinked);
    }

    [Fact]
    public void Off_an_avatar_standing_on_a_crossing_object_is_not_carried_and_nothing_is_held()
    {
        TestHelpers.InMethod();
        Setup(false, false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        AssertCrossedPlain();
        Assert.False(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.False(rider.IsInTransit);
        Assert.NotNull(rider.PhysicsActor);
        Assert.True(m_sceneB.GetScenePresence(rider.UUID)?.IsChildAgent ?? true);
    }

    [Fact]
    public void Off_a_seated_rider_crosses_as_before_with_nothing_frozen_held_or_hidden()
    {
        TestHelpers.InMethod();
        Setup(false, false);
        ScenePresence sitter = AddSeatedRider(0x12);
        TaskInventoryItem worn = Wear(sitter);

        Cross(sitter);

        Assert.True(m_sceneA.GetScenePresence(sitter.UUID).IsChildAgent);
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.DoesNotContain(m_platformScript.ItemID, m_engineA.Suspended);
        Assert.DoesNotContain(worn.ItemID, m_engineA.Suspended);
        Assert.Empty(m_connector.Completed);
    }

    [Fact]
    public void Off_a_region_answers_no_to_the_carried_rider_and_held_crossing_questions()
    {
        TestHelpers.InMethod();
        Setup(true, false);
        GridRegion second = new(m_sceneB.RegionInfo);
        EntityTransferContext ctx = new();

        bool allowed = m_connector.QueryAccess(second, TestHelpers.ParseTail(0x11), null, false, new Vector3(128, 5, 30),
            new List<UUID>(), ctx, out _);

        Assert.True(allowed);
        Assert.False(ctx.CarriedRiders);
        Assert.False(m_connector.SupportsHeldCrossing(second));
        Assert.False(m_sceneA.EntityTransferModule.SupportsHeldCrossing(second));
    }

    [Fact]
    public void On_a_region_answers_yes_to_both_questions()
    {
        TestHelpers.InMethod();
        Setup(true, true);
        GridRegion second = new(m_sceneB.RegionInfo);
        EntityTransferContext ctx = new();

        m_connector.QueryAccess(second, TestHelpers.ParseTail(0x11), null, false, new Vector3(128, 5, 30),
            new List<UUID>(), ctx, out _);

        Assert.True(ctx.CarriedRiders);
        Assert.True(m_connector.SupportsHeldCrossing(second));
        Assert.True(m_sceneA.EntityTransferModule.SupportsHeldCrossing(second));
    }

    // ---- one region on, the other off: the one that is on treats the other as an older region -----------------------

    [Fact]
    public void A_region_with_it_on_does_not_carry_a_rider_to_a_neighbour_with_it_off()
    {
        TestHelpers.InMethod();
        Setup(true, false);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        List<string> alerts = new();
        ((TestClient)rider.ControllingClient).OnReceivedAlertMessage += alerts.Add;

        Cross(rider);

        AssertCrossedPlain(senderFreezes: true);
        Assert.False(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.False(rider.IsInTransit);
        Assert.NotNull(rider.PhysicsActor);
        Assert.False(rider.RideOn.IsLinked);
        Assert.Single(alerts);
    }

    [Fact]
    public void A_region_with_it_off_does_not_carry_a_rider_to_a_neighbour_with_it_on()
    {
        TestHelpers.InMethod();
        Setup(false, true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        AssertCrossedPlain();
        Assert.False(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.False(rider.IsInTransit);
    }

    [Fact]
    public void A_region_with_it_on_and_a_neighbour_that_is_on_carries_the_rider_and_holds_the_object()
    {
        TestHelpers.InMethod();
        Setup(true, true);
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);

        Assert.True(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
    }
}
