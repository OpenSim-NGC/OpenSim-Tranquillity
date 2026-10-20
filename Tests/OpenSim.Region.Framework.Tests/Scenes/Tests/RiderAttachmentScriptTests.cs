/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The scripts in the attachments of an avatar that crosses with an object start when the object's hold ends (every
/// rider has arrived, or the time ran out), not at the arrival of each avatar: a script that runs before the object
/// and the other riders are there sees a half-arrived crossing.
/// </summary>
public class RiderAttachmentScriptTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup() => SetupTwoRegions(5700, 5700);

    /// <summary>Puts an attachment with a script on the avatar as it is in the second region, before its viewer completes the arrival.</summary>
    private TaskInventoryItem WearInDestination(UUID userId, int tail)
    {
        ScenePresence arriving = m_sceneB.GetScenePresence(userId);
        Assert.NotNull(arriving);
        SceneObjectGroup worn = SceneHelpers.AddSceneObject(m_sceneB, 1, userId, "worn", tail);
        TaskInventoryItem item = TaskInventoryHelpers.AddScript(m_sceneB.AssetService, worn.RootPart, "script",
            "default { state_entry() { } }");
        arriving.AddAttachment(worn);
        return item;
    }

    [Fact]
    public void The_attachment_scripts_of_a_rider_wait_for_the_other_rider_and_start_with_the_object()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        ScenePresence rider2 = AddSeatedRider(0x12);

        Cross(rider1);
        TaskInventoryItem worn1 = WearInDestination(rider1.UUID, 0x61);
        TaskInventoryItem worn2 = WearInDestination(rider2.UUID, 0x62);

        Arrive(rider1.UUID);

        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.DoesNotContain(worn1.ItemID, m_engineB.Resumed);
        Assert.DoesNotContain(m_platformScript.ItemID, m_engineB.Resumed);

        Arrive(rider2.UUID);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(m_platformScript.ItemID, m_engineB.Resumed);
        Assert.Contains(worn1.ItemID, m_engineB.Resumed);
        Assert.Contains(worn2.ItemID, m_engineB.Resumed);
    }

    [Fact]
    public void The_attachment_scripts_start_when_the_hold_runs_out_for_a_rider_that_is_late()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider1 = AddStandingRider(0x11, StandingOffset);
        ScenePresence rider2 = AddStandingRider(0x12, new Vector3(-0.5f, 0f, 0.1f));

        Cross(rider1);
        TaskInventoryItem worn1 = WearInDestination(rider1.UUID, 0x61);
        TaskInventoryItem worn2 = WearInDestination(rider2.UUID, 0x62);

        Arrive(rider1.UUID);
        Assert.DoesNotContain(worn1.ItemID, m_engineB.Resumed);

        Elapse(RideOnLimits.ArriveHoldMs + 1);

        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Contains(worn1.ItemID, m_engineB.Resumed);

        // the rider that was late starts its scripts when it arrives, as an avatar does when nothing is held
        Assert.DoesNotContain(worn2.ItemID, m_engineB.Resumed);
        Arrive(rider2.UUID);
        Assert.Contains(worn2.ItemID, m_engineB.Resumed);
    }
}
