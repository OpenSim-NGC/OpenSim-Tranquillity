/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
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
/// The cell builder splits a concave physical prim (hollow, cut, twisted, torus, tube, ring, hollow sphere) into convex
/// cells from the mesher's profile and path rings. For each shape the probe measured, the cells must reproduce the
/// measured cell count and stay within a volume ratio (the compound's volume over the volume of the mesher's own mesh)
/// of its own; points inside a hollow must lie outside every cell; thin-walled shapes must cook; and no shape of the
/// probe's two parameter grids may come out above 64 cells. Each result is a line "PRIM-CELLS ..." in the test output.
/// Serial with the other native tests (they cook through the native hull builder).
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PrimHullCellsTests
{
    private readonly ITestOutputHelper _out;
    public PrimHullCellsTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const float Lod = 32f;

    private static string F(double v, string f = "0.000") => v.ToString(f, CultureInfo.InvariantCulture);

    /// <summary>The rings and the mesh volume of a prim at unit size, as the engine will ask for them.</summary>
    private static (PrimProfileRings rings, double meshVolume) Prepare(Meshmerizer mesher, Spec spec, Vector3? size = null)
    {
        PrimitiveBaseShape pbs = spec.Build();
        Vector3 s = size ?? Vector3.One;
        Assert.True(mesher.TryGetPrimProfileRings(spec.Name, pbs, s, Lod, out PrimProfileRings rings), spec.Name);
        IMesh mesh = mesher.CreateMesh(spec.Name, pbs, s, Lod, false, false, false);
        Assert.NotNull(mesh);
        return (rings, PrimHullCells.MeshVolume(mesh.getVertexListAsFloat(), mesh.getIndexListAsInt()));
    }

    private static Spec Named(string name) => Shapes().Single(s => s.Name == name);

    // ------------------------------------------------------------------ every shape the probe measured

    // Per shape: the cells the builder is expected to choose and the volume ratio it must stay within (the compound's
    // volume over the mesh's). The counts and ratios are the probe's at 30 / 30 degrees where that fits in 64 cells
    // (MEASURE.md), and its best pair within 64 where it does not (the last three rows), plus a margin of 0.02. The
    // builder finds a better pair than MEASURE.md's ladder for the hollow torus and the 0.9 sphere (see the pair in each line).
    private static readonly (string name, int cells, double maxRatio)[] Expected =
    {
        ("hollow box 0.5", 4, 1.01),
        ("hollow box 0.95 (thin wall)", 4, 1.01),
        ("hollow cylinder 0.5", 12, 1.03),
        ("path-cut cylinder (pie 0.25 to 1.0, 270 deg)", 2, 1.01),
        ("path-cut cylinder (pie 0.25 to 0.75, 180 deg)", 1, 1.01),
        ("profile-cut box (0 to 0.75, 270 deg)", 2, 1.01),
        ("profile-cut hollow box (0 to 0.75, hollow 0.5)", 3, 1.01),
        ("cylinder, path begin/end 0.25..0.75 (straight path)", 1, 1.01),
        ("twisted hollow box (180 deg)", 32, 1.39),
        ("torus", 12, 1.04),
        ("twisted torus (180 deg)", 24, 1.13),
        ("twisted torus (360 deg)", 24, 1.20),
        ("tube", 12, 1.03),
        ("ring", 12, 1.06),
        ("tube, hollow 0.5", 48, 1.09),
        ("torus, path cut 0..0.5", 6, 1.04),
        ("bowl (hollow sphere 0.8, dimple 0..0.5)", 36, 1.12),
        // over 64 cells at 30 / 30 degrees: coarsened
        ("torus, hollow 0.5", 48, 1.21),
        ("hollow sphere 0.5", 48, 1.05),
        ("hollow sphere 0.9", 48, 1.41),
    };

    [Fact]
    public void Each_measured_shape_gets_its_cell_count_within_its_volume_ratio()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            foreach (var (name, cells, maxRatio) in Expected)
            {
                var (rings, meshVolume) = Prepare(mesher, Named(name));
                var sw = Stopwatch.StartNew();
                PrimHullCellSet set = PrimHullCells.Build(scene.Backend, rings, meshVolume);
                double ms = sw.Elapsed.TotalMilliseconds;
                Say($"PRIM-CELLS shape \"{name}\": success={set.Success} cells={set.CellCount} ({set.Pieces} pieces x {set.Spans} spans) " +
                    $"sector={set.SectorDegrees} span={set.SpanDegrees} coarsened={set.Coarsened} pairs_cooked={set.PairsCooked} " +
                    $"| mesh_volume={F(set.MeshVolume)} cells_volume={F(set.CellsVolume)} ratio={F(set.VolumeRatio, "0.000")} " +
                    $"| expected cells={cells} ratio<={F(maxRatio, "0.00")} | build={F(ms, "0.00")} ms");
                Assert.True(set.Success, $"{name}: {set.Failure}");
                Assert.Equal(cells, set.CellCount);
                Assert.InRange(set.VolumeRatio, 0.97, maxRatio);
                Assert.True(set.CellCount <= PrimHullCells.MaxCells);
            }
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_fine_split_is_kept_when_it_fits_and_the_profile_is_coarsened_first_when_it_does_not()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);

            var (r1, v1) = Prepare(mesher, Named("hollow cylinder 0.5"));
            PrimHullCellSet fits = PrimHullCells.Build(scene.Backend, r1, v1);
            Assert.False(fits.Coarsened);
            Assert.Equal(30.0, fits.SectorDegrees);
            Assert.Equal(30.0, fits.SpanDegrees);
            Assert.Equal(1, fits.PairsCooked);

            // the hollow torus: 144 cells at 30 / 30; the profile goes to 90 degrees and the path stays at 30
            var (r2, v2) = Prepare(mesher, Named("torus, hollow 0.5"));
            PrimHullCellSet torus = PrimHullCells.Build(scene.Backend, r2, v2);
            Assert.True(torus.Coarsened);
            Assert.Equal(90.0, torus.SectorDegrees);
            Assert.True(torus.SpanDegrees <= 30.0);
            Assert.InRange(torus.VolumeRatio, 1.0, 1.21);
            Assert.True(torus.PairsCooked > 1);

            // the single ladder the probe used first (45 / 45) is worse than the pair the builder picked
            Assert.True(torus.VolumeRatio < 1.31);
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_same_prim_gives_the_same_cells_every_time()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            var (rings, volume) = Prepare(mesher, Named("hollow sphere 0.5"));
            PrimHullCellSet a = PrimHullCells.Build(scene.Backend, rings, volume);
            PrimHullCellSet b = PrimHullCells.Build(scene.Backend, rings, volume);
            Assert.True(a.Success && b.Success);
            Assert.Equal(a.CellCount, b.CellCount);
            Assert.Equal(a.VolumeRatio, b.VolumeRatio);
            for (int i = 0; i < a.Cells.Length; i++)
                Assert.Equal(a.Cells[i], b.Cells[i]);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ cells follow the prim

    [Fact]
    public void Every_cell_point_lies_on_a_ring_of_the_prim()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            // one with a round hole in a square (hollow vertices fall inside the outer ring's edges) and the reverse
            var specs = new[]
            {
                new Spec { Name = "square hole in a cylinder", Profile = ProfileShape.Circle, Hole = HollowShape.Square, Hollow = 0.5f },
                new Spec { Name = "round hole in a box", Profile = ProfileShape.Square, Hole = HollowShape.Circle, Hollow = 0.5f },
                new Spec { Name = "triangle hole in a prism, cut", Profile = ProfileShape.EquilateralTriangle, Hole = HollowShape.Triangle, Hollow = 0.4f, CutEnd = 0.8f },
            };
            foreach (Spec spec in specs)
            {
                var (rings, volume) = Prepare(mesher, spec);
                PrimHullCellSet set = PrimHullCells.Build(scene.Backend, rings, volume);
                Assert.True(set.Success, spec.Name);
                foreach (SVector3[] cell in set.Cells)
                    foreach (SVector3 p in cell)
                        Assert.True(OnARing(rings, p), $"{spec.Name}: {p} is on no ring");
            }
        }
        finally { scene.Dispose(); }
    }

    /// <summary>True when the point is a vertex of a layer or lies on the straight line between two neighbouring vertices of the same layer.</summary>
    private static bool OnARing(PrimProfileRings rings, SVector3 p)
    {
        int size = rings.LayerVertexCount;
        for (int layer = 0; layer < rings.LayerCount; layer++)
            for (int i = 0; i < size; i++)
            {
                Vector3 a = rings.GetVertex(layer, i);
                var va = new SVector3(a.X, a.Y, a.Z);
                if ((va - p).LengthSquared() < 1e-10f)
                    return true;
                if (i + 1 >= size)
                    continue;
                Vector3 b = rings.GetVertex(layer, i + 1);
                var vb = new SVector3(b.X, b.Y, b.Z);
                SVector3 e = vb - va;
                float len2 = e.LengthSquared();
                if (len2 < 1e-12f)
                    continue;
                float t = SVector3.Dot(p - va, e) / len2;
                if (t < -1e-4f || t > 1f + 1e-4f)
                    continue;
                if ((va + e * t - p).LengthSquared() < 1e-8f)
                    return true;
            }
        return false;
    }

    // ------------------------------------------------------------------ the cavity stays empty

    /// <summary>
    /// True when a plane separates the point from the points' convex hull. Frank-Wolfe on the distance from the point to the
    /// hull: each step looks along the vector from the point to the hull's current nearest point, and the point is outside when every
    /// hull point is on the far side of that direction.
    /// </summary>
    private static bool OutsideHull(SVector3 q, SVector3[] points)
    {
        SVector3 x = points[0];
        for (int i = 1; i < points.Length; i++)
            if ((points[i] - q).LengthSquared() < (x - q).LengthSquared())
                x = points[i];
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            SVector3 d = x - q;
            float length = d.Length();
            if (length < 1e-7f)
                return false;
            float low = float.MaxValue;
            SVector3 s = x;
            foreach (SVector3 p in points)
            {
                float v = SVector3.Dot(d, p);
                if (v < low) { low = v; s = p; }
            }
            if (low - SVector3.Dot(d, q) > 1e-5f * length)
                return true;
            SVector3 e = s - x;
            float ee = e.LengthSquared();
            if (ee < 1e-20f)
                return false;
            float t = Math.Clamp(-SVector3.Dot(d, e) / ee, 0f, 1f);
            if (t <= 1e-9f)
                return false;
            x += e * t;
        }
        return false;
    }

    /// <summary>Points in the cavity: for each layer, the middle of the hollow ring and points part of the way to its vertices, and the same half way between layers.</summary>
    private static List<SVector3> CavityPoints(PrimProfileRings rings, double radiusFraction)
    {
        int first = (rings.HasCentreVertex ? 1 : 0) + rings.OuterVertexCount;
        int count = rings.HollowVertexCount;
        var layerPoints = new List<SVector3[]>();
        for (int layer = 0; layer < rings.LayerCount; layer++)
        {
            var ring = new SVector3[count];
            for (int k = 0; k < count; k++)
            {
                Vector3 v = rings.GetVertex(layer, first + k);
                ring[k] = new SVector3(v.X, v.Y, v.Z);
            }
            SVector3 centre = SVector3.Zero;
            foreach (SVector3 v in ring)
                centre += v;
            centre /= count;
            var points = new List<SVector3> { centre };
            for (int k = 0; k < count; k += 3)
                points.Add(centre + (ring[k] - centre) * (float)radiusFraction);
            layerPoints.Add(points.ToArray());
        }
        var all = new List<SVector3>();
        for (int layer = 0; layer < rings.LayerCount; layer++)
        {
            all.AddRange(layerPoints[layer]);
            if (layer + 1 < rings.LayerCount)
                for (int k = 0; k < layerPoints[layer].Length; k++)
                    all.Add((layerPoints[layer][k] + layerPoints[layer + 1][k]) * 0.5f);
        }
        return all;
    }

    [Fact]
    public void Points_inside_a_hollow_lie_outside_every_cell()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            var names = new[]
            {
                "hollow box 0.5", "hollow box 0.95 (thin wall)", "hollow cylinder 0.5", "twisted hollow box (180 deg)",
                "tube, hollow 0.5", "torus, hollow 0.5", "hollow sphere 0.5", "hollow sphere 0.9",
            };
            foreach (string name in names)
            {
                var (rings, volume) = Prepare(mesher, Named(name));
                PrimHullCellSet set = PrimHullCells.Build(scene.Backend, rings, volume);
                Assert.True(set.Success, name);
                // a chord across a sector of s degrees comes in to cos(s / 2) of the hollow's radius: stay well inside that
                double fraction = 0.5 * Math.Cos(set.SectorDegrees * Math.PI / 360.0);
                List<SVector3> points = CavityPoints(rings, fraction);
                int inside = 0;
                foreach (SVector3 q in points)
                    foreach (SVector3[] cell in set.Cells)
                        if (!OutsideHull(q, cell))
                            inside++;
                // the control: the one hull of every vertex (what a physical prim gets today) has these points inside it
                var all = new List<SVector3>();
                foreach (Vector3 v in rings.Vertices)
                    all.Add(new SVector3(v.X, v.Y, v.Z));
                int insideOneHull = points.Count(q => !OutsideHull(q, all.ToArray()));
                Say($"PRIM-CELLS cavity \"{name}\": cells={set.CellCount} sector={set.SectorDegrees} points={points.Count} " +
                    $"point_in_cell_pairs={inside} radius_fraction={F(fraction, "0.00")} points_inside_the_one_hull={insideOneHull}");
                Assert.Equal(0, inside);
                Assert.Equal(points.Count, insideOneHull);
            }
        }
        finally { scene.Dispose(); }
    }

    [Fact]
    public void The_point_check_finds_a_point_that_is_inside_a_cell()
    {
        // the check is not vacuous: the middle of a solid cell is inside it
        var cube = new[]
        {
            new SVector3(0, 0, 0), new SVector3(1, 0, 0), new SVector3(0, 1, 0), new SVector3(1, 1, 0),
            new SVector3(0, 0, 1), new SVector3(1, 0, 1), new SVector3(0, 1, 1), new SVector3(1, 1, 1),
        };
        Assert.False(OutsideHull(new SVector3(0.5f, 0.5f, 0.5f), cube));
        Assert.False(OutsideHull(new SVector3(0.9f, 0.1f, 0.5f), cube));
        Assert.True(OutsideHull(new SVector3(1.1f, 0.5f, 0.5f), cube));
        Assert.True(OutsideHull(new SVector3(0.5f, 0.5f, -0.2f), cube));
    }

    // ------------------------------------------------------------------ thin walls, and a split that does not cook

    [Fact]
    public void A_thin_walled_torus_and_a_thin_walled_sphere_are_cooked()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            var specs = new[]
            {
                Torus("thin-walled torus (hollow 0.95)", ProfileShape.Circle, 0.95f),
                Torus("thin-walled torus (hollow 0.9)", ProfileShape.Circle, 0.9f),
                new Spec { Name = "thin-walled sphere (hollow 0.95)", Profile = ProfileShape.HalfCircle, Hollow = 0.95f, Circular = true, ScaleX = 100, ScaleY = 100 },
                new Spec { Name = "thin-walled sphere (hollow 0.9)", Profile = ProfileShape.HalfCircle, Hollow = 0.9f, Circular = true, ScaleX = 100, ScaleY = 100 },
                new Spec { Name = "thin-walled tube (hollow 0.95)", Profile = ProfileShape.Square, Hollow = 0.95f, Circular = true, ScaleY = 175 },
            };
            foreach (Spec spec in specs)
            {
                var (rings, volume) = Prepare(mesher, spec);
                PrimHullCellSet set = PrimHullCells.Build(scene.Backend, rings, volume);
                Say($"PRIM-CELLS thin \"{spec.Name}\": success={set.Success} cells={set.CellCount} sector={set.SectorDegrees} span={set.SpanDegrees} " +
                    $"mesh_volume={F(set.MeshVolume, "0.0000")} ratio={F(set.VolumeRatio)} pairs_cooked={set.PairsCooked}");
                Assert.True(set.Success, $"{spec.Name}: {set.Failure}");
                Assert.InRange(set.CellCount, 2, PrimHullCells.MaxCells);

                // and the chosen cells cook, as the engine will cook them
                ShapeId compound = PrimHullCells.CookCompound(scene.Backend, set.Cells, out int failed);
                Assert.Equal(0, failed);
                Assert.True(compound.IsValid);
                Assert.True(scene.Backend.GetShapeVolume(compound) > 0f);
                scene.Backend.ReleaseShape(compound);
            }
        }
        finally { scene.Dispose(); }
    }

    private static Spec Torus(string name, ProfileShape profile, float hollow) =>
        new Spec { Name = name, Profile = profile, Hollow = hollow, Circular = true, ScaleX = 100, ScaleY = 175 };

    [Fact]
    public void A_split_that_does_not_cook_fails_instead_of_returning_cells()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            // a hollow box with no thickness: every cell is flat, which the hull builder cannot make a hull of
            Spec spec = Named("hollow box 0.5");
            var (rings, _) = Prepare(mesher, spec, new Vector3(1f, 1f, 0f));
            PrimHullCellSet set = PrimHullCells.Build(scene.Backend, rings, 0.5);
            Say($"PRIM-CELLS flat: success={set.Success} cells={set.CellCount} failure=\"{set.Failure}\" pairs_cooked={set.PairsCooked}");
            Assert.False(set.Success);
            Assert.Empty(set.Cells);
            Assert.False(string.IsNullOrEmpty(set.Failure));

            // and with no mesh volume there is nothing to compare the cells with
            var (ok, volume) = Prepare(mesher, spec);
            Assert.False(PrimHullCells.Build(scene.Backend, ok, 0.0).Success);
            Assert.True(PrimHullCells.Build(scene.Backend, ok, volume).Success);
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ the 64-cell ceiling over the probe's grids

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_shape_of_the_grid_goes_above_64_cells(bool extreme)
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            int total = 0, coarsened = 0, failed = 0, fine = 0, worst = 0, single = 0;
            double worstRatio = 0, sumMs = 0, worstMs = 0;
            string worstName = "", worstRatioName = "";
            var ratios = new List<double>();
            var histogram = new SortedDictionary<int, int>();
            var failures = new List<string>();
            foreach (Spec spec in Grid(extreme))
            {
                PrimitiveBaseShape pbs = spec.Build();
                if (!mesher.TryGetPrimProfileRings(spec.Name, pbs, Vector3.One, Lod, out PrimProfileRings rings))
                    continue;
                IMesh mesh = mesher.CreateMesh(spec.Name, pbs, Vector3.One, Lod, false, false, false);
                if (mesh == null)
                    continue;
                double meshVolume = PrimHullCells.MeshVolume(mesh.getVertexListAsFloat(), mesh.getIndexListAsInt());
                total++;

                // the count alone, without cooking anything
                IReadOnlyList<int> counts = PrimHullCells.CandidateCellCounts(rings);
                Assert.NotEmpty(counts);
                Assert.All(counts, c => Assert.InRange(c, 1, PrimHullCells.MaxCells));

                var sw = Stopwatch.StartNew();
                PrimHullCellSet set = PrimHullCells.Build(scene.Backend, rings, meshVolume);
                double ms = sw.Elapsed.TotalMilliseconds;
                sumMs += ms; worstMs = Math.Max(worstMs, ms);
                if (!set.Success)
                {
                    failed++;
                    if (failures.Count < 10) failures.Add($"{spec.Name}: {set.Failure}");
                    continue;
                }
                Assert.InRange(set.CellCount, 1, PrimHullCells.MaxCells);
                if (set.CellCount == 1) single++;
                if (set.Coarsened) coarsened++; else fine++;
                int bucket = set.CellCount <= 1 ? 1 : set.CellCount <= 4 ? 4 : set.CellCount <= 8 ? 8 : set.CellCount <= 16 ? 16 : set.CellCount <= 32 ? 32 : 64;
                histogram[bucket] = histogram.GetValueOrDefault(bucket) + 1;
                if (set.CellCount > worst) { worst = set.CellCount; worstName = spec.Name; }
                // a twisted or self-crossing prim can have a mesh whose signed volume is nearly zero: its ratio says nothing
                if (meshVolume > 0.02)
                {
                    ratios.Add(set.VolumeRatio);
                    if (set.VolumeRatio > worstRatio) { worstRatio = set.VolumeRatio; worstRatioName = spec.Name; }
                }
            }

            string kind = extreme ? "EXTREME" : "ORDINARY";
            ratios.Sort();
            double At(double q) => ratios.Count == 0 ? 0 : ratios[Math.Min(ratios.Count - 1, (int)(q * ratios.Count))];
            Say($"PRIM-CELLS grid {kind}: shapes={total} built_fine={fine} coarsened={coarsened} failed={failed} one_cell={single} worst_cells={worst} [{worstName}] " +
                $"| volume ratio over {ratios.Count} shapes with a mesh volume above 0.02: median={F(At(0.5), "0.00")} p90={F(At(0.9), "0.00")} p99={F(At(0.99), "0.00")} " +
                $"worst={F(worstRatio, "0.00")} [{worstRatioName}] over_2={ratios.Count(r => r > 2.0)} " +
                $"| build_ms mean={F(sumMs / Math.Max(1, total), "0.00")} worst={F(worstMs, "0.0")}");
            Say($"PRIM-CELLS grid {kind} histogram (upper bound of bucket: shapes): " + string.Join(", ", histogram.Select(kv => $"{kv.Key}: {kv.Value}")));
            foreach (string f in failures)
                Say($"PRIM-CELLS grid {kind} failed: {f}");
            Assert.True(total > 500);
            Assert.Equal(0, failed);
            Assert.True(worst <= PrimHullCells.MaxCells);
        }
        finally { scene.Dispose(); }
    }
}
