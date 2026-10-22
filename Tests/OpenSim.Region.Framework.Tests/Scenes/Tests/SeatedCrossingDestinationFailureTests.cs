/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A vehicle with a seated driver crosses, and the second region fails after it has taken the vehicle in: at the step
/// where the vehicle is added (the crossing fails, and the region gives the half-taken vehicle up), at the step where
/// its scripts start (the scripts are lost for the moment, the crossing is not), or after the answer was lost on the
/// way (the sending region asks for the copy back). Whatever the failure, exactly one copy is left and the driver is
/// seated on it, in the same region; no part falls out of the world, and a retry crosses.
/// </summary>
public class SeatedCrossingDestinationFailureTests : HeldCrossingFixture
{
    // coordinates no other test class uses: the grid data is shared between classes
    private void Setup(uint x) => SetupTwoRegions(x, x);

    private int Copies() => (m_sceneA.GetSceneObjectGroup(m_platform.UUID) is null ? 0 : 1)
                            + (m_sceneB.GetSceneObjectGroup(m_platform.UUID) is null ? 0 : 1);

    private void AssertNothingFell()
    {
        foreach (Scene scene in new Scene[] { m_sceneA, m_sceneB })
        {
            SceneObjectGroup group = scene.GetSceneObjectGroup(m_platform.UUID);
            if (group is null)
                continue;
            foreach (SceneObjectPart part in group.Parts)
                Assert.True(part.GroupPosition.Z > -50f, $"a part is at z={part.GroupPosition.Z} in {scene.Name}");
        }
    }

    /// <summary>Makes the step fail the first time it is reached; the result says whether it was reached.</summary>
    private static Func<bool> FailOnce(Scene scene, string step, Action onFail = null)
    {
        bool thrown = false;
        scene.CrossingStepHook = (sog, label) =>
        {
            if (label == step && !thrown)
            {
                thrown = true;
                onFail?.Invoke();
                throw new InvalidOperationException("injected failure at " + label);
            }
        };
        return () => thrown;
    }

    private void AssertSeatedHere(ScenePresence sitter, Scene scene)
    {
        ScenePresence there = scene.GetScenePresence(sitter.UUID);
        Assert.NotNull(there);
        Assert.False(there.IsChildAgent);
        Assert.True(there.IsSatOnObject, "the driver is not seated");
        Assert.Equal(scene.GetSceneObjectGroup(m_platform.UUID), there.ParentPart?.ParentGroup);
    }

    [Fact]
    public void When_the_destination_fails_after_adding_the_vehicle_the_driver_stays_seated_on_the_one_copy_here()
    {
        TestHelpers.InMethod();
        Setup(5400);
        ScenePresence sitter = AddSeatedRider(0x11);
        bool addedAtFailure = false;
        FailOnce(m_sceneB, "destination-add", () => addedAtFailure = m_sceneB.GetSceneObjectGroup(m_platform.UUID) is not null);

        Cross(sitter);

        Assert.True(addedAtFailure, "the destination had not taken the vehicle in when it failed");
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.Equal(1, Copies());
        Assert.False(m_platform.inTransit);
        Assert.False(m_platform.HiddenForCrossing);
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        AssertSeatedHere(sitter, m_sceneA);
        Assert.False(sitter.IsInTransit);
        AssertNothingFell();
    }

    [Fact]
    public void After_the_destination_gave_the_half_taken_vehicle_up_a_retry_crosses_with_the_driver_seated()
    {
        TestHelpers.InMethod();
        Setup(5410);
        ScenePresence sitter = AddSeatedRider(0x11);
        Func<bool> failed = FailOnce(m_sceneB, "destination-add");

        Cross(sitter);
        Assert.True(failed(), "the step was never reached");
        Assert.Equal(1, Copies());
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));

        Cross(sitter);

        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Arrive(sitter.UUID);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Equal(1, Copies());
        AssertSeatedHere(sitter, m_sceneB);
        AssertNothingFell();
    }

    [Fact]
    public void When_the_scripts_cannot_start_in_the_destination_the_vehicle_still_crosses_and_the_driver_arrives_seated()
    {
        TestHelpers.InMethod();
        Setup(5420);
        ScenePresence sitter = AddSeatedRider(0x11);
        Func<bool> failed = FailOnce(m_sceneB, "script-start");

        Cross(sitter);

        Assert.True(failed(), "the step was never reached");
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.True(m_sceneB.HoldsArrival(m_platform.UUID));
        Arrive(sitter.UUID);

        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        Assert.Equal(1, Copies());
        AssertSeatedHere(sitter, m_sceneB);
        AssertNothingFell();
    }

    [Fact]
    public void When_the_answer_is_lost_after_the_destination_took_the_vehicle_the_copy_is_asked_back_and_the_driver_stays_seated_here()
    {
        TestHelpers.InMethod();
        Setup(5430);
        ScenePresence sitter = AddSeatedRider(0x11);
        m_connector.ThrowAfterObject = true;

        Cross(sitter);

        Assert.Contains(m_platform.UUID, m_connector.Recalled);
        Assert.Null(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_sceneB.HoldsArrival(m_platform.UUID));
        Assert.Equal(1, Copies());
        Assert.False(m_platform.inTransit);
        Assert.False(m_sceneA.HasPendingCrossing(m_platform.UUID));
        AssertSeatedHere(sitter, m_sceneA);
        AssertNothingFell();

        // and the retry, once the connection is back, crosses
        m_connector.ThrowAfterObject = false;
        Cross(sitter);
        Assert.NotNull(m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Arrive(sitter.UUID);
        Assert.Equal(1, Copies());
        AssertSeatedHere(sitter, m_sceneB);
    }

    [Fact]
    public void A_copy_an_earlier_try_left_in_the_destination_is_replaced_not_met_again_by_the_retry()
    {
        TestHelpers.InMethod();
        Setup(5440);
        ScenePresence sitter = AddSeatedRider(0x11);

        // the leftover: the same vehicle, held in the destination for a rider that has not arrived
        SceneObjectGroup stale = (SceneObjectGroup)m_platform.CloneForNewScene();
        Assert.True(m_sceneB.IncomingCreateObject(new Vector3(128, 255, 2), stale,
            new CrossingHoldRequest { Source = new GridRegion(m_sceneA.RegionInfo), ExpectedRiders = new List<UUID> { sitter.UUID } }));
        Assert.Same(stale, m_sceneB.GetSceneObjectGroup(m_platform.UUID));

        Cross(sitter);

        SceneObjectGroup now = m_sceneB.GetSceneObjectGroup(m_platform.UUID);
        Assert.NotNull(now);
        Assert.NotSame(stale, now);
        Arrive(sitter.UUID);
        Assert.Null(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.Equal(1, Copies());
        AssertSeatedHere(sitter, m_sceneB);
    }

    [Fact]
    public void A_destination_that_already_holds_the_vehicle_for_good_refuses_it_and_starts_no_scripts_on_it()
    {
        TestHelpers.InMethod();
        Setup(5450);
        ScenePresence sitter = AddSeatedRider(0x11);

        // an object of the same id that is not a held arrival: nothing may replace it
        SceneObjectGroup other = (SceneObjectGroup)m_platform.CloneForNewScene();
        Assert.True(m_sceneB.AddSceneObject(other));
        int scriptStarts = 0;
        m_sceneB.CrossingStepHook = (sog, label) =>
        {
            if (label == "script-start")
                scriptStarts++;
        };

        Cross(sitter);

        Assert.Equal(0, scriptStarts);
        Assert.Same(other, m_sceneB.GetSceneObjectGroup(m_platform.UUID));
        Assert.NotNull(m_sceneA.GetSceneObjectGroup(m_platform.UUID));
        Assert.False(m_platform.inTransit);
        AssertSeatedHere(sitter, m_sceneA);
    }
}
