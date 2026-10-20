/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Splits a physical prim that is not convex (hollow, cut, twisted, a torus, tube, ring or hollow sphere) into convex
// cells, so it can collide as a compound of convex hulls instead of one hull that fills in its cavities.
//
// A prim is its profile (the outline of its cross-section) copied along its path (see PrimProfileRings in the mesher).
// A cell is one convex piece of the profile swept over one span of the path; its hull is the hull of that piece's
// points on the rings of the span. The profile is cut into pieces by angle around its centre: a hollow profile into
// sectors that each hold a stretch of the outer ring and of the hollow ring, a cut solid profile into wedges of at most
// half a turn, an uncut solid profile stays whole. The path is cut into spans that each turn and twist by at most
// a set angle (never less than one step of the mesher's path).
//
// The fine split is 30 degrees of profile per sector and 30 degrees of path per span. A hollow torus at that size is 144
// cells and a hollow sphere 72, so when the fine split is over MaxCells the profile is coarsened first (sectors of 45, 60,
// 90, 120, 180 degrees and more) with the finest path span that still fits, every such pair is cooked, and the one whose
// compound volume is closest to the volume of the mesher's own mesh is kept. A cell the hull builder refuses rejects its
// pair; when no pair cooks cleanly the build fails and the caller keeps today's single hull.
//
// Only the mesher's rings are used, so the cells follow what the mesher draws. The design (convex pieces of a profile
// swept along a path) is the one Halcyon's physics shape determiner chooses between by prim type; the pieces here come
// from the prim's own parameters instead of a decomposition of its triangles.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>The cells chosen for one prim and how well they fit it.</summary>
    internal sealed class PrimHullCellSet
    {
        /// <summary>False when no split could be cooked; <see cref="Failure"/> says why and the caller keeps the single hull.</summary>
        internal bool Success;

        internal string Failure = "";

        /// <summary>The points of each cell, in the prim's own frame at the size the rings were asked for. At most <see cref="PrimHullCells.MaxCells"/>.</summary>
        internal SVector3[][] Cells = Array.Empty<SVector3[]>();

        /// <summary>Convex pieces of the profile times spans of the path: <see cref="Cells"/> is their product.</summary>
        internal int Pieces, Spans;

        /// <summary>The profile sector and path span in degrees the cells were cut with.</summary>
        internal double SectorDegrees, SpanDegrees;

        /// <summary>Whether the fine split (30 / 30 degrees) was over the cell limit and the profile had to be coarsened.</summary>
        internal bool Coarsened;

        /// <summary>How many pairs of sector and span were cooked to choose this one.</summary>
        internal int PairsCooked;

        /// <summary>The volume of the cells as a compound, the volume of the mesher's mesh, and their ratio (1 is exact; hulls overshoot).</summary>
        internal double CellsVolume, MeshVolume, VolumeRatio;

        internal int CellCount => Cells.Length;
    }

    internal static class PrimHullCells
    {
        /// <summary>The most cells (hulls) one prim is split into: a constant, not a setting.</summary>
        internal const int MaxCells = 64;

        /// <summary>The fine split: degrees of profile per sector and of path per span.</summary>
        internal const double FineSectorDegrees = 30, FineSpanDegrees = 30;

        // Coarser profile sectors and path spans, in degrees, tried when the fine split is over MaxCells. A span of 1440
        // degrees is the whole path (the mesher's longest is two turns plus), a sector of 360 the whole profile.
        private static readonly double[] SectorLadder = { 30, 45, 60, 90, 120, 180, 360 };
        private static readonly double[] SpanLadder = { 15, 30, 45, 60, 90, 120, 180, 360, 720, 1440 };

        private const double AngleTolerance = 1e-3;     // radians: two angles closer than this are the same ray (0.06 degrees)
        private const double Degrees = 180.0 / Math.PI;

        /// <summary>A point on a ring of a layer: vertex A, or the fraction T of the way from vertex A to vertex B.</summary>
        private readonly struct RingPoint
        {
            internal readonly int A, B;
            internal readonly float T;
            internal RingPoint(int a, int b, float t) { A = a; B = b; T = t; }
        }

        private sealed class ProfileSplit
        {
            internal double SectorDegrees;
            internal List<RingPoint[]> Pieces = new List<RingPoint[]>();
            internal string Key = "";
        }

        private sealed class PathSplit
        {
            internal double SpanDegrees;
            internal List<(int First, int Last)> Spans = new List<(int, int)>();
            internal string Key = "";
        }

        /// <summary>One sector and span to try.</summary>
        private sealed class Candidate
        {
            internal ProfileSplit Profile;
            internal PathSplit Path;
            internal int Count => Profile.Pieces.Count * Path.Spans.Count;
        }

        // ------------------------------------------------------------------------------------------ building

        /// <summary>
        /// Splits the prim described by <paramref name="rings"/> into convex cells and cooks the candidates through
        /// <paramref name="backend"/> to choose the best fit. Nothing it cooks is kept: the caller cooks the chosen cells
        /// itself with <see cref="CookCompound"/>.
        /// </summary>
        /// <param name="rings">The mesher's rings for the prim at the size it is cooked at.</param>
        /// <param name="meshVolume">The volume of the mesher's mesh at that size (see <see cref="MeshVolume"/>).</param>
        internal static PrimHullCellSet Build(IPhysicsBackend backend, PrimProfileRings rings, double meshVolume)
        {
            var result = new PrimHullCellSet { MeshVolume = meshVolume };
            string problem = CheckRings(rings);
            if (problem != null) { result.Failure = problem; return result; }
            if (!(meshVolume > 0.0) || double.IsInfinity(meshVolume)) { result.Failure = "the mesh has no volume"; return result; }

            List<ProfileSplit> profiles = ProfileSplits(rings);
            List<PathSplit> paths = PathSplits(rings);

            // the fine split, when it fits
            Candidate fine = new Candidate { Profile = profiles[0], Path = FinestSpan(paths, FineSpanDegrees) };
            bool fits = fine.Count <= MaxCells;
            var candidates = new List<Candidate>();
            if (fits)
                candidates.Add(fine);
            candidates.AddRange(CoarserCandidates(profiles, paths, fine));

            // The fine split is used when it fits and cooks. Otherwise the coarser pairs compete on volume.
            PrimHullCellSet best = null;
            int cooked = 0;
            foreach (Candidate candidate in candidates)
            {
                PrimHullCellSet attempt = Cook(backend, rings, candidate, meshVolume);
                cooked++;
                if (attempt == null)
                    continue;
                if (fits && ReferenceEquals(candidate, fine))
                {
                    best = attempt;
                    break;
                }
                if (best == null || Better(attempt, best))
                    best = attempt;
            }

            if (best == null)
            {
                result.Failure = candidates.Count == 0
                    ? "no split of the prim is within " + MaxCells + " cells"
                    : "no split of the prim cooks into convex hulls";
                result.PairsCooked = cooked;
                return result;
            }

            best.Coarsened = !fits;
            best.PairsCooked = cooked;
            return best;
        }

        private static bool Better(PrimHullCellSet a, PrimHullCellSet b)
        {
            double ea = Math.Abs(a.VolumeRatio - 1.0), eb = Math.Abs(b.VolumeRatio - 1.0);
            if (Math.Abs(ea - eb) > 1e-9)
                return ea < eb;
            return a.CellCount < b.CellCount;
        }

        /// <summary>
        /// The pairs tried when the fine split does not fit: for each coarser profile sector (the fine sector too), the
        /// finest path span that brings the cell count within <see cref="MaxCells"/>. Pairs that cut the prim the same way
        /// as an earlier one are dropped.
        /// </summary>
        private static IEnumerable<Candidate> CoarserCandidates(List<ProfileSplit> profiles, List<PathSplit> paths, Candidate skip)
        {
            var seen = new HashSet<string>();
            if (skip != null)
                seen.Add(skip.Profile.Key + "|" + skip.Path.Key);
            foreach (ProfileSplit profile in profiles)
            {
                foreach (PathSplit path in paths)
                {
                    if (profile.Pieces.Count * path.Spans.Count > MaxCells)
                        continue;
                    if (seen.Add(profile.Key + "|" + path.Key))
                        yield return new Candidate { Profile = profile, Path = path };
                    break;      // paths run from the finest span to the coarsest: the first that fits is the finest
                }
            }
        }

        /// <summary>
        /// The cell counts of the pairs that <see cref="Build"/> would cook, without cooking anything: the fine split when
        /// it fits, else the coarser candidates. Empty when no split fits (it cannot happen: a whole-profile, whole-path
        /// cell is always one cell).
        /// </summary>
        internal static IReadOnlyList<int> CandidateCellCounts(PrimProfileRings rings)
        {
            if (CheckRings(rings) != null)
                return Array.Empty<int>();
            List<ProfileSplit> profiles = ProfileSplits(rings);
            List<PathSplit> paths = PathSplits(rings);
            Candidate fine = new Candidate { Profile = profiles[0], Path = FinestSpan(paths, FineSpanDegrees) };
            if (fine.Count <= MaxCells)
                return new[] { fine.Count };
            return CoarserCandidates(profiles, paths, fine).Select(c => c.Count).ToArray();
        }

        private static PathSplit FinestSpan(List<PathSplit> paths, double spanDegrees) =>
            paths.First(p => Math.Abs(p.SpanDegrees - spanDegrees) < 1e-9);

        private static string CheckRings(PrimProfileRings rings)
        {
            if (rings == null)
                return "no rings";
            int centre = rings.HasCentreVertex ? 1 : 0;
            if (rings.LayerCount < 2 || rings.OuterVertexCount < 2
                || rings.LayerVertexCount != centre + rings.OuterVertexCount + rings.HollowVertexCount
                || rings.ProfileAngles.Count != rings.LayerVertexCount
                || rings.Vertices.Count != rings.LayerCount * rings.LayerVertexCount)
                return "the rings are not a profile swept along a path";
            return null;
        }

        // ------------------------------------------------------------------------------------------ the profile

        /// <summary>The profile cut at each sector size of the ladder, finest first. Splits that came out the same are kept once.</summary>
        private static List<ProfileSplit> ProfileSplits(PrimProfileRings rings)
        {
            var splits = new List<ProfileSplit>();
            var keys = new HashSet<string>();
            foreach (double sector in SectorLadder)
            {
                ProfileSplit split = SplitProfile(rings, sector);
                if (keys.Add(split.Key) || splits.Count == 0)
                    splits.Add(split);
            }
            return splits;
        }

        private static ProfileSplit SplitProfile(PrimProfileRings rings, double sectorDegrees)
        {
            int centre = rings.HasCentreVertex ? 1 : 0;
            int no = rings.OuterVertexCount, nh = rings.HollowVertexCount;
            var split = new ProfileSplit { SectorDegrees = sectorDegrees };
            double sector = sectorDegrees / Degrees;

            double[] outer = new double[no];
            for (int k = 0; k < no; k++)
                outer[k] = rings.ProfileAngles[centre + k];
            double sweep = outer[no - 1];

            if (nh > 0)
            {
                // the hollow ring in angular order: stored index first + nh - 1 - j is its j-th vertex
                int first = centre + no;
                double[] hollow = new double[nh];
                for (int j = 0; j < nh; j++)
                    hollow[j] = rings.ProfileAngles[first + nh - 1 - j];

                // the angles where either ring has a vertex, in order; a piece is a run of them
                var bounds = new List<double>();
                foreach (double b in outer.Concat(hollow).OrderBy(x => x))
                    if (bounds.Count == 0 || b - bounds[bounds.Count - 1] > AngleTolerance)
                        bounds.Add(b);

                int s = 0;
                while (s < bounds.Count - 1)
                {
                    int e = s + 1;
                    while (e + 1 < bounds.Count && bounds[e + 1] - bounds[s] <= sector + AngleTolerance)
                        e++;
                    var piece = new List<RingPoint>();
                    for (int k = s; k <= e; k++)
                    {
                        piece.Add(PointAt(outer, i => centre + i, bounds[k]));
                        piece.Add(PointAt(hollow, j => first + nh - 1 - j, bounds[k]));
                    }
                    split.Pieces.Add(piece.ToArray());
                    s = e;
                }
            }
            else if (sweep >= 2.0 * Math.PI - AngleTolerance)
            {
                // a solid profile that goes all the way round is convex
                var piece = new RingPoint[rings.LayerVertexCount];
                for (int i = 0; i < piece.Length; i++)
                    piece[i] = new RingPoint(i, i, 0f);
                split.Pieces.Add(piece);
            }
            else
            {
                // a cut solid profile is a fan from the centre; wider than half a turn it is cut into fans of at most half a turn
                double limit = Math.PI + AngleTolerance;
                int s = 0;
                while (s < no - 1)
                {
                    int e = s + 1;
                    while (e + 1 < no && outer[e + 1] - outer[s] <= limit)
                        e++;
                    var piece = new List<RingPoint>();
                    if (centre == 1)
                        piece.Add(new RingPoint(0, 0, 0f));
                    for (int k = s; k <= e; k++)
                        piece.Add(new RingPoint(centre + k, centre + k, 0f));
                    split.Pieces.Add(piece.ToArray());
                    s = e;
                }
            }

            var key = new StringBuilder();
            foreach (RingPoint[] piece in split.Pieces)
            {
                foreach (RingPoint p in piece)
                    key.Append(p.A.ToString(CultureInfo.InvariantCulture)).Append(',').Append(p.B.ToString(CultureInfo.InvariantCulture))
                       .Append(',').Append(p.T.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                key.Append('/');
            }
            split.Key = key.ToString();
            return split;
        }

        /// <summary>The point of a ring at angle <paramref name="b"/>: its vertex when one is there, else a point on the edge that spans it.</summary>
        private static RingPoint PointAt(double[] angles, Func<int, int> layerIndex, double b)
        {
            int n = angles.Length;
            for (int j = 0; j < n; j++)
                if (Math.Abs(angles[j] - b) < AngleTolerance)
                {
                    int i = layerIndex(j);
                    return new RingPoint(i, i, 0f);
                }
            for (int j = 0; j < n - 1; j++)
                if (b > angles[j] && b < angles[j + 1])
                {
                    float t = (float)((b - angles[j]) / (angles[j + 1] - angles[j]));
                    return new RingPoint(layerIndex(j), layerIndex(j + 1), t);
                }
            int end = layerIndex(b <= angles[0] ? 0 : n - 1);
            return new RingPoint(end, end, 0f);
        }

        // ------------------------------------------------------------------------------------------ the path

        /// <summary>The path cut into spans at each span size of the ladder, finest first. Splits that came out the same are kept once, but the 30 degree one always.</summary>
        private static List<PathSplit> PathSplits(PrimProfileRings rings)
        {
            int intervals = rings.LayerCount - 1;
            double total = 0.0;
            for (int i = 0; i < intervals; i++)
                total += (Math.Abs(rings.LayerTurns[i + 1] - rings.LayerTurns[i]) + Math.Abs(rings.LayerTwists[i + 1] - rings.LayerTwists[i])) * Degrees;
            double perInterval = total / intervals;

            var splits = new List<PathSplit>();
            var keys = new HashSet<string>();
            foreach (double span in SpanLadder)
            {
                var split = new PathSplit { SpanDegrees = span };
                int per = perInterval < 1e-6 ? intervals : Math.Clamp((int)Math.Floor(span / perInterval + 1e-6), 1, intervals);
                int count = (intervals + per - 1) / per;
                int at = 0;
                var key = new StringBuilder();
                for (int sp = 0; sp < count; sp++)
                {
                    // spread the intervals evenly over the spans
                    int length = intervals / count + (sp < intervals % count ? 1 : 0);
                    split.Spans.Add((at, at + length));
                    key.Append(at).Append('-').Append(at + length).Append(' ');
                    at += length;
                }
                split.Key = key.ToString();
                if (keys.Add(split.Key) || Math.Abs(span - FineSpanDegrees) < 1e-9)
                    splits.Add(split);
            }
            return splits;
        }

        // ------------------------------------------------------------------------------------------ cooking

        /// <summary>The cell points of a candidate: for each piece and span, the piece's points on every layer of the span.</summary>
        private static SVector3[][] CellPoints(PrimProfileRings rings, Candidate candidate)
        {
            var cells = new List<SVector3[]>(candidate.Count);
            int size = rings.LayerVertexCount;
            foreach (RingPoint[] piece in candidate.Profile.Pieces)
                foreach ((int first, int last) in candidate.Path.Spans)
                {
                    var points = new HashSet<SVector3>();
                    var list = new List<SVector3>();
                    for (int layer = first; layer <= last; layer++)
                        foreach (RingPoint p in piece)
                        {
                            OpenMetaverse.Vector3 a = rings.Vertices[layer * size + p.A], b = rings.Vertices[layer * size + p.B];
                            var v = new SVector3(a.X + (b.X - a.X) * p.T, a.Y + (b.Y - a.Y) * p.T, a.Z + (b.Z - a.Z) * p.T);
                            if (points.Add(v))
                                list.Add(v);
                        }
                    cells.Add(list.ToArray());
                }
            return cells.ToArray();
        }

        /// <summary>Cooks one candidate's cells as a compound, measures it and throws the shapes away. Null when a cell does not cook.</summary>
        private static PrimHullCellSet Cook(IPhysicsBackend backend, PrimProfileRings rings, Candidate candidate, double meshVolume)
        {
            SVector3[][] cells = CellPoints(rings, candidate);
            ShapeId compound = CookCompound(backend, cells, out int failed);
            if (failed > 0 || !compound.IsValid)
                return null;
            double volume = backend.GetShapeVolume(compound);
            backend.ReleaseShape(compound);
            if (!(volume > 0.0) || double.IsInfinity(volume))
                return null;
            return new PrimHullCellSet
            {
                Success = true,
                Cells = cells,
                Pieces = candidate.Profile.Pieces.Count,
                Spans = candidate.Path.Spans.Count,
                SectorDegrees = candidate.Profile.SectorDegrees,
                SpanDegrees = candidate.Path.SpanDegrees,
                CellsVolume = volume,
                MeshVolume = meshVolume,
                VolumeRatio = volume / meshVolume,
            };
        }

        /// <summary>
        /// Cooks each cell as a convex hull and joins them into one compound shape (a lone cell is returned as its hull).
        /// When any cell does not cook, everything made is released and an invalid shape is returned, with
        /// <paramref name="failed"/> the number of cells that did not cook.
        /// </summary>
        internal static ShapeId CookCompound(IPhysicsBackend backend, IReadOnlyList<SVector3[]> cells, out int failed)
        {
            var hulls = new List<ShapeId>(cells.Count);
            failed = 0;
            foreach (SVector3[] points in cells)
            {
                ShapeId hull = points.Length >= 4 ? backend.CreateConvexHullShape(points) : default;
                if (hull.IsValid)
                    hulls.Add(hull);
                else
                    failed++;
            }

            if (failed > 0 || hulls.Count == 0)
            {
                foreach (ShapeId hull in hulls)
                    backend.ReleaseShape(hull);
                return default;
            }
            if (hulls.Count == 1)
                return hulls[0];

            ShapeId compound = backend.CreateHullCompoundShape(hulls.ToArray());
            foreach (ShapeId hull in hulls)
                backend.ReleaseShape(hull);         // the compound holds its own reference to each
            if (!compound.IsValid)
                failed = hulls.Count;
            return compound;
        }

        // ------------------------------------------------------------------------------------------ volume

        /// <summary>
        /// What the mesher's own mesh says about a prim: its volume and centroid, and whether that volume can be trusted as the
        /// prim's real shape volume. <see cref="Reason"/> is null when it can; otherwise one of "empty" (no volume, or the
        /// triangles wind inside out), "open" (the surface has a hole) or "self-crossing" (two triangles pass through each other,
        /// as a heavily twisted prim's surface does, so the divergence-theorem volume counts a stretch twice or cancels it).
        /// </summary>
        internal sealed class MeshAssessment
        {
            /// <summary>The volume the divergence theorem gives, positive when the triangles wind outward.</summary>
            internal double SignedVolume;
            /// <summary>The centroid of the solid the mesh encloses (meaningful when <see cref="Reason"/> is null).</summary>
            internal SVector3 Centroid;
            /// <summary>How many edges of the surface are not matched by an opposite edge (0 for a closed surface), and how far
            /// the farthest of their ends is from the best plane through the prim's centre (0 when all lie in one).</summary>
            internal int RimEdges;
            internal double RimOffPlane;
            internal string Reason;
            internal bool Trusted => Reason == null;
        }

        /// <summary>
        /// How far (in the mesh's unit-size frame) an unmatched edge may lie from the plane the others lie in. The mesher's
        /// full-circle sphere carries one flat disc through its centre that nothing closes; a divergence-theorem volume taken
        /// from the centre counts no volume for a sheet in a plane through it, so a surface open only there is as good as a closed one.
        /// </summary>
        internal const double RimPlaneTolerance = 1e-4;

        /// <summary>
        /// Reads a prim's mesh as its real shape: a closed, outward-wound, non-self-crossing triangle mesh has a volume that is
        /// the prim's. <paramref name="vertices"/> is x, y, z for each vertex.
        /// </summary>
        internal static MeshAssessment AssessMesh(float[] vertices, int[] indices)
        {
            var result = new MeshAssessment();
            double signed = 0.0, cx = 0, cy = 0, cz = 0;
            int triangles = indices.Length / 3;
            for (int t = 0; t < triangles; t++)
            {
                int ia = indices[3 * t] * 3, ib = indices[3 * t + 1] * 3, ic = indices[3 * t + 2] * 3;
                double x0 = vertices[ia], y0 = vertices[ia + 1], z0 = vertices[ia + 2];
                double x1 = vertices[ib], y1 = vertices[ib + 1], z1 = vertices[ib + 2];
                double x2 = vertices[ic], y2 = vertices[ic + 1], z2 = vertices[ic + 2];
                double v = (x0 * (y1 * z2 - z1 * y2) - y0 * (x1 * z2 - z1 * x2) + z0 * (x1 * y2 - y1 * x2)) / 6.0;
                signed += v;
                cx += v * (x0 + x1 + x2) * 0.25; cy += v * (y0 + y1 + y2) * 0.25; cz += v * (z0 + z1 + z2) * 0.25;
            }
            result.SignedVolume = signed;
            if (signed > 0)
                result.Centroid = new SVector3((float)(cx / signed), (float)(cy / signed), (float)(cz / signed));

            int[] corner = WeldCorners(vertices, indices);
            MeasureRim(vertices, indices, corner, result);
            if (!(signed > 1e-9))
                result.Reason = "empty";
            else if (result.RimOffPlane > RimPlaneTolerance)
                result.Reason = "open";
            else if (SelfCrossing(vertices, indices, corner))
                result.Reason = "self-crossing";
            return result;
        }

        // The welded vertex number of each triangle corner: corners at one position (to 1e-5) are one vertex, since the mesher
        // repeats a vertex for each face that uses it.
        private static int[] WeldCorners(float[] vertices, int[] indices)
        {
            var welded = new Dictionary<(long, long, long), int>();
            var corner = new int[indices.Length / 3 * 3];
            for (int c = 0; c < corner.Length; c++)
            {
                int vertex = indices[c] * 3;
                var key = ((long)Math.Round(vertices[vertex] * 1e5), (long)Math.Round(vertices[vertex + 1] * 1e5), (long)Math.Round(vertices[vertex + 2] * 1e5));
                if (!welded.TryGetValue(key, out int id))
                    welded[key] = id = welded.Count;
                corner[c] = id;
            }
            return corner;
        }

        // Counts the edges of the surface that no opposite edge cancels (a closed surface has none) and how far their ends are
        // from one plane through the origin: the origin the volume is taken from, so a sheet in such a plane adds no volume.
        private static void MeasureRim(float[] vertices, int[] indices, int[] corner, MeshAssessment result)
        {
            var net = new Dictionary<(int, int), int>();
            var position = new Dictionary<int, SVector3>();
            for (int t = 0; t + 2 < corner.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int u = corner[t + k], v = corner[t + (k + 1) % 3];
                    if (u == v)
                        continue;
                    net[(Math.Min(u, v), Math.Max(u, v))] = net.GetValueOrDefault((Math.Min(u, v), Math.Max(u, v))) + (u < v ? 1 : -1);
                    int a = indices[t + k] * 3, b = indices[t + (k + 1) % 3] * 3;
                    position[u] = new SVector3(vertices[a], vertices[a + 1], vertices[a + 2]);
                    position[v] = new SVector3(vertices[b], vertices[b + 1], vertices[b + 2]);
                }
            var rim = new HashSet<int>();
            foreach (var kv in net)
                if (kv.Value != 0)
                {
                    result.RimEdges++;
                    rim.Add(kv.Key.Item1);
                    rim.Add(kv.Key.Item2);
                }
            if (rim.Count == 0)
                return;
            SVector3[] points = rim.Select(i => position[i]).ToArray();
            SVector3 normal = SVector3.Zero;
            double best = 1e-9;
            for (int i = 0; i < points.Length; i++)
                for (int j = i + 1; j < points.Length; j++)
                {
                    SVector3 cross = SVector3.Cross(points[i], points[j]);
                    if (cross.Length() > best) { best = cross.Length(); normal = cross; }
                }
            if (normal == SVector3.Zero)
                return;                     // every end of every unmatched edge is on one line through the origin
            normal = SVector3.Normalize(normal);
            foreach (SVector3 point in points)
                result.RimOffPlane = Math.Max(result.RimOffPlane, Math.Abs(SVector3.Dot(normal, point)));
        }

        // Whether any two triangles of the mesh pass through each other. Triangles that share a vertex (by position: the mesher
        // repeats a corner for each face) are neighbours and are not compared. Two triangles cross when an edge of one pierces
        // the inside of the other, so every edge is tested against every triangle whose box it meets, found by sweeping the
        // triangles in order of their lowest x.
        private static bool SelfCrossing(float[] vertices, int[] indices, int[] corner)
        {
            int triangles = indices.Length / 3;
            var lo = new double[triangles * 3];
            var hi = new double[triangles * 3];
            for (int t = 0; t < triangles; t++)
                for (int axis = 0; axis < 3; axis++)
                {
                    double a = vertices[indices[3 * t] * 3 + axis], b = vertices[indices[3 * t + 1] * 3 + axis], c = vertices[indices[3 * t + 2] * 3 + axis];
                    lo[3 * t + axis] = Math.Min(a, Math.Min(b, c));
                    hi[3 * t + axis] = Math.Max(a, Math.Max(b, c));
                }
            int[] order = Enumerable.Range(0, triangles).ToArray();
            Array.Sort(order, (p, q) => lo[3 * p].CompareTo(lo[3 * q]));
            const double Gap = 1e-6;
            for (int i = 0; i < triangles; i++)
            {
                int a = order[i];
                for (int j = i + 1; j < triangles; j++)
                {
                    int b = order[j];
                    if (lo[3 * b] > hi[3 * a] + Gap)
                        break;
                    if (lo[3 * b + 1] > hi[3 * a + 1] + Gap || lo[3 * a + 1] > hi[3 * b + 1] + Gap
                        || lo[3 * b + 2] > hi[3 * a + 2] + Gap || lo[3 * a + 2] > hi[3 * b + 2] + Gap)
                        continue;
                    if (SharesCorner(corner, a, b))
                        continue;
                    if (EdgesPierce(vertices, indices, a, b) || EdgesPierce(vertices, indices, b, a))
                        return true;
                }
            }
            return false;
        }

        private static bool SharesCorner(int[] corner, int a, int b)
        {
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                    if (corner[3 * a + i] == corner[3 * b + k])
                        return true;
            return false;
        }

        // Whether an edge of triangle `edges` goes through the inside of triangle `face` (Moller-Trumbore on the segment, with a
        // margin so an edge that only meets the other's border, as at a seam, is not a crossing).
        private static bool EdgesPierce(float[] v, int[] idx, int edges, int face)
        {
            const double Margin = 1e-5;
            int f0 = idx[3 * face] * 3, f1 = idx[3 * face + 1] * 3, f2 = idx[3 * face + 2] * 3;
            double ox = v[f0], oy = v[f0 + 1], oz = v[f0 + 2];
            double e1x = v[f1] - ox, e1y = v[f1 + 1] - oy, e1z = v[f1 + 2] - oz;
            double e2x = v[f2] - ox, e2y = v[f2 + 1] - oy, e2z = v[f2 + 2] - oz;
            for (int k = 0; k < 3; k++)
            {
                int p = idx[3 * edges + k] * 3, q = idx[3 * edges + (k + 1) % 3] * 3;
                double dx = v[q] - v[p], dy = v[q + 1] - v[p + 1], dz = v[q + 2] - v[p + 2];
                double hx = dy * e2z - dz * e2y, hy = dz * e2x - dx * e2z, hz = dx * e2y - dy * e2x;
                double det = e1x * hx + e1y * hy + e1z * hz;
                if (Math.Abs(det) < 1e-14)
                    continue;                   // parallel to the face: no crossing through its inside
                double inv = 1.0 / det;
                double sx = v[p] - ox, sy = v[p + 1] - oy, sz = v[p + 2] - oz;
                double u = inv * (sx * hx + sy * hy + sz * hz);
                if (u < Margin || u > 1 - Margin)
                    continue;
                double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                double w = inv * (dx * qx + dy * qy + dz * qz);
                if (w < Margin || u + w > 1 - Margin)
                    continue;
                double t = inv * (e2x * qx + e2y * qy + e2z * qz);
                if (t > Margin && t < 1 - Margin)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The volume of a closed triangle mesh (the divergence theorem: the sum of the signed volumes of the tetrahedra
        /// from the origin to each triangle). <paramref name="vertices"/> is x, y, z for each vertex.
        /// </summary>
        internal static double MeshVolume(float[] vertices, int[] indices)
        {
            double volume = 0.0;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t] * 3, b = indices[t + 1] * 3, c = indices[t + 2] * 3;
                double ax = vertices[a], ay = vertices[a + 1], az = vertices[a + 2];
                double bx = vertices[b], by = vertices[b + 1], bz = vertices[b + 2];
                double cx = vertices[c], cy = vertices[c + 1], cz = vertices[c + 2];
                volume += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
            }
            return Math.Abs(volume) / 6.0;
        }
    }
}
