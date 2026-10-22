/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// A physical object's move-to target and hover cross a region border in the saved object (CrossingTargets, written by core as it
// starts a crossing for an actor that asks), and the Jolt actor of the neighbour takes them up on arrival, so the motion goes on:
// the object keeps arriving at a point that was named in the other region's coordinates and does not fall.
//
// Halcyon keeps the move target, the hover and the look-at in the actor's saved properties and wraps the targets by the region
// size when the object is suspended for a crossing (PhysxPrim.cs, SuspendPhysicsSync); the Second Life pages for llMoveToTarget,
// llSetHoverHeight and llRotLookAt say nothing of a crossing. So the actor asks for the carry under
// [Jolt] BasicForcesBehaviour = InWorldz, and under SecondLife the targets end at the border as before.
//
// The arrival here is made the way a remote crossing makes it: the object is written to XML with the targets in its root part and
// read back into the neighbour's scene. Core's own crossing, with a stand-in engine, is in CrossingTargetsTests (Framework.Tests).
// Serial with the other native tests: every scene steps a real backend on the shared job pool.

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class CrossingTargetsArrivalTests
{
    private readonly ITestOutputHelper _out;
    public CrossingTargetsArrivalTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Owner = UUID.Random();

    // 15 m ahead of the object, in the coordinates of the region it arrives in
    private static readonly Vector3 Start = new(128f, 250f, 40f);
    private static readonly Vector3 Target = new(128f, 236f, 40f);

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

    // The object as the first region saves it when it starts the crossing: physical, with the targets in the root part.
    private static string Saved(Action<CrossingTargets> set)
    {
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, Owner, "crosser", 0x40);
        so.RootPart.Shape = PrimitiveBaseShape.CreateBox();
        so.RootPart.Scale = new Vector3(1f, 1f, 1f);
        so.AbsolutePosition = Start;
        so.RootPart.Flags |= PrimFlags.Physics;
        if (set != null)
        {
            so.RootPart.CrossingTargets = new CrossingTargets();
            set(so.RootPart.CrossingTargets);
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
    public void A_move_to_target_that_crossed_goes_on_arriving_in_the_next_region()
    {
        using var r = new Region("InWorldz");
        SceneObjectGroup so = Arrive(r, Saved(t => { t.MoveTarget = Target; t.MoveTau = 0.5f; }));
        Assert.Null(so.RootPart.CrossingTargets);

        Step(r.Jolt, 8f);

        Vector3 at = so.RootPart.PhysActor.Position;
        _out.WriteLine($"started {Start}, target {Target}, after 8 s at {at}");
        Assert.True((at - Target).Length() < 0.5f, $"the object ended at {at}, {(at - Target).Length():0.00} m from the target");
    }

    [Fact]
    public void An_object_that_arrives_with_no_target_falls_where_it_would_have_gone_on()
    {
        using var r = new Region("InWorldz");
        SceneObjectGroup so = Arrive(r, Saved(null));

        Step(r.Jolt, 8f);

        Vector3 at = so.RootPart.PhysActor.Position;
        _out.WriteLine($"no targets: after 8 s at {at}");
        Assert.True(at.Z < Target.Z - 10f, $"the control did not fall: {at}");
    }

    [Fact]
    public void A_hover_that_crossed_goes_on_holding_the_object_up()
    {
        using var r = new Region("InWorldz");
        float hover = 20f;
        SceneObjectGroup so = Arrive(r, Saved(t =>
        {
            t.HoverHeight = hover; t.HoverType = (int)PIDHoverType.Ground; t.HoverTau = 0.5f; t.HoverRepel = false;
        }));

        Step(r.Jolt, 8f);

        float above = so.RootPart.PhysActor.Position.Z - Ground;
        _out.WriteLine($"hover {hover} above the ground: after 8 s {above:0.00} m above it");
        Assert.True(Math.Abs(above - hover) < 1f, $"the object hovers {above:0.00} m above the ground, asked for {hover}");
    }

    [Theory]
    [InlineData("InWorldz", true)]
    [InlineData("SecondLife", false)]
    public void The_actor_asks_for_the_carry_under_InWorldz_basic_forces_only(string behaviour, bool asks)
    {
        using var r = new Region(behaviour);
        SceneObjectGroup so = Arrive(r, Saved(null));
        Assert.Equal(asks, so.RootPart.PhysActor.CarriesTargetsAcrossCrossing);
    }
}
