/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A region with no entity transfer module has nowhere to send an object: a crossing there leaves the object where it
/// is, as it always did, and does not treat the missing module as a border with no region beyond (which would delete an
/// object that dies at a void edge).
/// </summary>
public class CrossingWithoutTransferModuleTests : OpenSimTestCase
{
    [Fact]
    public void AnObjectThatDiesAtAVoidEdgeSurvivesACrossingInARegionWithNoTransferModule()
    {
        TestHelpers.InMethod();

        SceneHelpers sh = new();
        TestScene scene = sh.SetupScene("Example Region", UUID.Random(), 1000, 1000);
        Assert.Null(scene.RequestModuleInterface<OpenSim.Region.Framework.Interfaces.IEntityTransferModule>());

        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, 1, UUID.Random(), "Example Rover", 0x31);
        so.AbsolutePosition = new Vector3(128, 255.9f, 20);
        so.DieAtVoidEdge = true;

        object gate = new();
        bool ended = false;
        scene.EventManager.OnGroupEndInTransit += (g, crossed) => { lock (gate) ended = true; };

        so.AbsolutePosition = new Vector3(128, 260, 20);

        DateTime until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            lock (gate)
                if (ended)
                    break;
            Thread.Sleep(50);
        }

        lock (gate)
            Assert.True(ended);
        Assert.False(so.IsDeleted);
        Assert.NotNull(scene.GetSceneObjectGroup(so.UUID));
    }
}
