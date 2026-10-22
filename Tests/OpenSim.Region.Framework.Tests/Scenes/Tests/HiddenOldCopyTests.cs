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
/// While a crossing is held, the sending region keeps its frozen copy of the object for a possible recall. The
/// viewers of that region must not see it twice (once here, once in the new region): the copy is hidden from them the
/// moment the new region accepts the object, and shown again only if the object is recalled.
/// </summary>
public class HiddenOldCopyTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    private readonly List<(string Event, uint Id)> m_seen = new();
    private TestClient m_watcherClient;
    private ScenePresence m_watcher;

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup()
    {
        SetupTwoRegions(5600, 5600);
        m_watcher = AddAvatar(0x31, new Vector3(30, 100, 25));
        m_watcherClient = (TestClient)m_watcher.ControllingClient;
        m_watcherClient.OnReceivedEntityUpdate += (entity, flags) =>
        {
            if (entity is SceneObjectPart part && part.UUID == m_platform.RootPart.UUID)
                m_seen.Add(((flags & PrimUpdateFlags.Kill) != 0 ? "kill" : (flags & PrimUpdateFlags.CancelKill) != 0 ? "show" : "update", part.LocalId));
        };
    }

    private int Count(string name) => m_seen.Count(s => s.Event == name);

    private int LastIndex(string name) => m_seen.FindLastIndex(s => s.Event == name);

    [Fact]
    public void The_old_copy_is_hidden_from_a_viewer_in_the_old_region_when_the_destination_accepts_the_object()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        bool hiddenWhenAccepted = false;
        m_sceneA.CrossingStepHook = (sog, label) =>
        {
            if (label == "object-sent")
                hiddenWhenAccepted = m_sceneB.GetSceneObjectGroup(m_platform.UUID) is not null
                    && m_platform.HiddenForCrossing && Count("kill") == 1;
        };

        Cross(rider);

        Assert.True(hiddenWhenAccepted, "the old copy was still visible when the new region had the object");
        Assert.Equal(1, Count("kill"));
        Assert.Contains(m_platform.RootPart.LocalId, m_watcherClient.ReceivedKills);
        Assert.True(m_platform.HiddenForCrossing);
        // still here on the server, for a possible recall
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneA.HasPendingCrossing(m_platform.UUID));
    }

    [Fact]
    public void The_hidden_copy_is_not_sent_again_while_the_crossing_is_held_and_not_after_it_completes()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        int killAt = LastIndex("kill");
        m_platform.ScheduleGroupForFullUpdate();
        m_platform.RootPart.ScheduleFullUpdate();
        m_sceneA.Update(4);

        Assert.Equal(killAt, LastIndex("kill"));
        Assert.Equal(m_seen.Count - 1, killAt);

        Arrive(rider.UUID);

        Assert.Equal(1, Count("kill"));
        Assert.Equal(0, Count("show"));
        Assert.Equal(m_seen.Count - 1, LastIndex("kill"));
    }

    [Fact]
    public void The_old_copy_is_shown_again_to_the_viewer_when_the_object_is_recalled()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        m_connector.RefuseAgentUpdates.Add(1);

        Cross(rider);

        Assert.Equal(1, Count("kill"));
        Assert.Equal(1, Count("show"));
        Assert.True(LastIndex("show") > LastIndex("kill"));
        Assert.False(m_platform.HiddenForCrossing);
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
    }

    [Fact]
    public void An_object_the_destination_refuses_is_never_hidden()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        m_connector.RefuseObject = true;

        Cross(rider);

        Assert.Equal(0, Count("kill"));
        Assert.False(m_platform.HiddenForCrossing);
    }

    [Fact]
    public void The_riders_themselves_are_not_sent_a_kill_of_the_object_they_are_crossing_with()
    {
        TestHelpers.InMethod();
        Setup();
        ScenePresence sitter = AddSeatedRider(0x12);
        List<uint> kills = new();
        ((TestClient)sitter.ControllingClient).OnReceivedEntityUpdate += (entity, flags) =>
        {
            if (entity is SceneObjectPart part && part.UUID == m_platform.RootPart.UUID && (flags & PrimUpdateFlags.Kill) != 0)
                kills.Add(part.LocalId);
        };

        Cross(sitter);

        Assert.Empty(kills);
        Assert.Equal(1, Count("kill"));
    }
}
