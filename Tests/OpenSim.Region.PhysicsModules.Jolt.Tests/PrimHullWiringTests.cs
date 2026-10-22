/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.PrimHullProbeTests;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical prim of shape type PRIM that is made from its own profile and path and is not convex (a hollow box, a torus,
/// a bowl) is cooked as a compound of convex cells; CONVEX, NONE, the plain box, sphere and cylinder, a prim that is one
/// cell, a sculpt and every non-physical prim are cooked as they were. Turning physics on or off, or changing the shape
/// type, cooks the shape again on the same prim; a welded child of a physical linkset carries its cells at its own place
/// and turn; a prim whose cells cannot be made keeps the one hull; a hollow box lets a small ball settle inside it. Each
/// result is a line "PRIM-WIRING ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PrimHullWiringTests
{
    private readonly ITestOutputHelper _out;
    public PrimHullWiringTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const byte Prim = (byte)PhysShapeType.prim;
    private const byte Convex = (byte)PhysShapeType.convex;
    private const byte None = (byte)PhysShapeType.none;

    private static Spec Named(string name) => Shapes().Single(s => s.Name == name);

    private static JoltPrim Add(JoltScene scene, PrimitiveBaseShape pbs, Vector3 size, Vector3 at, bool physical, byte shapeType = Prim,
                                Quaternion? rotation = null)
        => (JoltPrim)scene.AddPrimShape("prim", pbs, at, size, rotation ?? Quaternion.Identity, physical, false, shapeType, _id++);

    private static readonly Vector3 Centre = new(CentreX, CentreY, Ground + 3f);

    private static Vector3 Low(float height) => new(CentreX, CentreY, Ground + height * 0.5f);

    private static int Count(string kind)
    {
        const string head = JoltScene.PrimCellsKind + " x";
        if (!kind.StartsWith(head, StringComparison.Ordinal) || !kind.EndsWith(")", StringComparison.Ordinal))
            return 0;
        return int.Parse(kind.Substring(head.Length, kind.Length - head.Length - 1), CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ 1: the cells, and what is left alone

    [Theory]
    [InlineData("hollow box 0.5", 4)]
    [InlineData("torus", 12)]
    [InlineData("bowl (hollow sphere 0.8, dimple 0..0.5)", 36)]
    public void A_physical_prim_of_type_PRIM_that_is_concave_cooks_as_its_cells(string shape, int cells)
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim p = Add(scene, Named(shape).Build(), Vector3.One, Centre, true);
            Say($"PRIM-WIRING {shape}: kind={p.ShapeKind} hulls={p.HullCount}");
            Assert.Equal(cells, Count(p.ShapeKind));
            Assert.Equal(cells, p.HullCount);
            Assert.Equal(1, scene.PrimCellShapesCooked);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_hollow_physical_prim_weighs_its_cells_not_the_filled_solid()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim hollow = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, Centre, true);
            JoltPrim filled = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, Centre + new Vector3(5f, 0f, 0f), true, Convex);
            Say($"PRIM-WIRING mass: cells={hollow.Mass:0.0000} one hull={filled.Mass:0.0000} ratio={hollow.Mass / filled.Mass:0.000}");
            Assert.Equal(JoltScene.PrimCellsKind + " x4)", hollow.ShapeKind);
            Assert.Equal("hull(mesher)", filled.ShapeKind);
            // a 0.5 hollow box is 1 - 0.5^2 of its box: exactly the mesher's volume, so the ratio is 0.75
            Assert.Equal(0.75f, hollow.Mass / filled.Mass, 2);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void CONVEX_NONE_a_prim_that_is_one_cell_and_the_plain_shapes_are_cooked_as_before()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            Spec hollow = Named("hollow box 0.5");
            JoltPrim convex = Add(scene, hollow.Build(), Vector3.One, Centre, true, Convex);
            JoltPrim none = Add(scene, hollow.Build(), Vector3.One, Centre + new Vector3(4f, 0f, 0f), true, None);
            // a half cylinder is convex: one cell, the one hull as ever
            JoltPrim half = Add(scene, Named("path-cut cylinder (pie 0.25 to 0.75, 180 deg)").Build(), Vector3.One, Centre + new Vector3(8f, 0f, 0f), true);
            JoltPrim box = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Centre + new Vector3(12f, 0f, 0f), true);
            JoltPrim sphere = Add(scene, PrimitiveBaseShape.CreateSphere(), Vector3.One, Centre + new Vector3(16f, 0f, 0f), true);
            JoltPrim cylinder = Add(scene, new Spec { Name = "cylinder", Profile = ProfileShape.Circle }.Build(), Vector3.One, Centre + new Vector3(20f, 0f, 0f), true);
            Say($"PRIM-WIRING unchanged: convex={convex.ShapeKind} none={none.ShapeKind} half={half.ShapeKind} box={box.ShapeKind} sphere={sphere.ShapeKind} cylinder={cylinder.ShapeKind}");

            Assert.Equal("hull(mesher)", convex.ShapeKind);
            Assert.Equal("hull(mesher)", none.ShapeKind);
            Assert.Equal("hull(mesher)", half.ShapeKind);
            Assert.Equal("box", box.ShapeKind);
            Assert.Equal("sphere", sphere.ShapeKind);
            Assert.Equal("cylinder", cylinder.ShapeKind);
            foreach (JoltPrim p in new[] { convex, none, half, box, sphere, cylinder })
                Assert.Equal(1, p.HullCount);
            Assert.Equal(0, scene.PrimCellShapesCooked);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_non_physical_prim_is_cooked_as_before_and_reads_the_mass_it_has_when_physical()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim p = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, Low(1f), false);
            Assert.Equal("mesh(mesher)", p.ShapeKind);
            Assert.Equal(1, p.HullCount);
            Assert.Equal(0, scene.PrimCellShapesCooked);
            float still = p.Mass;      // cooks the shape it would have as a physical prim, only to read its volume
            Assert.Equal("mesh(mesher)", p.ShapeKind);

            p.IsPhysical = true;
            Assert.Equal(JoltScene.PrimCellsKind + " x4)", p.ShapeKind);
            float physical = p.Mass;
            Say($"PRIM-WIRING mass before and after physics on: {still:0.0000} / {physical:0.0000}");
            Assert.Equal(physical, still, 3);
        }
        finally { scene.Dispose(); }
    }

    // A sculpt is one hull (as Halcyon keeps it): the gate on cells leaves it alone, with or without physics.
    [Fact]
    public void A_physical_sculpt_is_cooked_as_before()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim p = Add(scene, SculptFlagsTests.Sculpt(SculptFlagsTests.Map(SculptType.Torus), (byte)SculptType.Torus), Vector3.One, Centre, true);
            Say($"PRIM-WIRING sculpt: kind={p.ShapeKind} hulls={p.HullCount}");
            Assert.Equal("hull(mesher)", p.ShapeKind);
            Assert.Equal(1, p.HullCount);
            Assert.Equal(0, scene.PrimCellShapesCooked);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 2: changes on the same prim

    [Fact]
    public void Turning_physics_on_rebuilds_the_shape_as_cells_and_turning_it_off_rebuilds_the_static_shape()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim p = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, Low(1f), false);
            Assert.Equal("mesh(mesher)", p.ShapeKind);

            p.IsPhysical = true;
            Assert.Equal(JoltScene.PrimCellsKind + " x4)", p.ShapeKind);
            Assert.Equal(4, p.HullCount);
            Step(scene, 0.5f);

            p.IsPhysical = false;
            Assert.Equal("mesh(mesher)", p.ShapeKind);
            Assert.Equal(1, p.HullCount);

            p.IsPhysical = true;
            Assert.Equal(4, p.HullCount);

            // and the shape type: CONVEX is the one hull, PRIM the cells again, on the same prim
            p.PhysicsShapeType = Convex;
            Assert.Equal("hull(mesher)", p.ShapeKind);
            Assert.Equal(1, p.HullCount);
            p.PhysicsShapeType = Prim;
            Assert.Equal(JoltScene.PrimCellsKind + " x4)", p.ShapeKind);
            Step(scene, 0.5f);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_resize_cooks_the_cells_again_at_the_new_size()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim p = Add(scene, Named("torus").Build(), Vector3.One, Centre, true);
            float small = p.Mass;
            p.Size = new Vector3(2f, 2f, 2f);
            Say($"PRIM-WIRING resize: kind={p.ShapeKind} mass {small:0.00} -> {p.Mass:0.00}");
            Assert.Equal(12, p.HullCount);
            Assert.Equal(8f, p.Mass / small, 1);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3: a linkset with a concave child

    // A 1 m root box resting on the ground with a hollow-box child 1.5 m east of it, turned a quarter turn about x so that
    // its hole runs along y. A ray down at the child's middle meets the child's top wall; one along y through it passes
    // the hole. Were the child's offset or turn lost, the first would pass through the hole (it runs up and down in the
    // child's own frame) and the second would meet a wall. The struck part is the child's local id, which is read through
    // the compound the child's cells make inside the root's.
    [Fact]
    public void A_linkset_child_that_is_concave_carries_its_cells_at_its_own_offset_and_turn()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Low(1f), true);
            Quaternion quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f);
            Vector3 childAt = Low(1f) + new Vector3(1.5f, 0f, 0f);
            JoltPrim child = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, childAt, true, Prim, quarter);
            Assert.Equal(4, child.HullCount);
            int cookedBefore = scene.PrimCellShapesCooked;
            child.link(root);
            child.link(root);
            Step(scene, 0.3f);
            // the weld, done on the step thread at the top of Simulate, cooked no cells: it joined the shapes already cooked
            Assert.Equal(cookedBefore, scene.PrimCellShapesCooked);

            IPhysicsBackend backend = scene.Backend;
            float middle = Ground + 0.5f;
            float rootPlace = root.Position.X;
            // down through the child's middle: its top wall, 0.25 m thick, is struck from 0.25 m above its middle
            Assert.True(backend.RayCast(new SVector3(childAt.X, childAt.Y, Ground + 3f), -SVector3.UnitZ, 10f, QueryFilter.All, out RayHit down), "down");
            // along y through the child's middle: the hole
            bool along = backend.RayCast(new SVector3(childAt.X, childAt.Y - 3f, middle), SVector3.UnitY, 10f, QueryFilter.All, out RayHit alongHit);
            // along y through the middle of a wall: 0.375 m off the middle of the child, in x, level with its middle in z
            Assert.True(backend.RayCast(new SVector3(childAt.X + 0.375f, childAt.Y - 3f, middle), SVector3.UnitY, 10f, QueryFilter.All, out RayHit wall), "wall");
            // down through the root's middle
            Assert.True(backend.RayCast(new SVector3(rootPlace, root.Position.Y, Ground + 3f), -SVector3.UnitZ, 10f, QueryFilter.All, out RayHit rootDown), "root");

            Say($"PRIM-WIRING linkset child: down hits {down.ChildUserData} at z={down.Point.Z:0.00}; along the hole hit={along}" +
                (along ? $" {alongHit.ChildUserData}" : "") + $"; wall hits {wall.ChildUserData} at y={wall.Point.Y:0.00}; root hits {rootDown.ChildUserData}");

            Assert.Equal(child.LocalID, down.ChildUserData);
            Assert.InRange(down.Point.Z, Ground + 0.99f, Ground + 1.01f);          // the top of the child
            Assert.False(along && alongHit.ChildUserData == child.LocalID, "the ray along the hole struck the child");
            Assert.Equal(child.LocalID, wall.ChildUserData);
            Assert.InRange(wall.Point.Y, childAt.Y - 0.51f, childAt.Y - 0.49f);    // the near face of the child
            Assert.Equal(root.LocalID, rootDown.ChildUserData);

            // the linkset weighs the root plus the child's cells (0.75 of its box), not the filled child
            JoltPrim lone = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Centre + new Vector3(30f, 0f, 0f), true);
            Say($"PRIM-WIRING linkset mass: {root.BodyMass:0.000} against a lone box {lone.BodyMass:0.000}");
            Assert.Equal(1.75f, root.BodyMass / lone.BodyMass, 2);
        }
        finally { scene.Dispose(); }
    }

    // The child's shape type changed to CONVEX in the linkset: its cells go, its one hull takes their place, and the root's
    // compound is built again (so the hole is closed); back to PRIM and the hole is open again.
    [Fact]
    public void A_welded_child_changing_its_shape_type_rebuilds_the_linkset_compound()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Low(1f), true);
            Vector3 childAt = Low(1f) + new Vector3(1.5f, 0f, 0f);
            JoltPrim child = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, childAt, true);
            child.link(root);
            child.link(root);
            Step(scene, 0.3f);
            IPhysicsBackend backend = scene.Backend;
            // the hole runs up and down here: a ray down through the child's middle reaches the ground
            bool Struck()
            {
                bool hit = backend.RayCast(new SVector3(childAt.X, childAt.Y, Ground + 3f), -SVector3.UnitZ, 10f, QueryFilter.All, out RayHit h);
                return hit && h.ChildUserData == child.LocalID;
            }
            Assert.False(Struck(), "the hole is open");

            child.PhysicsShapeType = Convex;
            Step(scene, 0.3f);
            Assert.Equal(1, child.HullCount);
            Assert.True(Struck(), "one hull closes the hole");

            child.PhysicsShapeType = Prim;
            Step(scene, 0.3f);
            Assert.Equal(4, child.HullCount);
            Assert.False(Struck(), "the cells open it again");
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 4: a cell that cannot be made

    [Fact]
    public void A_prim_whose_cells_cannot_be_made_keeps_the_one_hull()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            // no thickness: every cell would be flat and the mesh has no volume to measure them against
            JoltPrim p = Add(scene, Named("hollow box 0.5").Build(), new Vector3(1f, 1f, 0f), Centre, true);
            Say($"PRIM-WIRING no cells: kind={p.ShapeKind} hulls={p.HullCount} notices={scene.HullNotices.Length}");
            Assert.StartsWith("hull", p.ShapeKind);
            Assert.Equal(1, p.HullCount);
            Assert.Equal(0, scene.PrimCellShapesCooked);
            Assert.Contains(scene.HullNotices, n => n.Contains("convex cells", StringComparison.Ordinal));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 5: the step and the cells

    // Cells are cooked by whoever adds or changes the prim, on that thread. Here one thread cooks hollow tori (and a sphere
    // with cells too) while another steps the scene: neither waits on the other for good (a deadlock would hold the step
    // for the whole 60 s), and every cook comes out as cells. The step never cooks a prim's cells itself (the weld test
    // above counts them).
    [Fact]
    public void Cells_cooked_on_one_thread_never_hold_up_the_step_on_another()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            const int cooks = 40;
            int cells = 0;
            Exception failure = null;
            var cooker = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < cooks; i++)
                    {
                        JoltPrim p = Add(scene, Named(i % 2 == 0 ? "torus" : "hollow sphere 0.5").Build(), Vector3.One,
                                         Centre + new Vector3(2f * (i % 20), 2f * (i / 20), 0f), true);
                        if (p.HullCount > 1) Interlocked.Increment(ref cells);
                    }
                }
                catch (Exception e) { failure = e; }
            });
            cooker.Start();
            int steps = 0;
            double worst = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (cooker.IsAlive && watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                scene.Simulate(Heartbeat);
                worst = Math.Max(worst, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                steps++;
            }
            Assert.True(cooker.Join(TimeSpan.FromSeconds(5)), "the cooking thread did not finish");
            Assert.Null(failure);
            Say($"PRIM-WIRING step beside cooks: {steps} steps while {cooks} prims were cooked ({cells} as cells); slowest step {worst:0.0} ms");
            Assert.Equal(cooks, cells);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 6: a ball in a hollow box

    // A 1 m hollow box (a square hole, 0.5 m, up and down through it) rests on the ground. A 0.2 m ball dropped over the
    // hole falls in and rests on the ground inside the box. With the box as the one hull it rests on top, 1 m up.
    [Theory]
    [InlineData(Prim, true)]
    [InlineData(Convex, false)]
    public void A_small_ball_dropped_into_a_physical_hollow_box_settles_inside_it(byte shapeType, bool inside)
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            Add(scene, Named("hollow box 0.5").Build(), Vector3.One, Low(1f), true, shapeType);
            Step(scene, 1f);
            PhysicsActor ball = scene.AddPrimShape("ball", PrimitiveBaseShape.CreateSphere(), new Vector3(CentreX + 0.05f, CentreY - 0.03f, Ground + 2.5f),
                                                   new Vector3(0.2f, 0.2f, 0.2f), Quaternion.Identity, true, _id++);
            Step(scene, 4f);
            float rest = ball.Position.Z - Ground;
            Say($"PRIM-WIRING ball in box (type {shapeType}): rests {rest:0.00} m above the ground; inside={inside}");
            if (inside)
                Assert.InRange(rest, 0.05f, 0.2f);            // on the ground, within the box (the ball's radius is 0.1)
            else
                Assert.InRange(rest, 1.05f, 1.2f);            // on top of the box
        }
        finally { scene.Dispose(); }
    }
}
