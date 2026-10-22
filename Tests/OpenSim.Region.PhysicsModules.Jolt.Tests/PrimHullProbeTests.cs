/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A measuring probe for splitting a physical concave prim (hollow, cut, twisted, torus, tube, ring, hollow sphere) into
/// convex cells, with no engine change: a cell is one convex piece of the prim's profile swept over one span of its path,
/// and its hull is the hull of that piece's points on the span's rings. The probe takes the rings from the mesher's own
/// output (the same extrusion that draws the prim), splits them, cooks the cells through the backend's existing
/// CreateConvexHullShape and CreateHullCompoundShape, and reports for each shape the cell count, the volume against the
/// mesher's own closed mesh and against today's single hull, the cook time, a rough native memory figure, and whether a
/// scaled copy of the compound is valid. It also counts cells over a grid of ordinary and extreme prim parameters against
/// a ceiling of 64 hulls per prim, and reports the coarsening step each shape would need.
///
/// Each result is a line "PRIM-HULLS ..." in the test output. The assertions are sanity checks, not timings.
/// The analytic cell split here is a prototype: the profile layout and the path node count come from the mesher's output,
/// and the profile angles are rebuilt from a Profile built with the same sides and cut.
/// Serial with the other native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public partial class PrimHullProbeTests
{
    private readonly ITestOutputHelper _out;
    public PrimHullProbeTests(ITestOutputHelper output) { _out = output; }

    private void Say(string line) { _out.WriteLine(line); Console.WriteLine(line); }

    private const int HullCeiling = 64;

    // ------------------------------------------------------------------ the mesher's own rings

    private static readonly MethodInfo GenerateCoords =
        typeof(Meshmerizer).GetMethod("GenerateCoordsAndFacesFromPrimShapeData", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The mesher's coordinates and faces for the prim at unit size, the level of detail a physical prim gets (32).</summary>
    private static bool Rings(Meshmerizer mesher, PrimitiveBaseShape pbs, out List<Coord> coords, out List<Face> faces)
    {
        object[] args = { "probe", pbs, new OpenMetaverse.Vector3(1f, 1f, 1f), 32f, null, null };
        bool ok = (bool)GenerateCoords.Invoke(mesher, args)!;
        coords = (List<Coord>)args[4];
        faces = (List<Face>)args[5];
        return ok && coords.Count > 0;
    }

    /// <summary>The volume of the mesher's mesh by the divergence theorem (its sides close geometrically).</summary>
    private static double MeshVolume(List<Coord> c, List<Face> faces)
    {
        double v = 0;
        foreach (Face f in faces)
        {
            Coord a = c[f.v1], b = c[f.v2], d = c[f.v3];
            v += a.X * (b.Y * d.Z - b.Z * d.Y) - a.Y * (b.X * d.Z - b.Z * d.X) + a.Z * (b.X * d.Y - b.Y * d.X);
        }
        return Math.Abs(v) / 6.0;
    }

    // ------------------------------------------------------------------ the profile's angles

    private sealed class ProfileLayout
    {
        public int Sides, HollowSides;
        public bool HasHollow, HasCut;
        public int Centre;                    // 1 when the layer starts with a centre vertex, else 0
        public int NumOuter, NumHollow;
        public int LayerSize;
        public List<Coord> Flat;              // the profile itself (unscaled, unrotated), for angles
        public double[] OuterAngle;           // radians from the first outer vertex, increasing
        public double[] HollowAngle;          // radians, in angular order (the layer stores the hollow ring reversed)
        public double Sweep;                  // radians
    }

    private static ProfileLayout Layout(PrimitiveBaseShape pbs)
    {
        // The same sides, hollow sides and profile range as Meshmerizer.GenerateCoordsAndFacesFromPrimShapeData.
        int sides = 4;
        ProfileShape ps = pbs.ProfileShape;
        float begin = pbs.ProfileBegin * 2.0e-5f, end = 1f - pbs.ProfileEnd * 2.0e-5f;
        float hollow = Math.Min(pbs.ProfileHollow * 2.0e-5f, 0.95f);
        if (ps == ProfileShape.EquilateralTriangle) sides = 3;
        else if (ps == ProfileShape.Circle) sides = 24;
        else if (ps == ProfileShape.HalfCircle) { sides = 24; begin = 0.5f * begin + 0.5f; end = 0.5f * end + 0.5f; }
        int hollowSides = sides;
        if (pbs.HollowShape == HollowShape.Circle) hollowSides = 24;
        else if (pbs.HollowShape == HollowShape.Square) hollowSides = 4;
        else if (pbs.HollowShape == HollowShape.Triangle) hollowSides = 3;

        bool hasHollow = hollow > 0.001f;
        var profile = new Profile(sides, begin, end, hasHollow ? 0.5f : 0f, hollowSides, true, false);
        bool hasCut = begin > 0f || end < 1f;
        var l = new ProfileLayout
        {
            Sides = sides, HollowSides = hollowSides, HasHollow = hasHollow, HasCut = hasCut,
            NumOuter = profile.numOuterVerts, NumHollow = profile.numHollowVerts, Flat = profile.coords, LayerSize = profile.coords.Count,
        };
        bool simpleFace = sides < 5 && !hasHollow && !hasCut;
        l.Centre = (!hasHollow && !simpleFace) ? 1 : 0;

        l.OuterAngle = Unwrap(Enumerable.Range(l.Centre, l.NumOuter).Select(i => Polar(profile.coords[i])).ToArray(), 0.0);
        if (hasHollow)
        {
            // stored reversed; angular order runs the other way
            int first = l.Centre + l.NumOuter;
            var raw = new double[l.NumHollow];
            for (int k = 0; k < l.NumHollow; k++) raw[k] = Polar(profile.coords[first + (l.NumHollow - 1 - k)]);
            double baseAngle = Polar(profile.coords[l.Centre]);
            l.HollowAngle = Unwrap(raw, baseAngle);
        }
        l.Sweep = l.OuterAngle[^1] - l.OuterAngle[0];
        // express outer angles relative to the first vertex
        double a0 = l.OuterAngle[0];
        for (int i = 0; i < l.OuterAngle.Length; i++) l.OuterAngle[i] -= a0;
        if (l.HollowAngle != null)
            for (int i = 0; i < l.HollowAngle.Length; i++) l.HollowAngle[i] -= a0;
        return l;
    }

    private static double Polar(Coord c) => Math.Atan2(c.Y, c.X);

    // Angles in increasing order starting at (or just after) baseAngle; the polar angle wraps once at most.
    private static double[] Unwrap(double[] polar, double baseAngle)
    {
        var r = new double[polar.Length];
        double prev = double.NegativeInfinity;
        for (int i = 0; i < polar.Length; i++)
        {
            double a = polar[i];
            if (i == 0)
            {
                // the first vertex is the reference: put it at its own polar angle
                while (a < baseAngle - 1e-3) a += 2 * Math.PI;
                while (a > baseAngle + 2 * Math.PI - 1e-3) a -= 2 * Math.PI;
            }
            else
                while (a < prev - 1e-4) a += 2 * Math.PI;
            r[i] = prev = a;
        }
        return r;
    }

    // ------------------------------------------------------------------ the cell split

    /// <summary>A point of a profile ring: vertex i, or the point t of the way from vertex i to vertex i + 1 of the same ring.</summary>
    private readonly struct RingPoint
    {
        public readonly int A, B; public readonly float T;
        public RingPoint(int a, int b, float t) { A = a; B = b; T = t; }
    }

    private sealed class Plan
    {
        public List<List<RingPoint>> Pieces = new List<List<RingPoint>>();   // each piece: points in one layer
        public List<(int First, int Last)> Spans = new List<(int, int)>();   // layers, inclusive
        public int Layers;
        public int Count => Pieces.Count * Spans.Count;
    }

    private static int Index(ProfileLayout l, bool hollow, int k) =>
        hollow ? l.Centre + l.NumOuter + (l.NumHollow - 1 - k) : l.Centre + k;

    // The point on a ring at angle b (radians from the first outer vertex): a vertex when one is there, else on the edge.
    private static RingPoint PointAt(ProfileLayout l, bool hollow, double b)
    {
        double[] ang = hollow ? l.HollowAngle : l.OuterAngle;
        int n = ang.Length;
        for (int j = 0; j < n; j++)
            if (Math.Abs(ang[j] - b) < 1e-3) { int i = Index(l, hollow, j); return new RingPoint(i, i, 0f); }
        for (int j = 0; j < n - 1; j++)
        {
            if (b > ang[j] && b < ang[j + 1])
            {
                int i0 = Index(l, hollow, j), i1 = Index(l, hollow, j + 1);
                Coord p0 = l.Flat[i0], p1 = l.Flat[i1];
                double a0 = Polar(l.Flat[Index(l, false, 0)]);
                double dx = Math.Cos(a0 + b), dy = Math.Sin(a0 + b);
                double c0 = dx * p0.Y - dy * p0.X;
                double c1 = dx * (p1.Y - p0.Y) - dy * (p1.X - p0.X);
                float t = Math.Abs(c1) < 1e-12 ? 0.5f : (float)Math.Clamp(-c0 / c1, 0.0, 1.0);
                return new RingPoint(i0, i1, t);
            }
        }
        // outside the ring's own range: the nearest end
        int e = b <= ang[0] ? 0 : n - 1;
        int ie = Index(l, hollow, e);
        return new RingPoint(ie, ie, 0f);
    }

    private static Plan Split(ProfileLayout l, int layers, PrimitiveBaseShape pbs, double profileSectorDeg, double pathSpanDeg)
    {
        var plan = new Plan { Layers = layers };
        double sector = profileSectorDeg * Math.PI / 180.0;

        if (l.HasHollow)
        {
            var bps = l.OuterAngle.Concat(l.HollowAngle).OrderBy(x => x).ToList();
            var uniq = new List<double>();
            foreach (double b in bps)
                if (uniq.Count == 0 || b - uniq[^1] > 1e-3) uniq.Add(b);
            // greedy groups of consecutive segments no wider than the sector (at least one segment)
            int s = 0;
            while (s < uniq.Count - 1)
            {
                int e = s + 1;
                while (e + 1 < uniq.Count && uniq[e + 1] - uniq[s] <= sector + 1e-3) e++;
                var piece = new List<RingPoint>();
                for (int k = s; k <= e; k++)
                {
                    piece.Add(PointAt(l, false, uniq[k]));
                    piece.Add(PointAt(l, true, uniq[k]));
                }
                plan.Pieces.Add(piece);
                s = e;
            }
        }
        else if (!l.HasCut)
        {
            // an uncut solid profile is convex
            var piece = new List<RingPoint>();
            for (int i = 0; i < l.LayerSize; i++) piece.Add(new RingPoint(i, i, 0f));
            plan.Pieces.Add(piece);
        }
        else
        {
            // a cut solid profile is a wedge from the centre; wider than a half turn it is cut into wedges of at most a half turn
            double limit = Math.PI + 1e-3;
            int s = 0, n = l.OuterAngle.Length;
            while (s < n - 1)
            {
                int e = s + 1;
                while (e + 1 < n && l.OuterAngle[e + 1] - l.OuterAngle[s] <= limit) e++;
                var piece = new List<RingPoint>();
                if (l.Centre == 1) piece.Add(new RingPoint(0, 0, 0f));
                for (int k = s; k <= e; k++) { int i = Index(l, false, k); piece.Add(new RingPoint(i, i, 0f)); }
                plan.Pieces.Add(piece);
                s = e;
            }
        }

        // path spans: how far the profile turns (circular path) and twists between one layer and the next
        int intervals = layers - 1;
        bool circular = pbs.PathCurve != (byte)Extrusion.Straight && pbs.PathCurve != (byte)Extrusion.Flexible;
        double twistUnit = circular ? 3.6 : 1.8;
        double twistDeg = Math.Abs((pbs.PathTwist - pbs.PathTwistBegin) * twistUnit);
        double pathDeg = 0;
        if (circular)
        {
            double cut = 1.0 - (pbs.PathBegin + pbs.PathEnd) * 2.0e-5;
            pathDeg = cut * (1.0 + 0.015 * pbs.PathRevolutions) * 360.0;
        }
        double perInterval = (twistDeg + pathDeg) / Math.Max(1, intervals);
        int per = perInterval < 1e-6 ? intervals : Math.Clamp((int)Math.Floor(pathSpanDeg / perInterval + 1e-6), 1, intervals);
        int nSpans = (intervals + per - 1) / per;
        // spread the intervals evenly over the spans
        int at = 0;
        for (int sp = 0; sp < nSpans; sp++)
        {
            int len = intervals / nSpans + (sp < intervals % nSpans ? 1 : 0);
            plan.Spans.Add((at, at + len));
            at += len;
        }
        return plan;
    }

    /// <summary>The cells' point lists: for each piece and each span, the piece's points on every layer of the span.</summary>
    private static List<SVector3[]> CellPoints(Plan plan, ProfileLayout l, List<Coord> coords)
    {
        var cells = new List<SVector3[]>();
        foreach (var piece in plan.Pieces)
            foreach (var span in plan.Spans)
            {
                var pts = new List<SVector3>();
                for (int layer = span.First; layer <= span.Last; layer++)
                {
                    int baseIndex = layer * l.LayerSize;
                    foreach (RingPoint p in piece)
                    {
                        Coord a = coords[baseIndex + p.A], b = coords[baseIndex + p.B];
                        pts.Add(new SVector3(a.X + (b.X - a.X) * p.T, a.Y + (b.Y - a.Y) * p.T, a.Z + (b.Z - a.Z) * p.T));
                    }
                }
                cells.Add(pts.Distinct().ToArray());
            }
        return cells;
    }

    private static readonly (double sector, double span)[] Coarsening =
        { (30, 30), (45, 45), (60, 60), (90, 90), (180, 180) };

    // ------------------------------------------------------------------ the measurement

    private sealed class Row
    {
        public string Name;
        public int Layers, Pieces, Spans, Cells, Failed;
        public double MeshVolume, CellsVolume, OneHullVolume, SplitMs, CookMs, OneHullCookMs, MeshMs, KbPerCompound, KbPerOneHull;
        public string OneHullKind;
        public string ScaledOk;
        public int AtLevel;        // index into Coarsening of the first level whose count is within the ceiling; -1 none
        public int CountAtLevel0, CountAtChosen;
        public double ChosenVolume, ChosenCookMs;
        public int ChosenFailed;
    }

    /// <summary>Cooks a plan's cells into a compound and reports its volume, failed cells and cook time (median of 5 after a warm-up).</summary>
    private static (double volume, int failed, double ms, int cells) CookPlan(IPhysicsBackend backend, Plan plan, ProfileLayout layout, List<Coord> coords)
    {
        List<SVector3[]> cells = CellPoints(plan, layout, coords);
        var times = new List<double>();
        double volume = 0; int failedCount = 0;
        for (int run = 0; run < 6; run++)
        {
            var sw = Stopwatch.StartNew();
            var hulls = new List<ShapeId>();
            int failed = 0;
            foreach (SVector3[] pts in cells)
            {
                ShapeId h = pts.Length >= 4 ? backend.CreateConvexHullShape(pts) : default;
                if (h.IsValid) hulls.Add(h); else failed++;
            }
            ShapeId c = hulls.Count >= 2 ? backend.CreateHullCompoundShape(hulls.ToArray()) : (hulls.Count == 1 ? hulls[0] : default);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (run == 0) { volume = c.IsValid ? backend.GetShapeVolume(c) : 0; failedCount = failed; } else times.Add(ms);
            if (hulls.Count >= 2) foreach (ShapeId h in hulls) backend.ReleaseShape(h);
            if (c.IsValid) backend.ReleaseShape(c);
        }
        return (volume, failedCount, Median(times), cells.Count);
    }

    private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

    private static double PrivateKb() { using var p = Process.GetCurrentProcess(); return p.PrivateMemorySize64 / 1024.0; }

    private Row Measure(JoltScene scene, Meshmerizer mesher, Spec spec)
    {
        PrimitiveBaseShape pbs = spec.Build();
        IPhysicsBackend backend = scene.Backend;
        var row = new Row { Name = spec.Name };

        var sw = Stopwatch.StartNew();
        Assert.True(Rings(mesher, pbs, out List<Coord> coords, out List<Face> faces), spec.Name);
        row.MeshMs = sw.Elapsed.TotalMilliseconds;
        row.MeshVolume = MeshVolume(coords, faces);

        ProfileLayout layout = Layout(pbs);
        Assert.Equal(0, coords.Count % layout.LayerSize);
        int layers = coords.Count / layout.LayerSize;
        row.Layers = layers;

        // the first coarsening level within the ceiling
        Plan chosen = null;
        row.AtLevel = -1;
        for (int lv = 0; lv < Coarsening.Length; lv++)
        {
            Plan p = Split(layout, layers, pbs, Coarsening[lv].sector, Coarsening[lv].span);
            if (lv == 0) row.CountAtLevel0 = p.Count;
            if (p.Count <= HullCeiling) { chosen = p; row.AtLevel = lv; break; }
            chosen = p;
        }
        row.CountAtChosen = chosen.Count;
        {
            var r = CookPlan(backend, chosen, layout, coords);
            row.ChosenVolume = r.volume; row.ChosenCookMs = r.ms; row.ChosenFailed = r.failed;
        }

        // the cells at the first level (level 0 whatever its count), to cook and measure
        Plan plan = Split(layout, layers, pbs, Coarsening[0].sector, Coarsening[0].span);
        row.Pieces = plan.Pieces.Count; row.Spans = plan.Spans.Count; row.Cells = plan.Count;
        sw.Restart();
        List<SVector3[]> cells = CellPoints(plan, layout, coords);
        row.SplitMs = sw.Elapsed.TotalMilliseconds;

        // one hull of every point, as the engine cooks a physical prim today
        var all = coords.Select(c => new SVector3(c.X, c.Y, c.Z)).ToArray();
        var oneTimes = new List<double>();
        ShapeId oneHull = default;
        for (int run = 0; run < 6; run++)
        {
            sw.Restart();
            ShapeId h = backend.CreateConvexHullShape(all);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (run > 0) oneTimes.Add(ms);
            if (run == 0) oneHull = h; else backend.ReleaseShape(h);
        }
        row.OneHullCookMs = Median(oneTimes);
        row.OneHullVolume = backend.GetShapeVolume(oneHull);
        // and the engine's own cook, to confirm what kind it is
        ShapeId engine = scene.CookPrimShape(backend, pbs, OpenMetaverse.Vector3.One, true, out _, out row.OneHullKind, true, false, false);
        backend.ReleaseShape(engine);
        backend.ReleaseShape(oneHull);

        // the cells: hulls, then the compound
        var times = new List<double>();
        ShapeId compound = default;
        for (int run = 0; run < 6; run++)
        {
            sw.Restart();
            var hulls = new List<ShapeId>();
            int failed = 0;
            foreach (SVector3[] pts in cells)
            {
                ShapeId h = pts.Length >= 4 ? backend.CreateConvexHullShape(pts) : default;
                if (h.IsValid) hulls.Add(h); else failed++;
            }
            ShapeId c = hulls.Count >= 2 ? backend.CreateHullCompoundShape(hulls.ToArray()) : (hulls.Count == 1 ? hulls[0] : default);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (hulls.Count >= 2) foreach (ShapeId h in hulls) backend.ReleaseShape(h);
            if (run > 0) times.Add(ms);
            if (run == 0) { compound = c; row.Failed = failed; } else backend.ReleaseShape(c);
        }
        row.CookMs = Median(times);
        row.CellsVolume = compound.IsValid ? backend.GetShapeVolume(compound) : 0f;

        // a scaled copy of the compound
        var scale = new SVector3(2f, 3f, 1.5f);
        if (!compound.IsValid) row.ScaledOk = "n/a";
        else
        {
            bool valid = backend.IsScaleValid(compound, scale);
            if (!valid) row.ScaledOk = "IsScaleValid=false";
            else
            {
                ShapeId scaled = backend.CreateScaledShape(compound, scale);
                float v = scaled.IsValid ? backend.GetShapeVolume(scaled) : 0f;
                double expect = row.CellsVolume * scale.X * scale.Y * scale.Z;
                row.ScaledOk = scaled.IsValid && Math.Abs(v - expect) <= 0.001 * expect + 1e-6
                    ? "ok (volume x" + (v / row.CellsVolume).ToString("0.00", CultureInfo.InvariantCulture) + ")"
                    : $"WRONG volume {v} expected {expect}";
                if (scaled.IsValid) backend.ReleaseShape(scaled);
            }
        }
        backend.ReleaseShape(compound);

        // rough native memory: 100 copies of each, process private bytes
        row.KbPerCompound = KbPerCopy(backend, () => BuildCompound(backend, cells), 100);
        row.KbPerOneHull = KbPerCopy(backend, () => backend.CreateConvexHullShape(all), 100);
        return row;
    }

    private static ShapeId BuildCompound(IPhysicsBackend backend, List<SVector3[]> cells)
    {
        var hulls = new List<ShapeId>();
        foreach (SVector3[] pts in cells)
        {
            ShapeId h = pts.Length >= 4 ? backend.CreateConvexHullShape(pts) : default;
            if (h.IsValid) hulls.Add(h);
        }
        if (hulls.Count < 2) return hulls.Count == 1 ? hulls[0] : default;
        ShapeId c = backend.CreateHullCompoundShape(hulls.ToArray());
        foreach (ShapeId h in hulls) backend.ReleaseShape(h);
        return c;
    }

    private static double KbPerCopy(IPhysicsBackend backend, Func<ShapeId> make, int copies)
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        double before = PrivateKb();
        var held = new List<ShapeId>(copies);
        for (int i = 0; i < copies; i++) held.Add(make());
        double after = PrivateKb();
        foreach (ShapeId s in held) if (s.IsValid) backend.ReleaseShape(s);
        return (after - before) / copies;
    }

    private static string F(double v, string f = "0.000") => v.ToString(f, CultureInfo.InvariantCulture);

    [Fact]
    public void Hull_counts_volumes_and_cook_times_of_the_concave_shapes()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            foreach (Spec spec in Shapes())
            {
                Row r = Measure(scene, mesher, spec);
                Say($"PRIM-HULLS shape \"{r.Name}\": layers={r.Layers} pieces={r.Pieces} spans={r.Spans} cells={r.Cells} failed_cells={r.Failed} " +
                    $"| mesh_volume={F(r.MeshVolume)} cells_volume={F(r.CellsVolume)} ratio={F(r.CellsVolume / r.MeshVolume, "0.00")} " +
                    $"one_hull_volume={F(r.OneHullVolume)} ratio={F(r.OneHullVolume / r.MeshVolume, "0.00")} (engine kind={r.OneHullKind}) " +
                    $"| split={F(r.SplitMs, "0.00")} ms cook_cells={F(r.CookMs, "0.00")} ms cook_one_hull={F(r.OneHullCookMs, "0.00")} ms mesher={F(r.MeshMs, "0.00")} ms " +
                    $"| native_kb_per_copy cells={F(r.KbPerCompound, "0.0")} one_hull={F(r.KbPerOneHull, "0.0")} | scaled(2,3,1.5)={r.ScaledOk} " +
                    $"| ceiling: cells@30deg={r.CountAtLevel0} first_level_within_{HullCeiling}={(r.AtLevel < 0 ? "none" : $"{Coarsening[r.AtLevel].sector}deg")} cells_there={r.CountAtChosen} volume_there={F(r.ChosenVolume)} ratio={F(r.ChosenVolume / r.MeshVolume, "0.00")} cook_there={F(r.ChosenCookMs, "0.00")} ms failed_there={r.ChosenFailed}");
                Assert.True(r.Cells >= 1);
                Assert.True(r.CellsVolume > 0 && float.IsFinite((float)r.CellsVolume), r.Name);
            }
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ how fine a twist or turn span has to be

    [Fact]
    public void Volume_error_and_cell_count_by_path_span_and_profile_sector()
    {
        string scratch = Scratch();
        JoltScene scene = NewScene(scratch);
        try
        {
            Meshmerizer mesher = NewMesher(scratch);
            var specs = new List<Spec>
            {
                new Spec { Name = "twisted box 180 deg", Profile = ProfileShape.Square, Twist = 100 },
                new Spec { Name = "twisted hollow box 0.5, 180 deg", Profile = ProfileShape.Square, Hollow = 0.5f, Twist = 100 },
                new Spec { Name = "twisted hollow cylinder 0.5, 180 deg", Profile = ProfileShape.Circle, Hollow = 0.5f, Twist = 100 },
                Torus("torus, hollow 0.5", ProfileShape.Circle, 0.5f),
                new Spec { Name = "hollow sphere 0.5", Profile = ProfileShape.HalfCircle, Hollow = 0.5f, Circular = true, ScaleX = 100, ScaleY = 100 },
            };
            foreach (Spec spec in specs)
            {
                PrimitiveBaseShape pbs = spec.Build();
                Assert.True(Rings(mesher, pbs, out List<Coord> coords, out List<Face> faces));
                double mesh = MeshVolume(coords, faces);
                ProfileLayout layout = Layout(pbs);
                int layers = coords.Count / layout.LayerSize;
                foreach ((double sector, double span) in new[] { (30.0, 7.5), (30.0, 15.0), (30.0, 30.0), (30.0, 45.0), (30.0, 60.0), (45.0, 30.0), (45.0, 45.0), (60.0, 60.0), (90.0, 90.0) })
                {
                    Plan plan = Split(layout, layers, pbs, sector, span);
                    var r = CookPlan(scene.Backend, plan, layout, coords);
                    Say($"PRIM-HULLS sweep \"{spec.Name}\": profile_sector={sector} path_span={span} pieces={plan.Pieces.Count} spans={plan.Spans.Count} cells={r.cells} " +
                        $"volume_ratio={F(r.volume / mesh, "0.00")} failed={r.failed} cook={F(r.ms, "0.00")} ms");
                }
                // the best pair of sector and span that stays within the ceiling
                double bestRatio = double.MaxValue; double bs = 0, bp = 0; int bc = 0;
                foreach (double sector in new[] { 30.0, 45, 60, 90, 120, 180 })
                    foreach (double span in new[] { 15.0, 30, 45, 60, 90, 120, 180 })
                    {
                        Plan plan = Split(layout, layers, pbs, sector, span);
                        if (plan.Count > HullCeiling) continue;
                        double ratio = CookPlan(scene.Backend, plan, layout, coords).volume / mesh;
                        if (ratio < bestRatio) { bestRatio = ratio; bs = sector; bp = span; bc = plan.Count; }
                    }
                Say($"PRIM-HULLS best within {HullCeiling} \"{spec.Name}\": profile_sector={bs} path_span={bp} cells={bc} volume_ratio={F(bestRatio, "0.00")}");
            }
        }
        finally { scene.Dispose(); }
    }

    // ------------------------------------------------------------------ the 64-hull ceiling over a parameter grid

    internal static IEnumerable<Spec> Grid(bool extreme)
    {
        var profiles = new (ProfileShape p, bool circ, string n)[]
        {
            (ProfileShape.Square, false, "box"), (ProfileShape.Circle, false, "cylinder"), (ProfileShape.EquilateralTriangle, false, "prism"),
            (ProfileShape.HalfCircle, true, "sphere"), (ProfileShape.Circle, true, "torus"), (ProfileShape.Square, true, "tube"),
            (ProfileShape.EquilateralTriangle, true, "ring"),
        };
        var holes = new List<(float h, HollowShape s)> { (0f, HollowShape.Same) };
        foreach (float h in new[] { 0.5f, 0.95f })
            foreach (HollowShape s in new[] { HollowShape.Same, HollowShape.Circle, HollowShape.Square, HollowShape.Triangle })
                holes.Add((h, s));
        // twist in PathTwist units: ordinary up to 180 degrees; extreme the largest the field holds, with extra revolutions
        int[] twistLinear = extreme ? new[] { 0, 100, 127 } : new[] { 0, 50, 100 };
        int[] twistCirc = extreme ? new[] { 0, 50, 127 } : new[] { 0, 25, 50 };
        var cuts = new (float b, float e)[] { (0f, 1f), (0.25f, 0.75f), (0f, 0.75f) };
        foreach (var pr in profiles)
            foreach (var hole in holes)
                foreach (int tw in pr.circ ? twistCirc : twistLinear)
                    foreach (var cut in cuts)
                        foreach (float pathEnd in pr.circ ? new[] { 1f, 0.5f } : new[] { 1f })
                            yield return new Spec
                            {
                                Name = $"{pr.n} hollow={hole.h} {hole.s} twist={tw} cut={cut.b}..{cut.e} pathEnd={pathEnd}" + (extreme && pr.circ ? " rev+" : ""),
                                Profile = pr.p, Circular = pr.circ, Hollow = hole.h, Hole = hole.s, Twist = tw,
                                CutBegin = cut.b, CutEnd = cut.e, PathEnd = pathEnd,
                                Revolutions = extreme && pr.circ ? 66 : 0,   // 66 units = 1.99 turns
                                ScaleX = 100, ScaleY = pr.p == ProfileShape.HalfCircle ? (byte)100 : (byte)(pr.circ ? 175 : 100),
                            };
    }

    [Fact]
    public void Cell_counts_over_a_grid_of_prim_parameters_against_the_ceiling()
    {
        string scratch = Scratch();
        Meshmerizer mesher = NewMesher(scratch);
        foreach (bool extreme in new[] { false, true })
        {
            int total = 0, over = 0, needs45 = 0, needs60 = 0, needs90 = 0, needs180 = 0, beyond = 0, rings = 0;
            int worst = 0; string worstName = "";
            var histogram = new SortedDictionary<int, int>();
            var overNames = new List<string>();
            foreach (Spec spec in Grid(extreme))
            {
                PrimitiveBaseShape pbs = spec.Build();
                if (!Rings(mesher, pbs, out List<Coord> coords, out _)) continue;
                ProfileLayout layout = Layout(pbs);
                if (coords.Count % layout.LayerSize != 0) { rings++; continue; }
                int layers = coords.Count / layout.LayerSize;
                total++;
                int n0 = Split(layout, layers, pbs, 30, 30).Count;
                int bucket = n0 <= 1 ? n0 : n0 <= 4 ? 4 : n0 <= 8 ? 8 : n0 <= 16 ? 16 : n0 <= 32 ? 32 : n0 <= 64 ? 64 : n0 <= 128 ? 128 : 256;
                histogram[bucket] = histogram.GetValueOrDefault(bucket) + 1;
                if (n0 > worst) { worst = n0; worstName = spec.Name; }
                if (n0 > HullCeiling)
                {
                    over++;
                    if (overNames.Count < 12) overNames.Add($"{spec.Name} cells={n0}");
                    if (Split(layout, layers, pbs, 45, 45).Count <= HullCeiling) needs45++;
                    else if (Split(layout, layers, pbs, 60, 60).Count <= HullCeiling) needs60++;
                    else if (Split(layout, layers, pbs, 90, 90).Count <= HullCeiling) needs90++;
                    else if (Split(layout, layers, pbs, 180, 180).Count <= HullCeiling) needs180++;
                    else beyond++;
                }
            }
            string kind = extreme ? "EXTREME (twist to 127 units, about 2 turns)" : "ORDINARY (twist up to 180 deg, one turn)";
            Say($"PRIM-HULLS grid {kind}: shapes={total} (layout mismatches skipped={rings}) over_{HullCeiling}_at_30deg={over} " +
                $"fixed_by_45deg={needs45} by_60deg={needs60} by_90deg={needs90} by_180deg={needs180} still_over={beyond} worst={worst} [{worstName}]");
            Say("PRIM-HULLS grid " + (extreme ? "EXTREME" : "ORDINARY") + " histogram (upper bound of bucket: shapes): " +
                string.Join(", ", histogram.Select(kv => $"{kv.Key}: {kv.Value}")));
            foreach (string n in overNames) Say("PRIM-HULLS grid " + (extreme ? "EXTREME" : "ORDINARY") + " over the ceiling: " + n);
            Assert.True(total > 100);
        }
    }
}
