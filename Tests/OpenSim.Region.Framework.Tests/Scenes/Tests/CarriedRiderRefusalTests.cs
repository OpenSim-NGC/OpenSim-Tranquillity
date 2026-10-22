/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Which standing avatars an object takes across a border and which it leaves behind: a destination that cannot take
/// a carried avatar, or refuses this one, is never sent it (the avatar stays, unfrozen, and is told why); avatars that
/// are not carried (NPCs) stay; a crossing that fails puts the object back and leaves its riders linked to it.
/// </summary>
public class CarriedRiderRefusalTests : HeldCrossingFixture
{
    private static readonly Vector3 Offset1 = new(0.5f, 0f, 0.1f);
    private static readonly Vector3 Offset2 = new(-0.5f, 0f, 0.1f);

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup(bool banOwnerInDestination = false) => SetupTwoRegions(6100, 6100, banOwnerInDestination);

    private static List<string> CollectAlerts(ScenePresence sp)
    {
        List<string> alerts = new();
        ((TestClient)sp.ControllingClient).OnReceivedAlertMessage += alerts.Add;
        return alerts;
    }

    private void AssertLeftBehind(ScenePresence rider)
    {
        Assert.False(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.False(rider.IsInTransit);
        Assert.NotNull(rider.PhysicsActor);
        Assert.False(rider.RideOn.IsLinked);
        Assert.True(m_sceneB.GetScenePresence(rider.UUID)?.IsChildAgent ?? true);
    }

    // ---- the capability bit ----------------------------------------------------------------------------------------

    [Fact]
    public void A_rider_is_carried_to_a_destination_that_says_it_can_take_carried_riders()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, Offset1);

        Cross(rider);

        Assert.True(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.NotNull(m_sceneB.GetScenePresence(rider.UUID));
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
    }

    [Fact]
    public void A_rider_is_never_sent_to_a_destination_that_does_not_say_it_can_take_carried_riders()
    {
        TestHelpers.InMethod();
        Setup();
        m_connector.CarriedRidersSupported = false;
        ScenePresence rider = AddStandingRider(0x11, Offset1);
        List<string> alerts = CollectAlerts(rider);

        Cross(rider);

        // the object crosses, the rider is where it was: told, unfrozen, standing in this region on its own feet
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        AssertLeftBehind(rider);
        Assert.Single(alerts);
        Assert.False(string.IsNullOrWhiteSpace(alerts[0]));
    }

    [Fact]
    public void An_object_with_only_a_refused_rider_crosses_as_an_object_with_no_riders()
    {
        TestHelpers.InMethod();
        Setup();
        m_connector.CarriedRidersSupported = false;
        ScenePresence rider = AddStandingRider(0x11, Offset1);

        Cross(rider);

        // nobody to wait for: nothing is held, the old region lets go of its copy at once (the plain crossing)
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
    }

    [Fact]
    public void A_seated_rider_is_still_crossed_when_the_destination_cannot_take_carried_riders()
    {
        TestHelpers.InMethod();
        Setup();
        m_connector.CarriedRidersSupported = false;
        ScenePresence sitter = AddSeatedRider(0x12);

        Cross(sitter);

        // seated riders cross by the old rules, which every region has
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneA.GetScenePresence(sitter.UUID).IsChildAgent);
    }

    [Fact]
    public void The_access_answer_carries_the_capability_and_an_answer_without_it_clears_it()
    {
        EntityTransferContext asker = new();
        Assert.False(asker.CarriedRiders);

        OSDMap answer = new();
        EntityTransferContext.WriteAnswer(answer, true);
        asker.ReadAnswer(answer);
        Assert.True(asker.CarriedRiders);

        // a region with the feature off says no
        EntityTransferContext.WriteAnswer(answer, false);
        asker.ReadAnswer(answer);
        Assert.False(asker.CarriedRiders);
        EntityTransferContext.WriteAnswer(answer, true);
        asker.ReadAnswer(answer);

        // the answer of a region from before the capability
        asker.ReadAnswer(new OSDMap { ["success"] = OSD.FromBoolean(true) });
        Assert.False(asker.CarriedRiders);
    }

    // ---- refused riders --------------------------------------------------------------------------------------------

    [Fact]
    public void A_rider_the_destination_refuses_is_left_behind_with_an_alert_and_the_others_are_carried()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence banned = AddStandingRider(0x11, Offset1);
        ScenePresence carried = AddStandingRider(0x12, Offset2);
        List<string> alerts = CollectAlerts(banned);
        List<string> otherAlerts = CollectAlerts(carried);
        m_connector.RefuseAccess.Add(banned.UUID);

        Cross(carried);

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        AssertLeftBehind(banned);
        Assert.Single(alerts);
        Assert.False(string.IsNullOrWhiteSpace(alerts[0]));

        Assert.True(m_sceneA.GetScenePresence(carried.UUID).IsChildAgent);
        Assert.Empty(otherAlerts);
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Arrive(carried.UUID);
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID), "only the carried rider is expected");
    }

    // ---- NPCs ------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_NPC_standing_on_the_object_is_not_carried()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence npc = AddStandingRider(0x13, Offset1);
        ScenePresence rider = AddStandingRider(0x14, Offset2);
        typeof(ScenePresence).GetProperty(nameof(ScenePresence.IsNPC)).GetSetMethod(true).Invoke(npc, new object[] { true });
        Assert.True(npc.IsNPC);

        Cross(rider);

        Assert.True(m_sceneA.GetScenePresence(rider.UUID).IsChildAgent);
        Assert.False(m_sceneA.GetScenePresence(npc.UUID).IsChildAgent);
        Assert.False(npc.IsInTransit);
        Assert.NotNull(npc.PhysicsActor);
        Assert.True(m_sceneB.GetScenePresence(npc.UUID)?.IsChildAgent ?? true);
    }

    // ---- a crossing that fails -------------------------------------------------------------------------------------

    [Fact]
    public void An_object_the_destination_refuses_stops_inside_the_region_and_its_riders_stay_linked_to_it()
    {
        TestHelpers.InMethod();
        Setup(banOwnerInDestination: true);
        ScenePresence rider1 = AddStandingRider(0x11, Offset1);
        ScenePresence rider2 = AddStandingRider(0x12, Offset2);
        m_platform.RootPart.Velocity = new Vector3(0, -5f, 0);
        // the engine has moved the object out of the region by the time its crossing starts
        m_platform.RootPart.GroupPosition = Target;

        Cross(rider1);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.InRange(m_platform.AbsolutePosition.Y, 0.4f, 0.6f);
        Assert.Equal(Vector3.Zero, m_platform.RootPart.Velocity);
        foreach ((ScenePresence rider, Vector3 offset) in new[] { (rider1, Offset1), (rider2, Offset2) })
        {
            Assert.False(rider.IsChildAgent);
            Assert.False(rider.IsInTransit);
            Assert.NotNull(rider.PhysicsActor);
            Assert.True(rider.RideOn.IsLinked);
            Assert.Equal(m_platform.UUID, rider.RideOn.GroupID);
            Assert.InRange(Vector3.Distance(
                new Vector3(rider.AbsolutePosition.X, rider.AbsolutePosition.Y, 0),
                new Vector3(m_platform.AbsolutePosition.X + offset.X, m_platform.AbsolutePosition.Y + offset.Y, 0)), 0f, 0.1f);
        }
    }

    [Fact]
    public void A_crossing_that_is_recalled_leaves_its_riders_linked_to_the_object()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, Offset1);
        ScenePresence rider2 = AddStandingRider(0x12, Offset2);
        m_connector.RefuseAgentUpdates.Add(1);

        Cross(rider1);

        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        foreach (ScenePresence rider in new[] { rider1, rider2 })
        {
            Assert.False(rider.IsChildAgent);
            Assert.True(rider.RideOn.IsLinked);
            Assert.Equal(m_platform.UUID, rider.RideOn.GroupID);
        }
    }
}
