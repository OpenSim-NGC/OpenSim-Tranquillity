/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// An object moved by keyframed motion that crosses with riders arrives with its motion set up but not running: it does
/// not start to move until the hold ends, so the riders do not see it leave without them.
/// </summary>
public class HeldKeyframedObjectTests : HeldCrossingFixture
{
    private static readonly Vector3 StandingOffset = new(0.5f, 0f, 0.1f);

    private readonly List<uint> m_movingStarts = new();

    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup()
    {
        SetupTwoRegions(6000, 6000);
        m_sceneB.EventManager.OnScriptMovingStartEvent += id => m_movingStarts.Add(id);

        // a slow loop to one side and back, as llSetKeyframedMotion makes it
        KeyframeMotion motion = new(m_platform, KeyframeMotion.PlayMode.Loop, KeyframeMotion.DataFormat.Translation);
        motion.SetKeyframes(new[]
        {
            new KeyframeMotion.Keyframe { Position = new Vector3(1f, 0f, 0f), TimeMS = 4000 },
            new KeyframeMotion.Keyframe { Position = new Vector3(-1f, 0f, 0f), TimeMS = 4000 },
        });
        m_platform.RootPart.KeyframeMotion = motion;
    }

    private static bool Field(KeyframeMotion motion, string name)
        => (bool)typeof(KeyframeMotion).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(motion)!;

    private KeyframeMotion ArrivedMotion()
    {
        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platform.UUID);
        Assert.NotNull(arrived);
        Assert.NotNull(arrived.RootPart.KeyframeMotion);
        return arrived.RootPart.KeyframeMotion;
    }

    [Fact]
    public void The_motion_of_an_arriving_object_starts_when_its_rider_has_arrived()
    {
        TestHelpers.InMethod();
        Setup();
        m_platform.RootPart.KeyframeMotion.Start();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        KeyframeMotion arrivedMotion = null;
        try
        {
            Cross(rider);

            arrivedMotion = ArrivedMotion();
            Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
            Assert.True(Field(arrivedMotion, "m_running"), "the motion was not carried over");
            Assert.True(Field(arrivedMotion, "m_timerStopped"), "the motion moves the held object");
            Assert.Empty(m_movingStarts);

            Arrive(rider.UUID);

            Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
            Assert.True(Field(arrivedMotion, "m_running"));
            Assert.False(Field(arrivedMotion, "m_timerStopped"), "the motion did not start when the hold ended");
            Assert.Single(m_movingStarts);
        }
        finally
        {
            arrivedMotion?.Stop();
        }
    }

    [Fact]
    public void The_motion_of_an_arriving_object_starts_when_the_hold_runs_out()
    {
        TestHelpers.InMethod();
        Setup();
        m_platform.RootPart.KeyframeMotion.Start();
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);
        KeyframeMotion arrivedMotion = null;
        try
        {
            Cross(rider);
            arrivedMotion = ArrivedMotion();
            Elapse(RideOnLimits.ArriveHoldMs - 1);
            Assert.True(Field(arrivedMotion, "m_timerStopped"));
            Assert.Empty(m_movingStarts);

            Elapse(2);

            Assert.False(Field(arrivedMotion, "m_timerStopped"));
            Assert.Single(m_movingStarts);
        }
        finally
        {
            arrivedMotion?.Stop();
        }
    }

    [Fact]
    public void A_motion_that_was_not_running_stays_stopped_after_the_hold()
    {
        TestHelpers.InMethod();
        Setup();   // the motion is set up but never started
        ScenePresence rider = AddStandingRider(0x11, StandingOffset);

        Cross(rider);
        Arrive(rider.UUID);

        SceneObjectGroup arrived = m_sceneB.GetSceneObjectGroup(m_platform.UUID);
        Assert.NotNull(arrived);
        Assert.Empty(m_movingStarts);
        if (arrived.RootPart.KeyframeMotion is KeyframeMotion motion)
            Assert.False(Field(motion, "m_running"));
    }
}
