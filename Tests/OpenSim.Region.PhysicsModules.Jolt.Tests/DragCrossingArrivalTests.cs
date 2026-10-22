/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A mouse drag of a physical object goes on in the next region when the object crosses a border (CrossingTargets carries the point
// dragged to, the grabbed point's offset and the agent). The arrival here is made the way a remote crossing makes it: the object is
// written to XML with the drag in its root part and read back into the neighbour's scene, where the agent has a presence. The Jolt
// actor is steered to the dragged point with no gravity pulling it down, as the drag of any object here, until the viewer lets go.
//
// Halcyon moves the grab target by the region size when the object is suspended for a crossing (PhysxPrim.cs, SuspendPhysicsSync).
// Core's own crossing, with a stand-in engine, is in DragCrossingTests (Framework.Tests).
// Serial with the other native tests: every scene steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class DragCrossingArrivalTests
{
    private readonly ITestOutputHelper _out;
    public DragCrossingArrivalTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Owner = UUID.Random();
    private static readonly Vector3 Start = new(128f, 250f, 40f);
    private static readonly Vector3 DragTarget = new(128f, 236f, 40f);

    private sealed class Region : IDisposable
    {
        public readonly TestScene Scene;
        public readonly JoltScene Jolt;

        public Region(string behaviour)
        {
            Jolt = NewScene(Scratch(), ("BasicForcesBehaviour", behaviour));
            Scene = new SceneHelpers().SetupScene();
            Scene.PhysicsScene = Jolt;
        }

        public void Dispose() => Jolt.Dispose();
    }

    private static string Saved(UUID dragger)
    {
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, Owner, "dragged", 0x41);
        so.RootPart.Shape = PrimitiveBaseShape.CreateBox();
        so.RootPart.Scale = new Vector3(1f, 1f, 1f);
        so.AbsolutePosition = Start;
        so.RootPart.Flags |= PrimFlags.Physics;
        if (dragger != UUID.Zero)
        {
            so.RootPart.CrossingTargets = new CrossingTargets
            {
                DragAgent = dragger, DragAnchor = Vector3.Zero, DragTarget = DragTarget,
            };
        }
        return SceneObjectSerializer.ToOriginalXmlFormat(so);
    }

    private static SceneObjectGroup Arrive(Region region, string xml)
    {
        SceneObjectGroup so = SceneObjectSerializer.FromOriginalXmlFormat(xml);
        region.Scene.AddNewSceneObject(so, false);
        Assert.NotNull(so.RootPart.PhysActor);
        Assert.True(so.RootPart.PhysActor.IsPhysical);
        return so;
    }

    [Fact]
    public void A_drag_that_crossed_holds_the_object_at_the_dragged_point_in_the_next_region()
    {
        using var r = new Region("InWorldz");
        ScenePresence dragger = SceneHelpers.AddScenePresence(r.Scene, UUID.Random());
        SceneObjectGroup so = Arrive(r, Saved(dragger.UUID));
        Assert.Null(so.RootPart.CrossingTargets);

        Step(r.Jolt, 8f);

        Vector3 at = so.RootPart.PhysActor.Position;
        _out.WriteLine($"started {Start}, dragged to {DragTarget}, after 8 s at {at}");
        Assert.True((at - DragTarget).Length() < 0.5f, $"the object ended at {at}, {(at - DragTarget).Length():0.00} m from the dragged point");
    }

    [Fact]
    public void The_release_of_a_drag_that_crossed_lets_the_object_fall()
    {
        using var r = new Region("InWorldz");
        ScenePresence dragger = SceneHelpers.AddScenePresence(r.Scene, UUID.Random());
        SceneObjectGroup so = Arrive(r, Saved(dragger.UUID));
        Step(r.Jolt, 4f);
        Assert.True((so.RootPart.PhysActor.Position - DragTarget).Length() < 0.5f);

        so.ReleaseGrab(dragger.UUID);
        Step(r.Jolt, 4f);

        Vector3 at = so.RootPart.PhysActor.Position;
        _out.WriteLine($"released at {DragTarget}: after 4 s at {at}");
        Assert.True(at.Z < DragTarget.Z - 10f, $"the object did not fall after the release: {at}");
    }

    [Fact]
    public void An_object_that_arrives_with_no_drag_falls()
    {
        using var r = new Region("InWorldz");
        SceneObjectGroup so = Arrive(r, Saved(UUID.Zero));

        Step(r.Jolt, 8f);

        Vector3 at = so.RootPart.PhysActor.Position;
        _out.WriteLine($"no drag: after 8 s at {at}");
        Assert.True(at.Z < DragTarget.Z - 10f, $"the control did not fall: {at}");
    }

    [Fact]
    public void A_drag_of_an_agent_the_region_does_not_know_is_not_taken_up()
    {
        using var r = new Region("InWorldz");
        SceneObjectGroup so = Arrive(r, Saved(UUID.Random()));

        Step(r.Jolt, 8f);

        Vector3 at = so.RootPart.PhysActor.Position;
        _out.WriteLine($"unknown dragger: after 8 s at {at}");
        Assert.True(at.Z < DragTarget.Z - 10f, $"the object was held: {at}");
    }
}
