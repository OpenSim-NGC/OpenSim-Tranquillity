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
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.PrimHullProbeTests;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A physical concave prim of shape type PRIM weighs the real volume of its shape, the volume of the mesher's closed mesh,
/// times its density; its cells give the collision shape, the centre of mass and the way the mass is spread. Where the
/// mesh volume cannot be trusted (a mesh that encloses no volume or winds inside out, one whose surface has a hole, one
/// whose triangles cross each other, or a volume above the cells') the cells' volume is the weight. Each result is a
/// line "PRIM-MASS ..." in the test output. Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PrimRealVolumeTests
{
    private readonly ITestOutputHelper _out;
    public PrimRealVolumeTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Lod = 32f;
    private const byte Prim = (byte)PhysShapeType.prim;
    private const byte Convex = (byte)PhysShapeType.convex;

    private static string F(double v, string f = "0.000") => v.ToString(f, CultureInfo.InvariantCulture);

    private static Spec Named(string name) => Shapes().Single(s => s.Name == name);

    private static JoltPrim Add(JoltScene scene, PrimitiveBaseShape pbs, Vector3 size, Vector3 at, bool physical, byte shapeType = Prim)
        => (JoltPrim)scene.AddPrimShape("prim", pbs, at, size, Quaternion.Identity, physical, false, shapeType, _id++);

    private static readonly Vector3 Centre = new(CentreX, CentreY, Ground + 3f);

    // Where the next prim goes: far enough from the others that they never touch.
    private static Vector3 Spot(int n) => Centre + new Vector3(8f * n, 0f, 0f);

    /// <summary>The mesh at unit size as the engine asks the mesher for it, assessed and with its volume.</summary>
    private static (PrimHullCells.MeshAssessment assessment, double meshVolume, PrimHullCellSet cells) Measure(Meshmerizer mesher, JoltScene scene, Spec spec)
    {
        PrimitiveBaseShape pbs = spec.Build();
        IMesh mesh = mesher.CreateMesh(spec.Name, pbs, Vector3.One, Lod, true, false, false);
        Assert.NotNull(mesh);
        float[] v = mesh.getVertexListAsFloat();
        int[] i = mesh.getIndexListAsInt();
        Assert.True(mesher.TryGetPrimProfileRings(spec.Name, pbs, Vector3.One, Lod, out PrimProfileRings rings), spec.Name);
        double meshVolume = PrimHullCells.MeshVolume(v, i);
        return (PrimHullCells.AssessMesh(v, i), meshVolume, PrimHullCells.Build(scene.Backend, rings, meshVolume));
    }

    // ------------------------------------------------------------------ 1: the thin shapes weigh what they are

    // Named as the cell tests name them: hollow 0.95 is a wall of a twentieth of the prim.
    private static IEnumerable<Spec> Thin()
    {
        yield return new Spec { Name = "thin-walled torus (hollow 0.95)", Profile = ProfileShape.Circle, Hollow = 0.95f, Circular = true, ScaleX = 100, ScaleY = 175 };
        yield return new Spec { Name = "thin-walled sphere (hollow 0.95)", Profile = ProfileShape.HalfCircle, Hollow = 0.95f, Circular = true, ScaleX = 100, ScaleY = 100 };
        yield return new Spec { Name = "thin-walled tube (hollow 0.95)", Profile = ProfileShape.Square, Hollow = 0.95f, Circular = true, ScaleX = 100, ScaleY = 175 };
    }

    [Fact]
    public void The_thin_shapes_weigh_their_real_volume_and_no_longer_their_cells()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            JoltPrim reference = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Spot(0), true);
            float perCubicMetre = reference.Mass;             // a unit box: its mass is the density
            int n = 1;
            foreach (Spec spec in Thin())
            {
                var (assessment, meshVolume, cells) = Measure(mesher, scene, spec);
                Assert.True(assessment.Trusted, $"{spec.Name}: {assessment.Reason}");
                Assert.True(cells.Success, spec.Name);

                JoltPrim prim = Add(scene, spec.Build(), Vector3.One, Spot(n++), true);
                double weighed = prim.Mass / perCubicMetre;
                // the same prim at a size a builder uses: the volume is the unit one times the sides
                Vector3 size = new(2f, 3f, 1.5f);
                JoltPrim sized = Add(scene, spec.Build(), size, Spot(n++), true);
                double weighedSized = sized.Mass / perCubicMetre;

                Say($"PRIM-MASS thin \"{spec.Name}\": cells={cells.CellCount} mesh_volume={F(meshVolume)} " +
                    $"BEFORE (cells' volume)={F(cells.CellsVolume)} x{F(cells.CellsVolume / meshVolume, "0.00")} " +
                    $"AFTER weighs={F(weighed)} x{F(weighed / meshVolume, "0.000")} | at 2 x 3 x 1.5: weighs={F(weighedSized)} against {F(meshVolume * 9)}");

                Assert.True(cells.CellsVolume / meshVolume > 1.4, $"{spec.Name}: the cells were not heavier than the shape");
                Assert.InRange(weighed / meshVolume, 0.98, 1.02);
                Assert.InRange(weighedSized / (meshVolume * 9.0), 0.98, 1.02);
                Assert.DoesNotContain(scene.HullNotices, h => h.Contains("mesh volume"));
            }
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_hollow_box_still_weighs_075_of_its_solid_twin()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            JoltPrim hollow = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, Spot(0), true);
            JoltPrim solid = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Spot(1), true);
            JoltPrim asHull = Add(scene, Named("hollow box 0.5").Build(), Vector3.One, Spot(2), true, Convex);
            Say($"PRIM-MASS hollow box 0.5: {hollow.Mass / solid.Mass:0.0000} of its solid twin; CONVEX keeps {asHull.Mass / solid.Mass:0.0000}");
            Assert.Equal(0.75f, hollow.Mass / solid.Mass, 3);
            Assert.Equal(1.0f, asHull.Mass / solid.Mass, 3);        // CONVEX is unchanged: its hull is the filled box
            Assert.Equal(hollow.Mass, hollow.BodyMass, 3);          // llGetMass and the body read the same number
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 2: where the mesh volume cannot be trusted

    [Fact]
    public void A_self_crossing_twisted_prim_weighs_its_cells()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            JoltPrim reference = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Spot(0), true);
            float perCubicMetre = reference.Mass;
            // a thin-walled box twisted a quarter turn and more: its twisted surfaces pass through each other, so the mesh's
            // volume (0.007 of a unit box for a cells' volume of 0.36) is not the prim's
            var specs = new[]
            {
                new Spec { Name = "twisted thin box 90", Profile = ProfileShape.Square, Hollow = 0.95f, Twist = 50 },
                new Spec { Name = "twisted thin box 180", Profile = ProfileShape.Square, Hollow = 0.95f, Twist = 100 },
            };
            int n = 1;
            foreach (Spec spec in specs)
            {
                var (assessment, meshVolume, cells) = Measure(mesher, scene, spec);
                JoltPrim prim = Add(scene, spec.Build(), Vector3.One, Spot(n++), true);
                double weighed = prim.Mass / perCubicMetre;
                Say($"PRIM-MASS self-crossing \"{spec.Name}\": mesh check={assessment.Reason ?? "trusted"} mesh_volume={F(meshVolume)} cells_volume={F(cells.CellsVolume)} weighs={F(weighed)}");
                Assert.Equal("self-crossing", assessment.Reason);
                Assert.Equal(cells.CellsVolume, weighed, 2);
                Assert.True(weighed > meshVolume * 5, "the prim must not weigh the crossing mesh's volume");
            }
            // logged once for the reason, not once for each prim
            string[] notices = scene.HullNotices.Where(h => h.Contains("crosses itself")).ToArray();
            Say($"PRIM-MASS self-crossing notices: {notices.Length}");
            Assert.Single(notices);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_mesh_that_winds_inside_out_weighs_its_cells_and_is_named_once()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            JoltPrim reference = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Spot(0), true);
            // a square hole in a round profile, nearly the width of the prim: the mesher builds its hole inside out
            Spec spec = new Spec { Name = "round with a square hole", Profile = ProfileShape.Circle, Hole = HollowShape.Square, Hollow = 0.95f };
            var (assessment, meshVolume, cells) = Measure(mesher, scene, spec);
            JoltPrim prim = Add(scene, spec.Build(), Vector3.One, Spot(1), true);
            double weighed = prim.Mass / reference.Mass;
            Say($"PRIM-MASS inside-out \"{spec.Name}\": mesh check={assessment.Reason ?? "trusted"} signed_volume={F(assessment.SignedVolume)} cells_volume={F(cells.CellsVolume)} weighs={F(weighed)}");
            Assert.Equal("empty", assessment.Reason);
            Assert.Equal(cells.CellsVolume, weighed, 2);
            Assert.Single(scene.HullNotices, h => h.Contains("winds inside out"));
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void A_mesh_volume_above_the_cells_or_an_untrusted_mesh_is_not_believed_and_logged_once_for_each_reason()
    {
        JoltScene scene = NewScene(Scratch());
        try
        {
            var trusted = new PrimHullCells.MeshAssessment { SignedVolume = 0.8 };
            Assert.Equal(0.8, scene.RealVolumeOf(trusted, 1.0), 6);
            Assert.Equal(0.0, scene.RealVolumeOf(new PrimHullCells.MeshAssessment { SignedVolume = 1.2 }, 1.0));         // the cells hold the whole solid
            Assert.Equal(0.0, scene.RealVolumeOf(new PrimHullCells.MeshAssessment { SignedVolume = 1.3 }, 1.0));
            Assert.Equal(1.0, scene.RealVolumeOf(new PrimHullCells.MeshAssessment { SignedVolume = 1.003 }, 1.0), 2);     // within the tolerance
            Assert.Equal(0.0, scene.RealVolumeOf(new PrimHullCells.MeshAssessment { SignedVolume = 0.5, Reason = "open" }, 1.0));
            Assert.Equal(0.0, scene.RealVolumeOf(new PrimHullCells.MeshAssessment { SignedVolume = 0.5, Reason = "open" }, 1.0));
            Assert.Equal(0.0, scene.RealVolumeOf(null, 1.0));
            string[] notices = scene.HullNotices;
            Say("PRIM-MASS fallback notices: " + string.Join(" | ", notices));
            Assert.Single(notices, h => h.Contains("above that of its convex cells"));
            Assert.Single(notices, h => h.Contains("has a hole"));
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 3: the cells still give the centre of mass

    [Fact]
    public void The_centre_of_mass_of_the_cells_sits_where_the_real_shapes_does()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            foreach (string name in new[] { "torus, path cut 0..0.5", "torus", "bowl (hollow sphere 0.8, dimple 0..0.5)", "tube, hollow 0.5", "ring" })
            {
                Spec spec = Named(name);
                var (assessment, _, _) = Measure(mesher, scene, spec);
                Assert.True(assessment.Trusted, name);
                IMesh mesh = mesher.CreateMesh(name, spec.Build(), Vector3.One, Lod, true, false, false);
                var points = mesh.getVertexListAsFloat();
                float side = 0f;                       // the longest side of the shape's box
                foreach (int axis in new[] { 0, 1, 2 })
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int i = axis; i < points.Length; i += 3) { lo = MathF.Min(lo, points[i]); hi = MathF.Max(hi, points[i]); }
                    side = MathF.Max(side, hi - lo);
                }
                ShapeId shape = scene.CookPrimShape(scene.Backend, spec.Build(), Vector3.One, true, out _, out string kind, out int hulls, out double massVolume, true, true, false);
                try
                {
                    Assert.StartsWith(JoltScene.PrimCellsKind, kind);
                    SVector3 cells = scene.Backend.GetShapeCenterOfMass(shape);
                    float off = (cells - assessment.Centroid).Length();
                    Say($"PRIM-MASS centre of mass \"{name}\": cells={hulls} real=({assessment.Centroid.X:0.000},{assessment.Centroid.Y:0.000},{assessment.Centroid.Z:0.000}) " +
                        $"cells'=({cells.X:0.000},{cells.Y:0.000},{cells.Z:0.000}) off={off:0.0000} m = {off / side * 100:0.00} % of the shape's {side:0.00} m");
                    Assert.InRange(off / side, 0f, 0.05f);
                    Assert.True(massVolume > 0.0);
                }
                finally { scene.Backend.ReleaseShape(shape); }
            }
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 4: a linkset

    [Fact]
    public void A_linkset_weighs_the_sum_of_its_parts_and_balances_on_their_real_weights()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Spec thin = Thin().First();                              // the thin torus: its cells are 3.7 times the real volume
            Spec box = Named("hollow box 0.5");
            JoltPrim reference = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, Spot(0), true);
            JoltPrim loneThin = Add(scene, thin.Build(), Vector3.One, Spot(1), true);
            JoltPrim loneBox = Add(scene, box.Build(), Vector3.One, Spot(2), true);

            Vector3 rootAt = Spot(4);
            JoltPrim root = Add(scene, PrimitiveBaseShape.CreateBox(), Vector3.One, rootAt, true);
            JoltPrim torus = Add(scene, thin.Build(), Vector3.One, rootAt + new Vector3(3f, 0f, 0f), true);
            JoltPrim hollow = Add(scene, box.Build(), Vector3.One, rootAt + new Vector3(-3f, 0f, 0f), true);
            torus.link(root);
            hollow.link(root);
            torus.link(root);
            hollow.link(root);
            Step(scene, 0.3f);

            float sum = reference.Mass + loneThin.Mass + loneBox.Mass;
            Say($"PRIM-MASS linkset: body={root.BodyMass:0.000} against the parts {reference.Mass:0.000} + {loneThin.Mass:0.000} + {loneBox.Mass:0.000} = {sum:0.000}");
            Assert.InRange(root.BodyMass / sum, 0.999f, 1.001f);

            // the compound's centre of mass is the weighted mean of the parts' own, by what each weighs
            ShapeId compound = root.CompoundHandle;
            Assert.True(compound.IsValid);
            SVector3 body = scene.Backend.GetShapeCenterOfMass(compound);

            SVector3 CentreOf(Spec spec)
            {
                ShapeId s = scene.CookPrimShape(scene.Backend, spec.Build(), Vector3.One, true, out _, out _, out _, out _, true, true, false);
                try { return scene.Backend.GetShapeCenterOfMass(s); }
                finally { scene.Backend.ReleaseShape(s); }
            }
            SVector3 torusCentre = CentreOf(thin) + new SVector3(3f, 0f, 0f);
            SVector3 hollowCentre = CentreOf(box) + new SVector3(-3f, 0f, 0f);
            SVector3 expected = (SVector3.Zero * reference.Mass + torusCentre * loneThin.Mass + hollowCentre * loneBox.Mass) / sum;
            // weighed by the cells' volume the thin torus would count 3.7 times as much and pull the centre by this much
            SVector3 byCells = (SVector3.Zero * reference.Mass + torusCentre * loneThin.Mass * 3.7f + hollowCentre * loneBox.Mass)
                               / (reference.Mass + loneThin.Mass * 3.7f + loneBox.Mass);
            Say($"PRIM-MASS linkset centre of mass: compound=({body.X:0.000},{body.Y:0.000},{body.Z:0.000}) expected by real weights=({expected.X:0.000},{expected.Y:0.000},{expected.Z:0.000}) " +
                $"(by the cells' volume it would be x={byCells.X:0.000})");
            Assert.InRange(body.X, expected.X - 0.02f, expected.X + 0.02f);
            Assert.InRange(body.Y, expected.Y - 0.02f, expected.Y + 0.02f);
            Assert.InRange(body.Z, expected.Z - 0.02f, expected.Z + 0.02f);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ 5: how often the mesh volume is not believed

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Over_the_probes_grids_the_real_volume_is_used_except_where_the_mesh_cannot_be_trusted(bool extreme)
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            int total = 0, oneHull = 0, real = 0;
            var fallbacks = new SortedDictionary<string, int>();
            var heavier = new List<double>();
            int untwisted = 0, untwistedFallback = 0;
            foreach (Spec spec in Grid(extreme))
            {
                PrimitiveBaseShape pbs = spec.Build();
                ShapeId shape = scene.CookPrimShape(scene.Backend, pbs, Vector3.One, true, out _, out string kind, out int hulls, out double massVolume, true, true, false);
                try
                {
                    total++;
                    if (!kind.StartsWith(JoltScene.PrimCellsKind, StringComparison.Ordinal)) { oneHull++; Assert.Equal(0.0, massVolume); continue; }
                    double cellsVolume = scene.Backend.GetShapeVolume(shape);
                    if (spec.Twist == 0) { untwisted++; if (massVolume <= 0.0) untwistedFallback++; }
                    if (massVolume > 0.0)
                    {
                        real++;
                        Assert.True(massVolume <= cellsVolume * (1.0 + JoltScene.MeshOverCellsTolerance), spec.Name);
                        heavier.Add(cellsVolume / massVolume);
                    }
                    else
                    {
                        // which of the checks said no, read again from the mesh the way the engine did
                        IMesh mesh = mesher.CreateMesh(spec.Name, pbs, Vector3.One, Lod, true, false, false);
                        var a = PrimHullCells.AssessMesh(mesh.getVertexListAsFloat(), mesh.getIndexListAsInt());
                        string reason = a.Reason ?? "over-cells";
                        fallbacks[reason] = fallbacks.GetValueOrDefault(reason) + 1;
                    }
                }
                finally { scene.Backend.ReleaseShape(shape); }
            }
            int cellsShapes = real + fallbacks.Values.Sum();
            heavier.Sort();
            double At(double q) => heavier.Count == 0 ? 0 : heavier[Math.Min(heavier.Count - 1, (int)(q * heavier.Count))];
            Say($"PRIM-MASS grid {(extreme ? "EXTREME" : "ORDINARY")}: shapes={total} one_hull_as_before={oneHull} cooked_as_cells={cellsShapes} real_volume={real} " +
                $"fell_back_to_cells_volume={fallbacks.Values.Sum()} ({(cellsShapes == 0 ? 0 : fallbacks.Values.Sum() * 100.0 / cellsShapes):0.0} % of the cell shapes) " +
                "by reason: " + string.Join(", ", fallbacks.Select(kv => $"{kv.Key}: {kv.Value}")) +
                $" | untwisted prims: {untwisted} as cells, {untwistedFallback} fell back" +
                $" | cells' volume over the real volume where real is used: median={F(At(0.5), "0.00")} p90={F(At(0.9), "0.00")} worst={F(heavier.Count == 0 ? 0 : heavier[^1], "0.00")}");
            Assert.True(total > 500);
            Assert.True(real > 0);
        }
        finally { scene.Dispose(); }
    }

    // A twist turns every cross-section about the axis without changing its area, so on a straight path a twisted prim has the
    // volume of its untwisted twin (Cavalieri). The mesher's flat faces cut a little off a twisted wall, so the twin is a
    // bound from above, not an equal. A prim the check calls self-crossing must not have the twin's volume.
    [Fact]
    public void Twisted_straight_prims_trusted_by_the_check_have_their_twins_volume_and_those_it_rejects_do_not()
    {
        string scratch = Scratch();
        Meshmerizer mesher = NewMesher(scratch);
        int trustedCount = 0, crossingCount = 0, crossingClose = 0;
        double worstTrusted = 0, bestOverTwin = 0;
        foreach (Spec spec in Grid(false).Where(s => !s.Circular && s.Twist != 0))
        {
            Spec twin = new Spec { Name = spec.Name + " twin", Profile = spec.Profile, Hole = spec.Hole, Hollow = spec.Hollow, CutBegin = spec.CutBegin, CutEnd = spec.CutEnd, PathBegin = spec.PathBegin, PathEnd = spec.PathEnd, ScaleX = spec.ScaleX, ScaleY = spec.ScaleY };
            IMesh m = mesher.CreateMesh(spec.Name, spec.Build(), Vector3.One, Lod, true, false, false);
            IMesh mt = mesher.CreateMesh(twin.Name, twin.Build(), Vector3.One, Lod, true, false, false);
            if (m == null || mt == null) continue;
            var a = PrimHullCells.AssessMesh(m.getVertexListAsFloat(), m.getIndexListAsInt());
            var at = PrimHullCells.AssessMesh(mt.getVertexListAsFloat(), mt.getIndexListAsInt());
            if (!at.Trusted) continue;
            double d = (a.SignedVolume - at.SignedVolume) / at.SignedVolume;
            if (a.Trusted)
            {
                trustedCount++;
                worstTrusted = Math.Max(worstTrusted, -d);
                bestOverTwin = Math.Max(bestOverTwin, d);
            }
            else if (a.Reason == "self-crossing")
            {
                crossingCount++;
                if (Math.Abs(d) < 0.03) crossingClose++;
            }
        }
        Say($"PRIM-MASS twin check: trusted={trustedCount} (largest shortfall against the twin {F(worstTrusted * 100, "0.0")} %, largest excess {F(bestOverTwin * 100, "0.0")} %) | self-crossing={crossingCount} of which within 3% of the twin={crossingClose}");
        Assert.True(trustedCount > 50);
        Assert.True(crossingCount > 10);
        Assert.Equal(0, crossingClose);          // no prim is rejected for a volume that is right
        Assert.True(bestOverTwin < 0.005);       // a trusted twisted mesh never has more than its twin
        Assert.True(worstTrusted < 0.20);        // and loses at most the chord of its flat faces
    }
}
