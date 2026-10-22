/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.PrimHullProbeTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The complexity limit ([Jolt] MaxPhysicalComplexity, here 5) through a real region scene running on the Jolt engine: every way an
/// object becomes physical (llSetStatus and PRIM_PHYSICS, the build tool's flag update, rez, region load), a part linked into a
/// physical object, and a shape change. An object over the limit stays non-physical (or goes non-physical), every part of it, and
/// its owner is told "Object was too complex to be made physical: (N of 5 convex hulls)" once. The physics cost of its parts adds
/// up to the same count. Each result is a line "PRIM-SCENE ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PhysicalComplexitySceneTests
{
    private readonly ITestOutputHelper _out;
    public PhysicalComplexitySceneTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const string Prefix = "Object was too complex to be made physical: ";

    /// <summary>Records the alerts an owner is sent.</summary>
    private class Alerts : DispatchProxy
    {
        public readonly List<(UUID Agent, string Text)> Sent = new();

        public static IDialogModule Create(out Alerts alerts)
        {
            IDialogModule module = Create<IDialogModule, Alerts>();
            alerts = (Alerts)(object)module;
            return module;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "SendAlertToUser" && args.Length > 0 && args[0] is UUID agent)
                lock (Sent) Sent.Add((agent, args.OfType<string>().First()));
            Type returns = method.ReturnType;
            return returns == typeof(void) || !returns.IsValueType ? null : Activator.CreateInstance(returns);
        }
    }

    private sealed class Region : IDisposable
    {
        public readonly TestScene Scene;
        public readonly JoltScene Jolt;
        public readonly Alerts Alerts;
        public readonly UUID Owner = UUID.Random();

        public Region(int limit)
        {
            Jolt = NewScene(Scratch(), ("MaxPhysicalComplexity", limit.ToString()));
            Scene = new SceneHelpers().SetupScene();
            Scene.RegisterModuleInterface(Alerts.Create(out Alerts));
            Scene.PhysicsScene = Jolt;
        }

        public void Dispose() => Jolt.Dispose();

        /// <summary>A linkset of plain boxes, not in the scene yet.</summary>
        public SceneObjectGroup Make(int parts, int tail)
        {
            SceneObjectGroup so = SceneHelpers.CreateSceneObject(parts, Owner, "p" + tail, tail);
            foreach (SceneObjectPart part in so.Parts)
                part.Shape = PrimitiveBaseShape.CreateBox();
            return so;
        }

        public SceneObjectGroup Add(int parts, int tail)
        {
            SceneObjectGroup so = Make(parts, tail);
            Scene.AddNewSceneObject(so, false);
            return so;
        }
    }

    private static PrimitiveBaseShape HollowBox() => Shapes().Single(s => s.Name == "hollow box 0.5").Build();   // 4 hulls
    private static PrimitiveBaseShape Torus() => Shapes().Single(s => s.Name == "torus").Build();               // 12 hulls

    private static int Physical(SceneObjectGroup so) => so.Parts.Count(p => p.PhysActor is JoltPrim j && j.IsPhysicalBody);

    private static void AssertRefused(Region r, SceneObjectGroup so, string detail)
    {
        Assert.False(so.UsesPhysics);
        Assert.All(so.Parts, p => Assert.Equal((PrimFlags)0, p.Flags & PrimFlags.Physics));
        Assert.Equal(0, Physical(so));
        Assert.Equal(new[] { (r.Owner, Prefix + detail) }, r.Alerts.Sent);
    }

    private static void AssertPhysical(Region r, SceneObjectGroup so)
    {
        Assert.True(so.UsesPhysics);
        Assert.Equal(so.Parts.Length, Physical(so));
        Assert.Empty(r.Alerts.Sent);
    }

    // ------------------------------------------------------------------ llSetStatus(STATUS_PHYSICS) and PRIM_PHYSICS

    [Fact]
    public void llSetStatus_makes_an_object_at_the_limit_physical_and_refuses_one_over_it()
    {
        using var r = new Region(5);
        SceneObjectGroup atLimit = r.Add(5, 0x10);
        atLimit.RootPart.ScriptSetPhysicsStatus(true);
        Say($"PRIM-SCENE llSetStatus at the limit: physical parts {Physical(atLimit)} of {atLimit.Parts.Length}, alerts {r.Alerts.Sent.Count}");
        AssertPhysical(r, atLimit);

        SceneObjectGroup over = r.Add(6, 0x20);
        over.RootPart.ScriptSetPhysicsStatus(true);
        Say($"PRIM-SCENE llSetStatus over the limit: physical parts {Physical(over)}, alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        Assert.False(over.UsesPhysics);
        Assert.Equal(0, Physical(over));
        Assert.Equal(new[] { Prefix + "(6 of 5 convex hulls)" }, r.Alerts.Sent.Select(a => a.Text));
        Assert.True(atLimit.UsesPhysics);   // the other object is as it was
    }

    [Fact]
    public void PRIM_PHYSICS_set_through_a_child_part_is_refused_the_same_way()
    {
        using var r = new Region(5);
        SceneObjectGroup over = r.Add(6, 0x30);
        SceneObjectPart child = over.Parts.First(p => p != over.RootPart);
        child.ScriptSetPhysicsStatus(true);   // what llSetPrimitiveParams PRIM_PHYSICS and llSetStatus call, on any link
        Say($"PRIM-SCENE PRIM_PHYSICS through a child: physical parts {Physical(over)}, alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        AssertRefused(r, over, "(6 of 5 convex hulls)");
    }

    [Fact]
    public void A_prim_that_is_cut_counts_its_hulls_so_two_hollow_boxes_are_over_the_limit_and_one_is_not()
    {
        using var r = new Region(5);
        SceneObjectGroup one = r.Add(1, 0x40);
        one.RootPart.Shape = HollowBox();
        one.RootPart.PhysActor.Shape = one.RootPart.Shape;
        one.ScriptSetPhysicsStatus(true);
        AssertPhysical(r, one);                // 4 of 5

        SceneObjectGroup two = r.Add(2, 0x50);
        foreach (SceneObjectPart part in two.Parts)
        {
            part.Shape = HollowBox();
            part.PhysActor.Shape = part.Shape;
        }
        two.ScriptSetPhysicsStatus(true);
        Say($"PRIM-SCENE two hollow boxes: alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        Assert.False(two.UsesPhysics);
        Assert.Equal(new[] { Prefix + "(8 of 5 convex hulls)" }, r.Alerts.Sent.Select(a => a.Text));
    }

    [Fact]
    public void A_part_of_shape_type_none_is_not_counted()
    {
        using var r = new Region(5);
        SceneObjectGroup so = r.Add(6, 0x60);
        SceneObjectPart child = so.Parts.First(p => p != so.RootPart);
        child.PhysicsShapeType = (byte)PhysShapeType.none;   // out of physics: 5 shapes are left
        so.ScriptSetPhysicsStatus(true);
        Say($"PRIM-SCENE six parts, one NONE: physical parts {Physical(so)}, alerts {r.Alerts.Sent.Count}");
        Assert.True(so.UsesPhysics);
        Assert.Empty(r.Alerts.Sent);
        Assert.Equal(5, Physical(so));
    }

    // ------------------------------------------------------------------ the build tool's flag update

    [Fact]
    public void The_build_tools_flag_update_refuses_an_object_over_the_limit()
    {
        // SceneGraph.UpdatePrimFlags (the client's "Physical" check box) calls SceneObjectGroup.UpdateFlags
        using var r = new Region(5);
        SceneObjectGroup over = r.Add(6, 0x70);
        over.UpdateFlags(true, false, false, false);
        Say($"PRIM-SCENE build tool: physical parts {Physical(over)}, alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        AssertRefused(r, over, "(6 of 5 convex hulls)");
        Assert.True(over.TakePhysicsTooComplexTold());   // so the tool does not add its own, less specific, message
    }

    // ------------------------------------------------------------------ rez and region load

    [Fact]
    public void Rezzing_a_physical_object_over_the_limit_leaves_it_non_physical()
    {
        using var r = new Region(5);
        SceneObjectGroup so = r.Make(6, 0x80);
        so.RootPart.AddFlag(PrimFlags.Physics);
        foreach (SceneObjectPart part in so.Parts)
            part.AddFlag(PrimFlags.Physics);
        r.Scene.AddNewSceneObject(so, false);
        Say($"PRIM-SCENE rez: physical parts {Physical(so)}, alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        AssertRefused(r, so, "(6 of 5 convex hulls)");
    }

    [Fact]
    public void Rezzing_a_physical_object_at_the_limit_keeps_it_physical()
    {
        using var r = new Region(5);
        SceneObjectGroup so = r.Make(5, 0x90);
        foreach (SceneObjectPart part in so.Parts)
            part.AddFlag(PrimFlags.Physics);
        r.Scene.AddNewSceneObject(so, false);
        Say($"PRIM-SCENE rez at the limit: physical parts {Physical(so)} of {so.Parts.Length}, alerts {r.Alerts.Sent.Count}");
        AssertPhysical(r, so);
    }

    [Fact]
    public void Loading_a_saved_physical_object_over_the_limit_leaves_it_non_physical()
    {
        using var r = new Region(5);
        SceneObjectGroup so = r.Make(7, 0xA0);
        foreach (SceneObjectPart part in so.Parts)
            part.AddFlag(PrimFlags.Physics);
        r.Scene.AddRestoredSceneObject(so, true, false);
        Say($"PRIM-SCENE region load: physical parts {Physical(so)}, alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        AssertRefused(r, so, "(7 of 5 convex hulls)");
    }

    // ------------------------------------------------------------------ growing while physical

    [Fact]
    public void A_part_linked_into_a_physical_object_that_takes_it_over_the_limit_makes_the_whole_object_non_physical()
    {
        using var r = new Region(5);
        SceneObjectGroup grp1 = r.Add(5, 0xB0);
        grp1.ScriptSetPhysicsStatus(true);
        AssertPhysical(r, grp1);
        SceneObjectGroup grp2 = r.Add(1, 0xC0);
        grp1.RootPart.ClearUpdateSchedule();
        grp2.RootPart.ClearUpdateSchedule();

        grp1.LinkToGroup(grp2);

        Say($"PRIM-SCENE linked a sixth part: parts {grp1.Parts.Length}, physical parts {Physical(grp1)}, alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        Assert.Equal(6, grp1.Parts.Length);
        AssertRefused(r, grp1, "(6 of 5 convex hulls)");
    }

    [Fact]
    public void A_shape_change_that_takes_a_physical_object_over_the_limit_makes_it_non_physical()
    {
        using var r = new Region(5);
        SceneObjectGroup so = r.Add(3, 0xD0);
        so.ScriptSetPhysicsStatus(true);
        AssertPhysical(r, so);

        SceneObjectPart child = so.Parts.First(p => p != so.RootPart);
        child.Shape = Torus();
        child.PhysActor.Shape = child.Shape;     // what the part does when its shape is edited (3 + 12 - 1 = 14)

        Say($"PRIM-SCENE shape edit: physical parts {Physical(so)}, alerts: {string.Join(" | ", r.Alerts.Sent.Select(a => a.Text))}");
        AssertRefused(r, so, "(14 of 5 convex hulls)");
    }

    // ------------------------------------------------------------------ no limit, and the cost

    [Fact]
    public void With_no_limit_set_a_large_object_is_made_physical_as_before()
    {
        using var r = new Region(0);
        SceneObjectGroup so = r.Add(40, 0xE0);
        so.ScriptSetPhysicsStatus(true);
        Say($"PRIM-SCENE no limit: physical parts {Physical(so)} of {so.Parts.Length}, alerts {r.Alerts.Sent.Count}");
        AssertPhysical(r, so);
    }

    [Fact]
    public void The_physics_cost_of_the_parts_adds_up_to_the_count_whether_or_not_the_object_is_physical()
    {
        using var r = new Region(0);
        SceneObjectGroup so = r.Add(4, 0xF0);
        SceneObjectPart hollow = so.Parts.First(p => p != so.RootPart);
        hollow.Shape = HollowBox();
        hollow.PhysActor.Shape = hollow.Shape;
        hollow.Scale = new Vector3(3f, 3f, 3f);   // size does not change what a physical shape count is

        SceneObjectPart none = so.Parts.Last();
        none.PhysicsShapeType = (byte)PhysShapeType.none;

        // not physical: the hollow box is still a triangle mesh (1), the others 1 each, NONE 0
        so.GetSelectedCosts(out float idle, out _, out _);
        float idleParts = so.Parts.Sum(p => p.PhysicsCost);
        so.ScriptSetPhysicsStatus(true);
        so.GetSelectedCosts(out float physical, out _, out _);
        float physicalParts = so.Parts.Sum(p => p.PhysicsCost);
        Say($"PRIM-SCENE cost: not physical {idle} (parts {idleParts}), physical {physical} (parts {physicalParts}); the hollow box part {hollow.PhysicsCost}, the NONE part {none.PhysicsCost}");

        Assert.Equal(3f, idle);                   // 3 parts with a shape, 1 each
        Assert.Equal(idle, idleParts);
        Assert.Equal(1f + 4f + 1f + 0f, physical);   // root 1, hollow box 4, a box 1, the NONE part 0
        Assert.Equal(physical, physicalParts);
        Assert.Equal(4f, hollow.PhysicsCost);
        Assert.Equal(0f, none.PhysicsCost);
    }
}
