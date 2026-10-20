/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.BasicPhysics;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// An avatar that an object carried in outside the region is added to the physics engine with that fact, so the actor
/// has it from the moment it exists and no step of the engine can come between the two.
/// </summary>
public class AddAvatarAllowOutsideTests : OpenSimTestCase
{
    /// <summary>The basic engine, noting how each avatar was asked for.</summary>
    private sealed class Watcher : BasicScene
    {
        public readonly List<bool> Asked = new();

        public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 size, float feetOffset,
            bool isFlying, bool allowOutsideRegion)
        {
            Asked.Add(allowOutsideRegion);
            return base.AddAvatar(localID, avName, position, size, feetOffset, isFlying, allowOutsideRegion);
        }
    }

    [Fact]
    public void An_avatar_that_is_carried_is_added_to_the_engine_with_the_flag_and_an_ordinary_one_without()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        Watcher engine = new();
        scene.PhysicsScene = engine;
        SceneObjectGroup platform = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x1), "platform", 0x2);
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x10));
        engine.Asked.Clear();

        sp.RemoveFromPhysicalScene();
        sp.AddToPhysicalScene(false);
        Assert.Equal(new[] { false }, engine.Asked);
        Assert.False(sp.PhysicsActor.AllowOutsideRegion);

        sp.CarriedByGroup = platform.UUID;
        sp.RemoveFromPhysicalScene();
        sp.AddToPhysicalScene(false);
        Assert.Equal(new[] { false, true }, engine.Asked);
        Assert.True(sp.PhysicsActor.AllowOutsideRegion);
    }
}
