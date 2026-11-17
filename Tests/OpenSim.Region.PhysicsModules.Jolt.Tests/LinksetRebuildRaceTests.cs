/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A part linked to a physical linkset while the step thread is rebuilding that linkset's compound. Core links the parts of
/// an object that turns physical one at a time on its own thread (SceneObjectGroup's physics actor setup calls link() for
/// each child), while the physics step rebuilds a dirty root's compound once per frame (JoltScene.DrainDirtyLinksets). The
/// rebuild sized its list of sub-shapes from the root's children and then walked the live list, so a part linked in between
/// ran past the end of the array: an IndexOutOfRangeException, caught and logged, that left the parts without bodies and the
/// root without its compound for that frame. The scene's rebuild hook links the extra part at exactly that point, so the
/// interleaving is the same on every run. Serial with the other native tests (the native engine is process-wide).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class LinksetRebuildRaceTests
{
    private static uint _id = 48100;

    private static JoltPrim Box(JoltScene scene, Vector3 at)
        => (JoltPrim)scene.AddPrimShape("part", PrimitiveBaseShape.CreateBox(), at, new Vector3(0.5f, 0.5f, 0.5f), Quaternion.Identity, true, _id++);

    private sealed record Linkset(JoltScene Scene, JoltPrim Root, JoltPrim[] Parts, JoltPrim Late);

    // A physical box root with two parts linked, and a third part made but not linked yet: the scene links it from inside
    // the rebuild of the first two, once.
    private static Linkset Build()
    {
        JoltScene scene = NewScene(Scratch());
        var at = new Vector3(CentreX, CentreY, 60f);
        JoltPrim root = Box(scene, at);
        JoltPrim a = Box(scene, at + new Vector3(0.5f, 0f, 0f));
        JoltPrim b = Box(scene, at + new Vector3(-0.5f, 0f, 0f));
        JoltPrim late = Box(scene, at + new Vector3(0f, 0.5f, 0f));
        a.link(root);
        b.link(root);
        bool done = false;
        scene.LinksetRebuildChildHook = r =>
        {
            if (done || !ReferenceEquals(r, root)) return;
            done = true;
            late.link(root);
        };
        return new Linkset(scene, root, new[] { a, b }, late);
    }

    [Fact]
    public void A_part_linked_during_the_rebuild_does_not_fail_the_rebuild()
    {
        Linkset s = Build();
        try
        {
            Step(s.Scene, 1f / 11f);
            Assert.Equal(0, s.Scene.LinksetRebuildErrors);
            Assert.True(s.Root.CompoundHandle.IsValid, "the root has no compound after the frame in which a part was linked during its rebuild");
            Assert.True(s.Root.BodyHandle.IsValid, "the root has no body");
            foreach (JoltPrim p in s.Parts)
                Assert.False(p.BodyHandle.IsValid, $"part {p.LocalID} kept a body of its own: it is not welded into the root");
        }
        finally { s.Scene.Dispose(); }
    }

    [Fact]
    public void A_part_linked_during_the_rebuild_is_welded_in_on_the_next_frame()
    {
        Linkset s = Build();
        try
        {
            Step(s.Scene, 1f / 11f);
            Step(s.Scene, 1f / 11f);
            Assert.Equal(0, s.Scene.LinksetRebuildErrors);
            Assert.True(s.Root.CompoundHandle.IsValid, "the root has no compound");
            Assert.True(s.Root.BodyHandle.IsValid, "the root has no body");
            Assert.False(s.Late.BodyHandle.IsValid, "the part linked during the rebuild still has a body of its own after the next frame");
            foreach (JoltPrim p in s.Parts)
                Assert.False(p.BodyHandle.IsValid, $"part {p.LocalID} has a body of its own");
        }
        finally { s.Scene.Dispose(); }
    }
}
